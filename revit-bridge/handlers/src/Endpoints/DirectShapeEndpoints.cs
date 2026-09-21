using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcpBridge.Endpoints
{
    /// <summary>
    /// Geometry built straight into the project document, with no family and no family template
    /// behind it.
    ///
    /// This exists because family content is an optional Autodesk download: a Revit install can -
    /// and on the machine this was written for, does - have zero .rfa families loaded and zero .rft
    /// family templates, which leaves families/place with nothing to place and no way to author a
    /// replacement. DirectShape needs neither. GeometryCreationUtilities builds the solids,
    /// DirectShape.CreateElement hangs them off a real Revit category, and the result is a proper
    /// element: visible, selectable, tagged with a category, and schedulable.
    ///
    /// Every element built here gets a real DirectShapeType, reused across the batch and across
    /// calls. Without one "Edit Type" in the Properties palette is dead, the elements cannot be
    /// scheduled or filtered by type, and no type parameter exists at all - see ResolveTypeId.
    ///
    /// Material is decided before the geometry is built, never after: a DirectShape carries its
    /// material on each Solid, so it comes from the SolidOptions handed to
    /// GeometryCreationUtilities. That is why "materialId" is an argument of these endpoints and
    /// not something materials/assign can fix afterwards.
    ///
    /// All lengths and coordinates are Revit internal units (decimal feet), unconverted.
    /// </summary>
    internal static class DirectShapeEndpoints
    {
        /// <summary>Defaults for planting/place, in feet - a 20 ft tree over a 8 ft trunk.</summary>
        private const double DefaultTrunkHeight = 8.0;
        private const double DefaultTrunkRadius = 0.5;
        private const double DefaultCrownRadius = 6.0;

        /// <summary>Default pipe radius for pipes/create, in feet - about 50 mm.</summary>
        private const double DefaultPipeRadius = 0.08;

        /// <summary>Defaults for sprinklers/place, in feet - a head 0.5 ft proud of the ground.</summary>
        private const double DefaultSprinklerRadius = 0.15;
        private const double DefaultSprinklerHeight = 0.5;

        /// <summary>
        /// Body: {category, name?, typeName?, materialId?, materialName?, comments?, mark?,
        /// shapes: [primitive | {kind: "group", name?, parts: [primitive]}]}.
        ///
        /// One DirectShape element per entry in "shapes"; a "group" entry is still one element, it
        /// just carries several solids - which is how a tree becomes trunk + crown in a single
        /// schedulable element.
        ///
        /// Any entry may carry its own "name", "comments" and "mark"; the top-level ones are the
        /// fallback for every entry that does not. See Build for why that matters.
        ///
        /// One TransactionGroup for the whole batch - one Ctrl+Z - with one inner transaction per
        /// entry, so a solid Revit refuses to build rolls back alone and lands in "failed".
        /// </summary>
        internal static object CreateDirectShapes(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            string categoryName = JsonBody.RequireString(body, "category");
            string defaultName = JsonBody.OptionalString(body, "name");
            string typeName = JsonBody.OptionalString(body, "typeName");
            string comments = JsonBody.OptionalString(body, "comments");
            string mark = JsonBody.OptionalString(body, "mark");
            JsonElement shapes = JsonBody.RequireArray(body, "shapes");

            ElementId categoryId = ResolveCategoryId(document, categoryName);
            SolidOptions material = MaterialOptions(document, body);

            List<JsonElement> requests = Entries(shapes);

            if (requests.Count == 0)
            {
                throw BridgeException.BadRequest("\"shapes\" must contain at least one shape.");
            }

            return RevitWrite.InGroup(document, "MCP: create direct shapes", delegate
            {
                return Build(
                    document,
                    categoryId,
                    categoryName,
                    defaultName,
                    typeName,
                    requests,
                    null,
                    null,
                    comments,
                    mark,
                    material);
            });
        }

        /// <summary>
        /// Body: {points: [{x, y, z?, comments?, mark?}], trunkHeight?, trunkRadius?, crownRadius?,
        /// name?, typeName?, materialId?, materialName?, comments?, mark?}.
        ///
        /// One DirectShape per point in the Planting category, each a trunk cylinder with a crown
        /// sphere sitting on top of it - the sphere's underside touches the top of the trunk, so a
        /// default tree is 8 + 2 * 6 = 20 ft tall. Defaults are feet: trunkHeight 8, trunkRadius
        /// 0.5, crownRadius 6.
        ///
        /// A point may carry its own "name", "comments" and "mark", which is how one call plants a
        /// mixed grove that schedules as a species breakdown rather than "Planting: 38".
        /// </summary>
        internal static object PlantPlanting(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            JsonElement points = JsonBody.RequireArray(body, "points");
            double trunkHeight = JsonBody.OptionalDouble(body, "trunkHeight", DefaultTrunkHeight);
            double trunkRadius = JsonBody.OptionalDouble(body, "trunkRadius", DefaultTrunkRadius);
            double crownRadius = JsonBody.OptionalDouble(body, "crownRadius", DefaultCrownRadius);
            string name = JsonBody.OptionalString(body, "name");
            string typeName = JsonBody.OptionalString(body, "typeName");
            string comments = JsonBody.OptionalString(body, "comments");
            string mark = JsonBody.OptionalString(body, "mark");

            if (trunkHeight <= 0 || trunkRadius <= 0 || crownRadius <= 0)
            {
                throw BridgeException.BadRequest(
                    "\"trunkHeight\", \"trunkRadius\" and \"crownRadius\" must all be greater than "
                        + "zero (decimal feet).");
            }

            List<XYZ> locations = RevitFacts.ReadPoints(points, "points", false, 0.0);
            if (locations.Count == 0)
            {
                throw BridgeException.BadRequest("\"points\" must contain at least one point.");
            }

            ElementId categoryId = ResolveCategoryId(document, "Planting");
            SolidOptions material = MaterialOptions(document, body);

            return RevitWrite.InGroup(document, "MCP: place planting", delegate
            {
                List<List<Solid>> trees = new List<List<Solid>>();

                foreach (XYZ location in locations)
                {
                    List<Solid> tree = new List<Solid>();
                    tree.Add(Cylinder(location, trunkRadius, trunkHeight, material));

                    XYZ crownCentre = new XYZ(
                        location.X,
                        location.Y,
                        location.Z + trunkHeight + crownRadius);

                    tree.Add(Sphere(crownCentre, crownRadius, material));
                    trees.Add(tree);
                }

                return Build(
                    document,
                    categoryId,
                    "Planting",
                    name == null ? "Tree" : name,
                    typeName,
                    Entries(points),
                    trees,
                    locations,
                    comments,
                    mark,
                    material);
            });
        }

        /// <summary>
        /// Body: {category?, name?, typeName?, materialId?, materialName?, comments?, mark?,
        /// runs: [{points: [{x, y, z?}], radius?, name?, comments?, mark?}]}.
        ///
        /// Irrigation, drainage and anything else that is a run of pipe. No MEP family is needed
        /// and none is used: each run becomes one DirectShape built from a cylinder per segment
        /// between consecutive points. A cylinder per segment on purpose - a real sweep along a
        /// polyline is a pile of failure modes for a result nobody can tell apart at 1:100.
        ///
        /// "category" defaults to PipeCurves when DirectShape.IsValidCategoryId accepts it in this
        /// document and GenericModel when it does not; the response says which one each element
        /// ended up in. "radius" is per run and defaults to 0.08 ft.
        ///
        /// A malformed run is a BAD_REQUEST for the whole call rather than one entry in "failed",
        /// exactly as it is for planting/place: the solids are built before anything is written.
        /// </summary>
        internal static object CreatePipes(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            string categoryName = JsonBody.OptionalString(body, "category");
            string name = JsonBody.OptionalString(body, "name");
            string typeName = JsonBody.OptionalString(body, "typeName");
            string comments = JsonBody.OptionalString(body, "comments");
            string mark = JsonBody.OptionalString(body, "mark");
            JsonElement runs = JsonBody.RequireArray(body, "runs");

            SolidOptions material = MaterialOptions(document, body);

            ElementId categoryId;
            if (categoryName != null)
            {
                categoryId = ResolveCategoryId(document, categoryName);
            }
            else
            {
                categoryId = DefaultCategoryId(document, "PipeCurves", out categoryName);
            }

            List<JsonElement> requests = Entries(runs);

            if (requests.Count == 0)
            {
                throw BridgeException.BadRequest("\"runs\" must contain at least one run.");
            }

            List<List<Solid>> prebuilt = new List<List<Solid>>();

            for (int index = 0; index < requests.Count; index++)
            {
                string label = "runs[" + index + "]";

                JsonElement points = JsonBody.RequireArray(requests[index], "points");
                double radius = JsonBody.OptionalDouble(requests[index], "radius", DefaultPipeRadius);

                if (radius <= 0)
                {
                    throw BridgeException.BadRequest(
                        "\"" + label + ".radius\" must be greater than zero (decimal feet).");
                }

                List<XYZ> route = RevitFacts.ReadPoints(points, label + ".points", false, 0.0);

                if (route.Count < 2)
                {
                    throw BridgeException.BadRequest(
                        "\"" + label + ".points\" needs at least 2 points to make a run; got "
                            + route.Count + ".");
                }

                List<Solid> segments = new List<Solid>();

                for (int step = 0; step < route.Count - 1; step++)
                {
                    if (route[step].DistanceTo(route[step + 1]) < document.Application.ShortCurveTolerance)
                    {
                        throw BridgeException.BadRequest(
                            "\"" + label + ".points\" segment " + step + " is shorter than Revit's "
                                + "short-curve tolerance, so it has no length to sweep along.");
                    }

                    segments.Add(Segment(route[step], route[step + 1], radius, material));
                }

                prebuilt.Add(segments);
            }

            return RevitWrite.InGroup(document, "MCP: create pipes", delegate
            {
                return Build(
                    document,
                    categoryId,
                    categoryName,
                    name == null ? "Pipe" : name,
                    typeName,
                    requests,
                    prebuilt,
                    null,
                    comments,
                    mark,
                    material);
            });
        }

        /// <summary>
        /// Body: {points: [{x, y, z?, name?, comments?, mark?}], radius?, height?, name?,
        /// typeName?, materialId?, materialName?, comments?, mark?}.
        ///
        /// One small DirectShape cylinder per point - a sprinkler head standing proud of the
        /// ground. The category is Sprinklers when DirectShape.IsValidCategoryId accepts it in this
        /// document and GenericModel when it does not, and the response says which. Defaults are
        /// feet: radius 0.15, height 0.5.
        /// </summary>
        internal static object PlaceSprinklers(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            JsonElement points = JsonBody.RequireArray(body, "points");
            double radius = JsonBody.OptionalDouble(body, "radius", DefaultSprinklerRadius);
            double height = JsonBody.OptionalDouble(body, "height", DefaultSprinklerHeight);
            string name = JsonBody.OptionalString(body, "name");
            string typeName = JsonBody.OptionalString(body, "typeName");
            string comments = JsonBody.OptionalString(body, "comments");
            string mark = JsonBody.OptionalString(body, "mark");

            if (radius <= 0 || height <= 0)
            {
                throw BridgeException.BadRequest(
                    "\"radius\" and \"height\" must both be greater than zero (decimal feet).");
            }

            List<XYZ> locations = RevitFacts.ReadPoints(points, "points", false, 0.0);
            if (locations.Count == 0)
            {
                throw BridgeException.BadRequest("\"points\" must contain at least one point.");
            }

            string categoryName;
            ElementId categoryId = DefaultCategoryId(document, "Sprinklers", out categoryName);
            SolidOptions material = MaterialOptions(document, body);

            return RevitWrite.InGroup(document, "MCP: place sprinklers", delegate
            {
                List<List<Solid>> heads = new List<List<Solid>>();

                foreach (XYZ location in locations)
                {
                    heads.Add(new List<Solid> { Cylinder(location, radius, height, material) });
                }

                return Build(
                    document,
                    categoryId,
                    categoryName,
                    name == null ? "Sprinkler" : name,
                    typeName,
                    Entries(points),
                    heads,
                    locations,
                    comments,
                    mark,
                    material);
            });
        }

        /// <summary>
        /// The shared body of every endpoint here: one element per entry, one inner transaction
        /// each, per-entry catch. <paramref name="requests"/> is always the caller's array - the
        /// shapes, the points, the runs - and is what per-entry "name", "comments" and "mark" are
        /// read from. <paramref name="prebuilt"/> carries the solids when the endpoint made them
        /// itself; when it is null they are read out of the requests.
        ///
        /// "comments" and "mark" are written to ALL_MODEL_INSTANCE_COMMENTS and ALL_MODEL_MARK,
        /// which is the difference between a planting schedule that reads "Planting: 38" and one
        /// that breaks down by species. The values are read back off the element into the response,
        /// so a caller knows what actually landed.
        ///
        /// Every element also gets a DirectShapeType, named <paramref name="typeName"/> when the
        /// caller asked for one and after the element's own name when it did not - so a batch that
        /// already names its species gets a type per species for free, and one that names nothing
        /// gets a type named after the category. Types are shared across the batch: see
        /// ResolveTypeId.
        /// </summary>
        private static object Build(
            Document document,
            ElementId categoryId,
            string categoryName,
            string defaultName,
            string typeName,
            List<JsonElement> requests,
            List<List<Solid>> prebuilt,
            List<XYZ> locations,
            string defaultComments,
            string defaultMark,
            SolidOptions material)
        {
            List<object> created = new List<object>();
            List<object> failed = new List<object>();

            // One entry per distinct type name, filled only once a type has really been committed:
            // a type created in a transaction that then rolled back never existed.
            Dictionary<string, ElementId> types =
                new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);

            int count = requests.Count;

            for (int index = 0; index < count; index++)
            {
                string name = defaultName;
                string comments = defaultComments;
                string mark = defaultMark;
                List<Solid> solids;

                try
                {
                    JsonElement request = requests[index];

                    string entryName = JsonBody.OptionalString(request, "name");
                    if (entryName != null)
                    {
                        name = entryName;
                    }

                    string entryComments = JsonBody.OptionalString(request, "comments");
                    if (entryComments != null)
                    {
                        comments = entryComments;
                    }

                    string entryMark = JsonBody.OptionalString(request, "mark");
                    if (entryMark != null)
                    {
                        mark = entryMark;
                    }

                    solids = prebuilt != null
                        ? prebuilt[index]
                        : ReadSolids(document, request, "shapes[" + index + "]", material);
                }
                catch (BridgeException ex)
                {
                    // A malformed or impossible primitive is this entry's problem, not the batch's.
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

                // Never null: an unnamed shape in an unnamed batch is still typed, after the
                // category it was asked for.
                string entryTypeName = typeName != null
                    ? typeName
                    : (name != null ? name : categoryName);

                DirectShape shape = null;
                ElementId typeId = null;

                try
                {
                    RevitWrite.InTransaction(document, "Create direct shape", delegate
                    {
                        typeId = ResolveTypeId(document, categoryId, entryTypeName, types);

                        shape = DirectShape.CreateElement(document, categoryId);

                        // Before SetShape, and once only: DirectShape.SetTypeId is documented as
                        // settable a single time, and the instance geometry is what is wanted on
                        // the element, not whatever the type does or does not carry.
                        shape.SetTypeId(typeId);

                        List<GeometryObject> geometry = new List<GeometryObject>();
                        foreach (Solid solid in solids)
                        {
                            geometry.Add(solid);
                        }

                        shape.SetShape(geometry);

                        if (name != null)
                        {
                            shape.SetName(name);
                        }

                        SetText(shape, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, comments);
                        SetText(shape, BuiltInParameter.ALL_MODEL_MARK, mark);
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

                // Committed, so the next entry asking for this type reuses it rather than
                // collecting the document again or creating a second one with the same name.
                types[entryTypeName] = typeId;

                Dictionary<string, object> row = new Dictionary<string, object>
                {
                    { "id", shape.Id.Value },
                    { "category", categoryName },
                    { "name", name },
                    { "typeId", typeId.Value },
                    { "typeName", entryTypeName },
                };

                if (material != null)
                {
                    row["materialId"] = material.MaterialId.Value;
                }

                if (locations != null)
                {
                    row["x"] = locations[index].X;
                    row["y"] = locations[index].Y;
                }

                if (comments != null)
                {
                    row["comments"] = ReadText(shape, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                }

                if (mark != null)
                {
                    row["mark"] = ReadText(shape, BuiltInParameter.ALL_MODEL_MARK);
                }

                created.Add(row);
            }

            return new Dictionary<string, object>
            {
                { "created", created },
                { "failed", failed },
            };
        }

        /// <summary>The entries of a JSON array, indexable alongside the solids built from them.</summary>
        private static List<JsonElement> Entries(JsonElement array)
        {
            List<JsonElement> entries = new List<JsonElement>();

            foreach (JsonElement entry in array.EnumerateArray())
            {
                entries.Add(entry);
            }

            return entries;
        }

        /// <summary>
        /// Writes a built-in text parameter, when the caller asked for one and the element has it
        /// writable. Nothing is invented and nothing is thrown: what the element ended up carrying
        /// is read straight back into the response by ReadText.
        /// </summary>
        private static void SetText(Element element, BuiltInParameter parameter, string value)
        {
            if (value == null)
            {
                return;
            }

            Parameter target = element.get_Parameter(parameter);
            if (target == null || target.IsReadOnly)
            {
                return;
            }

            target.Set(value);
        }

        private static string ReadText(Element element, BuiltInParameter parameter)
        {
            Parameter source = element.get_Parameter(parameter);
            if (source == null)
            {
                return null;
            }

            return source.AsString();
        }

        /// <summary>
        /// The DirectShapeType named <paramref name="typeName"/> in this category: the one made
        /// earlier in this batch, the one already in the document, or a new one - in that order.
        ///
        /// Looked up before it is created on purpose. A type per element would mean 140 identical
        /// DirectShapeTypes for one grove, which is worse than the no-type bug this fixes; the
        /// names are matched case-insensitively like every other name in this bridge, and within
        /// one category, because that is the pair a DirectShapeType is identified by.
        ///
        /// Must be called inside a transaction: DirectShapeType.Create is a model change.
        /// </summary>
        private static ElementId ResolveTypeId(
            Document document,
            ElementId categoryId,
            string typeName,
            Dictionary<string, ElementId> types)
        {
            ElementId known;
            if (types.TryGetValue(typeName, out known))
            {
                return known;
            }

            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(DirectShapeType)))
            {
                Category category = element.Category;
                if (category == null || category.Id != categoryId)
                {
                    continue;
                }

                if (string.Equals(RevitFacts.SafeName(element), typeName, StringComparison.OrdinalIgnoreCase))
                {
                    return element.Id;
                }
            }

            return DirectShapeType.Create(document, typeName, categoryId).Id;
        }

        /// <summary>
        /// The SolidOptions every solid of this request is built with, or null when the caller
        /// named no material.
        ///
        /// A DirectShape's material is a property of its Solid, so it has to be decided here -
        /// before GeometryCreationUtilities runs - rather than written to the element afterwards.
        /// That is the whole reason these endpoints take a material at all; see materials/assign
        /// for what can and cannot be done to an element that already exists.
        /// </summary>
        private static SolidOptions MaterialOptions(Document document, JsonElement body)
        {
            ElementId materialId = MaterialEndpoints.OptionalMaterialId(document, body);
            if (materialId == null)
            {
                return null;
            }

            // InvalidElementId for the graphics style: the caller asked for a material, not for a
            // subcategory, and the element's own category already supplies the graphics.
            return new SolidOptions(materialId, ElementId.InvalidElementId);
        }

        /// <summary>One entry: a primitive, or a "group" of them making one multi-solid element.</summary>
        private static List<Solid> ReadSolids(
            Document document,
            JsonElement request,
            string label,
            SolidOptions material)
        {
            string kind = JsonBody.RequireString(request, "kind");

            if (string.Equals(kind, "group", StringComparison.OrdinalIgnoreCase))
            {
                JsonElement parts = JsonBody.RequireArray(request, "parts");

                List<Solid> solids = new List<Solid>();
                int index = 0;

                foreach (JsonElement part in parts.EnumerateArray())
                {
                    solids.Add(ReadPrimitive(document, part, label + ".parts[" + index + "]", material));
                    index++;
                }

                if (solids.Count == 0)
                {
                    throw BridgeException.BadRequest("\"" + label + ".parts\" must contain at least one shape.");
                }

                return solids;
            }

            return new List<Solid> { ReadPrimitive(document, request, label, material) };
        }

        private static Solid ReadPrimitive(
            Document document,
            JsonElement shape,
            string label,
            SolidOptions material)
        {
            string kind = JsonBody.RequireString(shape, "kind");

            switch (kind.ToLowerInvariant())
            {
                case "cylinder":
                    return Cylinder(
                        RevitFacts.RequirePoint(shape, "base"),
                        RequirePositive(shape, "radius", label),
                        RequirePositive(shape, "height", label),
                        material);

                case "box":
                    return Box(
                        document,
                        RevitFacts.RequirePoint(shape, "min"),
                        RevitFacts.RequirePoint(shape, "max"),
                        label,
                        material);

                case "sphere":
                    return Sphere(
                        RevitFacts.RequirePoint(shape, "center"),
                        RequirePositive(shape, "radius", label),
                        material);

                case "cone":
                    return Cone(
                        RevitFacts.RequirePoint(shape, "base"),
                        RequirePositive(shape, "radius", label),
                        RequirePositive(shape, "height", label),
                        material);

                case "extrusion":
                    return Extrusion(
                        document,
                        JsonBody.RequireArray(shape, "profile"),
                        JsonBody.OptionalDouble(shape, "baseZ", 0.0),
                        RequirePositive(shape, "height", label),
                        label,
                        material);

                default:
                    throw BridgeException.BadRequest(
                        "\"" + label + ".kind\" is \"" + kind + "\", which is not a shape this bridge "
                            + "builds. Use cylinder, box, sphere, cone, extrusion or group.");
            }
        }

        private static Solid Cylinder(XYZ basePoint, double radius, double height, SolidOptions material)
        {
            return Extrude(
                new List<CurveLoop> { Circle(basePoint, radius) },
                XYZ.BasisZ,
                height,
                material);
        }

        /// <summary>
        /// GeometryCreationUtilities' two forms of the same call: with SolidOptions when the caller
        /// named a material, and without when it did not. Passing SolidOptions carrying
        /// InvalidElementId would probably mean the same thing, but "probably" is not a reason to
        /// change how every existing call builds its geometry.
        /// </summary>
        private static Solid Extrude(
            List<CurveLoop> loops,
            XYZ direction,
            double distance,
            SolidOptions material)
        {
            if (material == null)
            {
                return GeometryCreationUtilities.CreateExtrusionGeometry(loops, direction, distance);
            }

            return GeometryCreationUtilities.CreateExtrusionGeometry(loops, direction, distance, material);
        }

        private static Solid Revolve(
            Frame frame,
            List<CurveLoop> loops,
            double startAngle,
            double endAngle,
            SolidOptions material)
        {
            if (material == null)
            {
                return GeometryCreationUtilities.CreateRevolvedGeometry(frame, loops, startAngle, endAngle);
            }

            return GeometryCreationUtilities.CreateRevolvedGeometry(frame, loops, startAngle, endAngle, material);
        }

        /// <summary>
        /// A cylinder from one point to another, at any angle: the circle is built in the plane the
        /// segment is normal to and extruded along it. One per segment of a run is what stands in
        /// for a swept pipe here.
        /// </summary>
        private static Solid Segment(XYZ start, XYZ end, double radius, SolidOptions material)
        {
            XYZ axis = end - start;
            double length = axis.GetLength();
            XYZ direction = axis.Normalize();

            // Any vector across the axis will do for the circle's x; BasisZ gives one for every
            // segment that is not itself vertical, where the cross product collapses.
            XYZ across = direction.CrossProduct(XYZ.BasisZ);
            if (across.GetLength() < ThinVector)
            {
                across = direction.CrossProduct(XYZ.BasisX);
            }

            across = across.Normalize();

            // across x up is the direction, so the loop's normal is the way the extrusion goes.
            XYZ up = direction.CrossProduct(across);

            CurveLoop loop = new CurveLoop();
            loop.Append(Arc.Create(start, radius, 0.0, Math.PI, across, up));
            loop.Append(Arc.Create(start, radius, Math.PI, 2.0 * Math.PI, across, up));

            return Extrude(new List<CurveLoop> { loop }, direction, length, material);
        }

        private static Solid Box(Document document, XYZ min, XYZ max, string label, SolidOptions material)
        {
            double height = max.Z - min.Z;
            if (height <= 0)
            {
                throw BridgeException.BadRequest(
                    "\"" + label + ".max\".z must be above \"" + label + ".min\".z.");
            }

            if (Math.Abs(max.X - min.X) < document.Application.ShortCurveTolerance
                || Math.Abs(max.Y - min.Y) < document.Application.ShortCurveTolerance)
            {
                throw BridgeException.BadRequest(
                    "\"" + label + "\" is flatter than Revit's short-curve tolerance in x or y, so "
                        + "it has no volume.");
            }

            double lowX = Math.Min(min.X, max.X);
            double highX = Math.Max(min.X, max.X);
            double lowY = Math.Min(min.Y, max.Y);
            double highY = Math.Max(min.Y, max.Y);

            List<XYZ> ring = new List<XYZ>
            {
                new XYZ(lowX, lowY, min.Z),
                new XYZ(highX, lowY, min.Z),
                new XYZ(highX, highY, min.Z),
                new XYZ(lowX, highY, min.Z),
            };

            return Extrude(
                new List<CurveLoop> { RevitFacts.PolygonLoop(document, ring, label) },
                XYZ.BasisZ,
                height,
                material);
        }

        /// <summary>
        /// A half-disc in the global XZ plane through the centre, revolved a full turn about the
        /// vertical. CreateRevolvedGeometry wants the profile on the +x side of the frame's z axis,
        /// which is what the arc from the bottom pole to the top pole gives.
        /// </summary>
        private static Solid Sphere(XYZ centre, double radius, SolidOptions material)
        {
            XYZ bottom = new XYZ(centre.X, centre.Y, centre.Z - radius);
            XYZ top = new XYZ(centre.X, centre.Y, centre.Z + radius);

            Arc profile = Arc.Create(centre, radius, -Math.PI / 2.0, Math.PI / 2.0, XYZ.BasisX, XYZ.BasisZ);

            CurveLoop loop = new CurveLoop();
            loop.Append(profile);
            loop.Append(Line.CreateBound(top, bottom));

            Frame frame = new Frame(centre, XYZ.BasisX, XYZ.BasisY, XYZ.BasisZ);

            return Revolve(frame, new List<CurveLoop> { loop }, 0.0, 2.0 * Math.PI, material);
        }

        /// <summary>A triangle with two vertices on the axis, revolved a full turn.</summary>
        private static Solid Cone(XYZ basePoint, double radius, double height, SolidOptions material)
        {
            XYZ rim = new XYZ(basePoint.X + radius, basePoint.Y, basePoint.Z);
            XYZ apex = new XYZ(basePoint.X, basePoint.Y, basePoint.Z + height);

            CurveLoop loop = new CurveLoop();
            loop.Append(Line.CreateBound(basePoint, rim));
            loop.Append(Line.CreateBound(rim, apex));
            loop.Append(Line.CreateBound(apex, basePoint));

            Frame frame = new Frame(basePoint, XYZ.BasisX, XYZ.BasisY, XYZ.BasisZ);

            return Revolve(frame, new List<CurveLoop> { loop }, 0.0, 2.0 * Math.PI, material);
        }

        private static Solid Extrusion(
            Document document,
            JsonElement profile,
            double baseZ,
            double height,
            string label,
            SolidOptions material)
        {
            List<XYZ> ring = RevitFacts.ReadPoints(profile, label + ".profile", true, baseZ);

            return Extrude(
                new List<CurveLoop> { RevitFacts.PolygonLoop(document, ring, label + ".profile") },
                XYZ.BasisZ,
                height,
                material);
        }

        /// <summary>
        /// Two half arcs, never one closed curve: CreateExtrusionGeometry refuses a loop made of a
        /// single closed curve.
        /// </summary>
        private static CurveLoop Circle(XYZ centre, double radius)
        {
            XYZ right = new XYZ(centre.X + radius, centre.Y, centre.Z);
            XYZ left = new XYZ(centre.X - radius, centre.Y, centre.Z);

            CurveLoop loop = new CurveLoop();
            loop.Append(Arc.Create(right, left, new XYZ(centre.X, centre.Y + radius, centre.Z)));
            loop.Append(Arc.Create(left, right, new XYZ(centre.X, centre.Y - radius, centre.Z)));

            return loop;
        }

        /// <summary>
        /// A BuiltInCategory without its OST_ prefix, matched case-insensitively. The prefix is
        /// accepted too, because a caller that already knows the enum name should not be corrected.
        /// </summary>
        private static ElementId ResolveCategoryId(Document document, string categoryName)
        {
            string enumName = categoryName.StartsWith("OST_", StringComparison.OrdinalIgnoreCase)
                ? categoryName
                : "OST_" + categoryName;

            BuiltInCategory builtIn;
            if (Enum.TryParse<BuiltInCategory>(enumName, true, out builtIn))
            {
                ElementId categoryId = new ElementId(builtIn);

                if (DirectShape.IsValidCategoryId(categoryId, document))
                {
                    return categoryId;
                }

                throw BridgeException.BadRequest(
                    "Category \"" + categoryName + "\" exists but cannot hold a DirectShape. Try one "
                        + "of: " + string.Join(", ", ExampleCategories) + ".");
            }

            throw BridgeException.BadRequest(
                "Unknown category \"" + categoryName + "\". Pass a BuiltInCategory name without its "
                    + "OST_ prefix, for example: " + string.Join(", ", ExampleCategories) + ".");
        }

        /// <summary>
        /// The preferred category when DirectShape.IsValidCategoryId accepts it in this document,
        /// and GenericModel when it does not. Which categories can hold a DirectShape is Revit's
        /// own rule and it is not published anywhere, so this asks rather than assumes - and every
        /// row of the response says which category the element actually ended up in.
        /// </summary>
        private static ElementId DefaultCategoryId(Document document, string preferred, out string categoryName)
        {
            BuiltInCategory builtIn;
            if (Enum.TryParse<BuiltInCategory>("OST_" + preferred, true, out builtIn))
            {
                ElementId categoryId = new ElementId(builtIn);

                if (DirectShape.IsValidCategoryId(categoryId, document))
                {
                    categoryName = preferred;
                    return categoryId;
                }
            }

            categoryName = "GenericModel";
            return ResolveCategoryId(document, categoryName);
        }

        /// <summary>Below this a cross product is noise rather than a direction.</summary>
        private const double ThinVector = 1e-9;

        private static readonly string[] ExampleCategories =
        {
            "Planting",
            "LightingFixtures",
            "Furniture",
            "Site",
            "Walls",
            "GenericModel",
        };

        private static double RequirePositive(JsonElement shape, string name, string label)
        {
            double value = JsonBody.RequireDouble(shape, name);

            if (value <= 0)
            {
                throw BridgeException.BadRequest(
                    "\"" + label + "." + name + "\" must be greater than zero (decimal feet).");
            }

            return value;
        }
    }
}
