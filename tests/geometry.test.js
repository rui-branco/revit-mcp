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

const CYLINDER = { kind: "cylinder", base: { x: 0, y: 0, z: 0 }, radius: 0.5, height: 8 };
const SPHERE = { kind: "sphere", center: { x: 0, y: 0, z: 14 }, radius: 6 };

// --- revit_create_directshape: categories ------------------------------------

test("revit_create_directshape: the category goes through as given, without OST_", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  for (const category of ["Planting", "LightingFixtures", "Furniture", "Site", "OST_Walls"]) {
    await client.callTool({
      name: "revit_create_directshape",
      arguments: { category, shapes: [CYLINDER] },
    });
  }
  assert.deepEqual(
    bridge.calls.map((c) => c.payload.category),
    ["Planting", "LightingFixtures", "Furniture", "Site", "OST_Walls"],
  );
  assert.deepEqual(
    bridge.calls.map((c) => c.endpoint),
    Array(5).fill("/directshape/create"),
  );
  await client.close();
});

test("revit_create_directshape: an unknown category is the bridge's error, surfaced verbatim", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      'Revit failed on /directshape/create: Unknown category "Trees". Pass a BuiltInCategory name without its OST_ prefix, for example: Planting, LightingFixtures, Furniture, Site, Walls, GenericModel.',
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_directshape",
    arguments: { category: "Trees", shapes: [CYLINDER] },
  });
  assert.match(textOf(result), /^Error: Revit failed on \/directshape\/create/);
  assert.match(textOf(result), /without its OST_ prefix/);
  await client.close();
});

test("revit_create_directshape: rejects an empty category and an empty shape list", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_create_directshape", {});
  await assertRejected(client, bridge, "revit_create_directshape", { category: "Planting" });
  await assertRejected(client, bridge, "revit_create_directshape", {
    category: "Planting",
    shapes: [],
  });
  await assertRejected(client, bridge, "revit_create_directshape", {
    category: "",
    shapes: [CYLINDER],
  });
  await client.close();
});

// --- revit_create_directshape: each primitive --------------------------------

test("revit_create_directshape: every primitive kind is forwarded unchanged", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  const shapes = [
    CYLINDER,
    { kind: "box", min: { x: 0, y: 0, z: 0 }, max: { x: 10, y: 4, z: 0.5 } },
    SPHERE,
    { kind: "cone", base: { x: 5, y: 5, z: 0 }, radius: 2, height: 9 },
  ];
  await client.callTool({
    name: "revit_create_directshape",
    arguments: { category: "Site", name: "Bollard", shapes },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/directshape/create",
    // comments / mark / typeName / materialId / materialName ride along on every
    // call, undefined when not asked for.
    payload: {
      category: "Site",
      name: "Bollard",
      typeName: undefined,
      materialId: undefined,
      materialName: undefined,
      comments: undefined,
      mark: undefined,
      shapes,
    },
  });
  await client.close();
});

test("revit_create_directshape: extrusion base_z maps to baseZ on the wire", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_directshape",
    arguments: {
      category: "Site",
      shapes: [
        {
          kind: "extrusion",
          profile: [
            { x: 0, y: 0 },
            { x: 10, y: 0 },
            { x: 10, y: 10 },
          ],
          base_z: 2.5,
          height: 1,
        },
      ],
    },
  });
  const shape = bridge.calls[0].payload.shapes[0];
  assert.equal(shape.baseZ, 2.5);
  assert.equal("base_z" in shape, false);
  await client.close();
});

test("revit_create_directshape: an omitted base_z is left for the bridge to default", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_directshape",
    arguments: {
      category: "Site",
      shapes: [
        {
          kind: "extrusion",
          profile: [
            { x: 0, y: 0 },
            { x: 10, y: 0 },
            { x: 10, y: 10 },
          ],
          height: 1,
        },
      ],
    },
  });
  assert.equal(bridge.calls[0].payload.shapes[0].baseZ, undefined);
  await client.close();
});

test("revit_create_directshape: rejects an unknown kind and a kind's missing or wrong arguments", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  const bad = [
    { kind: "pyramid", base: { x: 0, y: 0, z: 0 }, radius: 1, height: 1 },
    { kind: "cylinder", base: { x: 0, y: 0, z: 0 }, radius: 0, height: 8 },
    { kind: "cylinder", base: { x: 0, y: 0, z: 0 }, radius: 1, height: -8 },
    { kind: "cylinder", base: { x: 0, y: 0 }, radius: 1, height: 8 },
    { kind: "cylinder", radius: 1, height: 8 },
    { kind: "box", min: { x: 0, y: 0, z: 0 } },
    { kind: "sphere", center: { x: 0, y: 0, z: 0 } },
    { kind: "sphere", center: { x: 0, y: 0, z: 0 }, radius: -6 },
    { kind: "cone", base: { x: 0, y: 0, z: 0 }, radius: 2 },
    { kind: "extrusion", profile: [{ x: 0, y: 0 }], height: 1 },
    { kind: "extrusion", profile: [{ x: 0, y: 0 }, { x: 1, y: 0 }, { x: 1, y: 1 }] },
  ];
  for (const shape of bad) {
    await assertRejected(client, bridge, "revit_create_directshape", {
      category: "Site",
      shapes: [shape],
    });
  }
  await client.close();
});

// --- revit_create_directshape: the group form --------------------------------

test("revit_create_directshape: a group of parts is forwarded as one entry", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_directshape",
    arguments: {
      category: "Planting",
      shapes: [{ kind: "group", name: "Olive", parts: [CYLINDER, SPHERE] }],
    },
  });
  assert.equal(bridge.calls[0].payload.shapes.length, 1);
  assert.deepEqual(bridge.calls[0].payload.shapes[0], {
    kind: "group",
    name: "Olive",
    parts: [CYLINDER, SPHERE],
  });
  await client.close();
});

test("revit_create_directshape: base_z inside a group's parts is mapped too", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_directshape",
    arguments: {
      category: "Planting",
      shapes: [
        {
          kind: "group",
          parts: [
            {
              kind: "extrusion",
              profile: [
                { x: 0, y: 0 },
                { x: 2, y: 0 },
                { x: 2, y: 2 },
              ],
              base_z: 1,
              height: 3,
            },
          ],
        },
      ],
    },
  });
  const part = bridge.calls[0].payload.shapes[0].parts[0];
  assert.equal(part.baseZ, 1);
  assert.equal("base_z" in part, false);
  await client.close();
});

test("revit_create_directshape: rejects an empty group and a group nested inside a group", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_create_directshape", {
    category: "Planting",
    shapes: [{ kind: "group", parts: [] }],
  });
  await assertRejected(client, bridge, "revit_create_directshape", {
    category: "Planting",
    shapes: [{ kind: "group", parts: [{ kind: "group", parts: [CYLINDER] }] }],
  });
  await client.close();
});

test("revit_create_directshape: the whole batch goes in one call, so it is one undo step", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  const shapes = Array.from({ length: 50 }, (_, i) => ({
    kind: "group",
    parts: [
      { ...CYLINDER, base: { x: i * 20, y: 0, z: 0 } },
      { ...SPHERE, center: { x: i * 20, y: 0, z: 14 } },
    ],
  }));
  await client.callTool({
    name: "revit_create_directshape",
    arguments: { category: "Planting", shapes },
  });
  assert.equal(bridge.calls.length, 1);
  assert.equal(bridge.calls[0].payload.shapes.length, 50);
  await client.close();
});

test("revit_create_directshape: a refused shape comes back with its index, not as an error", async () => {
  const bridge = fakeBridge(async () => ({
    created: [{ id: 1001, category: "Planting", name: "Tree" }],
    failed: [{ index: 1, reason: '"shapes[1].radius" must be greater than zero (decimal feet).' }],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_directshape",
    arguments: { category: "Planting", shapes: [CYLINDER, SPHERE] },
  });
  assert.equal(result.isError, undefined);
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.created.length, 1);
  assert.equal(payload.failed[0].index, 1);
  await client.close();
});

test("revit_create_directshape: the description says why this exists at all", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  const description = tools.find((t) => t.name === "revit_create_directshape").description;
  assert.match(description, /no family/i);
  assert.match(description, /optional Autodesk download/);
  assert.match(description, /feet \(Revit internal units\)/);
  await client.close();
});

// --- revit_place_planting ----------------------------------------------------

test("revit_place_planting: snake_case sizes map to camelCase on the wire", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_place_planting",
    arguments: {
      points: [
        { x: 0, y: 0 },
        { x: 20, y: 0, z: 1.5 },
      ],
      trunk_height: 10,
      trunk_radius: 0.75,
      crown_radius: 7,
      name: "Olea europaea",
    },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/planting/place",
    payload: {
      points: [
        { x: 0, y: 0 },
        { x: 20, y: 0, z: 1.5 },
      ],
      trunkHeight: 10,
      trunkRadius: 0.75,
      crownRadius: 7,
      name: "Olea europaea",
      typeName: undefined,
      materialId: undefined,
      materialName: undefined,
      comments: undefined,
      mark: undefined,
    },
  });
  await client.close();
});

test("revit_place_planting: omitted sizes are left for the bridge's own defaults", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_place_planting",
    arguments: { points: [{ x: 0, y: 0 }] },
  });
  const payload = bridge.calls[0].payload;
  assert.equal(payload.trunkHeight, undefined);
  assert.equal(payload.trunkRadius, undefined);
  assert.equal(payload.crownRadius, undefined);
  assert.equal(payload.name, undefined);
  await client.close();
});

test("revit_place_planting: the documented defaults are 8 / 0.5 / 6 feet", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  const description = tools.find((t) => t.name === "revit_place_planting").description;
  assert.match(description, /trunk_height 8/);
  assert.match(description, /trunk_radius 0\.5/);
  assert.match(description, /crown_radius 6/);
  await client.close();
});

test("revit_place_planting: rejects an empty batch and a non-positive size", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_place_planting", {});
  await assertRejected(client, bridge, "revit_place_planting", { points: [] });
  await assertRejected(client, bridge, "revit_place_planting", { points: [{ x: 0 }] });
  await assertRejected(client, bridge, "revit_place_planting", {
    points: [{ x: 0, y: 0 }],
    trunk_height: 0,
  });
  await assertRejected(client, bridge, "revit_place_planting", {
    points: [{ x: 0, y: 0 }],
    crown_radius: -6,
  });
  await client.close();
});

test("revit_place_planting: the whole grove goes in one call, so it is one undo step", async () => {
  const bridge = fakeBridge(async () => ({
    created: Array.from({ length: 120 }, (_, i) => ({
      id: 5000 + i,
      category: "Planting",
      name: "Tree",
      x: i * 15,
      y: 0,
    })),
    failed: [],
  }));
  const client = await connect(bridge);
  const points = Array.from({ length: 120 }, (_, i) => ({ x: i * 15, y: 0 }));
  const result = await client.callTool({
    name: "revit_place_planting",
    arguments: { points },
  });
  assert.equal(bridge.calls.length, 1);
  assert.equal(bridge.calls[0].payload.points.length, 120);
  assert.equal(JSON.parse(textOf(result)).created.length, 120);
  await client.close();
});

// --- comments and mark -------------------------------------------------------

test("revit_create_directshape: per-shape comments and mark reach the wire", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_directshape",
    arguments: {
      category: "Planting",
      comments: "Arbusto",
      mark: "AR",
      shapes: [
        { ...SPHERE, name: "Lavandula", comments: "Lavandula angustifolia", mark: "LA-01" },
        SPHERE,
      ],
    },
  });
  const payload = bridge.calls[0].payload;
  assert.equal(payload.comments, "Arbusto");
  assert.equal(payload.mark, "AR");
  assert.equal(payload.shapes[0].comments, "Lavandula angustifolia");
  assert.equal(payload.shapes[0].mark, "LA-01");
  // The second shape carries neither, so the top-level pair is what it gets.
  assert.equal(payload.shapes[1].comments, undefined);
  await client.close();
});

test("revit_create_directshape: a group entry carries its own comments and mark too", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_directshape",
    arguments: {
      category: "Planting",
      shapes: [
        {
          kind: "group",
          name: "Quercus suber",
          comments: "Sobreiro existente",
          mark: "Q-07",
          parts: [CYLINDER, SPHERE],
        },
      ],
    },
  });
  const entry = bridge.calls[0].payload.shapes[0];
  assert.equal(entry.comments, "Sobreiro existente");
  assert.equal(entry.mark, "Q-07");
  assert.equal(entry.parts.length, 2);
  await client.close();
});

test("revit_place_planting: each point can carry its own species, so a schedule breaks down", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_place_planting",
    arguments: {
      points: [
        { x: 0, y: 0, name: "Olea europaea", comments: "Oliveira", mark: "OL-01" },
        { x: 20, y: 0, name: "Quercus suber", comments: "Sobreiro", mark: "QS-01" },
      ],
      comments: "Plantação nova",
    },
  });
  const payload = bridge.calls[0].payload;
  assert.equal(payload.comments, "Plantação nova");
  assert.deepEqual(
    payload.points.map((point) => point.mark),
    ["OL-01", "QS-01"],
  );
  await client.close();
});

test("revit_create_directshape: comments and mark must be non-empty when given", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_create_directshape", {
    category: "Planting",
    comments: "",
    shapes: [SPHERE],
  });
  await assertRejected(client, bridge, "revit_create_directshape", {
    category: "Planting",
    shapes: [{ ...SPHERE, mark: "" }],
  });
  await client.close();
});

// --- revit_create_pipes ------------------------------------------------------

test("revit_create_pipes: runs go through as given, with category and mark alongside", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  const runs = [
    {
      points: [
        { x: 0, y: 0 },
        { x: 40, y: 0 },
        { x: 40, y: 25, z: -1 },
      ],
      radius: 0.1,
      mark: "REGA-01",
    },
  ];
  await client.callTool({
    name: "revit_create_pipes",
    arguments: { runs, comments: "Rega gota a gota" },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/pipes/create",
    payload: {
      runs,
      category: undefined,
      name: undefined,
      typeName: undefined,
      materialId: undefined,
      materialName: undefined,
      comments: "Rega gota a gota",
      mark: undefined,
    },
  });
  await client.close();
});

test("revit_create_pipes: rejects an empty batch, a one-point run and a non-positive radius", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_create_pipes", {});
  await assertRejected(client, bridge, "revit_create_pipes", { runs: [] });
  await assertRejected(client, bridge, "revit_create_pipes", {
    runs: [{ points: [{ x: 0, y: 0 }] }],
  });
  await assertRejected(client, bridge, "revit_create_pipes", {
    runs: [
      {
        points: [
          { x: 0, y: 0 },
          { x: 1, y: 0 },
        ],
        radius: 0,
      },
    ],
  });
  await client.close();
});

test("revit_create_pipes: the category the bridge settled on comes back per element", async () => {
  const bridge = fakeBridge(async () => ({
    created: [{ id: 8001, category: "GenericModel", name: "Pipe", mark: "REGA-01" }],
    failed: [],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_pipes",
    arguments: {
      runs: [
        {
          points: [
            { x: 0, y: 0 },
            { x: 40, y: 0 },
          ],
        },
      ],
    },
  });
  assert.equal(JSON.parse(textOf(result)).created[0].category, "GenericModel");
  await client.close();
});

test("revit_create_pipes: the documented default radius is 0.08 feet", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  const description = tools.find((t) => t.name === "revit_create_pipes").description;
  assert.match(description, /0\.08/);
  assert.match(description, /PipeCurves/);
  await client.close();
});

// --- revit_place_sprinklers --------------------------------------------------

test("revit_place_sprinklers: points and sizes go through unchanged", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  const points = [
    { x: 0, y: 0, mark: "ASP-01" },
    { x: 15, y: 0, mark: "ASP-02" },
  ];
  await client.callTool({
    name: "revit_place_sprinklers",
    arguments: { points, radius: 0.2, height: 0.6, comments: "Aspersor sectorial" },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/sprinklers/place",
    payload: {
      points,
      radius: 0.2,
      height: 0.6,
      name: undefined,
      typeName: undefined,
      materialId: undefined,
      materialName: undefined,
      comments: "Aspersor sectorial",
      mark: undefined,
    },
  });
  await client.close();
});

test("revit_place_sprinklers: omitted sizes are left for the bridge's own defaults", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_place_sprinklers",
    arguments: { points: [{ x: 0, y: 0 }] },
  });
  assert.equal(bridge.calls[0].payload.radius, undefined);
  assert.equal(bridge.calls[0].payload.height, undefined);
  await client.close();
});

test("revit_place_sprinklers: rejects an empty batch and a non-positive size", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_place_sprinklers", {});
  await assertRejected(client, bridge, "revit_place_sprinklers", { points: [] });
  await assertRejected(client, bridge, "revit_place_sprinklers", {
    points: [{ x: 0, y: 0 }],
    radius: 0,
  });
  await assertRejected(client, bridge, "revit_place_sprinklers", {
    points: [{ x: 0, y: 0 }],
    height: -1,
  });
  await client.close();
});

test("revit_place_sprinklers: the whole zone goes in one call, so it is one undo step", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  const points = Array.from({ length: 60 }, (_, i) => ({ x: i * 12, y: 0 }));
  await client.callTool({ name: "revit_place_sprinklers", arguments: { points } });
  assert.equal(bridge.calls.length, 1);
  assert.equal(bridge.calls[0].payload.points.length, 60);
  await client.close();
});

// --- element type and material ----------------------------------------------
//
// The two defects these arguments fix: geometry with no type at all (dead "Edit
// Type", nothing to schedule or filter by) and geometry with no material (the
// whole model grey). Both are batch-wide and both are snake_case out here.

test("type_name and material_id map to typeName and materialId on all four tools", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await client.callTool({
    name: "revit_create_directshape",
    arguments: {
      category: "Site",
      shapes: [CYLINDER],
      type_name: "Bollard 900mm",
      material_id: 4001,
    },
  });
  await client.callTool({
    name: "revit_place_planting",
    arguments: { points: [{ x: 0, y: 0 }], type_name: "Olea europaea", material_id: 4002 },
  });
  await client.callTool({
    name: "revit_create_pipes",
    arguments: {
      runs: [
        {
          points: [
            { x: 0, y: 0 },
            { x: 40, y: 0 },
          ],
        },
      ],
      type_name: "PEAD 50",
      material_id: 4003,
    },
  });
  await client.callTool({
    name: "revit_place_sprinklers",
    arguments: { points: [{ x: 0, y: 0 }], type_name: "Aspersor 180", material_id: 4004 },
  });

  assert.deepEqual(
    bridge.calls.map((c) => [c.endpoint, c.payload.typeName, c.payload.materialId]),
    [
      ["/directshape/create", "Bollard 900mm", 4001],
      ["/planting/place", "Olea europaea", 4002],
      ["/pipes/create", "PEAD 50", 4003],
      ["/sprinklers/place", "Aspersor 180", 4004],
    ],
  );
  for (const call of bridge.calls) {
    assert.equal("type_name" in call.payload, false);
    assert.equal("material_id" in call.payload, false);
  }
  await client.close();
});

test("material_name maps to materialName, alongside material_id", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_place_planting",
    arguments: { points: [{ x: 0, y: 0 }], material_name: "Folhagem" },
  });
  assert.equal(bridge.calls[0].payload.materialName, "Folhagem");
  assert.equal(bridge.calls[0].payload.materialId, undefined);
  await client.close();
});

test("an omitted type_name is left for the bridge to default off the element name", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_place_planting",
    arguments: { points: [{ x: 0, y: 0 }], name: "Quercus suber" },
  });
  assert.equal(bridge.calls[0].payload.typeName, undefined);
  assert.equal(bridge.calls[0].payload.name, "Quercus suber");
  await client.close();
});

test("rejects an empty type_name and a non-integer material_id", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_create_directshape", {
    category: "Site",
    shapes: [CYLINDER],
    type_name: "",
  });
  await assertRejected(client, bridge, "revit_create_directshape", {
    category: "Site",
    shapes: [CYLINDER],
    material_id: 4001.5,
  });
  await assertRejected(client, bridge, "revit_place_planting", {
    points: [{ x: 0, y: 0 }],
    material_name: "",
  });
  await assertRejected(client, bridge, "revit_place_sprinklers", {
    points: [{ x: 0, y: 0 }],
    material_id: "4001",
  });
  await client.close();
});

test("the type each element got comes back per created row", async () => {
  const bridge = fakeBridge(async () => ({
    created: [
      { id: 5001, category: "Planting", name: "Olea europaea", typeId: 900, typeName: "Olea europaea", materialId: 4002 },
      { id: 5002, category: "Planting", name: "Quercus suber", typeId: 901, typeName: "Quercus suber", materialId: 4002 },
    ],
    failed: [],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_place_planting",
    arguments: {
      points: [
        { x: 0, y: 0, name: "Olea europaea" },
        { x: 20, y: 0, name: "Quercus suber" },
      ],
      material_id: 4002,
    },
  });
  const created = JSON.parse(textOf(result)).created;
  assert.deepEqual(
    created.map((row) => row.typeName),
    ["Olea europaea", "Quercus suber"],
  );
  // A type per species, not a type per tree.
  assert.notEqual(created[0].typeId, created[1].typeId);
  await client.close();
});

test("one type is shared by a whole batch that resolves to the same name", async () => {
  const bridge = fakeBridge(async () => ({
    created: Array.from({ length: 40 }, (_, i) => ({
      id: 6000 + i,
      category: "Sprinklers",
      name: "Sprinkler",
      typeId: 950,
      typeName: "Aspersor 180",
    })),
    failed: [],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_place_sprinklers",
    arguments: {
      points: Array.from({ length: 40 }, (_, i) => ({ x: i * 12, y: 0 })),
      type_name: "Aspersor 180",
    },
  });
  const created = JSON.parse(textOf(result)).created;
  assert.equal(created.length, 40);
  assert.equal(new Set(created.map((row) => row.typeId)).size, 1);
  await client.close();
});

test("an unknown material is the bridge's error, surfaced verbatim", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      'Revit failed on /directshape/create: Unknown material "Relva". Call /revit-mcp/materials for the materials in this document, or create one with /revit-mcp/materials/create.',
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_directshape",
    arguments: { category: "Site", shapes: [CYLINDER], material_name: "Relva" },
  });
  assert.match(textOf(result), /^Error: Revit failed on \/directshape\/create/);
  assert.match(textOf(result), /Unknown material "Relva"/);
  await client.close();
});

test("revit_create_directshape: the description says the material cannot be added afterwards", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  const description = tools.find((t) => t.name === "revit_create_directshape").description;
  assert.match(description, /cannot be assigned afterwards|cannot be added afterwards/i);
  assert.match(description, /grey/i);
  assert.match(description, /type/i);
  await client.close();
});
