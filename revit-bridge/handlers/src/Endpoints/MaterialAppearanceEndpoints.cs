using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using Autodesk.Revit.UI;

namespace RevitMcpBridge.Endpoints
{
    /// <summary>
    /// The RENDERED look of a material - its AppearanceAssetElement - as opposed to the shading
    /// colour that Material.Color carries and MaterialEndpoints authors.
    ///
    /// Why this exists as its own pair of endpoints: an appearance asset has no fixed set of
    /// properties. What it carries depends on the SCHEMA it was built from - a Generic asset has
    /// "generic_diffuse", "generic_glossiness" and "generic_transparency"; a Ceramic one has
    /// "ceramic_color"; a Water one has neither. materials/set-texture answered that by only ever
    /// touching the Generic schema it authors itself, which is right for a bitmap and useless for
    /// "make this stucco less pale" or "take the orange out of these window frames".
    ///
    /// So: materials/appearance READS the asset a material actually has - its schema, every direct
    /// property with the runtime type and the value it really holds, what is connected to each, and
    /// which other materials point at the same asset. materials/set-appearance then writes named
    /// properties of that asset, and nothing it was not given.
    ///
    /// Two rules this file does not bend:
    ///
    ///   - A shared asset is never edited. Editing an asset two materials point at repaints both,
    ///     and nothing in the reply would say so. The asset is DUPLICATED before it is patched
    ///     (the default), and "duplicate": false is refused outright when anybody else shares it.
    ///     The reply carries the ids that shared it before and the ids that share the result after,
    ///     which is the evidence that nothing leaked.
    ///   - A patch either applies as asked or the whole request rolls back. Every patch names its
    ///     own type, the type is checked against the property's real runtime type, the value is
    ///     checked with Revit's own IsValidValue, and a property this asset's schema does not have
    ///     is a BAD_REQUEST - never a success that changed nothing. Everything runs inside one
    ///     TransactionGroup, so a patch refused half way through leaves the model as it was.
    ///
    /// A material with NO appearance asset is a normal state, not a broken one: it renders from
    /// Color and Transparency alone, and it does NOT need a bitmap to gain a look. "createGeneric"
    /// builds it a textureless Generic asset out of Revit's own asset library, which is the honest
    /// way to give it glossiness, transparency or a diffuse colour that a render can see.
    ///
    /// Colours cross the wire as {r, g, b}, 0-255, like everywhere else in this bridge; Revit
    /// stores them inside an asset as doubles 0-1, and the readback reports both.
    /// </summary>
    internal static class MaterialAppearanceEndpoints
    {
        /// <summary>
        /// The name of the library asset a material with no appearance gets when the caller asks
        /// for one. "Generic" is the plain shading schema every Revit install ships.
        /// </summary>
        private const string GenericAssetName = "Generic";

        /// <summary>The five patch types the wire contract allows.</summary>
        private const string PatchTypes = "\"color\", \"double\", \"integer\", \"boolean\" or \"string\"";

        /// <summary>
        /// Body: {materialId | materialName}. Read-only: it opens no transaction and changes
        /// nothing.
        ///
        /// Reports the material's shading side (colorRgb, transparency, shininess, smoothness,
        /// useRenderAppearanceForShading), the AppearanceAssetElement it really points at - id,
        /// name, schema, title, library - and every DIRECT property of that asset: its name, the
        /// AssetPropertyType Revit calls it, the runtime class that implements it, whether the API
        /// reports it read-only, the typed value it holds, and a summary of anything connected to
        /// it (a bitmap's file, tile size in feet and rotation).
        ///
        /// "patchType" on each row is the answer to the only question a caller really has: which of
        /// the five materials/set-appearance patch types can write this property, or null when none
        /// of them can.
        ///
        /// "readOnly" is what the API reports for an asset read OUTSIDE an edit scope. Revit hands
        /// the rendering asset out read-only, so it is usually true for every property and is not
        /// the test of whether a property can be written - materials/set-appearance is, because it
        /// validates inside an AppearanceAssetEditScope where IsValidValue is legal to call.
        ///
        /// "sharedWithMaterialIds" is every OTHER material pointing at the same asset. Non-empty
        /// means editing it in place would repaint those materials too, which is exactly what
        /// materials/set-appearance refuses to do.
        ///
        /// A material with no appearance asset answers with appearanceAssetId null, an empty
        /// property list and a "note" saying what to do about it - not an error.
        /// </summary>
        internal static object Appearance(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            ElementId materialId = RequireMaterialId(document, body);
            Material material = (Material)document.GetElement(materialId);

            AppearanceAssetElement asset =
                document.GetElement(material.AppearanceAssetId) as AppearanceAssetElement;

            Dictionary<string, object> report = new Dictionary<string, object>
            {
                { "materialId", material.Id.Value },
                { "materialName", RevitFacts.SafeName(material) },
                { "colorRgb", ReadColor(material.Color) },
                { "transparency", material.Transparency },
                { "shininess", material.Shininess },
                { "smoothness", material.Smoothness },
                { "useRenderAppearanceForShading", material.UseRenderAppearanceForShading },
                { "appearanceAssetId", asset == null ? (object)null : asset.Id.Value },
                { "appearanceAssetName", asset == null ? null : RevitFacts.SafeName(asset) },

                // Said here so a caller can tell "createGeneric will work" from "the material
                // libraries are not installed on this machine" before it asks for one.
                { "genericAssetAvailable", LibraryGenericAsset(app) != null },
            };

            if (asset == null)
            {
                report["assetSchema"] = null;
                report["assetTitle"] = null;
                report["assetLibrary"] = null;
                report["sharedWithMaterialIds"] = new List<object>();
                report["propertyCount"] = 0;
                report["properties"] = new List<object>();
                report["note"] = "This material has no AppearanceAssetElement, which is the normal "
                    + "state of a material created through the API: Color and Transparency alone "
                    + "decide how it renders. It does not need a bitmap to gain a look - call "
                    + "/revit-mcp/materials/set-appearance with \"createGeneric\": true to give it "
                    + "a textureless Generic asset and patch that, or \"sourceAppearanceAssetId\" "
                    + "to start from a copy of a material that does have one.";

                return report;
            }

            Asset rendering = asset.GetRenderingAsset();

            report["assetSchema"] = rendering == null ? null : rendering.Name;
            report["assetTitle"] = rendering == null ? null : rendering.Title;
            report["assetLibrary"] = rendering == null ? null : rendering.LibraryName;
            report["sharedWithMaterialIds"] = MaterialsSharing(document, material, asset.Id);

            List<object> properties = new List<object>();

            if (rendering != null)
            {
                for (int index = 0; index < rendering.Size; index++)
                {
                    properties.Add(DescribeProperty(rendering.Get(index)));
                }
            }

            string source = "material";
            object preset = null;

            if (properties.Count == 0 && rendering != null)
            {
                // Size 0 is the normal state of a library preset the model has never edited: the
                // schema lives in the shipped asset and the material only points at it. Reporting
                // that as "no properties" reads as "there is nothing to patch", which is the
                // opposite of true - so read the library asset of the same schema instead. It is
                // read-only, and set-appearance still patches a copy of the material's own asset;
                // this is only where the property names and types come from.
                Asset library = LibraryAsset(app, rendering.Name);

                if (library != null)
                {
                    for (int index = 0; index < library.Size; index++)
                    {
                        properties.Add(DescribeProperty(library.Get(index)));
                    }

                    source = "libraryPreset";
                    preset = library.Name;
                }
            }

            report["propertySource"] = source;
            report["presetName"] = preset;
            report["propertyCount"] = properties.Count;
            report["properties"] = properties;

            if (source == "libraryPreset")
            {
                report["note"] = "This material uses Revit's \"" + preset + "\" library preset "
                    + "unedited, so its own asset carries no properties and these were read off the "
                    + "shipped asset of that schema - they are the names, types and values it "
                    + "renders with. Patch them the same way: set-appearance copies the material's "
                    + "asset first, so the library preset is never touched.";
            }

            return report;
        }

        /// <summary>
        /// Body: {materialId | materialName, patches: [{name, type, value}], duplicate?,
        /// sourceAppearanceAssetId?, createGeneric?, disconnectTexture?, syncShadingColor?}.
        ///
        /// Writes named properties of a material's appearance asset. One request is one undo step,
        /// and it is all-or-nothing: a patch naming a property the schema does not have, a patch
        /// whose type does not match the property's runtime type, or a value Revit's own
        /// IsValidValue refuses, rolls the whole request back rather than reporting a success that
        /// changed nothing.
        ///
        /// Which asset gets patched, in order of precedence:
        ///
        ///   - "createGeneric": true - a new AppearanceAssetElement built from Revit's library
        ///     "Generic" asset is assigned to the material and patched. This is the answer for a
        ///     material that has no appearance asset at all; no bitmap is involved. Refused with
        ///     GENERIC_ASSET_UNAVAILABLE when the material libraries are not installed.
        ///   - "sourceAppearanceAssetId" - that asset is DUPLICATED and the copy is assigned to
        ///     this material and patched. The source is never touched, so the material it belongs
        ///     to keeps its look exactly.
        ///   - otherwise the material's own asset: duplicated first by default ("duplicate": true),
        ///     because an asset shared with another material would otherwise be repainted on its
        ///     surfaces too. "duplicate": false edits in place and is refused with
        ///     SHARED_APPEARANCE_ASSET when anybody else points at it. There is deliberately no way
        ///     to force a shared edit.
        ///   - a material with no asset and neither "createGeneric" nor "sourceAppearanceAssetId"
        ///     is NO_APPEARANCE_ASSET, with both routes named in the message.
        ///
        /// "disconnectTexture" only does anything for a "color" patch, and only when it is
        /// explicitly true: a colour written under a connected bitmap is a colour nothing renders,
        /// so a colour patch on a property that carries a texture is refused unless the caller says
        /// to remove that texture.
        ///
        /// "syncShadingColor" copies the first "color" patch onto Material.Color as well, which is
        /// what shaded views draw when UseRenderAppearanceForShading is false. Nothing else about
        /// the material is touched. When the asset is swapped (created, duplicated from a source)
        /// Revit repaints the shading colour to match it - to black for a fresh Generic asset - so
        /// the colour the material had is put back, unless syncShadingColor asked for a new one.
        ///
        /// The reply carries what was applied, what the committed asset reads back as, the
        /// resulting appearance asset id, and the materials sharing it before and after.
        /// </summary>
        internal static object SetAppearance(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            ElementId materialId = RequireMaterialId(document, body);
            Material material = (Material)document.GetElement(materialId);

            List<Patch> patches = ReadPatches(body);

            bool duplicate = JsonBody.OptionalBool(body, "duplicate", true);
            bool createGeneric = JsonBody.OptionalBool(body, "createGeneric", false);
            bool disconnectTexture = JsonBody.OptionalBool(body, "disconnectTexture", false);
            bool syncShadingColor = JsonBody.OptionalBool(body, "syncShadingColor", false);

            AppearanceAssetElement source = OptionalAppearanceAsset(document, body);

            if (createGeneric && source != null)
            {
                throw BridgeException.BadRequest(
                    "\"createGeneric\" builds a new Generic asset and \"sourceAppearanceAssetId\" "
                        + "copies an existing one, so they cannot both be asked for in one call.");
            }

            if (patches.Count == 0 && !createGeneric && source == null)
            {
                throw BridgeException.BadRequest(
                    "\"patches\" must hold at least one {name, type, value} - with no patches and "
                        + "no \"createGeneric\" or \"sourceAppearanceAssetId\" there is nothing for "
                        + "this call to do. Call /revit-mcp/materials/appearance for the property "
                        + "names and types this material's asset really has.");
            }

            Color first = FirstPatchedColor(patches);

            if (syncShadingColor && first == null)
            {
                throw BridgeException.BadRequest(
                    "\"syncShadingColor\" copies a patched colour onto Material.Color, so it needs "
                        + "at least one patch of type \"color\" to take that colour from.");
            }

            return RevitWrite.InGroup(document, "MCP: set material appearance", delegate
            {
                Dictionary<string, object> report = new Dictionary<string, object>
                {
                    { "materialId", material.Id.Value },
                    { "materialName", RevitFacts.SafeName(material) },
                };

                // Revit repaints a material's shading colour to match an appearance asset it has
                // just been given, and a fresh Generic one is black. Kept to be put back: this
                // endpoint patches a look, it does not repaint a material behind the caller's back.
                Color shading = material.Color == null || !material.Color.IsValid
                    ? null
                    : new Color(material.Color.Red, material.Color.Green, material.Color.Blue);

                AppearanceAssetElement asset = ResolveAsset(
                    app,
                    document,
                    material,
                    source,
                    createGeneric,
                    duplicate,
                    report);

                List<object> applied = new List<object>();

                if (patches.Count > 0)
                {
                    RevitWrite.InTransaction(document, "Patch appearance asset", delegate
                    {
                        // An AppearanceAssetEditScope is the only way in to an asset's properties,
                        // and IsValidValue is only legal inside one. Its Commit needs a transaction
                        // already open around it, or Revit throws "EditScope cannot be closed,
                        // there is no opened transaction".
                        using (AppearanceAssetEditScope scope = new AppearanceAssetEditScope(document))
                        {
                            Asset editable = scope.Start(asset.Id);

                            try
                            {
                                foreach (Patch patch in patches)
                                {
                                    applied.Add(Apply(editable, patch, disconnectTexture));
                                }
                            }
                            catch (Exception)
                            {
                                // Nothing half-written survives: the scope is abandoned, the
                                // transaction never commits, and InGroup rolls the asset swap back
                                // on the way out.
                                scope.Cancel();
                                throw;
                            }

                            scope.Commit(true);
                        }
                    });
                }

                if (syncShadingColor)
                {
                    RevitWrite.InTransaction(document, "Sync the shading colour", delegate
                    {
                        material.Color = first;
                    });
                }
                else if (shading != null && !SameColor(shading, material.Color))
                {
                    RevitWrite.InTransaction(document, "Keep the material's shading colour", delegate
                    {
                        material.Color = shading;
                    });
                }

                report["appearanceAssetId"] = asset.Id.Value;
                report["appearanceAssetName"] = RevitFacts.SafeName(asset);
                report["assetSchema"] = SchemaOf(asset);
                report["applied"] = applied;
                report["verified"] = ReadBack(document, asset.Id, patches);

                // Computed against the RESULTING asset: empty is the evidence that this edit
                // cannot have leaked onto another material's surfaces.
                report["sharedWithMaterialIds"] = MaterialsSharing(document, material, asset.Id);
                report["shadingColorRgb"] = ReadColor(material.Color);
                report["shadingColorSynced"] = syncShadingColor;
                report["useRenderAppearanceForShading"] = material.UseRenderAppearanceForShading;

                return report;
            });
        }

        // --- the asset a patch lands on ------------------------------------------------------

        /// <summary>
        /// The asset this request may write to, assigned to the material when it had to be created
        /// or copied. Records in <paramref name="report"/> which route was taken, what the material
        /// pointed at before, and who was sharing that.
        /// </summary>
        private static AppearanceAssetElement ResolveAsset(
            UIApplication app,
            Document document,
            Material material,
            AppearanceAssetElement source,
            bool createGeneric,
            bool duplicate,
            Dictionary<string, object> report)
        {
            AppearanceAssetElement current =
                document.GetElement(material.AppearanceAssetId) as AppearanceAssetElement;

            List<object> shared = current == null
                ? new List<object>()
                : MaterialsSharing(document, material, current.Id);

            report["previousAppearanceAssetId"] = current == null ? (object)null : current.Id.Value;
            report["previousSharedWithMaterialIds"] = shared;

            string name = RevitFacts.SafeName(material);

            if (createGeneric)
            {
                Asset generic = LibraryGenericAsset(app);

                if (generic == null)
                {
                    throw BridgeException.NotFound(
                        "GENERIC_ASSET_UNAVAILABLE",
                        "Revit's asset library offered no \"Generic\" appearance asset to build one "
                            + "from, so \"createGeneric\" has nothing to create. The material "
                            + "libraries are probably not installed on this machine. Pass "
                            + "\"sourceAppearanceAssetId\" instead, naming the appearance asset of "
                            + "a material that does have one (see /revit-mcp/materials) - it is "
                            + "duplicated, so that material keeps its own look.");
                }

                AppearanceAssetElement created = null;

                RevitWrite.InTransaction(document, "Create a Generic appearance asset", delegate
                {
                    created = AppearanceAssetElement.Create(
                        document,
                        UnusedAssetName(document, name),
                        generic);

                    material.AppearanceAssetId = created.Id;
                });

                report["assetAction"] = "created-generic";
                report["assetReason"] = "\"createGeneric\" was asked for, so the material was given "
                    + "a new asset built from Revit's library \"Generic\" asset, with no bitmap "
                    + "connected to anything.";

                return created;
            }

            if (source != null)
            {
                AppearanceAssetElement copy = null;

                RevitWrite.InTransaction(document, "Copy an appearance asset", delegate
                {
                    copy = source.Duplicate(UnusedAssetName(document, name));
                    material.AppearanceAssetId = copy.Id;
                });

                report["assetAction"] = "duplicated-source";
                report["sourceAppearanceAssetId"] = source.Id.Value;
                report["assetReason"] = "\"sourceAppearanceAssetId\" " + source.Id.Value + " was "
                    + "duplicated and the copy assigned to this material, so only this material "
                    + "changed.";

                return copy;
            }

            if (current == null)
            {
                throw BridgeException.NotFound(
                    "NO_APPEARANCE_ASSET",
                    "Material \"" + name + "\" (" + material.Id.Value + ") has no "
                        + "AppearanceAssetElement, so there is no asset to patch. That is a normal "
                        + "state, not a broken one - it renders from Color and Transparency alone - "
                        + "and it does NOT mean the material needs a bitmap. Pass "
                        + "\"createGeneric\": true to give it a textureless Generic asset and patch "
                        + "that, or \"sourceAppearanceAssetId\" to start from a copy of a material "
                        + "that already has the look you want.");
            }

            if (duplicate)
            {
                AppearanceAssetElement copy = null;

                RevitWrite.InTransaction(document, "Copy the appearance asset", delegate
                {
                    copy = current.Duplicate(UnusedAssetName(document, name));
                    material.AppearanceAssetId = copy.Id;
                });

                report["assetAction"] = "duplicated";
                report["assetReason"] = "the asset was duplicated before it was patched, which is "
                    + "the default, so this edit cannot appear on any other material's surfaces.";

                return copy;
            }

            if (shared.Count > 0)
            {
                throw new BridgeException(
                    409,
                    "SHARED_APPEARANCE_ASSET",
                    "\"duplicate\": false would patch appearance asset " + current.Id.Value + " \""
                        + RevitFacts.SafeName(current) + "\" in place, and " + shared.Count + " other "
                        + "material" + (shared.Count == 1 ? "" : "s") + " point at it ("
                        + string.Join(", ", shared) + "), so every one of them would be repainted "
                        + "too. Leave \"duplicate\" at its default true to patch a private copy. "
                        + "Nothing was changed.");
            }

            report["assetAction"] = "in-place";
            report["assetReason"] = "\"duplicate\": false, and no other material points at this "
                + "asset, so it was patched where it stands.";

            return current;
        }

        /// <summary>
        /// Revit's own "Generic" appearance asset, or null when the material libraries are not
        /// installed. It is a library asset, not an element: AppearanceAssetElement.Create is what
        /// turns it into one the document owns.
        /// </summary>
        private static Asset LibraryGenericAsset(UIApplication app)
        {
            return LibraryAsset(app, GenericAssetName);
        }

        /// <summary>
        /// The shipped library asset of that schema name, or null when the libraries are not
        /// installed. Read-only: Revit hands these out to be read or copied, never patched.
        /// </summary>
        private static Asset LibraryAsset(UIApplication app, string name)
        {
            if (name == null)
            {
                return null;
            }

            foreach (Asset asset in app.Application.GetAssets(AssetType.Appearance))
            {
                if (string.Equals(asset.Name, name, StringComparison.Ordinal))
                {
                    return asset;
                }
            }

            return null;
        }

        // --- patches --------------------------------------------------------------------------

        /// <summary>One {name, type, value} off the request, already read and range-checked.</summary>
        private sealed class Patch
        {
            internal string Name;

            /// <summary>One of color, double, integer, boolean, string.</summary>
            internal string Type;

            internal Color Color;

            internal double Number;

            internal int Integer;

            internal bool Flag;

            internal string Text;
        }

        /// <summary>
        /// The "patches" array, or an empty list when the body carries none. Every entry names its
        /// own type: the JSON shape of "value" is read against that type here, before anything is
        /// opened, so a malformed patch never reaches a transaction.
        /// </summary>
        private static List<Patch> ReadPatches(JsonElement body)
        {
            List<Patch> patches = new List<Patch>();

            JsonElement array;
            if (!JsonBody.TryGet(body, "patches", out array))
            {
                return patches;
            }

            if (array.ValueKind != JsonValueKind.Array)
            {
                throw BridgeException.BadRequest(
                    "\"patches\" must be an array of {name, type, value}, but was "
                        + array.ValueKind + ".");
            }

            int index = 0;

            foreach (JsonElement item in array.EnumerateArray())
            {
                string label = "patches[" + index + "]";

                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw BridgeException.BadRequest(
                        "\"" + label + "\" must be an object {name, type, value}, but was "
                            + item.ValueKind + ".");
                }

                Patch patch = new Patch();
                patch.Name = JsonBody.RequireString(item, "name");
                patch.Type = JsonBody.RequireString(item, "type").ToLowerInvariant();

                JsonElement value;
                if (!JsonBody.TryGet(item, "value", out value))
                {
                    throw BridgeException.BadRequest(
                        "\"" + label + ".value\" is required: a patch says what to write, and there "
                            + "is no way to write nothing.");
                }

                switch (patch.Type)
                {
                    case "color":
                        patch.Color = ReadRgb(value, label + ".value");
                        break;

                    case "double":
                        patch.Number = JsonBody.AsDouble(value, label + ".value");
                        break;

                    case "integer":
                        double raw = JsonBody.AsDouble(value, label + ".value");

                        if (raw != Math.Floor(raw))
                        {
                            throw BridgeException.BadRequest(
                                "\"" + label + ".value\" is type \"integer\", so it must be a whole "
                                    + "number, but was " + raw.ToString(CultureInfo.InvariantCulture)
                                    + ". Use type \"double\" for a fractional value.");
                        }

                        patch.Integer = (int)raw;
                        break;

                    case "boolean":
                        patch.Flag = JsonBody.AsBool(value, label + ".value");
                        break;

                    case "string":
                        patch.Text = JsonBody.AsString(value, label + ".value");
                        break;

                    default:
                        throw BridgeException.BadRequest(
                            "\"" + label + ".type\" is \"" + patch.Type + "\", which is not a patch "
                                + "type. It must be one of " + PatchTypes + ". Call "
                                + "/revit-mcp/materials/appearance - every property it lists carries "
                                + "the \"patchType\" that can write it.");
                }

                patches.Add(patch);
                index++;
            }

            return patches;
        }

        /// <summary>
        /// Writes one patch into the asset being edited. Every failure here is a BAD_REQUEST that
        /// names the property and says what it really is, and every one of them takes the whole
        /// request down with it rather than leaving a half-applied look behind.
        /// </summary>
        private static Dictionary<string, object> Apply(
            Asset editable,
            Patch patch,
            bool disconnectTexture)
        {
            AssetProperty property = editable.FindByName(patch.Name);

            if (property == null)
            {
                throw BridgeException.BadRequest(
                    "This material's appearance asset was built from the \"" + editable.Name
                        + "\" schema, and that schema has no property called \"" + patch.Name
                        + "\". Call /revit-mcp/materials/appearance for the " + editable.Size
                        + " property names it really has. Nothing was changed.");
            }

            Dictionary<string, object> row = new Dictionary<string, object>
            {
                { "name", patch.Name },
                { "type", patch.Type },
                { "assetPropertyType", property.Type.ToString() },
                { "runtimeType", property.GetType().Name },
                { "disconnectedTexture", false },

                // Revit offers no IsValidValue for a boolean property; every other type is checked
                // with the API's own validator before it is written.
                { "validated", patch.Type != "boolean" },
            };

            try
            {
                switch (patch.Type)
                {
                    case "color":
                        ApplyColor(property, patch, disconnectTexture, row);
                        break;

                    case "double":
                        ApplyDouble(property, patch, row);
                        break;

                    case "integer":
                        ApplyInteger(property, patch, row);
                        break;

                    case "boolean":
                        AssetPropertyBoolean flag = property as AssetPropertyBoolean;

                        if (flag == null)
                        {
                            throw TypeMismatch(patch, property, "an AssetPropertyBoolean");
                        }

                        flag.Value = patch.Flag;
                        row["value"] = patch.Flag;
                        break;

                    case "string":
                        AssetPropertyString text = property as AssetPropertyString;

                        if (text == null)
                        {
                            throw TypeMismatch(patch, property, "an AssetPropertyString");
                        }

                        if (!text.IsValidValue(patch.Text))
                        {
                            throw InvalidValue(patch, property, "\"" + patch.Text + "\"");
                        }

                        text.Value = patch.Text;
                        row["value"] = patch.Text;
                        break;
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                // Read-only in the edit scope, or a value Revit refused past IsValidValue. Either
                // way it is the caller's request that is wrong, not the bridge.
                throw BridgeException.BadRequest(
                    "Revit refused to write \"" + patch.Name + "\" on this "
                        + property.GetType().Name + ": " + ex.Message + " Nothing was changed.");
            }

            return row;
        }

        private static void ApplyColor(
            AssetProperty property,
            Patch patch,
            bool disconnectTexture,
            Dictionary<string, object> row)
        {
            AssetPropertyDoubleArray4d colour = property as AssetPropertyDoubleArray4d;

            if (colour == null)
            {
                throw TypeMismatch(patch, property, "an AssetPropertyDoubleArray4d");
            }

            if (!colour.IsValidValue(patch.Color))
            {
                throw InvalidValue(patch, property, DescribeColor(patch.Color));
            }

            if (property.NumberOfConnectedProperties > 0)
            {
                if (!disconnectTexture)
                {
                    throw BridgeException.BadRequest(
                        "\"" + patch.Name + "\" has a texture connected to it, and a connected "
                            + "texture is what renders - a colour written underneath it would "
                            + "change nothing visible. Pass \"disconnectTexture\": true to remove "
                            + "that texture and let the colour show, or patch a property that "
                            + "carries no texture. Nothing was changed.");
                }

                property.RemoveConnectedAsset();
                row["disconnectedTexture"] = true;
            }

            // The wire carries 0-255 per channel like every other colour in this bridge; the asset
            // holds doubles 0-1, and SetValueAsColor is the conversion.
            colour.SetValueAsColor(patch.Color);

            row["value"] = ReadColor(patch.Color);
        }

        private static void ApplyDouble(AssetProperty property, Patch patch, Dictionary<string, object> row)
        {
            AssetPropertyDouble number = property as AssetPropertyDouble;

            if (number != null)
            {
                if (!number.IsValidValue(patch.Number))
                {
                    throw InvalidValue(patch, property, patch.Number.ToString(CultureInfo.InvariantCulture));
                }

                number.Value = patch.Number;
                row["value"] = patch.Number;
                return;
            }

            AssetPropertyFloat single = property as AssetPropertyFloat;

            if (single != null)
            {
                if (!single.IsValidValue((float)patch.Number))
                {
                    throw InvalidValue(patch, property, patch.Number.ToString(CultureInfo.InvariantCulture));
                }

                single.Value = (float)patch.Number;
                row["value"] = (double)single.Value;
                return;
            }

            if (property is AssetPropertyDistance)
            {
                // A distance carries its own unit, so a bare number is ambiguous. materials/
                // set-texture is the endpoint that converts feet into whatever unit the property
                // wants; this one refuses to guess.
                throw BridgeException.BadRequest(
                    "\"" + patch.Name + "\" is an AssetPropertyDistance, which holds a length in its "
                        + "own unit rather than a plain number, so a \"double\" patch cannot write "
                        + "it without guessing at units. Texture tile sizes are what these usually "
                        + "are: /revit-mcp/materials/set-texture writes them, in feet. Nothing was "
                        + "changed.");
            }

            throw TypeMismatch(patch, property, "an AssetPropertyDouble or AssetPropertyFloat");
        }

        private static void ApplyInteger(AssetProperty property, Patch patch, Dictionary<string, object> row)
        {
            AssetPropertyInteger integer = property as AssetPropertyInteger;

            if (integer != null)
            {
                if (!integer.IsValidValue(patch.Integer))
                {
                    throw InvalidValue(patch, property, patch.Integer.ToString(CultureInfo.InvariantCulture));
                }

                integer.Value = patch.Integer;
                row["value"] = patch.Integer;
                return;
            }

            AssetPropertyEnum choice = property as AssetPropertyEnum;

            if (choice != null)
            {
                if (!choice.IsValidValue(patch.Integer))
                {
                    throw InvalidValue(patch, property, patch.Integer.ToString(CultureInfo.InvariantCulture));
                }

                choice.Value = patch.Integer;
                row["value"] = patch.Integer;
                return;
            }

            throw TypeMismatch(patch, property, "an AssetPropertyInteger or AssetPropertyEnum");
        }

        private static BridgeException TypeMismatch(Patch patch, AssetProperty property, string expected)
        {
            return BridgeException.BadRequest(
                "Patch \"" + patch.Name + "\" says type \"" + patch.Type + "\", which writes "
                    + expected + ", but that property is a " + property.GetType().Name + " ("
                    + property.Type + "). Call /revit-mcp/materials/appearance: every property it "
                    + "lists carries the \"patchType\" that can write it. Nothing was changed.");
        }

        private static BridgeException InvalidValue(Patch patch, AssetProperty property, string value)
        {
            return BridgeException.BadRequest(
                "Revit's own IsValidValue refused " + value + " for \"" + patch.Name + "\" on this "
                    + property.GetType().Name + ", so it is out of range for that property rather "
                    + "than merely unusual. Nothing was changed.");
        }

        /// <summary>The colour of the first "color" patch, or null when there is none.</summary>
        private static Color FirstPatchedColor(List<Patch> patches)
        {
            foreach (Patch patch in patches)
            {
                if (patch.Type == "color")
                {
                    return patch.Color;
                }
            }

            return null;
        }

        // --- reading properties back ------------------------------------------------------------

        /// <summary>
        /// What the committed asset really holds for every property this request patched, read back
        /// outside the edit scope. The caller asked for a look; this is the evidence it is there.
        /// </summary>
        private static object ReadBack(Document document, ElementId assetId, List<Patch> patches)
        {
            AppearanceAssetElement element =
                document.GetElement(assetId) as AppearanceAssetElement;

            if (element == null)
            {
                return null;
            }

            Asset committed = element.GetRenderingAsset();
            List<object> rows = new List<object>();

            foreach (Patch patch in patches)
            {
                AssetProperty property = committed == null ? null : committed.FindByName(patch.Name);

                if (property == null)
                {
                    rows.Add(new Dictionary<string, object>
                    {
                        { "name", patch.Name },
                        { "runtimeType", null },
                        { "value", null },
                        { "valueDoubles", null },
                        { "connectedCount", 0 },
                    });

                    continue;
                }

                rows.Add(new Dictionary<string, object>
                {
                    { "name", property.Name },
                    { "runtimeType", property.GetType().Name },
                    { "value", TypedValue(property) },
                    { "valueDoubles", ValueDoubles(property) },
                    { "connectedCount", property.NumberOfConnectedProperties },
                });
            }

            return rows;
        }

        /// <summary>
        /// One row of materials/appearance: what the property is called, what it is, what it holds,
        /// and what is hanging off it.
        /// </summary>
        private static Dictionary<string, object> DescribeProperty(AssetProperty property)
        {
            return new Dictionary<string, object>
            {
                { "name", property.Name },
                { "type", property.Type.ToString() },
                { "runtimeType", property.GetType().Name },
                { "patchType", PatchTypeOf(property) },
                { "readOnly", property.IsReadOnly },
                { "value", TypedValue(property) },
                { "valueDoubles", ValueDoubles(property) },
                { "valueFeet", ValueInFeet(property) },
                { "unit", UnitOf(property) },
                { "connectedCount", property.NumberOfConnectedProperties },
                { "connected", ConnectedSummary(property) },
            };
        }

        /// <summary>
        /// Which materials/set-appearance patch type can write this property, or null when none of
        /// them can - a list, a nested property set or a distance, all of which this endpoint is
        /// honest about rather than pretending at.
        /// </summary>
        private static string PatchTypeOf(AssetProperty property)
        {
            if (property is AssetPropertyDoubleArray4d)
            {
                return "color";
            }

            if (property is AssetPropertyDouble || property is AssetPropertyFloat)
            {
                return "double";
            }

            if (property is AssetPropertyInteger || property is AssetPropertyEnum)
            {
                return "integer";
            }

            if (property is AssetPropertyBoolean)
            {
                return "boolean";
            }

            if (property is AssetPropertyString)
            {
                return "string";
            }

            return null;
        }

        /// <summary>The value in its own type, or null for the property kinds that have no scalar.</summary>
        private static object TypedValue(AssetProperty property)
        {
            AssetPropertyBoolean flag = property as AssetPropertyBoolean;
            if (flag != null)
            {
                return flag.Value;
            }

            AssetPropertyDistance distance = property as AssetPropertyDistance;
            if (distance != null)
            {
                return distance.Value;
            }

            AssetPropertyDouble number = property as AssetPropertyDouble;
            if (number != null)
            {
                return number.Value;
            }

            AssetPropertyFloat single = property as AssetPropertyFloat;
            if (single != null)
            {
                return (double)single.Value;
            }

            AssetPropertyInteger integer = property as AssetPropertyInteger;
            if (integer != null)
            {
                return integer.Value;
            }

            AssetPropertyEnum choice = property as AssetPropertyEnum;
            if (choice != null)
            {
                return choice.Value;
            }

            AssetPropertyString text = property as AssetPropertyString;
            if (text != null)
            {
                return text.Value;
            }

            AssetPropertyDoubleArray4d colour = property as AssetPropertyDoubleArray4d;
            if (colour != null)
            {
                return ReadColor(SafeColor(colour));
            }

            return null;
        }

        /// <summary>
        /// The raw doubles behind a colour or a 3d array - 0-1 per channel for a colour, which is
        /// what the asset really stores and what the {r, g, b} above is translated from.
        /// </summary>
        private static object ValueDoubles(AssetProperty property)
        {
            AssetPropertyDoubleArray4d colour = property as AssetPropertyDoubleArray4d;
            if (colour != null)
            {
                return Doubles(colour.GetValueAsDoubles());
            }

            AssetPropertyDoubleArray3d triple = property as AssetPropertyDoubleArray3d;
            if (triple != null)
            {
                return Doubles(triple.GetValueAsDoubles());
            }

            return null;
        }

        private static object Doubles(IList<double> values)
        {
            if (values == null)
            {
                return null;
            }

            List<object> copy = new List<object>();

            foreach (double value in values)
            {
                copy.Add(value);
            }

            return copy;
        }

        /// <summary>
        /// A distance in feet - Revit internal units, like every other length in this bridge -
        /// beside the raw value in the property's own unit. Null for everything else.
        /// </summary>
        private static object ValueInFeet(AssetProperty property)
        {
            AssetPropertyDistance distance = property as AssetPropertyDistance;

            if (distance == null)
            {
                return null;
            }

            return UnitUtils.ConvertToInternalUnits(distance.Value, distance.GetUnitTypeId());
        }

        private static object UnitOf(AssetProperty property)
        {
            AssetPropertyDistance distance = property as AssetPropertyDistance;

            if (distance == null)
            {
                return null;
            }

            ForgeTypeId unit = distance.GetUnitTypeId();

            return unit == null ? null : unit.TypeId;
        }

        /// <summary>
        /// What is connected to this property, which for an appearance asset is almost always a
        /// UnifiedBitmap: its schema, the file it points at, the tile size in feet and its
        /// rotation. Null when nothing is connected.
        /// </summary>
        private static object ConnectedSummary(AssetProperty property)
        {
            if (property.NumberOfConnectedProperties == 0)
            {
                return null;
            }

            List<object> rows = new List<object>();

            foreach (AssetProperty connected in property.GetAllConnectedProperties())
            {
                Asset asset = connected as Asset;

                if (asset == null)
                {
                    rows.Add(new Dictionary<string, object>
                    {
                        { "schema", null },
                        { "runtimeType", connected.GetType().Name },
                    });

                    continue;
                }

                AssetPropertyString file =
                    asset.FindByName(UnifiedBitmap.UnifiedbitmapBitmap) as AssetPropertyString;

                AssetPropertyDouble angle =
                    asset.FindByName(UnifiedBitmap.TextureWAngle) as AssetPropertyDouble;

                rows.Add(new Dictionary<string, object>
                {
                    { "schema", asset.Name },
                    { "title", asset.Title },
                    { "bitmap", file == null ? null : file.Value },
                    { "scaleFeet", new Dictionary<string, object>
                        {
                            { "x", ReadDistanceInFeet(asset, UnifiedBitmap.TextureRealWorldScaleX) },
                            { "y", ReadDistanceInFeet(asset, UnifiedBitmap.TextureRealWorldScaleY) },
                        }
                    },
                    { "rotation", angle == null ? (object)null : angle.Value },
                });
            }

            return rows;
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

        // --- small shared readers ---------------------------------------------------------------

        /// <summary>
        /// The material this request is about. Named the same way as everywhere else in the bridge
        /// - "materialId" wins, "materialName" is the alternative - and a body carrying neither is
        /// a BAD_REQUEST rather than a silent nothing.
        /// </summary>
        private static ElementId RequireMaterialId(Document document, JsonElement body)
        {
            ElementId materialId = MaterialEndpoints.OptionalMaterialId(document, body);

            if (materialId == null)
            {
                throw BridgeException.BadRequest(
                    "\"materialId\" is required (or \"materialName\"). Call /revit-mcp/materials for "
                        + "the materials in this document.");
            }

            return materialId;
        }

        private static AppearanceAssetElement OptionalAppearanceAsset(Document document, JsonElement body)
        {
            JsonElement value;
            if (!JsonBody.TryGet(body, "sourceAppearanceAssetId", out value))
            {
                return null;
            }

            long id = JsonBody.AsLong(value, "sourceAppearanceAssetId");

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

        /// <summary>Reads a {r, g, b} value, each channel 0-255.</summary>
        private static Color ReadRgb(JsonElement value, string label)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw BridgeException.BadRequest(
                    "\"" + label + "\" is a colour, so it must be an object {r, g, b} with each "
                        + "channel 0-255, but was " + value.ValueKind + ".");
            }

            return new Color(
                Channel(value, "r", label),
                Channel(value, "g", label),
                Channel(value, "b", label));
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

        /// <summary>The ids of every OTHER material pointing at this appearance asset.</summary>
        private static List<object> MaterialsSharing(Document document, Material material, ElementId assetId)
        {
            List<object> ids = new List<object>();

            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(Material)))
            {
                Material other = (Material)element;

                if (other.Id != material.Id && other.AppearanceAssetId == assetId)
                {
                    ids.Add(other.Id.Value);
                }
            }

            return ids;
        }

        /// <summary>
        /// A name no appearance asset in the document has - Create and Duplicate both refuse one
        /// already in use, and the material's own name is the obvious first try. MaterialEndpoints
        /// keeps its own copy of this for the same reason; neither endpoint may reach into the
        /// other's privates.
        /// </summary>
        private static string UnusedAssetName(Document document, string name)
        {
            string candidate = string.IsNullOrWhiteSpace(name) ? "Appearance" : name;
            string stem = candidate;
            int suffix = 2;

            while (AppearanceAssetElement.GetAppearanceAssetElementByName(document, candidate) != null)
            {
                candidate = stem + " " + suffix;
                suffix++;
            }

            return candidate;
        }

        /// <summary>The schema an asset was built from, which is what decides its properties.</summary>
        private static string SchemaOf(AppearanceAssetElement element)
        {
            Asset asset = element.GetRenderingAsset();

            return asset == null ? null : asset.Name;
        }

        private static Color SafeColor(AssetPropertyDoubleArray4d property)
        {
            try
            {
                return property.GetValueAsColor();
            }
            catch (Exception)
            {
                // A 4d array that is not a colour at all - report nothing rather than throw out of
                // a read-only endpoint.
                return null;
            }
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

        private static string DescribeColor(Color color)
        {
            return "{r: " + color.Red + ", g: " + color.Green + ", b: " + color.Blue + "}";
        }

        private static bool SameColor(Color left, Color right)
        {
            return right != null
                && right.IsValid
                && left.Red == right.Red
                && left.Green == right.Green
                && left.Blue == right.Blue;
        }
    }
}
