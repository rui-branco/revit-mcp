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

const LINES = [
  { start: { x: 0, y: 0 }, end: { x: 2, y: 0 } },
  { start: { x: 2, y: 0 }, end: { x: 2, y: 1 } },
];

// --- revit_draw_detail_lines -------------------------------------------------

test("revit_draw_detail_lines: view_id and line_style map to camelCase on the wire", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_draw_detail_lines",
    arguments: { view_id: 4711, lines: LINES, line_style: "Thin Lines" },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/detail/lines",
    payload: { viewId: 4711, lines: LINES, lineStyle: "Thin Lines" },
  });
  await client.close();
});

test("revit_draw_detail_lines: an omitted line style is left for the bridge's default", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_draw_detail_lines",
    arguments: { view_id: 1, lines: LINES },
  });
  assert.equal(bridge.calls[0].payload.lineStyle, undefined);
  await client.close();
});

test("revit_draw_detail_lines: rejects an empty batch, a half line and a non-integer view", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_draw_detail_lines", {});
  await assertRejected(client, bridge, "revit_draw_detail_lines", { view_id: 1, lines: [] });
  await assertRejected(client, bridge, "revit_draw_detail_lines", {
    view_id: 1,
    lines: [{ start: { x: 0, y: 0 } }],
  });
  await assertRejected(client, bridge, "revit_draw_detail_lines", {
    view_id: 1,
    lines: [{ start: { x: 0 }, end: { x: 1, y: 1 } }],
  });
  await assertRejected(client, bridge, "revit_draw_detail_lines", { view_id: 1.5, lines: LINES });
  await client.close();
});

test("revit_draw_detail_lines: the whole detail goes in one call, so it is one undo step", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  const lines = Array.from({ length: 40 }, (_, index) => ({
    start: { x: index, y: 0 },
    end: { x: index, y: 3 },
  }));
  await client.callTool({
    name: "revit_draw_detail_lines",
    arguments: { view_id: 9, lines },
  });
  assert.equal(bridge.calls.length, 1);
  assert.equal(bridge.calls[0].payload.lines.length, 40);
  await client.close();
});

test("revit_draw_detail_lines: an unknown line style comes back with the available ones, not an error", async () => {
  const bridge = fakeBridge(async () => ({
    viewId: 9,
    elevation: 0,
    lineStyle: null,
    requestedLineStyle: "Hairline",
    availableLineStyles: ["Medium Lines", "Thin Lines", "Wide Lines"],
    created: [{ id: 501, start: { x: 0, y: 0 }, end: { x: 2, y: 0 } }],
    failed: [],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_draw_detail_lines",
    arguments: { view_id: 9, lines: [LINES[0]], line_style: "Hairline" },
  });
  const payload = JSON.parse(textOf(result));
  assert.equal(result.isError, undefined);
  assert.equal(payload.lineStyle, null);
  assert.deepEqual(payload.availableLineStyles, ["Medium Lines", "Thin Lines", "Wide Lines"]);
  assert.equal(payload.created.length, 1);
  await client.close();
});

test("revit_draw_detail_lines: a view that cannot host detail comes back with its code", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error("VIEW_CANNOT_HOST_DETAIL: view 12 is not a drafting view or a plan");
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_draw_detail_lines",
    arguments: { view_id: 12, lines: LINES },
  });
  assert.match(textOf(result), /VIEW_CANNOT_HOST_DETAIL/);
  await client.close();
});

test("revit_draw_detail_lines: the description says the bridge supplies the view plane", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  const description = tools.find((t) => t.name === "revit_draw_detail_lines").description;
  assert.match(description, /plane of the view/i);
  assert.match(description, /feet \(Revit internal units\)/);
  await client.close();
});

// --- revit_add_text_notes ----------------------------------------------------

test("revit_add_text_notes: view_id maps to viewId and the notes go through as given", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  const notes = [
    { x: 0.5, y: 1.2, text: "Betão de limpeza", size: 0.0082 },
    { x: 0.5, y: 0.9, text: "Tout-venant 0/31,5" },
  ];
  await client.callTool({
    name: "revit_add_text_notes",
    arguments: { view_id: 88, notes },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/detail/text",
    payload: { viewId: 88, notes },
  });
  await client.close();
});

test("revit_add_text_notes: rejects an empty batch, empty text and a non-positive size", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_add_text_notes", {});
  await assertRejected(client, bridge, "revit_add_text_notes", { view_id: 1, notes: [] });
  await assertRejected(client, bridge, "revit_add_text_notes", {
    view_id: 1,
    notes: [{ x: 0, y: 0, text: "" }],
  });
  await assertRejected(client, bridge, "revit_add_text_notes", {
    view_id: 1,
    notes: [{ x: 0, y: 0, text: "Nota", size: 0 }],
  });
  await assertRejected(client, bridge, "revit_add_text_notes", {
    view_id: 1,
    notes: [{ y: 0, text: "Nota" }],
  });
  await client.close();
});

test("revit_add_text_notes: a note Revit refused comes back under failed with its index", async () => {
  const bridge = fakeBridge(async () => ({
    viewId: 88,
    elevation: 0,
    created: [{ id: 601, x: 0, y: 0, typeName: "MCP Text 0.0082 ft" }],
    failed: [{ index: 1, reason: "A valid point must not be father then 10 miles from the origin." }],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_add_text_notes",
    arguments: {
      view_id: 88,
      notes: [
        { x: 0, y: 0, text: "Ok" },
        { x: 9e9, y: 0, text: "Too far" },
      ],
    },
  });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.created.length, 1);
  assert.equal(payload.failed[0].index, 1);
  await client.close();
});

test("revit_add_text_notes: the description says size is paper feet and lives on the type", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  const description = tools.find((t) => t.name === "revit_add_text_notes").description;
  assert.match(description, /PAPER/);
  assert.match(description, /type/i);
  await client.close();
});
