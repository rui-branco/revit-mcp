// Geometry built straight into the project document, with no family behind it.
//
// These exist because family content is an optional Autodesk download: a Revit
// install can have zero loaded families AND zero family templates, which leaves
// revit_place_families with nothing to place and no way to author a replacement.
// A DirectShape needs neither — Revit builds the solid in the project document
// and hangs it off a real category, so it is a proper element: visible,
// selectable, categorised and schedulable.
//
// All lengths and coordinates are Revit internal units: decimal feet.

import { z } from "zod";

const point3 = z.object({ x: z.number(), y: z.number(), z: z.number() });
const point2 = z.object({ x: z.number(), y: z.number() });

// Per-element identity, on every entry of every batch here. The per-entry value
// wins over the top-level one, which is what turns a schedule of 38 identical
// rows into a species breakdown — Comments and Mark are real Revit parameters
// and a schedule can group by either.
const IDENTITY = {
  name: z.string().min(1).optional().describe("Name for this element"),
  comments: z
    .string()
    .min(1)
    .optional()
    .describe("Written to the element's Comments parameter. Overrides the top-level one."),
  mark: z
    .string()
    .min(1)
    .optional()
    .describe("Written to the element's Mark parameter. Overrides the top-level one."),
};

const placementPoint = z.object({
  x: z.number(),
  y: z.number(),
  z: z.number().optional().describe("Base elevation in feet; defaults to 0"),
  ...IDENTITY,
});

// Batch-wide, on all four of these tools.
//
// The type is what makes "Edit Type" work in Revit's Properties palette and what
// lets a schedule or a filter work by type at all — without one these elements
// have no type parameters whatsoever. Omitted, it follows the name, so a batch
// that already names its species gets a type per species.
//
// The material has to be named HERE and not afterwards: this geometry carries
// its material on each solid, fixed when the solid is built, so nothing can put
// one on after the fact. Without it the model renders in Revit's default grey.
const TYPE_AND_MATERIAL = {
  type_name: z
    .string()
    .min(1)
    .optional()
    .describe(
      "Name of the element type to create or reuse. Omit it to name the type after the element's own name. One type is shared by every element in the batch that resolves to the same name.",
    ),
  material_id: z
    .number()
    .int()
    .optional()
    .describe("Material id from revit_list_materials or revit_create_material. Wins over material_name."),
  material_name: z
    .string()
    .min(1)
    .optional()
    .describe("Material name, as an alternative to material_id. Must already exist in the document."),
};

// Kept as an array so the same variants can make both the top-level shape union
// and the one inside a group's parts.
const PRIMITIVES = [
  z.object({
    kind: z.literal("cylinder"),
    ...IDENTITY,
    base: point3.describe("Centre of the bottom face, in feet"),
    radius: z.number().positive().describe("Radius in feet"),
    height: z.number().positive().describe("Height in feet, extruded straight up"),
  }),
  z.object({
    kind: z.literal("box"),
    ...IDENTITY,
    min: point3.describe("Low corner, in feet"),
    max: point3.describe("High corner, in feet. Its z must be above min's."),
  }),
  z.object({
    kind: z.literal("sphere"),
    ...IDENTITY,
    center: point3.describe("Centre of the sphere, in feet"),
    radius: z.number().positive().describe("Radius in feet"),
  }),
  z.object({
    kind: z.literal("cone"),
    ...IDENTITY,
    base: point3.describe("Centre of the base circle, in feet"),
    radius: z.number().positive().describe("Base radius in feet"),
    height: z.number().positive().describe("Height in feet; the apex is straight above the base"),
  }),
  z.object({
    kind: z.literal("extrusion"),
    ...IDENTITY,
    profile: z
      .array(point2)
      .min(3)
      .describe("Closed polygon in plan, in feet. Closed automatically; at least 3 points."),
    base_z: z.number().optional().describe("Elevation of the profile in feet; defaults to 0"),
    height: z.number().positive().describe("Height in feet, extruded straight up"),
  }),
];

const primitive = z.discriminatedUnion("kind", PRIMITIVES);

const shape = z.discriminatedUnion("kind", [
  ...PRIMITIVES,
  z.object({
    kind: z.literal("group"),
    ...IDENTITY,
    parts: z
      .array(primitive)
      .min(1)
      .describe("Solids that make up ONE element — a tree is a trunk cylinder plus a crown sphere"),
  }),
]);

// The tool arguments stay snake_case like every other tool's; the bridge reads
// baseZ — map it here rather than on the C# side.
function toWirePrimitive(part) {
  if (part.kind !== "extrusion") return part;

  const { base_z, ...rest } = part;
  return { ...rest, baseZ: base_z };
}

function toWireShape(entry) {
  if (entry.kind !== "group") return toWirePrimitive(entry);

  return { ...entry, parts: entry.parts.map(toWirePrimitive) };
}

export function registerGeometryTools(server, bridge) {
  server.tool(
    "revit_create_directshape",
    "Create elements from raw geometry, with no family involved. Use this when revit_list_family_symbols comes back empty: family libraries are an optional Autodesk download, and without them there is nothing to place and no template to author from — this builds the solids directly in the project document instead. 'category' is a Revit BuiltInCategory name without its OST_ prefix (Planting, LightingFixtures, Furniture, Site, Walls, GenericModel). Each entry in 'shapes' becomes one element; a 'group' entry becomes one element made of several solids, which is how a tree is a trunk plus a crown in a single schedulable element. Give each entry 'comments' and 'mark' (or set them once at the top level) — they land on the element's real Comments and Mark parameters, which is what lets a schedule break the batch down instead of counting it as one lump. Every element gets a real element type ('type_name', defaulting to the name), so Edit Type works and schedules can group by type, and the response says which type each one got. Give it a 'material_id' too — geometry built without one renders in Revit's default grey, and the material cannot be assigned afterwards because it is carried by the solid itself. All coordinates and sizes are feet (Revit internal units). Pass every shape in one call: the whole batch is one undo step, and a shape Revit refuses comes back under 'failed' with its index while the rest are still created.",
    {
      category: z
        .string()
        .min(1)
        .describe("BuiltInCategory name without OST_, e.g. 'Planting', 'LightingFixtures', 'Furniture', 'Site'"),
      name: z
        .string()
        .min(1)
        .optional()
        .describe("Name applied to every element that does not carry its own"),
      ...TYPE_AND_MATERIAL,
      comments: z
        .string()
        .min(1)
        .optional()
        .describe("Comments applied to every element that does not carry its own"),
      mark: z
        .string()
        .min(1)
        .optional()
        .describe("Mark applied to every element that does not carry its own"),
      shapes: z
        .array(shape)
        .min(1)
        .describe("One element per entry, all in this one call"),
    },
    async ({ category, name, type_name, material_id, material_name, comments, mark, shapes }) => {
      try {
        const result = await bridge.call("/directshape/create", {
          category,
          name,
          typeName: type_name,
          materialId: material_id,
          materialName: material_name,
          comments,
          mark,
          shapes: shapes.map(toWireShape),
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_place_planting",
    "Plant trees: one element in the Planting category per point, each a trunk cylinder with a crown sphere sitting on top of it. This is the no-family route — it builds the geometry directly rather than placing a loaded family, which is what makes it work on a Revit with no family content installed. Give each point its own 'name', 'comments' and 'mark' (the species, the size, the tag) — they go on the element's real parameters, which is the difference between a planting schedule that reads 'Planting: 38' and one that breaks down by species. Pass a 'material_id' (make one with revit_create_material — a green for foliage) or the trees come out Revit's default grey, and the material cannot be added afterwards: it is carried by the solid. Each tree also gets a real element type named after it, so a type-based schedule or filter works. All sizes are feet (Revit internal units); the defaults are trunk_height 8, trunk_radius 0.5, crown_radius 6, which is a 20 ft tree. Pass every point in one call: the whole batch is one undo step.",
    {
      points: z
        .array(placementPoint)
        .min(1)
        .describe("Tree positions in feet, one tree per point. All in this one call."),
      trunk_height: z.number().positive().optional().describe("Trunk height in feet (default 8)"),
      trunk_radius: z.number().positive().optional().describe("Trunk radius in feet (default 0.5)"),
      crown_radius: z.number().positive().optional().describe("Crown sphere radius in feet (default 6)"),
      name: z.string().min(1).optional().describe("Name for each element (default 'Tree')"),
      ...TYPE_AND_MATERIAL,
      comments: z
        .string()
        .min(1)
        .optional()
        .describe("Comments applied to every tree that does not carry its own"),
      mark: z.string().min(1).optional().describe("Mark applied to every tree that does not carry its own"),
    },
    async ({
      points,
      trunk_height,
      trunk_radius,
      crown_radius,
      name,
      type_name,
      material_id,
      material_name,
      comments,
      mark,
    }) => {
      try {
        const result = await bridge.call("/planting/place", {
          points,
          trunkHeight: trunk_height,
          trunkRadius: trunk_radius,
          crownRadius: crown_radius,
          name,
          typeName: type_name,
          materialId: material_id,
          materialName: material_name,
          comments,
          mark,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_create_pipes",
    "Model pipe runs — irrigation, drainage — as geometry, with no MEP family involved. Each run becomes one element built from a cylinder per segment between consecutive points, which is the same no-family route as revit_create_directshape. Points are x/y/z in feet (Revit internal units); z defaults to 0, so a run authored in plan lies at grade. 'radius' is per run and defaults to 0.08 ft (about 50 mm). 'category' defaults to PipeCurves when Revit allows a DirectShape in it and GenericModel when it does not — the response says which each element got, so read it rather than assuming. 'material_id' and 'type_name' work as they do on revit_create_directshape: the material has to be given here because the solid carries it, and every run gets a real element type. Pass every run in one call: the whole batch is one undo step.",
    {
      runs: z
        .array(
          z.object({
            points: z
              .array(placementPoint)
              .min(2)
              .describe("Route of this run in feet, at least 2 points. A cylinder per segment."),
            radius: z.number().positive().optional().describe("Pipe radius in feet (default 0.08)"),
            ...IDENTITY,
          }),
        )
        .min(1)
        .describe("Runs to model, all in this one call"),
      category: z
        .string()
        .min(1)
        .optional()
        .describe("BuiltInCategory name without OST_. Omit it for PipeCurves, falling back to GenericModel."),
      name: z.string().min(1).optional().describe("Name for each element (default 'Pipe')"),
      ...TYPE_AND_MATERIAL,
      comments: z
        .string()
        .min(1)
        .optional()
        .describe("Comments applied to every run that does not carry its own"),
      mark: z.string().min(1).optional().describe("Mark applied to every run that does not carry its own"),
    },
    async ({ runs, category, name, type_name, material_id, material_name, comments, mark }) => {
      try {
        const result = await bridge.call("/pipes/create", {
          runs,
          category,
          name,
          typeName: type_name,
          materialId: material_id,
          materialName: material_name,
          comments,
          mark,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_place_sprinklers",
    "Place sprinkler heads: one small element per point, a cylinder standing proud of the ground. The no-family route again — no MEP family is needed or used. The category is Sprinklers when Revit allows a DirectShape in it and GenericModel when it does not, and the response says which. All sizes are feet (Revit internal units); the defaults are radius 0.15 and height 0.5. Give each point its own 'comments' and 'mark' (the head type, the zone) so the irrigation schedule breaks down instead of counting heads, a 'type_name' to type them, and a 'material_id' so they are not grey. Pass every point in one call: the whole batch is one undo step.",
    {
      points: z
        .array(placementPoint)
        .min(1)
        .describe("Head positions in feet, one head per point. All in this one call."),
      radius: z.number().positive().optional().describe("Head radius in feet (default 0.15)"),
      height: z.number().positive().optional().describe("Head height in feet (default 0.5)"),
      name: z.string().min(1).optional().describe("Name for each element (default 'Sprinkler')"),
      ...TYPE_AND_MATERIAL,
      comments: z
        .string()
        .min(1)
        .optional()
        .describe("Comments applied to every head that does not carry its own"),
      mark: z.string().min(1).optional().describe("Mark applied to every head that does not carry its own"),
    },
    async ({ points, radius, height, name, type_name, material_id, material_name, comments, mark }) => {
      try {
        const result = await bridge.call("/sprinklers/place", {
          points,
          radius,
          height,
          name,
          typeName: type_name,
          materialId: material_id,
          materialName: material_name,
          comments,
          mark,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );
}
