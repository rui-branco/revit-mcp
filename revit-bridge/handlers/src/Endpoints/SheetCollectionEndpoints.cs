using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcpBridge.Endpoints
{
    /// <summary>
    /// Sheet collections: the collapsible groups Revit itself draws under Sheets in the Project
    /// Browser, as a native SheetCollection element rather than a browser-organisation parameter
    /// the user has to wire up in the UI.
    ///
    /// One level only. A collection holds sheets; the views under a sheet stay where Revit puts
    /// them, and there is no nesting of collections inside collections to expose.
    /// </summary>
    internal static class SheetCollectionEndpoints
    {
        /// <summary>Characters Revit rejects in a sheet collection name.</summary>
        private const string Prohibited = "{}[]|;<>?`~";

        /// <summary>
        /// Body: {}. Every sheet collection with its members, and the sheets in none of them.
        /// </summary>
        internal static object Collections(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            Dictionary<long, List<ViewSheet>> members = MembersByCollection(document);

            List<object> rows = new List<object>();

            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(SheetCollection)))
            {
                List<ViewSheet> sheets;
                if (!members.TryGetValue(element.Id.Value, out sheets))
                {
                    sheets = new List<ViewSheet>();
                }

                rows.Add(new Dictionary<string, object>
                {
                    { "id", element.Id.Value },
                    { "name", RevitFacts.SafeName(element) },
                    { "sheetCount", sheets.Count },
                    { "sheets", SheetRows(sheets) },
                });
            }

            List<ViewSheet> loose;
            if (!members.TryGetValue(ElementId.InvalidElementId.Value, out loose))
            {
                loose = new List<ViewSheet>();
            }

            return new Dictionary<string, object>
            {
                { "collectionCount", rows.Count },
                { "collections", rows },
                { "unassignedSheetCount", loose.Count },
                { "unassignedSheets", SheetRows(loose) },
            };
        }

        /// <summary>
        /// Body: {collections: [{name, sheetIds: [...]}], dryRun?}. Puts sheets into native sheet
        /// collections, creating the ones that do not exist yet and reusing an existing collection
        /// whose name matches exactly.
        ///
        /// "dryRun" defaults to TRUE: the call reports the plan and writes nothing until it is
        /// passed as false. Nothing is renamed, renumbered or deleted either way - a collection this
        /// request does not mention keeps its members, and a sheet moved out of one is simply moved,
        /// never emptied out of the document.
        /// </summary>
        internal static object SetCollections(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            bool dryRun = JsonBody.OptionalBool(body, "dryRun", true);
            List<Group> groups = ReadGroups(document, body);

            if (dryRun)
            {
                return Report(document, groups, true);
            }

            RevitWrite.InGroup(document, "MCP: set sheet collections", delegate
            {
                RevitWrite.InTransaction(document, "Assign sheet collections", delegate
                {
                    foreach (Group group in groups)
                    {
                        if (group.Unchanged)
                        {
                            continue;
                        }

                        SheetCollection collection = group.Existing;

                        if (collection == null)
                        {
                            collection = SheetCollection.Create(document, group.Name);
                            group.CollectionId = collection.Id;
                        }

                        foreach (ViewSheet sheet in group.Sheets)
                        {
                            if (sheet.SheetCollectionId.Value != collection.Id.Value)
                            {
                                sheet.SheetCollectionId = collection.Id;
                            }
                        }
                    }

                    document.Regenerate();
                });

                return true;
            });

            // Read back off the committed document, not off what was asked for.
            return Report(document, groups, false);
        }

        // --- the request ----------------------------------------------------------------------

        private sealed class Group
        {
            internal string Name;

            internal List<ViewSheet> Sheets;

            /// <summary>The collection already carrying this exact name, or null.</summary>
            internal SheetCollection Existing;

            internal ElementId CollectionId;

            /// <summary>Existing collection, and its membership is already exactly this.</summary>
            internal bool Unchanged;

            /// <summary>Each sheet as it was before the write: id, number, name, old collection.</summary>
            internal List<Dictionary<string, object>> Snapshot;
        }

        private static List<Group> ReadGroups(Document document, JsonElement body)
        {
            JsonElement array = JsonBody.RequireArray(body, "collections");

            Dictionary<string, SheetCollection> existing = ExistingByName(document);
            Dictionary<long, List<ViewSheet>> members = MembersByCollection(document);

            List<Group> groups = new List<Group>();
            Dictionary<string, bool> names = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            Dictionary<long, string> claimed = new Dictionary<long, string>();

            int index = 0;

            foreach (JsonElement item in array.EnumerateArray())
            {
                string label = "collections[" + index + "]";

                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw BridgeException.BadRequest(
                        "\"" + label + "\" must be an object {name, sheetIds}, but was "
                            + item.ValueKind + ".");
                }

                Group group = new Group();
                group.Name = ValidName(JsonBody.RequireString(item, "name"), label);

                if (names.ContainsKey(group.Name))
                {
                    throw BridgeException.BadRequest(
                        "\"" + group.Name + "\" is named twice in this request. One collection is "
                            + "one entry: put all of its sheets in that entry's \"sheetIds\".");
                }

                names[group.Name] = true;

                group.Sheets = new List<ViewSheet>();
                group.Snapshot = new List<Dictionary<string, object>>();

                foreach (long id in JsonBody.RequireIds(item, "sheetIds"))
                {
                    string owner;
                    if (claimed.TryGetValue(id, out owner))
                    {
                        throw BridgeException.BadRequest(
                            "Sheet " + id + " is listed in both \"" + owner + "\" and \""
                                + group.Name + "\". A sheet belongs to one collection.");
                    }

                    claimed[id] = group.Name;

                    ViewSheet sheet = RevitFacts.RequireElement(document, id) as ViewSheet;

                    if (sheet == null)
                    {
                        throw BridgeException.BadRequest(
                            "Element " + id + " is not a sheet. Call /revit-mcp/sheets for the "
                                + "sheets in this document.");
                    }

                    if (sheet.IsAssemblyView)
                    {
                        throw BridgeException.BadRequest(
                            "Sheet " + id + " (" + sheet.SheetNumber + ") is an assembly sheet, and "
                                + "Revit does not allow those in a sheet collection.");
                    }

                    group.Sheets.Add(sheet);
                    group.Snapshot.Add(Snapshot(document, sheet));
                }

                SheetCollection match;
                if (existing.TryGetValue(group.Name, out match))
                {
                    group.Existing = match;
                    group.CollectionId = match.Id;
                    group.Unchanged = SameMembership(members, match.Id, group.Sheets);
                }

                groups.Add(group);
                index++;
            }

            if (groups.Count == 0)
            {
                throw BridgeException.BadRequest(
                    "\"collections\" must hold at least one {name, sheetIds}.");
            }

            return groups;
        }

        private static string ValidName(string name, string label)
        {
            string trimmed = name.Trim();

            if (trimmed.Length == 0)
            {
                throw BridgeException.BadRequest("\"" + label + ".name\" must not be blank.");
            }

            if (trimmed.IndexOfAny(Prohibited.ToCharArray()) >= 0)
            {
                throw BridgeException.BadRequest(
                    "\"" + trimmed + "\" cannot be a sheet collection name: Revit prohibits the "
                        + "characters " + Prohibited + " in one.");
            }

            return trimmed;
        }

        // --- the reply ------------------------------------------------------------------------

        private static object Report(Document document, List<Group> groups, bool dryRun)
        {
            Dictionary<long, List<ViewSheet>> members = dryRun
                ? null
                : MembersByCollection(document);

            List<object> rows = new List<object>();
            int sheetCount = 0;

            foreach (Group group in groups)
            {
                sheetCount += group.Sheets.Count;

                Dictionary<string, object> row = new Dictionary<string, object>
                {
                    { "name", group.Name },
                    { "action", group.Unchanged ? "unchanged" : (group.Existing == null ? "created" : "reused") },
                    { "collectionId", group.CollectionId == null ? (object)null : group.CollectionId.Value },
                    { "requestedSheetCount", group.Sheets.Count },
                    { "sheetsBefore", group.Snapshot },
                };

                if (!dryRun)
                {
                    List<ViewSheet> actual;
                    if (group.CollectionId == null
                        || !members.TryGetValue(group.CollectionId.Value, out actual))
                    {
                        actual = new List<ViewSheet>();
                    }

                    List<object> missing = new List<object>();
                    List<object> renumbered = new List<object>();

                    Dictionary<long, bool> present = new Dictionary<long, bool>();
                    foreach (ViewSheet sheet in actual)
                    {
                        present[sheet.Id.Value] = true;
                    }

                    for (int i = 0; i < group.Sheets.Count; i++)
                    {
                        ViewSheet sheet = group.Sheets[i];

                        if (!present.ContainsKey(sheet.Id.Value))
                        {
                            missing.Add(sheet.Id.Value);
                        }

                        // Revit allows a sheet number to repeat across collections, so a move could
                        // in principle renumber one. Said plainly rather than assumed.
                        string before = (string)group.Snapshot[i]["number"];
                        if (!string.Equals(before, sheet.SheetNumber, StringComparison.Ordinal))
                        {
                            renumbered.Add(new Dictionary<string, object>
                            {
                                { "id", sheet.Id.Value },
                                { "before", before },
                                { "after", sheet.SheetNumber },
                            });
                        }
                    }

                    row["memberSheets"] = SheetRows(actual);
                    row["missingSheetIds"] = missing;
                    row["renumberedSheets"] = renumbered;
                    row["verified"] = missing.Count == 0 && renumbered.Count == 0;
                }

                rows.Add(row);
            }

            Dictionary<string, object> report = new Dictionary<string, object>
            {
                { "dryRun", dryRun },
                { "collectionCount", groups.Count },
                { "sheetCount", sheetCount },
                { "collections", rows },
            };

            if (dryRun)
            {
                report["note"] = "Nothing was written: \"dryRun\" defaults to true. Send the same "
                    + "body with \"dryRun\": false to apply it.";
            }

            return report;
        }

        // --- the document ---------------------------------------------------------------------

        /// <summary>Sheets by the collection they are in, with InvalidElementId for the loose ones.</summary>
        private static Dictionary<long, List<ViewSheet>> MembersByCollection(Document document)
        {
            Dictionary<long, List<ViewSheet>> members = new Dictionary<long, List<ViewSheet>>();

            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(ViewSheet)))
            {
                ViewSheet sheet = element as ViewSheet;
                if (sheet == null)
                {
                    continue;
                }

                long key = sheet.SheetCollectionId == null
                    ? ElementId.InvalidElementId.Value
                    : sheet.SheetCollectionId.Value;

                List<ViewSheet> list;
                if (!members.TryGetValue(key, out list))
                {
                    list = new List<ViewSheet>();
                    members[key] = list;
                }

                list.Add(sheet);
            }

            foreach (List<ViewSheet> list in members.Values)
            {
                list.Sort(delegate (ViewSheet left, ViewSheet right)
                {
                    return string.Compare(left.SheetNumber, right.SheetNumber, StringComparison.OrdinalIgnoreCase);
                });
            }

            return members;
        }

        private static Dictionary<string, SheetCollection> ExistingByName(Document document)
        {
            Dictionary<string, SheetCollection> found =
                new Dictionary<string, SheetCollection>(StringComparer.Ordinal);

            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(SheetCollection)))
            {
                SheetCollection collection = element as SheetCollection;
                if (collection == null)
                {
                    continue;
                }

                string name = RevitFacts.SafeName(collection);
                if (!found.ContainsKey(name))
                {
                    found[name] = collection;
                }
            }

            return found;
        }

        private static bool SameMembership(
            Dictionary<long, List<ViewSheet>> members,
            ElementId collectionId,
            List<ViewSheet> requested)
        {
            List<ViewSheet> actual;
            if (!members.TryGetValue(collectionId.Value, out actual))
            {
                return requested.Count == 0;
            }

            if (actual.Count != requested.Count)
            {
                return false;
            }

            Dictionary<long, bool> present = new Dictionary<long, bool>();
            foreach (ViewSheet sheet in actual)
            {
                present[sheet.Id.Value] = true;
            }

            foreach (ViewSheet sheet in requested)
            {
                if (!present.ContainsKey(sheet.Id.Value))
                {
                    return false;
                }
            }

            return true;
        }

        private static Dictionary<string, object> Snapshot(Document document, ViewSheet sheet)
        {
            Element collection = sheet.SheetCollectionId == null
                ? null
                : document.GetElement(sheet.SheetCollectionId);

            return new Dictionary<string, object>
            {
                { "id", sheet.Id.Value },
                { "number", sheet.SheetNumber },
                { "name", sheet.Name },
                { "collectionId", collection == null ? (object)null : collection.Id.Value },
                { "collectionName", collection == null ? null : RevitFacts.SafeName(collection) },
            };
        }

        private static List<object> SheetRows(List<ViewSheet> sheets)
        {
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
