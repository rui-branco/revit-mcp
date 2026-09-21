import { test } from "node:test";
import assert from "node:assert/strict";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { createRevitServer } from "../index.js";

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

async function assertRejected(client, bridge, name, args) {
  const before = bridge.calls.length;
  const result = await client.callTool({ name, arguments: args });
  assert.equal(result.isError, true, `${name} accepted ${JSON.stringify(args)}`);
  assert.match(textOf(result), /validation error/i);
  assert.equal(bridge.calls.length, before, `${name} hit the bridge despite bad args`);
}

const INSPECTION = {
  readOnly: true,
  note: "The family was opened with Document.EditFamily, which hands back an independent copy, and that copy was closed without saving.",
  units: "Revit internal units (decimal feet).",
  symbol: { id: 28295, familyName: "A0 metric", typeName: "A0 metric" },
  family: { id: 28290, name: "A0 metric", category: "Title Blocks", isEditable: true, isInPlace: false },
  familyDocument: { title: "A0 metric.rfa", pathName: "", isModified: false },
  familyTypes: ["A0 metric"],
  currentType: "A0 metric",
  familyParameters: [
    {
      id: 31001,
      name: "Project Name",
      isInstance: false,
      isShared: false,
      storageType: "String",
      formula: null,
      value: null,
      display: null,
      associatedElementIds: [7101],
    },
  ],
  elementCount: 3,
  truncated: false,
  byClass: [{ name: "TextNote", count: 2 }],
  byCategory: [{ name: "Generic Annotations", count: 3 }],
  elements: [
    {
      id: 7101,
      class: "TextElement",
      category: "Generic Annotations",
      name: null,
      labelOf: ["Project Name"],
      bounds: { min: { x: 0.1, y: 0.2, z: 0 }, max: { x: 2.7, y: 0.4, z: 0 }, source: "view" },
      text: { isTextNote: false, value: "Project Name", textSize: 0.0164, textTypeName: "8mm" },
    },
    {
      id: 7102,
      class: "TextNote",
      category: "Generic Annotations",
      text: { isTextNote: true, value: "Consultant 1", textSize: 0.0082 },
    },
    {
      id: 7103,
      class: "ImageInstance",
      category: "Raster Images",
      image: { width: 0.5, height: 0.2, path: "C:\\logo.png", widthInPixels: 400 },
    },
  ],
};

test("revit_inspect_titleblock_family: symbol_id maps to symbolId on its own endpoint", async () => {
  const bridge = fakeBridge(async () => INSPECTION);
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_inspect_titleblock_family",
    arguments: { symbol_id: 28295 },
  });
  assert.deepEqual(bridge.calls, [
    { endpoint: "/families/titleblock-inspect", payload: { symbolId: 28295 } },
  ]);
  await client.close();
});

test("revit_inspect_titleblock_family: literal text, the label's parameter and the image all come back", async () => {
  const bridge = fakeBridge(async () => INSPECTION);
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_inspect_titleblock_family",
    arguments: { symbol_id: 28295 },
  });
  const payload = JSON.parse(textOf(result));

  assert.equal(payload.readOnly, true);

  // The distinction the whole inspection exists for: what is safe to remove
  // against what is driven by a parameter and has to be preserved.
  const literal = payload.elements.find((row) => row.id === 7102);
  assert.equal(literal.text.isTextNote, true);
  assert.equal(literal.text.value, "Consultant 1");
  assert.equal(literal.labelOf, undefined);

  const label = payload.elements.find((row) => row.id === 7101);
  assert.deepEqual(label.labelOf, ["Project Name"]);
  assert.equal(label.text.isTextNote, false);
  assert.equal(label.text.textSize, 0.0164);
  assert.deepEqual(payload.familyParameters[0].associatedElementIds, [7101]);

  const image = payload.elements.find((row) => row.id === 7103);
  assert.equal(image.image.path, "C:\\logo.png");
  await client.close();
});

test("revit_inspect_titleblock_family: rejects a missing, non-integer and non-numeric symbol_id", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_inspect_titleblock_family", {});
  await assertRejected(client, bridge, "revit_inspect_titleblock_family", { symbol_id: 28295.5 });
  await assertRejected(client, bridge, "revit_inspect_titleblock_family", { symbol_id: "28295" });
  await client.close();
});

test("revit_inspect_titleblock_family: a refused family is an isError, not a success with prose", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      'Revit failed on /families/titleblock-inspect: "A0 metric" is an in-place family, and Revit\'s API cannot open one for editing. Nothing was read.',
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_inspect_titleblock_family",
    arguments: { symbol_id: 28295 },
  });
  assert.equal(result.isError, true);
  assert.match(textOf(result), /in-place family/);
  await client.close();
});

const EDIT_ARGS = {
  symbol_id: 28295,
  expected_family_name: "A0 metric",
  remove_ids: [7103, 7102],
  text_edits: [{ id: 7104, text: "Escala" }],
  label_sizes: [{ id: 7101, size: 0.019685 }],
  new_notes: [
    { text: "MORADIA", point: { x: 3.385827, y: 2.591864 }, size: 0.019685, width: 0.393701 },
  ],
};

const EDIT_PLAN = {
  dryRun: true,
  applied: false,
  note: "Nothing was changed. The family was opened with Document.EditFamily and closed without saving.",
  preserved: { labelIds: [7101], scheduleInstanceIds: [7110] },
  removals: [{ id: 7103, class: "ImageInstance", removed: false }],
  labelSizes: [{ id: 7101, action: "reuse", toTypeId: 7200, isValidType: true }],
};

test("revit_edit_titleblock_family: every snake_case argument reaches the bridge camelCased", async () => {
  const bridge = fakeBridge(async () => EDIT_PLAN);
  const client = await connect(bridge);
  await client.callTool({ name: "revit_edit_titleblock_family", arguments: EDIT_ARGS });

  assert.equal(bridge.calls.length, 1);
  const { endpoint, payload } = bridge.calls[0];
  assert.equal(endpoint, "/families/titleblock-edit");
  assert.equal(payload.symbolId, 28295);
  assert.equal(payload.expectedFamilyName, "A0 metric");
  assert.deepEqual(payload.removeIds, [7103, 7102]);
  assert.deepEqual(payload.textEdits, [{ id: 7104, text: "Escala" }]);
  assert.deepEqual(payload.labelSizes, [{ id: 7101, size: 0.019685 }]);
  assert.deepEqual(payload.newNotes, EDIT_ARGS.new_notes);

  // The one argument nobody passes and everybody depends on.
  assert.equal(payload.dryRun, true);
  await client.close();
});

test("revit_edit_titleblock_family: dry_run defaults to true and false is passed through", async () => {
  const bridge = fakeBridge(async () => EDIT_PLAN);
  const client = await connect(bridge);

  await client.callTool({
    name: "revit_edit_titleblock_family",
    arguments: { symbol_id: 28295, expected_family_name: "A0 metric", remove_ids: [7103] },
  });
  assert.equal(bridge.calls[0].payload.dryRun, true);

  await client.callTool({
    name: "revit_edit_titleblock_family",
    arguments: {
      symbol_id: 28295,
      expected_family_name: "A0 metric",
      remove_ids: [7103],
      dry_run: false,
    },
  });
  assert.equal(bridge.calls[1].payload.dryRun, false);
  await client.close();
});

test("revit_edit_titleblock_family: the plan comes back with what has to survive", async () => {
  const bridge = fakeBridge(async () => EDIT_PLAN);
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_edit_titleblock_family",
    arguments: EDIT_ARGS,
  });
  const payload = JSON.parse(textOf(result));

  assert.equal(payload.applied, false);
  assert.deepEqual(payload.preserved.labelIds, [7101]);
  assert.deepEqual(payload.preserved.scheduleInstanceIds, [7110]);
  assert.equal(payload.removals[0].removed, false);
  assert.equal(payload.labelSizes[0].action, "reuse");
  await client.close();
});

test("revit_edit_titleblock_family: bad arguments never reach the bridge", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  // The guard that makes family ids mean something is not optional.
  await assertRejected(client, bridge, "revit_edit_titleblock_family", { symbol_id: 28295 });
  await assertRejected(client, bridge, "revit_edit_titleblock_family", {
    expected_family_name: "A0 metric",
  });
  await assertRejected(client, bridge, "revit_edit_titleblock_family", {
    symbol_id: 28295,
    expected_family_name: "A0 metric",
    remove_ids: ["7103"],
  });
  await assertRejected(client, bridge, "revit_edit_titleblock_family", {
    symbol_id: 28295,
    expected_family_name: "A0 metric",
    label_sizes: [{ id: 7101, size: 0 }],
  });
  await assertRejected(client, bridge, "revit_edit_titleblock_family", {
    symbol_id: 28295,
    expected_family_name: "A0 metric",
    new_notes: [{ text: "MORADIA", point: { x: 3.4 }, size: 0.02 }],
  });
  await client.close();
});

test("revit_edit_titleblock_family: a refused edit is an isError, not a success with prose", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      'Revit failed on /families/titleblock-edit: Element 205697 ("Project Name") is a label, not a literal text note. Nothing was changed.',
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_edit_titleblock_family",
    arguments: { symbol_id: 28295, expected_family_name: "A0 metric", remove_ids: [205697] },
  });
  assert.equal(result.isError, true);
  assert.match(textOf(result), /is a label, not a literal text note/);
  await client.close();
});

test("titleblock editing: the tool is registered, requires the family name and says dry_run defaults true", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  const tool = tools.find((t) => t.name === "revit_edit_titleblock_family");
  assert.ok(tool);
  assert.match(tool.description, /dry_run DEFAULTS TO TRUE/);
  assert.match(tool.description, /TextNote and an ImageInstance|TextNote or an ImageInstance/);
  assert.match(tool.description, /decimal feet/);
  assert.deepEqual(tool.inputSchema.required, ["symbol_id", "expected_family_name"]);
  await client.close();
});

test("titleblock inspection: the tool is registered and says it writes nothing", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  const tool = tools.find((t) => t.name === "revit_inspect_titleblock_family");
  assert.ok(tool);
  assert.match(tool.description, /READ-ONLY/);
  assert.match(tool.description, /closed without saving/);
  assert.match(tool.description, /isTextNote/);
  assert.match(tool.description, /labelOf/);
  assert.deepEqual(tool.inputSchema.required, ["symbol_id"]);
  await client.close();
});
