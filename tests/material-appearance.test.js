import { test } from "node:test";
import assert from "node:assert/strict";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { createRevitServer } from "../index.js";

// Same setup as materials.test.js and tools.test.js: a real MCP client over an
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

const DARK = { r: 54, g: 48, b: 44 };

// What a Generic asset really answers with, trimmed to the rows a caller uses.
const GENERIC_APPEARANCE = {
  materialId: 4101,
  materialName: "Reboco claro",
  colorRgb: { r: 236, g: 230, b: 214 },
  transparency: 0,
  shininess: 64,
  smoothness: 50,
  useRenderAppearanceForShading: true,
  appearanceAssetId: 212630,
  appearanceAssetName: "Reboco claro",
  genericAssetAvailable: true,
  assetSchema: "Generic",
  assetTitle: "Generic",
  assetLibrary: null,
  sharedWithMaterialIds: [4102, 4103],
  propertyCount: 3,
  properties: [
    {
      name: "generic_diffuse",
      type: "Double4",
      runtimeType: "AssetPropertyDoubleArray4d",
      patchType: "color",
      readOnly: true,
      value: { r: 236, g: 230, b: 214 },
      valueDoubles: [0.925, 0.902, 0.839, 1],
      valueFeet: null,
      unit: null,
      connectedCount: 0,
      connected: null,
    },
    {
      name: "generic_glossiness",
      type: "Double1",
      runtimeType: "AssetPropertyDouble",
      patchType: "double",
      readOnly: true,
      value: 0.2,
      valueDoubles: null,
      valueFeet: null,
      unit: null,
      connectedCount: 0,
      connected: null,
    },
    {
      name: "generic_bump_map",
      type: "Asset",
      runtimeType: "AssetPropertyReference",
      patchType: null,
      readOnly: true,
      value: null,
      valueDoubles: null,
      valueFeet: null,
      unit: null,
      connectedCount: 1,
      connected: [
        {
          schema: "UnifiedBitmap",
          title: "Unified Bitmap",
          bitmap: "C:\\Program Files\\Common Files\\Autodesk Shared\\Materials\\Textures\\1\\Mats\\stucco.jpg",
          scaleFeet: { x: 3, y: 3 },
          rotation: 0,
        },
      ],
    },
  ],
};

// --- revit_get_material_appearance -------------------------------------------

test("revit_get_material_appearance: snake_case arguments map to camelCase on the wire", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_get_material_appearance",
    arguments: { material_id: 4101 },
  });
  await client.callTool({
    name: "revit_get_material_appearance",
    arguments: { material_name: "Reboco claro" },
  });
  assert.deepEqual(bridge.calls, [
    { endpoint: "/materials/appearance", payload: { materialId: 4101, materialName: undefined } },
    {
      endpoint: "/materials/appearance",
      payload: { materialId: undefined, materialName: "Reboco claro" },
    },
  ]);
  await client.close();
});

test("revit_get_material_appearance: the property list comes back with its types and patchType", async () => {
  const bridge = fakeBridge(async () => GENERIC_APPEARANCE);
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_get_material_appearance",
    arguments: { material_id: 4101 },
  });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.assetSchema, "Generic");
  assert.deepEqual(
    payload.properties.map((row) => [row.name, row.runtimeType, row.patchType]),
    [
      ["generic_diffuse", "AssetPropertyDoubleArray4d", "color"],
      ["generic_glossiness", "AssetPropertyDouble", "double"],
      ["generic_bump_map", "AssetPropertyReference", null],
    ],
  );
  // The colour is reported both ways: 0-255 on the wire, 0-1 as the asset holds it.
  assert.deepEqual(payload.properties[0].value, { r: 236, g: 230, b: 214 });
  assert.equal(payload.properties[0].valueDoubles[0], 0.925);
  await client.close();
});

test("revit_get_material_appearance: a connected bitmap comes back summarised, not as a blob", async () => {
  const bridge = fakeBridge(async () => GENERIC_APPEARANCE);
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_get_material_appearance",
    arguments: { material_id: 4101 },
  });
  const payload = JSON.parse(textOf(result));
  const bump = payload.properties.find((row) => row.name === "generic_bump_map");
  assert.equal(bump.connectedCount, 1);
  assert.equal(bump.connected[0].schema, "UnifiedBitmap");
  assert.match(bump.connected[0].bitmap, /stucco\.jpg$/);
  assert.deepEqual(bump.connected[0].scaleFeet, { x: 3, y: 3 });
  await client.close();
});

test("revit_get_material_appearance: the materials sharing the asset are named, so a shared edit is visible", async () => {
  const bridge = fakeBridge(async () => GENERIC_APPEARANCE);
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_get_material_appearance",
    arguments: { material_id: 4101 },
  });
  assert.deepEqual(JSON.parse(textOf(result)).sharedWithMaterialIds, [4102, 4103]);
  await client.close();
});

test("revit_get_material_appearance: a material with no appearance asset is answered, not an error", async () => {
  const bridge = fakeBridge(async () => ({
    materialId: 4104,
    materialName: "Água",
    colorRgb: { r: 60, g: 110, b: 140 },
    transparency: 80,
    appearanceAssetId: null,
    appearanceAssetName: null,
    assetSchema: null,
    genericAssetAvailable: true,
    sharedWithMaterialIds: [],
    propertyCount: 0,
    properties: [],
    note: 'This material has no AppearanceAssetElement, which is the normal state of a material created through the API: Color and Transparency alone decide how it renders. It does not need a bitmap to gain a look - call /revit-mcp/materials/set-appearance with "createGeneric": true to give it a textureless Generic asset and patch that.',
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_get_material_appearance",
    arguments: { material_name: "Água" },
  });
  const payload = JSON.parse(textOf(result));
  assert.equal(result.isError, undefined);
  assert.equal(payload.appearanceAssetId, null);
  assert.deepEqual(payload.properties, []);
  // The point of the note: null is not "it needs a bitmap".
  assert.match(payload.note, /does not need a bitmap/);
  assert.equal(payload.genericAssetAvailable, true);
  await client.close();
});

test("revit_get_material_appearance: naming neither material is the bridge's call, not a schema error", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      'Revit failed on /materials/appearance: "materialId" is required (or "materialName"). Call /revit-mcp/materials for the materials in this document.',
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_get_material_appearance", arguments: {} });
  assert.equal(bridge.calls.length, 1, "the tool should let the bridge decide");
  assert.match(textOf(result), /"materialId" is required/);
  await client.close();
});

test("revit_get_material_appearance: rejects an empty material_name and a non-integer id", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_get_material_appearance", { material_name: "" });
  await assertRejected(client, bridge, "revit_get_material_appearance", { material_id: 4101.5 });
  await client.close();
});

// --- revit_set_material_appearance -------------------------------------------

test("revit_set_material_appearance: snake_case arguments map to camelCase, and a patch keeps its shape", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_set_material_appearance",
    arguments: {
      material_id: 4101,
      patches: [
        { name: "generic_diffuse", type: "color", value: DARK },
        { name: "generic_glossiness", type: "double", value: 0.15 },
      ],
      duplicate: true,
      source_appearance_asset_id: 212630,
      create_generic: false,
      disconnect_texture: true,
      sync_shading_color: true,
    },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/materials/set-appearance",
    payload: {
      materialId: 4101,
      materialName: undefined,
      patches: [
        { name: "generic_diffuse", type: "color", value: DARK },
        { name: "generic_glossiness", type: "double", value: 0.15 },
      ],
      duplicate: true,
      sourceAppearanceAssetId: 212630,
      createGeneric: false,
      disconnectTexture: true,
      syncShadingColor: true,
    },
  });
  await client.close();
});

test("revit_set_material_appearance: omitted options stay off the wire, so the bridge's own defaults hold", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_set_material_appearance",
    arguments: {
      material_name: "Reboco claro",
      patches: [{ name: "generic_diffuse", type: "color", value: DARK }],
    },
  });
  const payload = bridge.calls[0].payload;
  assert.equal(payload.duplicate, undefined);
  assert.equal(payload.createGeneric, undefined);
  assert.equal(payload.disconnectTexture, undefined);
  assert.equal(payload.syncShadingColor, undefined);
  assert.equal(payload.sourceAppearanceAssetId, undefined);
  await client.close();
});

test("revit_set_material_appearance: every patch type the contract allows goes through", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_set_material_appearance",
    arguments: {
      material_id: 4101,
      patches: [
        { name: "generic_diffuse", type: "color", value: DARK },
        { name: "generic_glossiness", type: "double", value: 0.15 },
        { name: "generic_backface_cull", type: "integer", value: 1 },
        { name: "generic_is_metal", type: "boolean", value: false },
        { name: "unifiedbitmap_Bitmap", type: "string", value: "C:\\tex\\stone.jpg" },
      ],
    },
  });
  assert.deepEqual(
    bridge.calls[0].payload.patches.map((patch) => [patch.type, patch.value]),
    [
      ["color", DARK],
      ["double", 0.15],
      ["integer", 1],
      ["boolean", false],
      ["string", "C:\\tex\\stone.jpg"],
    ],
  );
  await client.close();
});

test("revit_set_material_appearance: an unknown patch type is refused before the bridge is called", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_set_material_appearance", {
    material_id: 4101,
    patches: [{ name: "generic_diffuse", type: "colour", value: DARK }],
  });
  await assertRejected(client, bridge, "revit_set_material_appearance", {
    material_id: 4101,
    patches: [{ name: "generic_diffuse", type: "rgb", value: DARK }],
  });
  await client.close();
});

test("revit_set_material_appearance: a patch missing a name or a value is refused", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_set_material_appearance", {
    material_id: 4101,
    patches: [{ type: "color", value: DARK }],
  });
  await assertRejected(client, bridge, "revit_set_material_appearance", {
    material_id: 4101,
    patches: [{ name: "", type: "color", value: DARK }],
  });
  await assertRejected(client, bridge, "revit_set_material_appearance", {
    material_id: 4101,
    patches: [{ name: "generic_diffuse", type: "color" }],
  });
  await client.close();
});

test("revit_set_material_appearance: a colour channel outside 0-255 is refused", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_set_material_appearance", {
    material_id: 4101,
    patches: [{ name: "generic_diffuse", type: "color", value: { r: 256, g: 0, b: 0 } }],
  });
  await assertRejected(client, bridge, "revit_set_material_appearance", {
    material_id: 4101,
    patches: [{ name: "generic_diffuse", type: "color", value: { r: -1, g: 0, b: 0 } }],
  });
  await assertRejected(client, bridge, "revit_set_material_appearance", {
    material_id: 4101,
    patches: [{ name: "generic_diffuse", type: "color", value: { r: 0, g: 0 } }],
  });
  await client.close();
});

test("revit_set_material_appearance: no patches at all is the bridge's call, not a schema error", async () => {
  const bridge = fakeBridge(async () => ({ appearanceAssetId: 212631 }));
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_set_material_appearance",
    arguments: { material_id: 4104, create_generic: true },
  });
  // create_generic on its own is a real request: give this material an asset.
  assert.equal(bridge.calls.length, 1);
  assert.equal(bridge.calls[0].payload.patches, undefined);
  assert.equal(bridge.calls[0].payload.createGeneric, true);
  await client.close();
});

test("revit_set_material_appearance: the reply carries the readback and the resulting asset id", async () => {
  const bridge = fakeBridge(async () => ({
    materialId: 4101,
    materialName: "Reboco claro",
    previousAppearanceAssetId: 212630,
    previousSharedWithMaterialIds: [4102, 4103],
    assetAction: "duplicated",
    assetReason:
      "the asset was duplicated before it was patched, which is the default, so this edit cannot appear on any other material's surfaces.",
    appearanceAssetId: 212645,
    appearanceAssetName: "Reboco claro 2",
    assetSchema: "Generic",
    applied: [
      {
        name: "generic_diffuse",
        type: "color",
        assetPropertyType: "Double4",
        runtimeType: "AssetPropertyDoubleArray4d",
        disconnectedTexture: false,
        validated: true,
        value: DARK,
      },
    ],
    verified: [
      {
        name: "generic_diffuse",
        runtimeType: "AssetPropertyDoubleArray4d",
        value: DARK,
        valueDoubles: [0.212, 0.188, 0.173, 1],
        connectedCount: 0,
      },
    ],
    sharedWithMaterialIds: [],
    shadingColorRgb: { r: 236, g: 230, b: 214 },
    shadingColorSynced: false,
    useRenderAppearanceForShading: true,
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_material_appearance",
    arguments: {
      material_id: 4101,
      patches: [{ name: "generic_diffuse", type: "color", value: DARK }],
    },
  });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.appearanceAssetId, 212645);
  assert.notEqual(payload.appearanceAssetId, payload.previousAppearanceAssetId);
  assert.deepEqual(payload.verified[0].value, DARK);
  // The proof nothing leaked: the materials that shared the old asset still do,
  // and nobody shares the new one.
  assert.deepEqual(payload.sharedWithMaterialIds, []);
  assert.deepEqual(payload.previousSharedWithMaterialIds, [4102, 4103]);
  await client.close();
});

test("revit_set_material_appearance: a shared asset with duplicate:false comes back refused, with the ids", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      'Revit failed on /materials/set-appearance: "duplicate": false would patch appearance asset 212630 "Reboco claro" in place, and 2 other materials point at it (4102, 4103), so every one of them would be repainted too. Leave "duplicate" at its default true to patch a private copy. Nothing was changed.',
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_material_appearance",
    arguments: {
      material_id: 4101,
      duplicate: false,
      patches: [{ name: "generic_diffuse", type: "color", value: DARK }],
    },
  });
  assert.match(textOf(result), /^Error: Revit failed on \/materials\/set-appearance/);
  assert.match(textOf(result), /4102, 4103/);
  assert.match(textOf(result), /Nothing was changed/);
  await client.close();
});

test("revit_set_material_appearance: an unknown property name fails the call rather than reporting success", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      'Revit failed on /materials/set-appearance: This material\'s appearance asset was built from the "Ceramic" schema, and that schema has no property called "generic_diffuse". Call /revit-mcp/materials/appearance for the 28 property names it really has. Nothing was changed.',
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_material_appearance",
    arguments: {
      material_id: 4101,
      patches: [{ name: "generic_diffuse", type: "color", value: DARK }],
    },
  });
  assert.match(textOf(result), /no property called "generic_diffuse"/);
  assert.match(textOf(result), /Nothing was changed/);
  await client.close();
});

test("revit_set_material_appearance: a connected texture under a colour patch is refused, not silently ignored", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      'Revit failed on /materials/set-appearance: "generic_diffuse" has a texture connected to it, and a connected texture is what renders - a colour written underneath it would change nothing visible. Pass "disconnectTexture": true to remove that texture and let the colour show. Nothing was changed.',
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_material_appearance",
    arguments: {
      material_id: 4101,
      patches: [{ name: "generic_diffuse", type: "color", value: DARK }],
    },
  });
  assert.match(textOf(result), /disconnectTexture/);
  await client.close();
});

test("revit_set_material_appearance: a material with no asset says which two routes exist", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      'Revit failed on /materials/set-appearance: Material "Água" (4104) has no AppearanceAssetElement, so there is no asset to patch. That is a normal state, not a broken one - it renders from Color and Transparency alone - and it does NOT mean the material needs a bitmap. Pass "createGeneric": true to give it a textureless Generic asset and patch that, or "sourceAppearanceAssetId" to start from a copy of a material that already has the look you want.',
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_material_appearance",
    arguments: {
      material_name: "Água",
      patches: [{ name: "generic_transparency", type: "double", value: 0.8 }],
    },
  });
  assert.match(textOf(result), /does NOT mean the material needs a bitmap/);
  assert.match(textOf(result), /"createGeneric": true/);
  assert.match(textOf(result), /"sourceAppearanceAssetId"/);
  await client.close();
});

// --- the tool surface --------------------------------------------------------

test("material appearance: both tools are registered, and nothing about them is required", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  const names = tools.map((t) => t.name);
  assert.ok(names.includes("revit_get_material_appearance"));
  assert.ok(names.includes("revit_set_material_appearance"));

  const schema = (name) => tools.find((t) => t.name === name).inputSchema;
  // Both id forms are optional on their own, and so is every option; the bridge
  // is what checks that a material was named at all.
  assert.equal(schema("revit_get_material_appearance").required, undefined);
  assert.equal(schema("revit_set_material_appearance").required, undefined);
  assert.deepEqual(Object.keys(schema("revit_get_material_appearance").properties), [
    "material_id",
    "material_name",
  ]);
  assert.deepEqual(Object.keys(schema("revit_set_material_appearance").properties), [
    "material_id",
    "material_name",
    "patches",
    "duplicate",
    "source_appearance_asset_id",
    "create_generic",
    "disconnect_texture",
    "sync_shading_color",
  ]);
  await client.close();
});

test("material appearance: the descriptions say the asset is copied and a null asset needs no bitmap", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  const read = tools.find((t) => t.name === "revit_get_material_appearance").description;
  const write = tools.find((t) => t.name === "revit_set_material_appearance").description;

  assert.match(read, /read-only/i);
  assert.match(read, /patchType/);
  assert.match(read, /sharedWithMaterialIds/);
  assert.match(read, /does NOT need a bitmap/);

  assert.match(write, /DUPLICATED/);
  assert.match(write, /SHARED_APPEARANCE_ASSET/);
  assert.match(write, /GENERIC_ASSET_UNAVAILABLE/);
  assert.match(write, /rolls the WHOLE request back/);
  assert.match(write, /0-255/);
  assert.match(write, /One call is one undo step/);
  await client.close();
});

test("material appearance: only these two tools reach the appearance endpoints", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_get_material_appearance",
    arguments: { material_id: 4101 },
  });
  await client.callTool({
    name: "revit_set_material_appearance",
    arguments: {
      material_id: 4101,
      patches: [{ name: "generic_diffuse", type: "color", value: DARK }],
    },
  });
  await client.callTool({ name: "revit_list_materials", arguments: {} });
  assert.deepEqual(
    bridge.calls.map((call) => call.endpoint),
    ["/materials/appearance", "/materials/set-appearance", "/materials"],
  );
  await client.close();
});

// --- a bridge failure is a tool error, not prose --------------------------------

test("material appearance: a bridge failure comes back flagged isError, not as a success", async () => {
  // A rolled-back patch reaches the tool as an exception. Without isError an MCP
  // client reads it as a call that worked and whose text starts with "Error:",
  // which is exactly the report a failed write must never produce.
  for (const [name, args] of [
    ["revit_get_material_appearance", { material_id: 4101 }],
    [
      "revit_set_material_appearance",
      { material_id: 4101, patches: [{ name: "generic_diffuse", type: "color", value: DARK }] },
    ],
  ]) {
    const bridge = fakeBridge(async () => {
      throw new Error(
        'Revit failed on /materials/set-appearance: Revit rolled "Patch appearance asset" back instead of committing it (status RolledBack).',
      );
    });
    const client = await connect(bridge);
    const result = await client.callTool({ name, arguments: args });
    assert.equal(result.isError, true, `${name} reported a bridge failure as a success`);
    assert.match(textOf(result), /^Error: /);
    await client.close();
  }
});

test("revit_get_material_appearance: an unedited library preset reports its schema, not zero properties", async () => {
  // Size 0 off the material's own asset is the normal state of a preset nobody
  // has touched. Reporting it as "no properties" reads as "nothing to patch".
  const bridge = fakeBridge(async () => ({
    materialId: 4101,
    materialName: "Água",
    appearanceAssetId: 4102,
    assetSchema: "Water",
    propertySource: "libraryPreset",
    presetName: "Water",
    propertyCount: 2,
    properties: [
      { name: "water_type", type: "integer", value: 0 },
      { name: "water_tint_enable", type: "boolean", value: false },
    ],
    note: 'This material uses Revit\'s "Water" library preset unedited, so its own asset carries no properties and these were read off the shipped asset of that schema.',
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_get_material_appearance",
    arguments: { material_id: 4101 },
  });
  const payload = JSON.parse(textOf(result));
  assert.equal(result.isError, undefined);
  assert.equal(payload.propertySource, "libraryPreset");
  assert.equal(payload.presetName, "Water");
  assert.equal(payload.propertyCount, 2);
  assert.equal(payload.properties[0].name, "water_type");
  await client.close();
});
