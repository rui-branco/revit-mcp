using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

namespace RevitMcpBridge.Endpoints
{
    /// <summary>
    /// Site, hardscape and family placement: the endpoints that put real modelled elements in the
    /// document rather than drawing-side ones. Same rules as WriteEndpoints - one request is one
    /// TransactionGroup and therefore one Ctrl+Z, and every coordinate is Revit internal units
    /// (decimal feet), unconverted.
    /// </summary>
    internal static class ModelEndpoints
    {
        /// <summary>How close to the target a toposolid vertex counts as flattened, in feet.</summary>
        private const double FlattenTolerance = 0.001;

        /// <summary>What toposolid/flatten probes the vertex offset with, in feet. See FlattenRegion.</summary>
        private const double ProbeOffset = 1.0;

        /// <summary>How far from a point openings/place will look for a wall to host in, in feet.</summary>
        private const double HostWallTolerance = 3.0;

        /// <summary>The sill a Windows-category symbol gets when the caller names none, in feet.</summary>
        private const double DefaultWindowSill = 3.0;

        /// <summary>
        /// Body: {points: [{x, y, z}], typeName?, level?}.
        ///
        /// Prefers Toposolid (Revit 2024+, which is what the 2025 reference assemblies this
        /// assembly compiles against already expose) and falls back to TopographySurface only when
        /// the document has no ToposolidType loaded at all. The response says which one was used in
        /// "type", because the two behave differently afterwards and the caller has to know.
        /// </summary>
        internal static object CreateToposolid(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            JsonElement points = JsonBody.RequireArray(body, "points");
            string typeName = JsonBody.OptionalString(body, "typeName");
            string levelName = JsonBody.OptionalString(body, "level");

            // Toposolid.Create builds the top face from the points as given, so z is meaningful
            // here in a way it is not for a plan-space boundary - never flattened.
            List<XYZ> vertices = RevitFacts.ReadPoints(points, "points", false, 0.0);

            if (vertices.Count < 3)
            {
                throw BridgeException.BadRequest(
                    "\"points\" needs at least 3 points to build a surface; got " + vertices.Count + ".");
            }

            Level level = ResolveLevelOrLowest(document, levelName);

            ElementId typeId = ResolveToposolidTypeId(document, typeName);

            return RevitWrite.InGroup(document, "MCP: create toposolid", delegate
            {
                Dictionary<string, object> created = new Dictionary<string, object>();

                RevitWrite.InTransaction(document, "Create toposolid", delegate
                {
                    if (typeId == null)
                    {
                        // No ToposolidType in this document: the Revit 2023-era element is the only
                        // thing that can still carry these points. Deprecated in 2024 and it says
                        // so - that is exactly why this branch is the fallback and not the default.
#pragma warning disable CS0618
                        TopographySurface surface = TopographySurface.Create(document, vertices);
#pragma warning restore CS0618

                        created["id"] = surface.Id.Value;
                        created["type"] = "TopographySurface";
                        created["typeName"] = null;
                        created["level"] = null;
                        created["points"] = vertices.Count;
                        return;
                    }

                    Toposolid toposolid = Toposolid.Create(document, vertices, typeId, level.Id);

                    created["id"] = toposolid.Id.Value;
                    created["type"] = "Toposolid";
                    created["typeName"] = RevitFacts.TypeName(document, toposolid);
                    created["level"] = level.Name;
                    created["points"] = vertices.Count;
                });

                return created;
            });
        }

        /// <summary>
        /// Body: {points: [{x, y}], elevation, toposolidId?}.
        ///
        /// Flattens the region of a toposolid bounded by "points" to "elevation", so paving can sit
        /// on graded terrain without the two interpenetrating. The ring's points are added to the
        /// toposolid's shape at the target elevation, the ring itself is creased so the flat region
        /// ends at its boundary instead of sloping on into the terrain, and every existing shape
        /// vertex inside the ring is moved to the same elevation.
        ///
        /// "toposolidId" is optional: a document with exactly one toposolid does not need it. It is
        /// required as soon as there are two, because flattening the wrong one is not something to
        /// guess at.
        ///
        /// The response reports "residual" - the largest distance any vertex in the region is still
        /// off the target elevation - so a caller can assert the region really is flat rather than
        /// trust that it is. See FlattenRegion for why that number is measured and not assumed.
        /// </summary>
        internal static object FlattenToposolid(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            JsonElement points = JsonBody.RequireArray(body, "points");
            double elevation = JsonBody.RequireDouble(body, "elevation");

            Toposolid toposolid = ResolveToposolid(document, body);

            // Flattened on purpose, unlike toposolid/create: the z of a point in a region to be
            // levelled is the answer, not a reading.
            List<XYZ> ring = RevitFacts.ReadPoints(points, "points", true, elevation);

            // A caller that repeated the first point at the end means the same ring as one that did
            // not, and AddPoints refuses two points at the same place in plan.
            if (ring.Count > 1
                && ring[0].DistanceTo(ring[ring.Count - 1]) < document.Application.ShortCurveTolerance)
            {
                ring.RemoveAt(ring.Count - 1);
            }

            if (ring.Count < 3)
            {
                throw BridgeException.BadRequest(
                    "\"points\" needs at least 3 distinct points to bound a region; got " + ring.Count + ".");
            }

            return RevitWrite.InGroup(document, "MCP: flatten toposolid", delegate
            {
                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    { "id", toposolid.Id.Value },
                    { "elevation", elevation },
                };

                AddRing(document, toposolid, ring, result);
                FlattenRegion(document, toposolid, ring, elevation, result);

                return result;
            });
        }

        /// <summary>
        /// Body: {level, typeName?, boundary: [{x, y}], structural?, offset?}.
        ///
        /// Paving, pool decks and terraces. The boundary is a ring in plan; it is closed
        /// automatically when the last point is not the first, and every point is taken at the
        /// level's elevation, so the floor sits on the level rather than at an accidental offset.
        ///
        /// "offset" is the floor's height offset from that level in feet, written to
        /// FLOOR_HEIGHTOFFSET_PARAM ("Height Offset From Level") after creation and defaulting to
        /// 0, which is what the endpoint did before it existed. It is how paving stops fighting a
        /// graded toposolid: lift the slab clear of the surface instead of letting the two
        /// interpenetrate. The response reports the value read back off the parameter.
        /// </summary>
        internal static object CreateFloor(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            string levelName = JsonBody.RequireString(body, "level");
            string typeName = JsonBody.OptionalString(body, "typeName");
            JsonElement boundary = JsonBody.RequireArray(body, "boundary");
            bool structural = JsonBody.OptionalBool(body, "structural", false);
            double offset = JsonBody.OptionalDouble(body, "offset", 0.0);

            Level level = RevitFacts.ResolveLevel(document, levelName);
            if (level == null)
            {
                throw BridgeException.BadRequest(
                    "Unknown level \"" + levelName + "\". Levels in this document: "
                        + string.Join(", ", RevitFacts.LevelNames(document)) + ".");
            }

            ElementId typeId = ResolveFloorTypeId(document, typeName);

            List<XYZ> ring = RevitFacts.ReadPoints(boundary, "boundary", true, level.Elevation);
            CurveLoop loop = RevitFacts.PolygonLoop(document, ring, "boundary");

            return RevitWrite.InGroup(document, "MCP: create floor", delegate
            {
                Dictionary<string, object> created = new Dictionary<string, object>();

                RevitWrite.InTransaction(document, "Create floor", delegate
                {
                    Floor floor = Floor.Create(
                        document,
                        new List<CurveLoop> { loop },
                        typeId,
                        level.Id,
                        structural,
                        null,
                        0.0);

                    // FLOOR_HEIGHTABOVELEVEL_PARAM is what Revit calls "Height Offset From Level"
                    // on a floor instance; there is no FLOOR_HEIGHTOFFSET_PARAM in the API.
                    Parameter heightOffset = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);

                    if (offset != 0.0)
                    {
                        if (heightOffset == null || heightOffset.IsReadOnly)
                        {
                            throw BridgeException.BadRequest(
                                "This floor type has no writable \"Height Offset From Level\" "
                                    + "parameter, so \"offset\" cannot be applied.");
                        }

                        heightOffset.Set(offset);
                    }

                    // Area is computed, so it reads as 0 until the document catches up.
                    document.Regenerate();

                    created["id"] = floor.Id.Value;
                    created["level"] = level.Name;
                    created["typeName"] = RevitFacts.TypeName(document, floor);
                    created["structural"] = structural;

                    // Read back rather than echoed: the caller asked for a height, and what the
                    // floor actually carries is the only answer worth reporting.
                    created["offset"] = heightOffset == null ? offset : heightOffset.AsDouble();
                    created["area"] = FloorArea(floor);
                });

                return created;
            });
        }

        /// <summary>
        /// Body: {name, basedOnTypeName?, thickness?, materialId?, materialName?}.
        ///
        /// A wall's material is a property of its TYPE, not of the wall: it lives in the type's
        /// CompoundStructure, one material per layer. So the only way to give walls a material is
        /// to author a type carrying one and build the walls with it - which is what this endpoint
        /// and walls/create's "typeName" are for.
        /// </summary>
        internal static object CreateWallType(UIApplication app, JsonElement body)
        {
            return CreateHostType(app, body, typeof(WallType), ElementTypeGroup.WallType, "wall");
        }

        /// <summary>
        /// Body: {name, basedOnTypeName?, thickness?, materialId?, materialName?}. The same thing
        /// for floors, and the same reason: paving that is not grey needs a floor type whose
        /// structure carries the material. Pass the new type's name to floors/create as "typeName".
        /// </summary>
        internal static object CreateFloorType(UIApplication app, JsonElement body)
        {
            return CreateHostType(app, body, typeof(FloorType), ElementTypeGroup.FloorType, "floor");
        }

        /// <summary>
        /// Body: {paths: [string]} or {path: string}. Loads .rfa family files into the project.
        ///
        /// This is the endpoint that stops a project being built out of DirectShape primitives.
        /// Autodesk's library is an optional download and lives outside the project - under
        /// C:\ProgramData\Autodesk\RVT &lt;year&gt;\Libraries - so nothing in it exists to
        /// families/symbols or families/place until it has been loaded here first.
        ///
        /// Four things this endpoint is careful about:
        ///
        /// 1. **A family already in the project must not throw.** The three-argument
        ///    Document.LoadFamily(path, IFamilyLoadOptions, out Family) is used precisely so there
        ///    is somewhere to answer Revit's "this family already exists" question - the plain
        ///    LoadFamily(path) has no such hook and raises instead. See BridgeFamilyLoadOptions for
        ///    what is answered and why.
        /// 2. **A library one release behind still loads.** An .rfa saved by an older Revit is
        ///    upgraded on load, silently, and the upgrade can raise warnings the bridge resolves on
        ///    the caller's behalf. Those would otherwise vanish, so whatever BridgeDiagnostics
        ///    recorded during this call comes back in "warnings".
        /// 3. **One bad path cannot abort the batch.** A missing file is FILE_NOT_FOUND on that
        ///    row, an .rfa Revit refuses is LOAD_FAILED on that row, and every other file in the
        ///    request still loads. Each file gets its own transaction for that reason.
        /// 4. **The caller gets symbol ids back immediately.** A family is useless without its
        ///    types, and looking them up is a second round trip, so every row carries them.
        ///
        /// Read "loaded" and "alreadyLoaded" together. "alreadyLoaded" is whether the project had
        /// the family before the call; "loaded" is whether Revit actually wrote it in. Both true
        /// means it was reloaded over an older copy. "loaded" false with "alreadyLoaded" true is
        /// NOT a failure: Revit found the project's copy identical to the file and did nothing,
        /// and the familyName and symbols in the row are the ones already there.
        /// </summary>
        internal static object LoadFamilies(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            List<string> paths;

            JsonElement value;
            if (JsonBody.TryGet(body, "paths", out value))
            {
                paths = JsonBody.OptionalStringList(body, "paths");
            }
            else
            {
                paths = new List<string>();
                paths.Add(JsonBody.RequireString(body, "path"));
            }

            if (paths.Count == 0)
            {
                throw BridgeException.BadRequest(
                    "\"paths\" must contain at least one .rfa file path, or pass a single \"path\".");
            }

            int dialogsBefore = DiagnosticEntries("dialogs").Count;
            int failuresBefore = DiagnosticEntries("failures").Count;

            return RevitWrite.InGroup(document, "MCP: load families", delegate
            {
                List<object> families = new List<object>();

                foreach (string path in paths)
                {
                    families.Add(LoadOneFamily(document, path));
                }

                return new Dictionary<string, object>
                {
                    { "families", families },

                    // What Revit said during the load and the bridge answered for the caller -
                    // the family upgrade from an older library is the one that matters here.
                    { "warnings", NewDiagnostics(dialogsBefore, failuresBefore) },
                };
            });
        }

        /// <summary>
        /// Body: {category?, familyName?}. The loaded FamilySymbols, which is how a caller
        /// discovers what it can actually place. An empty array is the honest answer for a project
        /// that has had no family content loaded into it - see families/load.
        ///
        /// Both filters are optional and combine. "familyName" matches the family, not the type:
        /// "M_RPC Tree - Deciduous" picks out that family's types whatever they are called, which
        /// is what a caller that has just loaded a file by name actually holds.
        /// </summary>
        internal static object FamilySymbols(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            string categoryName = JsonBody.OptionalString(body, "category");
            string familyName = JsonBody.OptionalString(body, "familyName");

            FilteredElementCollector collector =
                new FilteredElementCollector(document).OfClass(typeof(FamilySymbol));

            if (categoryName != null)
            {
                Category category = RevitFacts.ResolveCategory(document, categoryName);
                if (category == null)
                {
                    throw BridgeException.BadRequest(
                        "Unknown category \"" + categoryName + "\". Call /revit-mcp/categories for "
                            + "the categories present in this document, or pass a BuiltInCategory "
                            + "name such as OST_Planting.");
                }

                collector = collector.OfCategoryId(category.Id);
            }

            List<FamilySymbol> symbols = new List<FamilySymbol>();
            foreach (Element element in collector)
            {
                FamilySymbol symbol = element as FamilySymbol;
                if (symbol == null)
                {
                    continue;
                }

                // Applied here rather than as a filter: FamilyName is a property of the symbol,
                // not a parameter, so there is no FilteredElementCollector filter for it.
                if (familyName != null
                    && !string.Equals(symbol.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                symbols.Add(symbol);
            }

            symbols.Sort(delegate (FamilySymbol left, FamilySymbol right)
            {
                int byFamily = string.Compare(left.FamilyName, right.FamilyName, StringComparison.OrdinalIgnoreCase);
                if (byFamily != 0)
                {
                    return byFamily;
                }

                return string.Compare(
                    RevitFacts.SafeName(left),
                    RevitFacts.SafeName(right),
                    StringComparison.OrdinalIgnoreCase);
            });

            List<object> rows = new List<object>();
            foreach (FamilySymbol symbol in symbols)
            {
                rows.Add(new Dictionary<string, object>
                {
                    { "id", symbol.Id.Value },
                    { "familyName", symbol.FamilyName },
                    { "typeName", RevitFacts.SafeName(symbol) },
                    { "category", RevitFacts.CategoryName(symbol) },
                });
            }

            return rows;
        }

        /// <summary>
        /// Body: {symbolId, level?, z?, points: [{x, y, z?}], rotation?}.
        ///
        /// One TransactionGroup for the whole batch - 200 trees are one Ctrl+Z - but one inner
        /// transaction per point, because a symbol Revit refuses to place at one spot must not take
        /// the other 199 with it. Those land in "failed" with Revit's own reason.
        ///
        /// **Which NewFamilyInstance overload, and the elevation trap in it.** The point-based one
        /// that takes a Level: NewFamilyInstance(XYZ, FamilySymbol, Level, StructuralType). A
        /// planting or site family is OneLevelBased, not OneLevelBasedHosted - it is not hosted by
        /// the terrain it stands on, it sits at an elevation - so the overloads taking an Element
        /// host or a Face are the wrong ones for it and Revit refuses or mis-hosts them.
        ///
        /// What that overload does with the Z is measured against Revit 2027 with a real M_RPC
        /// Tree, not assumed, because it is not what the rest of this bridge means by z: **it
        /// reads location.Z as the offset FROM THE LEVEL, not as a model elevation.** Asked for
        /// z = 0 on a level at -1.476 ft, the tree stands at -1.476 ft; asked for z = 5 on the same
        /// level it stands at 3.524 ft. So a caller passing the absolute elevations every other
        /// endpoint here takes would get every tree pushed up or down by the level elevation, in
        /// silence.
        ///
        /// This endpoint therefore takes z as an ABSOLUTE model elevation like everything else in
        /// the bridge and subtracts the level elevation before handing the point to Revit. The
        /// response reports "placedZ" read back off the instance, so the elevation a family
        /// actually ended up at is never a matter of trust.
        ///
        /// "level" is optional and defaults to the lowest level in the document, which is the one
        /// site content belongs on. "z" is added to every point's own z, so a batch of ground-level
        /// points can be lifted onto a terrace in one number instead of being recomputed.
        ///
        /// "rotation" is radians about the vertical axis through each point, counter-clockwise in
        /// plan - Revit's internal angle unit, like feet is its internal length unit.
        /// </summary>
        internal static object PlaceFamilies(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            long symbolId = JsonBody.AsLong(RequireValue(body, "symbolId"), "symbolId");
            string levelName = JsonBody.OptionalString(body, "level");
            JsonElement points = JsonBody.RequireArray(body, "points");
            double rotation = JsonBody.OptionalDouble(body, "rotation", 0.0);
            double zOffset = JsonBody.OptionalDouble(body, "z", 0.0);

            FamilySymbol symbol = document.GetElement(new ElementId(symbolId)) as FamilySymbol;
            if (symbol == null)
            {
                throw BridgeException.BadRequest(
                    "Element " + symbolId + " is not a loaded family type in this document. Call "
                        + "/revit-mcp/families/symbols for the ones that are, and "
                        + "/revit-mcp/families/load to put one there.");
            }

            Level level = ResolveLevelOrLowest(document, levelName);

            // Read every point up front: a malformed one is a BAD_REQUEST for the whole call, not a
            // placement Revit refused.
            List<XYZ> locations = RevitFacts.ReadPoints(points, "points", false, 0.0);

            if (zOffset != 0.0)
            {
                for (int index = 0; index < locations.Count; index++)
                {
                    locations[index] = new XYZ(
                        locations[index].X,
                        locations[index].Y,
                        locations[index].Z + zOffset);
                }
            }

            return RevitWrite.InGroup(document, "MCP: place families", delegate
            {
                List<object> placed = new List<object>();
                List<object> failed = new List<object>();

                if (!symbol.IsActive)
                {
                    // NewFamilyInstance throws on a symbol that has never been activated, and
                    // activating one is itself a model change, so it needs its own transaction.
                    RevitWrite.InTransaction(document, "Activate family type", delegate
                    {
                        symbol.Activate();
                        document.Regenerate();
                    });
                }

                foreach (XYZ location in locations)
                {
                    FamilyInstance instance = null;

                    try
                    {
                        RevitWrite.InTransaction(document, "Place family instance", delegate
                        {
                            // location.Z is an absolute model elevation here; the overload wants an
                            // offset from the level. See the summary - this subtraction is the
                            // whole difference between a tree on the ground and a tree floating.
                            instance = document.Create.NewFamilyInstance(
                                new XYZ(location.X, location.Y, location.Z - level.Elevation),
                                symbol,
                                level,
                                StructuralType.NonStructural);

                            if (rotation != 0.0)
                            {
                                // The instance has to exist properly before it can be transformed;
                                // rotating one Revit has not regenerated yet is how you get an
                                // element with no location.
                                document.Regenerate();

                                Line axis = Line.CreateBound(location, location + XYZ.BasisZ);
                                ElementTransformUtils.RotateElement(document, instance.Id, axis, rotation);
                            }
                        });
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                    {
                        // The inner transaction rolled back, so nothing was left behind and the
                        // rest of the batch carries on.
                        failed.Add(new Dictionary<string, object>
                        {
                            { "x", location.X },
                            { "y", location.Y },
                            { "reason", ex.Message },
                        });

                        continue;
                    }

                    Dictionary<string, object> row = new Dictionary<string, object>
                    {
                        { "id", instance.Id.Value },
                        { "x", location.X },
                        { "y", location.Y },
                        { "z", location.Z },
                    };

                    // Read back off the instance, not echoed: where a family actually ended up is
                    // the whole question with a point-based placement, and the only way to see an
                    // elevation Revit decided differently about is to ask it.
                    LocationPoint point = instance.Location as LocationPoint;
                    if (point != null)
                    {
                        row["placedZ"] = point.Point.Z;
                    }

                    placed.Add(row);
                }

                return new Dictionary<string, object>
                {
                    { "symbolId", symbolId },
                    { "familyName", symbol.FamilyName },
                    { "typeName", RevitFacts.SafeName(symbol) },
                    { "level", level.Name },
                    { "levelElevation", level.Elevation },
                    { "placed", placed },
                    { "failed", failed },
                };
            });
        }


        /// <summary>
        /// Body: {symbolId, points: [{x, y, z?}], hostWallId?, level?, sillHeight?}. Doors and
        /// windows - the family instances that have to CUT the wall they sit in, which is what
        /// makes this its own endpoint rather than a flag on families/place.
        ///
        /// The host is the whole point. Document.Create.NewFamilyInstance has one overload that
        /// takes a host - (XYZ location, FamilySymbol symbol, Element host, Level level,
        /// StructuralType structuralType) - and only that one produces an opening. The overloads
        /// without a host leave a door-shaped object standing in front of an uncut wall, which
        /// looks nearly right in plan and wrong in every 3D view.
        ///
        /// "hostWallId" is optional, and omitting it is the normal case: for each point the bridge
        /// projects it onto every wall's LocationCurve and takes the nearest within
        /// <see cref="HostWallTolerance"/> feet. The wall each instance actually landed in is
        /// reported per point, read back off FamilyInstance.Host rather than echoed - a symbol that
        /// is not wall-hosted places with a null host, and that has to be visible rather than
        /// assumed away. A point with no wall near it fails with NO_HOST_WALL and the rest of the
        /// batch still lands.
        ///
        /// "sillHeight" is feet above the level. Omitted, a Windows-category symbol gets
        /// <see cref="DefaultWindowSill"/> feet - a window sitting on the floor is not a window -
        /// and anything else keeps whatever Revit gave it. It is written to the instance's Sill
        /// Height, or to the type's when that is where the family keeps it, and the row says which
        /// in "sillHeightOn": writing the type's moves every other instance of that type.
        ///
        /// Same rules as families/place: the symbol is activated if it has never been used, every
        /// point is one instance, and the whole batch is one Ctrl+Z.
        /// </summary>
        internal static object PlaceOpenings(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            long symbolId = JsonBody.AsLong(RequireValue(body, "symbolId"), "symbolId");
            string levelName = JsonBody.OptionalString(body, "level");
            JsonElement points = JsonBody.RequireArray(body, "points");
            double sillHeight = JsonBody.OptionalDouble(body, "sillHeight", double.NaN);

            FamilySymbol symbol = document.GetElement(new ElementId(symbolId)) as FamilySymbol;
            if (symbol == null)
            {
                throw BridgeException.BadRequest(
                    "Element " + symbolId + " is not a loaded family type in this document. Call "
                        + "/revit-mcp/families/symbols for the ones that are, and "
                        + "/revit-mcp/families/load to put one there.");
            }

            Wall requestedHost = null;

            JsonElement hostValue;
            if (JsonBody.TryGet(body, "hostWallId", out hostValue))
            {
                long hostWallId = JsonBody.AsLong(hostValue, "hostWallId");

                requestedHost = document.GetElement(new ElementId(hostWallId)) as Wall;
                if (requestedHost == null)
                {
                    throw BridgeException.BadRequest(
                        "Element " + hostWallId + " is not a wall in this document. Omit "
                            + "\"hostWallId\" to let the bridge find the nearest wall to each "
                            + "point, or find the walls with /revit-mcp/query.");
                }
            }

            Level level = ResolveLevelOrLowest(document, levelName);

            // Read every point up front: a malformed one is a BAD_REQUEST for the whole call, not a
            // placement Revit refused.
            List<XYZ> locations = RevitFacts.ReadPoints(points, "points", false, 0.0);

            // Collected once, not per point: the set cannot change while this request runs.
            List<Wall> walls = requestedHost == null ? HostWalls(document) : null;

            bool isWindow = symbol.Category != null
                && symbol.Category.Id.Value == (long)BuiltInCategory.OST_Windows;

            double sill = double.IsNaN(sillHeight) && isWindow ? DefaultWindowSill : sillHeight;

            return RevitWrite.InGroup(document, "MCP: place openings", delegate
            {
                List<object> placed = new List<object>();
                List<object> failed = new List<object>();

                if (!symbol.IsActive)
                {
                    // NewFamilyInstance throws on a symbol that has never been activated, and
                    // activating one is itself a model change, so it needs its own transaction.
                    RevitWrite.InTransaction(document, "Activate family type", delegate
                    {
                        symbol.Activate();
                        document.Regenerate();
                    });
                }

                foreach (XYZ location in locations)
                {
                    Wall host = requestedHost ?? NearestWall(walls, location);

                    if (host == null)
                    {
                        failed.Add(new Dictionary<string, object>
                        {
                            { "x", location.X },
                            { "y", location.Y },
                            { "z", location.Z },
                            { "code", "NO_HOST_WALL" },
                            {
                                "reason",
                                "No wall within " + HostWallTolerance + " feet of this point, "
                                    + "measured onto each wall's location line. A door or a window "
                                    + "with no host cuts nothing, so nothing was placed here. Move "
                                    + "the point onto a wall, or name the wall with \"hostWallId\"."
                            },
                        });

                        continue;
                    }

                    FamilyInstance instance = null;
                    Dictionary<string, object> sillReport = new Dictionary<string, object>();

                    try
                    {
                        RevitWrite.InTransaction(document, "Place opening", delegate
                        {
                            instance = document.Create.NewFamilyInstance(
                                location,
                                symbol,
                                host,
                                level,
                                StructuralType.NonStructural);

                            // The instance has to exist properly before its sill can be read or
                            // written: Revit derives the sill from the placement during
                            // regeneration, and writing over a value it has not produced yet is
                            // how you get a window at the wrong height.
                            document.Regenerate();
                        });

                        if (!double.IsNaN(sill))
                        {
                            ApplySillHeight(document, instance, sill, sillReport);
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                    {
                        // The inner transaction rolled back, so nothing was left behind and the
                        // rest of the batch carries on.
                        failed.Add(new Dictionary<string, object>
                        {
                            { "x", location.X },
                            { "y", location.Y },
                            { "z", location.Z },
                            { "code", "REVIT_API_ERROR" },
                            { "reason", ex.Message },
                        });

                        continue;
                    }

                    Dictionary<string, object> row = new Dictionary<string, object>
                    {
                        { "id", instance.Id.Value },
                        { "x", location.X },
                        { "y", location.Y },
                        { "z", location.Z },
                    };

                    // Read back off the instance, not echoed. The host is the whole question - a
                    // null one is an instance that cuts nothing - and where the sill and the
                    // insertion point ended up is Revit's answer, not the request's.
                    Element actualHost = instance.Host;
                    row["hostWallId"] = actualHost == null ? null : (object)actualHost.Id.Value;
                    row["hostWallType"] = actualHost == null
                        ? null
                        : RevitFacts.TypeName(document, actualHost);

                    row["sillHeight"] = ReadSillHeight(document, instance);

                    foreach (KeyValuePair<string, object> entry in sillReport)
                    {
                        row[entry.Key] = entry.Value;
                    }

                    LocationPoint point = instance.Location as LocationPoint;
                    if (point != null)
                    {
                        row["placedZ"] = point.Point.Z;
                    }

                    placed.Add(row);
                }

                return new Dictionary<string, object>
                {
                    { "symbolId", symbolId },
                    { "familyName", symbol.FamilyName },
                    { "typeName", RevitFacts.SafeName(symbol) },
                    { "category", RevitFacts.CategoryName(symbol) },
                    { "level", level.Name },
                    { "levelElevation", level.Elevation },
                    { "placed", placed },
                    { "failed", failed },
                };
            });
        }

        /// <summary>
        /// Adds the ring to the toposolid's shape at the target elevation and creases it, so the
        /// flat region ends at its boundary rather than sloping on into the surrounding terrain.
        ///
        /// A split line Revit refuses is counted, not thrown: the region is still flattened, it
        /// just meets the terrain across triangles instead of along an edge.
        /// </summary>
        private static void AddRing(
            Document document,
            Toposolid toposolid,
            List<XYZ> ring,
            Dictionary<string, object> result)
        {
            int added = 0;
            int creases = 0;

            RevitWrite.InTransaction(document, "Add toposolid points", delegate
            {
                SlabShapeEditor editor = toposolid.GetSlabShapeEditor();

                if (!editor.IsEnabled)
                {
                    editor.Enable();
                }

                IList<SlabShapeVertex> vertices;

                try
                {
                    vertices = editor.AddPoints(ring);
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                {
                    throw BridgeException.BadRequest(
                        "Revit would not add these points to toposolid " + toposolid.Id.Value + ": "
                            + ex.Message + " They must be distinct in plan and inside the "
                            + "toposolid's own boundary.");
                }

                // Required when AddPoints shares a transaction with the edits that follow it.
                document.Regenerate();

                added = vertices.Count;

                for (int index = 0; index < ring.Count; index++)
                {
                    XYZ start = ring[index];
                    XYZ end = ring[(index + 1) % ring.Count];

                    try
                    {
                        // Found by where they are rather than held across the edits, and only
                        // used when a vertex really is at the ring point: a split line drawn
                        // between the wrong pair would cut across the region instead of bounding
                        // it. AddSplitLine, not DrawSplitLine - the latter is deprecated in 2025.
                        editor = toposolid.GetSlabShapeEditor();

                        SlabShapeVertex from = NearestVertex(editor, start);
                        SlabShapeVertex to = NearestVertex(editor, end);

                        if (from == null
                            || to == null
                            || PlanDistance(start, from.Position) > FlattenTolerance
                            || PlanDistance(end, to.Position) > FlattenTolerance)
                        {
                            continue;
                        }

                        editor.AddSplitLine(from, to);
                        document.Regenerate();

                        creases++;
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        // A crease Revit refuses costs the region a crisp edge, nothing more.
                    }
                }
            });

            result["added"] = added;
            result["creases"] = creases;
        }

        /// <summary>
        /// Moves every shape vertex inside the ring to the target elevation.
        ///
        /// SlabShapeEditor.ModifySubElement takes "the new value of the vertex offset" and the API
        /// documents no datum for it: it is either measured from where the vertex already is, or
        /// from something the whole slab shares. Guessing would be a coin flip, so this measures
        /// instead. One vertex is offset by 1 ft, then by 2 ft, and the two elevations it lands at
        /// answer the question outright - a relative offset stacks (the second lands 2 ft above the
        /// first), an absolute one does not (it lands 1 ft above). The datum, when there is one,
        /// falls straight out of the first measurement.
        ///
        /// "residual" is measured afterwards and reported, so a caller never has to take any of
        /// this on trust.
        /// </summary>
        private static void FlattenRegion(
            Document document,
            Toposolid toposolid,
            List<XYZ> ring,
            double elevation,
            Dictionary<string, object> result)
        {
            int flattened = 0;
            double residual = 0.0;
            string mode = "none";

            RevitWrite.InTransaction(document, "Flatten toposolid region", delegate
            {
                SlabShapeEditor editor = toposolid.GetSlabShapeEditor();

                List<SlabShapeVertex> inside = InsideRing(editor, ring);
                if (inside.Count == 0)
                {
                    return;
                }

                XYZ probe = inside[0].Position;

                editor.ModifySubElement(inside[0], ProbeOffset);
                document.Regenerate();

                editor = toposolid.GetSlabShapeEditor();
                double first = NearestVertexElevation(editor, probe, elevation);

                SlabShapeVertex again = NearestVertex(editor, probe);
                editor.ModifySubElement(again, 2.0 * ProbeOffset);
                document.Regenerate();

                editor = toposolid.GetSlabShapeEditor();
                double second = NearestVertexElevation(editor, probe, elevation);

                double step = second - first;
                double datum = first - ProbeOffset;

                // Relative when the second probe stacked on the first, absolute when it replaced
                // it. Anything else means Revit did not move the vertex the way either reading
                // predicts; relative is the safer fallback and "residual" will say if it was wrong.
                bool absolute = Math.Abs(step - ProbeOffset) < Math.Abs(step - 2.0 * ProbeOffset);
                mode = absolute ? "absolute" : "relative";

                // Plan positions, taken before anything moves. Flattening never changes x or y, so
                // each vertex is found again by where it is - which is what makes this safe across
                // the regeneration every edit gets, rather than holding handles Revit may have
                // invalidated underneath us.
                List<XYZ> targets = new List<XYZ>();
                foreach (SlabShapeVertex vertex in InsideRing(editor, ring))
                {
                    targets.Add(vertex.Position);
                }

                foreach (XYZ target in targets)
                {
                    editor = toposolid.GetSlabShapeEditor();

                    SlabShapeVertex vertex = NearestVertex(editor, target);
                    if (vertex == null)
                    {
                        continue;
                    }

                    double offset = elevation - datum;

                    if (!absolute)
                    {
                        offset = elevation - vertex.Position.Z;

                        if (Math.Abs(offset) < FlattenTolerance)
                        {
                            continue;
                        }
                    }

                    editor.ModifySubElement(vertex, offset);
                    document.Regenerate();

                    flattened++;
                }

                editor = toposolid.GetSlabShapeEditor();

                foreach (SlabShapeVertex vertex in InsideRing(editor, ring))
                {
                    residual = Math.Max(residual, Math.Abs(elevation - vertex.Position.Z));
                }
            });

            result["flattened"] = flattened;
            result["offsetMode"] = mode;
            result["residual"] = residual;
        }

        /// <summary>
        /// The named toposolid, or the document's only one. Two toposolids and no "toposolidId" is
        /// a BAD_REQUEST listing them: flattening the wrong surface is not worth guessing at.
        /// </summary>
        private static Toposolid ResolveToposolid(Document document, JsonElement body)
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
                    "This document has no toposolid to flatten. Build one with "
                        + "/revit-mcp/toposolid/create first. A legacy TopographySurface cannot be "
                        + "flattened by this endpoint.");
            }

            if (toposolids.Count > 1)
            {
                throw BridgeException.BadRequest(
                    "This document has " + toposolids.Count + " toposolids, so \"toposolidId\" is "
                        + "required. They are: " + string.Join(", ", available) + ".");
            }

            return toposolids[0];
        }

        /// <summary>Every editable shape vertex whose plan position is inside the ring, or on it.</summary>
        private static List<SlabShapeVertex> InsideRing(SlabShapeEditor editor, List<XYZ> ring)
        {
            List<SlabShapeVertex> inside = new List<SlabShapeVertex>();

            SlabShapeVertexArray vertices = editor.SlabShapeVertices;

            for (int index = 0; index < vertices.Size; index++)
            {
                SlabShapeVertex vertex = vertices.get_Item(index);

                if (IsInside(ring, vertex.Position))
                {
                    inside.Add(vertex);
                }
            }

            return inside;
        }

        /// <summary>
        /// Ray casting in plan, with the ring itself counting as inside - the points this endpoint
        /// just added sit exactly on it, and a ray cast is no use at all about those.
        /// </summary>
        private static bool IsInside(List<XYZ> ring, XYZ point)
        {
            bool inside = false;

            for (int index = 0; index < ring.Count; index++)
            {
                XYZ start = ring[index];
                XYZ end = ring[(index + 1) % ring.Count];

                if (PlanDistanceToSegment(start, end, point) < FlattenTolerance)
                {
                    return true;
                }

                if ((start.Y > point.Y) != (end.Y > point.Y))
                {
                    double crossing = start.X + (point.Y - start.Y) / (end.Y - start.Y) * (end.X - start.X);

                    if (point.X < crossing)
                    {
                        inside = !inside;
                    }
                }
            }

            return inside;
        }

        private static double PlanDistance(XYZ left, XYZ right)
        {
            double dx = left.X - right.X;
            double dy = left.Y - right.Y;

            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double PlanDistanceToSegment(XYZ start, XYZ end, XYZ point)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double lengthSquared = dx * dx + dy * dy;

            double position = 0.0;
            if (lengthSquared > 0.0)
            {
                position = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
                position = Math.Max(0.0, Math.Min(1.0, position));
            }

            double offsetX = start.X + position * dx - point.X;
            double offsetY = start.Y + position * dy - point.Y;

            return Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
        }

        /// <summary>
        /// The shape vertex nearest <paramref name="plan"/> in plan. A regeneration invalidates the
        /// SlabShapeVertex objects handed out before it, so a vertex followed across one is found
        /// again by where it is, which flattening never moves.
        /// </summary>
        private static SlabShapeVertex NearestVertex(SlabShapeEditor editor, XYZ plan)
        {
            SlabShapeVertex nearest = null;
            double best = double.MaxValue;

            SlabShapeVertexArray vertices = editor.SlabShapeVertices;

            for (int index = 0; index < vertices.Size; index++)
            {
                SlabShapeVertex vertex = vertices.get_Item(index);

                double distance = PlanDistance(plan, vertex.Position);

                if (distance < best)
                {
                    best = distance;
                    nearest = vertex;
                }
            }

            return nearest;
        }

        private static double NearestVertexElevation(SlabShapeEditor editor, XYZ plan, double fallback)
        {
            SlabShapeVertex nearest = NearestVertex(editor, plan);

            return nearest == null ? fallback : nearest.Position.Z;
        }

        /// <summary>
        /// The named ToposolidType, the document default, or null when the document has none at all
        /// - which is the signal to fall back to TopographySurface.
        /// </summary>
        private static ElementId ResolveToposolidTypeId(Document document, string typeName)
        {
            List<string> available = new List<string>();
            ElementId first = null;

            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(ToposolidType)))
            {
                string name = RevitFacts.SafeName(element);
                if (name == null)
                {
                    continue;
                }

                if (typeName != null && string.Equals(name, typeName, StringComparison.OrdinalIgnoreCase))
                {
                    return element.Id;
                }

                if (first == null)
                {
                    first = element.Id;
                }

                available.Add(name);
            }

            if (typeName != null)
            {
                available.Sort(StringComparer.OrdinalIgnoreCase);

                throw BridgeException.BadRequest(
                    "Unknown toposolid type \"" + typeName + "\". Toposolid types in this document: "
                        + string.Join(", ", available) + ".");
            }

            return first;
        }

        /// <summary>
        /// The shared body of walltypes/create and floortypes/create: duplicate an existing type,
        /// then give the duplicate a single-layer CompoundStructure carrying the thickness and the
        /// material that were asked for.
        ///
        /// A type already called "name" is REUSED and comes back with "created": false and its
        /// current thickness and material - it is not re-cut to match the request. Re-running the
        /// same call is therefore safe, and a type somebody authored by hand is never quietly
        /// rebuilt underneath them; the response says what the caller actually got.
        ///
        /// The single layer replaces whatever layering the source type had, and only when a
        /// thickness or a material was asked for: with neither, this is a plain duplicate. An
        /// omitted "thickness" keeps the source's width and an omitted material keeps the source's
        /// first-layer material, so either can be set without disturbing the other.
        /// </summary>
        private static object CreateHostType(
            UIApplication app,
            JsonElement body,
            Type typeClass,
            ElementTypeGroup group,
            string label)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            string name = JsonBody.RequireString(body, "name");
            string basedOnTypeName = JsonBody.OptionalString(body, "basedOnTypeName");
            double thickness = JsonBody.OptionalDouble(body, "thickness", 0.0);
            ElementId materialId = MaterialEndpoints.OptionalMaterialId(document, body);

            if (thickness < 0.0)
            {
                throw BridgeException.BadRequest(
                    "\"thickness\" must be greater than zero (decimal feet).");
            }

            HostObjAttributes existing = FindHostType(document, typeClass, name);
            if (existing != null)
            {
                return DescribeHostType(document, existing, false, null);
            }

            HostObjAttributes source = ResolveHostTypeSource(document, typeClass, group, basedOnTypeName, label);
            string basedOn = RevitFacts.SafeName(source);

            return RevitWrite.InGroup(document, "MCP: create " + label + " type", delegate
            {
                Dictionary<string, object> created = null;

                RevitWrite.InTransaction(document, "Create " + label + " type", delegate
                {
                    HostObjAttributes duplicate;

                    try
                    {
                        duplicate = (HostObjAttributes)source.Duplicate(name);
                    }
                    catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                    {
                        throw BridgeException.BadRequest(
                            "Revit would not name the new " + label + " type \"" + name + "\": "
                                + ex.Message);
                    }

                    if (thickness > 0.0 || materialId != null)
                    {
                        CompoundStructure structure = duplicate.GetCompoundStructure();

                        if (structure == null || structure.LayerCount == 0)
                        {
                            throw BridgeException.BadRequest(
                                "\"" + basedOn + "\" has no compound structure, so it cannot carry a "
                                    + "thickness or a material - a curtain or stacked wall type "
                                    + "carries neither. Base the new type on a basic one with "
                                    + "\"basedOnTypeName\".");
                        }

                        double width = thickness > 0.0 ? thickness : structure.GetWidth();
                        ElementId layerMaterial = materialId != null
                            ? materialId
                            : structure.GetMaterialId(0);

                        try
                        {
                            CompoundStructure replacement =
                                CompoundStructure.CreateSingleLayerCompoundStructure(
                                    MaterialFunctionAssignment.Structure,
                                    width,
                                    layerMaterial);

                            // A CompoundStructure also carries an end cap condition - which shell
                            // layers wrap at the ends - and only a WallType may carry a real one.
                            // CreateSingleLayerCompoundStructure hands back a wall's, so a floor,
                            // ceiling or roof type must be told otherwise before SetCompoundStructure
                            // or Revit throws "Input compound structure has wrong EndCap condition
                            // for this element type". EndCapCondition.NoEndCap is the one the API
                            // documents as required for floors and roofs - NOT .None, which is a
                            // wall's "no shell layer wraps". Set explicitly rather than inherited
                            // from the duplicated source, whose condition is only right by luck.
                            if (typeClass != typeof(WallType))
                            {
                                replacement.EndCap = EndCapCondition.NoEndCap;
                            }

                            duplicate.SetCompoundStructure(replacement);
                        }
                        catch (Autodesk.Revit.Exceptions.ArgumentOutOfRangeException ex)
                        {
                            throw BridgeException.BadRequest(
                                "Revit refused a single layer " + width + " ft thick: " + ex.Message);
                        }
                    }

                    created = DescribeHostType(document, duplicate, true, basedOn);
                });

                return created;
            });
        }

        /// <summary>
        /// Thickness and material are read back off the type's own compound structure rather than
        /// echoed, so the reuse path and the create path answer the same way and both say what the
        /// type really carries.
        /// </summary>
        private static Dictionary<string, object> DescribeHostType(
            Document document,
            HostObjAttributes type,
            bool created,
            string basedOn)
        {
            CompoundStructure structure = type.GetCompoundStructure();

            object thickness = null;
            ElementId materialId = null;

            if (structure != null)
            {
                thickness = structure.GetWidth();

                if (structure.LayerCount > 0)
                {
                    ElementId layerMaterial = structure.GetMaterialId(0);
                    if (layerMaterial != null && layerMaterial != ElementId.InvalidElementId)
                    {
                        materialId = layerMaterial;
                    }
                }
            }

            Element material = materialId == null ? null : document.GetElement(materialId);

            return new Dictionary<string, object>
            {
                { "id", type.Id.Value },
                { "name", RevitFacts.SafeName(type) },
                { "created", created },
                { "basedOn", basedOn },
                { "thickness", thickness },
                { "materialId", materialId == null ? null : (object)materialId.Value },
                { "materialName", material == null ? null : RevitFacts.SafeName(material) },
                { "layers", structure == null ? 0 : structure.LayerCount },
            };
        }

        private static HostObjAttributes FindHostType(Document document, Type typeClass, string name)
        {
            foreach (Element element in new FilteredElementCollector(document).OfClass(typeClass))
            {
                if (string.Equals(RevitFacts.SafeName(element), name, StringComparison.OrdinalIgnoreCase))
                {
                    return element as HostObjAttributes;
                }
            }

            return null;
        }

        /// <summary>
        /// The type the new one is duplicated from: the named one, the document default, or the
        /// first one there is. Duplicating is the only way to author one of these - there is no
        /// WallType.Create - so a document with none at all cannot be helped, and says so.
        /// </summary>
        private static HostObjAttributes ResolveHostTypeSource(
            Document document,
            Type typeClass,
            ElementTypeGroup group,
            string basedOnTypeName,
            string label)
        {
            if (basedOnTypeName != null)
            {
                HostObjAttributes named = FindHostType(document, typeClass, basedOnTypeName);
                if (named != null)
                {
                    return named;
                }

                List<string> available = new List<string>();

                foreach (Element element in new FilteredElementCollector(document).OfClass(typeClass))
                {
                    string name = RevitFacts.SafeName(element);
                    if (name != null)
                    {
                        available.Add(name);
                    }
                }

                available.Sort(StringComparer.OrdinalIgnoreCase);

                throw BridgeException.BadRequest(
                    "Unknown " + label + " type \"" + basedOnTypeName + "\". " + label + " types in "
                        + "this document: " + string.Join(", ", available) + ".");
            }

            ElementId defaultId = document.GetDefaultElementTypeId(group);
            if (defaultId != null && defaultId != ElementId.InvalidElementId)
            {
                HostObjAttributes fallback = document.GetElement(defaultId) as HostObjAttributes;
                if (fallback != null)
                {
                    return fallback;
                }
            }

            foreach (Element element in new FilteredElementCollector(document).OfClass(typeClass))
            {
                HostObjAttributes first = element as HostObjAttributes;
                if (first != null)
                {
                    return first;
                }
            }

            throw BridgeException.BadRequest(
                "This document has no " + label + " type to base a new one on.");
        }

        private static ElementId ResolveFloorTypeId(Document document, string typeName)
        {
            if (typeName == null)
            {
                ElementId defaultId = document.GetDefaultElementTypeId(ElementTypeGroup.FloorType);
                if (defaultId == null || defaultId == ElementId.InvalidElementId)
                {
                    throw BridgeException.BadRequest(
                        "This document has no default floor type; pass \"typeName\" explicitly.");
                }

                return defaultId;
            }

            List<string> available = new List<string>();

            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(FloorType)))
            {
                string name = RevitFacts.SafeName(element);
                if (name == null)
                {
                    continue;
                }

                if (string.Equals(name, typeName, StringComparison.OrdinalIgnoreCase))
                {
                    return element.Id;
                }

                available.Add(name);
            }

            available.Sort(StringComparer.OrdinalIgnoreCase);

            throw BridgeException.BadRequest(
                "Unknown floor type \"" + typeName + "\". Floor types in this document: "
                    + string.Join(", ", available) + ".");
        }

        /// <summary>
        /// Toposolid.Create needs a level and the request shape does not insist on one, so an
        /// omitted "level" means the lowest one in the document - the ground, for a site element.
        /// </summary>
        private static Level ResolveLevelOrLowest(Document document, string levelName)
        {
            if (levelName != null)
            {
                Level named = RevitFacts.ResolveLevel(document, levelName);
                if (named == null)
                {
                    throw BridgeException.BadRequest(
                        "Unknown level \"" + levelName + "\". Levels in this document: "
                            + string.Join(", ", RevitFacts.LevelNames(document)) + ".");
                }

                return named;
            }

            Level lowest = null;

            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(Level)))
            {
                Level level = element as Level;
                if (level == null)
                {
                    continue;
                }

                if (lowest == null || level.Elevation < lowest.Elevation)
                {
                    lowest = level;
                }
            }

            if (lowest == null)
            {
                throw BridgeException.BadRequest(
                    "This document has no levels, so there is nothing to host a toposolid on.");
            }

            return lowest;
        }

        private static object FloorArea(Floor floor)
        {
            Parameter area = floor.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
            if (area == null)
            {
                return null;
            }

            return area.AsDouble();
        }

        /// <summary>
        /// Loads one .rfa and describes what happened to it. Never throws: every failure is a row
        /// carrying a code and Revit's own reason, so the rest of the batch still loads.
        /// </summary>
        private static Dictionary<string, object> LoadOneFamily(Document document, string path)
        {
            Dictionary<string, object> row = new Dictionary<string, object>
            {
                { "path", path },
                { "familyName", null },
                { "loaded", false },
                { "alreadyLoaded", false },
                { "symbols", new List<object>() },
            };

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex)
            {
                row["code"] = "BAD_PATH";
                row["reason"] = "\"" + path + "\" is not a usable file path: " + ex.Message;
                return row;
            }

            row["path"] = full;

            if (!File.Exists(full))
            {
                row["code"] = "FILE_NOT_FOUND";
                row["reason"] = "No file at \"" + full + "\". Autodesk's family library is an "
                    + "optional download and lives under C:\\ProgramData\\Autodesk\\RVT <year>\\"
                    + "Libraries\\<language>; pass the full path of an .rfa file that exists.";
                return row;
            }

            // Revit names a loaded family after its file, so this is what says whether the project
            // already has it - and therefore whether BridgeFamilyLoadOptions is about to be asked.
            Family existing = FindFamily(document, Path.GetFileNameWithoutExtension(full));
            bool alreadyLoaded = existing != null;

            row["alreadyLoaded"] = alreadyLoaded;

            Family family = existing;
            bool loaded = false;

            try
            {
                RevitWrite.InTransaction(document, "Load family", delegate
                {
                    Family result;
                    loaded = document.LoadFamily(full, new BridgeFamilyLoadOptions(), out result);

                    // LoadFamily hands back the family whether it loaded it or found it, but only
                    // when it has one - a refusal leaves it null and the existing one stands.
                    if (result != null)
                    {
                        family = result;
                    }
                });
            }
            catch (Exception ex)
            {
                // Exception, not Autodesk.Revit.Exceptions.ApplicationException: a corrupt or
                // unreadable .rfa surfaces as an IO or serialisation failure from outside Revit's
                // own hierarchy, and one of those must not take the rest of the batch with it.
                row["code"] = "LOAD_FAILED";
                row["reason"] = ex.Message;

                if (alreadyLoaded)
                {
                    row["familyName"] = RevitFacts.SafeName(existing);
                    row["symbols"] = FamilySymbolRows(document, existing);
                }

                return row;
            }

            row["loaded"] = loaded;

            if (family != null)
            {
                row["familyName"] = RevitFacts.SafeName(family);
                row["symbols"] = FamilySymbolRows(document, family);
            }

            if (!loaded && family == null)
            {
                row["code"] = "NOT_LOADED";
                row["reason"] = "Revit declined to load \"" + full + "\" and gave no reason. The "
                    + "usual cause is a file that is not a family document.";
            }

            return row;
        }

        /// <summary>The family types a family owns, as {id, typeName}, sorted by name.</summary>
        private static List<object> FamilySymbolRows(Document document, Family family)
        {
            List<FamilySymbol> symbols = new List<FamilySymbol>();

            foreach (ElementId id in family.GetFamilySymbolIds())
            {
                FamilySymbol symbol = document.GetElement(id) as FamilySymbol;
                if (symbol != null)
                {
                    symbols.Add(symbol);
                }
            }

            symbols.Sort(delegate (FamilySymbol left, FamilySymbol right)
            {
                return string.Compare(
                    RevitFacts.SafeName(left),
                    RevitFacts.SafeName(right),
                    StringComparison.OrdinalIgnoreCase);
            });

            List<object> rows = new List<object>();
            foreach (FamilySymbol symbol in symbols)
            {
                rows.Add(new Dictionary<string, object>
                {
                    { "id", symbol.Id.Value },
                    { "typeName", RevitFacts.SafeName(symbol) },
                });
            }

            return rows;
        }

        private static Family FindFamily(Document document, string name)
        {
            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(Family)))
            {
                Family family = element as Family;
                if (family != null
                    && string.Equals(RevitFacts.SafeName(family), name, StringComparison.OrdinalIgnoreCase))
                {
                    return family;
                }
            }

            return null;
        }

        private static List<object> DiagnosticEntries(string key)
        {
            return (List<object>)BridgeDiagnostics.Snapshot()[key];
        }

        /// <summary>
        /// Everything BridgeDiagnostics recorded since the counts were taken. Loading a family
        /// saved by an older Revit upgrades it, and the upgrade is exactly the kind of thing that
        /// raises a warning the bridge answers silently - so it is reported rather than left for a
        /// caller who would have to know to go and look at /revit-mcp/diagnostics.
        /// </summary>
        private static List<object> NewDiagnostics(int dialogsBefore, int failuresBefore)
        {
            List<object> warnings = new List<object>();

            AppendFrom(warnings, DiagnosticEntries("dialogs"), dialogsBefore);
            AppendFrom(warnings, DiagnosticEntries("failures"), failuresBefore);

            return warnings;
        }

        private static void AppendFrom(List<object> target, List<object> entries, int from)
        {
            for (int index = from; index < entries.Count; index++)
            {
                target.Add(entries[index]);
            }
        }

        /// <summary>
        /// What the bridge answers Revit with when a family being loaded is already in the project.
        ///
        /// Without an IFamilyLoadOptions there is nowhere for Revit to ask, and the load raises
        /// instead - which is why families/load uses the three-argument LoadFamily overload and
        /// never the bare one.
        ///
        /// Both answers are "yes, take the file's version of the family, but leave the project's
        /// values alone":
        ///
        ///   OnFamilyFound -> true, overwriteParameterValues = false. Reloading is the whole point
        ///   of asking again, so it goes ahead; but overwriting parameter values would throw away
        ///   whatever has been set on the types IN this project - the height somebody tuned on a
        ///   tree type - in exchange for the library defaults. A reload that quietly undoes model
        ///   edits is far worse than one that leaves them, so the project's values win. This is
        ///   also what Revit's own "Overwrite the existing version" button does.
        ///
        ///   OnSharedFamilyFound -> true, source = FamilySource.Project. A nested shared family the
        ///   project already has stays as the project has it; taking the incoming file's copy would
        ///   change instances that are already placed and that nobody in this request mentioned.
        /// </summary>
        private sealed class BridgeFamilyLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = false;
                return true;
            }

            public bool OnSharedFamilyFound(
                Family sharedFamily,
                bool familyInUse,
                out FamilySource source,
                out bool overwriteParameterValues)
            {
                source = FamilySource.Project;
                overwriteParameterValues = false;
                return true;
            }
        }


        /// <summary>
        /// Every wall that could host an opening. A stacked wall member and anything without a
        /// location line is left out: neither can be projected onto, and Revit will not host in a
        /// stacked wall's container anyway.
        /// </summary>
        private static List<Wall> HostWalls(Document document)
        {
            List<Wall> walls = new List<Wall>();

            foreach (Element element in new FilteredElementCollector(document)
                .OfClass(typeof(Wall))
                .WhereElementIsNotElementType())
            {
                Wall wall = element as Wall;
                if (wall != null && wall.Location is LocationCurve)
                {
                    walls.Add(wall);
                }
            }

            return walls;
        }

        /// <summary>
        /// The wall whose location line passes closest to the point, or null when the nearest is
        /// further away than <see cref="HostWallTolerance"/>.
        ///
        /// The point is dropped onto each curve's own elevation before projecting. A door is given
        /// at floor level while the wall it belongs to may be based metres below, and measuring
        /// that vertical gap would rank walls by where they start instead of by where they run.
        /// </summary>
        private static Wall NearestWall(List<Wall> walls, XYZ point)
        {
            Wall nearest = null;
            double best = HostWallTolerance;

            foreach (Wall wall in walls)
            {
                LocationCurve location = (LocationCurve)wall.Location;
                Curve curve = location.Curve;

                XYZ flat = new XYZ(point.X, point.Y, curve.GetEndPoint(0).Z);

                IntersectionResult result = curve.Project(flat);
                if (result == null)
                {
                    continue;
                }

                if (result.Distance < best)
                {
                    best = result.Distance;
                    nearest = wall;
                }
            }

            return nearest;
        }

        /// <summary>
        /// The sill a family actually exposes. INSTANCE_SILL_HEIGHT_PARAM first - where a stock
        /// Autodesk door or window keeps it - then a parameter literally named "Sill Height", which
        /// RevitFacts.FindParameter looks for on the instance and then on the type. The type
        /// fallback is for the families that keep it there, and writing it moves every other
        /// instance of that type, which is why the caller is told which one was written.
        /// </summary>
        private static Parameter SillParameter(
            Document document,
            FamilyInstance instance,
            out bool fromType)
        {
            fromType = false;

            Parameter onInstance = instance.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
            if (onInstance != null)
            {
                return onInstance;
            }

            return RevitFacts.FindParameter(document, instance, "Sill Height", out fromType);
        }

        private static void ApplySillHeight(
            Document document,
            FamilyInstance instance,
            double sill,
            Dictionary<string, object> report)
        {
            bool fromType;
            Parameter parameter = SillParameter(document, instance, out fromType);

            if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.Double)
            {
                report["sillHeightOn"] = null;
                report["sillHeightSkipped"] = "This family exposes no writable Sill Height on the "
                    + "instance or on its type, so \"sillHeight\" was not applied. The sill "
                    + "reported is the one Revit gave the instance.";

                return;
            }

            RevitWrite.InTransaction(document, "Set sill height", delegate
            {
                parameter.Set(sill);
            });

            report["sillHeightOn"] = fromType ? "type" : "instance";
        }

        private static object ReadSillHeight(Document document, FamilyInstance instance)
        {
            bool fromType;
            Parameter parameter = SillParameter(document, instance, out fromType);

            if (parameter == null || parameter.StorageType != StorageType.Double)
            {
                return null;
            }

            return parameter.AsDouble();
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
