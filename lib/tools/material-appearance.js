// The rendered look behind a material: its appearance asset.
//
// revit_create_material and revit_set_material_texture author a look. These two
// read and edit the one a material already has, which is what "this stucco is
// too pale" and "take the orange out of the window frames" actually need.
//
// An appearance asset has no fixed set of properties: what it carries depends
// on the SCHEMA it was built from — a Generic asset has "generic_diffuse",
// "generic_glossiness" and "generic_transparency"; a Ceramic one has
// "ceramic_color"; a Water one has neither. So there is no guessing here:
// revit_get_material_appearance lists what the asset really has, with the type
// of each property, and revit_set_material_appearance writes the properties you
// name, by the type you name, or fails the whole call.
//
// Two rules the bridge does not bend, and they are worth knowing before you
// call it:
//
//   - The asset is DUPLICATED before it is patched. Two materials very often
//     share one appearance asset, and editing it in place repaints both.
//   - A patch that does not fit — an unknown property, the wrong type, a value
//     Revit's own IsValidValue refuses — rolls the entire request back. It
//     never reports success for a change that did not happen.
//
// That second rule is why both catches answer with `isError: true`: a rolled
// back patch reaches here as a bridge exception, and without the flag an MCP
// client reads it as a successful call whose text happens to start with
// "Error:".

import { z } from "zod";

const CHANNEL = z.number().int().min(0).max(255);

const RGB = z.object({ r: CHANNEL, g: CHANNEL, b: CHANNEL });

// Named the same way as every other material tool: material_id wins, and
// material_name is the alternative. Neither is required here — the bridge is
// what says so, with the message that names the endpoint to call instead.
const MATERIAL = {
  material_id: z
    .number()
    .int()
    .optional()
    .describe("Material id from revit_list_materials or revit_create_material"),
  material_name: z
    .string()
    .min(1)
    .optional()
    .describe("Material name, as an alternative to material_id. Must already exist in the document."),
};

export function registerMaterialAppearanceTools(server, bridge) {
  server.tool(
    "revit_get_material_appearance",
    "Read the appearance asset behind a material — the rendered look, as opposed to the shading colour revit_create_material sets. Read-only: it changes nothing. It reports the material's shading side (colorRgb, transparency, shininess, smoothness, useRenderAppearanceForShading), the AppearanceAssetElement it really points at (id, name and the SCHEMA it was built from, e.g. 'Generic' or 'Ceramic'), and every direct property of that asset: 'name' as the API knows it (e.g. 'generic_diffuse'), 'type' and 'runtimeType', the typed 'value' it holds, and 'patchType' — which of revit_set_material_appearance's five types can write it, or null when none can. Anything connected to a property comes back under 'connected' with the bitmap's file, tile size in feet and rotation. 'sharedWithMaterialIds' is every OTHER material pointing at the same asset: non-empty means editing it in place would repaint them too, which is exactly what revit_set_material_appearance refuses to do. NOTE on 'readOnly': Revit hands the rendering asset out read-only outside an edit scope, so it is usually true for every property and is NOT the test of whether a property can be written — revit_set_material_appearance is, because it validates inside an edit scope. A material with no appearance asset at all is a normal state, not a broken one: it renders from Color and Transparency alone, appearanceAssetId comes back null with an empty property list, and it does NOT need a bitmap — create_generic on revit_set_material_appearance gives it a textureless Generic asset to patch. 'genericAssetAvailable' says up front whether Revit's library can supply one on this machine.",
    MATERIAL,
    { title: "Read Material Appearance", readOnlyHint: true },
    async ({ material_id, material_name }) => {
      try {
        const result = await bridge.call("/materials/appearance", {
          materialId: material_id,
          materialName: material_name,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return {
          content: [{ type: "text", text: `Error: ${error.message}` }],
          isError: true,
        };
      }
    },
  );

  server.tool(
    "revit_set_material_appearance",
    "Edit the appearance asset behind a material by naming the properties to write. Call revit_get_material_appearance FIRST: every patch names a property of the asset's own schema ('generic_diffuse', 'generic_glossiness', ...), and a name that schema does not have fails the call instead of quietly doing nothing. Each patch is {name, type, value} where type is 'color', 'double', 'integer', 'boolean' or 'string' — the type is checked against the property's real runtime type and the value against Revit's own IsValidValue, and one patch that does not fit rolls the WHOLE request back. Colours are {r, g, b} with each channel 0-255, which Revit stores inside the asset as doubles 0-1; both come back in the readback. The asset is DUPLICATED before it is patched (duplicate defaults to true), because materials very often share one and editing it in place would repaint every one of them — 'duplicate': false is refused with SHARED_APPEARANCE_ASSET when anybody else points at it, and there is deliberately no way to force a shared edit. 'sharedWithMaterialIds' in the reply is computed against the RESULTING asset, so an empty list is the evidence nothing leaked. source_appearance_asset_id duplicates another material's asset onto this one (the source keeps its look). create_generic gives a material with NO appearance asset a textureless Generic asset built from Revit's own library — a null appearance asset never means the material needs a bitmap — and answers GENERIC_ASSET_UNAVAILABLE when the material libraries are not installed. disconnect_texture only matters for a 'color' patch and only when explicitly true: a colour written under a connected bitmap renders as nothing, so that patch is refused unless you say to remove the texture. sync_shading_color also copies the first patched colour onto the material's own Color, which is what SHADED views draw when useRenderAppearanceForShading is false; otherwise the shading colour the material had is preserved, even when Revit would have repainted it to match a new asset. The reply carries 'applied', 'verified' (the committed asset read back), the resulting appearanceAssetId and the asset it replaced. One call is one undo step.",
    {
      ...MATERIAL,
      patches: z
        .array(
          z.object({
            name: z
              .string()
              .min(1)
              .describe(
                "Asset property name, exactly as revit_get_material_appearance lists it, e.g. 'generic_diffuse'",
              ),
            type: z
              .enum(["color", "double", "integer", "boolean", "string"])
              .describe(
                "Type to write it as. It must match the property's 'patchType' from revit_get_material_appearance.",
              ),
            value: z
              .union([RGB, z.number(), z.boolean(), z.string()])
              .describe(
                "The value: {r, g, b} 0-255 for 'color', a number for 'double' and 'integer', true/false for 'boolean', text for 'string'",
              ),
          }),
        )
        .optional()
        .describe(
          "Properties to write, all in this one call. Optional only when create_generic or source_appearance_asset_id is given; otherwise the call has nothing to do and is refused.",
        ),
      duplicate: z
        .boolean()
        .optional()
        .describe(
          "Duplicate the asset before patching it. Defaults to true. False edits in place and is refused when another material shares the asset.",
        ),
      source_appearance_asset_id: z
        .number()
        .int()
        .optional()
        .describe(
          "Id of an appearance asset to start from, from revit_list_materials or revit_get_material_appearance. It is duplicated and the copy assigned to THIS material only, so the material it came from is untouched.",
        ),
      create_generic: z
        .boolean()
        .optional()
        .describe(
          "Give the material a new textureless Generic appearance asset from Revit's library and patch that. This is the route for a material whose appearanceAssetId is null.",
        ),
      disconnect_texture: z
        .boolean()
        .optional()
        .describe(
          "Remove a bitmap connected to a property being patched as a colour, so the colour is what renders. Only applies to 'color' patches, and only when explicitly true.",
        ),
      sync_shading_color: z
        .boolean()
        .optional()
        .describe(
          "Also write the first patched colour onto the material's own Color, which is what shaded views draw. Needs at least one 'color' patch.",
        ),
    },
    { title: "Edit Material Appearance", readOnlyHint: false, destructiveHint: false },
    async ({
      material_id,
      material_name,
      patches,
      duplicate,
      source_appearance_asset_id,
      create_generic,
      disconnect_texture,
      sync_shading_color,
    }) => {
      try {
        // The tool arguments stay snake_case like every other tool's; the bridge
        // reads materialId / sourceAppearanceAssetId / ... — map them here rather
        // than on the C# side. A patch keeps its {name, type, value} shape on the
        // wire, because those three are the contract on both sides.
        const result = await bridge.call("/materials/set-appearance", {
          materialId: material_id,
          materialName: material_name,
          patches,
          duplicate,
          sourceAppearanceAssetId: source_appearance_asset_id,
          createGeneric: create_generic,
          disconnectTexture: disconnect_texture,
          syncShadingColor: sync_shading_color,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return {
          content: [{ type: "text", text: `Error: ${error.message}` }],
          isError: true,
        };
      }
    },
  );
}
