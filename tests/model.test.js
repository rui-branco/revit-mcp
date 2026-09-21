import { test } from "node:test";
import assert from "node:assert/strict";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { createRevitServer } from "../index.js";

// Same setup as tools.test.js: a real MCP client over an in-memory transport,
// so the schemas doing the validating are the SDK's, with the bridge faked —
// nothing here starts Revit or opens a socket.

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

const SQUARE = [
  { x: 0, y: 0 },
  { x: 10, y: 0 },
  { x: 10, y: 10 },
  { x: 0, y: 10 },
];

// --- revit_create_toposolid --------------------------------------------------

test("revit_create_toposolid: forwards points, type_name mapped to typeName", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_toposolid",
    arguments: {
      points: [
        { x: 0, y: 0, z: 0 },
        { x: 100, y: 0, z: 2 },
        { x: 100, y: 100, z: 4.5 },
      ],
      type_name: "Toposolid 1'-0\"",
      level: "Level 1",
    },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/toposolid/create",
    payload: {
      points: [
        { x: 0, y: 0, z: 0 },
        { x: 100, y: 0, z: 2 },
        { x: 100, y: 100, z: 4.5 },
      ],
      typeName: "Toposolid 1'-0\"",
      level: "Level 1",
    },
  });
  await client.close();
});

test("revit_create_toposolid: an omitted type_name and level are left for the bridge to resolve", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_toposolid",
    arguments: {
      points: [
        { x: 0, y: 0, z: 0 },
        { x: 10, y: 0, z: 0 },
        { x: 10, y: 10, z: 1 },
      ],
    },
  });
  assert.equal(bridge.calls[0].payload.typeName, undefined);
  assert.equal(bridge.calls[0].payload.level, undefined);
  await client.close();
});

test("revit_create_toposolid: rejects fewer than 3 points and a point without z", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_create_toposolid", {});
  await assertRejected(client, bridge, "revit_create_toposolid", { points: [] });
  await assertRejected(client, bridge, "revit_create_toposolid", {
    points: [
      { x: 0, y: 0, z: 0 },
      { x: 10, y: 0, z: 0 },
    ],
  });
  // z is the surface elevation here, so it is not optional.
  await assertRejected(client, bridge, "revit_create_toposolid", {
    points: [
      { x: 0, y: 0 },
      { x: 10, y: 0 },
      { x: 10, y: 10 },
    ],
  });
  await client.close();
});

test("revit_create_toposolid: the response says which element kind it used", async () => {
  const bridge = fakeBridge(async () => ({
    id: 987654,
    type: "Toposolid",
    typeName: "Toposolid 1'-0\"",
    level: "Level 1",
    points: 3,
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_toposolid",
    arguments: {
      points: [
        { x: 0, y: 0, z: 0 },
        { x: 10, y: 0, z: 0 },
        { x: 10, y: 10, z: 1 },
      ],
    },
  });
  assert.equal(result.isError, undefined);
  assert.equal(JSON.parse(textOf(result)).type, "Toposolid");
  await client.close();
});

// --- revit_flatten_toposolid -------------------------------------------------

test("revit_flatten_toposolid: forwards the ring and elevation, toposolid_id mapped to toposolidId", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_flatten_toposolid",
    arguments: { points: SQUARE, elevation: 6.5, toposolid_id: 32145 },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/toposolid/flatten",
    payload: { points: SQUARE, elevation: 6.5, toposolidId: 32145 },
  });
  await client.close();
});

test("revit_flatten_toposolid: an omitted toposolid_id leaves the bridge to find the only one", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_flatten_toposolid",
    arguments: { points: SQUARE, elevation: 0 },
  });
  assert.equal(bridge.calls[0].payload.toposolidId, undefined);
  await client.close();
});

test("revit_flatten_toposolid: rejects a ring with fewer than 3 points and a missing elevation", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_flatten_toposolid", {});
  await assertRejected(client, bridge, "revit_flatten_toposolid", { points: SQUARE });
  await assertRejected(client, bridge, "revit_flatten_toposolid", { points: [], elevation: 0 });
  await assertRejected(client, bridge, "revit_flatten_toposolid", {
    points: SQUARE.slice(0, 2),
    elevation: 0,
  });
  await assertRejected(client, bridge, "revit_flatten_toposolid", {
    points: [{ x: 0 }, { x: 1, y: 0 }, { x: 1, y: 1 }],
    elevation: 0,
  });
  await client.close();
});

test("revit_flatten_toposolid: residual comes through, so the caller can check it worked", async () => {
  const bridge = fakeBridge(async () => ({
    id: 32145,
    elevation: 6.5,
    added: 4,
    creases: 4,
    flattened: 11,
    offsetMode: "absolute",
    residual: 0,
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_flatten_toposolid",
    arguments: { points: SQUARE, elevation: 6.5 },
  });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.residual, 0);
  assert.equal(payload.flattened, 11);
  await client.close();
});

test("revit_flatten_toposolid: the description says residual is what you check", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  const description = tools.find((t) => t.name === "revit_flatten_toposolid").description;
  assert.match(description, /residual/);
  assert.match(description, /overlap/i);
  await client.close();
});

// --- revit_create_floor ------------------------------------------------------

test("revit_create_floor: forwards level and boundary, type_name mapped to typeName", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_floor",
    arguments: {
      level: "Level 1",
      boundary: SQUARE,
      type_name: "Generic - 300mm",
      structural: true,
    },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/floors/create",
    payload: {
      level: "Level 1",
      boundary: SQUARE,
      typeName: "Generic - 300mm",
      structural: true,
      offset: undefined,
    },
  });
  await client.close();
});

test("revit_create_floor: offset goes through as the height offset from the level", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_floor",
    arguments: { level: "Level 1", boundary: SQUARE, offset: -0.5 },
  });
  assert.equal(bridge.calls[0].payload.offset, -0.5);
  await client.close();
});

test("revit_create_floor: an omitted offset is left for the bridge's own default of 0", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_floor",
    arguments: { level: "Level 1", boundary: SQUARE },
  });
  assert.equal(bridge.calls[0].payload.offset, undefined);
  await client.close();
});

test("revit_create_floor: the offset read back off the floor comes through", async () => {
  const bridge = fakeBridge(async () => ({
    id: 771,
    level: "Level 1",
    typeName: "Generic - 300mm",
    structural: false,
    offset: 0.25,
    area: 100,
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_floor",
    arguments: { level: "Level 1", boundary: SQUARE, offset: 0.25 },
  });
  assert.equal(JSON.parse(textOf(result)).offset, 0.25);
  await client.close();
});

test("revit_create_floor: rejects a boundary with fewer than 3 points and a missing level", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_create_floor", {});
  await assertRejected(client, bridge, "revit_create_floor", { level: "Level 1" });
  await assertRejected(client, bridge, "revit_create_floor", { level: "Level 1", boundary: [] });
  await assertRejected(client, bridge, "revit_create_floor", {
    level: "Level 1",
    boundary: [
      { x: 0, y: 0 },
      { x: 10, y: 0 },
    ],
  });
  await assertRejected(client, bridge, "revit_create_floor", { level: "", boundary: SQUARE });
  await assertRejected(client, bridge, "revit_create_floor", {
    level: "Level 1",
    boundary: [{ x: 0 }, { x: 10, y: 0 }, { x: 10, y: 10 }],
  });
  await client.close();
});

test("revit_create_floor: an unknown floor type is surfaced, not swallowed", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      'Revit failed on /floors/create: Unknown floor type "Paving". Floor types in this document: Generic - 300mm.',
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_floor",
    arguments: { level: "Level 1", boundary: SQUARE, type_name: "Paving" },
  });
  assert.match(textOf(result), /^Error: Revit failed on \/floors\/create/);
  assert.match(textOf(result), /Floor types in this document/);
  await client.close();
});

// --- revit_load_families -----------------------------------------------------

test("revit_load_families: the whole batch of paths goes in one call", async () => {
  const bridge = fakeBridge(async () => ({ families: [], warnings: [] }));
  const client = await connect(bridge);
  const paths = [
    "C:\\ProgramData\\Autodesk\\RVT 2026\\Libraries\\English\\US\\Planting\\M_RPC Tree - Deciduous.rfa",
    "C:\\ProgramData\\Autodesk\\RVT 2026\\Libraries\\English\\US\\Site\\Accessories\\M_Park Bench.rfa",
  ];
  await client.callTool({ name: "revit_load_families", arguments: { paths } });
  assert.equal(bridge.calls.length, 1);
  assert.deepEqual(bridge.calls[0], { endpoint: "/families/load", payload: { paths } });
  await client.close();
});

test("revit_load_families: a single path is sent as path, not wrapped in paths", async () => {
  const bridge = fakeBridge(async () => ({ families: [], warnings: [] }));
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_load_families",
    arguments: { path: "C:\\families\\M_RPC Shrub.rfa" },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/families/load",
    payload: { path: "C:\\families\\M_RPC Shrub.rfa" },
  });
  await client.close();
});

test("revit_load_families: neither paths nor path is an error that never reaches the bridge", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_load_families", arguments: {} });
  assert.match(textOf(result), /pass 'paths'/);
  assert.equal(bridge.calls.length, 0);
  await client.close();
});

test("revit_load_families: rejects an empty array and an empty path string", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_load_families", { paths: [] });
  await assertRejected(client, bridge, "revit_load_families", { paths: [""] });
  await assertRejected(client, bridge, "revit_load_families", { path: "" });
  await client.close();
});

test("revit_load_families: per-file rows come back as data, FILE_NOT_FOUND included", async () => {
  const bridge = fakeBridge(async () => ({
    families: [
      {
        path: "C:\\families\\M_Park Bench.rfa",
        familyName: "M_Park Bench",
        loaded: true,
        alreadyLoaded: false,
        symbols: [
          { id: 215490, typeName: "1800mm" },
          { id: 215492, typeName: "2400mm" },
        ],
      },
      {
        path: "C:\\families\\Nope.rfa",
        familyName: null,
        loaded: false,
        alreadyLoaded: false,
        symbols: [],
        code: "FILE_NOT_FOUND",
        reason: "No file at that path.",
      },
    ],
    warnings: [],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_load_families",
    arguments: { paths: ["C:\\families\\M_Park Bench.rfa", "C:\\families\\Nope.rfa"] },
  });
  assert.equal(result.isError, undefined);
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.families[0].symbols[0].id, 215490);
  assert.equal(payload.families[1].code, "FILE_NOT_FOUND");
  await client.close();
});

// --- revit_list_family_symbols -----------------------------------------------

test("revit_list_family_symbols: hits its endpoint, with and without a category", async () => {
  const bridge = fakeBridge(async () => []);
  const client = await connect(bridge);
  await client.callTool({ name: "revit_list_family_symbols", arguments: {} });
  await client.callTool({
    name: "revit_list_family_symbols",
    arguments: { category: "Planting" },
  });
  assert.deepEqual(bridge.calls, [
    { endpoint: "/families/symbols", payload: { category: undefined, familyName: undefined } },
    { endpoint: "/families/symbols", payload: { category: "Planting", familyName: undefined } },
  ]);
  await client.close();
});

test("revit_list_family_symbols: family_name maps to familyName and combines with category", async () => {
  const bridge = fakeBridge(async () => []);
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_list_family_symbols",
    arguments: { family_name: "M_RPC Tree - Deciduous" },
  });
  await client.callTool({
    name: "revit_list_family_symbols",
    arguments: { category: "Planting", family_name: "M_RPC Shrub" },
  });
  assert.deepEqual(bridge.calls, [
    {
      endpoint: "/families/symbols",
      payload: { category: undefined, familyName: "M_RPC Tree - Deciduous" },
    },
    {
      endpoint: "/families/symbols",
      payload: { category: "Planting", familyName: "M_RPC Shrub" },
    },
  ]);
  await client.close();
});

test("revit_list_family_symbols: rejects an empty family_name", async () => {
  const bridge = fakeBridge(async () => []);
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_list_family_symbols", { family_name: "" });
  await client.close();
});

test("revit_list_family_symbols: no loaded families is an empty list, not an error", async () => {
  const bridge = fakeBridge(async () => []);
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_list_family_symbols", arguments: {} });
  assert.equal(result.isError, undefined);
  assert.equal(textOf(result), "[]");
  await client.close();
});

// --- revit_place_families ----------------------------------------------------

test("revit_place_families: symbol_id maps to symbolId and the whole batch is one call", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  const points = Array.from({ length: 40 }, (_, i) => ({ x: i * 10, y: 0 }));
  await client.callTool({
    name: "revit_place_families",
    arguments: { symbol_id: 424242, level: "Level 1", points, rotation: 1.5707963267948966 },
  });
  assert.equal(bridge.calls.length, 1);
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/families/place",
    payload: {
      symbolId: 424242,
      level: "Level 1",
      points,
      z: undefined,
      rotation: 1.5707963267948966,
    },
  });
  await client.close();
});

test("revit_place_families: level is optional and z is forwarded as the batch offset", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_place_families",
    arguments: { symbol_id: 7, points: [{ x: 1, y: 2, z: -1.5 }], z: 2.5 },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/families/place",
    payload: {
      symbolId: 7,
      level: undefined,
      points: [{ x: 1, y: 2, z: -1.5 }],
      z: 2.5,
      rotation: undefined,
    },
  });
  await client.close();
});

test("revit_place_families: placedZ read back off each instance comes through", async () => {
  const bridge = fakeBridge(async () => ({
    symbolId: 7,
    familyName: "M_RPC Tree - Deciduous",
    level: "Cota do jardim",
    levelElevation: -1.476378,
    placed: [{ id: 216013, x: 10, y: 12, z: -1.476378, placedZ: -1.476378 }],
    failed: [],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_place_families",
    arguments: { symbol_id: 7, points: [{ x: 10, y: 12, z: -1.476378 }] },
  });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.placed[0].placedZ, -1.476378);
  assert.equal(payload.levelElevation, -1.476378);
  await client.close();
});

test("revit_place_families: rejects an empty batch, a non-integer symbol_id and a point without x", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_place_families", {});
  await assertRejected(client, bridge, "revit_place_families", {
    symbol_id: 1,
    level: "Level 1",
    points: [],
  });
  await assertRejected(client, bridge, "revit_place_families", {
    symbol_id: 1.5,
    level: "Level 1",
    points: [{ x: 0, y: 0 }],
  });
  await assertRejected(client, bridge, "revit_place_families", {
    symbol_id: 1,
    level: "",
    points: [{ x: 0, y: 0 }],
  });
  await assertRejected(client, bridge, "revit_place_families", {
    symbol_id: 1,
    level: "Level 1",
    points: [{ y: 0 }],
  });
  await client.close();
});

test("revit_place_families: placed and failed come back as data, not an error", async () => {
  const bridge = fakeBridge(async () => ({
    symbolId: 424242,
    level: "Level 1",
    placed: [{ id: 1, x: 0, y: 0 }],
    failed: [{ x: 10, y: 0, reason: "Could not create the family instance." }],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_place_families",
    arguments: {
      symbol_id: 424242,
      level: "Level 1",
      points: [
        { x: 0, y: 0 },
        { x: 10, y: 0 },
      ],
    },
  });
  assert.equal(result.isError, undefined);
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.placed.length, 1);
  assert.equal(payload.failed[0].reason, "Could not create the family instance.");
  await client.close();
});

// --- revit_place_openings ----------------------------------------------------

test("revit_place_openings: symbol_id, host_wall_id and sill_height map to camelCase in one call", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  const points = [
    { x: 40, y: 12, z: 0 },
    { x: 50, y: 12, z: 0 },
  ];
  await client.callTool({
    name: "revit_place_openings",
    arguments: {
      symbol_id: 231961,
      points,
      host_wall_id: 210828,
      level: "Level 1",
      sill_height: 3,
    },
  });
  assert.equal(bridge.calls.length, 1);
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/openings/place",
    payload: {
      symbolId: 231961,
      points,
      hostWallId: 210828,
      level: "Level 1",
      sillHeight: 3,
    },
  });
  await client.close();
});

test("revit_place_openings: host_wall_id, level and sill_height are all optional", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_place_openings",
    arguments: { symbol_id: 226962, points: [{ x: 40, y: 12 }] },
  });
  assert.deepEqual(bridge.calls[0].payload, {
    symbolId: 226962,
    points: [{ x: 40, y: 12 }],
    hostWallId: undefined,
    level: undefined,
    sillHeight: undefined,
  });
  await client.close();
});

test("revit_place_openings: the host wall read back off each instance comes through", async () => {
  const bridge = fakeBridge(async () => ({
    symbolId: 231961,
    familyName: "M_Window-Fixed",
    typeName: "900 x 1500mm",
    category: "Windows",
    level: "Level 1",
    levelElevation: 0,
    placed: [
      {
        id: 232004,
        x: 50,
        y: 12,
        z: 0,
        hostWallId: 210828,
        hostWallType: "Parede exterior moradia",
        sillHeight: 3,
        sillHeightOn: "instance",
        placedZ: 3,
      },
    ],
    failed: [],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_place_openings",
    arguments: { symbol_id: 231961, points: [{ x: 50, y: 12, z: 0 }] },
  });
  const payload = JSON.parse(textOf(result));
  // The host is the whole question: an opening with a null host cuts nothing.
  assert.equal(payload.placed[0].hostWallId, 210828);
  assert.equal(payload.placed[0].sillHeightOn, "instance");
  await client.close();
});

test("revit_place_openings: NO_HOST_WALL is per point and the rest of the batch still lands", async () => {
  const bridge = fakeBridge(async () => ({
    symbolId: 231961,
    familyName: "M_Window-Fixed",
    typeName: "900 x 1500mm",
    category: "Windows",
    level: "Level 1",
    levelElevation: 0,
    placed: [{ id: 232013, x: 58, y: 12, z: 0, hostWallId: 210828, sillHeight: 4.5, placedZ: 4.5 }],
    failed: [
      {
        x: 300,
        y: 300,
        z: 0,
        code: "NO_HOST_WALL",
        reason: "No wall within 3 feet of this point.",
      },
    ],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_place_openings",
    arguments: {
      symbol_id: 231961,
      points: [
        { x: 300, y: 300, z: 0 },
        { x: 58, y: 12, z: 0 },
      ],
      sill_height: 4.5,
    },
  });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.placed.length, 1);
  assert.equal(payload.failed[0].code, "NO_HOST_WALL");
  await client.close();
});

test("revit_place_openings: rejects an empty batch, a non-integer id and a point without y", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_place_openings", {});
  await assertRejected(client, bridge, "revit_place_openings", { symbol_id: 1, points: [] });
  await assertRejected(client, bridge, "revit_place_openings", {
    symbol_id: 1.5,
    points: [{ x: 0, y: 0 }],
  });
  await assertRejected(client, bridge, "revit_place_openings", {
    symbol_id: 1,
    points: [{ x: 0 }],
  });
  await assertRejected(client, bridge, "revit_place_openings", {
    symbol_id: 1,
    points: [{ x: 0, y: 0 }],
    host_wall_id: 1.5,
  });
  await client.close();
});
