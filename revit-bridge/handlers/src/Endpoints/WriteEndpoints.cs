using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcpBridge.Endpoints
{
    /// <summary>
    /// Mutating endpoints. Each one wraps its work in RevitWrite.InGroup, so the whole request is a
    /// single undo step and any failure leaves the model untouched.
    ///
    /// All coordinates, elevations and heights are Revit internal units (decimal feet), taken as
    /// given - the bridge converts nothing.
    /// </summary>
    internal static class WriteEndpoints
    {
        /// <summary>
        /// Body: [{name, elevation}] or {"levels": [{name, elevation}]}.
        /// "name" is optional; Revit auto-names the level when it is omitted.
        /// </summary>
        internal static object CreateLevels(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);
            JsonElement requests = JsonBody.ArrayOrProperty(body, "levels");

            List<object> created = new List<object>();

            return RevitWrite.InGroup(document, "MCP: create levels", delegate
            {
                RevitWrite.InTransaction(document, "Create levels", delegate
                {
                    int index = 0;

                    foreach (JsonElement request in requests.EnumerateArray())
                    {
                        double elevation = JsonBody.RequireDouble(request, "elevation");
                        string name = JsonBody.OptionalString(request, "name");

                        Level level = Level.Create(document, elevation);

                        if (name != null)
                        {
                            try
                            {
                                level.Name = name;
                            }
                            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                            {
                                throw BridgeException.BadRequest(
                                    "Could not name level " + index + " \"" + name + "\": "
                                        + ex.Message + " (level names must be unique).");
                            }
                        }

                        created.Add(new Dictionary<string, object>
                        {
                            { "id", level.Id.Value },
                            { "name", RevitFacts.SafeName(level) },
                            { "elevation", level.Elevation },
                        });

                        index++;
                    }
                });

                return created;
            });
        }

        /// <summary>
        /// Body: {level, wallType?, typeName?, height, curves: [{start: {x, y}, end: {x, y}}]}.
        /// Curves are straight segments drawn at the level elevation; the wall type falls back to
        /// the document default when omitted.
        ///
        /// "typeName" is an accepted spelling of "wallType" - every other type-taking endpoint here
        /// calls it that, and a type authored by walltypes/create is selected by name either way.
        /// </summary>
        internal static object CreateWalls(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            string levelName = JsonBody.RequireString(body, "level");
            string wallTypeName = JsonBody.OptionalString(body, "wallType");

            if (wallTypeName == null)
            {
                wallTypeName = JsonBody.OptionalString(body, "typeName");
            }

            double height = JsonBody.RequireDouble(body, "height");
            JsonElement curves = JsonBody.RequireArray(body, "curves");

            if (height <= 0)
            {
                throw BridgeException.BadRequest("\"height\" must be greater than zero (decimal feet).");
            }

            Level level = RevitFacts.ResolveLevel(document, levelName);
            if (level == null)
            {
                throw BridgeException.BadRequest(
                    "Unknown level \"" + levelName + "\". Levels in this document: "
                        + string.Join(", ", RevitFacts.LevelNames(document)) + ".");
            }

            ElementId wallTypeId = ResolveWallTypeId(document, wallTypeName);

            List<object> created = new List<object>();

            return RevitWrite.InGroup(document, "MCP: create walls", delegate
            {
                RevitWrite.InTransaction(document, "Create walls", delegate
                {
                    int index = 0;

                    foreach (JsonElement curve in curves.EnumerateArray())
                    {
                        XYZ start = ReadPoint(curve, "start", index);
                        XYZ end = ReadPoint(curve, "end", index);

                        if (start.DistanceTo(end) < document.Application.ShortCurveTolerance)
                        {
                            throw BridgeException.BadRequest(
                                "curves[" + index + "] is shorter than Revit's short-curve tolerance; "
                                    + "the wall cannot be created.");
                        }

                        Line line = Line.CreateBound(start, end);

                        Wall wall = Wall.Create(
                            document,
                            line,
                            wallTypeId,
                            level.Id,
                            height,
                            0.0,
                            false,
                            false);

                        created.Add(new Dictionary<string, object>
                        {
                            { "id", wall.Id.Value },
                            { "name", RevitFacts.SafeName(wall) },
                            { "typeName", RevitFacts.TypeName(document, wall) },
                            { "level", level.Name },
                            { "height", height },
                        });

                        index++;
                    }
                });

                return created;
            });
        }

        /// <summary>
        /// Body: {ids: [...], name, value, parameterId?}. All-or-nothing: if one element rejects the
        /// value the whole group rolls back.
        ///
        /// **A display name does not identify a parameter.** A family instance carries two
        /// parameters called "Level" - FAMILY_LEVEL_PARAM, which is read-only, and
        /// SCHEDULE_LEVEL_PARAM, which is not - and Element.LookupParameter answers with whichever
        /// Revit enumerates first. That is how a write to "Level" is refused as read-only while the
        /// writable "Level" sits next to it, untouched and unmentioned.
        ///
        /// So a name that matches more than one parameter is no longer guessed at: the write is
        /// refused with AMBIGUOUS_PARAMETER, which lists each candidate with its id, its built-in
        /// name, whether it is read-only and what it currently reads. Pass one of those ids back as
        /// "parameterId" - negative for a built-in, positive for a shared or project parameter, the
        /// same "id" elements/inspect reports - and it is written with no guessing at all.
        ///
        /// A name that matches exactly one parameter behaves as it always has.
        /// </summary>
        internal static object SetParameters(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            List<long> ids = JsonBody.RequireIds(body, "ids");
            string name = JsonBody.RequireString(body, "name");

            JsonElement parameterIdValue;
            long? parameterId = null;
            if (JsonBody.TryGet(body, "parameterId", out parameterIdValue))
            {
                parameterId = JsonBody.AsLong(parameterIdValue, "parameterId");
            }

            JsonElement value;
            if (!JsonBody.TryGet(body, "value", out value))
            {
                throw BridgeException.BadRequest("\"value\" is required.");
            }

            List<object> updated = new List<object>();

            return RevitWrite.InGroup(document, "MCP: set " + name, delegate
            {
                RevitWrite.InTransaction(document, "Set " + name, delegate
                {
                    foreach (long id in ids)
                    {
                        Element element = RevitFacts.RequireElement(document, id);

                        // Instance parameters only: silently writing to the type would change every
                        // other instance of it, which is never what a caller naming ids meant.
                        Parameter parameter = ResolveParameter(element, name, parameterId, id);

                        if (parameter.IsReadOnly)
                        {
                            throw BridgeException.BadRequest(
                                "Parameter \"" + name + "\" (id " + parameter.Id.Value + ") is "
                                    + "read-only on element " + id + ". Call "
                                    + "/revit-mcp/elements/inspect with \"includeParameters\": true "
                                    + "for the ids of the ones that are not - a name Revit uses "
                                    + "twice, such as \"Level\", is read-only on one of them and "
                                    + "writable on the other.");
                        }

                        ApplyValue(parameter, value, name, id);

                        updated.Add(new Dictionary<string, object>
                        {
                            { "id", id },
                            { "name", name },
                            { "parameterId", parameter.Id.Value },
                            { "value", RevitFacts.RawValue(parameter) },
                            { "display", RevitFacts.SafeValueString(parameter) },
                        });
                    }
                });

                return new Dictionary<string, object>
                {
                    { "updated", updated.Count },
                    { "results", updated },
                };
            });
        }

        /// <summary>
        /// The one parameter this write is for. By name when the name is unique on the element -
        /// the old contract, unchanged - and by "parameterId" when it is not.
        ///
        /// Element.LookupParameter is deliberately not used: it answers a repeated name with
        /// whichever parameter Revit enumerated first, and on a family instance "Level" is two
        /// parameters, one of them read-only. Enumerating them all is the only way to know that a
        /// name is ambiguous, and an ambiguous name is refused rather than guessed at - a write
        /// that lands on the wrong parameter of the right name is a change nobody asked for and
        /// nobody can see.
        /// </summary>
        private static Parameter ResolveParameter(Element element, string name, long? parameterId, long id)
        {
            List<Parameter> named = new List<Parameter>();
            Parameter chosen = null;

            foreach (Parameter parameter in element.Parameters)
            {
                if (parameter == null || parameter.Definition == null)
                {
                    continue;
                }

                if (parameterId.HasValue && parameter.Id.Value == parameterId.Value)
                {
                    chosen = parameter;
                }

                if (string.Equals(parameter.Definition.Name, name, StringComparison.Ordinal))
                {
                    named.Add(parameter);
                }
            }

            if (parameterId.HasValue)
            {
                if (chosen == null)
                {
                    throw BridgeException.NotFound(
                        "PARAMETER_NOT_FOUND",
                        "Element " + id + " has no instance parameter with id " + parameterId.Value
                            + ". Call /revit-mcp/elements/inspect with \"includeParameters\": true "
                            + "for the ids this element really carries.");
                }

                // The name is still required, and it still has to be the name of the parameter the
                // id points at: an id from the wrong element, or copied from the wrong row, would
                // otherwise write silently to something the caller never named.
                if (!string.Equals(chosen.Definition.Name, name, StringComparison.Ordinal))
                {
                    throw BridgeException.BadRequest(
                        "Parameter id " + parameterId.Value + " on element " + id + " is \""
                            + chosen.Definition.Name + "\", not \"" + name + "\". Pass the name that "
                            + "goes with the id, or drop \"parameterId\".");
                }

                return chosen;
            }

            if (named.Count == 0)
            {
                throw BridgeException.NotFound(
                    "PARAMETER_NOT_FOUND",
                    "Element " + id + " has no instance parameter named \"" + name + "\".");
            }

            if (named.Count == 1)
            {
                return named[0];
            }

            List<string> candidates = new List<string>();
            foreach (Parameter parameter in named)
            {
                InternalDefinition definition = parameter.Definition as InternalDefinition;
                string builtIn = definition == null || definition.BuiltInParameter == BuiltInParameter.INVALID
                    ? "not built-in"
                    : definition.BuiltInParameter.ToString();

                candidates.Add("id " + parameter.Id.Value + " (" + builtIn + ", "
                    + (parameter.IsReadOnly ? "read-only" : "writable") + ", currently "
                    + RevitFacts.SafeValueString(parameter) + ")");
            }

            throw new BridgeException(
                409,
                "AMBIGUOUS_PARAMETER",
                "Element " + id + " has " + named.Count + " instance parameters named \"" + name
                    + "\": " + string.Join("; ", candidates) + ". Revit uses one display name for "
                    + "more than one parameter, so the name alone cannot say which to write - pass "
                    + "\"parameterId\" with the id you mean. Nothing was written.");
        }

        /// <summary>
        /// Body: {titleBlockId?, sheets: [{number, name}]}.
        ///
        /// One TransactionGroup for the whole batch - 29 sheets are one Ctrl+Z - but one inner
        /// transaction per sheet, because a duplicate SheetNumber throws and only the sheet that
        /// caused it may be rolled back. Those come back in "skipped" rather than as an error, so
        /// re-running the same call after a partial run is safe and says what already existed.
        /// </summary>
        internal static object CreateSheets(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);
            JsonElement requests = JsonBody.RequireArray(body, "sheets");

            ElementId titleBlockId = ResolveTitleBlockId(document, body);

            return RevitWrite.InGroup(document, "MCP: create sheets", delegate
            {
                List<object> created = new List<object>();
                List<object> skipped = new List<object>();

                FamilySymbol titleBlock = (FamilySymbol)document.GetElement(titleBlockId);
                if (!titleBlock.IsActive)
                {
                    // ViewSheet.Create throws on a symbol that has never been activated, and
                    // activating one is itself a model change, so it needs a transaction.
                    RevitWrite.InTransaction(document, "Activate title block", delegate
                    {
                        titleBlock.Activate();
                        document.Regenerate();
                    });
                }

                foreach (JsonElement request in requests.EnumerateArray())
                {
                    // Read outside the try: a malformed request is a BAD_REQUEST for the whole
                    // call, not a sheet Revit refused.
                    string number = JsonBody.RequireString(request, "number");
                    string name = JsonBody.RequireString(request, "name");

                    ViewSheet sheet = null;

                    try
                    {
                        RevitWrite.InTransaction(document, "Create sheet " + number, delegate
                        {
                            sheet = ViewSheet.Create(document, titleBlockId);
                            sheet.SheetNumber = number;
                            sheet.Name = name;
                        });
                    }
                    catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                    {
                        // Almost always "the name entered is already in use": the sheet number or
                        // name exists. The inner transaction rolled back, so nothing was left
                        // behind, and the rest of the batch carries on.
                        skipped.Add(new Dictionary<string, object>
                        {
                            { "number", number },
                            { "reason", ex.Message },
                        });

                        continue;
                    }

                    created.Add(new Dictionary<string, object>
                    {
                        { "id", sheet.Id.Value },
                        { "number", sheet.SheetNumber },
                        { "name", sheet.Name },
                    });
                }

                return new Dictionary<string, object>
                {
                    { "created", created },
                    { "skipped", skipped },
                };
            });
        }

        /// <summary>Body: {ids: [...]}, or a bare array of ids.</summary>
        internal static object DeleteElements(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            List<long> ids = JsonBody.RequireIds(body, "ids");

            List<ElementId> targets = new List<ElementId>();
            foreach (long id in ids)
            {
                // Validate up front so a bad id fails before anything is deleted.
                RevitFacts.RequireElement(document, id);
                targets.Add(new ElementId(id));
            }

            return RevitWrite.InGroup(document, "MCP: delete elements", delegate
            {
                List<object> deletedIds = new List<object>();

                RevitWrite.InTransaction(document, "Delete elements", delegate
                {
                    ICollection<ElementId> deleted = document.Delete(targets);

                    if (deleted != null)
                    {
                        foreach (ElementId elementId in deleted)
                        {
                            deletedIds.Add(elementId.Value);
                        }
                    }
                });

                return new Dictionary<string, object>
                {
                    { "requested", ids.Count },

                    // Can exceed "requested": Revit also removes dependent elements.
                    { "deleted", deletedIds.Count },
                    { "deletedIds", deletedIds },
                };
            });
        }

        private static ElementId ResolveWallTypeId(Document document, string wallTypeName)
        {
            if (wallTypeName == null)
            {
                ElementId defaultId = document.GetDefaultElementTypeId(ElementTypeGroup.WallType);
                if (defaultId == null || defaultId == ElementId.InvalidElementId)
                {
                    throw BridgeException.BadRequest(
                        "This document has no default wall type; pass \"wallType\" explicitly.");
                }

                return defaultId;
            }

            List<string> available = new List<string>();

            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(WallType)))
            {
                string name = RevitFacts.SafeName(element);
                if (name == null)
                {
                    continue;
                }

                if (string.Equals(name, wallTypeName, StringComparison.OrdinalIgnoreCase))
                {
                    return element.Id;
                }

                available.Add(name);
            }

            available.Sort(StringComparer.OrdinalIgnoreCase);

            throw BridgeException.BadRequest(
                "Unknown wall type \"" + wallTypeName + "\". Wall types in this document: "
                    + string.Join(", ", available) + ".");
        }

        /// <summary>
        /// The requested title block symbol, or the first one loaded when "titleBlockId" is
        /// omitted. A sheet without a title block is not worth creating, so a document with none
        /// loaded is a NO_TITLEBLOCK failure rather than a silent fallback.
        /// </summary>
        private static ElementId ResolveTitleBlockId(Document document, JsonElement body)
        {
            FilteredElementCollector collector = new FilteredElementCollector(document)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .OfClass(typeof(FamilySymbol));

            JsonElement value;
            if (JsonBody.TryGet(body, "titleBlockId", out value))
            {
                long id = JsonBody.AsLong(value, "titleBlockId");

                foreach (Element element in collector)
                {
                    if (element.Id.Value == id)
                    {
                        return element.Id;
                    }
                }

                throw BridgeException.BadRequest(
                    "Element " + id + " is not a title block family type in this document. Call "
                        + "/revit-mcp/titleblocks for the ones that are loaded.");
            }

            foreach (Element element in collector)
            {
                return element.Id;
            }

            throw new BridgeException(
                409,
                "NO_TITLEBLOCK",
                "This document has no title block family loaded, so no sheet can be created. Load a "
                    + "title block family in Revit (Insert > Load Family, from the Titleblocks "
                    + "folder) and retry; /revit-mcp/titleblocks lists what is loaded.");
        }

        private static XYZ ReadPoint(JsonElement curve, string name, int index)
        {
            JsonElement point;
            if (!JsonBody.TryGet(curve, name, out point))
            {
                throw BridgeException.BadRequest(
                    "curves[" + index + "] is missing \"" + name + "\": expected {x, y}.");
            }

            double x = JsonBody.RequireDouble(point, "x");
            double y = JsonBody.RequireDouble(point, "y");

            // z is optional and defaults to 0; the wall is hosted on the level regardless.
            double z = 0.0;
            JsonElement zValue;
            if (JsonBody.TryGet(point, "z", out zValue))
            {
                z = JsonBody.AsDouble(zValue, "z");
            }

            return new XYZ(x, y, z);
        }

        /// <summary>
        /// Writes a JSON value into a parameter, branching on the parameter's storage type.
        /// Internal because sheets/set-parameter writes a different value per element and must
        /// answer for the same storage types in the same way - see ParameterEndpoints.
        /// </summary>
        internal static void ApplyValue(Parameter parameter, JsonElement value, string name, long id)
        {
            switch (parameter.StorageType)
            {
                case StorageType.Double:
                    parameter.Set(JsonBody.AsDouble(value, name));
                    return;

                case StorageType.Integer:
                    if (value.ValueKind == JsonValueKind.True)
                    {
                        parameter.Set(1);
                        return;
                    }

                    if (value.ValueKind == JsonValueKind.False)
                    {
                        parameter.Set(0);
                        return;
                    }

                    parameter.Set((int)JsonBody.AsDouble(value, name));
                    return;

                case StorageType.String:
                    parameter.Set(JsonBody.AsString(value, name));
                    return;

                case StorageType.ElementId:
                    parameter.Set(new ElementId(JsonBody.AsLong(value, name)));
                    return;

                default:
                    throw BridgeException.BadRequest(
                        "Parameter \"" + name + "\" on element " + id + " has storage type "
                            + parameter.StorageType + ", which the bridge cannot set.");
            }
        }
    }
}
