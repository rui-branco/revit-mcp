using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcpBridge.Endpoints
{
    /// <summary>
    /// Category graphic overrides in one view - the Visibility/Graphics dialog, per category.
    ///
    /// This is what makes a set of drawings read as drawings rather than as one CAD export
    /// repeated: paving drawn light so it sits behind the planting, planting in green, context
    /// halftoned, the thing the drawing is about drawn heavy. None of it changes the model; all of
    /// it is stored on the view.
    ///
    /// Two things separate this from calling SetCategoryOverrides directly, and both are the
    /// difference between a drawing that changed and one that only reports it did:
    ///
    /// 1. **It merges.** View.GetCategoryOverrides hands back everything already overridden on that
    ///    category - fill patterns, line patterns, detail level - and the request only names a few
    ///    fields. The existing settings are COPIED and the named fields set on the copy, so a call
    ///    that asks for a colour cannot silently wipe a solid fill somebody set in the dialog.
    /// 2. **It refuses a view whose template owns the overrides.** Revit accepts SetCategoryOverrides
    ///    on such a view and then draws it the template's way regardless. That is a success message
    ///    for a change nobody can see, so the template is named instead - the same treatment
    ///    views/set-crop gives a template-controlled crop.
    ///
    /// Everything is validated before the transaction opens: the view, every category name, every
    /// category's View.IsCategoryOverridable, and every value. One transaction for the batch, so one
    /// request is one Ctrl+Z, and every row is read back off the view afterwards rather than echoed.
    /// </summary>
    internal static class ViewOverrideEndpoints
    {
        /// <summary>
        /// Body: {viewId, overrides: [{category, projectionColor?: {r, g, b}, cutColor?: {r, g, b},
        /// projectionLineWeight?, cutLineWeight?, halftone?, surfaceTransparency?}], dryRun?}.
        ///
        /// "category" is a display name ("Planting", "Site") or a literal OST_* BuiltInCategory
        /// name, matched the same way everywhere else in the bridge.
        ///
        /// The colours are LINE colours - Projection/Surface > Lines and Cut > Lines in the dialog.
        /// Surface and cut PATTERNS are not touched by this endpoint, and that is deliberate: they
        /// are reported before and after precisely so it is visible they survived the call.
        ///
        /// Line weights are 1-16, or -1 to clear the override and go back to the category's own
        /// weight (Revit's InvalidPenNumber). "surfaceTransparency" is 0 (opaque) to 100.
        ///
        /// "dryRun" DEFAULTS TO TRUE: the default call resolves everything, reports the current
        /// override of each category and the exact merged override it would write, and changes
        /// nothing. Pass dryRun false to apply it.
        ///
        /// Hidden categories stay hidden: SetCategoryOverrides does not touch visibility, and the
        /// response carries "hidden" from before the write and after it so that is checked rather
        /// than asserted.
        /// </summary>
        internal static object OverrideCategories(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            long viewId = JsonBody.AsLong(RequireValue(body, "viewId"), "viewId");
            bool dryRun = JsonBody.OptionalBool(body, "dryRun", true);

            View view = document.GetElement(new ElementId(viewId)) as View;
            if (view == null)
            {
                throw BridgeException.BadRequest(
                    "Element " + viewId + " is not a view in this document. Call /revit-mcp/views "
                        + "for the ones that are.");
            }

            if (!view.AreGraphicsOverridesAllowed())
            {
                throw new BridgeException(
                    409,
                    "OVERRIDES_NOT_SUPPORTED",
                    "View \"" + RevitFacts.SafeName(view) + "\" (" + view.ViewType + ") does not "
                        + "support Visibility/Graphics overrides - a sheet, a schedule and a legend "
                        + "are the usual cases. Nothing was changed.");
            }

            List<Request> requests = Parse(document, view, body);

            // Named before anything is written: Revit would take these writes and then draw the
            // view the template's way, which is a success for a change nobody can see.
            RefuseTemplateControlled(document, view, requests);

            List<object> rows = new List<object>();

            Dictionary<string, object> report = new Dictionary<string, object>
            {
                { "dryRun", dryRun },
                { "viewId", viewId },
                { "name", RevitFacts.SafeName(view) },
                { "viewType", view.ViewType.ToString() },
                { "template", Template(document, view) },
                { "categories", rows },
            };

            if (dryRun)
            {
                foreach (Request request in requests)
                {
                    Dictionary<string, object> row = Row(view, request);

                    // The same merge the write would do, on a copy of the settings object. No
                    // transaction, nothing touched: this is the override Revit would end up with.
                    row["would"] = ReadSettings(MergeChecked(view, request));

                    rows.Add(row);
                }

                report["applied"] = false;
                report["note"] = "Nothing was changed. Every category and value was validated and "
                    + "\"would\" is the merged override this call would write - the current settings "
                    + "with only the named fields replaced. Send dryRun false to apply it.";

                return report;
            }

            return RevitWrite.InGroup(document, "MCP: override view categories", delegate
            {
                foreach (Request request in requests)
                {
                    request.Row = Row(view, request);
                    rows.Add(request.Row);

                    // Built - and therefore value-checked by Revit itself - before the transaction
                    // opens, so a value Revit will not take is a refusal rather than a rollback.
                    request.Merged = MergeChecked(view, request);
                }

                // One transaction for the batch: a drawing's graphics are one decision and one
                // undo step, not one per category.
                RevitWrite.InTransaction(document, "Override view categories", delegate
                {
                    foreach (Request request in requests)
                    {
                        view.SetCategoryOverrides(request.CategoryId, request.Merged);
                    }
                });

                foreach (Request request in requests)
                {
                    // Read back off the view, never echoed: this is the only honest answer to "did
                    // the drawing change".
                    request.Row["after"] = ReadSettings(view.GetCategoryOverrides(request.CategoryId));

                    bool hidden = view.GetCategoryHidden(request.CategoryId);
                    request.Row["hiddenAfter"] = hidden;
                    request.Row["hiddenPreserved"] = hidden == request.Hidden;
                }

                report["applied"] = true;
                report["note"] = "Applied in one transaction, so this is one undo step. \"after\" is "
                    + "read back off the view. The pattern fields are reported before and after "
                    + "because this endpoint never writes them: a call that sets a colour leaves a "
                    + "fill pattern exactly as it found it.";

                return report;
            });
        }

        // --- the request --------------------------------------------------------------------------

        private sealed class Request
        {
            internal string Name;
            internal Category Category;
            internal ElementId CategoryId;
            internal bool Hidden;

            internal Color ProjectionColor;
            internal Color CutColor;
            internal int? ProjectionLineWeight;
            internal int? CutLineWeight;
            internal bool? Halftone;
            internal int? SurfaceTransparency;

            internal Dictionary<string, object> Requested;
            internal Dictionary<string, object> Row;
            internal OverrideGraphicSettings Merged;
        }

        /// <summary>
        /// Every row resolved and checked against this view before a transaction is opened: an
        /// unknown category name, a category this view will not override, or a value outside
        /// Revit's range fails the whole call rather than leaving half a request on the drawing.
        /// </summary>
        private static List<Request> Parse(Document document, View view, JsonElement body)
        {
            List<Request> requests = new List<Request>();
            HashSet<long> seen = new HashSet<long>();

            JsonElement array = JsonBody.RequireArray(body, "overrides");

            foreach (JsonElement row in array.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                {
                    throw BridgeException.BadRequest(
                        "\"overrides\" must be an array of objects, each with a \"category\" and at "
                            + "least one setting.");
                }

                string name = JsonBody.RequireString(row, "category");

                Category category = RevitFacts.ResolveCategory(document, name);
                if (category == null)
                {
                    throw BridgeException.BadRequest(
                        "\"" + name + "\" is not a category in this document. Call "
                            + "/revit-mcp/categories for the names it has; a literal OST_* "
                            + "BuiltInCategory name is accepted too. Nothing was changed.");
                }

                if (!view.IsCategoryOverridable(category.Id))
                {
                    throw new BridgeException(
                        409,
                        "CATEGORY_NOT_OVERRIDABLE",
                        "View \"" + RevitFacts.SafeName(view) + "\" (" + view.ViewType + ") reports "
                            + "IsCategoryOverridable false for \"" + category.Name + "\": Revit does "
                            + "not allow that category to be overridden in this kind of view. "
                            + "Nothing was changed.");
                }

                if (!seen.Add(category.Id.Value))
                {
                    throw BridgeException.BadRequest(
                        "\"" + category.Name + "\" appears twice in \"overrides\". Merge the two "
                            + "rows: the second would decide, which is not what a request that "
                            + "names both means. Nothing was changed.");
                }

                Request request = new Request
                {
                    Name = category.Name,
                    Category = category,
                    CategoryId = category.Id,
                    Hidden = view.GetCategoryHidden(category.Id),
                    Requested = new Dictionary<string, object>(),
                };

                request.ProjectionColor = OptionalColor(row, "projectionColor", request.Requested);
                request.CutColor = OptionalColor(row, "cutColor", request.Requested);
                request.ProjectionLineWeight = OptionalLineWeight(row, "projectionLineWeight", request.Requested);
                request.CutLineWeight = OptionalLineWeight(row, "cutLineWeight", request.Requested);
                request.Halftone = OptionalFlag(row, "halftone", request.Requested);
                request.SurfaceTransparency = OptionalTransparency(row, request.Requested);

                if (request.Requested.Count == 0)
                {
                    throw BridgeException.BadRequest(
                        "The \"overrides\" row for \"" + category.Name + "\" names no setting. Pass "
                            + "at least one of projectionColor, cutColor, projectionLineWeight, "
                            + "cutLineWeight, halftone or surfaceTransparency. Nothing was changed.");
                }

                requests.Add(request);
            }

            if (requests.Count == 0)
            {
                throw BridgeException.BadRequest(
                    "\"overrides\" is required and must be a non-empty array of "
                        + "{category, ...settings} objects.");
            }

            return requests;
        }

        private static Color OptionalColor(JsonElement row, string name, Dictionary<string, object> requested)
        {
            JsonElement value;
            if (!JsonBody.TryGet(row, name, out value))
            {
                return null;
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
                throw BridgeException.BadRequest(
                    "\"" + name + "\" must be an object {r, g, b} with each channel 0-255.");
            }

            int red = Channel(value, name, "r");
            int green = Channel(value, name, "g");
            int blue = Channel(value, name, "b");

            requested[name] = new Dictionary<string, object>
            {
                { "r", red },
                { "g", green },
                { "b", blue },
            };

            return new Color((byte)red, (byte)green, (byte)blue);
        }

        private static int Channel(JsonElement color, string name, string channel)
        {
            double raw = JsonBody.AsDouble(RequireValue(color, channel), name + "." + channel);
            int value = (int)Math.Round(raw);

            if (value < 0 || value > 255 || Math.Abs(raw - value) > 1e-9)
            {
                throw BridgeException.BadRequest(
                    "\"" + name + "." + channel + "\" is " + raw + "; a colour channel is a whole "
                        + "number from 0 to 255. Nothing was changed.");
            }

            return value;
        }

        /// <summary>
        /// 1-16, Revit's own range, or -1 to clear the override and let the category's weight back
        /// through. Anything else is refused here rather than at commit time.
        /// </summary>
        private static int? OptionalLineWeight(JsonElement row, string name, Dictionary<string, object> requested)
        {
            JsonElement value;
            if (!JsonBody.TryGet(row, name, out value))
            {
                return null;
            }

            double raw = JsonBody.AsDouble(value, name);
            int weight = (int)Math.Round(raw);

            if (Math.Abs(raw - weight) > 1e-9 || weight == 0 || weight < -1 || weight > 16)
            {
                throw BridgeException.BadRequest(
                    "\"" + name + "\" is " + raw + "; a line weight override is a whole number from "
                        + "1 to 16, or -1 to clear the override. Nothing was changed.");
            }

            requested[name] = weight;

            return weight == -1 ? OverrideGraphicSettings.InvalidPenNumber : weight;
        }

        private static bool? OptionalFlag(JsonElement row, string name, Dictionary<string, object> requested)
        {
            JsonElement value;
            if (!JsonBody.TryGet(row, name, out value))
            {
                return null;
            }

            bool flag = JsonBody.AsBool(value, name);
            requested[name] = flag;
            return flag;
        }

        private static int? OptionalTransparency(JsonElement row, Dictionary<string, object> requested)
        {
            JsonElement value;
            if (!JsonBody.TryGet(row, "surfaceTransparency", out value))
            {
                return null;
            }

            double raw = JsonBody.AsDouble(value, "surfaceTransparency");
            int transparency = (int)Math.Round(raw);

            if (Math.Abs(raw - transparency) > 1e-9 || transparency < 0 || transparency > 100)
            {
                throw BridgeException.BadRequest(
                    "\"surfaceTransparency\" is " + raw + "; it is a whole percentage from 0 "
                        + "(opaque) to 100 (fully transparent). Nothing was changed.");
            }

            requested["surfaceTransparency"] = transparency;
            return transparency;
        }

        // --- the merge ----------------------------------------------------------------------------

        /// <summary>
        /// The view's current override for that category with ONLY the named fields replaced.
        /// Everything else - both fill patterns, both line patterns, the detail level - is carried
        /// over by the copy constructor, which is what stops a request for a colour from clearing a
        /// pattern that is not in it.
        /// </summary>
        /// <summary>
        /// The merge, with Revit's own value checking turned into a refusal. The setters validate
        /// as they are called - on the settings object, with no transaction open - so a value Revit
        /// will not take is caught here rather than rolling a commit back. Transparency is the one
        /// that matters: the API documents the accepted range as GREATER than 0 and LESS than 100,
        /// while the dialog offers 0, so the bridge passes 0-100 through and lets Revit answer.
        /// </summary>
        private static OverrideGraphicSettings MergeChecked(View view, Request request)
        {
            try
            {
                return Merge(view.GetCategoryOverrides(request.CategoryId), request);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                throw BridgeException.BadRequest(
                    "Revit refused one of the values for \"" + request.Name + "\": " + ex.Message
                        + " Line weights are 1-16 or -1 to clear; surfaceTransparency is 0-100. "
                        + "Nothing was changed.");
            }
        }

        private static OverrideGraphicSettings Merge(OverrideGraphicSettings current, Request request)
        {
            OverrideGraphicSettings merged = current == null
                ? new OverrideGraphicSettings()
                : new OverrideGraphicSettings(current);

            if (request.ProjectionColor != null)
            {
                merged.SetProjectionLineColor(request.ProjectionColor);
            }

            if (request.CutColor != null)
            {
                merged.SetCutLineColor(request.CutColor);
            }

            if (request.ProjectionLineWeight.HasValue)
            {
                merged.SetProjectionLineWeight(request.ProjectionLineWeight.Value);
            }

            if (request.CutLineWeight.HasValue)
            {
                merged.SetCutLineWeight(request.CutLineWeight.Value);
            }

            if (request.Halftone.HasValue)
            {
                merged.SetHalftone(request.Halftone.Value);
            }

            if (request.SurfaceTransparency.HasValue)
            {
                merged.SetSurfaceTransparency(request.SurfaceTransparency.Value);
            }

            return merged;
        }

        // --- the template -------------------------------------------------------------------------

        /// <summary>
        /// A view template owning the V/G overrides is the case where SetCategoryOverrides lands,
        /// commits, and the view is still drawn the template's way. The categories in the request
        /// are grouped by which template parameter owns them - model, annotation, analytical - and
        /// a controlled one fails the call by name.
        ///
        /// A template controls a parameter when it is in GetTemplateParameterIds and NOT in
        /// GetNonControlledTemplateParameterIds: the second list is the exceptions, which is the
        /// opposite way round from how the dialog reads.
        /// </summary>
        private static void RefuseTemplateControlled(Document document, View view, List<Request> requests)
        {
            View template = TemplateOf(document, view);
            if (template == null)
            {
                return;
            }

            HashSet<long> controlled = ControlledParameters(template);
            if (controlled == null)
            {
                // Revit would not answer. Reported on the response as an unknown rather than
                // claimed either way; the read-back after the write is what settles it.
                return;
            }

            List<string> blocked = new List<string>();

            foreach (Request request in requests)
            {
                BuiltInParameter? parameter = VisibilityParameter(request.Category);
                if (!parameter.HasValue)
                {
                    continue;
                }

                if (controlled.Contains(new ElementId(parameter.Value).Value))
                {
                    blocked.Add(request.Name + " (" + parameter.Value + ")");
                }
            }

            if (blocked.Count == 0)
            {
                return;
            }

            throw new BridgeException(
                409,
                "OVERRIDES_CONTROLLED_BY_TEMPLATE",
                "View template \"" + RevitFacts.SafeName(template) + "\" (" + template.Id.Value
                    + ") owns the Visibility/Graphics overrides for " + string.Join(", ", blocked)
                    + " in view \"" + RevitFacts.SafeName(view) + "\". Revit would accept this write "
                    + "and keep drawing the view the template's way, so nothing was changed. Set the "
                    + "overrides on the template, or take that parameter out of its control.");
        }

        private static Dictionary<string, object> Template(Document document, View view)
        {
            View template = TemplateOf(document, view);
            if (template == null)
            {
                return null;
            }

            HashSet<long> controlled = ControlledParameters(template);

            List<object> owns = new List<object>();

            if (controlled != null)
            {
                foreach (BuiltInParameter parameter in VisibilityParameters)
                {
                    if (controlled.Contains(new ElementId(parameter).Value))
                    {
                        owns.Add(parameter.ToString());
                    }
                }
            }

            return new Dictionary<string, object>
            {
                { "id", template.Id.Value },
                { "name", RevitFacts.SafeName(template) },

                // null, not an empty list: "Revit would not say" is not "it owns nothing".
                { "controls", controlled == null ? null : owns },
            };
        }

        private static View TemplateOf(Document document, View view)
        {
            ElementId templateId;

            try
            {
                templateId = view.ViewTemplateId;
            }
            catch (Exception)
            {
                return null;
            }

            if (templateId == null || templateId == ElementId.InvalidElementId)
            {
                return null;
            }

            return document.GetElement(templateId) as View;
        }

        private static HashSet<long> ControlledParameters(View template)
        {
            HashSet<long> controlled = new HashSet<long>();
            HashSet<long> excluded = new HashSet<long>();

            try
            {
                foreach (ElementId id in template.GetTemplateParameterIds())
                {
                    controlled.Add(id.Value);
                }

                foreach (ElementId id in template.GetNonControlledTemplateParameterIds())
                {
                    excluded.Add(id.Value);
                }
            }
            catch (Exception)
            {
                return null;
            }

            controlled.ExceptWith(excluded);
            return controlled;
        }

        /// <summary>The V/G parameters a template can own, one per kind of category.</summary>
        private static readonly BuiltInParameter[] VisibilityParameters =
        {
            BuiltInParameter.VIS_GRAPHICS_MODEL,
            BuiltInParameter.VIS_GRAPHICS_ANNOTATION,
            BuiltInParameter.VIS_GRAPHICS_ANALYTICAL_MODEL,
            BuiltInParameter.VIS_GRAPHICS_IMPORT,
        };

        /// <summary>
        /// Which template parameter owns this category's overrides. Null for a category whose kind
        /// maps to none of them - reported as unknown rather than guessed at.
        /// </summary>
        private static BuiltInParameter? VisibilityParameter(Category category)
        {
            switch (category.CategoryType)
            {
                case CategoryType.Model:
                    return BuiltInParameter.VIS_GRAPHICS_MODEL;

                case CategoryType.Annotation:
                    return BuiltInParameter.VIS_GRAPHICS_ANNOTATION;

                case CategoryType.AnalyticalModel:
                    return BuiltInParameter.VIS_GRAPHICS_ANALYTICAL_MODEL;

                default:
                    return null;
            }
        }

        // --- the report ---------------------------------------------------------------------------

        private static Dictionary<string, object> Row(View view, Request request)
        {
            BuiltInParameter? parameter = VisibilityParameter(request.Category);

            return new Dictionary<string, object>
            {
                { "category", request.Name },
                { "categoryId", request.CategoryId.Value },
                { "categoryType", request.Category.CategoryType.ToString() },
                { "templateParameter", parameter.HasValue ? parameter.Value.ToString() : null },

                // Overrides and visibility are two different things; this one is reported, not
                // written, on both sides of the call.
                { "hidden", request.Hidden },
                { "requested", request.Requested },
                { "before", ReadSettings(view.GetCategoryOverrides(request.CategoryId)) },
            };
        }

        /// <summary>
        /// Every field of an override as an explicit value, including the ones this endpoint never
        /// writes. A line weight of InvalidPenNumber and an invalid colour both mean "not
        /// overridden" and are reported as null, because 0 is a weight and black is a colour.
        /// </summary>
        private static Dictionary<string, object> ReadSettings(OverrideGraphicSettings settings)
        {
            if (settings == null)
            {
                return null;
            }

            return new Dictionary<string, object>
            {
                { "projectionLineColor", ReadColor(settings.ProjectionLineColor) },
                { "cutLineColor", ReadColor(settings.CutLineColor) },
                { "projectionLineWeight", ReadWeight(settings.ProjectionLineWeight) },
                { "cutLineWeight", ReadWeight(settings.CutLineWeight) },
                { "halftone", settings.Halftone },
                { "surfaceTransparency", settings.Transparency },
                { "detailLevel", settings.DetailLevel.ToString() },

                // Not written here, reported so it is visible they were not: a merge that dropped a
                // fill pattern would show up as a change on this side of the answer.
                { "projectionLinePatternId", ReadId(settings.ProjectionLinePatternId) },
                { "cutLinePatternId", ReadId(settings.CutLinePatternId) },
                { "surfaceForegroundPatternId", ReadId(settings.SurfaceForegroundPatternId) },
                { "surfaceForegroundPatternColor", ReadColor(settings.SurfaceForegroundPatternColor) },
                { "surfaceForegroundPatternVisible", settings.IsSurfaceForegroundPatternVisible },
                { "surfaceBackgroundPatternId", ReadId(settings.SurfaceBackgroundPatternId) },
                { "surfaceBackgroundPatternColor", ReadColor(settings.SurfaceBackgroundPatternColor) },
                { "surfaceBackgroundPatternVisible", settings.IsSurfaceBackgroundPatternVisible },
                { "cutForegroundPatternId", ReadId(settings.CutForegroundPatternId) },
                { "cutForegroundPatternColor", ReadColor(settings.CutForegroundPatternColor) },
                { "cutForegroundPatternVisible", settings.IsCutForegroundPatternVisible },
                { "cutBackgroundPatternId", ReadId(settings.CutBackgroundPatternId) },
                { "cutBackgroundPatternColor", ReadColor(settings.CutBackgroundPatternColor) },
                { "cutBackgroundPatternVisible", settings.IsCutBackgroundPatternVisible },
            };
        }

        private static object ReadColor(Color color)
        {
            if (color == null || !color.IsValid)
            {
                return null;
            }

            return new Dictionary<string, object>
            {
                { "r", (int)color.Red },
                { "g", (int)color.Green },
                { "b", (int)color.Blue },
            };
        }

        private static object ReadWeight(int weight)
        {
            return weight == OverrideGraphicSettings.InvalidPenNumber ? (object)null : weight;
        }

        private static object ReadId(ElementId id)
        {
            return id == null || id == ElementId.InvalidElementId ? (object)null : id.Value;
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
