using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcpBridge.Endpoints
{
    /// <summary>
    /// Views, schedules and what puts them on a sheet. This is the half that makes a set of sheets
    /// stop being empty rectangles.
    ///
    /// Same rules as the other write endpoints: one request is one TransactionGroup and therefore
    /// one Ctrl+Z, and every coordinate is Revit internal units (decimal feet), unconverted. Sheet
    /// points are feet on the paper, not model feet - a 594 x 420 mm sheet is 1.95 x 1.38.
    /// </summary>
    internal static class ViewEndpoints
    {
        /// <summary>Pixels across an exported image when the caller asks for no size.</summary>
        private const int DefaultImageWidth = 1600;

        /// <summary>
        /// Body: {}. Every non-template view, with whether it is already on a sheet - which is what
        /// decides whether sheets/place-view can take it.
        ///
        /// Sheets themselves are left out: they are views in the API, /revit-mcp/sheets already
        /// lists them, and a sheet is never something you place on a sheet.
        /// </summary>
        internal static object Views(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            HashSet<long> placed = PlacedViewIds(document);

            List<View> views = new List<View>();
            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(View)))
            {
                View view = element as View;
                if (view == null || view.IsTemplate || view is ViewSheet)
                {
                    continue;
                }

                views.Add(view);
            }

            views.Sort(delegate (View left, View right)
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
            foreach (View view in views)
            {
                Dictionary<string, object> row = new Dictionary<string, object>
                {
                    { "id", view.Id.Value },
                    { "name", RevitFacts.SafeName(view) },
                    { "viewType", view.ViewType.ToString() },
                    { "isTemplate", view.IsTemplate },
                    { "isPlacedOnSheet", placed.Contains(view.Id.Value) },
                };

                AddDirections(row, view);
                rows.Add(row);
            }

            return rows;
        }

        /// <summary>
        /// Body: {level, name, viewFamilyType?, scale?}. A plan on the named level - a floor plan
        /// unless "viewFamilyType" names another one.
        ///
        /// "viewFamilyType" is the NAME of a ViewFamilyType in this document - "Site", "Ceiling
        /// Plan", whatever the template calls it - not a ViewFamily enum name. That is deliberate:
        /// a Site plan is an ordinary FloorPlan-family type and only its name tells it apart from
        /// "Floor Plan", so matching on the family would make a Site plan unreachable. An unknown
        /// name is a BAD_REQUEST carrying the names that would have worked.
        ///
        /// A name Revit already has is not a failure: the bridge appends " 2", " 3", ... until one
        /// is free and tells the caller in "name" what it actually got. Half a phase of plans
        /// aborting because one name collided is worse than a suffix.
        /// </summary>
        internal static object CreatePlan(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            string levelName = JsonBody.RequireString(body, "level");
            string name = JsonBody.RequireString(body, "name");
            string viewFamilyTypeName = JsonBody.OptionalString(body, "viewFamilyType");
            int scale = JsonBody.OptionalInt(body, "scale", 0);

            Level level = RevitFacts.ResolveLevel(document, levelName);
            if (level == null)
            {
                throw BridgeException.BadRequest(
                    "Unknown level \"" + levelName + "\". Levels in this document: "
                        + string.Join(", ", RevitFacts.LevelNames(document)) + ".");
            }

            ElementId viewFamilyTypeId = ResolvePlanViewFamilyTypeId(document, viewFamilyTypeName);
            string resolvedTypeName = RevitFacts.SafeName(document.GetElement(viewFamilyTypeId));

            return RevitWrite.InGroup(document, "MCP: create plan view", delegate
            {
                Dictionary<string, object> created = new Dictionary<string, object>();

                RevitWrite.InTransaction(document, "Create plan view", delegate
                {
                    ViewPlan view = ViewPlan.Create(document, viewFamilyTypeId, level.Id);

                    ApplyName(document, view, name);
                    ApplyScale(view, scale);

                    created["id"] = view.Id.Value;
                    created["name"] = RevitFacts.SafeName(view);
                    created["viewType"] = view.ViewType.ToString();
                    created["viewFamilyType"] = resolvedTypeName;
                    created["level"] = level.Name;

                    // Read back off the view, not echoed: a view template on the type can override
                    // what was asked for, and the caller should see what it actually got.
                    created["scale"] = view.Scale;
                });

                return created;
            });
        }

        /// <summary>
        /// Body: {name, scale?}. A drafting view: linework and annotation that is not a view of the
        /// model at all, which is what a detail sheet is made of. Fill it with detail/lines and
        /// detail/text.
        ///
        /// Same unique-name handling as the other view endpoints: a taken name gets " 2", " 3", ...
        /// and the response says what the view is actually called.
        /// </summary>
        internal static object CreateDrafting(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            string name = JsonBody.RequireString(body, "name");
            int scale = JsonBody.OptionalInt(body, "scale", 0);

            ElementId viewFamilyTypeId = ResolveViewFamilyTypeId(document, ViewFamily.Drafting, "drafting");

            return RevitWrite.InGroup(document, "MCP: create drafting view", delegate
            {
                Dictionary<string, object> created = new Dictionary<string, object>();

                RevitWrite.InTransaction(document, "Create drafting view", delegate
                {
                    ViewDrafting view = ViewDrafting.Create(document, viewFamilyTypeId);

                    ApplyName(document, view, name);
                    ApplyScale(view, scale);

                    created["id"] = view.Id.Value;
                    created["name"] = RevitFacts.SafeName(view);
                    created["viewType"] = view.ViewType.ToString();
                    created["scale"] = view.Scale;
                });

                return created;
            });
        }

        /// <summary>
        /// Body: {name, eye?: {x, y, z}, target?: {x, y, z}, perspective?, scale?}. A 3D view -
        /// isometric by default, a perspective camera with {"perspective": true}.
        ///
        /// "eye" and "target" go together and aim the camera. ViewOrientation3D takes
        /// (eye, up, forward) and Revit requires up perpendicular to forward, so neither vector can
        /// be handed over raw - see Orientation for how the three are built. Omit both and the view
        /// keeps Revit's default orientation, which is the standard south-east isometric.
        ///
        /// The response carries "modelExtents": the bounding box of everything modelled in the
        /// document. A camera cannot be aimed without knowing where the model is, and there is no
        /// other endpoint that says - so creating a view once, reading the extents and creating the
        /// real one is the loop this is here to make possible. It also carries the view's own
        /// viewDirection / rightDirection / upDirection, so what the camera ended up doing is
        /// assertable without opening Revit and looking at it.
        ///
        /// Same unique-name handling as the other view endpoints: a taken name gets " 2", " 3", ...
        /// </summary>
        internal static object Create3D(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            string name = JsonBody.RequireString(body, "name");
            bool perspective = JsonBody.OptionalBool(body, "perspective", false);
            int scale = JsonBody.OptionalInt(body, "scale", 0);

            XYZ eye = null;
            XYZ target = null;

            JsonElement value;
            if (JsonBody.TryGet(body, "eye", out value))
            {
                eye = RevitFacts.ReadPoint(value, "eye");
            }

            if (JsonBody.TryGet(body, "target", out value))
            {
                target = RevitFacts.ReadPoint(value, "target");
            }

            if ((eye == null) != (target == null))
            {
                throw BridgeException.BadRequest(
                    "\"eye\" and \"target\" go together: a camera needs both a position and "
                        + "something to look at. Pass both, or neither to keep Revit's default "
                        + "orientation.");
            }

            if (eye != null && eye.DistanceTo(target) < document.Application.ShortCurveTolerance)
            {
                throw BridgeException.BadRequest(
                    "\"eye\" and \"target\" are the same point, so the camera has no direction to "
                        + "look in.");
            }

            ElementId viewFamilyTypeId =
                ResolveViewFamilyTypeId(document, ViewFamily.ThreeDimensional, "3D");

            Dictionary<string, object> extents = ModelExtents(document);

            return RevitWrite.InGroup(document, "MCP: create 3D view", delegate
            {
                Dictionary<string, object> created = new Dictionary<string, object>();

                RevitWrite.InTransaction(document, "Create 3D view", delegate
                {
                    View3D view = perspective
                        ? View3D.CreatePerspective(document, viewFamilyTypeId)
                        : View3D.CreateIsometric(document, viewFamilyTypeId);

                    ApplyName(document, view, name);

                    // A perspective view has no view scale - it is a camera, not a projection -
                    // and Revit refuses the setter on one. The response reports what it got.
                    if (!view.IsPerspective)
                    {
                        ApplyScale(view, scale);
                    }

                    if (eye != null)
                    {
                        view.SetOrientation(Orientation(eye, target));
                    }

                    created["id"] = view.Id.Value;
                    created["name"] = RevitFacts.SafeName(view);
                    created["viewType"] = view.ViewType.ToString();
                    created["isPerspective"] = view.IsPerspective;
                    created["scale"] = view.Scale;

                    AddDirections(created, view);
                });

                created["modelExtents"] = extents;
                return created;
            });
        }

        /// <summary>
        /// Body: {}. The legend views this document already has: [{id, name, scale}].
        ///
        /// Read-only, and that is the point: Revit cannot author a legend from nothing (see
        /// views/create-legend), so what is here is what a caller has to duplicate from.
        /// </summary>
        internal static object Legends(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            List<object> rows = new List<object>();

            foreach (View legend in LegendViews(document))
            {
                rows.Add(new Dictionary<string, object>
                {
                    { "id", legend.Id.Value },
                    { "name", RevitFacts.SafeName(legend) },
                    { "scale", legend.Scale },
                });
            }

            return rows;
        }

        /// <summary>
        /// Body: {name, fromLegendId?, scale?}. A legend view, made the only way the Revit API can
        /// make one: by duplicating a legend the document already has.
        ///
        /// There is no creation route for the FIRST legend, and this endpoint does not pretend
        /// otherwise. The API exposes no ViewLegend type at all; ViewPlan.Create documents that
        /// "the type needs to be a FloorPlan, CeilingPlan, AreaPlan, or StructuralPlan ViewType",
        /// and ViewDrafting.Create throws ArgumentException when "viewFamilyTypeId is not a valid
        /// ViewFamilyType for a drafting view" - so the Legend ViewFamilyType every document
        /// carries has nothing that accepts it. A document with no legend therefore answers
        /// NO_LEGEND_TO_DUPLICATE rather than quietly handing back a drafting view that only looks
        /// like one.
        ///
        /// "fromLegendId" picks the legend to copy; omitted, it is the first one in the document.
        /// The copy is made with ViewDuplicateOption.Duplicate, which brings the view and none of
        /// its contents - a new legend should be empty, not a copy of someone else's key.
        ///
        /// What can then go IN it: detail/lines and detail/text. LegendComponent - the element
        /// that shows a real family type at scale - has no creation API either, and there is no
        /// workaround for that one.
        /// </summary>
        internal static object CreateLegend(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            string name = JsonBody.RequireString(body, "name");
            int scale = JsonBody.OptionalInt(body, "scale", 0);

            View source;

            JsonElement fromLegend;
            if (JsonBody.TryGet(body, "fromLegendId", out fromLegend))
            {
                long fromLegendId = JsonBody.AsLong(fromLegend, "fromLegendId");

                source = document.GetElement(new ElementId(fromLegendId)) as View;
                if (source == null || source.ViewType != ViewType.Legend)
                {
                    throw BridgeException.BadRequest(
                        "Element " + fromLegendId + " is not a legend view in this document. Call "
                            + "/revit-mcp/views/legends for the ones that are.");
                }
            }
            else
            {
                List<View> legends = LegendViews(document);

                if (legends.Count == 0)
                {
                    throw new BridgeException(
                        409,
                        "NO_LEGEND_TO_DUPLICATE",
                        "This document has no legend view, and the Revit API cannot create the "
                            + "first one: there is no ViewLegend creation method, ViewPlan.Create "
                            + "takes only plan view family types, and ViewDrafting.Create refuses a "
                            + "Legend view family type. Make one legend in the Revit UI (View tab > "
                            + "Legends > Legend), or use a template that has one, and this endpoint "
                            + "will duplicate it from then on.");
                }

                source = legends[0];
            }

            if (!source.CanViewBeDuplicated(ViewDuplicateOption.Duplicate))
            {
                throw new BridgeException(
                    409,
                    "CANNOT_DUPLICATE",
                    "Revit will not duplicate legend \"" + RevitFacts.SafeName(source) + "\" ("
                        + source.Id.Value + ").");
            }

            return RevitWrite.InGroup(document, "MCP: create legend", delegate
            {
                Dictionary<string, object> created = new Dictionary<string, object>();

                RevitWrite.InTransaction(document, "Create legend", delegate
                {
                    ElementId copyId = source.Duplicate(ViewDuplicateOption.Duplicate);
                    View copy = (View)document.GetElement(copyId);

                    ApplyName(document, copy, name);
                    ApplyScale(copy, scale);

                    created["id"] = copy.Id.Value;
                    created["name"] = RevitFacts.SafeName(copy);
                    created["viewType"] = copy.ViewType.ToString();
                    created["sourceViewId"] = source.Id.Value;
                    created["scale"] = copy.Scale;
                });

                return created;
            });
        }

        /// <summary>
        /// Body: {name, origin: {x, y, z}, direction: {x, y}, width, height, depth, scale?}.
        ///
        /// The geometry, precisely, because this is the fiddly one:
        ///
        ///   origin    the point the section is centred on, in model feet. The cut plane passes
        ///             through it.
        ///   direction the horizontal direction the section LOOKS, in plan. Its z is ignored and it
        ///             is normalised, so {x: 0, y: 1} and {x: 0, y: 5} mean the same thing.
        ///   width     the extent across the view, along the section line, centred on origin.
        ///   height    the vertical extent, centred on origin. Up is always +Z.
        ///   depth     how far in front of origin, along direction, the view sees. The far clip
        ///             lands there.
        ///
        /// So the crop is width x height centred on origin, and the view volume runs from origin to
        /// origin + direction * depth.
        ///
        /// The transform is settled by MEASUREMENT against a live Revit 2027, not by reading
        /// RevitAPI.xml. Three sections were created with BasisZ = -direction, BasisX = direction x
        /// Z, and the created views' own frames were read back off the views:
        ///
        ///   asked {x: 0, y: 1}   ->  viewDirection {0, 1, 0}   rightDirection {-1, 0, 0}
        ///   asked {x: 0, y: -1}  ->  viewDirection {0, -1, 0}  rightDirection {1, 0, 0}
        ///   asked {x: 1, y: 0}   ->  viewDirection {1, 0, 0}   rightDirection {0, 1, 0}
        ///
        /// upDirection was {0, 0, 1} in all three. So Revit gave back viewDirection = -BasisZ and
        /// rightDirection = -BasisX: it turns the frame it is handed by 180 degrees about up.
        /// Since View.ViewDirection is "the direction towards the viewer", a viewDirection equal to
        /// the asked direction means those sections LOOKED the opposite way - backwards from what
        /// this endpoint promises. The right directions say it independently: asked {0, 1}, Revit
        /// reported right = {-1, 0, 0}, west, which is the right hand of a viewer facing SOUTH.
        ///
        /// Hence, measured: the view looks along the box's BasisZ and reports viewDirection =
        /// -BasisZ. To make a section that looks toward direction, hand it BasisZ = +direction -
        /// the caller's direction negated relative to what this code used to do.
        /// BoundingBoxXYZ.Transform: "The transform must always be right-handed and orthonormal",
        /// which then fixes BasisX = BasisY x BasisZ = Z x direction. Revit reports rightDirection
        /// = -BasisX = direction x Z: looking north (+Y) that is east (+X), which is what is on the
        /// right of a viewer facing north.
        ///
        /// Do not re-derive this from the docs or the Autodesk samples. CreateSection's remark
        /// that "the view direction of the resulting section will be sectionBox.Transform.BasisZ"
        /// uses "view direction" for the way the view LOOKS, which is the opposite of the
        /// View.ViewDirection property of the same name; and its remark that (right, up, view
        /// direction) is "left handed" reads either way depending on which of the two is meant.
        /// Taking those remarks to be about View.ViewDirection is precisely what put the sign the
        /// wrong way round here before. The three measurements above are the authority; the
        /// response carries the created view's viewDirection, rightDirection and upDirection so
        /// that any caller can re-measure it in one call.
        ///
        /// Min/Max are (-width/2, -height/2, 0) and (width/2, height/2, depth) in that frame: the
        /// near plane - the cut - passes through origin, and the far clip lands depth ahead of it,
        /// along BasisZ. That is the origin -> origin + direction * depth this endpoint promises.
        /// The pair is expressed in the box frame, so the depth region turns with BasisZ and always
        /// lands in front of the way the view looks.
        ///
        /// The depth interval runs 0..depth and never -depth..0, and BOTH ends of that are measured
        /// against the same request: origin (53, 40, 8.5), direction {x: 1, y: 0}, width 86,
        /// height 33, depth 8.
        ///
        ///   Min.Z = -depth, Max.Z = 0  ->  view 247769, model x 45..53, cut at 45
        ///   Min.Z = 0, Max.Z = depth   ->  view 247787, model x 53..61, cut at 53
        ///
        /// The first put the whole view volume BEHIND the asked origin and cut 8 feet short of it;
        /// the second is the contract, cutting on the asked origin and seeing depth ahead.
        ///
        /// Do not judge that from the local pair read back off a created view. Revit rewrites the
        /// frame into its own convention - corner origin, BasisZ turned back at the viewer, depth
        /// expressed -depth..0 - so 247769 and 247787 report the IDENTICAL local min (0, 0, -8) and
        /// max (86, 33, 0) while their model bounds sit 8 feet apart. Only the model-space numbers
        /// settle it, which is why the response carries requestedOrigin next to a measured
        /// cutPlaneOrigin, modelBounds and cropTransform read back off the created view.
        /// </summary>
        internal static object CreateSection(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            string name = JsonBody.RequireString(body, "name");
            XYZ origin = RevitFacts.RequirePoint(body, "origin");
            XYZ requested = RevitFacts.RequirePoint(body, "direction");
            double width = JsonBody.RequireDouble(body, "width");
            double height = JsonBody.RequireDouble(body, "height");
            double depth = JsonBody.RequireDouble(body, "depth");
            int scale = JsonBody.OptionalInt(body, "scale", 0);

            if (width <= 0 || height <= 0 || depth <= 0)
            {
                throw BridgeException.BadRequest(
                    "\"width\", \"height\" and \"depth\" must all be greater than zero (decimal feet).");
            }

            // Flat on purpose: a section that looks downhill is not what "direction" means here.
            XYZ flat = new XYZ(requested.X, requested.Y, 0.0);
            if (flat.GetLength() < document.Application.ShortCurveTolerance)
            {
                throw BridgeException.BadRequest(
                    "\"direction\" must have a non-zero x or y: it is the horizontal direction the "
                        + "section looks.");
            }

            XYZ look = flat.Normalize();
            XYZ up = XYZ.BasisZ;

            // Measured: Revit looks along BasisZ and reports viewDirection = -BasisZ, so BasisZ is
            // the way the section is asked to look, not its negation.
            // BasisX = BasisY x BasisZ = up x look. BoundingBoxXYZ.Transform must be right-handed,
            // and this is the vector that makes it so; Revit reports right = -BasisX = look x up.
            XYZ right = up.CrossProduct(look);

            Transform transform = Transform.Identity;
            transform.Origin = origin;
            transform.BasisX = right;
            transform.BasisY = up;
            transform.BasisZ = look;

            BoundingBoxXYZ sectionBox = new BoundingBoxXYZ();
            sectionBox.Transform = transform;
            sectionBox.Min = new XYZ(-width / 2.0, -height / 2.0, 0.0);
            sectionBox.Max = new XYZ(width / 2.0, height / 2.0, depth);

            ElementId viewFamilyTypeId = ResolveViewFamilyTypeId(document, ViewFamily.Section, "section");

            return RevitWrite.InGroup(document, "MCP: create section view", delegate
            {
                Dictionary<string, object> created = new Dictionary<string, object>();

                RevitWrite.InTransaction(document, "Create section view", delegate
                {
                    ViewSection view = ViewSection.CreateSection(document, viewFamilyTypeId, sectionBox);

                    ApplyName(document, view, name);
                    ApplyScale(view, scale);

                    created["id"] = view.Id.Value;
                    created["name"] = RevitFacts.SafeName(view);
                    created["viewType"] = view.ViewType.ToString();
                    created["scale"] = view.Scale;

                    // Echoed, not measured: the point the caller asked the cut plane to pass
                    // through, so that cutPlaneOrigin below can be compared against it in the same
                    // response without the caller having to hold on to the request.
                    created["requestedOrigin"] = Vector(origin);

                    // What Revit actually made of the transform. viewDirection is the direction
                    // towards the viewer, so a section asked to look at {x: 0, y: 1} reports
                    // {x: 0, y: -1, z: 0} - that is the assertion, not a bug.
                    //
                    // Before the sign fix above this came back as {x: 0, y: 1, z: 0}, which is how
                    // the bug was caught: the numbers are read off the created view, so a caller
                    // can catch it again the same way.
                    AddDirections(created, view);
                    AddCropReadback(created, view);
                });

                return created;
            });
        }

        /// <summary>
        /// Body: {viewId, name, detailing?}. "detailing" is a ViewDuplicateOption name - Duplicate
        /// (the default, geometry only), WithDetailing, or AsDependent.
        /// </summary>
        internal static object DuplicateView(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            long viewId = JsonBody.AsLong(RequireValue(body, "viewId"), "viewId");
            string name = JsonBody.RequireString(body, "name");
            string detailing = JsonBody.OptionalString(body, "detailing");

            View source = document.GetElement(new ElementId(viewId)) as View;
            if (source == null)
            {
                throw BridgeException.BadRequest(
                    "Element " + viewId + " is not a view in this document. Call /revit-mcp/views "
                        + "for the ones that are.");
            }

            ViewDuplicateOption option = ResolveDuplicateOption(detailing);

            if (!source.CanViewBeDuplicated(option))
            {
                throw new BridgeException(
                    409,
                    "CANNOT_DUPLICATE",
                    "Revit will not duplicate view \"" + RevitFacts.SafeName(source) + "\" (" + viewId
                        + ") with option " + option + ".");
            }

            return RevitWrite.InGroup(document, "MCP: duplicate view", delegate
            {
                Dictionary<string, object> created = new Dictionary<string, object>();

                RevitWrite.InTransaction(document, "Duplicate view", delegate
                {
                    ElementId copyId = source.Duplicate(option);
                    View copy = (View)document.GetElement(copyId);

                    ApplyName(document, copy, name);

                    created["id"] = copy.Id.Value;
                    created["name"] = RevitFacts.SafeName(copy);
                    created["viewType"] = copy.ViewType.ToString();
                    created["sourceViewId"] = viewId;
                    created["detailing"] = option.ToString();
                });

                return created;
            });
        }

        /// <summary>
        /// Body: {viewId, scale} for one, or {viewIds: [...], scale} for a batch - a whole phase of
        /// views re-scaled in one Ctrl+Z.
        ///
        /// "scale" is the denominator X in 1/X: 100 is 1:100. It is checked once, up front, with
        /// View.IsValidViewScale, which documents the range as 1 to 24,000.
        ///
        /// A view whose scale Revit will not set - a schedule, a sheet, a view template - comes
        /// back in "failed" with a code and a reason rather than failing the call, exactly as a
        /// refused placement does in sheets/place-view. Re-scaling 30 plans should not be lost
        /// because a schedule was in the list.
        ///
        /// A PERSPECTIVE view is refused the same way, with its own code: a perspective camera has
        /// no view scale at all, Revit throws on the setter, and what a caller asking to "scale"
        /// one actually wants is views/scale-perspective-crop. Answering that explicitly rather
        /// than letting Revit's refusal come back as a bare REVIT_API_ERROR is the whole point.
        /// </summary>
        internal static object SetScale(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            int scale = (int)JsonBody.RequireDouble(body, "scale");

            if (!View.IsValidViewScale(scale))
            {
                throw BridgeException.BadRequest(
                    "\"scale\" must be the denominator X in the view scale 1/X, an integer from 1 "
                        + "to 24000; got " + scale + ".");
            }

            List<long> viewIds;

            JsonElement batch;
            if (JsonBody.TryGet(body, "viewIds", out batch))
            {
                viewIds = JsonBody.RequireIds(body, "viewIds");
            }
            else
            {
                viewIds = new List<long>();
                viewIds.Add(JsonBody.AsLong(RequireValue(body, "viewId"), "viewId"));
            }

            return RevitWrite.InGroup(document, "MCP: set view scale", delegate
            {
                List<object> updated = new List<object>();
                List<object> failed = new List<object>();

                foreach (long viewId in viewIds)
                {
                    View view = document.GetElement(new ElementId(viewId)) as View;

                    if (view == null)
                    {
                        failed.Add(ScaleFailure(
                            viewId,
                            "NOT_A_VIEW",
                            "Element " + viewId + " is not a view in this document. Call "
                                + "/revit-mcp/views for the ones that are."));

                        continue;
                    }

                    if (!CanSetScale(view))
                    {
                        failed.Add(ScaleFailure(
                            viewId,
                            "VIEW_SCALE_NOT_SETTABLE",
                            "View \"" + RevitFacts.SafeName(view) + "\" (" + viewId + ", "
                                + view.ViewType + ") is a schedule, a sheet or a view template, and "
                                + "the bridge does not scale those: a schedule and a sheet have no "
                                + "view scale, and a template's scale belongs to every view that "
                                + "uses it."));

                        continue;
                    }

                    if (IsPerspectiveView(view))
                    {
                        failed.Add(ScaleFailure(
                            viewId,
                            "PERSPECTIVE_VIEW_HAS_NO_SCALE",
                            "View \"" + RevitFacts.SafeName(view) + "\" (" + viewId + ") is a "
                                + "perspective camera, and a perspective view has no view scale: "
                                + "1/X describes a projection, and a camera is not one. Revit "
                                + "refuses the setter on it. To change how big that view comes out "
                                + "on its sheet, call /revit-mcp/views/scale-perspective-crop with "
                                + "a multiplier - it scales the crop box and the on-sheet size "
                                + "together, proportions locked, without moving the camera. The "
                                + "other views in this batch were still re-scaled."));

                        continue;
                    }

                    try
                    {
                        RevitWrite.InTransaction(document, "Set view scale", delegate
                        {
                            view.Scale = scale;
                        });
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                    {
                        failed.Add(ScaleFailure(viewId, "REVIT_API_ERROR", ex.Message));
                        continue;
                    }

                    updated.Add(new Dictionary<string, object>
                    {
                        { "id", viewId },
                        { "name", RevitFacts.SafeName(view) },
                        { "viewType", view.ViewType.ToString() },

                        // Read back off the view: a scale a view template controls is not the scale
                        // that was asked for, and the caller should see the difference.
                        { "scale", view.Scale },
                    });
                }

                return new Dictionary<string, object>
                {
                    { "updated", updated },
                    { "failed", failed },
                };
            });
        }

        /// <summary>
        /// Body: {viewId, multiplier, dryRun?}. Resize ONE perspective 3D view on its sheet, with
        /// the proportions locked.
        ///
        /// This is View3D.ScalePerspectiveCropBox(double), which exists from Revit 2024.1 and does
        /// exactly one thing: it scales the crop box of a perspective view on both X and Y, and -
        /// in Revit's own words - "makes the change analogous to changing the scale of the
        /// orthographic view, so that both the size and scale of the view on a sheet changes
        /// according to the provided argument". So multiplier 2 makes the view twice as big on the
        /// paper and 0.5 halves it, and the picture inside the frame is the same picture.
        ///
        /// What it is NOT, because these are the two things a caller reaches for first and both are
        /// the wrong tool:
        ///   - It is not a reframe. views/set-crop crops a view to a REGION OF THE MODEL and
        ///     changes what is in shot. This changes the size of the shot, not its content, and
        ///     the camera is not moved at all - which is why the answer reports the orientation on
        ///     both sides and says whether it stayed put.
        ///   - It is not the view scale. views/set-scale writes View.Scale, the 1/X denominator,
        ///     and that is meaningless on a perspective camera - Revit refuses the setter, and
        ///     views/set-scale now says so with PERSPECTIVE_VIEW_HAS_NO_SCALE rather than passing
        ///     Revit's exception on. For an isometric 3D view, a plan or a section, views/set-scale
        ///     is still the right call.
        ///
        /// Everything is checked BEFORE a transaction is opened - the element exists, it is a
        /// View3D, it is not a view template (Revit's own method throws InvalidOperationException
        /// on one) and it is a perspective camera - so a refusal never leaves a half-applied
        /// change behind.
        ///
        /// "dryRun" DEFAULTS TO TRUE. The dry run reports the view as it is NOW plus the multiplier
        /// that was asked for, and deliberately no predicted "after": the sizes Revit ends up with
        /// are a readback, and inventing them here would be a measurement nobody took.
        ///
        /// An applied call is one transaction and therefore one Ctrl+Z. The document is regenerated
        /// inside it before anything is read back, because View.Outline and the viewport's box on
        /// the sheet are derived geometry - without the regeneration the readback can still be the
        /// size from before the call.
        ///
        /// Everything in "before" and "after" is measured: "outline" is View.Outline, the bounds of
        /// the view in PAPER feet, which is the number that actually says how big it comes out;
        /// "cropBox" is the crop box's own Min/Max and Transform; "camera" is View3D.GetOrientation;
        /// and "viewport" is the Viewport on the sheet, with GetBoxCenter and GetBoxOutline in
        /// paper feet, or null when the view is not on a sheet. "cameraUnchanged" compares the two
        /// orientations, and null there means one of them could not be read, never "it moved".
        ///
        /// Two things a live run measured, both worth knowing before reading the answer:
        ///   - The MODEL-space crop box does not have to move. On view 223065 at multiplier
        ///     5.64896 the crop box Min/Max came back identical while the view went from
        ///     0.492 x 0.369 to 2.78 x 2.085 paper feet and the viewport with it. Judge the call by
        ///     "outline" and "viewport", not by "cropBox": camera AND composition are both kept,
        ///     which is the whole point of this endpoint.
        ///   - The view TITLE does not follow. Revit leaves it at the paper position it had, so
        ///     after a large multiplier the old label offset sits inside the enlarged image and the
        ///     title has to be put back with sheets/set-viewport-position - labelOffset and
        ///     labelLineLength, a separate call, on purpose: where a title belongs is a drawing
        ///     decision and this endpoint does not get to make it.
        /// </summary>
        internal static object ScalePerspectiveCrop(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            long viewId = JsonBody.AsLong(RequireValue(body, "viewId"), "viewId");
            double multiplier = JsonBody.RequireDouble(body, "multiplier");
            bool dryRun = JsonBody.OptionalBool(body, "dryRun", true);

            if (double.IsNaN(multiplier) || double.IsInfinity(multiplier) || multiplier <= 0.0)
            {
                throw BridgeException.BadRequest(
                    "\"multiplier\" must be a finite number greater than zero: it multiplies the "
                        + "view's size on the sheet, so 0.5 halves it and 2 doubles it. Got "
                        + multiplier.ToString(CultureInfo.InvariantCulture) + ". Nothing was changed.");
            }

            View3D view = RequirePerspectiveView(document, viewId);

            ViewOrientation3D beforeCamera = SafeOrientation(view);

            Dictionary<string, object> report = new Dictionary<string, object>
            {
                { "dryRun", dryRun },
                { "viewId", viewId },
                { "name", RevitFacts.SafeName(view) },
                { "viewType", view.ViewType.ToString() },
                { "multiplier", multiplier },
                { "before", ReadPerspectiveSize(document, view, beforeCamera) },
                { "after", null },
                { "cameraUnchanged", null },
            };

            if (dryRun)
            {
                report["applied"] = false;
                report["note"] = "Nothing was changed. \"before\" is measured off the view now and "
                    + "\"multiplier\" is what you asked for; there is no predicted \"after\" here on "
                    + "purpose - the size Revit ends up with is read back off an applied call, not "
                    + "calculated by this endpoint. Send dryRun false to apply it.";

                return report;
            }

            return RevitWrite.InGroup(document, "MCP: scale perspective crop", delegate
            {
                RevitWrite.InTransaction(document, "Scale perspective crop", delegate
                {
                    view.ScalePerspectiveCropBox(multiplier);

                    // View.Outline and the viewport's box on the sheet are derived geometry: read
                    // without this, they are still the sizes from before the call.
                    document.Regenerate();
                });

                ViewOrientation3D afterCamera = SafeOrientation(view);

                report["applied"] = true;
                report["after"] = ReadPerspectiveSize(document, view, afterCamera);
                report["cameraUnchanged"] = CameraUnchanged(beforeCamera, afterCamera);
                report["note"] = "Applied in one transaction, so this is one undo step. \"after\" is "
                    + "read back off the view after a regeneration, never echoed. The camera is not "
                    + "touched by this call - \"cameraUnchanged\" is the proof, and null there means "
                    + "an orientation could not be read rather than that it moved. Compare "
                    + "\"outline\" and \"viewport\" to see the change: the crop box's MODEL "
                    + "coordinates can come back identical on a call that worked, because the "
                    + "composition is kept and the size change is on the paper. Revit also leaves "
                    + "the view TITLE where it was, so after a large multiplier the label can end "
                    + "up over the enlarged image - move it with "
                    + "/revit-mcp/sheets/set-viewport-position.";

                return report;
            });
        }

        /// <summary>
        /// Body: {viewId, style, detailLevel?, shadows?}. How a view is drawn, which is most of
        /// what decides whether an exported image reads as a model or as a diagram.
        ///
        /// "style" is a DisplayStyle name. "HiddenLine" is accepted alongside Revit's own spelling
        /// for it, which is "HLR" - the enum member is named after hidden line removal and nobody
        /// outside the API calls it that.
        ///
        /// "shadows" is not handled here and is redirected rather than refused. This endpoint used
        /// to answer SHADOWS_NOT_EXPOSED and say cast shadows are impossible; that claim was too
        /// broad. BuiltInParameter.GRAPHIC_DISPLAY_OPTIONS_SHADOWS does exist, and whether it is a
        /// writable toggle is a property of the LIVE VIEW - it can be missing, or storage type
        /// None, or read-only, or controlled by a view template - so it has to be probed rather
        /// than declared either way. /revit-mcp/views/set-graphics does that probing, writes the
        /// parameter when the probe proves it writable, and falls back to posting Revit's own
        /// ID_IMAGE_SHADOW_ON/OFF command when it does not. Everything that needs the view active
        /// and the document unmodifiable lives there, which is why this endpoint sends the caller
        /// on instead of growing a second copy of it.
        /// </summary>
        internal static object SetStyle(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            long viewId = JsonBody.AsLong(RequireValue(body, "viewId"), "viewId");
            string style = JsonBody.RequireString(body, "style");
            string detailLevelName = JsonBody.OptionalString(body, "detailLevel");

            JsonElement shadowsValue;
            if (JsonBody.TryGet(body, "shadows", out shadowsValue))
            {
                throw new BridgeException(
                    409,
                    "SHADOWS_HANDLED_ELSEWHERE",
                    "Cast shadows are not set here - send \"shadows\" to "
                        + "/revit-mcp/views/set-graphics instead, which takes the same \"style\" and "
                        + "\"detailLevel\" you passed. That endpoint probes "
                        + "GRAPHIC_DISPLAY_OPTIONS_SHADOWS on the live view and reports what it "
                        + "found - whether the parameter is there, its storage type, whether Revit "
                        + "calls it read-only and whether a view template controls it - then either "
                        + "writes it or posts Revit's own ID_IMAGE_SHADOW_ON/OFF command, saying "
                        + "which it did. Call /revit-mcp/views/graphics first if you want the "
                        + "diagnosis before the write. Everything else in this request - style and "
                        + "detailLevel - would have worked.");
            }

            View view = document.GetElement(new ElementId(viewId)) as View;
            if (view == null)
            {
                throw BridgeException.BadRequest(
                    "Element " + viewId + " is not a view in this document. Call /revit-mcp/views "
                        + "for the ones that are.");
            }

            DisplayStyle displayStyle = ResolveDisplayStyle(style);
            ViewDetailLevel detailLevel = ResolveDetailLevel(detailLevelName);

            if (!view.CanModifyDisplayStyle())
            {
                throw BridgeException.BadRequest(
                    "Revit will not set the display style of view \"" + RevitFacts.SafeName(view)
                        + "\" (" + viewId + ", " + view.ViewType + "). A schedule, a sheet and a "
                        + "legend have no display style.");
            }

            return RevitWrite.InGroup(document, "MCP: set view style", delegate
            {
                Dictionary<string, object> updated = new Dictionary<string, object>();

                RevitWrite.InTransaction(document, "Set view style", delegate
                {
                    view.DisplayStyle = displayStyle;

                    if (detailLevelName != null)
                    {
                        view.DetailLevel = detailLevel;
                    }

                    updated["viewId"] = viewId;
                    updated["name"] = RevitFacts.SafeName(view);
                    updated["viewType"] = view.ViewType.ToString();

                    // Read back, never echoed: a view template owns these on the views that use
                    // one, and then what was asked for is not what the view is drawn with.
                    updated["style"] = view.DisplayStyle.ToString();
                    updated["detailLevel"] = view.DetailLevel.ToString();
                });

                return updated;
            });
        }


        /// <summary>
        /// Body: {viewId, kind, skyColor?, horizonColor?, groundColor?, imagePath?}. What sits
        /// behind the model in a 3D view. A default 3D view is drawn on Revit's flat dark
        /// background, and an export of one reads as a screenshot of the application rather than a
        /// visualisation - a sky is the single cheapest thing that changes that.
        ///
        /// Only a 3D view has a background. Revit exposes View.GetBackground/SetBackground on the
        /// View base class, but the setting is meaningless anywhere else and a plan is a
        /// BAD_REQUEST here rather than a silent no-op.
        ///
        /// "kind" picks the ViewDisplayBackground factory, and they do not take the same arguments
        /// - measured against the installed RevitAPI.dll, not assumed:
        ///   - "sky"      -> ViewDisplayBackground.CreateSky(), which takes NO PARAMETERS. Revit's
        ///                   own sky-and-clouds gradient is not configurable, and the type it reads
        ///                   back as is SunAndClouds, not Sky. Passing a colour with it is refused
        ///                   rather than ignored: the caller asked for something that cannot
        ///                   happen.
        ///   - "gradient" -> CreateGradient(Color skyColor, Color horizonColor, Color groundColor),
        ///                   the three-band sky the Graphic Display Options dialog calls Gradient.
        ///                   All three default to a daylight sky when the caller names none.
        ///   - "image"    -> CreateImage(string imagePath, ViewDisplayBackgroundImageFlags flags,
        ///                   UV imageOffsets, UV imageScales). The bridge passes FitToScreen with
        ///                   zero offset and unit scale, which is what a backdrop wants.
        ///
        /// Colours are {r, g, b}, each channel 0-255, like every other colour in this bridge.
        ///
        /// The response is read back off the view with GetBackground() after the commit, never
        /// echoed: a view template owning the graphic display settings is exactly the case where
        /// what was asked for is not what the view ends up drawn with.
        /// </summary>
        internal static object SetBackground(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            long viewId = JsonBody.AsLong(RequireValue(body, "viewId"), "viewId");
            string kind = JsonBody.RequireString(body, "kind");

            View view = document.GetElement(new ElementId(viewId)) as View;
            if (view == null)
            {
                throw BridgeException.BadRequest(
                    "Element " + viewId + " is not a view in this document. Call /revit-mcp/views "
                        + "for the ones that are.");
            }

            if (!(view is View3D))
            {
                throw BridgeException.BadRequest(
                    "View \"" + RevitFacts.SafeName(view) + "\" (" + viewId + ", " + view.ViewType
                        + ") is not a 3D view, and only a 3D view has a background - a plan, a "
                        + "section and a sheet are drawn on paper. Create one with "
                        + "/revit-mcp/views/create-3d, or pick a ThreeD view out of "
                        + "/revit-mcp/views.");
            }

            ViewDisplayBackground background = BuildBackground(body, kind);

            return RevitWrite.InGroup(document, "MCP: set view background", delegate
            {
                Dictionary<string, object> updated = new Dictionary<string, object>();

                RevitWrite.InTransaction(document, "Set view background", delegate
                {
                    view.SetBackground(background);
                });

                updated["viewId"] = viewId;
                updated["name"] = RevitFacts.SafeName(view);
                updated["viewType"] = view.ViewType.ToString();

                // Read back off the view, outside the transaction that wrote it: what stuck is the
                // only thing worth reporting, and a view template can own this.
                Dictionary<string, object> actual = ReadBackground(view.GetBackground());
                foreach (KeyValuePair<string, object> entry in actual)
                {
                    updated[entry.Key] = entry.Value;
                }

                return updated;
            });
        }

        /// <summary>
        /// Body: {viewId, categories: [...], hidden?}. Turns whole categories off in one view -
        /// the level datums, section marks, elevation tags, reference planes and sun path that
        /// float through an otherwise finished isometric and mark it instantly as a screenshot of
        /// somebody's working view.
        ///
        /// Names are matched case-insensitively against a friendly set - Levels, Grids,
        /// ReferencePlanes, Sections, Elevations, Cameras, SunPath, Lines - and a literal OST_*
        /// BuiltInCategory name is taken as well, so anything outside the friendly set is still
        /// reachable. The shorthand "annotation" expands to the whole friendly set at once, which
        /// is what a presentation view actually wants.
        ///
        /// Revit refuses to hide some categories in some views, so View.CanCategoryBeHidden is
        /// asked first and a refusal comes back as a skipped row rather than an exception: one
        /// category this view will not hide must not cost the caller the other seven. An unknown
        /// name is the one hard failure, because it is a typo the caller can fix, and the message
        /// carries the accepted names.
        /// </summary>
        internal static object HideCategories(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            long viewId = JsonBody.AsLong(RequireValue(body, "viewId"), "viewId");
            List<string> requested = JsonBody.OptionalStringList(body, "categories");
            bool hidden = JsonBody.OptionalBool(body, "hidden", true);

            if (requested == null || requested.Count == 0)
            {
                throw BridgeException.BadRequest(
                    "\"categories\" is required and must be a non-empty array of category names. "
                        + AcceptedCategories());
            }

            View view = document.GetElement(new ElementId(viewId)) as View;
            if (view == null)
            {
                throw BridgeException.BadRequest(
                    "Element " + viewId + " is not a view in this document. Call /revit-mcp/views "
                        + "for the ones that are.");
            }

            // Expanded and resolved up front: an unknown name is a typo, and failing the whole call
            // before anything is hidden is kinder than half a request landing.
            List<KeyValuePair<string, BuiltInCategory>> targets = ResolveHideCategories(requested);

            return RevitWrite.InGroup(document, "MCP: hide view categories", delegate
            {
                List<object> results = new List<object>();

                foreach (KeyValuePair<string, BuiltInCategory> target in targets)
                {
                    // Straight off the BuiltInCategory, not via Category.GetCategory: that returns
                    // null for plenty of categories a view can still hide - Cameras and Sun Path
                    // among them - and going through it would refuse work Revit would have done.
                    ElementId categoryId = new ElementId(target.Value);

                    Dictionary<string, object> row = new Dictionary<string, object>
                    {
                        { "category", target.Key },
                    };

                    if (!view.CanCategoryBeHidden(categoryId))
                    {
                        row["hidden"] = false;
                        row["skipped"] = true;
                        row["reason"] = "Revit reports CanCategoryBeHidden false for "
                            + CategoryLabel(document, target.Value) + " in view \""
                            + RevitFacts.SafeName(view) + "\" (" + view.ViewType + "): either this "
                            + "document has no such category, or its visibility in this view is not "
                            + "the view's to set.";

                        results.Add(row);
                        continue;
                    }

                    try
                    {
                        RevitWrite.InTransaction(document, "Hide view category", delegate
                        {
                            view.SetCategoryHidden(categoryId, hidden);
                        });
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                    {
                        row["hidden"] = view.GetCategoryHidden(categoryId);
                        row["skipped"] = true;
                        row["reason"] = ex.Message;

                        results.Add(row);
                        continue;
                    }

                    // Read back, never echoed: a view template owning V/G overrides is exactly the
                    // case where SetCategoryHidden lands and the view is still drawn the old way.
                    row["hidden"] = view.GetCategoryHidden(categoryId);
                    results.Add(row);
                }

                return new Dictionary<string, object>
                {
                    { "viewId", viewId },
                    { "name", RevitFacts.SafeName(view) },
                    { "viewType", view.ViewType.ToString() },
                    { "categories", results },
                };
            });
        }

        /// <summary>
        /// Body: {viewId, azimuth?, altitude?} in DEGREES, or {viewId, date?, time?}. Where the sun
        /// is, which is what decides the direction of every shaded face in an export and therefore
        /// most of whether it reads as architecture.
        ///
        /// Two routes, and they are two different Revit modes - measured against the installed
        /// RevitAPI.dll and against a live view, not assumed:
        ///   - azimuth/altitude sets SunAndShadowType.Lighting and writes SunAndShadowSettings
        ///     .Azimuth / .Altitude, which Revit stores in RADIANS. The bridge converts from
        ///     degrees. RelativeToView is forced false so azimuth is a compass bearing off project
        ///     north rather than something that moves with the camera.
        ///   - date/time sets SunAndShadowType.StillImage and writes StartDateAndTime, and Revit
        ///     computes the azimuth and altitude itself from the project's latitude, longitude and
        ///     time zone. That is the honest route for "half three on a June afternoon".
        /// Pass one route or the other; both at once is a BAD_REQUEST, because the second would
        /// silently overwrite the first.
        ///
        /// Two things about the date/time route came out of testing it against a live view:
        ///   - The DateTime handed to Revit MUST carry a Kind. An Unspecified one - which is what
        ///     DateTime.TryParseExact produces - is refused with "Revit does not accept input
        ///     DateTime objects if they are of kind DateTypeKind.Unspecified".
        ///   - Revit applies the project's daylight saving rule, so the hour that sticks is not
        ///     always the hour asked for: 15:30 on 21 June came back as 14:30, while 15:30 on
        ///     21 January round-tripped exactly. The response reports the stored time.
        ///
        /// SUN SETTINGS CAN BE SHARED BETWEEN VIEWS. View.SunAndShadowSettings is a document
        /// Element, and a view can either own one or sit on the document's shared settings - in
        /// which case moving the sun here moves it in every other view sharing them. The response
        /// reports "sunSettingsId" and "sharesSettings" precisely so that is visible rather than
        /// hidden: two views reporting the same sunSettingsId are one sun, and changing one
        /// changed both.
        ///
        /// The azimuth and altitude in the response are read back off the settings after the
        /// commit and converted to degrees, so the date/time route reports the sun position Revit
        /// actually computed.
        /// </summary>
        internal static object SetSun(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            long viewId = JsonBody.AsLong(RequireValue(body, "viewId"), "viewId");

            JsonElement value;
            bool hasAzimuth = JsonBody.TryGet(body, "azimuth", out value);
            bool hasAltitude = JsonBody.TryGet(body, "altitude", out value);
            bool hasDate = JsonBody.TryGet(body, "date", out value);
            bool hasTime = JsonBody.TryGet(body, "time", out value);

            if ((hasAzimuth || hasAltitude) && (hasDate || hasTime))
            {
                throw BridgeException.BadRequest(
                    "Pass either \"azimuth\"/\"altitude\" or \"date\"/\"time\", not both: they are "
                        + "two different Revit sun modes and the second would overwrite the first. "
                        + "Angles put the view in Lighting mode; a date and time put it in Still "
                        + "Image mode and let Revit compute the angles from the project location.");
            }

            if (!hasAzimuth && !hasAltitude && !hasDate && !hasTime)
            {
                throw BridgeException.BadRequest(
                    "Nothing to set. Pass \"azimuth\" and/or \"altitude\" in degrees, or \"date\" "
                        + "(YYYY-MM-DD) and/or \"time\" (HH:MM).");
            }

            View view = document.GetElement(new ElementId(viewId)) as View;
            if (view == null)
            {
                throw BridgeException.BadRequest(
                    "Element " + viewId + " is not a view in this document. Call /revit-mcp/views "
                        + "for the ones that are.");
            }

            SunAndShadowSettings settings = view.SunAndShadowSettings;
            if (settings == null)
            {
                throw new BridgeException(
                    409,
                    "NO_SUN_SETTINGS",
                    "View \"" + RevitFacts.SafeName(view) + "\" (" + viewId + ", " + view.ViewType
                        + ") has no SunAndShadowSettings. A schedule, a sheet and a drafting view "
                        + "have no sun; use a 3D view, a plan or an elevation.");
            }

            double azimuth = JsonBody.OptionalDouble(body, "azimuth", double.NaN);
            double altitude = JsonBody.OptionalDouble(body, "altitude", double.NaN);

            if (hasAzimuth && (azimuth < 0.0 || azimuth > 360.0))
            {
                throw BridgeException.BadRequest(
                    "\"azimuth\" is a compass bearing in degrees, 0 (north) to 360 clockwise; got "
                        + azimuth + ".");
            }

            if (hasAltitude && (altitude < -90.0 || altitude > 90.0))
            {
                throw BridgeException.BadRequest(
                    "\"altitude\" is the sun's height in degrees above the horizon, -90 to 90; got "
                        + altitude + ".");
            }

            DateTime when = hasDate || hasTime
                ? SunDateTime(settings, JsonBody.OptionalString(body, "date"), JsonBody.OptionalString(body, "time"))
                : DateTime.MinValue;

            return RevitWrite.InGroup(document, "MCP: set sun position", delegate
            {
                RevitWrite.InTransaction(document, "Set sun position", delegate
                {
                    if (hasAzimuth || hasAltitude)
                    {
                        // Lighting is the only mode in which Revit lets the angles be written: in
                        // Still Image and the study modes they are computed from the date, the
                        // time and the project location, and the setter throws.
                        settings.SunAndShadowType = SunAndShadowType.Lighting;
                        settings.RelativeToView = false;

                        if (hasAzimuth)
                        {
                            settings.Azimuth = azimuth * Math.PI / 180.0;
                        }

                        if (hasAltitude)
                        {
                            settings.Altitude = altitude * Math.PI / 180.0;
                        }
                    }
                    else
                    {
                        settings.SunAndShadowType = SunAndShadowType.StillImage;
                        settings.StartDateAndTime = when;
                    }
                });

                Dictionary<string, object> updated = new Dictionary<string, object>
                {
                    { "viewId", viewId },
                    { "name", RevitFacts.SafeName(view) },
                    { "viewType", view.ViewType.ToString() },

                    // The two that answer "did this move every other view too?".
                    { "sunSettingsId", settings.Id.Value },
                    { "sharesSettings", settings.SharesSettings },

                    { "sunAndShadowType", settings.SunAndShadowType.ToString() },
                    { "relativeToView", settings.RelativeToView },
                };

                // Read back and converted out of Revit's radians, never echoed: the date/time route
                // never stated an angle at all, and this is where it comes from.
                //
                // The branch is not tidiness. Azimuth and Altitude are the LIGHTING angles and hold
                // their own values - Revit's defaults are 135 and 35 - in every mode, so in Still
                // Image they report a sun that is not the one being drawn. Measured on a live view:
                // a Still Image set to 15:30 on 21 June still read Azimuth 135 / Altitude 35, while
                // GetFrameAzimuth / GetFrameAltitude of the active frame gave the real position.
                if (settings.SunAndShadowType == SunAndShadowType.Lighting)
                {
                    updated["azimuth"] = AzimuthDegrees(settings.Azimuth);
                    updated["altitude"] = Degrees(settings.Altitude);
                }
                else
                {
                    updated["azimuth"] = AzimuthDegrees(settings.GetFrameAzimuth(settings.ActiveFrame));
                    updated["altitude"] = Degrees(settings.GetFrameAltitude(settings.ActiveFrame));
                }

                // Read back too: Revit stores the sun clock at the project location and applies the
                // project's daylight saving rule, so the hour that stuck is not always the hour
                // that was asked for.
                updated["dateAndTime"] = settings.StartDateAndTime.ToString(
                    "yyyy-MM-dd HH:mm",
                    CultureInfo.InvariantCulture);

                return updated;
            });
        }

        /// <summary>
        /// Body: {viewId, path?, width?, height?, format?} for one, or {viewIds: [...]} for a
        /// batch. Raster images of views, on disk, which is the only way anything outside Revit
        /// gets to see what the model looks like.
        ///
        /// Two things about this endpoint are not obvious:
        ///
        /// 1. **Revit renames the file.** ExportImage treats "path" as a stem and appends the view
        ///    to it - "garden.png" becomes "garden - 3D View - Garden Eye.png". So the batch is run
        ///    as one single-view export per view, the directory is compared before and after, and
        ///    the path reported back is the file Revit actually wrote. Never the one asked for.
        /// 2. **This is not a render.** The Revit API has no way to run the photoreal raytracer:
        ///    View3D.GetRenderingSettings / SetRenderingSettings only configure what the Render
        ///    dialog would do, and there is no method that starts it. What comes out is the view
        ///    exactly as drawn on screen, so the display style set by views/set-style is what
        ///    decides how it looks.
        ///
        /// Size is Revit's own model: one PixelSize along one FitDirection, with the other
        /// dimension following the view's aspect. "width" fits horizontally, "height" fits
        /// vertically, and passing both fits the width. The width and height in the response are
        /// read out of the PNG that was written, not echoed back.
        ///
        /// Not transacted: exporting is a read of the document, and Revit refuses it inside a
        /// transaction.
        /// </summary>
        internal static object ExportImage(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            List<long> viewIds;

            JsonElement batch;
            if (JsonBody.TryGet(body, "viewIds", out batch))
            {
                viewIds = JsonBody.RequireIds(body, "viewIds");
            }
            else
            {
                viewIds = new List<long>();
                viewIds.Add(JsonBody.AsLong(RequireValue(body, "viewId"), "viewId"));
            }

            int width = JsonBody.OptionalInt(body, "width", 0);
            int height = JsonBody.OptionalInt(body, "height", 0);
            ImageFileType fileType = ResolveImageFileType(JsonBody.OptionalString(body, "format"));

            FitDirectionType fitDirection =
                width <= 0 && height > 0 ? FitDirectionType.Vertical : FitDirectionType.Horizontal;

            int pixelSize = fitDirection == FitDirectionType.Vertical ? height : width;
            if (pixelSize <= 0)
            {
                pixelSize = DefaultImageWidth;
            }

            string directory = ResolveExportDirectory(JsonBody.OptionalString(body, "path"));
            string stem = ResolveExportStem(JsonBody.OptionalString(body, "path"));

            Directory.CreateDirectory(directory);

            List<object> exported = new List<object>();

            foreach (long viewId in viewIds)
            {
                View view = document.GetElement(new ElementId(viewId)) as View;
                if (view == null)
                {
                    throw BridgeException.BadRequest(
                        "Element " + viewId + " is not a view in this document. Call "
                            + "/revit-mcp/views for the ones that are.");
                }

                if (!view.CanBePrinted)
                {
                    throw BridgeException.BadRequest(
                        "View \"" + RevitFacts.SafeName(view) + "\" (" + viewId + ", "
                            + view.ViewType + ") cannot be exported: Revit reports it as not "
                            + "printable. A view template and a browser-only view are the usual "
                            + "ones.");
                }

                exported.Add(ExportOne(document, view, directory, stem, fileType, fitDirection, pixelSize));
            }

            if (viewIds.Count == 1)
            {
                return exported[0];
            }

            return new Dictionary<string, object>
            {
                { "exported", exported },
            };
        }

        /// <summary>
        /// Body: {sheetId, viewId, x?, y?} for one, or {placements: [{sheetId, viewId, x?, y?}]}
        /// for a batch - a whole phase of sheets in one Ctrl+Z, with a per-placement catch so one
        /// view Revit refuses cannot abort the rest.
        ///
        /// A ViewSchedule is not a Viewport: Revit places schedules with ScheduleSheetInstance and
        /// throws if you try Viewport.Create on one. That branch is handled here so the caller does
        /// not have to know which kind of view it is holding.
        ///
        /// x / y are sheet coordinates in feet, and default to the centre of the sheet.
        /// </summary>
        internal static object PlaceViews(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            JsonElement placements;
            bool batch = JsonBody.TryGet(body, "placements", out placements);

            if (batch && placements.ValueKind != JsonValueKind.Array)
            {
                throw BridgeException.BadRequest("\"placements\" must be an array.");
            }

            List<JsonElement> requests = new List<JsonElement>();
            if (batch)
            {
                foreach (JsonElement request in placements.EnumerateArray())
                {
                    requests.Add(request);
                }
            }
            else
            {
                requests.Add(body);
            }

            return RevitWrite.InGroup(document, "MCP: place views on sheets", delegate
            {
                List<object> placed = new List<object>();
                List<object> failed = new List<object>();

                foreach (JsonElement request in requests)
                {
                    // Read outside the try: a malformed request is a BAD_REQUEST for the whole
                    // call, not a placement Revit refused.
                    long sheetId = JsonBody.AsLong(RequireValue(request, "sheetId"), "sheetId");
                    long viewId = JsonBody.AsLong(RequireValue(request, "viewId"), "viewId");

                    Dictionary<string, object> result;

                    try
                    {
                        result = PlaceOne(document, request, sheetId, viewId);
                    }
                    catch (BridgeException ex)
                    {
                        if (!batch)
                        {
                            throw;
                        }

                        failed.Add(new Dictionary<string, object>
                        {
                            { "sheetId", sheetId },
                            { "viewId", viewId },
                            { "code", ex.Code },
                            { "reason", ex.Message },
                        });

                        continue;
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                    {
                        if (!batch)
                        {
                            throw;
                        }

                        failed.Add(new Dictionary<string, object>
                        {
                            { "sheetId", sheetId },
                            { "viewId", viewId },
                            { "code", "REVIT_API_ERROR" },
                            { "reason", ex.Message },
                        });

                        continue;
                    }

                    if (!batch)
                    {
                        return result;
                    }

                    placed.Add(result);
                }

                return new Dictionary<string, object>
                {
                    { "placed", placed },
                    { "failed", failed },
                };
            });
        }

        /// <summary>
        /// Body: {category, name, fields: [string], scale?}.
        ///
        /// A field name Revit does not know for that category goes into "skippedFields" instead of
        /// failing the call, and the response then also carries "availableFields" - the exact names
        /// this category does offer - so the caller can correct itself without a second round trip.
        /// </summary>
        internal static object CreateSchedule(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            string categoryName = JsonBody.RequireString(body, "category");
            string name = JsonBody.RequireString(body, "name");
            List<string> fields = JsonBody.OptionalStringList(body, "fields");
            int scale = JsonBody.OptionalInt(body, "scale", 0);

            if (fields == null)
            {
                fields = new List<string>();
            }

            Category category = RevitFacts.ResolveCategory(document, categoryName);
            if (category == null)
            {
                throw BridgeException.BadRequest(
                    "Unknown category \"" + categoryName + "\". Call /revit-mcp/categories for the "
                        + "categories present in this document, or pass a BuiltInCategory name such "
                        + "as OST_Planting.");
            }

            return RevitWrite.InGroup(document, "MCP: create schedule", delegate
            {
                Dictionary<string, object> created = new Dictionary<string, object>();

                RevitWrite.InTransaction(document, "Create schedule", delegate
                {
                    // Sheets are not a schedulable category: CreateSchedule rejects OST_Sheets with
                    // an ArgumentException. A sheet list is its own kind of view and Revit builds it
                    // through CreateSheetList instead.
                    ViewSchedule schedule =
                        category.Id.Value == (long)BuiltInCategory.OST_Sheets
                            ? ViewSchedule.CreateSheetList(document)
                            : ViewSchedule.CreateSchedule(document, category.Id);

                    ApplyName(document, schedule, name);
                    ApplyScale(schedule, scale);

                    ScheduleDefinition definition = schedule.Definition;

                    // Built once and reused: GetSchedulableFields walks the whole category.
                    Dictionary<string, SchedulableField> byName =
                        new Dictionary<string, SchedulableField>(StringComparer.OrdinalIgnoreCase);
                    List<string> available = new List<string>();

                    foreach (SchedulableField field in definition.GetSchedulableFields())
                    {
                        string fieldName = field.GetName(document);
                        if (string.IsNullOrEmpty(fieldName))
                        {
                            continue;
                        }

                        available.Add(fieldName);

                        if (!byName.ContainsKey(fieldName))
                        {
                            byName[fieldName] = field;
                        }
                    }

                    List<object> added = new List<object>();
                    List<object> skipped = new List<object>();

                    foreach (string fieldName in fields)
                    {
                        SchedulableField field;
                        if (!byName.TryGetValue(fieldName, out field))
                        {
                            skipped.Add(fieldName);
                            continue;
                        }

                        definition.AddField(field);
                        added.Add(fieldName);
                    }

                    created["id"] = schedule.Id.Value;
                    created["name"] = RevitFacts.SafeName(schedule);
                    created["category"] = category.Name;
                    created["fields"] = added;
                    created["skippedFields"] = skipped;

                    if (skipped.Count > 0)
                    {
                        available.Sort(StringComparer.OrdinalIgnoreCase);
                        created["availableFields"] = available;
                    }
                });

                return created;
            });
        }

        /// <summary>
        /// One placement, inside its own transaction so a refusal rolls back alone. Branches on
        /// ViewSchedule, which Revit places with ScheduleSheetInstance rather than Viewport.
        /// </summary>
        private static Dictionary<string, object> PlaceOne(
            Document document,
            JsonElement request,
            long sheetId,
            long viewId)
        {
            ViewSheet sheet = document.GetElement(new ElementId(sheetId)) as ViewSheet;
            if (sheet == null)
            {
                throw BridgeException.BadRequest(
                    "Element " + sheetId + " is not a sheet in this document. Call "
                        + "/revit-mcp/sheets for the ones that are.");
            }

            View view = document.GetElement(new ElementId(viewId)) as View;
            if (view == null)
            {
                throw BridgeException.BadRequest(
                    "Element " + viewId + " is not a view in this document. Call /revit-mcp/views "
                        + "for the ones that are.");
            }

            XYZ point = SheetPoint(request, sheet);

            ViewSchedule schedule = view as ViewSchedule;

            if (schedule != null)
            {
                // A schedule may legitimately appear on several sheets - unlike a view, which Revit
                // allows on exactly one - so this only refuses a repeat of the same pair.
                if (ScheduleInstanceOn(document, sheetId, viewId))
                {
                    throw new BridgeException(
                        409,
                        "VIEW_ALREADY_PLACED",
                        "Schedule \"" + RevitFacts.SafeName(view) + "\" (" + viewId + ") is already "
                            + "on sheet " + sheet.SheetNumber + " (" + sheetId + ").");
                }

                ScheduleSheetInstance instance = null;

                RevitWrite.InTransaction(document, "Place schedule on sheet", delegate
                {
                    instance = ScheduleSheetInstance.Create(document, sheet.Id, view.Id, point);
                });

                return new Dictionary<string, object>
                {
                    { "viewportId", instance.Id.Value },
                    { "sheetId", sheetId },
                    { "viewId", viewId },
                    { "kind", "ScheduleSheetInstance" },
                    { "x", point.X },
                    { "y", point.Y },
                };
            }

            long existingSheetId;
            if (ViewportSheetId(document, viewId, out existingSheetId))
            {
                throw new BridgeException(
                    409,
                    "VIEW_ALREADY_PLACED",
                    "View \"" + RevitFacts.SafeName(view) + "\" (" + viewId + ") is already on sheet "
                        + existingSheetId + ". A view can be on only one sheet; duplicate it with "
                        + "/revit-mcp/views/duplicate to place it again.");
            }

            if (!Viewport.CanAddViewToSheet(document, sheet.Id, view.Id))
            {
                throw new BridgeException(
                    409,
                    "CANNOT_PLACE",
                    "Revit will not put view \"" + RevitFacts.SafeName(view) + "\" (" + viewId
                        + ", " + view.ViewType + ") on sheet " + sheet.SheetNumber + " (" + sheetId
                        + "). View templates, legends already used elsewhere and the sheet's own "
                        + "view cannot be placed.");
            }

            Viewport viewport = null;

            RevitWrite.InTransaction(document, "Place view on sheet", delegate
            {
                viewport = Viewport.Create(document, sheet.Id, view.Id, point);
            });

            if (viewport == null)
            {
                throw new BridgeException(
                    409,
                    "CANNOT_PLACE",
                    "Revit returned no viewport for view " + viewId + " on sheet " + sheetId + ".");
            }

            return new Dictionary<string, object>
            {
                { "viewportId", viewport.Id.Value },
                { "sheetId", sheetId },
                { "viewId", viewId },
                { "kind", "Viewport" },
                { "x", point.X },
                { "y", point.Y },
            };
        }

        /// <summary>The requested sheet point, or the middle of the sheet when x / y are omitted.</summary>
        private static XYZ SheetPoint(JsonElement request, ViewSheet sheet)
        {
            BoundingBoxUV outline = sheet.Outline;

            double x = outline == null ? 0.0 : (outline.Min.U + outline.Max.U) / 2.0;
            double y = outline == null ? 0.0 : (outline.Min.V + outline.Max.V) / 2.0;

            return new XYZ(
                JsonBody.OptionalDouble(request, "x", x),
                JsonBody.OptionalDouble(request, "y", y),
                0.0);
        }

        /// <summary>Every view id that already has a Viewport or a ScheduleSheetInstance.</summary>
        private static HashSet<long> PlacedViewIds(Document document)
        {
            HashSet<long> placed = new HashSet<long>();

            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(Viewport)))
            {
                Viewport viewport = element as Viewport;
                if (viewport != null)
                {
                    placed.Add(viewport.ViewId.Value);
                }
            }

            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(ScheduleSheetInstance)))
            {
                ScheduleSheetInstance instance = element as ScheduleSheetInstance;
                if (instance != null)
                {
                    placed.Add(instance.ScheduleId.Value);
                }
            }

            return placed;
        }

        private static bool ViewportSheetId(Document document, long viewId, out long sheetId)
        {
            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(Viewport)))
            {
                Viewport viewport = element as Viewport;
                if (viewport != null && viewport.ViewId.Value == viewId)
                {
                    sheetId = viewport.SheetId.Value;
                    return true;
                }
            }

            sheetId = 0;
            return false;
        }

        private static bool ScheduleInstanceOn(Document document, long sheetId, long scheduleId)
        {
            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(ScheduleSheetInstance)))
            {
                ScheduleSheetInstance instance = element as ScheduleSheetInstance;
                if (instance == null || instance.ScheduleId.Value != scheduleId)
                {
                    continue;
                }

                // OwnerViewId rather than a view-scoped collector: a ScheduleSheetInstance is a
                // view-specific element, and its owner view is the sheet it sits on.
                ElementId owner = instance.OwnerViewId;
                if (owner != null && owner.Value == sheetId)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Names the view, appending " 2", " 3", ... when Revit already has that name. View names
        /// are unique document-wide, and a colliding name is not worth failing a batch over.
        /// </summary>
        internal static void ApplyName(Document document, View view, string name)
        {
            HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(View)))
            {
                if (element.Id == view.Id)
                {
                    continue;
                }

                string existing = RevitFacts.SafeName(element);
                if (existing != null)
                {
                    taken.Add(existing);
                }
            }

            string candidate = name;
            int suffix = 2;

            while (taken.Contains(candidate))
            {
                candidate = name + " " + suffix;
                suffix++;
            }

            view.Name = candidate;
        }

        /// <summary>
        /// Adds the view's own frame to a row: viewDirection (Revit's "direction towards the
        /// viewer", so the opposite of the way a section looks), rightDirection and upDirection,
        /// each {x, y, z}. This is what makes views/create-section assertable without opening
        /// Revit and looking at it.
        ///
        /// A view with no frame - a schedule, a legend - simply does not get the fields, and all
        /// three are read before any is written so a row can never carry half a frame.
        /// </summary>
        private static void AddDirections(Dictionary<string, object> row, View view)
        {
            XYZ viewDirection;
            XYZ rightDirection;
            XYZ upDirection;

            try
            {
                viewDirection = view.ViewDirection;
                rightDirection = view.RightDirection;
                upDirection = view.UpDirection;
            }
            catch (Exception)
            {
                return;
            }

            if (viewDirection == null || rightDirection == null || upDirection == null)
            {
                return;
            }

            row["viewDirection"] = Vector(viewDirection);
            row["rightDirection"] = Vector(rightDirection);
            row["upDirection"] = Vector(upDirection);
        }

        private static Dictionary<string, object> Vector(XYZ value)
        {
            return new Dictionary<string, object>
            {
                { "x", value.X },
                { "y", value.Y },
                { "z", value.Z },
            };
        }

        /// <summary>
        /// Where the created view's crop actually ended up, MEASURED off the view and reported in
        /// model feet: viewOrigin, cropTransform, cropLocalBounds, modelBounds and cutPlaneOrigin.
        /// This is what makes an offset regression in views/create-section visible in the create
        /// response itself, next to the requestedOrigin echoed beside it.
        ///
        /// Everything here is read back, and nothing is assumed to match the box handed to
        /// CreateSection, because Revit does not keep it. Measured on view 247787 - origin
        /// (53, 40, 8.5), direction {x: 1, y: 0}, width 86, height 33, depth 8, handed a box of
        /// (-43, -16.5, 0)..(43, 16.5, 8) on a frame with BasisZ = look:
        ///
        ///   view.Origin      (53, 83, -8)   a CORNER, not the asked origin and not a centre
        ///   cropTransform    origin (53, 83, -8), basisX {0, -1, 0}, basisY {0, 0, 1},
        ///                    basisZ {-1, 0, 0}
        ///   cropLocalBounds  min (0, 0, -8), max (86, 33, 0)
        ///   modelBounds      (53, -3, -8)..(61, 83, 25)
        ///
        /// So Revit rewrites the frame into its own convention: origin moved to a corner, BasisZ
        /// turned to point BACK AT THE VIEWER (it equals View.ViewDirection, the negation of the
        /// way the section looks), and the depth interval expressed as -depth..0 whatever it was
        /// given. That last part is why cropLocalBounds proves nothing on its own: the buggy view
        /// 247769 and the correct view 247787 read back the SAME local pair, min (0, 0, -8) and
        /// max (86, 33, 0), while their model bounds sat 8 feet apart, on x 45..53 and x 53..61.
        /// It is reported to be seen, never to be judged.
        ///
        /// modelBounds is the axis-aligned model box around all EIGHT corners of the local box put
        /// through the crop's transform; transforming Min and Max alone would be wrong for any
        /// section whose frame is turned, which is every section but one.
        ///
        /// cutPlaneOrigin is the centre of the NEAR face, the plane the section cuts on. WHICH of
        /// the two Z faces that is has to be measured, not assumed: on 247787 the near face is
        /// Max.Z, because BasisZ came back pointing at the viewer, and taking Min.Z reported
        /// (61, 40, 8.5) - the far clip - for a section that correctly cut on the asked
        /// (53, 40, 8.5). So both face centres are projected on View.ViewDirection and the larger
        /// wins: ViewDirection points back at the viewer, so the face furthest along it is the one
        /// nearest the viewer. That holds whichever way round Revit hands the frame back, and it
        /// never has to trust the sign of BasisZ.
        ///
        /// A view whose crop or frame Revit will not hand over simply does not get the fields.
        /// </summary>
        private static void AddCropReadback(Dictionary<string, object> row, View view)
        {
            BoundingBoxXYZ crop;

            try
            {
                crop = view.CropBox;
            }
            catch (Exception)
            {
                return;
            }

            if (crop == null || crop.Transform == null)
            {
                return;
            }

            XYZ viewOrigin;
            XYZ viewDirection;

            try
            {
                viewOrigin = view.Origin;
                viewDirection = view.ViewDirection;
            }
            catch (Exception)
            {
                viewOrigin = null;
                viewDirection = null;
            }

            if (viewOrigin != null)
            {
                row["viewOrigin"] = Vector(viewOrigin);
            }

            Transform frame = crop.Transform;

            row["cropTransform"] = new Dictionary<string, object>
            {
                { "origin", Vector(frame.Origin) },
                { "basisX", Vector(frame.BasisX) },
                { "basisY", Vector(frame.BasisY) },
                { "basisZ", Vector(frame.BasisZ) },
            };

            row["cropLocalBounds"] = new Dictionary<string, object>
            {
                { "min", Vector(crop.Min) },
                { "max", Vector(crop.Max) },
            };

            XYZ min = null;
            XYZ max = null;

            foreach (XYZ corner in Corners(crop.Min, crop.Max))
            {
                XYZ point = frame.OfPoint(corner);

                if (min == null)
                {
                    min = point;
                    max = point;
                    continue;
                }

                min = new XYZ(
                    Math.Min(min.X, point.X),
                    Math.Min(min.Y, point.Y),
                    Math.Min(min.Z, point.Z));

                max = new XYZ(
                    Math.Max(max.X, point.X),
                    Math.Max(max.Y, point.Y),
                    Math.Max(max.Z, point.Z));
            }

            row["modelBounds"] = new Dictionary<string, object>
            {
                { "min", Vector(min) },
                { "max", Vector(max) },
            };

            if (viewDirection == null)
            {
                return;
            }

            double centreX = (crop.Min.X + crop.Max.X) / 2.0;
            double centreY = (crop.Min.Y + crop.Max.Y) / 2.0;

            XYZ minFace = frame.OfPoint(new XYZ(centreX, centreY, crop.Min.Z));
            XYZ maxFace = frame.OfPoint(new XYZ(centreX, centreY, crop.Max.Z));

            // The cut is the face nearest the VIEWER, and ViewDirection is the direction towards
            // the viewer, so it is the face furthest along ViewDirection. Measured on 247787,
            // whose frame came back with BasisZ {-1, 0, 0}: the Max.Z centre projects to -53 and
            // the Min.Z centre to -61, so Max.Z is the cut - the asked (53, 40, 8.5) - and Min.Z
            // is the far clip at 61. Picking Min.Z outright reported that far clip as the cut.
            row["cutPlaneOrigin"] = Vector(
                maxFace.DotProduct(viewDirection) >= minFace.DotProduct(viewDirection)
                    ? maxFace
                    : minFace);
        }

        /// <summary>The eight corners of the box spanned by two opposite points.</summary>
        private static List<XYZ> Corners(XYZ min, XYZ max)
        {
            List<XYZ> corners = new List<XYZ>();

            double[] xs = new double[] { min.X, max.X };
            double[] ys = new double[] { min.Y, max.Y };
            double[] zs = new double[] { min.Z, max.Z };

            foreach (double x in xs)
            {
                foreach (double y in ys)
                {
                    foreach (double z in zs)
                    {
                        corners.Add(new XYZ(x, y, z));
                    }
                }
            }

            return corners;
        }

        /// <summary>Scale 0 means "not requested"; Revit has no scale of 0.</summary>
        private static void ApplyScale(View view, int scale)
        {
            if (scale > 0)
            {
                view.Scale = scale;
            }
        }

        /// <summary>
        /// The three the bridge will not scale: a schedule and a sheet have no view scale at all,
        /// and a view template's scale is inherited by every view using it, so re-scaling one from
        /// a batch would quietly re-scale views that were never named. Everything else is attempted
        /// and any refusal Revit makes is caught per view.
        /// </summary>
        private static bool CanSetScale(View view)
        {
            return !view.IsTemplate && !(view is ViewSheet) && !(view is ViewSchedule);
        }

        /// <summary>
        /// True for a perspective camera. views/set-scale refuses one outright rather than letting
        /// Revit's refusal come back as a bare REVIT_API_ERROR: a perspective view has no view
        /// scale, and what a caller asking to "scale" one wants is views/scale-perspective-crop.
        /// </summary>
        private static bool IsPerspectiveView(View view)
        {
            View3D view3d = view as View3D;
            if (view3d == null)
            {
                return false;
            }

            try
            {
                return view3d.IsPerspective;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Dictionary<string, object> ScaleFailure(long viewId, string code, string reason)
        {
            return new Dictionary<string, object>
            {
                { "viewId", viewId },
                { "code", code },
                { "reason", reason },
            };
        }

        /// <summary>
        /// The view views/scale-perspective-crop can honestly resize, or a refusal naming why. All
        /// four checks run before any transaction is opened, so nothing is ever half applied:
        ///   - not a view at all: BAD_REQUEST.
        ///   - a view that is not a View3D: NOT_A_3D_VIEW - ScalePerspectiveCropBox is a View3D
        ///     method, and a plan or a section is re-scaled with views/set-scale.
        ///   - a view template: VIEW_IS_TEMPLATE - Revit's own method throws
        ///     InvalidOperationException on one, which is what its documentation records.
        ///   - an isometric 3D view: VIEW_NOT_PERSPECTIVE - it has a real view scale, so
        ///     views/set-scale is the call that changes its size on a sheet.
        /// </summary>
        private static View3D RequirePerspectiveView(Document document, long viewId)
        {
            Element element = document.GetElement(new ElementId(viewId));

            View3D view = element as View3D;

            if (view == null)
            {
                View other = element as View;
                if (other == null)
                {
                    throw BridgeException.BadRequest(
                        "Element " + viewId + " is not a view in this document. Call "
                            + "/revit-mcp/views for the ones that are.");
                }

                throw new BridgeException(
                    409,
                    "NOT_A_3D_VIEW",
                    "View \"" + RevitFacts.SafeName(other) + "\" (" + viewId + ", " + other.ViewType
                        + ") is not a 3D view. ScalePerspectiveCropBox is a View3D method and only a "
                        + "perspective camera has a perspective crop box to scale; a plan, a section "
                        + "or an elevation changes size on its sheet through its view scale, which "
                        + "is /revit-mcp/views/set-scale. Nothing was changed.");
            }

            if (view.IsTemplate)
            {
                throw new BridgeException(
                    409,
                    "VIEW_IS_TEMPLATE",
                    "View \"" + RevitFacts.SafeName(view) + "\" (" + viewId + ") is a view template. "
                        + "Revit's ScalePerspectiveCropBox throws InvalidOperationException on a "
                        + "template - it has no crop box of its own to scale - so name the views "
                        + "that use it instead. Nothing was changed.");
            }

            if (!view.IsPerspective)
            {
                throw new BridgeException(
                    409,
                    "VIEW_NOT_PERSPECTIVE",
                    "3D view \"" + RevitFacts.SafeName(view) + "\" (" + viewId + ") is isometric, not "
                        + "a perspective camera. ScalePerspectiveCropBox scales the crop box of a "
                        + "PERSPECTIVE view; an orthographic 3D view has an ordinary view scale, so "
                        + "re-size it with /revit-mcp/views/set-scale (1/X) instead. Nothing was "
                        + "changed.");
            }

            return view;
        }

        /// <summary>
        /// How big a perspective view is and where its camera stands, all MEASURED off the view:
        /// View.Outline in PAPER feet (the size it comes out on the sheet), the crop box's own
        /// Min/Max and Transform, the orientation, and the viewport on the sheet when it has one.
        ///
        /// Read "outline" and "viewport" to see whether the call did anything. A live run measured
        /// the crop box's model-space Min/Max coming back IDENTICAL while the view grew 5.65x on
        /// the paper, so "cropBox" on its own would make a working call look like a no-op - what
        /// moves is the on-sheet size and the scale, and the model coordinates staying put is the
        /// composition being kept rather than evidence of nothing happening.
        ///
        /// Nothing here is derived from the request, which is what lets "before" and "after" be
        /// compared honestly. A field Revit will not answer for comes back null - "Revit gave no
        /// answer" is a different report from zero.
        /// </summary>
        private static Dictionary<string, object> ReadPerspectiveSize(
            Document document,
            View3D view,
            ViewOrientation3D camera)
        {
            Dictionary<string, object> row = new Dictionary<string, object>
            {
                { "isPerspective", view.IsPerspective },
                { "scale", SafeScale(view) },
                { "outline", OutlineUV(view) },
                { "cropBoxActive", null },
                { "cropBox", null },
                { "camera", Camera(camera) },
                { "viewport", ViewportOn(document, view) },
            };

            try
            {
                row["cropBoxActive"] = view.CropBoxActive;
            }
            catch (Exception)
            {
                // A view with no crop at all answers neither; null already stands for that.
            }

            BoundingBoxXYZ crop;

            try
            {
                crop = view.CropBox;
            }
            catch (Exception)
            {
                return row;
            }

            if (crop == null)
            {
                return row;
            }

            Dictionary<string, object> box = new Dictionary<string, object>
            {
                { "min", Vector(crop.Min) },
                { "max", Vector(crop.Max) },

                // The crop box's own extents, and they can come back UNCHANGED by a call that did
                // work: measured on view 223065 at multiplier 5.64896, Min/Max did not move at all
                // while the view went from 0.492 x 0.369 to 2.78 x 2.085 PAPER feet. The size on
                // the sheet is in "outline", "viewport" and "scale"; this pair is here to show the
                // composition was kept, not to prove the call landed.
                { "width", crop.Max.X - crop.Min.X },
                { "height", crop.Max.Y - crop.Min.Y },
                { "transform", null },
            };

            if (crop.Transform != null)
            {
                box["transform"] = new Dictionary<string, object>
                {
                    { "origin", Vector(crop.Transform.Origin) },
                    { "basisX", Vector(crop.Transform.BasisX) },
                    { "basisY", Vector(crop.Transform.BasisY) },
                    { "basisZ", Vector(crop.Transform.BasisZ) },
                };
            }

            row["cropBox"] = box;
            return row;
        }

        /// <summary>The view's bounds in PAPER feet - View.Outline, a BoundingBoxUV.</summary>
        private static Dictionary<string, object> OutlineUV(View view)
        {
            try
            {
                BoundingBoxUV outline = view.Outline;
                if (outline == null)
                {
                    return null;
                }

                return new Dictionary<string, object>
                {
                    { "min", new Dictionary<string, object> { { "x", outline.Min.U }, { "y", outline.Min.V } } },
                    { "max", new Dictionary<string, object> { { "x", outline.Max.U }, { "y", outline.Max.V } } },
                    { "width", outline.Max.U - outline.Min.U },
                    { "height", outline.Max.V - outline.Min.V },
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>View.Scale, or null for a view that will not answer.</summary>
        private static object SafeScale(View view)
        {
            try
            {
                return view.Scale;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static ViewOrientation3D SafeOrientation(View3D view)
        {
            try
            {
                return view.GetOrientation();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Dictionary<string, object> Camera(ViewOrientation3D orientation)
        {
            if (orientation == null)
            {
                return null;
            }

            return new Dictionary<string, object>
            {
                { "eyePosition", Vector(orientation.EyePosition) },
                { "forwardDirection", Vector(orientation.ForwardDirection) },
                { "upDirection", Vector(orientation.UpDirection) },
            };
        }

        /// <summary>
        /// Whether the camera is where it was, compared with Revit's own tolerance. Null - not
        /// false - when either orientation could not be read: "not measured" is not "it moved".
        /// </summary>
        private static object CameraUnchanged(ViewOrientation3D before, ViewOrientation3D after)
        {
            if (before == null || after == null)
            {
                return null;
            }

            return before.EyePosition.IsAlmostEqualTo(after.EyePosition)
                && before.ForwardDirection.IsAlmostEqualTo(after.ForwardDirection)
                && before.UpDirection.IsAlmostEqualTo(after.UpDirection);
        }

        /// <summary>
        /// The viewport this view sits in, in PAPER feet: its centre (the point
        /// sheets/set-viewport-position moves) and its box outline, which is what actually grows
        /// when the perspective crop is scaled. Null when the view is not on a sheet.
        /// </summary>
        private static Dictionary<string, object> ViewportOn(Document document, View view)
        {
            Viewport viewport = null;

            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(Viewport)))
            {
                Viewport candidate = element as Viewport;
                if (candidate != null && candidate.ViewId.Value == view.Id.Value)
                {
                    viewport = candidate;
                    break;
                }
            }

            if (viewport == null)
            {
                return null;
            }

            ViewSheet sheet = document.GetElement(viewport.SheetId) as ViewSheet;

            Dictionary<string, object> row = new Dictionary<string, object>
            {
                { "id", viewport.Id.Value },
                { "sheetId", viewport.SheetId.Value },
                { "sheetNumber", sheet == null ? null : sheet.SheetNumber },
                { "sheetName", sheet == null ? null : RevitFacts.SafeName(sheet) },
                { "center", null },
                { "bounds", null },
            };

            try
            {
                XYZ centre = viewport.GetBoxCenter();
                row["center"] = new Dictionary<string, object> { { "x", centre.X }, { "y", centre.Y } };
            }
            catch (Exception)
            {
                // A viewport on a placeholder sheet has no box; null says so.
            }

            try
            {
                Outline outline = viewport.GetBoxOutline();
                if (outline != null)
                {
                    row["bounds"] = new Dictionary<string, object>
                    {
                        {
                            "min",
                            new Dictionary<string, object>
                            {
                                { "x", outline.MinimumPoint.X },
                                { "y", outline.MinimumPoint.Y },
                            }
                        },
                        {
                            "max",
                            new Dictionary<string, object>
                            {
                                { "x", outline.MaximumPoint.X },
                                { "y", outline.MaximumPoint.Y },
                            }
                        },
                        { "width", outline.MaximumPoint.X - outline.MinimumPoint.X },
                        { "height", outline.MaximumPoint.Y - outline.MinimumPoint.Y },
                    };
                }
            }
            catch (Exception)
            {
                // Same: nothing to report rather than a fabricated zero.
            }

            return row;
        }

        /// <summary>
        /// Every legend view in the document, by name. Legends have no dedicated API type - the
        /// Revit API exposes no ViewLegend - so they are found by ViewType, which is the only thing
        /// that tells one from a drafting view.
        /// </summary>
        private static List<View> LegendViews(Document document)
        {
            List<View> legends = new List<View>();

            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(View)))
            {
                View view = element as View;
                if (view == null || view.IsTemplate || view.ViewType != ViewType.Legend)
                {
                    continue;
                }

                legends.Add(view);
            }

            legends.Sort(delegate (View left, View right)
            {
                return string.Compare(
                    RevitFacts.SafeName(left),
                    RevitFacts.SafeName(right),
                    StringComparison.OrdinalIgnoreCase);
            });

            return legends;
        }

        /// <summary>
        /// The ViewFamilyType a plan is created from: the one named, or the first FloorPlan type
        /// when nothing is asked for.
        ///
        /// ViewPlan.Create documents which families it takes - "the type needs to be a FloorPlan,
        /// CeilingPlan, AreaPlan, or StructuralPlan ViewType" - so a type outside those four is
        /// refused here, with the names that would have worked, instead of surfacing as Revit's
        /// "This view family type is not a plan view type".
        /// </summary>
        private static ElementId ResolvePlanViewFamilyTypeId(Document document, string name)
        {
            if (name == null)
            {
                return ResolveViewFamilyTypeId(document, ViewFamily.FloorPlan, "floor plan");
            }

            List<string> available = new List<string>();

            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(ViewFamilyType)))
            {
                ViewFamilyType type = element as ViewFamilyType;
                if (type == null || !IsPlanViewFamily(type.ViewFamily))
                {
                    continue;
                }

                string typeName = RevitFacts.SafeName(type);
                if (typeName == null)
                {
                    continue;
                }

                if (string.Equals(typeName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return type.Id;
                }

                available.Add(typeName);
            }

            available.Sort(StringComparer.OrdinalIgnoreCase);

            throw BridgeException.BadRequest(
                "Unknown plan view family type \"" + name + "\". Plan view family types in this "
                    + "document: " + string.Join(", ", available) + ". The name is the one Revit "
                    + "shows for the type - \"Site\" and \"Floor Plan\" are both FloorPlan-family "
                    + "types and only the name tells them apart.");
        }

        private static bool IsPlanViewFamily(ViewFamily family)
        {
            return family == ViewFamily.FloorPlan
                || family == ViewFamily.CeilingPlan
                || family == ViewFamily.AreaPlan
                || family == ViewFamily.StructuralPlan;
        }

        private static ElementId ResolveViewFamilyTypeId(Document document, ViewFamily family, string label)
        {
            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(ViewFamilyType)))
            {
                ViewFamilyType type = element as ViewFamilyType;
                if (type != null && type.ViewFamily == family)
                {
                    return type.Id;
                }
            }

            throw new BridgeException(
                409,
                "NO_VIEW_FAMILY_TYPE",
                "This document has no " + label + " view family type, so no " + label + " can be "
                    + "created. That normally means the project was made from a template without "
                    + "one.");
        }

        /// <summary>
        /// The ViewOrientation3D for a camera standing at <paramref name="eye"/> and looking at
        /// <paramref name="target"/>.
        ///
        /// ViewOrientation3D takes (eyePosition, upDirection, forwardDirection) and Revit requires
        /// up perpendicular to forward, so the three are built rather than passed through:
        ///
        ///   forward = normalize(target - eye)   the direction the camera looks
        ///   right   = normalize(forward x Z)    horizontal; +X when the camera looks north
        ///   up      = right x forward           world up, tilted with the camera
        ///
        /// A camera looking straight down or straight up has no horizontal right vector at all -
        /// forward x Z is zero there - so that one case falls back to +X, which puts north at the
        /// top of the image the way a plan does.
        ///
        /// View3D.ViewDirection then reads back as -forward: Revit's view direction points from
        /// the model back at the viewer, which is why the response carries it.
        /// </summary>
        private static ViewOrientation3D Orientation(XYZ eye, XYZ target)
        {
            XYZ forward = (target - eye).Normalize();

            XYZ right = forward.CrossProduct(XYZ.BasisZ);
            right = right.IsZeroLength() ? XYZ.BasisX : right.Normalize();

            XYZ up = right.CrossProduct(forward).Normalize();

            return new ViewOrientation3D(eye, up, forward);
        }

        /// <summary>
        /// The bounding box of everything modelled in the document, as
        /// {min: {x, y, z}, max: {x, y, z}, center: {x, y, z}}, or null for an empty model.
        ///
        /// View-independent elements only, and only the ones with a model bounding box: a view, a
        /// schedule and a text note have no place in the extents a camera is aimed at.
        /// </summary>
        private static Dictionary<string, object> ModelExtents(Document document)
        {
            XYZ min = null;
            XYZ max = null;

            FilteredElementCollector collector = new FilteredElementCollector(document)
                .WhereElementIsNotElementType()
                .WhereElementIsViewIndependent();

            foreach (Element element in collector)
            {
                // Model categories only. A level, a grid and the sun path all have bounding boxes
                // the size of the site or bigger, and none of them is something a camera is aimed
                // at - leaving them in makes the extents useless for the one thing they are for.
                Category category = element.Category;
                if (category == null || category.CategoryType != CategoryType.Model)
                {
                    continue;
                }

                // Measured against this model, not assumed: a ViewSheet reports a Model category
                // and a bounding box at z = -1000, and a camera - the glyph that stands for a 3D
                // view - sits above everything built. Both would swamp the extents, and neither is
                // something a camera gets aimed at.
                if (element is View || category.Id.Value == (long)BuiltInCategory.OST_Cameras)
                {
                    continue;
                }

                BoundingBoxXYZ box = element.get_BoundingBox(null);
                if (box == null || !box.Enabled)
                {
                    continue;
                }

                if (min == null)
                {
                    min = box.Min;
                    max = box.Max;
                    continue;
                }

                min = new XYZ(
                    Math.Min(min.X, box.Min.X),
                    Math.Min(min.Y, box.Min.Y),
                    Math.Min(min.Z, box.Min.Z));

                max = new XYZ(
                    Math.Max(max.X, box.Max.X),
                    Math.Max(max.Y, box.Max.Y),
                    Math.Max(max.Z, box.Max.Z));
            }

            if (min == null)
            {
                return null;
            }

            return new Dictionary<string, object>
            {
                { "min", Vector(min) },
                { "max", Vector(max) },
                { "center", Vector((min + max) / 2.0) },
            };
        }

        /// <summary>
        /// Revit's enum member for Hidden Line is "HLR", after hidden line removal. Nobody outside
        /// the API calls it that, so "HiddenLine" is accepted for it and everything else goes
        /// through Enum.TryParse.
        /// </summary>
        internal static DisplayStyle ResolveDisplayStyle(string style)
        {
            if (string.Equals(style, "HiddenLine", StringComparison.OrdinalIgnoreCase))
            {
                return DisplayStyle.HLR;
            }

            DisplayStyle parsed;
            if (Enum.TryParse<DisplayStyle>(style, true, out parsed) && parsed != DisplayStyle.Undefined)
            {
                return parsed;
            }

            throw BridgeException.BadRequest(
                "Unknown display style \"" + style + "\". Use Wireframe, HiddenLine, Shading, "
                    + "ShadingWithEdges, Realistic, RealisticWithEdges, FlatColors or Rendering. "
                    + "Rendering shows the last rendered image, which the API cannot produce - "
                    + "Realistic is what makes an exported image look like the model.");
        }

        internal static ViewDetailLevel ResolveDetailLevel(string detailLevel)
        {
            if (detailLevel == null)
            {
                return ViewDetailLevel.Undefined;
            }

            ViewDetailLevel parsed;
            if (Enum.TryParse<ViewDetailLevel>(detailLevel, true, out parsed)
                && parsed != ViewDetailLevel.Undefined)
            {
                return parsed;
            }

            throw BridgeException.BadRequest(
                "Unknown \"detailLevel\" value \"" + detailLevel + "\". Use Coarse, Medium or Fine.");
        }

        /// <summary>PNG unless asked otherwise; "JPEG" means the lossless one.</summary>
        private static ImageFileType ResolveImageFileType(string format)
        {
            if (format == null)
            {
                return ImageFileType.PNG;
            }

            if (string.Equals(format, "JPEG", StringComparison.OrdinalIgnoreCase)
                || string.Equals(format, "JPG", StringComparison.OrdinalIgnoreCase))
            {
                return ImageFileType.JPEGLossless;
            }

            ImageFileType parsed;
            if (Enum.TryParse<ImageFileType>(format, true, out parsed))
            {
                return parsed;
            }

            throw BridgeException.BadRequest(
                "Unknown \"format\" value \"" + format + "\". Use PNG, JPEG, JPEGLossless, "
                    + "JPEGMedium, JPEGSmallest, BMP, TIFF or TARGA.");
        }

        private static string ImageExtension(ImageFileType fileType)
        {
            switch (fileType)
            {
                case ImageFileType.BMP:
                    return ".bmp";
                case ImageFileType.TIFF:
                    return ".tif";
                case ImageFileType.TARGA:
                    return ".tga";
                case ImageFileType.JPEGLossless:
                case ImageFileType.JPEGMedium:
                case ImageFileType.JPEGSmallest:
                    return ".jpg";
                default:
                    return ".png";
            }
        }

        /// <summary>
        /// The folder images go in: the one in "path" when it names a file or a directory, and
        /// &lt;user profile&gt;\RevitProjects\renders otherwise. Deliberately derived from the
        /// profile rather than hard-coded, so it is the caller's own folder on any machine.
        /// </summary>
        private static string ResolveExportDirectory(string path)
        {
            if (path == null)
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "RevitProjects",
                    "renders");
            }

            // No extension means the caller named a folder, not a file.
            if (string.IsNullOrEmpty(Path.GetExtension(path)))
            {
                return Path.GetFullPath(path);
            }

            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            return string.IsNullOrEmpty(directory) ? Directory.GetCurrentDirectory() : directory;
        }

        /// <summary>
        /// The stem Revit appends the view to, or null to use the view's own name. A "path" that
        /// named a folder has no stem.
        /// </summary>
        private static string ResolveExportStem(string path)
        {
            if (path == null || string.IsNullOrEmpty(Path.GetExtension(path)))
            {
                return null;
            }

            return Path.GetFileNameWithoutExtension(path);
        }

        /// <summary>
        /// Exports one view and reports the file Revit actually wrote.
        ///
        /// Revit appends its own suffix - the view type and the view name - to whatever FilePath
        /// it is given, so the requested path is not the written path and reporting it would be a
        /// lie. The directory is listed before and after instead, and the file that appeared (or,
        /// for a re-export over an existing one, the file that changed) is what comes back.
        ///
        /// That is also why the batch is one export per view rather than one export of a set: with
        /// one view in flight there is exactly one file to attribute.
        /// </summary>
        private static Dictionary<string, object> ExportOne(
            Document document,
            View view,
            string directory,
            string stem,
            ImageFileType fileType,
            FitDirectionType fitDirection,
            int pixelSize)
        {
            // The document title, the way Revit's own export dialog names a file. Deliberately not
            // ImageExportOptions.GetFileName: that returns the SUFFIX Revit is about to append
            // (" - 3D View - <name>"), so using it as the stem gets that suffix written twice -
            // "renders\ - 3D View - Iso - 3D View - Iso.png", which is what it actually did here
            // before this was fixed.
            string name = stem == null ? document.Title : stem;

            string requested = Path.Combine(directory, name + ImageExtension(fileType));

            Dictionary<string, DateTime> before = SnapshotDirectory(directory);

            ImageExportOptions options = new ImageExportOptions();
            options.ExportRange = ExportRange.SetOfViews;
            options.SetViewsAndSheets(new List<ElementId> { view.Id });
            options.FilePath = requested;
            options.ZoomType = ZoomFitType.FitToPage;
            options.FitDirection = fitDirection;
            options.PixelSize = pixelSize;

            // Both, always: Revit picks the one that matches how the view is drawn, and a shaded
            // view exported with only HLRandWFViewsFileType set comes out in the other format.
            options.HLRandWFViewsFileType = fileType;
            options.ShadowViewsFileType = fileType;
            options.ShouldCreateWebSite = false;

            document.ExportImage(options);

            string written = FindWrittenFile(directory, before, name);
            if (written == null)
            {
                throw new BridgeException(
                    500,
                    "EXPORT_PRODUCED_NO_FILE",
                    "Revit reported no error exporting view \"" + RevitFacts.SafeName(view) + "\" ("
                        + view.Id.Value + ") but no file appeared in \"" + directory + "\".");
            }

            FileInfo info = new FileInfo(written);

            Dictionary<string, object> row = new Dictionary<string, object>
            {
                { "viewId", view.Id.Value },
                { "viewName", RevitFacts.SafeName(view) },

                // The file Revit wrote, not options.FilePath - see the summary.
                { "path", written },
                { "requestedPath", requested },
                { "bytes", info.Length },
                { "width", null },
                { "height", null },
            };

            int[] size = PngSize(written);
            if (size != null)
            {
                row["width"] = size[0];
                row["height"] = size[1];
            }

            return row;
        }

        private static Dictionary<string, DateTime> SnapshotDirectory(string directory)
        {
            Dictionary<string, DateTime> files =
                new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            foreach (string file in Directory.GetFiles(directory))
            {
                files[file] = File.GetLastWriteTimeUtc(file);
            }

            return files;
        }

        /// <summary>
        /// The file the export produced: one that was not there before, or one whose timestamp
        /// moved because the export overwrote it. A name carrying the stem Revit was given wins
        /// over one that does not, and the newest wins among equals.
        /// </summary>
        private static string FindWrittenFile(
            string directory,
            Dictionary<string, DateTime> before,
            string stem)
        {
            string best = null;
            bool bestMatchesStem = false;
            DateTime bestWritten = DateTime.MinValue;

            foreach (string file in Directory.GetFiles(directory))
            {
                DateTime previous;
                DateTime written = File.GetLastWriteTimeUtc(file);

                if (before.TryGetValue(file, out previous) && previous == written)
                {
                    continue;
                }

                bool matchesStem = Path.GetFileName(file).IndexOf(stem, StringComparison.OrdinalIgnoreCase) >= 0;

                if (best != null && bestMatchesStem && !matchesStem)
                {
                    continue;
                }

                if (best != null && bestMatchesStem == matchesStem && written <= bestWritten)
                {
                    continue;
                }

                best = file;
                bestMatchesStem = matchesStem;
                bestWritten = written;
            }

            return best;
        }

        /// <summary>
        /// Width and height out of a PNG's IHDR chunk, or null when the file is not a PNG. The
        /// only honest source for what an export came out at: Revit is given one PixelSize along
        /// one direction and works the other one out from the view.
        /// </summary>
        private static int[] PngSize(string path)
        {
            byte[] header = new byte[24];

            using (FileStream stream = File.OpenRead(path))
            {
                int read = stream.Read(header, 0, header.Length);
                if (read < header.Length)
                {
                    return null;
                }
            }

            if (header[0] != 0x89 || header[1] != 'P' || header[2] != 'N' || header[3] != 'G')
            {
                return null;
            }

            // Big-endian, straight after the 8-byte signature, the 4-byte length and "IHDR".
            int width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            int height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];

            return new int[] { width, height };
        }

        private static ViewDuplicateOption ResolveDuplicateOption(string detailing)
        {
            if (detailing == null)
            {
                return ViewDuplicateOption.Duplicate;
            }

            ViewDuplicateOption option;
            if (Enum.TryParse<ViewDuplicateOption>(detailing, true, out option))
            {
                return option;
            }

            throw BridgeException.BadRequest(
                "Unknown \"detailing\" value \"" + detailing + "\". Use Duplicate, WithDetailing or "
                    + "AsDependent.");
        }


        /// <summary>
        /// The friendly category names views/hide-categories accepts, in the order the "annotation"
        /// shorthand expands to. Mapped to BuiltInCategory rather than looked up by display name on
        /// purpose: the display names are localised and "Levels" has to keep working on a Revit
        /// that calls them something else.
        /// </summary>
        private static readonly KeyValuePair<string, BuiltInCategory>[] HideableCategories =
        {
            new KeyValuePair<string, BuiltInCategory>("Levels", BuiltInCategory.OST_Levels),
            new KeyValuePair<string, BuiltInCategory>("Grids", BuiltInCategory.OST_Grids),
            new KeyValuePair<string, BuiltInCategory>("ReferencePlanes", BuiltInCategory.OST_CLines),
            new KeyValuePair<string, BuiltInCategory>("Sections", BuiltInCategory.OST_Sections),
            new KeyValuePair<string, BuiltInCategory>("Elevations", BuiltInCategory.OST_Elev),
            new KeyValuePair<string, BuiltInCategory>("Cameras", BuiltInCategory.OST_Cameras),
            new KeyValuePair<string, BuiltInCategory>("SunPath", BuiltInCategory.OST_SunPath1),
            new KeyValuePair<string, BuiltInCategory>("Lines", BuiltInCategory.OST_Lines),
        };

        /// <summary>
        /// The ViewDisplayBackground the request asks for. "sky" takes no colours at all - that is
        /// the factory's signature, not a simplification here - and saying so is better than
        /// accepting three the caller will never see.
        ///
        /// The gradient defaults are a daylight sky: sky 70/130/190, horizon haze 205/225/240,
        /// dry ground 130/120/105.
        /// </summary>
        private static ViewDisplayBackground BuildBackground(JsonElement body, string kind)
        {
            if (string.Equals(kind, "sky", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "SunAndClouds", StringComparison.OrdinalIgnoreCase))
            {
                JsonElement colour;
                if (JsonBody.TryGet(body, "skyColor", out colour)
                    || JsonBody.TryGet(body, "horizonColor", out colour)
                    || JsonBody.TryGet(body, "groundColor", out colour))
                {
                    throw BridgeException.BadRequest(
                        "ViewDisplayBackground.CreateSky() takes no parameters: Revit's own sky and "
                            + "clouds are not colourable. Drop skyColor/horizonColor/groundColor, "
                            + "or use kind \"gradient\", which takes all three.");
                }

                return ViewDisplayBackground.CreateSky();
            }

            if (string.Equals(kind, "gradient", StringComparison.OrdinalIgnoreCase))
            {
                return ViewDisplayBackground.CreateGradient(
                    OptionalColor(body, "skyColor", 70, 130, 190),
                    OptionalColor(body, "horizonColor", 205, 225, 240),
                    OptionalColor(body, "groundColor", 130, 120, 105));
            }

            if (string.Equals(kind, "image", StringComparison.OrdinalIgnoreCase))
            {
                string imagePath = JsonBody.RequireString(body, "imagePath");

                if (!File.Exists(imagePath))
                {
                    throw BridgeException.NotFound(
                        "FILE_NOT_FOUND",
                        "No image file at \"" + imagePath + "\". Revit reads a background image off "
                            + "disk every time it draws the view, so the file has to be there and "
                            + "has to stay there.");
                }

                return ViewDisplayBackground.CreateImage(
                    imagePath,
                    ViewDisplayBackgroundImageFlags.FitToScreen,
                    new UV(0.0, 0.0),
                    new UV(1.0, 1.0));
            }

            throw BridgeException.BadRequest(
                "Unknown \"kind\" value \"" + kind + "\". Use \"sky\" for Revit's own sky and "
                    + "clouds, \"gradient\" for skyColor/horizonColor/groundColor, or \"image\" "
                    + "for imagePath.");
        }

        /// <summary>
        /// What a background actually is, read off the view. Only the fields that belong to the
        /// type it came back as: the properties for the other kinds hold whatever Revit last had
        /// and reporting them would be inventing a background the view does not have.
        /// </summary>
        internal static Dictionary<string, object> ReadBackground(ViewDisplayBackground background)
        {
            Dictionary<string, object> row = new Dictionary<string, object>();

            if (background == null)
            {
                row["kind"] = null;
                return row;
            }

            row["kind"] = background.Type.ToString();

            if (background.Type == ViewDisplayBackgroundType.Gradient)
            {
                // CreateGradient takes (sky, horizon, ground) but the properties are named
                // SkyColor, BackgroundColor and GroundColor - the middle band is "BackgroundColor".
                row["skyColor"] = ReadColor(background.SkyColor);
                row["horizonColor"] = ReadColor(background.BackgroundColor);
                row["groundColor"] = ReadColor(background.GroundColor);
            }

            if (background.Type == ViewDisplayBackgroundType.Image)
            {
                row["imagePath"] = background.ImagePath;
                row["imageFlags"] = background.ImageFlags.ToString();
            }

            return row;
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

        private static Color OptionalColor(JsonElement body, string name, byte red, byte green, byte blue)
        {
            JsonElement value;
            if (!JsonBody.TryGet(body, name, out value))
            {
                return new Color(red, green, blue);
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
                throw BridgeException.BadRequest(
                    "\"" + name + "\" must be an object {r, g, b}, but was " + value.ValueKind + ".");
            }

            return new Color(
                ColorChannel(value, "r", name),
                ColorChannel(value, "g", name),
                ColorChannel(value, "b", name));
        }

        private static byte ColorChannel(JsonElement color, string channel, string label)
        {
            double raw = JsonBody.RequireDouble(color, channel);
            int value = (int)Math.Round(raw);

            if (value < 0 || value > 255)
            {
                throw BridgeException.BadRequest(
                    "\"" + label + "." + channel + "\" must be between 0 and 255, but was " + raw + ".");
            }

            return (byte)value;
        }

        /// <summary>
        /// Expands "annotation" and resolves every name to a BuiltInCategory, keeping the caller's
        /// own spelling as the key so the response rows say back what was asked for. A name that is
        /// neither friendly nor a BuiltInCategory is a BAD_REQUEST for the whole call - that is a
        /// typo, and half a request landing is worse than none of it.
        /// </summary>
        private static List<KeyValuePair<string, BuiltInCategory>> ResolveHideCategories(
            List<string> requested)
        {
            List<KeyValuePair<string, BuiltInCategory>> targets =
                new List<KeyValuePair<string, BuiltInCategory>>();

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string name in requested)
            {
                if (string.Equals(name, "annotation", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (KeyValuePair<string, BuiltInCategory> entry in HideableCategories)
                    {
                        if (seen.Add(entry.Key))
                        {
                            targets.Add(entry);
                        }
                    }

                    continue;
                }

                BuiltInCategory builtIn;
                if (!TryResolveHideCategory(name, out builtIn))
                {
                    throw BridgeException.BadRequest(
                        "Unknown category \"" + name + "\". " + AcceptedCategories());
                }

                if (seen.Add(name))
                {
                    targets.Add(new KeyValuePair<string, BuiltInCategory>(name, builtIn));
                }
            }

            return targets;
        }

        private static bool TryResolveHideCategory(string name, out BuiltInCategory builtIn)
        {
            foreach (KeyValuePair<string, BuiltInCategory> entry in HideableCategories)
            {
                if (string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    builtIn = entry.Value;
                    return true;
                }
            }

            return Enum.TryParse<BuiltInCategory>(name, true, out builtIn)
                && Category.IsBuiltInCategoryValid(builtIn);
        }

        /// <summary>
        /// Revit's own display name for a category when the document has one, and the
        /// BuiltInCategory name when it does not - which is the case for several a view can still
        /// hide. Only ever used to word a refusal.
        /// </summary>
        private static string CategoryLabel(Document document, BuiltInCategory builtIn)
        {
            Category category;

            try
            {
                category = Category.GetCategory(document, builtIn);
            }
            catch (Exception)
            {
                // Not every BuiltInCategory exists in every document; fall back to the enum name.
                category = null;
            }

            if (category == null)
            {
                return builtIn.ToString();
            }

            return "\"" + category.Name + "\"";
        }

        private static string AcceptedCategories()
        {
            List<string> names = new List<string>();
            foreach (KeyValuePair<string, BuiltInCategory> entry in HideableCategories)
            {
                names.Add(entry.Key);
            }

            return "Accepted: " + string.Join(", ", names) + ", the shorthand \"annotation\" for all "
                + "of them at once, or any literal OST_* BuiltInCategory name.";
        }

        private static double Degrees(double radians)
        {
            return radians * 180.0 / Math.PI;
        }

        /// <summary>
        /// A compass bearing in the same 0-360 the request takes. GetFrameAzimuth returns a signed
        /// angle - a live Still Image on 21 June read -93.89 - and reporting that back against an
        /// input documented as 0-360 would be two conventions in one response.
        /// </summary>
        private static double AzimuthDegrees(double radians)
        {
            double degrees = Degrees(radians) % 360.0;
            return degrees < 0.0 ? degrees + 360.0 : degrees;
        }

        /// <summary>
        /// The date and time the sun is asked to stand at, built on top of whatever the settings
        /// already hold so "time" alone keeps the day and "date" alone keeps the hour.
        /// </summary>
        private static DateTime SunDateTime(SunAndShadowSettings settings, string date, string time)
        {
            DateTime current = settings.StartDateAndTime;

            DateTime day = current.Date;
            if (date != null && !DateTime.TryParseExact(
                    date,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out day))
            {
                throw BridgeException.BadRequest(
                    "\"date\" must be YYYY-MM-DD, e.g. \"2026-06-21\"; got \"" + date + "\".");
            }

            TimeSpan clock = current.TimeOfDay;
            if (time != null)
            {
                DateTime parsed;
                if (!DateTime.TryParseExact(
                        time,
                        new string[] { "HH:mm", "H:mm", "HH:mm:ss" },
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out parsed))
                {
                    throw BridgeException.BadRequest(
                        "\"time\" must be HH:MM on a 24-hour clock, e.g. \"15:30\"; got \"" + time
                            + "\".");
                }

                clock = parsed.TimeOfDay;
            }

            // Kind matters: Revit's setter throws ArgumentException "Revit does not accept input
            // DateTime objects if they are of kind DateTypeKind.Unspecified" - which is what
            // DateTime.TryParseExact produces - and Local is what a wall-clock time on site means.
            return DateTime.SpecifyKind(day.Date + clock, DateTimeKind.Local);
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
