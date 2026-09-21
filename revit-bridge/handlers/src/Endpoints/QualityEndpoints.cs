using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;

namespace RevitMcpBridge.Endpoints
{
    /// <summary>
    /// Quality-assurance endpoints: what is actually in the model, and the one edit that is safe to
    /// make while checking it.
    ///
    /// These exist because every other read here is deliberately compact - id, name, category, type
    /// - and compact is not enough to answer "is this tree standing in the pool" or "is that chair
    /// buried under the slab". That needs measured geometry: the location Revit holds, the model
    /// bounding box, the grades of the site surface, the loops a floor was sketched from. So this is
    /// the one place that reports geometry at length, and it still bounds what it returns.
    ///
    /// Same rules as the rest of the bridge: every length is Revit internal units (decimal feet),
    /// unconverted in both directions, and every point is in absolute model coordinates - the
    /// document's internal origin, the same frame element locations and bounding boxes are reported
    /// in. Nothing here is relative to a level, a view or a survey point. The responses say so in
    /// "coordinateSystem" rather than leaving the caller to assume it.
    ///
    /// Three of the five are read-only and open no transaction. elements/move and
    /// toposolid/excavate are the exceptions: both default to a dry run, both are all-or-nothing,
    /// and neither ever deletes anything. See MoveElements and ExcavateToposolid.
    /// </summary>
    internal static class QualityEndpoints
    {
        /// <summary>Hard ceiling on ids per inspect request - serialising a whole model is not a read.</summary>
        private const int MaxInspectIds = 500;

        private const int DefaultWarningLimit = 100;

        /// <summary>Hard ceiling on warnings per page, same as the other paged read.</summary>
        private const int MaxWarningLimit = 500;

        /// <summary>
        /// How many geometry points one element may contribute - slab-shape vertices, floor loop
        /// segments. A graded site surface runs to thousands of vertices and dumping all of them is
        /// how an inspection becomes unreadable; past this the count and the Z range still come
        /// back, so nothing is hidden, only abbreviated.
        /// </summary>
        private const int MaxGeometryPoints = 500;

        private const string CoordinateSystem =
            "Absolute Revit model coordinates (the document's internal origin), decimal feet. Not "
                + "relative to a level, a view, the project base point or the survey point.";

        /// <summary>
        /// Body: {ids: [id], includeParameters?, includeGeometry?}. Everything measurable about the
        /// named elements: identity, where Revit holds them, and the geometry that says whether they
        /// are where they should be.
        ///
        /// Per element: id, name, uniqueId, class (the Revit API type), category, typeId, typeName,
        /// level, hostId, pinned, groupId, location (a point or a curve's endpoints), the model
        /// bounding box, and the material ids it carries.
        ///
        /// Then, by what the element is:
        /// - a **floor** reports its level and height offset, and the closed loops of its top face -
        ///   straight line segments as endpoints, anything curved as a tessellation;
        /// - a **toposolid** (and a legacy TopographySurface) reports its shape vertices with their
        ///   absolute positions, which is the only way to see the grades paving and planting have to
        ///   sit on;
        /// - a **family instance** reports its symbol, its facing and hand vectors, and the Z it
        ///   actually sits at - read off the instance, not off whatever was asked for when it was
        ///   placed.
        ///
        /// "includeParameters" (default false) adds every instance parameter, each with its own
        /// parameter id and built-in name, plus "duplicateParameters" for the names Revit uses more
        /// than once on the same element - see InstanceParameters, and read it before writing
        /// anything called "Level". Off by default for the usual reason: it is 200 lines of noise
        /// per element when three of them were the question.
        ///
        /// "includeGeometry" is a tri-state. Unset, vertex and segment lists come back whenever
        /// there are <see cref="MaxGeometryPoints"/> of them or fewer, and collapse to a count plus
        /// a min/max Z above that. true asks for them anyway, capped at the same number with
        /// "truncated": true. false keeps the counts and drops the lists.
        ///
        /// Ids with no element are not silently dropped - they come back in "missingIds", and
        /// "requested"/"found" say how many of each.
        /// </summary>
        internal static object InspectElements(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            List<long> ids = JsonBody.RequireIds(body, "ids");
            if (ids.Count > MaxInspectIds)
            {
                throw BridgeException.BadRequest(
                    "\"ids\" carries " + ids.Count + " ids; the cap is " + MaxInspectIds
                        + " per request. Inspect them in batches.");
            }

            bool includeParameters = JsonBody.OptionalBool(body, "includeParameters", false);

            bool? includeGeometry = null;
            JsonElement geometryValue;
            if (JsonBody.TryGet(body, "includeGeometry", out geometryValue))
            {
                includeGeometry = JsonBody.AsBool(geometryValue, "includeGeometry");
            }

            List<object> elements = new List<object>();
            List<object> missing = new List<object>();

            foreach (long id in ids)
            {
                Element element = document.GetElement(new ElementId(id));

                if (element == null)
                {
                    missing.Add(id);
                    continue;
                }

                elements.Add(Describe(document, element, includeParameters, includeGeometry));
            }

            return new Dictionary<string, object>
            {
                { "coordinateSystem", CoordinateSystem },
                { "units", "Revit internal units (decimal feet)" },
                { "requested", ids.Count },
                { "found", elements.Count },
                { "missingIds", missing },
                { "elements", elements },
            };
        }

        /// <summary>
        /// Body: {ids: [id], translation: {x, y, z?}, dryRun?}. Moves elements by a vector, or says
        /// what moving them would do.
        ///
        /// All-or-nothing, on purpose. The whole batch goes through one transaction inside one
        /// transaction group, so it is one Ctrl+Z and a failure part way through rolls every element
        /// back rather than leaving half a relocation in the model. Nothing is ever deleted: the ids
        /// in the response are exactly the ids that were asked for.
        ///
        /// Three things make the request fail before anything is touched, each naming every offender
        /// rather than the first:
        /// - an id with no element behind it (ELEMENT_NOT_FOUND);
        /// - a pinned element (ELEMENTS_PINNED) - pinning is a decision somebody made and this
        ///   endpoint will not quietly undo it. Unpin in Revit, or leave that element out;
        /// - an element in a group (ELEMENTS_GROUPED) - moving one member moves it out of step with
        ///   every other instance of that group, so it is refused rather than done silently.
        ///
        /// "dryRun" defaults to **true**: the default call measures and changes nothing. It opens no
        /// transaction at all, so there is nothing to undo and nothing to roll back.
        ///
        /// Either way every element comes back with "before" and "after" - location and model
        /// bounding box - so the caller never has to guess where something ended up. On a dry run
        /// "after" is arithmetic ("afterSource": "predicted"); on a real move it is read back off
        /// the element after a regeneration ("afterSource": "readBack"), which is the only way to
        /// see Revit having done something other than what was asked.
        ///
        /// A real move is also measured before it is allowed to commit - see VerifyMoved. Revit
        /// accepts a move it then declines to apply, and a no-op reported as "moved": true is the
        /// worst answer this endpoint could give, so the displacement every element actually took
        /// is compared against the one that was asked for and the whole group is rolled back
        /// (MOVE_NOT_APPLIED) when they disagree. Elements with nothing measurable to compare come
        /// back in "unverified" rather than being counted as moved.
        /// </summary>
        internal static object MoveElements(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            List<long> ids = JsonBody.RequireIds(body, "ids");
            XYZ translation = RequireTranslation(body);
            bool dryRun = JsonBody.OptionalBool(body, "dryRun", true);

            List<Element> elements = new List<Element>();
            List<object> missing = new List<object>();
            List<object> pinned = new List<object>();
            List<object> grouped = new List<object>();

            foreach (long id in ids)
            {
                Element element = document.GetElement(new ElementId(id));

                if (element == null)
                {
                    missing.Add(id);
                    continue;
                }

                if (element.Pinned)
                {
                    pinned.Add(id);
                }

                if (element.GroupId != null && element.GroupId != ElementId.InvalidElementId)
                {
                    grouped.Add(id);
                }

                elements.Add(element);
            }

            if (missing.Count > 0)
            {
                throw BridgeException.NotFound(
                    "ELEMENT_NOT_FOUND",
                    "No element exists for id(s) " + string.Join(", ", missing) + " in "
                        + document.Title + ". Nothing was moved - this endpoint moves the whole "
                        + "batch or none of it.");
            }

            if (pinned.Count > 0)
            {
                throw new BridgeException(
                    409,
                    "ELEMENTS_PINNED",
                    "Element(s) " + string.Join(", ", pinned) + " are pinned. The bridge will not "
                        + "unpin them for you - unpin in Revit if the move is intended, or leave "
                        + "them out. Nothing was moved.");
            }

            if (grouped.Count > 0)
            {
                throw new BridgeException(
                    409,
                    "ELEMENTS_GROUPED",
                    "Element(s) " + string.Join(", ", grouped) + " belong to a group. Moving one "
                        + "member alone puts it out of step with every other instance of that "
                        + "group, so it is refused. Move the group itself, or ungroup in Revit "
                        + "first. Nothing was moved.");
            }

            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();

            foreach (Element element in elements)
            {
                rows.Add(new Dictionary<string, object>
                {
                    { "id", element.Id.Value },
                    { "name", RevitFacts.SafeName(element) },
                    { "category", RevitFacts.CategoryName(element) },
                    { "before", State(element) },
                });
            }

            if (dryRun)
            {
                for (int index = 0; index < elements.Count; index++)
                {
                    rows[index]["after"] = Shift(rows[index]["before"] as Dictionary<string, object>, translation);
                }

                return MoveResult(rows, translation, true, "predicted");
            }

            List<ElementId> elementIds = new List<ElementId>();
            List<XYZ> anchors = new List<XYZ>();
            foreach (Element element in elements)
            {
                elementIds.Add(element.Id);
                anchors.Add(Anchor(element));
            }

            List<object> unverified = new List<object>();

            return RevitWrite.InGroup(document, "MCP: move elements", delegate
            {
                RevitWrite.InTransaction(document, "Move elements", delegate
                {
                    // One call for the whole batch: Revit moves them together or throws, and a
                    // throw takes the transaction - and then the group - back with it.
                    ElementTransformUtils.MoveElements(document, elementIds, translation);

                    // A location read before the document catches up is the old one.
                    document.Regenerate();

                    // Still inside the transaction, so a "no" can still be taken back.
                    unverified = VerifyMoved(document, elementIds, anchors, translation);
                });

                for (int index = 0; index < elementIds.Count; index++)
                {
                    // Read back off the document rather than off the captured Element: what the
                    // model says now is the only answer worth reporting.
                    Element moved = document.GetElement(elementIds[index]);
                    rows[index]["after"] = moved == null ? null : State(moved);
                }

                Dictionary<string, object> result = MoveResult(rows, translation, false, "readBack");
                result["unverified"] = unverified;
                return result;
            });
        }

        /// <summary>
        /// Body: {toposolidId?, ids: [id], dryRun?}. Cuts the toposolid with the elements named,
        /// through Revit 2025's native Toposolid.ExcavateBy - which is what a pool, a basement or a
        /// sunken path is supposed to do to the ground.
        ///
        /// This is the non-destructive alternative to toposolid/flatten. Flattening rewrites the
        /// grades under a region and the ground it levelled never comes back; an excavation is an
        /// association between two elements, so the surface keeps every vertex it has, the hole
        /// follows the element that made it, and Revit can take it off again by itself
        /// (Toposolid.RemoveExcavationBy, in the UI). Reach for this first, and flatten only when
        /// the ground genuinely has to change shape.
        ///
        /// "toposolidId" may be left out when the document has exactly one toposolid, same as
        /// toposolid/flatten. Every id in "ids" is put through Toposolid.CanBeExcavatedBy BEFORE
        /// anything is opened: Revit answers an unsupported element with an InvalidOperationException
        /// mid-transaction, and a preflight that names every offender is a better answer than a 500.
        ///
        /// "dryRun" defaults to **true**: the default call is the preflight and nothing else. It
        /// opens no transaction at all, so there is nothing to undo and nothing to roll back.
        ///
        /// The volume readback is the point of the response. "volumeBefore" and "volumeAfter" are
        /// the toposolid's own computed volume in cubic feet and "volumeRemoved" is the difference -
        /// the one number that says the excavation actually cut something rather than merely being
        /// accepted. Each element also reports the volume Revit attributes to it; an element that
        /// was excavated and still reports null takes nothing out, which means it does not overlap
        /// the surface. None of the three can be predicted - Revit computes the intersection, it is
        /// not arithmetic - so a dry run answers null rather than guessing.
        ///
        /// An element that already excavates this toposolid is reported, not excavated again.
        /// </summary>
        internal static object ExcavateToposolid(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            Toposolid toposolid = RequireToposolid(document, body);
            List<long> ids = JsonBody.RequireIds(body, "ids");
            bool dryRun = JsonBody.OptionalBool(body, "dryRun", true);

            // Read once, before anything: what already cuts this surface decides both what is
            // reported and what is left alone.
            Dictionary<long, double> excavations = Excavations(toposolid);

            List<Element> elements = new List<Element>();
            List<object> missing = new List<object>();
            List<object> refused = new List<object>();

            foreach (long id in ids)
            {
                if (id == toposolid.Id.Value)
                {
                    throw BridgeException.BadRequest(
                        "Element " + id + " is the toposolid being excavated. \"ids\" is the list of "
                            + "elements that cut it - a pool, a floor, a mass - not the surface itself.");
                }

                Element element = document.GetElement(new ElementId(id));

                if (element == null)
                {
                    missing.Add(id);
                    continue;
                }

                // An element that is already excavating answers false here, so ask Revit only about
                // the ones this call would actually hand to ExcavateBy.
                if (!excavations.ContainsKey(id) && !toposolid.CanBeExcavatedBy(element.Id))
                {
                    refused.Add(id);
                }

                elements.Add(element);
            }

            if (missing.Count > 0)
            {
                throw BridgeException.NotFound(
                    "ELEMENT_NOT_FOUND",
                    "No element exists for id(s) " + string.Join(", ", missing) + " in "
                        + document.Title + ". Nothing was excavated - this endpoint excavates the "
                        + "whole batch or none of it.");
            }

            if (refused.Count > 0)
            {
                throw new BridgeException(
                    409,
                    "CANNOT_EXCAVATE",
                    "Toposolid " + toposolid.Id.Value + " cannot be excavated by element(s) "
                        + string.Join(", ", refused) + ": Revit's own CanBeExcavatedBy says no. Only "
                        + "some element kinds can cut a toposolid - a floor, a wall, a mass, a "
                        + "family instance that cuts. Nothing was excavated.");
            }

            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();

            foreach (Element element in elements)
            {
                bool already = excavations.ContainsKey(element.Id.Value);

                rows.Add(new Dictionary<string, object>
                {
                    { "id", element.Id.Value },
                    { "name", RevitFacts.SafeName(element) },
                    { "category", RevitFacts.CategoryName(element) },
                    { "alreadyExcavating", already },
                    { "excavated", false },
                    { "excavationVolume", already ? (object)excavations[element.Id.Value] : null },
                });
            }

            object volumeBefore = Volume(toposolid);

            if (dryRun)
            {
                return ExcavationResult(toposolid, rows, true, 0, volumeBefore, null);
            }

            List<ElementId> pending = new List<ElementId>();
            foreach (Element element in elements)
            {
                if (!excavations.ContainsKey(element.Id.Value))
                {
                    pending.Add(element.Id);
                }
            }

            if (pending.Count == 0)
            {
                // Every one of them already cuts this surface. Opening a transaction to do nothing
                // would still cost the user an undo step, so nothing is opened.
                return ExcavationResult(toposolid, rows, false, 0, volumeBefore, volumeBefore);
            }

            return RevitWrite.InGroup(document, "MCP: excavate toposolid", delegate
            {
                RevitWrite.InTransaction(document, "Excavate toposolid", delegate
                {
                    foreach (ElementId id in pending)
                    {
                        // Revit takes them one at a time. A throw takes the transaction - and then
                        // the group - back with it, so a batch that fails part way through leaves
                        // no half-cut surface behind.
                        toposolid.ExcavateBy(id);
                    }

                    // The volume parameter and the intersection data are both stale until the
                    // document catches up.
                    document.Regenerate();
                });

                Toposolid cut = document.GetElement(toposolid.Id) as Toposolid;
                Dictionary<long, double> after = cut == null
                    ? new Dictionary<long, double>()
                    : Excavations(cut);

                int excavated = 0;

                foreach (Dictionary<string, object> row in rows)
                {
                    long id = (long)row["id"];

                    if (!(bool)row["alreadyExcavating"])
                    {
                        row["excavated"] = true;
                        excavated++;
                    }

                    // Read back rather than assumed: an element Revit accepted that still takes no
                    // volume out is one that does not overlap the surface, and saying so is the
                    // whole point of reporting this.
                    row["excavationVolume"] = after.ContainsKey(id) ? (object)after[id] : null;
                }

                return ExcavationResult(cut == null ? toposolid : cut, rows, false, excavated,
                    volumeBefore, Volume(cut == null ? toposolid : cut));
            });
        }

        /// <summary>
        /// Body: {limit?, offset?}. The warnings the document is carrying right now - Revit's own
        /// Review Warnings list, read straight off Document.GetWarnings().
        ///
        /// Read-only and transaction-free. These are not the warnings the bridge resolved on your
        /// behalf during a write - those live at /revit-mcp/diagnostics and are a different list.
        /// This one is the standing state of the model: overlapping walls, a room not enclosed, a
        /// toposolid and a floor in the same place.
        ///
        /// Each warning carries the GUID of its failure definition, which is the only stable
        /// identity Revit gives a warning kind - the message text is localised and reworded between
        /// releases. "failingElementIds" is what the warning is about; "additionalElementIds" is the
        /// other half of a two-element warning (the second of the overlapping walls, say).
        ///
        /// Paged like the other list endpoints: "total" is the real count, a limit over
        /// <see cref="MaxWarningLimit"/> is clamped rather than refused.
        /// </summary>
        internal static object Warnings(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            int limit = JsonBody.OptionalInt(body, "limit", DefaultWarningLimit);
            if (limit < 1)
            {
                limit = 1;
            }

            if (limit > MaxWarningLimit)
            {
                limit = MaxWarningLimit;
            }

            int offset = JsonBody.OptionalInt(body, "offset", 0);
            if (offset < 0)
            {
                offset = 0;
            }

            IList<FailureMessage> warnings = document.GetWarnings();

            List<object> rows = new List<object>();

            for (int index = offset; index < warnings.Count && rows.Count < limit; index++)
            {
                FailureMessage warning = warnings[index];

                rows.Add(new Dictionary<string, object>
                {
                    { "failureDefinitionId", FailureDefinitionGuid(warning) },
                    { "severity", warning.GetSeverity().ToString() },
                    { "message", warning.GetDescriptionText() },
                    // FailureMessage names these GetFailingElements / GetAdditionalElements; the
                    // ...ElementIds spelling belongs to FailureMessageAccessor, which is the
                    // commit-time view of the same thing. Both hand back ElementIds.
                    { "failingElementIds", Ids(warning.GetFailingElements()) },
                    { "additionalElementIds", Ids(warning.GetAdditionalElements()) },
                });
            }

            return new Dictionary<string, object>
            {
                { "total", warnings.Count },
                { "offset", offset },
                { "limit", limit },
                { "warnings", rows },
            };
        }

        /// <summary>
        /// Body: {}. The view templates in the document, with the parameters each one controls.
        ///
        /// Read-only: creating a template is not something this bridge does, and applying one is a
        /// view edit that belongs with the view endpoints. What this is for is knowing, before you
        /// set a scale or a display style on a view, whether a template is going to overrule you -
        /// a controlled parameter is one the view cannot hold its own value for.
        ///
        /// "controlledParameters" is GetTemplateParameterIds() minus
        /// GetNonControlledTemplateParameterIds(), each with the label Revit shows in the template
        /// dialog. A built-in parameter has a negative id and its label comes from LabelUtils; a
        /// project or shared parameter has a positive one and is named by its ParameterElement. A
        /// label Revit refuses to produce comes back null rather than dropping the id.
        /// </summary>
        internal static object ViewTemplates(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            List<View> templates = new List<View>();
            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(View)))
            {
                View view = element as View;
                if (view != null && view.IsTemplate)
                {
                    templates.Add(view);
                }
            }

            templates.Sort(delegate (View left, View right)
            {
                int byType = string.Compare(
                    left.ViewType.ToString(),
                    right.ViewType.ToString(),
                    StringComparison.OrdinalIgnoreCase);

                if (byType != 0)
                {
                    return byType;
                }

                return string.Compare(
                    RevitFacts.SafeName(left),
                    RevitFacts.SafeName(right),
                    StringComparison.OrdinalIgnoreCase);
            });

            List<object> rows = new List<object>();

            foreach (View template in templates)
            {
                List<object> controlled = ControlledParameters(document, template);

                rows.Add(new Dictionary<string, object>
                {
                    { "id", template.Id.Value },
                    { "name", RevitFacts.SafeName(template) },
                    { "viewType", template.ViewType.ToString() },
                    { "controlledParameterCount", controlled.Count },
                    { "controlledParameters", controlled },
                });
            }

            return rows;
        }

        // --- inspect: one element ------------------------------------------------------------

        private static Dictionary<string, object> Describe(
            Document document,
            Element element,
            bool includeParameters,
            bool? includeGeometry)
        {
            Dictionary<string, object> row = new Dictionary<string, object>
            {
                { "id", element.Id.Value },
                { "name", RevitFacts.SafeName(element) },
                { "uniqueId", element.UniqueId },

                // The Revit API type, which is what decides which of the blocks below exist.
                { "class", element.GetType().Name },
                { "category", RevitFacts.CategoryName(element) },
                { "typeId", TypeId(element) },
                { "typeName", RevitFacts.TypeName(document, element) },
                { "level", RevitFacts.LevelName(document, element) },
                { "levelId", LevelId(element) },
                { "levelElevation", LevelElevation(document, element) },
                { "hostId", HostId(element) },
                { "pinned", element.Pinned },
                { "groupId", GroupId(element) },
                { "location", Location(element) },
                { "modelBoundingBox", BoundingBox(element) },
                { "materialIds", MaterialIds(element) },
            };

            Floor floor = element as Floor;
            if (floor != null)
            {
                row["floor"] = DescribeFloor(document, floor, includeGeometry);
            }

            Toposolid toposolid = element as Toposolid;
            if (toposolid != null)
            {
                row["slabShape"] = DescribeSlabShape(toposolid, includeGeometry);
            }

            TopographySurface topography = element as TopographySurface;
            if (topography != null)
            {
                row["topography"] = DescribeTopography(topography, includeGeometry);
            }

            FamilyInstance instance = element as FamilyInstance;
            if (instance != null)
            {
                row["familyInstance"] = DescribeFamilyInstance(document, instance);
            }

            if (includeParameters)
            {
                List<object> duplicates;
                row["parameters"] = InstanceParameters(element, out duplicates);
                row["duplicateParameters"] = duplicates;
            }

            return row;
        }

        /// <summary>
        /// The floor's level, its height offset from it, and the closed loops of its top face.
        ///
        /// The top face rather than the sketch: a floor's sketch is a Sketch element whose curves
        /// are only reachable by id, while HostObjectUtils.GetTopFaces gives the boundary as Revit
        /// currently holds it - after every join, opening and shape edit - which is what "is the
        /// paving where I think it is" actually asks about. Straight segments report their two
        /// endpoints; anything curved reports those and a tessellation.
        /// </summary>
        private static Dictionary<string, object> DescribeFloor(
            Document document,
            Floor floor,
            bool? includeGeometry)
        {
            Dictionary<string, object> result = new Dictionary<string, object>
            {
                { "level", RevitFacts.LevelName(document, floor) },
                { "levelId", LevelId(floor) },
                { "levelElevation", LevelElevation(document, floor) },
                { "heightOffsetFromLevel", OffsetFromLevel(floor) },
                { "boundarySource", "topFace" },
            };

            List<object> loops = new List<object>();
            int segments = 0;
            bool truncated = false;

            try
            {
                foreach (Reference reference in HostObjectUtils.GetTopFaces(floor))
                {
                    Face face = floor.GetGeometryObjectFromReference(reference) as Face;
                    if (face == null)
                    {
                        continue;
                    }

                    foreach (CurveLoop loop in face.GetEdgesAsCurveLoops())
                    {
                        List<object> loopSegments = new List<object>();

                        foreach (Curve curve in loop)
                        {
                            if (segments >= MaxGeometryPoints)
                            {
                                truncated = true;
                                break;
                            }

                            loopSegments.Add(Segment(curve));
                            segments++;
                        }

                        loops.Add(new Dictionary<string, object>
                        {
                            { "closed", !loop.IsOpen() },
                            { "segmentCount", loopSegments.Count },
                            { "segments", loopSegments },
                        });
                    }
                }

                result["loops"] = loops;
                result["truncated"] = truncated;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                // A floor Revit will not hand a top face for still reports its level and offset;
                // the reason goes in the row rather than failing the whole inspection.
                result["loops"] = null;
                result["boundaryError"] = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// A toposolid's shape vertices with their absolute positions - the grades themselves.
        ///
        /// SlabShapeVertex.Position is an absolute model point, so the Z of each vertex is the
        /// elevation that surface actually stands at. That is the one reading that says whether
        /// paving, a pool or a path is buried in the ground or floating over it.
        /// </summary>
        private static Dictionary<string, object> DescribeSlabShape(
            Toposolid toposolid,
            bool? includeGeometry)
        {
            List<XYZ> positions = new List<XYZ>();
            List<string> types = new List<string>();

            SlabShapeEditor editor = toposolid.GetSlabShapeEditor();
            if (editor != null)
            {
                SlabShapeVertexArray vertices = editor.SlabShapeVertices;

                for (int index = 0; index < vertices.Size; index++)
                {
                    SlabShapeVertex vertex = vertices.get_Item(index);
                    positions.Add(vertex.Position);
                    types.Add(vertex.VertexType.ToString());
                }
            }

            Dictionary<string, object> result = Bounded("vertex", positions.Count, includeGeometry);

            AddZRange(result, positions);

            if ((bool)result["included"])
            {
                List<object> rows = new List<object>();

                for (int index = 0; index < positions.Count && rows.Count < MaxGeometryPoints; index++)
                {
                    Dictionary<string, object> vertex = Point(positions[index]);
                    vertex["type"] = types[index];
                    rows.Add(vertex);
                }

                result["vertices"] = rows;
                result["truncated"] = positions.Count > rows.Count;
            }
            else
            {
                result["vertices"] = null;
                result["truncated"] = false;
            }

            return result;
        }

        /// <summary>
        /// The same reading for the pre-2024 element: TopographySurface keeps its grades as plain
        /// points rather than slab-shape vertices, and GetPoints returns them in absolute model
        /// coordinates too.
        /// </summary>
        private static Dictionary<string, object> DescribeTopography(
            TopographySurface topography,
            bool? includeGeometry)
        {
            List<XYZ> positions = new List<XYZ>(topography.GetPoints());

            Dictionary<string, object> result = Bounded("point", positions.Count, includeGeometry);

            AddZRange(result, positions);

            if ((bool)result["included"])
            {
                List<object> rows = new List<object>();

                for (int index = 0; index < positions.Count && rows.Count < MaxGeometryPoints; index++)
                {
                    rows.Add(Point(positions[index]));
                }

                result["points"] = rows;
                result["truncated"] = positions.Count > rows.Count;
            }
            else
            {
                result["points"] = null;
                result["truncated"] = false;
            }

            return result;
        }

        private static Dictionary<string, object> DescribeFamilyInstance(
            Document document,
            FamilyInstance instance)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();

            FamilySymbol symbol = instance.Symbol;
            if (symbol == null)
            {
                result["symbolId"] = null;
                result["symbolName"] = null;
                result["familyName"] = null;
            }
            else
            {
                result["symbolId"] = symbol.Id.Value;
                result["symbolName"] = RevitFacts.SafeName(symbol);
                result["familyName"] = symbol.FamilyName;
            }

            result["facing"] = Vector(instance.FacingOrientation);
            result["hand"] = Vector(instance.HandOrientation);
            result["facingFlipped"] = instance.FacingFlipped;
            result["handFlipped"] = instance.HandFlipped;

            // The elevation the instance is actually at, read off its location rather than off the
            // level it is hosted on: a family placed at an absolute z and a family Revit decided
            // differently about look identical until this is compared with what was asked for.
            LocationPoint point = instance.Location as LocationPoint;
            result["actualZ"] = point == null ? (object)null : point.Point.Z;

            result["hostId"] = instance.Host == null ? (object)null : instance.Host.Id.Value;

            return result;
        }

        /// <summary>
        /// Every instance parameter, keyed by the name Revit shows - plus, for each one, the
        /// parameter's own id and built-in name.
        ///
        /// **A display name is not unique.** A family instance carries two parameters called
        /// "Level": FAMILY_LEVEL_PARAM, which is read-only, and SCHEDULE_LEVEL_PARAM, which is not.
        /// A dictionary keyed by name can only hold one of them, and whichever it holds, the other
        /// one is the one Element.LookupParameter hands a writer - which is how a caller is told
        /// "Level" is writable and then told "Level" is read-only by the very next call.
        ///
        /// So a repeated name is reported rather than collapsed. The dictionary still answers by
        /// name - nothing that read it before reads anything different - but every descriptor now
        /// carries "id" (Revit's parameter id: negative for a built-in, positive for a shared or
        /// project parameter) and "builtIn" (the BuiltInParameter name, null when there is none),
        /// a repeated one is flagged "ambiguous": true, and the element's "duplicateParameters"
        /// lists every occurrence of every repeated name, in enumeration order. Write to one of
        /// those with
        /// parameters/set's "parameterId" - the name alone cannot say which.
        /// </summary>
        private static Dictionary<string, object> InstanceParameters(
            Element element,
            out List<object> duplicates)
        {
            Dictionary<string, object> parameters = new Dictionary<string, object>(StringComparer.Ordinal);
            Dictionary<string, List<Dictionary<string, object>>> byName =
                new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.Ordinal);
            List<string> order = new List<string>();

            foreach (Parameter parameter in element.Parameters)
            {
                if (parameter == null || parameter.Definition == null)
                {
                    continue;
                }

                string name = parameter.Definition.Name;
                Dictionary<string, object> described = RevitFacts.DescribeParameter(parameter, false);
                described["id"] = parameter.Id.Value;
                described["builtIn"] = BuiltInName(parameter);

                if (!byName.ContainsKey(name))
                {
                    byName[name] = new List<Dictionary<string, object>>();
                    order.Add(name);
                }

                byName[name].Add(described);
                parameters[name] = described;
            }

            duplicates = new List<object>();

            foreach (string name in order)
            {
                List<Dictionary<string, object>> found = byName[name];
                if (found.Count < 2)
                {
                    continue;
                }

                foreach (Dictionary<string, object> described in found)
                {
                    described["ambiguous"] = true;

                    Dictionary<string, object> row = new Dictionary<string, object>(described, StringComparer.Ordinal);
                    row["name"] = name;
                    duplicates.Add(row);
                }
            }

            return parameters;
        }

        /// <summary>
        /// The BuiltInParameter name behind a parameter - FAMILY_LEVEL_PARAM rather than "Level" -
        /// or null for a shared or project parameter, which has no built-in identity at all.
        /// </summary>
        private static string BuiltInName(Parameter parameter)
        {
            InternalDefinition definition = parameter.Definition as InternalDefinition;
            if (definition == null || definition.BuiltInParameter == BuiltInParameter.INVALID)
            {
                return null;
            }

            return definition.BuiltInParameter.ToString();
        }

        // --- shared shapes -------------------------------------------------------------------

        /// <summary>
        /// The count-plus-inclusion header shared by the two grade readings. Unset "includeGeometry"
        /// means "list them if the list is small enough to be worth reading".
        /// </summary>
        private static Dictionary<string, object> Bounded(string noun, int count, bool? includeGeometry)
        {
            bool included = includeGeometry.HasValue
                ? includeGeometry.Value
                : count <= MaxGeometryPoints;

            return new Dictionary<string, object>
            {
                { noun + "Count", count },
                { "included", included },
                { "limit", MaxGeometryPoints },
            };
        }

        /// <summary>The elevation range of a set of points, which is a grade summary in two numbers.</summary>
        private static void AddZRange(Dictionary<string, object> result, List<XYZ> positions)
        {
            if (positions.Count == 0)
            {
                result["minZ"] = null;
                result["maxZ"] = null;
                return;
            }

            double min = positions[0].Z;
            double max = positions[0].Z;

            foreach (XYZ position in positions)
            {
                min = Math.Min(min, position.Z);
                max = Math.Max(max, position.Z);
            }

            result["minZ"] = min;
            result["maxZ"] = max;
        }

        /// <summary>A curve as its two endpoints, plus a tessellation when it is not a straight line.</summary>
        private static Dictionary<string, object> Segment(Curve curve)
        {
            Dictionary<string, object> segment = new Dictionary<string, object>
            {
                { "type", curve.GetType().Name },
                { "start", Point(curve.GetEndPoint(0)) },
                { "end", Point(curve.GetEndPoint(1)) },
            };

            if (!(curve is Line))
            {
                List<object> tessellation = new List<object>();
                foreach (XYZ point in curve.Tessellate())
                {
                    tessellation.Add(Point(point));
                }

                segment["tessellation"] = tessellation;
            }

            return segment;
        }

        /// <summary>Where the element is: a point, or a curve's two endpoints. Null when it has neither.</summary>
        private static Dictionary<string, object> Location(Element element)
        {
            LocationPoint point = element.Location as LocationPoint;
            if (point != null)
            {
                return new Dictionary<string, object>
                {
                    { "kind", "point" },
                    { "point", Point(point.Point) },
                };
            }

            LocationCurve curve = element.Location as LocationCurve;
            if (curve != null && curve.Curve != null)
            {
                return new Dictionary<string, object>
                {
                    { "kind", "curve" },
                    { "curveType", curve.Curve.GetType().Name },
                    { "start", Point(curve.Curve.GetEndPoint(0)) },
                    { "end", Point(curve.Curve.GetEndPoint(1)) },
                };
            }

            return null;
        }

        /// <summary>
        /// The element's bounding box in model coordinates - get_BoundingBox(null), which is the
        /// view-independent one. A view-specific box would be a different frame and a different
        /// answer, so no view is ever passed here.
        /// </summary>
        private static Dictionary<string, object> BoundingBox(Element element)
        {
            BoundingBoxXYZ box;

            try
            {
                box = element.get_BoundingBox(null);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return null;
            }

            if (box == null)
            {
                return null;
            }

            return new Dictionary<string, object>
            {
                { "min", Point(box.Min) },
                { "max", Point(box.Max) },
                { "center", Point((box.Min + box.Max) / 2.0) },
            };
        }

        /// <summary>
        /// The one point a move is measured on: the location point, a curve's start, or failing
        /// both the bounding box minimum. Whatever it is, it is read the same way before and after,
        /// so the difference between the two readings is the displacement Revit applied. Null when
        /// the element offers none of the three.
        /// </summary>
        private static XYZ Anchor(Element element)
        {
            LocationPoint point = element.Location as LocationPoint;
            if (point != null)
            {
                return point.Point;
            }

            LocationCurve curve = element.Location as LocationCurve;
            if (curve != null && curve.Curve != null)
            {
                return curve.Curve.GetEndPoint(0);
            }

            BoundingBoxXYZ box;

            try
            {
                box = element.get_BoundingBox(null);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return null;
            }

            return box == null ? null : box.Min;
        }

        /// <summary>
        /// Did the elements actually move? Called inside the transaction, after the regeneration and
        /// before the commit, which is the only window where the answer "no" can still undo itself.
        ///
        /// ElementTransformUtils.MoveElements reports success for a move Revit then does not apply.
        /// A family instance whose elevation is owned by its level is the everyday case: ask for
        /// half a metre in Z, the call returns, no warning is raised, and the instance has not moved
        /// at all. Comparing the displacement against the one that was asked for is the only way to
        /// tell that apart from a real move, and a caller that is told "moved" for a no-op will
        /// build the rest of its work on a position the model does not have.
        ///
        /// The tolerance is the document's own ShortCurveTolerance: anything under it is a distance
        /// Revit does not consider a distance.
        ///
        /// Returns the ids that could not be measured - no location and no bounding box. Those are
        /// reported on the response rather than counted as moved, because an unverifiable element is
        /// not the same claim as a verified one.
        /// </summary>
        private static List<object> VerifyMoved(
            Document document,
            List<ElementId> elementIds,
            List<XYZ> before,
            XYZ translation)
        {
            double tolerance = document.Application.ShortCurveTolerance;
            List<string> wrong = new List<string>();
            List<object> unverified = new List<object>();

            for (int index = 0; index < elementIds.Count; index++)
            {
                Element element = document.GetElement(elementIds[index]);
                XYZ after = element == null ? null : Anchor(element);

                if (before[index] == null || after == null)
                {
                    unverified.Add(elementIds[index].Value);
                    continue;
                }

                XYZ applied = after - before[index];
                if (applied.DistanceTo(translation) <= tolerance)
                {
                    continue;
                }

                wrong.Add(elementIds[index].Value + " asked for " + Describe(translation)
                    + " and got " + Describe(applied));
            }

            if (wrong.Count > 0)
            {
                throw new BridgeException(
                    409,
                    "MOVE_NOT_APPLIED",
                    "Revit accepted the move and did not apply it: " + string.Join("; ", wrong)
                        + " (feet, tolerance " + tolerance.ToString("0.######", CultureInfo.InvariantCulture)
                        + "). The whole request was rolled back - the model is exactly as it was. An "
                        + "element whose position is driven by something else does not move this way: "
                        + "a family instance sitting on a level takes its elevation from that level, "
                        + "and a sub-component takes its position from the component hosting it. "
                        + "Change what drives it - the level association or the instance's elevation "
                        + "parameter - or move the driver.");
            }

            return unverified;
        }

        /// <summary>A vector as text, for the one place a message has to name two of them.</summary>
        private static string Describe(XYZ vector)
        {
            return "("
                + vector.X.ToString("0.######", CultureInfo.InvariantCulture) + ", "
                + vector.Y.ToString("0.######", CultureInfo.InvariantCulture) + ", "
                + vector.Z.ToString("0.######", CultureInfo.InvariantCulture) + ")";
        }

        /// <summary>Location plus bounding box: what "before" and "after" both are.</summary>
        private static Dictionary<string, object> State(Element element)
        {
            return new Dictionary<string, object>
            {
                { "location", Location(element) },
                { "boundingBox", BoundingBox(element) },
            };
        }

        /// <summary>
        /// A captured state moved by the translation, which is what a dry run's "after" is. Nothing
        /// in the model is read or touched: it is the caller's own arithmetic, done here so both
        /// paths answer in the same shape.
        /// </summary>
        private static Dictionary<string, object> Shift(Dictionary<string, object> state, XYZ translation)
        {
            Dictionary<string, object> shifted = new Dictionary<string, object>();

            Dictionary<string, object> location = state["location"] as Dictionary<string, object>;
            if (location == null)
            {
                shifted["location"] = null;
            }
            else
            {
                Dictionary<string, object> moved = new Dictionary<string, object>(location);

                foreach (string key in new string[] { "point", "start", "end" })
                {
                    Dictionary<string, object> value = location.ContainsKey(key)
                        ? location[key] as Dictionary<string, object>
                        : null;

                    if (value != null)
                    {
                        moved[key] = Point(ReadPoint(value) + translation);
                    }
                }

                shifted["location"] = moved;
            }

            Dictionary<string, object> box = state["boundingBox"] as Dictionary<string, object>;
            if (box == null)
            {
                shifted["boundingBox"] = null;
            }
            else
            {
                shifted["boundingBox"] = new Dictionary<string, object>
                {
                    { "min", Point(ReadPoint(box["min"] as Dictionary<string, object>) + translation) },
                    { "max", Point(ReadPoint(box["max"] as Dictionary<string, object>) + translation) },
                    { "center", Point(ReadPoint(box["center"] as Dictionary<string, object>) + translation) },
                };
            }

            return shifted;
        }

        private static Dictionary<string, object> MoveResult(
            List<Dictionary<string, object>> rows,
            XYZ translation,
            bool dryRun,
            string afterSource)
        {
            List<object> elements = new List<object>();
            foreach (Dictionary<string, object> row in rows)
            {
                elements.Add(row);
            }

            return new Dictionary<string, object>
            {
                { "dryRun", dryRun },
                { "moved", !dryRun },
                { "count", rows.Count },
                { "translation", Vector(translation) },
                { "coordinateSystem", CoordinateSystem },
                { "afterSource", afterSource },
                { "elements", elements },
            };
        }

        private static Dictionary<string, object> ExcavationResult(
            Toposolid toposolid,
            List<Dictionary<string, object>> rows,
            bool dryRun,
            int excavated,
            object volumeBefore,
            object volumeAfter)
        {
            List<object> elements = new List<object>();
            foreach (Dictionary<string, object> row in rows)
            {
                elements.Add(row);
            }

            object removed = null;
            if (volumeBefore is double && volumeAfter is double)
            {
                removed = (double)volumeBefore - (double)volumeAfter;
            }

            return new Dictionary<string, object>
            {
                { "dryRun", dryRun },
                { "excavated", excavated > 0 },
                { "excavatedCount", excavated },
                { "toposolidId", toposolid.Id.Value },
                { "toposolidName", RevitFacts.SafeName(toposolid) },
                { "count", rows.Count },
                { "units", "Revit internal units: decimal feet, volumes in cubic feet" },
                { "volumeBefore", volumeBefore },
                { "volumeAfter", volumeAfter },
                { "volumeRemoved", removed },
                { "elements", elements },
            };
        }

        /// <summary>
        /// The toposolid to cut: "toposolidId" when it is given, otherwise the only one in the
        /// document. Same rule as toposolid/flatten - a document with several of them has to be told
        /// which, because excavating the wrong surface is not something a caller notices.
        /// </summary>
        private static Toposolid RequireToposolid(Document document, JsonElement body)
        {
            JsonElement value;
            if (JsonBody.TryGet(body, "toposolidId", out value))
            {
                long id = JsonBody.AsLong(value, "toposolidId");

                Toposolid named = document.GetElement(new ElementId(id)) as Toposolid;
                if (named == null)
                {
                    throw BridgeException.BadRequest(
                        "Element " + id + " is not a toposolid in this document.");
                }

                return named;
            }

            List<Toposolid> toposolids = new List<Toposolid>();
            List<string> available = new List<string>();

            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(Toposolid)))
            {
                Toposolid toposolid = element as Toposolid;
                if (toposolid == null)
                {
                    continue;
                }

                toposolids.Add(toposolid);
                available.Add(toposolid.Id.Value + " (" + RevitFacts.TypeName(document, toposolid) + ")");
            }

            if (toposolids.Count == 0)
            {
                throw new BridgeException(
                    409,
                    "NO_TOPOSOLID",
                    "This document has no toposolid to excavate. A legacy TopographySurface cannot "
                        + "be excavated - Toposolid.ExcavateBy is a Revit 2025 toposolid feature.");
            }

            if (toposolids.Count > 1)
            {
                throw BridgeException.BadRequest(
                    "This document has " + toposolids.Count + " toposolids, so \"toposolidId\" is "
                        + "required. They are: " + string.Join(", ", available) + ".");
            }

            return toposolids[0];
        }

        /// <summary>
        /// What already cuts this toposolid, by element id, with the volume Revit attributes to
        /// each. GetIntersectingElementData reports plain cuts as well as excavations, so the type
        /// is checked: an element that intersects the surface some other way is not an excavation
        /// and must not be counted as one.
        /// </summary>
        private static Dictionary<long, double> Excavations(Toposolid toposolid)
        {
            Dictionary<long, double> excavations = new Dictionary<long, double>();

            IList<IntersectingElementData> data = toposolid.GetIntersectingElementData();
            if (data == null)
            {
                return excavations;
            }

            foreach (IntersectingElementData entry in data)
            {
                if (entry == null || entry.IntersectionType != IntersectionType.Excavate)
                {
                    continue;
                }

                ElementId id = entry.IntersectingElementId;

                // The pair is (this toposolid, the element cutting it); take whichever half is not
                // the surface itself rather than trusting which way round Revit filled it in.
                if (id == null || id.Value == toposolid.Id.Value)
                {
                    id = entry.IntersectedElementId;
                }

                if (id == null || id.Value == toposolid.Id.Value)
                {
                    continue;
                }

                excavations[id.Value] = entry.IntersectionVolume;
            }

            return excavations;
        }

        /// <summary>
        /// An element's own computed volume in cubic feet, or null when it does not carry the
        /// parameter. This is what an excavation is measured by: the difference between two readings
        /// of it is the material the hole took out.
        /// </summary>
        private static object Volume(Element element)
        {
            if (element == null)
            {
                return null;
            }

            Parameter volume = element.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED);
            if (volume == null || !volume.HasValue || volume.StorageType != StorageType.Double)
            {
                return null;
            }

            return volume.AsDouble();
        }

        /// <summary>
        /// The move vector, with every component checked for being a real number. "Infinity" arrives
        /// as a JSON string often enough - see JsonBody on quoted numbers - and a NaN translation is
        /// an ArgumentException out of Revit that says nothing about which field was wrong.
        /// </summary>
        private static XYZ RequireTranslation(JsonElement body)
        {
            JsonElement value;
            if (!JsonBody.TryGet(body, "translation", out value))
            {
                throw BridgeException.BadRequest(
                    "\"translation\" is required and must be {x, y, z?} in feet.");
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
                throw BridgeException.BadRequest(
                    "\"translation\" must be an object {x, y, z?}, but was " + value.ValueKind + ".");
            }

            XYZ point = RevitFacts.ReadPoint(value, "translation");

            RequireFinite(point.X, "translation.x");
            RequireFinite(point.Y, "translation.y");
            RequireFinite(point.Z, "translation.z");

            return point;
        }

        private static void RequireFinite(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw BridgeException.BadRequest(
                    "\"" + name + "\" must be a finite number of feet, but was " + value + ".");
            }
        }

        // --- small readers -------------------------------------------------------------------

        private static List<object> ControlledParameters(Document document, View template)
        {
            HashSet<long> notControlled = new HashSet<long>();
            foreach (ElementId id in template.GetNonControlledTemplateParameterIds())
            {
                notControlled.Add(id.Value);
            }

            List<object> controlled = new List<object>();

            foreach (ElementId id in template.GetTemplateParameterIds())
            {
                if (notControlled.Contains(id.Value))
                {
                    continue;
                }

                controlled.Add(new Dictionary<string, object>
                {
                    { "id", id.Value },
                    { "label", ParameterLabel(document, id) },
                });
            }

            return controlled;
        }

        /// <summary>
        /// The label Revit shows for a parameter id: LabelUtils for a built-in one (negative id),
        /// the ParameterElement's own name for a project or shared one.
        /// </summary>
        private static string ParameterLabel(Document document, ElementId id)
        {
            if (id.Value < 0)
            {
                try
                {
                    return LabelUtils.GetLabelFor((BuiltInParameter)(int)id.Value);
                }
                catch (Exception)
                {
                    // Not every built-in parameter has a label Revit is willing to produce.
                    return null;
                }
            }

            ParameterElement parameter = document.GetElement(id) as ParameterElement;
            if (parameter == null)
            {
                return null;
            }

            return RevitFacts.SafeName(parameter);
        }

        private static string FailureDefinitionGuid(FailureMessage warning)
        {
            FailureDefinitionId definitionId = warning.GetFailureDefinitionId();
            if (definitionId == null)
            {
                return null;
            }

            return definitionId.Guid.ToString();
        }

        private static List<object> Ids(ICollection<ElementId> ids)
        {
            List<object> values = new List<object>();

            if (ids == null)
            {
                return values;
            }

            foreach (ElementId id in ids)
            {
                values.Add(id.Value);
            }

            return values;
        }

        private static object TypeId(Element element)
        {
            ElementId typeId = element.GetTypeId();
            if (typeId == null || typeId == ElementId.InvalidElementId)
            {
                return null;
            }

            return typeId.Value;
        }

        private static object LevelId(Element element)
        {
            ElementId levelId = element.LevelId;
            if (levelId == null || levelId == ElementId.InvalidElementId)
            {
                return null;
            }

            return levelId.Value;
        }

        private static object LevelElevation(Document document, Element element)
        {
            ElementId levelId = element.LevelId;
            if (levelId == null || levelId == ElementId.InvalidElementId)
            {
                return null;
            }

            Level level = document.GetElement(levelId) as Level;
            if (level == null)
            {
                return null;
            }

            // ProjectElevation, not Elevation. Elevation is measured from the level's own elevation
            // base, which "Elevation Base" can set to the shared (survey) datum - and then it is a
            // different number from the Z of everything else reported here. ProjectElevation is
            // always the internal origin, which is the frame "coordinateSystem" promises.
            return level.ProjectElevation;
        }

        private static object OffsetFromLevel(Floor floor)
        {
            // FLOOR_HEIGHTABOVELEVEL_PARAM is what Revit calls "Height Offset From Level" on a
            // floor instance - see ModelEndpoints.CreateFloor, which writes the same parameter.
            Parameter offset = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
            if (offset == null || offset.StorageType != StorageType.Double)
            {
                return null;
            }

            return offset.AsDouble();
        }

        private static object HostId(Element element)
        {
            FamilyInstance instance = element as FamilyInstance;
            if (instance != null && instance.Host != null)
            {
                return instance.Host.Id.Value;
            }

            return null;
        }

        private static object GroupId(Element element)
        {
            ElementId groupId = element.GroupId;
            if (groupId == null || groupId == ElementId.InvalidElementId)
            {
                return null;
            }

            return groupId.Value;
        }

        private static List<object> MaterialIds(Element element)
        {
            List<object> ids = new List<object>();

            try
            {
                foreach (ElementId id in element.GetMaterialIds(false))
                {
                    ids.Add(id.Value);
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Not every element will answer for its materials; an empty list is the honest
                // reading, and the type's material is still reachable through typeId.
            }

            return ids;
        }

        private static Dictionary<string, object> Point(XYZ value)
        {
            return new Dictionary<string, object>
            {
                { "x", value.X },
                { "y", value.Y },
                { "z", value.Z },
            };
        }

        private static Dictionary<string, object> Vector(XYZ value)
        {
            return Point(value);
        }

        private static XYZ ReadPoint(Dictionary<string, object> point)
        {
            return new XYZ(
                Convert.ToDouble(point["x"]),
                Convert.ToDouble(point["y"]),
                Convert.ToDouble(point["z"]));
        }
    }
}
