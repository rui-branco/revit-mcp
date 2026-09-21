// Materials, and the wall and floor types that carry one.
//
// Nothing else here set a material, so everything the bridge built rendered in
// Revit's default grey. There are two routes onto an element and they are not
// interchangeable:
//
//   - Geometry built by revit_create_directshape, revit_place_planting,
//     revit_create_pipes and revit_place_sprinklers carries its material on the
//     solid. That is decided when the solid is built, so those tools take a
//     material_id and revit_assign_material cannot help them afterwards.
//   - A wall or a floor takes its material from its TYPE's compound structure:
//     revit_create_wall_type / revit_create_floor_type author one, and
//     revit_create_walls / revit_create_floor build with it by name.
//
// revit_assign_material covers what is left: elements with a real material
// parameter, and walls/floors whose type it can edit. It says per element which
// route it used, and which elements could take neither.
//
// Colour channels are 0-255; transparency is 0-100 and shininess 0-128, which
// are Revit's own ranges. Thickness is feet, like every other length here.
//
// A colour on its own is still flat paint. revit_set_material_texture is what
// puts a real bitmap on a material — grass that looks like grass rather than
// like green — and Revit ships the bitmaps to do it with, under
// C:\Program Files\Common Files\Autodesk Shared\Materials\Textures.

import { z } from "zod";

const MATERIAL_CHANNEL = z.number().int().min(0).max(255);

// Shared by the two type tools: they differ only in which type kind they make.
const HOST_TYPE = {
  name: z.string().min(1).describe("Name for the new type. Must be unique among types of this kind."),
  based_on_type_name: z
    .string()
    .min(1)
    .optional()
    .describe("Existing type to duplicate. Omit it to duplicate the document's default type."),
  thickness: z
    .number()
    .positive()
    .optional()
    .describe("Thickness of the single structural layer, in feet. Omit it to keep the source type's."),
  material_id: z
    .number()
    .int()
    .optional()
    .describe("Material id for that layer, from revit_list_materials or revit_create_material."),
  material_name: z
    .string()
    .min(1)
    .optional()
    .describe("Material name, as an alternative to material_id. Must already exist in the document."),
};

export function registerMaterialTools(server, bridge) {
  server.tool(
    "revit_list_materials",
    "List the materials in the document: id, name, colour as {r, g, b}, and the id of its appearance asset when it has one. This is where a material_id comes from — for revit_create_directshape, revit_place_planting, revit_create_pipes, revit_place_sprinklers, revit_assign_material, revit_create_wall_type and revit_create_floor_type. A Revit template usually ships dozens, so check here before creating a new one.",
    {},
    { title: "List Materials", readOnlyHint: true },
    async () => {
      try {
        const result = await bridge.call("/materials", {});
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_create_material",
    "Create a material with a colour, so the model stops rendering in Revit's default grey. 'color' is {r, g, b}, each 0-255. 'transparency' is 0-100 (0 is opaque — use it for water and glass) and 'shininess' is 0-128; omitted, Revit's own defaults are left alone. A material that already exists by this name is REUSED and comes back with created:false and its current properties — nothing about it is overwritten, so re-running the same call is safe, and a colour you did not ask for means somebody else authored that material. Shading is set to follow the colour rather than a render appearance, which is what makes the colour visible in a shaded view. 'appearance_asset_id' takes the look of an existing material's appearance asset (see revit_list_materials): it is duplicated, not shared. 'texture_path' puts a real bitmap on it in the same call — what that did comes back under 'texture'; it is ignored for a material that already exists, which revit_set_material_texture can texture instead.",
    {
      name: z.string().min(1).describe("Material name, e.g. 'Relva' or 'Betão pigmentado'"),
      color: z
        .object({ r: MATERIAL_CHANNEL, g: MATERIAL_CHANNEL, b: MATERIAL_CHANNEL })
        .describe("Colour as {r, g, b}, each channel 0-255"),
      transparency: z
        .number()
        .int()
        .min(0)
        .max(100)
        .optional()
        .describe("Transparency 0-100; 0 is opaque. Omit it to keep Revit's default."),
      shininess: z
        .number()
        .int()
        .min(0)
        .max(128)
        .optional()
        .describe("Shininess 0-128. Omit it to keep Revit's default."),
      surface_foreground_pattern_id: z
        .number()
        .int()
        .optional()
        .describe("Id of a FillPatternElement to use as the surface foreground pattern"),
      appearance_asset_id: z
        .number()
        .int()
        .optional()
        .describe(
          "Id of an AppearanceAssetElement to copy the rendered look of, from revit_list_materials. It is duplicated so later edits cannot leak between materials.",
        ),
      texture_path: z
        .string()
        .min(1)
        .optional()
        .describe(
          "Full path to a texture bitmap, to make the material textured in this same call. Revit's own library is under C:\\Program Files\\Common Files\\Autodesk Shared\\Materials\\Textures. A file that does not exist is refused before anything is created. Tile size, rotation and tint belong to revit_set_material_texture.",
        ),
    },
    { title: "Create Material", readOnlyHint: false, destructiveHint: false },
    async ({
      name,
      color,
      transparency,
      shininess,
      surface_foreground_pattern_id,
      appearance_asset_id,
      texture_path,
    }) => {
      try {
        const result = await bridge.call("/materials/create", {
          name,
          color,
          transparency,
          shininess,
          surfaceForegroundPatternId: surface_foreground_pattern_id,
          appearanceAssetId: appearance_asset_id,
          texturePath: texture_path,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_set_material_texture",
    "Put a real texture bitmap on an existing material, which is the difference between a surface that is green and one that looks like grass. Revit ships thousands of bitmaps under C:\\Program Files\\Common Files\\Autodesk Shared\\Materials\\Textures — pass a full path to one of those, or to any image file this machine can read; it is REFERENCED by the model, not copied into it, so a path that stops existing is a texture that stops rendering. A file that is not there is refused with TEXTURE_NOT_FOUND. 'scale' is the real-world size of one tile of the bitmap, {x, y} in feet — 3 by 3 makes a paving texture read as 3-foot slabs. 'rotation' is degrees and 'tint' is a colour multiplied over the bitmap. The material is given its own Generic appearance asset unless it already has one nobody else shares, so texturing one material can never change another's look; the reply says whether that happened and why. It reports every asset property it wrote under 'set', anything the asset turned out not to have under 'missing', and what the saved asset reads back as under 'verified'. One call is one undo step.",
    {
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
      texture_path: z
        .string()
        .min(1)
        .describe(
          "Full path to the texture bitmap, e.g. 'C:\\Program Files\\Common Files\\Autodesk Shared\\Materials\\Textures\\1\\Mats\\grass_color.jpg'",
        ),
      scale: z
        .object({ x: z.number().positive(), y: z.number().positive() })
        .optional()
        .describe(
          "Real-world size of one tile of the bitmap, {x, y} in feet. Omit it to leave Revit's own tile size.",
        ),
      rotation: z.number().optional().describe("Rotation of the texture, in degrees"),
      tint: z
        .object({ r: MATERIAL_CHANNEL, g: MATERIAL_CHANNEL, b: MATERIAL_CHANNEL })
        .optional()
        .describe("Colour multiplied over the bitmap, as {r, g, b}, each channel 0-255"),
    },
    { title: "Set Material Texture", readOnlyHint: false, destructiveHint: false },
    async ({ material_id, material_name, texture_path, scale, rotation, tint }) => {
      try {
        const result = await bridge.call("/materials/set-texture", {
          materialId: material_id,
          materialName: material_name,
          texturePath: texture_path,
          scale,
          rotation,
          tint,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_assign_material",
    "Put an existing material on elements that already exist. Two routes, chosen per element and reported per element: a wall, floor, roof, ceiling or toposolid gets it through its TYPE's compound structure — which changes every element of that type, and the row says so with appliesToType — and anything with a writable material parameter gets it written there. Elements that can take neither come back under 'skipped' with the reason. DirectShape elements (everything from revit_create_directshape, revit_place_planting, revit_create_pipes and revit_place_sprinklers) are always skipped: their material is carried by the solid and fixed when it is built, so pass material_id to those tools instead. One call is one undo step.",
    {
      material_id: z.number().int().describe("Material id from revit_list_materials or revit_create_material"),
      element_ids: z.array(z.number().int()).min(1).describe("Elements to put the material on"),
    },
    { title: "Assign Material", readOnlyHint: false, destructiveHint: false },
    async ({ material_id, element_ids }) => {
      try {
        const result = await bridge.call("/materials/assign", {
          materialId: material_id,
          elementIds: element_ids,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_create_wall_type",
    "Create a wall type carrying a material and a thickness, by duplicating an existing type and giving it a single structural layer. A wall's material is a property of its type, not of the wall, so this is the only way to get walls that are not the template's default grey: create the type, then pass its name to revit_create_walls as wall_type. A type that already exists by this name is REUSED and comes back with created:false and its current thickness and material — it is not re-cut to match the request. Thickness is feet (Revit internal units). One call is one undo step.",
    HOST_TYPE,
    { title: "Create Wall Type", readOnlyHint: false, destructiveHint: false },
    async ({ name, based_on_type_name, thickness, material_id, material_name }) => {
      try {
        const result = await bridge.call("/walltypes/create", {
          name,
          basedOnTypeName: based_on_type_name,
          thickness,
          materialId: material_id,
          materialName: material_name,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_create_floor_type",
    "Create a floor type carrying a material and a thickness, by duplicating an existing type and giving it a single structural layer. Like walls, a floor's material lives on its type — this is how paving stops being grey: create the type, then pass its name to revit_create_floor as type_name. A type that already exists by this name is REUSED and comes back with created:false and its current thickness and material. Thickness is feet (Revit internal units). One call is one undo step.",
    HOST_TYPE,
    { title: "Create Floor Type", readOnlyHint: false, destructiveHint: false },
    async ({ name, based_on_type_name, thickness, material_id, material_name }) => {
      try {
        const result = await bridge.call("/floortypes/create", {
          name,
          basedOnTypeName: based_on_type_name,
          thickness,
          materialId: material_id,
          materialName: material_name,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );
}
