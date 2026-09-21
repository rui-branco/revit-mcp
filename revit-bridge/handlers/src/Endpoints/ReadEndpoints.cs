using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcpBridge.Endpoints
{
    /// <summary>
    /// Read-only endpoints. Every one of these runs on Revit's main thread (the router puts it
    /// there) and none of them opens a transaction.
    /// </summary>
    internal static class ReadEndpoints
    {
        private const int DefaultLimit = 100;

        /// <summary>Hard ceiling: a runaway query must not try to serialise a whole model.</summary>
        private const int MaxLimit = 500;

        /// <summary>
        /// The one endpoint that deliberately tolerates having no document - it is how a client
        /// discovers that fact in the first place.
        /// </summary>
        internal static object Status(UIApplication app, JsonElement body)
        {
            Autodesk.Revit.ApplicationServices.Application revit = app.Application;

            Dictionary<string, object> result = new Dictionary<string, object>
            {
                { "bridgeVersion", BridgeInfo.Version },
                { "url", HttpBridgeServer.UrlPrefix + "revit-mcp/" },
                { "units", "Revit internal units (decimal feet)" },
                { "revitVersion", revit.VersionNumber },
                { "revitVersionName", revit.VersionName },
                { "revitVersionBuild", revit.VersionBuild },
                { "revitSubVersion", revit.SubVersionNumber },
                { "username", revit.Username },
                { "activeDocument", null },
            };

            UIDocument uiDocument = app.ActiveUIDocument;
            if (uiDocument != null && uiDocument.Document != null)
            {
                Document document = uiDocument.Document;

                result["activeDocument"] = new Dictionary<string, object>
                {
                    { "title", document.Title },

                    // Empty string for a model that has never been saved.
                    { "pathName", document.PathName },
                    { "isWorkshared", document.IsWorkshared },
                    { "isFamilyDocument", document.IsFamilyDocument },
                    { "isReadOnly", document.IsReadOnly },
                    { "isModified", document.IsModified },
                };
            }

            return result;
        }

        internal static object Levels(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            List<Level> levels = new List<Level>();
            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(Level)))
            {
                Level level = element as Level;
                if (level != null)
                {
                    levels.Add(level);
                }
            }

            levels.Sort(delegate (Level left, Level right)
            {
                return left.Elevation.CompareTo(right.Elevation);
            });

            List<object> rows = new List<object>();
            foreach (Level level in levels)
            {
                rows.Add(new Dictionary<string, object>
                {
                    { "id", level.Id.Value },
                    { "name", RevitFacts.SafeName(level) },
                    { "elevation", level.Elevation },
                });
            }

            return rows;
        }

        internal static object Categories(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);

            FilteredElementCollector collector =
                new FilteredElementCollector(document).WhereElementIsNotElementType();

            foreach (Element element in collector)
            {
                string name = RevitFacts.CategoryName(element);
                if (name == null)
                {
                    continue;
                }

                int current;
                counts.TryGetValue(name, out current);
                counts[name] = current + 1;
            }

            List<KeyValuePair<string, int>> ordered = new List<KeyValuePair<string, int>>(counts);
            ordered.Sort(delegate (KeyValuePair<string, int> left, KeyValuePair<string, int> right)
            {
                int byCount = right.Value.CompareTo(left.Value);
                if (byCount != 0)
                {
                    return byCount;
                }

                return string.Compare(left.Key, right.Key, StringComparison.OrdinalIgnoreCase);
            });

            List<object> rows = new List<object>();
            foreach (KeyValuePair<string, int> entry in ordered)
            {
                rows.Add(new Dictionary<string, object>
                {
                    { "category", entry.Key },
                    { "count", entry.Value },
                });
            }

            return rows;
        }

        internal static object Query(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            string categoryName = JsonBody.OptionalString(body, "category");
            string levelName = JsonBody.OptionalString(body, "level");
            string typeName = JsonBody.OptionalString(body, "typeName");

            int limit = JsonBody.OptionalInt(body, "limit", DefaultLimit);
            if (limit < 1)
            {
                limit = 1;
            }

            if (limit > MaxLimit)
            {
                limit = MaxLimit;
            }

            int offset = JsonBody.OptionalInt(body, "offset", 0);
            if (offset < 0)
            {
                offset = 0;
            }

            FilteredElementCollector collector =
                new FilteredElementCollector(document).WhereElementIsNotElementType();

            if (categoryName != null)
            {
                Category category = RevitFacts.ResolveCategory(document, categoryName);
                if (category == null)
                {
                    throw BridgeException.BadRequest(
                        "Unknown category \"" + categoryName + "\". Call /revit-mcp/categories for "
                            + "the categories present in this document, or pass a BuiltInCategory "
                            + "name such as OST_Walls.");
                }

                collector = collector.OfCategoryId(category.Id);
            }

            if (levelName != null)
            {
                Level level = RevitFacts.ResolveLevel(document, levelName);
                if (level == null)
                {
                    throw BridgeException.BadRequest(
                        "Unknown level \"" + levelName + "\". Levels in this document: "
                            + string.Join(", ", RevitFacts.LevelNames(document)) + ".");
                }

                collector = collector.WherePasses(new ElementLevelFilter(level.Id));
            }

            // typeName has no native filter, so it is applied while walking the collector.
            List<Element> matches = new List<Element>();
            foreach (Element element in collector)
            {
                if (typeName != null)
                {
                    string elementTypeName = RevitFacts.TypeName(document, element);
                    if (!string.Equals(elementTypeName, typeName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                matches.Add(element);
            }

            List<object> rows = new List<object>();
            for (int index = offset; index < matches.Count && rows.Count < limit; index++)
            {
                rows.Add(RevitFacts.CompactRow(document, matches[index]));
            }

            return new Dictionary<string, object>
            {
                { "total", matches.Count },
                { "offset", offset },
                { "limit", limit },
                { "rows", rows },
            };
        }

        /// <summary>
        /// Returns identity plus only the parameters named in "params". Never dumps every
        /// parameter an element has - that is how you turn one element into 200 lines of noise.
        /// </summary>
        internal static object Elements(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            List<long> ids = JsonBody.RequireIds(body, "ids");
            List<string> requested = JsonBody.OptionalStringList(body, "params");

            List<object> rows = new List<object>();

            foreach (long id in ids)
            {
                Element element = document.GetElement(new ElementId(id));

                if (element == null)
                {
                    // A missing id is reported per-row rather than failing the whole batch.
                    rows.Add(new Dictionary<string, object>
                    {
                        { "id", id },
                        { "found", false },
                    });

                    continue;
                }

                Dictionary<string, object> row = RevitFacts.CompactRow(document, element);
                row["found"] = true;

                if (requested != null && requested.Count > 0)
                {
                    Dictionary<string, object> parameters = new Dictionary<string, object>(StringComparer.Ordinal);

                    foreach (string name in requested)
                    {
                        bool fromType;
                        Parameter parameter = RevitFacts.FindParameter(document, element, name, out fromType);

                        if (parameter == null)
                        {
                            parameters[name] = null;
                            continue;
                        }

                        parameters[name] = RevitFacts.DescribeParameter(parameter, fromType);
                    }

                    row["parameters"] = parameters;
                }

                rows.Add(row);
            }

            return rows;
        }

        internal static object Selection(UIApplication app, JsonElement body)
        {
            UIDocument uiDocument = RevitFacts.RequireUiDocument(app);
            Document document = uiDocument.Document;

            ICollection<ElementId> selected = uiDocument.Selection.GetElementIds();

            List<object> ids = new List<object>();
            List<object> rows = new List<object>();

            foreach (ElementId elementId in selected)
            {
                ids.Add(elementId.Value);

                Element element = document.GetElement(elementId);
                if (element != null)
                {
                    rows.Add(RevitFacts.CompactRow(document, element));
                }
            }

            return new Dictionary<string, object>
            {
                { "count", selected.Count },
                { "ids", ids },
                { "elements", rows },
            };
        }

        /// <summary>
        /// The title block family types loaded in the document. An empty array - not an error - is
        /// the honest answer for a document with no title block family loaded; sheets/create is
        /// where that becomes a problem worth reporting.
        /// </summary>
        internal static object TitleBlocks(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            List<object> rows = new List<object>();

            FilteredElementCollector collector = new FilteredElementCollector(document)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .OfClass(typeof(FamilySymbol));

            foreach (Element element in collector)
            {
                FamilySymbol symbol = element as FamilySymbol;
                if (symbol == null)
                {
                    continue;
                }

                rows.Add(new Dictionary<string, object>
                {
                    { "id", symbol.Id.Value },
                    { "familyName", symbol.FamilyName },
                    { "typeName", RevitFacts.SafeName(symbol) },
                });
            }

            return rows;
        }

        internal static object Sheets(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            List<ViewSheet> sheets = new List<ViewSheet>();
            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(ViewSheet)))
            {
                ViewSheet sheet = element as ViewSheet;
                if (sheet != null)
                {
                    sheets.Add(sheet);
                }
            }

            sheets.Sort(delegate (ViewSheet left, ViewSheet right)
            {
                return string.Compare(left.SheetNumber, right.SheetNumber, StringComparison.OrdinalIgnoreCase);
            });

            List<object> rows = new List<object>();
            foreach (ViewSheet sheet in sheets)
            {
                rows.Add(new Dictionary<string, object>
                {
                    { "id", sheet.Id.Value },
                    { "number", sheet.SheetNumber },
                    { "name", sheet.Name },
                });
            }

            return rows;
        }
    }
}
