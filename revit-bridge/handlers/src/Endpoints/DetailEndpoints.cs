using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcpBridge.Endpoints
{
    /// <summary>
    /// Detail linework and annotation: what a detail sheet is actually made of. Nothing here
    /// touches the model - a DetailCurve and a TextNote are view-specific elements that live in
    /// exactly one view, which is the whole point of a drafting view.
    ///
    /// Same rules as every other write endpoint: one request is one TransactionGroup and therefore
    /// one Ctrl+Z, with one inner transaction per entry so a line Revit refuses lands in "failed"
    /// while the rest are still drawn. All coordinates are Revit internal units (decimal feet).
    /// </summary>
    internal static class DetailEndpoints
    {
        /// <summary>How close two text sizes count as the same, in feet. About a hundredth of a mm.</summary>
        private const double TextSizeTolerance = 1e-5;

        /// <summary>
        /// Body: {viewId, lines: [{start: {x, y}, end: {x, y}}], lineStyle?}.
        ///
        /// x / y are the view's own plan coordinates in feet; z is supplied by the bridge, because
        /// NewDetailCurve refuses a curve that is not in the plane of the view. For a drafting view
        /// that plane is 0, for a plan it is the level the plan is cut on, and the response says
        /// which elevation was used in "elevation".
        ///
        /// "lineStyle" is the name of a subcategory of OST_Lines - "Thin Lines", "Medium Lines",
        /// or anything the template loaded. An unknown one is not a failure: the lines are drawn in
        /// the view's default style and the response carries "requestedLineStyle" plus
        /// "availableLineStyles", the exact names this document does offer.
        /// </summary>
        internal static object CreateDetailLines(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            long viewId = JsonBody.AsLong(RequireValue(body, "viewId"), "viewId");
            JsonElement lines = JsonBody.RequireArray(body, "lines");
            string lineStyle = JsonBody.OptionalString(body, "lineStyle");

            View view = RequireDetailView(document, viewId);
            double elevation = ViewPlaneElevation(view);

            List<JsonElement> requests = new List<JsonElement>();
            foreach (JsonElement line in lines.EnumerateArray())
            {
                requests.Add(line);
            }

            if (requests.Count == 0)
            {
                throw BridgeException.BadRequest("\"lines\" must contain at least one line.");
            }

            List<string> available = null;
            GraphicsStyle style = null;

            if (lineStyle != null)
            {
                style = ResolveLineStyle(document, lineStyle, out available);
            }

            return RevitWrite.InGroup(document, "MCP: create detail lines", delegate
            {
                List<object> created = new List<object>();
                List<object> failed = new List<object>();

                for (int index = 0; index < requests.Count; index++)
                {
                    JsonElement request = requests[index];

                    XYZ start;
                    XYZ end;

                    try
                    {
                        start = PlanePoint(request, "start", elevation);
                        end = PlanePoint(request, "end", elevation);

                        if (start.DistanceTo(end) < document.Application.ShortCurveTolerance)
                        {
                            throw BridgeException.BadRequest(
                                "\"start\" and \"end\" are closer together than Revit's short-curve "
                                    + "tolerance, so there is no line to draw.");
                        }
                    }
                    catch (BridgeException ex)
                    {
                        // A malformed line is this entry's problem, not the batch's.
                        failed.Add(new Dictionary<string, object>
                        {
                            { "index", index },
                            { "reason", ex.Message },
                        });

                        continue;
                    }

                    DetailCurve curve = null;

                    try
                    {
                        RevitWrite.InTransaction(document, "Create detail line", delegate
                        {
                            curve = document.Create.NewDetailCurve(view, Line.CreateBound(start, end));

                            if (curve != null && style != null)
                            {
                                curve.LineStyle = style;
                            }
                        });
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                    {
                        failed.Add(new Dictionary<string, object>
                        {
                            { "index", index },
                            { "reason", ex.Message },
                        });

                        continue;
                    }

                    if (curve == null)
                    {
                        // NewDetailCurve is documented to answer null rather than throw.
                        failed.Add(new Dictionary<string, object>
                        {
                            { "index", index },
                            { "reason", "Revit returned no detail curve for this line." },
                        });

                        continue;
                    }

                    created.Add(new Dictionary<string, object>
                    {
                        { "id", curve.Id.Value },
                        { "start", Point(start) },
                        { "end", Point(end) },
                    });
                }

                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    { "viewId", viewId },
                    { "elevation", elevation },
                    { "lineStyle", style == null ? null : RevitFacts.SafeName(style) },
                    { "created", created },
                    { "failed", failed },
                };

                if (available != null)
                {
                    result["requestedLineStyle"] = lineStyle;
                    result["availableLineStyles"] = available;
                }

                return result;
            });
        }

        /// <summary>
        /// Body: {viewId, notes: [{x, y, text, size?}]}.
        ///
        /// x / y are the view's own plan coordinates in feet and place the note's top-left corner;
        /// the elevation is supplied by the bridge exactly as it is for detail lines.
        ///
        /// "size" is the text height in feet on the paper. Revit keeps text size on the type, not
        /// on the note, so a size no loaded type carries means duplicating the default type - once
        /// per distinct size in the batch, in its own transaction, and reusing any existing type
        /// whose size already matches so repeated calls do not litter the document with types.
        /// </summary>
        internal static object CreateTextNotes(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            long viewId = JsonBody.AsLong(RequireValue(body, "viewId"), "viewId");
            JsonElement notes = JsonBody.RequireArray(body, "notes");

            View view = RequireAnnotationView(document, viewId);
            double elevation = ViewPlaneElevation(view);

            ElementId defaultTypeId = document.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            if (defaultTypeId == null || defaultTypeId == ElementId.InvalidElementId)
            {
                throw new BridgeException(
                    409,
                    "NO_TEXT_NOTE_TYPE",
                    "This document has no default text type, so no text note can be created. That "
                        + "normally means the project was made from a template without one.");
            }

            List<JsonElement> requests = new List<JsonElement>();
            foreach (JsonElement note in notes.EnumerateArray())
            {
                requests.Add(note);
            }

            if (requests.Count == 0)
            {
                throw BridgeException.BadRequest("\"notes\" must contain at least one note.");
            }

            return RevitWrite.InGroup(document, "MCP: create text notes", delegate
            {
                List<object> created = new List<object>();
                List<object> failed = new List<object>();

                Dictionary<double, ElementId> bySize = new Dictionary<double, ElementId>();

                for (int index = 0; index < requests.Count; index++)
                {
                    JsonElement request = requests[index];

                    XYZ position;
                    string text;
                    ElementId typeId;

                    try
                    {
                        position = new XYZ(
                            JsonBody.RequireDouble(request, "x"),
                            JsonBody.RequireDouble(request, "y"),
                            elevation);

                        text = JsonBody.RequireString(request, "text");

                        double size = JsonBody.OptionalDouble(request, "size", 0.0);
                        if (size < 0)
                        {
                            throw BridgeException.BadRequest(
                                "\"size\" must be greater than zero (feet of text height on the paper).");
                        }

                        typeId = size > 0
                            ? TextTypeId(document, defaultTypeId, size, bySize)
                            : defaultTypeId;
                    }
                    catch (BridgeException ex)
                    {
                        failed.Add(new Dictionary<string, object>
                        {
                            { "index", index },
                            { "reason", ex.Message },
                        });

                        continue;
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                    {
                        failed.Add(new Dictionary<string, object>
                        {
                            { "index", index },
                            { "reason", ex.Message },
                        });

                        continue;
                    }

                    TextNote note = null;

                    try
                    {
                        RevitWrite.InTransaction(document, "Create text note", delegate
                        {
                            // The unwrapped overload: a note this bridge places is a label, and a
                            // width it was never given is not a width to invent.
                            note = TextNote.Create(document, view.Id, position, text, typeId);
                        });
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                    {
                        failed.Add(new Dictionary<string, object>
                        {
                            { "index", index },
                            { "reason", ex.Message },
                        });

                        continue;
                    }

                    created.Add(new Dictionary<string, object>
                    {
                        { "id", note.Id.Value },
                        { "x", position.X },
                        { "y", position.Y },
                        { "typeName", RevitFacts.TypeName(document, note) },
                    });
                }

                return new Dictionary<string, object>
                {
                    { "viewId", viewId },
                    { "elevation", elevation },
                    { "created", created },
                    { "failed", failed },
                };
            });
        }

        /// <summary>
        /// The view a detail line may go in: a drafting view, a plan, or a legend. Anything else is
        /// refused with a code, rather than left to surface as an unreadable Revit exception per
        /// line.
        ///
        /// Legends are in that list because detail lines are one of the two things the API can put
        /// in one. NewDetailCurve documents exactly one failure - "Thrown when curve is not in
        /// plane of the view" - and says nothing about view kinds, and a legend's plane is 0 like a
        /// drafting view's. The LegendComponent that would show a family type at scale has no
        /// creation API, so linework and text are the whole of what views/create-legend can then be
        /// filled with.
        /// </summary>
        private static View RequireDetailView(Document document, long viewId)
        {
            View view = RequireView(document, viewId);

            if (view.IsTemplate || !(view is ViewDrafting || view is ViewPlan || view.ViewType == ViewType.Legend))
            {
                throw new BridgeException(
                    409,
                    "VIEW_CANNOT_HOST_DETAIL",
                    "View \"" + RevitFacts.SafeName(view) + "\" (" + viewId + ", " + view.ViewType
                        + ") is not a drafting view, a plan or a legend, so detail lines cannot be "
                        + "drawn in it. Make one with /revit-mcp/views/create-drafting.");
            }

            return view;
        }

        /// <summary>
        /// The view a text note may go in: any graphical view. Schedules, sheets and templates are
        /// refused with a code; a section or an elevation is not, because annotating one is a
        /// perfectly ordinary thing to want. A legend passes this guard and always has - it is a
        /// graphic view, which is all TextNote.Create asks for ("the viewId does not represent a
        /// valid graphic view element" is its only view-related failure).
        /// </summary>
        private static View RequireAnnotationView(Document document, long viewId)
        {
            View view = RequireView(document, viewId);

            if (view.IsTemplate || view is ViewSchedule || view is ViewSheet)
            {
                throw new BridgeException(
                    409,
                    "VIEW_CANNOT_HOST_TEXT",
                    "View \"" + RevitFacts.SafeName(view) + "\" (" + viewId + ", " + view.ViewType
                        + ") cannot carry text notes. Schedules, sheets and view templates never "
                        + "can.");
            }

            return view;
        }

        private static View RequireView(Document document, long viewId)
        {
            View view = document.GetElement(new ElementId(viewId)) as View;
            if (view == null)
            {
                throw BridgeException.BadRequest(
                    "Element " + viewId + " is not a view in this document. Call /revit-mcp/views "
                        + "for the ones that are.");
            }

            return view;
        }

        /// <summary>
        /// The elevation the view's linework has to sit at. NewDetailCurve refuses a curve that is
        /// not in the plane of the view, and View.Origin is documented as "not meaningful" for a
        /// plan - so a plan's plane comes from the level it is cut on, and a drafting view or a
        /// legend, neither of which is a view of the model at all, is simply 0.
        /// </summary>
        private static double ViewPlaneElevation(View view)
        {
            ViewPlan plan = view as ViewPlan;

            if (plan != null && plan.GenLevel != null)
            {
                return plan.GenLevel.Elevation;
            }

            return 0.0;
        }

        /// <summary>Reads {x, y} and puts it on the view's plane, whatever z the caller sent.</summary>
        private static XYZ PlanePoint(JsonElement request, string name, double elevation)
        {
            XYZ point = RevitFacts.RequirePoint(request, name);

            return new XYZ(point.X, point.Y, elevation);
        }

        private static Dictionary<string, object> Point(XYZ value)
        {
            return new Dictionary<string, object>
            {
                { "x", value.X },
                { "y", value.Y },
            };
        }

        /// <summary>
        /// The GraphicsStyle for a named line style, or null when this document has no such
        /// subcategory of OST_Lines - in which case <paramref name="available"/> comes back with
        /// the names it does have, and the caller is told rather than failed.
        /// </summary>
        private static GraphicsStyle ResolveLineStyle(Document document, string name, out List<string> available)
        {
            List<string> names = new List<string>();

            Category lines = Category.GetCategory(document, BuiltInCategory.OST_Lines);

            if (lines != null)
            {
                foreach (Category subCategory in lines.SubCategories)
                {
                    names.Add(subCategory.Name);

                    if (!string.Equals(subCategory.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    GraphicsStyle style = subCategory.GetGraphicsStyle(GraphicsStyleType.Projection);
                    if (style != null)
                    {
                        available = null;
                        return style;
                    }
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            available = names;
            return null;
        }

        /// <summary>
        /// The text type carrying <paramref name="size"/>, creating it if no loaded type does. The
        /// duplicate happens in its own transaction: a text type is a document-wide thing and must
        /// not be rolled back by one note Revit later refused.
        /// </summary>
        private static ElementId TextTypeId(
            Document document,
            ElementId defaultTypeId,
            double size,
            Dictionary<double, ElementId> bySize)
        {
            ElementId cached;
            if (bySize.TryGetValue(size, out cached))
            {
                return cached;
            }

            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(TextNoteType)))
            {
                Parameter textSize = element.get_Parameter(BuiltInParameter.TEXT_SIZE);

                if (textSize != null && Math.Abs(textSize.AsDouble() - size) < TextSizeTolerance)
                {
                    bySize[size] = element.Id;
                    return element.Id;
                }
            }

            ElementId created = null;

            RevitWrite.InTransaction(document, "Create text type", delegate
            {
                TextNoteType source = (TextNoteType)document.GetElement(defaultTypeId);
                TextNoteType copy = (TextNoteType)source.Duplicate(TextTypeName(document, size));

                Parameter textSize = copy.get_Parameter(BuiltInParameter.TEXT_SIZE);
                if (textSize == null || textSize.IsReadOnly)
                {
                    throw BridgeException.BadRequest(
                        "The default text type has no writable text size, so \"size\" cannot be "
                            + "applied. Omit it to use the type as loaded.");
                }

                textSize.Set(size);
                created = copy.Id;
            });

            bySize[size] = created;
            return created;
        }

        /// <summary>A free element-type name for a text type of that size.</summary>
        private static string TextTypeName(Document document, double size)
        {
            HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(TextNoteType)))
            {
                string existing = RevitFacts.SafeName(element);
                if (existing != null)
                {
                    taken.Add(existing);
                }
            }

            string name = "MCP Text " + size.ToString("0.####", CultureInfo.InvariantCulture) + " ft";

            string candidate = name;
            int suffix = 2;

            while (taken.Contains(candidate))
            {
                candidate = name + " " + suffix;
                suffix++;
            }

            return candidate;
        }

        private static JsonElement RequireValue(JsonElement root, string name)
        {
            JsonElement value;
            if (!JsonBody.TryGet(root, name, out value))
            {
                throw BridgeException.BadRequest("\"" + name + "\" is required.");
            }

            return value;
        }
    }
}
