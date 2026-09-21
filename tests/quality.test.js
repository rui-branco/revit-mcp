import { test } from "node:test";
import assert from "node:assert/strict";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { createRevitServer } from "../index.js";
import { MAX_INSPECT_IDS } from "../lib/tools/quality.js";

// The quality-assurance tools: three read-only measurements and the one edit
// that is safe beside them. Same setup as the other suites — a real MCP client
// over an in-memory transport, so the schemas doing the validating are the
// SDK's, with the bridge faked. Nothing here starts Revit or opens a socket.

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

const POINT = { x: 10, y: 20, z: 0 };

// --- revit_inspect_elements ---------------------------------------------------

test("revit_inspect_elements: ids go to /elements/inspect, parameters off by default", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({ name: "revit_inspect_elements", arguments: { ids: [245473] } });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/elements/inspect",
    payload: { ids: [245473], includeParameters: false, includeGeometry: undefined },
  });
  await client.close();
});

test("revit_inspect_elements: snake_case arguments map to the bridge's camelCase", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_inspect_elements",
    arguments: { ids: [1, 2], include_parameters: true, include_geometry: true },
  });
  assert.deepEqual(bridge.calls[0].payload, {
    ids: [1, 2],
    includeParameters: true,
    includeGeometry: true,
  });
  await client.close();
});

test("revit_inspect_elements: include_geometry stays unset when omitted, so the bridge decides", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({ name: "revit_inspect_elements", arguments: { ids: [1] } });
  assert.equal("includeGeometry" in bridge.calls[0].payload, true);
  assert.equal(bridge.calls[0].payload.includeGeometry, undefined);
  await client.close();
});

test("revit_inspect_elements: a name Revit uses twice comes back with both ids, not collapsed", async () => {
  // The live bug: the inspector reported "Level" as writable, the setter was
  // told "Level" is read-only. Both were true - they are two parameters.
  const bridge = fakeBridge(async () => ({
    requested: 1,
    found: 1,
    elements: [
      {
        id: 220545,
        parameters: {
          Level: {
            value: 210748,
            display: "Cota do jardim",
            isReadOnly: false,
            id: -1002062,
            builtIn: "SCHEDULE_LEVEL_PARAM",
            ambiguous: true,
          },
        },
        duplicateParameters: [
          {
            name: "Level",
            id: -1001352,
            builtIn: "FAMILY_LEVEL_PARAM",
            isReadOnly: true,
            display: "Cota do jardim",
            ambiguous: true,
          },
          {
            name: "Level",
            id: -1002062,
            builtIn: "SCHEDULE_LEVEL_PARAM",
            isReadOnly: false,
            display: "Cota do jardim",
            ambiguous: true,
          },
        ],
      },
    ],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_inspect_elements",
    arguments: { ids: [220545], include_parameters: true },
  });
  const payload = JSON.parse(textOf(result));
  const element = payload.elements[0];

  // The dictionary still answers by name, and now says the name is not enough.
  assert.equal(element.parameters.Level.ambiguous, true);
  assert.equal(element.parameters.Level.builtIn, "SCHEDULE_LEVEL_PARAM");

  // Both of them are reachable, each with the id parameters/set needs.
  assert.equal(element.duplicateParameters.length, 2);
  assert.deepEqual(
    element.duplicateParameters.map((p) => p.id),
    [-1001352, -1002062],
  );
  assert.equal(element.duplicateParameters.find((p) => p.isReadOnly).builtIn, "FAMILY_LEVEL_PARAM");
  await client.close();
});

test("revit_inspect_elements: exactly 500 ids is allowed, 501 never reaches the bridge", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  const ids = Array.from({ length: MAX_INSPECT_IDS }, (_, index) => 1000 + index);
  await client.callTool({ name: "revit_inspect_elements", arguments: { ids } });
  assert.equal(bridge.calls[0].payload.ids.length, MAX_INSPECT_IDS);

  // Rejected rather than truncated: a silently shortened inspection reads as a
  // complete one, which is the failure this cap exists to prevent.
  await assertRejected(client, bridge, "revit_inspect_elements", {
    ids: [...ids, 9999],
  });
  await client.close();
});

test("revit_inspect_elements: rejects an empty list, a fractional id and non-boolean flags", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_inspect_elements", {});
  await assertRejected(client, bridge, "revit_inspect_elements", { ids: [] });
  await assertRejected(client, bridge, "revit_inspect_elements", { ids: [1.5] });
  await assertRejected(client, bridge, "revit_inspect_elements", { ids: ["245473"] });
  await assertRejected(client, bridge, "revit_inspect_elements", {
    ids: [1],
    include_parameters: "yes",
  });
  await assertRejected(client, bridge, "revit_inspect_elements", {
    ids: [1],
    include_geometry: 1,
  });
  await client.close();
});

test("revit_inspect_elements: ids with no element come back in missingIds, not dropped", async () => {
  const bridge = fakeBridge(async () => ({
    coordinateSystem: "Absolute Revit model coordinates (the document's internal origin), decimal feet.",
    units: "Revit internal units (decimal feet)",
    requested: 2,
    found: 1,
    missingIds: [999999],
    elements: [{ id: 245473, name: "Ground", class: "Floor" }],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_inspect_elements",
    arguments: { ids: [245473, 999999] },
  });
  assert.equal(result.isError, undefined);
  const payload = JSON.parse(textOf(result));
  assert.deepEqual(payload.missingIds, [999999]);
  assert.equal(payload.requested, 2);
  assert.equal(payload.found, 1);
  await client.close();
});

test("revit_inspect_elements: a floor's loops and a toposolid's grades reach the model intact", async () => {
  const bridge = fakeBridge(async () => ({
    coordinateSystem: "Absolute Revit model coordinates (the document's internal origin), decimal feet.",
    requested: 2,
    found: 2,
    missingIds: [],
    elements: [
      {
        id: 245473,
        class: "Floor",
        modelBoundingBox: {
          min: { x: 28, y: 12, z: -0.82021 },
          max: { x: 68, y: 42, z: 0 },
          center: { x: 48, y: 27, z: -0.410105 },
        },
        floor: {
          level: "Ground",
          heightOffsetFromLevel: 0,
          boundarySource: "topFace",
          truncated: false,
          loops: [
            {
              closed: true,
              segmentCount: 1,
              segments: [
                { type: "Line", start: { x: 28, y: 12, z: 0 }, end: { x: 68, y: 12, z: 0 } },
              ],
            },
          ],
        },
      },
      {
        id: 210751,
        class: "Toposolid",
        slabShape: {
          vertexCount: 3,
          included: true,
          limit: 500,
          minZ: -1.5,
          maxZ: 2.25,
          truncated: false,
          vertices: [{ x: 0, y: 0, z: -1.5, type: "Corner" }],
        },
      },
    ],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_inspect_elements",
    arguments: { ids: [245473, 210751] },
  });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.elements[0].floor.loops[0].closed, true);
  assert.equal(payload.elements[0].modelBoundingBox.min.z, -0.82021);
  // The grades the paving has to sit on: the Z range is the whole point.
  assert.equal(payload.elements[1].slabShape.minZ, -1.5);
  assert.equal(payload.elements[1].slabShape.maxZ, 2.25);
  assert.equal(payload.elements[1].slabShape.truncated, false);
  await client.close();
});

test("revit_inspect_elements: a bridge failure is reported, not swallowed", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      "Revit failed on /elements/inspect: \"ids\" carries 900 ids; the cap is 500 per request.",
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_inspect_elements",
    arguments: { ids: [1] },
  });
  assert.match(textOf(result), /^Error: Revit failed on \/elements\/inspect/);
  await client.close();
});

// --- revit_move_elements -------------------------------------------------------

test("revit_move_elements: dry_run defaults to true, so the default call changes nothing", async () => {
  const bridge = fakeBridge(async () => ({ dryRun: true, moved: false, elements: [] }));
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_move_elements",
    arguments: { ids: [101], translation: { x: 0, y: 0, z: 1.5 } },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/elements/move",
    payload: { ids: [101], translation: { x: 0, y: 0, z: 1.5 }, dryRun: true },
  });
  await client.close();
});

test("revit_move_elements: dry_run false is forwarded as dryRun false", async () => {
  const bridge = fakeBridge(async () => ({ dryRun: false, moved: true, elements: [] }));
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_move_elements",
    arguments: { ids: [101, 102], translation: POINT, dry_run: false },
  });
  assert.deepEqual(bridge.calls[0].payload, {
    ids: [101, 102],
    translation: POINT,
    dryRun: false,
  });
  await client.close();
});

test("revit_move_elements: the whole batch goes in one call, so it is one undo step", async () => {
  const bridge = fakeBridge(async () => ({ dryRun: false, moved: true, elements: [] }));
  const client = await connect(bridge);
  const ids = Array.from({ length: 42 }, (_, index) => 1000 + index);
  await client.callTool({
    name: "revit_move_elements",
    arguments: { ids, translation: { x: 0, y: 0, z: 2 }, dry_run: false },
  });
  assert.equal(bridge.calls.length, 1);
  assert.equal(bridge.calls[0].payload.ids.length, 42);
  await client.close();
});

test("revit_move_elements: rejects a missing, partial or non-finite translation", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_move_elements", { ids: [101] });
  await assertRejected(client, bridge, "revit_move_elements", {
    ids: [101],
    translation: { x: 1, y: 2 },
  });
  await assertRejected(client, bridge, "revit_move_elements", {
    ids: [101],
    translation: { x: 1, y: 2, z: "up" },
  });
  // Infinity and NaN are not lengths, and a move by one is not a move at all.
  await assertRejected(client, bridge, "revit_move_elements", {
    ids: [101],
    translation: { x: 1, y: 2, z: Infinity },
  });
  await assertRejected(client, bridge, "revit_move_elements", {
    ids: [101],
    translation: { x: 1, y: 2, z: NaN },
  });
  await client.close();
});

test("revit_move_elements: rejects an empty id list, a fractional id and a non-boolean dry_run", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_move_elements", { ids: [], translation: POINT });
  await assertRejected(client, bridge, "revit_move_elements", { ids: [1.5], translation: POINT });
  await assertRejected(client, bridge, "revit_move_elements", {
    ids: [101],
    translation: POINT,
    dry_run: "no",
  });
  await client.close();
});

test("revit_move_elements: before and after come back for every element, with afterSource", async () => {
  const bridge = fakeBridge(async () => ({
    dryRun: true,
    moved: false,
    count: 1,
    translation: { x: 0, y: 0, z: 1.5 },
    coordinateSystem: "Absolute Revit model coordinates (the document's internal origin), decimal feet.",
    afterSource: "predicted",
    elements: [
      {
        id: 101,
        name: "Chair",
        category: "Furniture",
        before: {
          location: { kind: "point", point: { x: 4, y: 6, z: -0.5 } },
          boundingBox: {
            min: { x: 3, y: 5, z: -0.5 },
            max: { x: 5, y: 7, z: 2 },
            center: { x: 4, y: 6, z: 0.75 },
          },
        },
        after: {
          location: { kind: "point", point: { x: 4, y: 6, z: 1 } },
          boundingBox: {
            min: { x: 3, y: 5, z: 1 },
            max: { x: 5, y: 7, z: 3.5 },
            center: { x: 4, y: 6, z: 2.25 },
          },
        },
      },
    ],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_move_elements",
    arguments: { ids: [101], translation: { x: 0, y: 0, z: 1.5 } },
  });
  assert.equal(result.isError, undefined);
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.dryRun, true);
  assert.equal(payload.moved, false);
  // A dry run says its "after" is arithmetic rather than a reading.
  assert.equal(payload.afterSource, "predicted");
  assert.equal(payload.elements[0].before.location.point.z, -0.5);
  assert.equal(payload.elements[0].after.location.point.z, 1);
  assert.equal(payload.elements[0].after.boundingBox.min.z, 1);
  await client.close();
});

test("revit_move_elements: a pinned or grouped element is refused by the bridge and reported here", async () => {
  const pinned = fakeBridge(async () => {
    throw new Error(
      "Revit failed on /elements/move: Element(s) 101 are pinned. The bridge will not unpin them for you. Nothing was moved.",
    );
  });
  const client = await connect(pinned);
  const result = await client.callTool({
    name: "revit_move_elements",
    arguments: { ids: [101], translation: POINT, dry_run: false },
  });
  assert.match(textOf(result), /pinned/);
  assert.match(textOf(result), /Nothing was moved/);
  await client.close();

  const grouped = fakeBridge(async () => {
    throw new Error(
      "Revit failed on /elements/move: Element(s) 102 belong to a group. Nothing was moved.",
    );
  });
  const second = await connect(grouped);
  const groupedResult = await second.callTool({
    name: "revit_move_elements",
    arguments: { ids: [102], translation: POINT, dry_run: false },
  });
  assert.match(textOf(groupedResult), /group/);
  await second.close();
});

test("revit_move_elements: a move Revit accepted but did not apply is an error, not moved:true", async () => {
  // The live bug this guards: element 220545 takes its elevation from its level,
  // so MoveElements returned success, the readback showed the same z before and
  // after, and the response still said moved: true.
  const bridge = fakeBridge(async () => {
    throw new Error(
      "Revit failed on /elements/move: MOVE_NOT_APPLIED — Revit accepted the move and did not apply it: " +
        "220545 asked for (0, 0, 1.5) and got (0, 0, 0) (feet, tolerance 0.0025). The whole request was " +
        "rolled back - the model is exactly as it was.",
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_move_elements",
    arguments: { ids: [220545], translation: { x: 0, y: 0, z: 1.5 }, dry_run: false },
  });
  assert.equal(result.isError, true);
  assert.match(textOf(result), /MOVE_NOT_APPLIED/);
  // Requested against actual, both named - "it did not move" on its own does not
  // tell the caller how far short it fell.
  assert.match(textOf(result), /asked for \(0, 0, 1\.5\) and got \(0, 0, 0\)/);
  assert.match(textOf(result), /rolled back/);
  await client.close();
});

test("revit_move_elements: an element that cannot be measured is reported, not counted as moved", async () => {
  const bridge = fakeBridge(async () => ({
    dryRun: false,
    moved: true,
    count: 2,
    translation: { x: 0, y: 0, z: 1.5 },
    afterSource: "readBack",
    unverified: [303],
    elements: [
      { id: 101, name: "Chair", category: "Furniture", before: {}, after: {} },
      { id: 303, name: "Sketch", category: "Lines", before: {}, after: {} },
    ],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_move_elements",
    arguments: { ids: [101, 303], translation: { x: 0, y: 0, z: 1.5 }, dry_run: false },
  });
  const payload = JSON.parse(textOf(result));
  assert.deepEqual(payload.unverified, [303]);
  await client.close();
});

test("revit_move_elements: a rolled-back transaction is an error here, never a quiet success", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      'Revit failed on /elements/move: Revit rolled "Move elements" back instead of committing it (status RolledBack). Nothing from this request was left in the model.',
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_move_elements",
    arguments: { ids: [101], translation: POINT, dry_run: false },
  });
  assert.match(textOf(result), /^Error: /);
  assert.match(textOf(result), /RolledBack/);
  await client.close();
});

// --- revit_excavate_toposolid --------------------------------------------------

test("revit_excavate_toposolid: dry_run defaults to true, so the default call changes nothing", async () => {
  const bridge = fakeBridge(async () => ({ dryRun: true, excavated: false }));
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_excavate_toposolid",
    arguments: { toposolid_id: 210751, ids: [245473] },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/toposolid/excavate",
    payload: { toposolidId: 210751, ids: [245473], dryRun: true },
  });
  await client.close();
});

test("revit_excavate_toposolid: dry_run false is forwarded as dryRun false", async () => {
  const bridge = fakeBridge(async () => ({ dryRun: false, excavated: true }));
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_excavate_toposolid",
    arguments: { toposolid_id: 210751, ids: [245473], dry_run: false },
  });
  assert.equal(bridge.calls[0].payload.dryRun, false);
  await client.close();
});

test("revit_excavate_toposolid: an omitted toposolid_id is left to the bridge, not invented", async () => {
  const bridge = fakeBridge(async () => ({ dryRun: true, toposolidId: 210751 }));
  const client = await connect(bridge);
  await client.callTool({ name: "revit_excavate_toposolid", arguments: { ids: [245473] } });
  assert.equal(bridge.calls[0].payload.toposolidId, undefined);
  assert.deepEqual(bridge.calls[0].payload.ids, [245473]);
  await client.close();
});

test("revit_excavate_toposolid: the whole batch goes in one call, so it is one undo step", async () => {
  const bridge = fakeBridge(async () => ({ dryRun: false, excavatedCount: 3 }));
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_excavate_toposolid",
    arguments: { toposolid_id: 210751, ids: [245473, 245502, 301], dry_run: false },
  });
  assert.equal(bridge.calls.length, 1);
  assert.deepEqual(bridge.calls[0].payload.ids, [245473, 245502, 301]);
  await client.close();
});

test("revit_excavate_toposolid: rejects an empty id list, fractional ids and a non-boolean dry_run", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_excavate_toposolid", { ids: [] });
  await assertRejected(client, bridge, "revit_excavate_toposolid", { ids: [1.5] });
  await assertRejected(client, bridge, "revit_excavate_toposolid", { ids: ["245473"] });
  await assertRejected(client, bridge, "revit_excavate_toposolid", {
    ids: [245473],
    toposolid_id: 210751.5,
  });
  await assertRejected(client, bridge, "revit_excavate_toposolid", {
    ids: [245473],
    dry_run: "yes",
  });
  await client.close();
});

test("revit_excavate_toposolid: the volume readback comes back whole, before and after", async () => {
  const bridge = fakeBridge(async () => ({
    dryRun: false,
    excavated: true,
    excavatedCount: 1,
    toposolidId: 210751,
    toposolidName: "Terreno",
    count: 1,
    units: "Revit internal units: decimal feet, volumes in cubic feet",
    volumeBefore: 12000.5,
    volumeAfter: 11750.25,
    volumeRemoved: 250.25,
    elements: [
      {
        id: 245473,
        name: "Ground slab",
        category: "Floors",
        alreadyExcavating: false,
        excavated: true,
        excavationVolume: 250.25,
      },
    ],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_excavate_toposolid",
    arguments: { toposolid_id: 210751, ids: [245473], dry_run: false },
  });
  const payload = JSON.parse(textOf(result));
  // The difference of two readings is what says the hole actually took material out.
  assert.equal(payload.volumeBefore - payload.volumeAfter, payload.volumeRemoved);
  assert.equal(payload.elements[0].excavated, true);
  assert.equal(payload.elements[0].excavationVolume, 250.25);
  await client.close();
});

test("revit_excavate_toposolid: a dry run reports null volumes rather than guessing them", async () => {
  const bridge = fakeBridge(async () => ({
    dryRun: true,
    excavated: false,
    excavatedCount: 0,
    volumeBefore: 12000.5,
    volumeAfter: null,
    volumeRemoved: null,
    elements: [
      {
        id: 245473,
        alreadyExcavating: false,
        excavated: false,
        excavationVolume: null,
      },
    ],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_excavate_toposolid",
    arguments: { toposolid_id: 210751, ids: [245473] },
  });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.volumeBefore, 12000.5);
  assert.equal(payload.volumeAfter, null);
  assert.equal(payload.volumeRemoved, null);
  assert.equal(payload.elements[0].excavated, false);
  await client.close();
});

test("revit_excavate_toposolid: an element already cutting the surface is reported, not cut twice", async () => {
  const bridge = fakeBridge(async () => ({
    dryRun: false,
    excavated: false,
    excavatedCount: 0,
    elements: [{ id: 245473, alreadyExcavating: true, excavated: false, excavationVolume: 250.25 }],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_excavate_toposolid",
    arguments: { toposolid_id: 210751, ids: [245473], dry_run: false },
  });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.elements[0].alreadyExcavating, true);
  assert.equal(payload.excavatedCount, 0);
  await client.close();
});

test("revit_excavate_toposolid: an element Revit will not excavate with is refused, whole batch", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      "Revit failed on /toposolid/excavate: Toposolid 210751 cannot be excavated by element(s) 301: Revit's own CanBeExcavatedBy says no. Nothing was excavated.",
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_excavate_toposolid",
    arguments: { toposolid_id: 210751, ids: [245473, 301], dry_run: false },
  });
  assert.match(textOf(result), /^Error: /);
  assert.match(textOf(result), /CanBeExcavatedBy/);
  assert.match(textOf(result), /Nothing was excavated/);
  await client.close();
});

test("revit_excavate_toposolid: a rolled-back transaction is an error here, never a quiet success", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      'Revit failed on /toposolid/excavate: Revit rolled "Excavate toposolid" back instead of committing it (status RolledBack). Nothing from this request was left in the model.',
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_excavate_toposolid",
    arguments: { toposolid_id: 210751, ids: [245473], dry_run: false },
  });
  assert.match(textOf(result), /^Error: /);
  assert.match(textOf(result), /RolledBack/);
  await client.close();
});

// --- revit_get_warnings --------------------------------------------------------

test("revit_get_warnings: defaults are limit 100, offset 0", async () => {
  const bridge = fakeBridge(async () => ({ total: 0, offset: 0, limit: 100, warnings: [] }));
  const client = await connect(bridge);
  await client.callTool({ name: "revit_get_warnings", arguments: {} });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/document/warnings",
    payload: { limit: 100, offset: 0 },
  });
  await client.close();
});

test("revit_get_warnings: an oversized limit is clamped to 500, not refused", async () => {
  const bridge = fakeBridge(async () => ({ total: 0, offset: 0, limit: 500, warnings: [] }));
  const client = await connect(bridge);
  await client.callTool({ name: "revit_get_warnings", arguments: { limit: 10000, offset: 20 } });
  assert.deepEqual(bridge.calls[0].payload, { limit: 500, offset: 20 });
  await client.close();
});

test("revit_get_warnings: rejects a zero limit and a negative offset", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_get_warnings", { limit: 0 });
  await assertRejected(client, bridge, "revit_get_warnings", { limit: 1.5 });
  await assertRejected(client, bridge, "revit_get_warnings", { offset: -1 });
  await client.close();
});

test("revit_get_warnings: the definition GUID and both id lists reach the model", async () => {
  const bridge = fakeBridge(async () => ({
    total: 3,
    offset: 0,
    limit: 100,
    warnings: [
      {
        failureDefinitionId: "d0b0d1b9-8b06-4e1a-9d21-1a9f2a6e9a11",
        severity: "Warning",
        message: "Toposolid and Floor overlap.",
        failingElementIds: [210751],
        additionalElementIds: [245473],
      },
    ],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_get_warnings", arguments: {} });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.total, 3);
  assert.match(payload.warnings[0].failureDefinitionId, /^[0-9a-f-]{36}$/);
  assert.deepEqual(payload.warnings[0].failingElementIds, [210751]);
  assert.deepEqual(payload.warnings[0].additionalElementIds, [245473]);
  await client.close();
});

test("revit_get_warnings: a bridge failure is reported, not swallowed", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error("Cannot reach Revit at http://localhost:48884/revit-mcp — connection refused.");
  });
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_get_warnings", arguments: {} });
  assert.match(textOf(result), /^Error: Cannot reach Revit/);
  await client.close();
});

// --- revit_list_view_templates -------------------------------------------------

test("revit_list_view_templates: hits /views/templates and takes no arguments", async () => {
  const bridge = fakeBridge(async () => []);
  const client = await connect(bridge);
  await client.callTool({ name: "revit_list_view_templates", arguments: {} });
  assert.deepEqual(bridge.calls[0], { endpoint: "/views/templates", payload: undefined });
  await client.close();
});

test("revit_list_view_templates: a document with no templates answers an empty list, not an error", async () => {
  const bridge = fakeBridge(async () => []);
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_list_view_templates", arguments: {} });
  assert.equal(result.isError, undefined);
  assert.deepEqual(JSON.parse(textOf(result)), []);
  await client.close();
});

test("revit_list_view_templates: controlled parameters come back with ids and labels", async () => {
  const bridge = fakeBridge(async () => [
    {
      id: 1204,
      name: "Site Plan",
      viewType: "FloorPlan",
      controlledParameterCount: 2,
      controlledParameters: [
        { id: -1006952, label: "View Scale" },
        { id: 883122, label: "Phase" },
      ],
    },
  ]);
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_list_view_templates", arguments: {} });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload[0].viewType, "FloorPlan");
  assert.equal(payload[0].controlledParameters[0].label, "View Scale");
  // A built-in parameter has a negative id, a project parameter a positive one.
  assert.ok(payload[0].controlledParameters[0].id < 0);
  assert.ok(payload[0].controlledParameters[1].id > 0);
  await client.close();
});

// --- a bridge failure is a tool error, not prose --------------------------------

test("the quality tools flag a bridge failure as isError, never as a quiet success", async () => {
  // Everything these tools refuse — a pinned element, CanBeExcavatedBy saying
  // no, a rolled-back transaction — arrives as a bridge exception. Without the
  // flag an MCP client reads it as a call that worked whose text happens to
  // start with "Error:", which is the one report a write must never produce.
  const calls = [
    ["revit_inspect_elements", { ids: [245473] }],
    ["revit_move_elements", { ids: [101], translation: POINT, dry_run: false }],
    ["revit_excavate_toposolid", { toposolid_id: 210751, ids: [245473], dry_run: false }],
    ["revit_get_warnings", {}],
    ["revit_list_view_templates", {}],
  ];

  for (const [name, args] of calls) {
    const bridge = fakeBridge(async () => {
      throw new Error(
        'Revit failed: Revit rolled "Excavate toposolid" back instead of committing it (status RolledBack). Nothing from this request was left in the model.',
      );
    });
    const client = await connect(bridge);
    const result = await client.callTool({ name, arguments: args });
    assert.equal(result.isError, true, `${name} reported a bridge failure as a success`);
    assert.match(textOf(result), /^Error: /);
    await client.close();
  }
});

// --- the descriptions carry what makes these safe ------------------------------

test("the quality tools say what they measure and what they will not do", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  const describe = (name) => tools.find((t) => t.name === name).description;

  assert.match(describe("revit_inspect_elements"), /absolute model feet/i);
  assert.match(describe("revit_inspect_elements"), /missingIds/);

  // The two properties that make a move tool safe to hand to a model.
  assert.match(describe("revit_move_elements"), /dry run by default/i);
  assert.match(describe("revit_move_elements"), /all-or-nothing/i);
  assert.match(describe("revit_move_elements"), /will NOT unpin/);
  assert.match(describe("revit_move_elements"), /before and after/i);

  // Two different lists that both read as "warnings" — say so.
  assert.match(describe("revit_get_warnings"), /NOT revit_diagnostics/);
  assert.match(describe("revit_list_view_templates"), /controls/i);
  await client.close();
});

test("the quality tools declare their required arguments", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  const schema = (name) => tools.find((t) => t.name === name).inputSchema;

  assert.deepEqual(schema("revit_inspect_elements").required, ["ids"]);
  assert.deepEqual(schema("revit_move_elements").required, ["ids", "translation"]);
  // Both are paging arguments with defaults, so neither is required.
  assert.equal(schema("revit_get_warnings").required, undefined);
  await client.close();
});
