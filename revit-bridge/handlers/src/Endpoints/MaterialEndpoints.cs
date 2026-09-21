using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using Autodesk.Revit.UI;

namespace RevitMcpBridge.Endpoints
{
    /// <summary>
    /// Materials: reading what the document has, authoring new ones, and putting one onto elements
    /// that already exist.
    ///
    /// This exists because nothing else here set a material, so everything the bridge built
    /// rendered in Revit's default grey. There are two honest routes onto an element and they are
    /// not interchangeable:
    ///
    ///   - Geometry built by this bridge carries its material on the Solid. That is decided when
    ///     the solid is built - "materialId" on directshape/create, planting/place, pipes/create
    ///     and sprinklers/place - and cannot be changed afterwards, because a Solid's material is
    ///     fixed at construction. See DirectShapeEndpoints.MaterialOptions.
    ///   - A wall or a floor takes its material from its TYPE's CompoundStructure, which is what
    ///     walltypes/create and floortypes/create author, and what materials/assign edits for an
    ///     element that already exists.
    ///
    /// materials/assign says which of those it used per element, and says plainly which elements
    /// could take neither.
    ///
    /// Colours are 0-255 per channel; transparency is 0-100 and shininess 0-128, which are Revit's
    /// own ranges for those properties.
    ///
    /// A colour alone still renders as flat paint, which is what materials/set-texture is for: it
    /// connects a real bitmap to the material's appearance asset. See SetTexture for why that goes
    /// through a Generic asset this endpoint authors rather than through whatever asset the
    /// material happened to arrive with.
    /// </summary>
    internal static class MaterialEndpoints
    {
        /// <summary>
        /// Body: {}. Every material in the document, by name: {id, name, colorRgb,
        /// appearanceAssetId}.
        ///
        /// "colorRgb" is null when the material carries no valid colour, and "appearanceAssetId" is
        /// null when it has no AppearanceAssetElement - which, per the API documentation, is the
        /// normal state of a material created through the API, and the case in which Color and
        /// Transparency alone dictate how it renders.
        /// </summary>
        internal static object Materials(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            List<Material> materials = new List<Material>();

            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(Material)))
            {
                Material material = element as Material;
                if (material != null)
                {
                    materials.Add(material);
                }
            }

            materials.Sort(delegate (Material left, Material right)
            {
                return string.Compare(
                    RevitFacts.SafeName(left),
                    RevitFacts.SafeName(right),
                    StringComparison.OrdinalIgnoreCase);
            });

            List<object> rows = new List<object>();

            foreach (Material material in materials)
            {
                rows.Add(new Dictionary<string, object>
                {
                    { "id", material.Id.Value },
                    { "name", RevitFacts.SafeName(material) },
                    { "colorRgb", ReadColor(material.Color) },
                    { "appearanceAssetId", OptionalId(material.AppearanceAssetId) },
                });
            }

            return rows;
        }

        /// <summary>
        /// Body: {name, color: {r, g, b}, transparency?, shininess?, surfaceForegroundPatternId?,
        /// appearanceAssetId?, texturePath?}.
        ///
        /// A material already called "name" is reused rather than duplicated and comes back with
        /// "created": false and its CURRENT properties - nothing about it is overwritten. Re-running
        /// the same call is therefore safe, and a caller that gets back a colour it did not ask for
        /// is looking at a material somebody else authored, which is worth knowing rather than
        /// silently trampling.
        ///
        /// UseRenderAppearanceForShading is set false for a colour-only material: it is documented
        /// as the switch between "shaded views use the render appearance" and "shaded views use
        /// Color and Transparency", and a material created here for its colour is no use if shaded
        /// views ignore that colour. It is set true when the caller supplied an appearance asset,
        /// because then the asset is the point.
        ///
        /// "appearanceAssetId" is an AppearanceAssetElement to copy the look of: it is DUPLICATED
        /// (AppearanceAssetElement.Duplicate, which also duplicates the asset it holds) and the copy
        /// is assigned, so editing one material's appearance later cannot leak into another's.
        ///
        /// "texturePath" makes the material textured in the same call - the bitmap goes on through
        /// the same route as materials/set-texture, at Revit's own tile size, and what it did comes
        /// back under "texture". A file that does not exist is TEXTURE_NOT_FOUND and is refused
        /// BEFORE the material is created, so a bad path leaves nothing behind. It does nothing for
        /// a material that already exists: that one comes back untouched, as it always does, and
        /// materials/set-texture is the way to texture it. Scale, rotation and tint live there too.
        /// </summary>
        internal static object CreateMaterial(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            string name = JsonBody.RequireString(body, "name");
            Color color = RequireColor(body, "color");
            int transparency = OptionalRange(body, "transparency", 0, 100);
            int shininess = OptionalRange(body, "shininess", 0, 128);
            ElementId patternId = OptionalPatternId(document, body);
            AppearanceAssetElement asset = OptionalAppearanceAsset(document, body);
            TextureRequest texture = OptionalTexture(body);

            Material existing = FindMaterial(document, name);
            if (existing != null)
            {
                return Describe(existing, false);
            }

            return RevitWrite.InGroup(document, "MCP: create material", delegate
            {
                Material created = null;

                RevitWrite.InTransaction(document, "Create material", delegate
                {
                    ElementId id;

                    try
                    {
                        id = Material.Create(document, name);
                    }
                    catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                    {
                        throw BridgeException.BadRequest(
                            "Revit would not create a material named \"" + name + "\": " + ex.Message);
                    }

                    Material material = (Material)document.GetElement(id);

                    material.Color = color;

                    if (transparency >= 0)
                    {
                        material.Transparency = transparency;
                    }

                    if (shininess >= 0)
                    {
                        material.Shininess = shininess;
                    }

                    if (patternId != null)
                    {
                        material.SurfaceForegroundPatternId = patternId;
                    }

                    if (asset != null)
                    {
                        material.AppearanceAssetId = Duplicate(document, asset, name).Id;
                    }

                    material.UseRenderAppearanceForShading = asset != null;

                    created = material;
                });

                Dictionary<string, object> applied = null;

                if (texture != null)
                {
                    // After the material exists and outside its transaction: an edit scope opens
                    // transactions of its own. Described afterwards, so "appearanceAssetId" is the
                    // asset the bitmap actually landed on.
                    applied = new Dictionary<string, object>();
                    ApplyTexture(app, document, created, texture, applied);
                }

                Dictionary<string, object> description = Describe(created, true);

                if (applied != null)
                {
                    description["texture"] = applied;
                }

                return description;
            });
        }

        /// <summary>
        /// Body: {materialId | materialName, elementIds: [...]}.
        ///
        /// Two routes, chosen per element and reported per element:
        ///
        ///   - "type": anything whose type is a HostObjAttributes - a wall, a floor, a roof, a
        ///     ceiling, and a Toposolid too, since ToposolidType is one - takes its material from
        ///     the type's CompoundStructure, so every layer of that structure is set to the
        ///     material. A type named directly is edited the same way. This changes EVERY element
        ///     of that type; the row says which type was edited precisely so that is visible
        ///     rather than a surprise.
        ///   - "parameter": an element with a writable material parameter - Structural Material,
        ///     Material, or a family's own - gets it written there.
        ///
        /// Anything else lands in "skipped" with the reason. A DirectShape is the important one:
        /// its material lives on each Solid and is fixed when the solid is built, so the fix is to
        /// pass "materialId" to the endpoint that builds it, not to assign afterwards.
        ///
        /// One inner transaction per element, so an element Revit refuses rolls back alone.
        /// </summary>
        internal static object AssignMaterial(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            ElementId materialId = OptionalMaterialId(document, body);
            if (materialId == null)
            {
                throw BridgeException.BadRequest(
                    "\"materialId\" is required (or \"materialName\"). Call /revit-mcp/materials for "
                        + "the materials in this document.");
            }

            List<long> ids = JsonBody.RequireIds(body, "elementIds");

            Material material = (Material)document.GetElement(materialId);

            return RevitWrite.InGroup(document, "MCP: assign material", delegate
            {
                List<object> assigned = new List<object>();
                List<object> skipped = new List<object>();

                foreach (long id in ids)
                {
                    Element element = document.GetElement(new ElementId(id));

                    if (element == null)
                    {
                        skipped.Add(Skip(id, "No element with id " + id + " exists in " + document.Title + "."));
                        continue;
                    }

                    try
                    {
                        Assign(document, element, materialId, assigned, skipped);
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                    {
                        // The inner transaction rolled back; the rest of the batch carries on.
                        skipped.Add(Skip(id, ex.Message));
                    }
                }

                return new Dictionary<string, object>
                {
                    { "materialId", materialId.Value },
                    { "materialName", RevitFacts.SafeName(material) },
                    { "assigned", assigned },
                    { "skipped", skipped },
                };
            });
        }

        /// <summary>
        /// Body: {materialId | materialName, texturePath, scale?: {x, y}, rotation?, tint?: {r,g,b}}.
        ///
        /// Puts a real bitmap on a material, which is the difference between a surface that is
        /// green and a surface that looks like grass. The bitmap is REFERENCED, not copied into the
        /// model, so "texturePath" has to be a file this machine can still read later - it is
        /// checked here and a missing one is TEXTURE_NOT_FOUND rather than an asset pointing at
        /// nothing. Revit's own texture library ships under C:\Program Files\Common Files\Autodesk
        /// Shared\Materials\Textures.
        ///
        /// "scale" is the real-world size of one tile of the bitmap, {x, y} in feet like every
        /// other length here; Revit stores it in inches, so it is converted rather than written
        /// raw, and x != y also unlocks texture_ScaleLock, which otherwise makes the two a lie.
        /// "rotation" is degrees. "tint" multiplies a colour over the bitmap.
        ///
        /// The schema problem, and how this answers it: an appearance asset's properties depend on
        /// the schema it was built from - Generic has "generic_diffuse", Ceramic has
        /// "ceramic_color", Water has no bitmap-bearing colour property at all - so editing
        /// whatever asset a material happens to carry means guessing. Instead the material is given
        /// an asset created from Revit's own library "Generic" asset unless it already has a
        /// Generic one to itself, and the report says whether that happened and why. Only if the
        /// library has no Generic to offer does this fall back to the asset in hand, trying the
        /// diffuse property of each schema it knows. Anything the schema turns out not to carry is
        /// reported in "missing" instead of thrown over.
        ///
        /// Revit repaints a material's shading colour to match a new appearance asset - to black,
        /// for a freshly created Generic one - so the colour the material had is written back
        /// afterwards and reported in "colorRgb". This endpoint textures a material; it does not
        /// repaint it.
        ///
        /// The report is deliberately verbose: "set" is every property this wrote, and "verified"
        /// is what the committed asset reads back as, so a caller can tell a texture that landed
        /// from one that only looked like it did.
        /// </summary>
        internal static object SetTexture(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            ElementId materialId = OptionalMaterialId(document, body);
            if (materialId == null)
            {
                throw BridgeException.BadRequest(
                    "\"materialId\" is required (or \"materialName\"). Call /revit-mcp/materials for "
                        + "the materials in this document.");
            }

            TextureRequest texture = RequireTexture(body);
            Material material = (Material)document.GetElement(materialId);

            return RevitWrite.InGroup(document, "MCP: set material texture", delegate
            {
                Dictionary<string, object> report = new Dictionary<string, object>
                {
                    { "materialId", material.Id.Value },
                    { "materialName", RevitFacts.SafeName(material) },
                };

                ApplyTexture(app, document, material, texture, report);

                return report;
            });
        }

        /// <summary>
        /// The material named by "materialId" or "materialName", or null when the body carries
        /// neither. Shared with the endpoints that build geometry and the ones that build wall and
        /// floor types, so a material is named the same way everywhere. "materialId" wins.
        /// </summary>
        internal static ElementId OptionalMaterialId(Document document, JsonElement body)
        {
            JsonElement value;
            if (JsonBody.TryGet(body, "materialId", out value))
            {
                long id = JsonBody.AsLong(value, "materialId");

                Material named = document.GetElement(new ElementId(id)) as Material;
                if (named == null)
                {
                    throw BridgeException.BadRequest(
                        "Element " + id + " is not a material in this document. Call "
                            + "/revit-mcp/materials for the ones that are.");
                }

                return named.Id;
            }

            string materialName = JsonBody.OptionalString(body, "materialName");
            if (materialName == null)
            {
                return null;
            }

            Material match = FindMaterial(document, materialName);
            if (match == null)
            {
                throw BridgeException.BadRequest(
                    "Unknown material \"" + materialName + "\". Call /revit-mcp/materials for the "
                        + "materials in this document, or create one with /revit-mcp/materials/create.");
            }

            return match.Id;
        }

        /// <summary>
        /// One element: the compound structure of its type when it has one, otherwise a writable
        /// material parameter, otherwise an entry in "skipped" saying why neither was available.
        /// </summary>
        private static void Assign(
            Document document,
            Element element,
            ElementId materialId,
            List<object> assigned,
            List<object> skipped)
        {
            long id = element.Id.Value;

            HostObjAttributes hostType = ResolveHostType(document, element);

            if (hostType != null)
            {
                CompoundStructure structure = hostType.GetCompoundStructure();

                if (structure == null || structure.LayerCount == 0)
                {
                    skipped.Add(Skip(
                        id,
                        "Type \"" + RevitFacts.SafeName(hostType) + "\" has no compound structure to "
                            + "carry a material - a curtain or stacked wall type carries its "
                            + "materials on its panels instead."));

                    return;
                }

                int layers = structure.LayerCount;

                RevitWrite.InTransaction(document, "Assign material", delegate
                {
                    // GetCompoundStructure hands out a copy, so this is edited and written back.
                    CompoundStructure edited = hostType.GetCompoundStructure();

                    for (int layer = 0; layer < edited.LayerCount; layer++)
                    {
                        edited.SetMaterialId(layer, materialId);
                    }

                    hostType.SetCompoundStructure(edited);
                });

                assigned.Add(new Dictionary<string, object>
                {
                    { "id", id },
                    { "route", "type" },
                    { "typeId", hostType.Id.Value },
                    { "typeName", RevitFacts.SafeName(hostType) },
                    { "layers", layers },

                    // Said out loud because it is not what "assign to this element" sounds like.
                    { "appliesToType", true },
                });

                return;
            }

            Parameter target = WritableMaterialParameter(element);

            if (target == null)
            {
                skipped.Add(Skip(id, NoMaterialReason(element)));
                return;
            }

            string parameterName = target.Definition == null ? null : target.Definition.Name;

            RevitWrite.InTransaction(document, "Assign material", delegate
            {
                target.Set(materialId);
            });

            assigned.Add(new Dictionary<string, object>
            {
                { "id", id },
                { "route", "parameter" },
                { "parameter", parameterName },
                { "appliesToType", false },
            });
        }

        /// <summary>
        /// The HostObjAttributes whose compound structure governs this element: the element itself
        /// when the caller named a wall or floor TYPE, otherwise the type of a wall, floor, roof or
        /// ceiling instance.
        /// </summary>
        private static HostObjAttributes ResolveHostType(Document document, Element element)
        {
            HostObjAttributes named = element as HostObjAttributes;
            if (named != null)
            {
                return named;
            }

            HostObject host = element as HostObject;
            if (host == null)
            {
                return null;
            }

            return document.GetElement(host.GetTypeId()) as HostObjAttributes;
        }

        private static Parameter WritableMaterialParameter(Element element)
        {
            Parameter structural = element.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM);
            if (IsWritableMaterial(structural))
            {
                return structural;
            }

            Parameter material = element.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
            if (IsWritableMaterial(material))
            {
                return material;
            }

            // A family may carry a material parameter of its author's own making, and it is
            // usually just called "Material".
            Parameter named = element.LookupParameter("Material");
            if (IsWritableMaterial(named))
            {
                return named;
            }

            return null;
        }

        private static bool IsWritableMaterial(Parameter parameter)
        {
            return parameter != null
                && !parameter.IsReadOnly
                && parameter.StorageType == StorageType.ElementId;
        }

        /// <summary>Why this element could not take a material - never a shrug.</summary>
        private static string NoMaterialReason(Element element)
        {
            if (element is DirectShape)
            {
                return "Element " + element.Id.Value + " is a DirectShape: its material is carried by "
                    + "each solid and fixed when the solid is built, so it cannot be assigned "
                    + "afterwards. Pass \"materialId\" to directshape/create, planting/place, "
                    + "pipes/create or sprinklers/place instead.";
            }

            return "Element " + element.Id.Value + " ("
                + (RevitFacts.CategoryName(element) == null ? "no category" : RevitFacts.CategoryName(element))
                + ") has no writable material parameter, and its type carries no compound structure "
                + "to put one in either.";
        }

        private static Dictionary<string, object> Skip(long id, string reason)
        {
            return new Dictionary<string, object>
            {
                { "id", id },
                { "reason", reason },
            };
        }

        private static Dictionary<string, object> Describe(Material material, bool created)
        {
            return new Dictionary<string, object>
            {
                { "id", material.Id.Value },
                { "name", RevitFacts.SafeName(material) },
                { "created", created },
                { "colorRgb", ReadColor(material.Color) },
                { "transparency", material.Transparency },
                { "shininess", material.Shininess },
                { "appearanceAssetId", OptionalId(material.AppearanceAssetId) },
            };
        }

        internal static Material FindMaterial(Document document, string name)
        {
            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(Material)))
            {
                if (string.Equals(RevitFacts.SafeName(element), name, StringComparison.OrdinalIgnoreCase))
                {
                    return element as Material;
                }
            }

            return null;
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

        private static object OptionalId(ElementId id)
        {
            if (id == null || id == ElementId.InvalidElementId)
            {
                return null;
            }

            return id.Value;
        }

        private static Color RequireColor(JsonElement body, string name)
        {
            JsonElement value;
            if (!JsonBody.TryGet(body, name, out value))
            {
                throw BridgeException.BadRequest(
                    "\"" + name + "\" is required and must be {r, g, b}, each channel 0-255.");
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
                throw BridgeException.BadRequest(
                    "\"" + name + "\" must be an object {r, g, b}, but was " + value.ValueKind + ".");
            }

            return new Color(
                Channel(value, "r", name),
                Channel(value, "g", name),
                Channel(value, "b", name));
        }

        private static byte Channel(JsonElement color, string channel, string label)
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
        /// An optional integer property in Revit's own range for it, or -1 when the caller did not
        /// ask for one - which is not the same as asking for 0. A new material has Revit's defaults
        /// for these and overwriting them with a value nobody asked for would be a silent edit. Out
        /// of range is refused here rather than left for Revit to clamp or throw over.
        /// </summary>
        private static int OptionalRange(JsonElement body, string name, int low, int high)
        {
            JsonElement raw;
            if (!JsonBody.TryGet(body, name, out raw))
            {
                return -1;
            }

            int value = (int)Math.Round(JsonBody.AsDouble(raw, name));

            if (value < low || value > high)
            {
                throw BridgeException.BadRequest(
                    "\"" + name + "\" must be between " + low + " and " + high + ", but was " + value + ".");
            }

            return value;
        }

        private static ElementId OptionalPatternId(Document document, JsonElement body)
        {
            JsonElement value;
            if (!JsonBody.TryGet(body, "surfaceForegroundPatternId", out value))
            {
                return null;
            }

            long id = JsonBody.AsLong(value, "surfaceForegroundPatternId");

            FillPatternElement pattern = document.GetElement(new ElementId(id)) as FillPatternElement;
            if (pattern == null)
            {
                throw BridgeException.BadRequest(
                    "Element " + id + " is not a fill pattern in this document, so it cannot be a "
                        + "surface foreground pattern.");
            }

            return pattern.Id;
        }

        private static AppearanceAssetElement OptionalAppearanceAsset(Document document, JsonElement body)
        {
            JsonElement value;
            if (!JsonBody.TryGet(body, "appearanceAssetId", out value))
            {
                return null;
            }

            long id = JsonBody.AsLong(value, "appearanceAssetId");

            AppearanceAssetElement asset =
                document.GetElement(new ElementId(id)) as AppearanceAssetElement;

            if (asset == null)
            {
                throw BridgeException.BadRequest(
                    "Element " + id + " is not an appearance asset in this document. The materials "
                        + "listed by /revit-mcp/materials carry the ones that exist, in "
                        + "\"appearanceAssetId\".");
            }

            return asset;
        }

        /// <summary>
        /// A copy of an appearance asset, under a name no other asset in the document has -
        /// Duplicate refuses a name already in use, and the material's own name is the obvious
        /// first try.
        /// </summary>
        private static AppearanceAssetElement Duplicate(
            Document document,
            AppearanceAssetElement source,
            string name)
        {
            return source.Duplicate(UnusedAssetName(document, name));
        }

        /// <summary>
        /// A name no appearance asset in the document has - Create and Duplicate both refuse one
        /// already in use, and the material's own name is the obvious first try.
        /// </summary>
        private static string UnusedAssetName(Document document, string name)
        {
            string candidate = name;
            int suffix = 2;

            while (AppearanceAssetElement.GetAppearanceAssetElementByName(document, candidate) != null)
            {
                candidate = name + " " + suffix;
                suffix++;
            }

            return candidate;
        }

        /// <summary>The schema identifier of the asset Revit connects a bitmap through.</summary>
        private const string BitmapSchema = "UnifiedBitmap";

        /// <summary>
        /// The name of the library asset this endpoint builds a material's appearance from, and the
        /// whole reason the schema is predictable: "Generic" is the plain shading schema every
        /// Revit install ships, and it carries "generic_diffuse".
        /// </summary>
        private const string GenericAssetName = "Generic";

        /// <summary>
        /// The diffuse - base colour - property of each schema, tried in order. Generic first,
        /// because that is the schema this endpoint hands out. The rest only matter on the fallback
        /// path, where Revit's library had no Generic asset to offer and the asset already on the
        /// material is all there is to work with. Metal and Water are deliberately absent: their
        /// schemas carry no colour property a bitmap can connect to, which is a thing to report,
        /// not to fake.
        /// </summary>
        private static readonly string[] DiffuseProperties = new string[]
        {
            Generic.GenericDiffuse,
            AdvancedOpaque.OpaqueAlbedo,
            Ceramic.CeramicColor,
            Concrete.ConcreteColor,
            Hardwood.HardwoodColor,
            MasonryCMU.MasonryCMUColor,
            PlasticVinyl.PlasticvinylColor,
            Stone.StoneColor,
            WallPaint.WallpaintColor,
        };

        /// <summary>What the caller asked for: the bitmap, and the transform and tint to put on it.</summary>
        private sealed class TextureRequest
        {
            internal string Path;

            /// <summary>Feet, or -1 when the caller asked for no particular tile size.</summary>
            internal double ScaleX;

            internal double ScaleY;

            /// <summary>Degrees, or NaN when the caller asked for no rotation.</summary>
            internal double Rotation;

            /// <summary>null when the caller asked for no tint.</summary>
            internal Color Tint;
        }

        private static TextureRequest RequireTexture(JsonElement body)
        {
            TextureRequest texture = new TextureRequest();

            texture.Path = JsonBody.RequireString(body, "texturePath");

            // The asset stores a path, so a file that is not there now is an asset that renders
            // nothing later. Refused here, while the caller can still fix it.
            if (!File.Exists(texture.Path))
            {
                throw BridgeException.NotFound(
                    "TEXTURE_NOT_FOUND",
                    "No file exists at \"" + texture.Path + "\". The bitmap is referenced by the "
                        + "appearance asset rather than copied into the model, so it has to be a "
                        + "path this machine can read. Revit's own texture library ships under "
                        + "C:\\Program Files\\Common Files\\Autodesk Shared\\Materials\\Textures.");
            }

            texture.ScaleX = -1;
            texture.ScaleY = -1;

            JsonElement scale;
            if (JsonBody.TryGet(body, "scale", out scale))
            {
                if (scale.ValueKind != JsonValueKind.Object)
                {
                    throw BridgeException.BadRequest(
                        "\"scale\" must be an object {x, y} in feet, but was " + scale.ValueKind + ".");
                }

                texture.ScaleX = RequireScale(scale, "x");
                texture.ScaleY = RequireScale(scale, "y");
            }

            texture.Rotation = JsonBody.OptionalDouble(body, "rotation", double.NaN);

            JsonElement tint;
            if (JsonBody.TryGet(body, "tint", out tint))
            {
                texture.Tint = RequireColor(body, "tint");
            }

            return texture;
        }

        /// <summary>
        /// The texture materials/create was asked for, or null when it was asked for none. Scale,
        /// rotation and tint are materials/set-texture's business; here the bitmap goes on at
        /// Revit's own tile size.
        /// </summary>
        private static TextureRequest OptionalTexture(JsonElement body)
        {
            if (JsonBody.OptionalString(body, "texturePath") == null)
            {
                return null;
            }

            TextureRequest texture = new TextureRequest();
            texture.Path = RequireTexture(body).Path;
            texture.ScaleX = -1;
            texture.ScaleY = -1;
            texture.Rotation = double.NaN;

            return texture;
        }

        private static double RequireScale(JsonElement scale, string axis)
        {
            double value = JsonBody.RequireDouble(scale, axis);

            if (value <= 0)
            {
                throw BridgeException.BadRequest(
                    "\"scale." + axis + "\" is the real-world size of one tile of the bitmap, in "
                        + "feet, and must be greater than 0, but was " + value + ".");
            }

            return value;
        }

        /// <summary>
        /// Connects the bitmap and fills <paramref name="report"/> with what really happened: which
        /// asset was edited and whether this endpoint had to author it, which property carried the
        /// bitmap, every value written, everything the schema did not have, and what the committed
        /// asset reads back as. Runs inside the caller's transaction group and opens transactions
        /// of its own.
        /// </summary>
        private static void ApplyTexture(
            UIApplication app,
            Document document,
            Material material,
            TextureRequest texture,
            Dictionary<string, object> report)
        {
            // Revit repaints a material's shading colour to match a new appearance asset, and a
            // freshly created Generic one is black. Kept here to be put back: the caller asked for
            // a texture, not for its material to be repainted.
            Color colour = material.Color == null || !material.Color.IsValid
                ? null
                : new Color(material.Color.Red, material.Color.Green, material.Color.Blue);

            AppearanceAssetElement asset = TexturableAsset(app, document, material, report);

            Dictionary<string, object> set = new Dictionary<string, object>();
            List<string> missing = new List<string>();
            string diffuseName = null;
            bool applied = false;

            RevitWrite.InTransaction(document, "Connect texture", delegate
            {
                // An AppearanceAssetEditScope is the only way in to an asset's properties, and its
                // Commit needs a transaction already open around it: without one Revit throws
                // "EditScope cannot be closed, there is no opened transaction".
                using (AppearanceAssetEditScope scope = new AppearanceAssetEditScope(document))
                {
                    Asset editable = scope.Start(asset.Id);

                    AssetProperty diffuse = FindDiffuse(editable, out diffuseName);

                    if (diffuse == null)
                    {
                        // No property a bitmap can hang off - a Water or Metal asset on the
                        // fallback path. Reported, not thrown, and nothing is changed.
                        missing.AddRange(DiffuseProperties);
                        scope.Cancel();
                        return;
                    }

                    ConnectBitmap(diffuse, texture, set, missing);
                    ApplyTint(editable, texture, set, missing);

                    scope.Commit(true);
                    applied = true;
                }
            });

            if (applied && !material.UseRenderAppearanceForShading)
            {
                // A texture a shaded view ignores is not worth much; this is the switch that makes
                // shaded views use the appearance asset rather than Color and Transparency.
                RevitWrite.InTransaction(document, "Shade with the render appearance", delegate
                {
                    material.UseRenderAppearanceForShading = true;
                });
            }

            if (colour != null && !SameColor(colour, material.Color))
            {
                RevitWrite.InTransaction(document, "Keep the material's colour", delegate
                {
                    material.Color = colour;
                });
            }

            report["appearanceAssetId"] = asset.Id.Value;
            report["appearanceAssetName"] = RevitFacts.SafeName(asset);
            report["assetSchema"] = SchemaOf(asset);
            report["colorRgb"] = ReadColor(material.Color);
            report["texturePath"] = texture.Path;
            report["diffuseProperty"] = diffuseName;
            report["textureApplied"] = applied;
            report["set"] = set;
            report["missing"] = missing;
            report["verified"] = applied ? ReadBack(document, asset.Id, diffuseName) : null;
        }

        /// <summary>
        /// An appearance asset this material owns outright and whose schema carries
        /// "generic_diffuse" - authored here when the material has none, when the one it has is of
        /// another schema, or when it shares one with other materials that must not change with it.
        /// Records in the report whether an asset was authored and, when it was, why.
        /// </summary>
        private static AppearanceAssetElement TexturableAsset(
            UIApplication app,
            Document document,
            Material material,
            Dictionary<string, object> report)
        {
            AppearanceAssetElement current =
                document.GetElement(material.AppearanceAssetId) as AppearanceAssetElement;

            int shared = current == null ? 0 : MaterialsSharing(document, material, current.Id);
            string reason = null;

            if (current == null)
            {
                reason = "the material had no appearance asset";
            }
            else if (current.GetRenderingAsset().FindByName(Generic.GenericDiffuse) == null)
            {
                reason = "its appearance asset is a \"" + SchemaOf(current) + "\" asset, and this "
                    + "endpoint textures the Generic schema it controls rather than guessing at "
                    + "another one";
            }
            else if (shared > 0)
            {
                // The same rule materials/create follows for appearanceAssetId: a look is copied,
                // never shared, so one material's texture cannot appear on another's surfaces.
                reason = shared == 1
                    ? "its appearance asset is shared with 1 other material, which would have been "
                        + "textured too"
                    : "its appearance asset is shared with " + shared + " other materials, which "
                        + "would all have been textured too";
            }

            if (reason == null)
            {
                report["assetAuthored"] = false;
                report["assetReason"] = null;
                return current;
            }

            if (shared > 0)
            {
                // Duplicate rather than start over: the material keeps the look it had, minus the
                // sharing.
                AppearanceAssetElement copy = null;

                RevitWrite.InTransaction(document, "Copy the appearance asset", delegate
                {
                    copy = Duplicate(document, current, RevitFacts.SafeName(material));
                    material.AppearanceAssetId = copy.Id;
                });

                report["assetAuthored"] = true;
                report["assetReason"] = reason;

                return copy;
            }

            Asset generic = LibraryGenericAsset(app);

            if (generic == null)
            {
                if (current == null)
                {
                    throw BridgeException.NotFound(
                        "GENERIC_ASSET_UNAVAILABLE",
                        "This material has no appearance asset and Revit's asset library offered no "
                            + "\"Generic\" asset to build one from, so there is nothing to connect a "
                            + "bitmap to. The material libraries may not be installed. Pass "
                            + "\"appearanceAssetId\" to /revit-mcp/materials/create to copy the look "
                            + "of a material that does have one, then texture that.");
                }

                report["assetAuthored"] = false;
                report["assetReason"] = reason
                    + " - but Revit's asset library offered no \"Generic\" asset to replace it with, "
                    + "so it was textured in place";

                return current;
            }

            AppearanceAssetElement authored = null;

            RevitWrite.InTransaction(document, "Give the material a Generic appearance asset", delegate
            {
                authored = AppearanceAssetElement.Create(
                    document,
                    UnusedAssetName(document, RevitFacts.SafeName(material)),
                    generic);

                material.AppearanceAssetId = authored.Id;
            });

            report["assetAuthored"] = true;
            report["assetReason"] = reason;

            return authored;
        }

        /// <summary>
        /// Revit's own "Generic" appearance asset, or null when the material libraries are not
        /// installed. This is a library asset, not an element: AppearanceAssetElement.Create turns
        /// it into one that belongs to the document.
        /// </summary>
        private static Asset LibraryGenericAsset(UIApplication app)
        {
            foreach (Asset asset in app.Application.GetAssets(AssetType.Appearance))
            {
                if (string.Equals(asset.Name, GenericAssetName, StringComparison.Ordinal))
                {
                    return asset;
                }
            }

            return null;
        }

        /// <summary>How many OTHER materials point at this appearance asset.</summary>
        private static int MaterialsSharing(Document document, Material material, ElementId assetId)
        {
            int count = 0;

            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(Material)))
            {
                Material other = (Material)element;

                if (other.Id != material.Id && other.AppearanceAssetId == assetId)
                {
                    count++;
                }
            }

            return count;
        }

        private static AssetProperty FindDiffuse(Asset asset, out string name)
        {
            foreach (string candidate in DiffuseProperties)
            {
                AssetProperty property = asset.FindByName(candidate);

                if (property != null)
                {
                    name = candidate;
                    return property;
                }
            }

            name = null;
            return null;
        }

        /// <summary>
        /// Hangs a UnifiedBitmap off the diffuse property and writes the bitmap, its tile size and
        /// its rotation into it. Every property the schema turns out not to carry is added to
        /// <paramref name="missing"/> rather than thrown over.
        /// </summary>
        private static void ConnectBitmap(
            AssetProperty diffuse,
            TextureRequest texture,
            Dictionary<string, object> set,
            List<string> missing)
        {
            if (diffuse.NumberOfConnectedProperties > 0)
            {
                // Whatever was connected before has to go: AddConnectedAsset has nowhere to put a
                // second one.
                diffuse.RemoveConnectedAsset();
            }

            diffuse.AddConnectedAsset(BitmapSchema);
            Asset bitmap = diffuse.GetSingleConnectedAsset();

            AssetPropertyString file =
                bitmap.FindByName(UnifiedBitmap.UnifiedbitmapBitmap) as AssetPropertyString;

            if (file == null)
            {
                missing.Add(UnifiedBitmap.UnifiedbitmapBitmap);
            }
            else
            {
                file.Value = texture.Path;
                set[UnifiedBitmap.UnifiedbitmapBitmap] = texture.Path;
            }

            if (texture.ScaleX > 0)
            {
                if (texture.ScaleX != texture.ScaleY)
                {
                    // Revit ships the two axes locked together. Different sizes with the lock still
                    // on is a state the UI would not let anyone author.
                    SetBoolean(bitmap, UnifiedBitmap.TextureScaleLock, false, set, missing);
                }

                SetDistance(bitmap, UnifiedBitmap.TextureRealWorldScaleX, texture.ScaleX, set, missing);
                SetDistance(bitmap, UnifiedBitmap.TextureRealWorldScaleY, texture.ScaleY, set, missing);
            }

            if (!double.IsNaN(texture.Rotation))
            {
                SetDouble(bitmap, UnifiedBitmap.TextureWAngle, texture.Rotation, set, missing);
            }
        }

        /// <summary>
        /// A tint is a colour multiplied over the bitmap, and it does nothing until its toggle is
        /// on - so both are written, or both are reported missing.
        /// </summary>
        private static void ApplyTint(
            Asset asset,
            TextureRequest texture,
            Dictionary<string, object> set,
            List<string> missing)
        {
            if (texture.Tint == null)
            {
                return;
            }

            AssetPropertyDoubleArray4d tint =
                asset.FindByName(Generic.CommonTintColor) as AssetPropertyDoubleArray4d;

            if (tint == null)
            {
                missing.Add(Generic.CommonTintColor);
            }
            else
            {
                tint.SetValueAsColor(texture.Tint);
                set[Generic.CommonTintColor] = ReadColor(texture.Tint);
            }

            SetBoolean(asset, Generic.CommonTintToggle, true, set, missing);
        }

        /// <summary>
        /// Writes a length the caller gave in feet - Revit internal units, like every other length
        /// in this bridge - into a property that wants its own unit, which for a texture scale is
        /// inches. What goes into "set" is the converted value, because that is what the asset
        /// holds.
        /// </summary>
        private static void SetDistance(
            Asset asset,
            string name,
            double feet,
            Dictionary<string, object> set,
            List<string> missing)
        {
            AssetPropertyDistance property = asset.FindByName(name) as AssetPropertyDistance;

            if (property == null)
            {
                missing.Add(name);
                return;
            }

            double value = UnitUtils.ConvertFromInternalUnits(feet, property.GetUnitTypeId());

            property.Value = value;
            set[name] = value;
        }

        private static void SetDouble(
            Asset asset,
            string name,
            double value,
            Dictionary<string, object> set,
            List<string> missing)
        {
            AssetPropertyDouble property = asset.FindByName(name) as AssetPropertyDouble;

            if (property == null)
            {
                missing.Add(name);
                return;
            }

            property.Value = value;
            set[name] = value;
        }

        private static void SetBoolean(
            Asset asset,
            string name,
            bool value,
            Dictionary<string, object> set,
            List<string> missing)
        {
            AssetPropertyBoolean property = asset.FindByName(name) as AssetPropertyBoolean;

            if (property == null)
            {
                missing.Add(name);
                return;
            }

            property.Value = value;
            set[name] = value;
        }

        /// <summary>
        /// What the committed asset really holds, read back outside the edit scope. The caller
        /// asked for a texture; this is the evidence it is there, in the units it asked in.
        /// </summary>
        private static object ReadBack(Document document, ElementId assetId, string diffuseName)
        {
            AppearanceAssetElement element =
                (AppearanceAssetElement)document.GetElement(assetId);

            Asset committed = element.GetRenderingAsset();
            AssetProperty diffuse = committed.FindByName(diffuseName);

            if (diffuse == null || diffuse.NumberOfConnectedProperties == 0)
            {
                return null;
            }

            Asset bitmap = diffuse.GetSingleConnectedAsset();

            AssetPropertyString file =
                bitmap.FindByName(UnifiedBitmap.UnifiedbitmapBitmap) as AssetPropertyString;

            AssetPropertyDouble angle =
                bitmap.FindByName(UnifiedBitmap.TextureWAngle) as AssetPropertyDouble;

            return new Dictionary<string, object>
            {
                { "connectedAsset", bitmap.Name },
                { "bitmap", file == null ? null : file.Value },
                { "scaleFeet", new Dictionary<string, object>
                    {
                        { "x", ReadDistanceInFeet(bitmap, UnifiedBitmap.TextureRealWorldScaleX) },
                        { "y", ReadDistanceInFeet(bitmap, UnifiedBitmap.TextureRealWorldScaleY) },
                    }
                },
                { "rotation", angle == null ? (object)null : angle.Value },
            };
        }

        private static object ReadDistanceInFeet(Asset asset, string name)
        {
            AssetPropertyDistance property = asset.FindByName(name) as AssetPropertyDistance;

            if (property == null)
            {
                return null;
            }

            return UnitUtils.ConvertToInternalUnits(property.Value, property.GetUnitTypeId());
        }

        private static bool SameColor(Color left, Color right)
        {
            return right != null
                && right.IsValid
                && left.Red == right.Red
                && left.Green == right.Green
                && left.Blue == right.Blue;
        }

        /// <summary>The schema an asset was built from, which is what decides its properties.</summary>
        private static string SchemaOf(AppearanceAssetElement element)
        {
            Asset asset = element.GetRenderingAsset();

            return asset == null ? null : asset.Name;
        }
    }
}
