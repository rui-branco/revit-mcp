import { test } from "node:test";
import assert from "node:assert/strict";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { createRevitServer } from "../index.js";

// Same setup as tools.test.js and geometry.test.js: a real MCP client over an
// in-memory transport, so the validating schemas are the SDK's, with the bridge
// faked — nothing here starts Revit or opens a socket.

function fakeBridge(reply = async () => ({ ok: true })) {
  const calls = [];
  return {
    calls,
    baseUrl: "http://localhost:48884/revit-mcp",
    timeoutMs: 30000,
    async call(endpoint, payload) {
      calls.push({ endpoint, payload });
      return reply(endpoint, payload);
    },
  };
}

async function connect(bridge) {
  const server = createRevitServer(bridge);

  const client = new Client({ name: "revit-mcp-tests", version: "1.0.0" });
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  await Promise.all([server.connect(serverTransport), client.connect(clientTransport)]);
  return client;
}

function textOf(result) {
  return result.content.map((c) => c.text).join("\n");
}

// Assert the SDK rejected the arguments and that nothing reached the bridge.
async function assertRejected(client, bridge, name, args) {
  const before = bridge.calls.length;
  const result = await client.callTool({ name, arguments: args });
  assert.equal(result.isError, true, `${name} accepted ${JSON.stringify(args)}`);
  assert.match(textOf(result), /validation error/i);
  assert.equal(bridge.calls.length, before, `${name} hit the bridge despite bad args`);
}

const GREEN = { r: 96, g: 140, b: 66 };

// --- revit_list_materials ----------------------------------------------------

test("revit_list_materials: posts an empty body to /materials", async () => {
  const bridge = fakeBridge(async () => [
    { id: 4001, name: "Relva", colorRgb: GREEN, appearanceAssetId: null },
  ]);
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_list_materials", arguments: {} });
  assert.deepEqual(bridge.calls[0], { endpoint: "/materials", payload: {} });
  const rows = JSON.parse(textOf(result));
  assert.equal(rows[0].name, "Relva");
  assert.deepEqual(rows[0].colorRgb, GREEN);
  await client.close();
});

test("revit_list_materials: a material with no appearance asset reports null, not an error", async () => {
  const bridge = fakeBridge(async () => [
    { id: 4001, name: "Relva", colorRgb: GREEN, appearanceAssetId: null },
    { id: 4002, name: "Betão", colorRgb: { r: 150, g: 150, b: 150 }, appearanceAssetId: 7788 },
  ]);
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_list_materials", arguments: {} });
  const rows = JSON.parse(textOf(result));
  assert.equal(rows[0].appearanceAssetId, null);
  assert.equal(rows[1].appearanceAssetId, 7788);
  await client.close();
});

// --- revit_create_material ---------------------------------------------------

test("revit_create_material: snake_case arguments map to camelCase on the wire", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_material",
    arguments: {
      name: "Relva",
      color: GREEN,
      transparency: 0,
      shininess: 12,
      surface_foreground_pattern_id: 3301,
      appearance_asset_id: 7788,
    },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/materials/create",
    payload: {
      name: "Relva",
      color: GREEN,
      transparency: 0,
      shininess: 12,
      surfaceForegroundPatternId: 3301,
      appearanceAssetId: 7788,
      texturePath: undefined,
    },
  });
  await client.close();
});

test("revit_create_material: omitted properties are left for Revit's own defaults", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_material",
    arguments: { name: "Relva", color: GREEN },
  });
  const payload = bridge.calls[0].payload;
  assert.equal(payload.transparency, undefined);
  assert.equal(payload.shininess, undefined);
  assert.equal(payload.appearanceAssetId, undefined);
  assert.equal(payload.surfaceForegroundPatternId, undefined);
  await client.close();
});

test("revit_create_material: rejects a missing name or colour, and a channel outside 0-255", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_create_material", {});
  await assertRejected(client, bridge, "revit_create_material", { name: "Relva" });
  await assertRejected(client, bridge, "revit_create_material", { name: "", color: GREEN });
  await assertRejected(client, bridge, "revit_create_material", {
    name: "Relva",
    color: { r: 256, g: 0, b: 0 },
  });
  await assertRejected(client, bridge, "revit_create_material", {
    name: "Relva",
    color: { r: -1, g: 0, b: 0 },
  });
  await assertRejected(client, bridge, "revit_create_material", {
    name: "Relva",
    color: { r: 0, g: 0 },
  });
  await client.close();
});

test("revit_create_material: transparency is 0-100 and shininess is 0-128, Revit's own ranges", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_create_material", {
    name: "Água",
    color: GREEN,
    transparency: 101,
  });
  await assertRejected(client, bridge, "revit_create_material", {
    name: "Água",
    color: GREEN,
    transparency: -1,
  });
  await assertRejected(client, bridge, "revit_create_material", {
    name: "Água",
    color: GREEN,
    shininess: 129,
  });
  // 100 and 128 are the top of each range, not over it.
  await client.callTool({
    name: "revit_create_material",
    arguments: { name: "Água", color: GREEN, transparency: 100, shininess: 128 },
  });
  assert.equal(bridge.calls.length, 1);
  await client.close();
});

test("revit_create_material: a material that already exists comes back created:false, untouched", async () => {
  const bridge = fakeBridge(async () => ({
    id: 4001,
    name: "Relva",
    created: false,
    colorRgb: { r: 10, g: 90, b: 20 },
    transparency: 0,
    shininess: 64,
    appearanceAssetId: null,
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_material",
    arguments: { name: "Relva", color: GREEN },
  });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.created, false);
  // Reported as it really is, not as it was asked for — that is the point.
  assert.deepEqual(payload.colorRgb, { r: 10, g: 90, b: 20 });
  await client.close();
});

test("revit_create_material: a name Revit refuses is surfaced verbatim", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      'Revit failed on /materials/create: Revit would not create a material named "Relva|2": name cannot include prohibited characters, such as "{, }, [, ], |, ;, less-than sign, greater-than sign, ?, `, ~".',
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_material",
    arguments: { name: "Relva|2", color: GREEN },
  });
  assert.match(textOf(result), /^Error: Revit failed on \/materials\/create/);
  assert.match(textOf(result), /prohibited characters/);
  await client.close();
});

test("revit_create_material: the description says the colour is reused, not overwritten", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  const description = tools.find((t) => t.name === "revit_create_material").description;
  assert.match(description, /REUSED/);
  assert.match(description, /created:false/);
  assert.match(description, /0-255/);
  await client.close();
});

// --- revit_assign_material ---------------------------------------------------

test("revit_assign_material: element_ids maps to elementIds and material_id to materialId", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_assign_material",
    arguments: { material_id: 4001, element_ids: [101, 102, 103] },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/materials/assign",
    payload: { materialId: 4001, elementIds: [101, 102, 103] },
  });
  await client.close();
});

test("revit_assign_material: rejects an empty batch, a missing material and non-integer ids", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_assign_material", {});
  await assertRejected(client, bridge, "revit_assign_material", { material_id: 4001 });
  await assertRejected(client, bridge, "revit_assign_material", {
    material_id: 4001,
    element_ids: [],
  });
  await assertRejected(client, bridge, "revit_assign_material", { element_ids: [101] });
  await assertRejected(client, bridge, "revit_assign_material", {
    material_id: 4001,
    element_ids: [101.5],
  });
  await client.close();
});

test("revit_assign_material: the route used comes back per element", async () => {
  const bridge = fakeBridge(async () => ({
    materialId: 4001,
    materialName: "Relva",
    assigned: [
      { id: 101, route: "type", typeId: 800, typeName: "Muro 200mm", layers: 1, appliesToType: true },
      { id: 102, route: "parameter", parameter: "Structural Material", appliesToType: false },
    ],
    skipped: [],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_assign_material",
    arguments: { material_id: 4001, element_ids: [101, 102] },
  });
  const payload = JSON.parse(textOf(result));
  assert.deepEqual(
    payload.assigned.map((row) => row.route),
    ["type", "parameter"],
  );
  // A wall's material is a type property, and that changes every wall of the type.
  assert.equal(payload.assigned[0].appliesToType, true);
  await client.close();
});

test("revit_assign_material: a DirectShape comes back skipped, with what to do instead", async () => {
  const bridge = fakeBridge(async () => ({
    materialId: 4001,
    materialName: "Relva",
    assigned: [],
    skipped: [
      {
        id: 5001,
        reason:
          "Element 5001 is a DirectShape: its material is carried by each solid and fixed when the solid is built, so it cannot be assigned afterwards. Pass \"materialId\" to directshape/create, planting/place, pipes/create or sprinklers/place instead.",
      },
    ],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_assign_material",
    arguments: { material_id: 4001, element_ids: [5001] },
  });
  const payload = JSON.parse(textOf(result));
  assert.equal(result.isError, undefined);
  assert.equal(payload.assigned.length, 0);
  assert.match(payload.skipped[0].reason, /carried by each solid/);
  await client.close();
});

test("revit_assign_material: the description says DirectShapes cannot take one this way", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  const description = tools.find((t) => t.name === "revit_assign_material").description;
  assert.match(description, /DirectShape/);
  assert.match(description, /revit_place_planting/);
  assert.match(description, /appliesToType/);
  await client.close();
});

// --- revit_create_wall_type / revit_create_floor_type ------------------------

test("wall and floor types: snake_case arguments map to camelCase on the wire", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_wall_type",
    arguments: {
      name: "Muro betão 200mm",
      based_on_type_name: "Generic - 200mm",
      thickness: 0.656,
      material_id: 4002,
    },
  });
  await client.callTool({
    name: "revit_create_floor_type",
    arguments: {
      name: "Pavimento granito 50mm",
      based_on_type_name: "Generic - 300mm",
      thickness: 0.164,
      material_name: "Granito",
    },
  });
  assert.deepEqual(bridge.calls, [
    {
      endpoint: "/walltypes/create",
      payload: {
        name: "Muro betão 200mm",
        basedOnTypeName: "Generic - 200mm",
        thickness: 0.656,
        materialId: 4002,
        materialName: undefined,
      },
    },
    {
      endpoint: "/floortypes/create",
      payload: {
        name: "Pavimento granito 50mm",
        basedOnTypeName: "Generic - 300mm",
        thickness: 0.164,
        materialId: undefined,
        materialName: "Granito",
      },
    },
  ]);
  await client.close();
});

// The live call that used to fail was floortypes/create with exactly this shape. The fix for it is
// entirely the bridge's - it forces EndCapCondition.NoEndCap on the new structure, because only a
// wall type may carry a real end cap - so the wire payload must still be these five keys and
// nothing else: a new argument here would mean the fix leaked into the tool surface.
test("floor types: the payload is the same five keys as before the end cap fix", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_floor_type",
    arguments: {
      name: "Pavimento em pedra",
      based_on_type_name: "Generic 300mm",
      thickness: 0.33,
      material_name: "Pedra natural",
    },
  });
  assert.equal(bridge.calls.length, 1);
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/floortypes/create",
    payload: {
      name: "Pavimento em pedra",
      basedOnTypeName: "Generic 300mm",
      thickness: 0.33,
      materialId: undefined,
      materialName: "Pedra natural",
    },
  });
  assert.deepEqual(Object.keys(bridge.calls[0].payload), [
    "name",
    "basedOnTypeName",
    "thickness",
    "materialId",
    "materialName",
  ]);
  const { tools } = await client.listTools();
  const floor = tools.find((t) => t.name === "revit_create_floor_type");
  assert.deepEqual(Object.keys(floor.inputSchema.properties), [
    "name",
    "based_on_type_name",
    "thickness",
    "material_id",
    "material_name",
  ]);
  await client.close();
});

test("wall and floor types: only the name is required; the rest is left to the bridge", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_wall_type",
    arguments: { name: "Muro seco" },
  });
  const payload = bridge.calls[0].payload;
  assert.equal(payload.basedOnTypeName, undefined);
  assert.equal(payload.thickness, undefined);
  assert.equal(payload.materialId, undefined);
  await client.close();
});

test("wall and floor types: reject an empty name and a non-positive thickness", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  for (const name of ["revit_create_wall_type", "revit_create_floor_type"]) {
    await assertRejected(client, bridge, name, {});
    await assertRejected(client, bridge, name, { name: "" });
    await assertRejected(client, bridge, name, { name: "Muro", thickness: 0 });
    await assertRejected(client, bridge, name, { name: "Muro", thickness: -0.5 });
    await assertRejected(client, bridge, name, { name: "Muro", based_on_type_name: "" });
  }
  await client.close();
});

test("wall and floor types: the thickness and material come back read off the type", async () => {
  const bridge = fakeBridge(async () => ({
    id: 800,
    name: "Muro betão 200mm",
    created: true,
    basedOn: "Generic - 200mm",
    thickness: 0.656,
    materialId: 4002,
    materialName: "Betão",
    layers: 1,
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_wall_type",
    arguments: { name: "Muro betão 200mm", thickness: 0.656, material_id: 4002 },
  });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.created, true);
  assert.equal(payload.materialName, "Betão");
  assert.equal(payload.layers, 1);
  await client.close();
});

test("wall and floor types: an existing name is reused, not rebuilt", async () => {
  const bridge = fakeBridge(async () => ({
    id: 800,
    name: "Muro betão 200mm",
    created: false,
    basedOn: null,
    thickness: 0.492,
    materialId: 4009,
    materialName: "Alvenaria",
    layers: 3,
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_wall_type",
    arguments: { name: "Muro betão 200mm", thickness: 0.656, material_id: 4002 },
  });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.created, false);
  // What the type really carries, not what was asked for.
  assert.equal(payload.thickness, 0.492);
  assert.equal(payload.materialId, 4009);
  await client.close();
});

test("wall and floor types: an unknown base type is the bridge's error, surfaced verbatim", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      'Revit failed on /walltypes/create: Unknown wall type "Generic - 999mm". wall types in this document: Generic - 200mm, Generic - 300mm.',
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_wall_type",
    arguments: { name: "Muro", based_on_type_name: "Generic - 999mm" },
  });
  assert.match(textOf(result), /^Error: Revit failed on \/walltypes\/create/);
  assert.match(textOf(result), /Generic - 200mm/);
  await client.close();
});

test("wall and floor types: the descriptions say the material is a type property", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  const wall = tools.find((t) => t.name === "revit_create_wall_type").description;
  const floor = tools.find((t) => t.name === "revit_create_floor_type").description;
  assert.match(wall, /property of its type/);
  assert.match(wall, /revit_create_walls/);
  assert.match(floor, /lives on its type/);
  assert.match(floor, /revit_create_floor/);
  await client.close();
});

// --- revit_set_material_texture ----------------------------------------------

const TEXTURE =
  "C:\\Program Files\\Common Files\\Autodesk Shared\\Materials\\Textures\\1\\Mats\\grass_color.jpg";

test("revit_set_material_texture: snake_case arguments map to camelCase on the wire", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_set_material_texture",
    arguments: {
      material_id: 4001,
      texture_path: TEXTURE,
      scale: { x: 3, y: 1.5 },
      rotation: 30,
      tint: { r: 120, g: 200, b: 90 },
    },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/materials/set-texture",
    payload: {
      materialId: 4001,
      materialName: undefined,
      texturePath: TEXTURE,
      scale: { x: 3, y: 1.5 },
      rotation: 30,
      tint: { r: 120, g: 200, b: 90 },
    },
  });
  await client.close();
});

test("revit_set_material_texture: a material can be named instead of numbered", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_set_material_texture",
    arguments: { material_name: "Relva", texture_path: TEXTURE },
  });
  const payload = bridge.calls[0].payload;
  assert.equal(payload.materialName, "Relva");
  assert.equal(payload.materialId, undefined);
  await client.close();
});

test("revit_set_material_texture: omitted scale, rotation and tint are left to Revit", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_set_material_texture",
    arguments: { material_id: 4001, texture_path: TEXTURE },
  });
  const payload = bridge.calls[0].payload;
  assert.equal(payload.scale, undefined);
  assert.equal(payload.rotation, undefined);
  assert.equal(payload.tint, undefined);
  await client.close();
});

test("revit_set_material_texture: rejects a missing or empty texture_path", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_set_material_texture", { material_id: 4001 });
  await assertRejected(client, bridge, "revit_set_material_texture", {
    material_id: 4001,
    texture_path: "",
  });
  await client.close();
});

test("revit_set_material_texture: a tile size must be positive on both axes", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_set_material_texture", {
    material_id: 4001,
    texture_path: TEXTURE,
    scale: { x: 0, y: 3 },
  });
  await assertRejected(client, bridge, "revit_set_material_texture", {
    material_id: 4001,
    texture_path: TEXTURE,
    scale: { x: 3, y: -1 },
  });
  // A scale needs both axes — one alone is not "the size of a tile".
  await assertRejected(client, bridge, "revit_set_material_texture", {
    material_id: 4001,
    texture_path: TEXTURE,
    scale: { x: 3 },
  });
  await client.close();
});

test("revit_set_material_texture: a tint channel outside 0-255 is refused", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_set_material_texture", {
    material_id: 4001,
    texture_path: TEXTURE,
    tint: { r: 256, g: 0, b: 0 },
  });
  await assertRejected(client, bridge, "revit_set_material_texture", {
    material_id: 4001,
    texture_path: TEXTURE,
    tint: { r: 0, g: 0 },
  });
  await client.close();
});

test("revit_set_material_texture: TEXTURE_NOT_FOUND comes back verbatim", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      'Revit failed on /materials/set-texture: No file exists at "C:\\nope\\missing.png". The bitmap is referenced by the appearance asset rather than copied into the model, so it has to be a path this machine can read.',
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_material_texture",
    arguments: { material_id: 4001, texture_path: "C:\\nope\\missing.png" },
  });
  assert.match(textOf(result), /^Error: Revit failed on \/materials\/set-texture/);
  assert.match(textOf(result), /No file exists at/);
  await client.close();
});

test("revit_set_material_texture: reports what it set, and what the asset did not have", async () => {
  const bridge = fakeBridge(async () => ({
    materialId: 4001,
    materialName: "Relva",
    assetAuthored: true,
    assetReason: "the material had no appearance asset",
    appearanceAssetId: 212630,
    appearanceAssetName: "Relva",
    assetSchema: "GenericSchema",
    colorRgb: GREEN,
    texturePath: TEXTURE,
    diffuseProperty: "generic_diffuse",
    textureApplied: true,
    set: {
      unifiedbitmap_Bitmap: TEXTURE,
      texture_RealWorldScaleX: 36,
      texture_RealWorldScaleY: 36,
    },
    missing: ["texture_WAngle"],
    verified: {
      connectedAsset: "UnifiedBitmap",
      bitmap: TEXTURE,
      scaleFeet: { x: 3, y: 3 },
      rotation: 0,
    },
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_material_texture",
    arguments: { material_id: 4001, texture_path: TEXTURE, scale: { x: 3, y: 3 } },
  });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.textureApplied, true);
  assert.equal(payload.diffuseProperty, "generic_diffuse");
  // The point of the report: the bitmap really is on the saved asset.
  assert.equal(payload.verified.bitmap, TEXTURE);
  assert.deepEqual(payload.verified.scaleFeet, { x: 3, y: 3 });
  // A property the schema did not carry is reported, not thrown over.
  assert.deepEqual(payload.missing, ["texture_WAngle"]);
  await client.close();
});

test("revit_set_material_texture: a texture that could not be applied says so rather than claiming success", async () => {
  const bridge = fakeBridge(async () => ({
    materialId: 4001,
    materialName: "Água",
    assetAuthored: false,
    assetReason:
      'its appearance asset is a "WaterSchema" asset - but Revit\'s asset library offered no "Generic" asset to replace it with, so it was textured in place',
    appearanceAssetId: 161256,
    appearanceAssetName: "Água",
    assetSchema: "WaterSchema",
    texturePath: TEXTURE,
    diffuseProperty: null,
    textureApplied: false,
    set: {},
    missing: ["generic_diffuse", "opaque_albedo", "ceramic_color"],
    verified: null,
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_material_texture",
    arguments: { material_name: "Água", texture_path: TEXTURE },
  });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.textureApplied, false);
  assert.equal(payload.verified, null);
  assert.ok(payload.missing.includes("generic_diffuse"));
  await client.close();
});

test("revit_set_material_texture: naming neither material is the bridge's call, not a schema error", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      'Revit failed on /materials/set-texture: "materialId" is required (or "materialName"). Call /revit-mcp/materials for the materials in this document.',
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_material_texture",
    arguments: { texture_path: TEXTURE },
  });
  assert.equal(bridge.calls.length, 1, "the tool should let the bridge decide");
  assert.match(textOf(result), /"materialId" is required/);
  await client.close();
});

test("revit_set_material_texture: the description says where Revit's own textures live", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  const description = tools.find((t) => t.name === "revit_set_material_texture").description;
  assert.match(description, /Autodesk Shared\\Materials\\Textures/);
  assert.match(description, /TEXTURE_NOT_FOUND/);
  await client.close();
});

// --- revit_create_material, textured -----------------------------------------

test("revit_create_material: texture_path goes to the bridge as texturePath", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_material",
    arguments: { name: "Relva", color: GREEN, texture_path: TEXTURE },
  });
  assert.equal(bridge.calls[0].endpoint, "/materials/create");
  assert.equal(bridge.calls[0].payload.texturePath, TEXTURE);
  await client.close();
});

test("revit_create_material: no texture_path means no texturePath on the wire", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_material",
    arguments: { name: "Relva", color: GREEN },
  });
  assert.equal(bridge.calls[0].payload.texturePath, undefined);
  await client.close();
});

test("revit_create_material: an empty texture_path is refused before the bridge is called", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_create_material", {
    name: "Relva",
    color: GREEN,
    texture_path: "",
  });
  await client.close();
});

test("revit_create_material: what the texture did comes back under 'texture'", async () => {
  const bridge = fakeBridge(async () => ({
    id: 212629,
    name: "Relva",
    created: true,
    colorRgb: GREEN,
    transparency: 0,
    shininess: 64,
    appearanceAssetId: 212630,
    texture: {
      assetAuthored: true,
      assetReason: "the material had no appearance asset",
      diffuseProperty: "generic_diffuse",
      textureApplied: true,
      verified: { connectedAsset: "UnifiedBitmap", bitmap: TEXTURE },
    },
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_material",
    arguments: { name: "Relva", color: GREEN, texture_path: TEXTURE },
  });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.texture.textureApplied, true);
  assert.equal(payload.texture.verified.bitmap, TEXTURE);
  await client.close();
});

// --- the whole surface -------------------------------------------------------

test("the material tools are the only ones that reach the material endpoints", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({ name: "revit_list_materials", arguments: {} });
  await client.callTool({
    name: "revit_create_material",
    arguments: { name: "Relva", color: GREEN },
  });
  await client.callTool({
    name: "revit_set_material_texture",
    arguments: { material_id: 4001, texture_path: TEXTURE },
  });
  await client.callTool({
    name: "revit_assign_material",
    arguments: { material_id: 4001, element_ids: [101] },
  });
  await client.callTool({ name: "revit_create_wall_type", arguments: { name: "Muro" } });
  await client.callTool({ name: "revit_create_floor_type", arguments: { name: "Pavimento" } });
  assert.deepEqual(
    bridge.calls.map((c) => c.endpoint),
    [
      "/materials",
      "/materials/create",
      "/materials/set-texture",
      "/materials/assign",
      "/walltypes/create",
      "/floortypes/create",
    ],
  );
  await client.close();
});
