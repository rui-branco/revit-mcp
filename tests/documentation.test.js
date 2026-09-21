import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { createRevitServer } from "../index.js";
import { MAX_QUERY_LIMIT, DEFAULT_QUERY_LIMIT } from "../lib/tools/read.js";

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

// Rejected by the tool body rather than the schema — "one of these two" and
// "min below max" are not expressible in a raw shape. Still must not reach Revit.
async function assertRefused(client, bridge, name, args, pattern) {
  const before = bridge.calls.length;
  const result = await client.callTool({ name, arguments: args });
  assert.match(textOf(result), pattern);
  assert.equal(bridge.calls.length, before, `${name} hit the bridge despite bad args`);
}

const BOUNDS = { min: { x: 0, y: 0, z: 0 }, max: { x: 100, y: 80, z: 30 } };

// --- revit_get_view_crop -----------------------------------------------------

test("revit_get_view_crop: one view and a batch both reach /views/crop", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await client.callTool({ name: "revit_get_view_crop", arguments: { view_id: 211925 } });
  await client.callTool({ name: "revit_get_view_crop", arguments: { view_ids: [1, 2, 3] } });

  assert.deepEqual(bridge.calls[0], {
    endpoint: "/views/crop",
    payload: { viewId: 211925, viewIds: undefined },
  });
  assert.deepEqual(bridge.calls[1], {
    endpoint: "/views/crop",
    payload: { viewId: undefined, viewIds: [1, 2, 3] },
  });
  await client.close();
});

test("revit_get_view_crop: neither id form, and both at once, are refused", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await assertRefused(client, bridge, "revit_get_view_crop", {}, /pass either view_id/);
  await assertRefused(
    client,
    bridge,
    "revit_get_view_crop",
    { view_id: 1, view_ids: [2] },
    /not both/,
  );
  await client.close();
});

test("revit_get_view_crop: rejects a non-integer id and an empty batch", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await assertRejected(client, bridge, "revit_get_view_crop", { view_id: 1.5 });
  await assertRejected(client, bridge, "revit_get_view_crop", { view_ids: [] });
  await assertRejected(client, bridge, "revit_get_view_crop", { view_ids: ["211925"] });
  await client.close();
});

test("revit_get_view_crop: the model bounds and the template verdict reach the model unchanged", async () => {
  const bridge = fakeBridge(async () => ({
    views: [
      {
        id: 211925,
        cropSupported: true,
        cropBoxActive: false,
        modelBounds: BOUNDS,
        localBounds: { min: { x: -50, y: -40, z: 0 }, max: { x: 50, y: 40, z: 30 } },
        templateControlledCrop: ["Crop View"],
      },
    ],
  }));
  const client = await connect(bridge);

  const result = await client.callTool({
    name: "revit_get_view_crop",
    arguments: { view_id: 211925 },
  });

  // modelBounds and localBounds are different boxes and both survive the trip:
  // collapsing them would be the exact bug the endpoint exists to prevent.
  assert.match(textOf(result), /"modelBounds":\{"min":\{"x":0/);
  assert.match(textOf(result), /"localBounds":\{"min":\{"x":-50/);
  assert.match(textOf(result), /"templateControlledCrop":\["Crop View"\]/);
  await client.close();
});

// --- revit_set_view_crop -----------------------------------------------------

test("revit_set_view_crop: dry_run defaults to TRUE and model_bounds is mapped", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await client.callTool({
    name: "revit_set_view_crop",
    arguments: { view_id: 211925, model_bounds: BOUNDS },
  });

  assert.deepEqual(bridge.calls[0], {
    endpoint: "/views/set-crop",
    payload: { viewId: 211925, viewIds: undefined, modelBounds: BOUNDS, dryRun: true },
  });
  await client.close();
});

test("revit_set_view_crop: dry_run false is forwarded, not silently re-defaulted", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await client.callTool({
    name: "revit_set_view_crop",
    arguments: { view_ids: [1, 2], model_bounds: BOUNDS, dry_run: false },
  });

  assert.equal(bridge.calls[0].payload.dryRun, false);
  assert.deepEqual(bridge.calls[0].payload.viewIds, [1, 2]);
  await client.close();
});

test("revit_set_view_crop: a zero-depth box is refused before it reaches Revit", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  // Equal on z: a legal-looking box that is not a crop Revit can store.
  await assertRefused(
    client,
    bridge,
    "revit_set_view_crop",
    {
      view_id: 1,
      model_bounds: { min: { x: 0, y: 0, z: 10 }, max: { x: 10, y: 10, z: 10 } },
    },
    /min strictly less than max/,
  );

  // Inverted on x.
  await assertRefused(
    client,
    bridge,
    "revit_set_view_crop",
    {
      view_id: 1,
      model_bounds: { min: { x: 50, y: 0, z: 0 }, max: { x: 10, y: 10, z: 10 } },
    },
    /min strictly less than max/,
  );
  await client.close();
});

test("revit_set_view_crop: rejects a two-dimensional box and a missing one", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await assertRejected(client, bridge, "revit_set_view_crop", { view_id: 1 });
  await assertRejected(client, bridge, "revit_set_view_crop", {
    view_id: 1,
    model_bounds: { min: { x: 0, y: 0 }, max: { x: 10, y: 10 } },
  });
  await assertRejected(client, bridge, "revit_set_view_crop", {
    view_id: 1,
    model_bounds: { min: { x: 0, y: 0, z: 0 } },
  });
  await client.close();
});

test("revit_set_view_crop: a template refusal comes back readable", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      "Revit failed on /views/set-crop: CROP_CONTROLLED_BY_TEMPLATE — view template \"Plano Geral\" controls Crop View.",
    );
  });
  const client = await connect(bridge);

  const result = await client.callTool({
    name: "revit_set_view_crop",
    arguments: { view_id: 211925, model_bounds: BOUNDS, dry_run: false },
  });

  assert.match(textOf(result), /CROP_CONTROLLED_BY_TEMPLATE/);
  assert.match(textOf(result), /Plano Geral/);
  await client.close();
});

// --- revit_hide_elements_in_view ---------------------------------------------

test("revit_hide_elements_in_view: hidden and dry_run both default to TRUE", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await client.callTool({
    name: "revit_hide_elements_in_view",
    arguments: { view_id: 211925, ids: [900, 901] },
  });

  assert.deepEqual(bridge.calls[0], {
    endpoint: "/views/hide-elements",
    payload: { viewId: 211925, ids: [900, 901], hidden: true, dryRun: true },
  });
  await client.close();
});

test("revit_hide_elements_in_view: hidden false unhides the same list", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await client.callTool({
    name: "revit_hide_elements_in_view",
    arguments: { view_id: 1, ids: [900], hidden: false, dry_run: false },
  });

  assert.equal(bridge.calls[0].payload.hidden, false);
  assert.equal(bridge.calls[0].payload.dryRun, false);
  await client.close();
});

test("revit_hide_elements_in_view: rejects an empty id list and a missing view", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await assertRejected(client, bridge, "revit_hide_elements_in_view", { view_id: 1, ids: [] });
  await assertRejected(client, bridge, "revit_hide_elements_in_view", { ids: [900] });
  await assertRejected(client, bridge, "revit_hide_elements_in_view", {
    view_id: 1,
    ids: ["900"],
  });
  await client.close();
});

test("revit_hide_elements_in_view: an element Revit will not hide fails the batch, readably", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      "Revit failed on /views/hide-elements: ELEMENT_CANNOT_BE_HIDDEN — CanBeHidden false for \"Group 3\" (900, Model Groups).",
    );
  });
  const client = await connect(bridge);

  const result = await client.callTool({
    name: "revit_hide_elements_in_view",
    arguments: { view_id: 1, ids: [900, 901], dry_run: false },
  });

  assert.match(textOf(result), /ELEMENT_CANNOT_BE_HIDDEN/);
  await client.close();
});

// --- revit_export_pdf --------------------------------------------------------

test("revit_export_pdf: combine defaults true, overwrite defaults false, ids are mapped", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await client.callTool({
    name: "revit_export_pdf",
    arguments: { sheet_ids: [211089], folder: "C:\\Projects\\PDF" },
  });

  assert.deepEqual(bridge.calls[0], {
    endpoint: "/export/pdf",
    payload: {
      viewIds: undefined,
      sheetIds: [211089],
      folder: "C:\\Projects\\PDF",
      filename: undefined,
      combine: true,
      overwrite: false,
    },
  });
  await client.close();
});

test("revit_export_pdf: an export naming neither views nor sheets never reaches Revit", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await assertRefused(
    client,
    bridge,
    "revit_export_pdf",
    { folder: "C:\\Projects\\PDF" },
    /pass view_ids, sheet_ids, or both/,
  );
  await client.close();
});

test("revit_export_pdf: rejects a missing folder, an empty folder and empty id lists", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await assertRejected(client, bridge, "revit_export_pdf", { sheet_ids: [1] });
  await assertRejected(client, bridge, "revit_export_pdf", { sheet_ids: [1], folder: "" });
  await assertRejected(client, bridge, "revit_export_pdf", { sheet_ids: [], folder: "C:\\x" });
  await assertRejected(client, bridge, "revit_export_pdf", { view_ids: [], folder: "C:\\x" });
  await assertRejected(client, bridge, "revit_export_pdf", {
    view_ids: [1],
    folder: "C:\\x",
    filename: "",
  });
  await client.close();
});

test("revit_export_pdf: both lists are forwarded, views first then sheets", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await client.callTool({
    name: "revit_export_pdf",
    arguments: {
      view_ids: [211925],
      sheet_ids: [211089],
      folder: "C:\\Projects\\PDF",
      filename: "Plano Geral",
      combine: false,
      overwrite: true,
    },
  });

  assert.deepEqual(bridge.calls[0].payload.viewIds, [211925]);
  assert.deepEqual(bridge.calls[0].payload.sheetIds, [211089]);
  assert.equal(bridge.calls[0].payload.filename, "Plano Geral");
  assert.equal(bridge.calls[0].payload.combine, false);
  assert.equal(bridge.calls[0].payload.overwrite, true);
  await client.close();
});

test("revit_export_pdf: the manifest's measured path and unmeasurable page count survive", async () => {
  const bridge = fakeBridge(async () => ({
    folder: "C:\\Projects\\PDF",
    pageMapping: "requestedOrder",
    files: [
      {
        path: "C:\\Projects\\PDF\\Plano Geral.pdf",
        bytes: 482913,
        pages: null,
        pagesMeasured: false,
      },
    ],
  }));
  const client = await connect(bridge);

  const result = await client.callTool({
    name: "revit_export_pdf",
    arguments: { sheet_ids: [211089], folder: "C:\\Projects\\PDF" },
  });

  // pagesMeasured false with pages null is the honest answer and must not be
  // rounded into a number on the way out.
  assert.match(textOf(result), /"pages":null/);
  assert.match(textOf(result), /"pagesMeasured":false/);
  assert.match(textOf(result), /"pageMapping":"requestedOrder"/);
  await client.close();
});

test("revit_export_pdf: an unwritable folder and a clash come back readable", async () => {
  const client = await connect(
    fakeBridge(async () => {
      throw new Error(
        "Revit failed on /export/pdf: FOLDER_NOT_WRITABLE — \"C:\\Windows\\System32\" exists but cannot be written to.",
      );
    }),
  );
  const denied = await client.callTool({
    name: "revit_export_pdf",
    arguments: { sheet_ids: [1], folder: "C:\\Windows\\System32" },
  });
  assert.match(textOf(denied), /FOLDER_NOT_WRITABLE/);
  await client.close();

  const second = await connect(
    fakeBridge(async () => {
      throw new Error("Revit failed on /export/pdf: FILE_EXISTS — nothing was exported.");
    }),
  );
  const clash = await second.callTool({
    name: "revit_export_pdf",
    arguments: { sheet_ids: [1], folder: "C:\\Projects\\PDF" },
  });
  assert.match(textOf(clash), /FILE_EXISTS/);
  assert.match(textOf(clash), /nothing was exported/);
  await second.close();
});

// --- revit_read_schedule -----------------------------------------------------

test("revit_read_schedule: limit above the cap is clamped before the call", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await client.callTool({
    name: "revit_read_schedule",
    arguments: { schedule_id: 4242, limit: 9999 },
  });

  assert.equal(bridge.calls[0].endpoint, "/schedules/read");
  assert.equal(bridge.calls[0].payload.limit, MAX_QUERY_LIMIT);
  await client.close();
});

test("revit_read_schedule: omitted limit/offset default to 100/0", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await client.callTool({ name: "revit_read_schedule", arguments: { schedule_id: 4242 } });

  assert.deepEqual(bridge.calls[0].payload, {
    scheduleId: 4242,
    limit: DEFAULT_QUERY_LIMIT,
    offset: 0,
  });
  await client.close();
});

test("revit_read_schedule: rejects a negative offset, a zero limit and a missing id", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await assertRejected(client, bridge, "revit_read_schedule", { schedule_id: 1, offset: -1 });
  await assertRejected(client, bridge, "revit_read_schedule", { schedule_id: 1, limit: 0 });
  await assertRejected(client, bridge, "revit_read_schedule", {});
  await client.close();
});

test("revit_read_schedule: a null cell stays null and the total row count survives", async () => {
  const bridge = fakeBridge(async () => ({
    id: 4242,
    columns: [{ index: 0, name: "Family and Type", heading: "Espécie", isHidden: false }],
    body: {
      totalRows: 812,
      returnedRows: 2,
      truncatedRows: true,
      cells: [["Tilia cordata", null], ["Acer platanoides", "12"]],
    },
  }));
  const client = await connect(bridge);

  const result = await client.callTool({
    name: "revit_read_schedule",
    arguments: { schedule_id: 4242 },
  });

  // null means "Revit would not give text for this cell" and is not the same
  // answer as an empty string — it must not be normalised away.
  assert.match(textOf(result), /\["Tilia cordata",null\]/);
  assert.match(textOf(result), /"totalRows":812/);
  assert.match(textOf(result), /"truncatedRows":true/);
  await client.close();
});

test("revit_read_schedule: an empty cell stays an empty string, distinct from null", async () => {
  // Regression for the blank-column bug on schedule 212388: the bridge used to read cells with
  // TableSectionData.GetCellText, which returns "" for any cell type it cannot render, so a whole
  // "Family and Type" column came back blank on 142 rows that were not blank at all. The bridge
  // now reads through TableView.GetCellText and "" means empty. That only holds if this layer
  // keeps "" and null apart, which is what this pins.
  const bridge = fakeBridge(async () => ({
    id: 212388,
    columns: [
      { index: 0, name: "Family and Type", heading: "Family and Type", isHidden: false },
      { index: 1, name: "Count", heading: "Count", isHidden: false },
    ],
    body: {
      totalRows: 142,
      returnedRows: 3,
      cells: [
        ["Family and Type", "Count"],
        ["", "1"],
        ["Quercus suber", null],
      ],
    },
  }));
  const client = await connect(bridge);

  const result = await client.callTool({
    name: "revit_read_schedule",
    arguments: { schedule_id: 212388 },
  });

  const text = textOf(result);
  assert.match(text, /\["","1"\]/);
  assert.match(text, /\["Quercus suber",null\]/);
  assert.equal(text.includes('["Quercus suber",""]'), false);
  await client.close();
});

// --- revit_get_sheet_layout --------------------------------------------------

test("revit_get_sheet_layout: sheet_id is mapped and the endpoint is read-only", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await client.callTool({ name: "revit_get_sheet_layout", arguments: { sheet_id: 211089 } });

  assert.deepEqual(bridge.calls[0], {
    endpoint: "/sheets/layout",
    payload: { sheetId: 211089 },
  });
  await client.close();
});

test("revit_get_sheet_layout: rejects a missing and a non-integer sheet id", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await assertRejected(client, bridge, "revit_get_sheet_layout", {});
  await assertRejected(client, bridge, "revit_get_sheet_layout", { sheet_id: "211089" });
  await client.close();
});

test("revit_get_sheet_layout: a viewport with no measurable geometry keeps its nulls", async () => {
  const bridge = fakeBridge(async () => ({
    id: 211089,
    outline: { min: { x: 0, y: 0 }, max: { x: 3.9, y: 2.76 }, width: 3.9, height: 2.76 },
    titleblocks: [{ id: 5, bounds: null }],
    viewports: [
      { id: 7, viewId: 211925, center: { x: 1.2, y: 0.9 }, bounds: null, labelBounds: null },
    ],
    scheduleInstances: [{ id: 9, topLeft: { x: 3.1, y: 2.4 } }],
  }));
  const client = await connect(bridge);

  const result = await client.callTool({
    name: "revit_get_sheet_layout",
    arguments: { sheet_id: 211089 },
  });

  // null is "Revit gave no geometry", which must not arrive as a zero box that
  // reads like a viewport collapsed into the sheet corner.
  assert.match(textOf(result), /"bounds":null/);
  assert.match(textOf(result), /"topLeft":\{"x":3.1/);
  await client.close();
});

// --- revit_set_viewport_position ---------------------------------------------

test("revit_set_viewport_position: dry_run defaults TRUE and the optional label args stay out", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await client.callTool({
    name: "revit_set_viewport_position",
    arguments: { viewport_id: 7, center: { x: 1.95, y: 1.38 } },
  });

  assert.deepEqual(bridge.calls[0], {
    endpoint: "/sheets/set-viewport-position",
    payload: {
      viewportId: 7,
      center: { x: 1.95, y: 1.38 },
      labelOffset: undefined,
      labelLineLength: undefined,
      dryRun: true,
    },
  });
  await client.close();
});

test("revit_set_viewport_position: label_offset and label_line_length are mapped when given", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await client.callTool({
    name: "revit_set_viewport_position",
    arguments: {
      viewport_id: 7,
      center: { x: 1, y: 1 },
      label_offset: { x: 0, y: -0.2 },
      label_line_length: 0.5,
      dry_run: false,
    },
  });

  assert.deepEqual(bridge.calls[0].payload.labelOffset, { x: 0, y: -0.2 });
  assert.equal(bridge.calls[0].payload.labelLineLength, 0.5);
  assert.equal(bridge.calls[0].payload.dryRun, false);
  await client.close();
});

test("revit_set_viewport_position: rejects a missing centre, a 3D centre and a non-positive line", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await assertRejected(client, bridge, "revit_set_viewport_position", { viewport_id: 7 });
  await assertRejected(client, bridge, "revit_set_viewport_position", {
    center: { x: 1, y: 1 },
  });
  await assertRejected(client, bridge, "revit_set_viewport_position", {
    viewport_id: 7,
    center: { x: 1 },
  });
  await assertRejected(client, bridge, "revit_set_viewport_position", {
    viewport_id: 7,
    center: { x: 1, y: 1 },
    label_line_length: 0,
  });
  await client.close();
});

test("revit_set_viewport_position: before and after both reach the model", async () => {
  const bridge = fakeBridge(async () => ({
    id: 7,
    applied: true,
    requestedCenter: { x: 1.95, y: 1.38 },
    before: { center: { x: 0.5, y: 0.4 } },
    after: { center: { x: 1.95, y: 1.38 } },
  }));
  const client = await connect(bridge);

  const result = await client.callTool({
    name: "revit_set_viewport_position",
    arguments: { viewport_id: 7, center: { x: 1.95, y: 1.38 }, dry_run: false },
  });

  // "after" is read back off Revit: a viewport that did not move has to be
  // visible in the answer rather than assumed from the request.
  assert.match(textOf(result), /"before":\{"center":\{"x":0.5/);
  assert.match(textOf(result), /"after":\{"center":\{"x":1.95/);
  await client.close();
});

// --- revit_get_browser_organization ------------------------------------------

test("revit_get_browser_organization: sheet_ids is optional and the limit is clamped", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await client.callTool({ name: "revit_get_browser_organization", arguments: {} });
  await client.callTool({
    name: "revit_get_browser_organization",
    arguments: { sheet_ids: [211089], limit: 9999 },
  });

  assert.deepEqual(bridge.calls[0], {
    endpoint: "/sheets/browser-organization",
    payload: { sheetIds: undefined, limit: DEFAULT_QUERY_LIMIT },
  });
  assert.deepEqual(bridge.calls[1].payload.sheetIds, [211089]);
  assert.equal(bridge.calls[1].payload.limit, MAX_QUERY_LIMIT);
  await client.close();
});

test("revit_get_browser_organization: the read-only verdict reaches the model intact", async () => {
  const bridge = fakeBridge(async () => ({
    active: { id: 11, name: "all", sortingParameterName: "Sheet Number" },
    schemes: [{ id: 11, name: "all" }],
    canApplyFromApi: false,
    applyLimitation: "The BROWSER ORGANIZATION SCHEME is read-only in Revit's API",
    groupingLevels: null,
  }));
  const client = await connect(bridge);

  const result = await client.callTool({
    name: "revit_get_browser_organization",
    arguments: {},
  });

  // The whole point of the endpoint: the model must see that this cannot be
  // applied, so it reports the gap instead of claiming the grouping is done.
  assert.match(textOf(result), /"canApplyFromApi":false/);
  assert.match(textOf(result), /read-only in Revit's API/);
  await client.close();
});

// --- revit_set_schedule_position ---------------------------------------------

test("revit_set_schedule_position: instance_id and top_left are mapped, dry_run defaults TRUE", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await client.callTool({
    name: "revit_set_schedule_position",
    arguments: { instance_id: 211099, top_left: { x: 1.95, y: 1.38 } },
  });

  assert.equal(bridge.calls[0].endpoint, "/sheets/set-schedule-position");
  assert.deepEqual(bridge.calls[0].payload, {
    instanceId: 211099,
    topLeft: { x: 1.95, y: 1.38 },
    dryRun: true,
  });
  await client.close();
});

test("revit_set_schedule_position: rejects a half point, a missing instance and a missing point", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await assertRejected(client, bridge, "revit_set_schedule_position", {
    instance_id: 1,
    top_left: { x: 1 },
  });
  await assertRejected(client, bridge, "revit_set_schedule_position", {
    top_left: { x: 1, y: 1 },
  });
  await assertRejected(client, bridge, "revit_set_schedule_position", { instance_id: 1 });
  await client.close();
});

test("revit_set_schedule_position: a stray z is dropped, not rejected", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await client.callTool({
    name: "revit_set_schedule_position",
    arguments: { instance_id: 1, top_left: { x: 1, y: 2, z: 3 } },
  });

  // Worth pinning rather than assuming either way: the schema is a plain zod
  // object, so an extra key is stripped silently, not refused. The bridge
  // therefore never sees a z — which is right, a sheet has two dimensions — but
  // a caller who passed model coordinates gets no warning that they did.
  assert.deepEqual(bridge.calls[0].payload.topLeft, { x: 1, y: 2 });
  await client.close();
});

test("revit_set_schedule_position: the measured overflow survives, not just the point", async () => {
  // A schedule 10.6 ft tall on a 2.76 ft page is not misplaced, it is too long —
  // only the measured bounds show that, so they must reach the model intact.
  const bridge = fakeBridge(async () => ({
    id: 211099,
    requestedTopLeft: { x: 1.95, y: 1.38 },
    before: {
      topLeft: { x: 1.95, y: 1.38 },
      bounds: { min: { x: 1.94, y: -9.25 }, max: { x: 2.12, y: 1.38 }, height: 10.62 },
    },
    after: null,
    dryRun: true,
    applied: false,
  }));
  const client = await connect(bridge);

  const result = await client.callTool({
    name: "revit_set_schedule_position",
    arguments: { instance_id: 211099, top_left: { x: 1.95, y: 1.38 } },
  });

  assert.match(textOf(result), /"height":10\.62/);
  assert.match(textOf(result), /"y":-9\.25/);
  assert.match(textOf(result), /"applied":false/);
  await client.close();
});

// --- revit_configure_schedule ------------------------------------------------

test("revit_configure_schedule: snake_case is mapped to camelCase throughout", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await client.callTool({
    name: "revit_configure_schedule",
    arguments: {
      schedule_id: 212388,
      itemized: false,
      group_by: [{ field: "Family and Type", sort_order: "Ascending", show_footer_count: true }],
      fields: [{ field: 0, heading: "Espécie", width_ft: 0.4, totals: true }],
      grand_total: { show: true, show_count: true, title: "Total" },
    },
  });

  const payload = bridge.calls[0].payload;
  assert.equal(bridge.calls[0].endpoint, "/schedules/configure");
  assert.equal(payload.scheduleId, 212388);
  assert.equal(payload.itemized, false);
  assert.deepEqual(payload.groupBy, [
    {
      field: "Family and Type",
      sortOrder: "Ascending",
      showHeader: undefined,
      showFooter: undefined,
      showFooterCount: true,
      showBlankLine: undefined,
    },
  ]);
  assert.deepEqual(payload.fields, [
    { field: 0, heading: "Espécie", widthFt: 0.4, totals: true, hidden: undefined },
  ]);
  assert.deepEqual(payload.grandTotal, {
    show: true,
    showCount: true,
    showTitle: undefined,
    title: "Total",
  });
  assert.equal(payload.dryRun, true);
  await client.close();
});

test("revit_configure_schedule: a call that changes nothing is refused before the bridge", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  const result = await client.callTool({
    name: "revit_configure_schedule",
    arguments: { schedule_id: 212388 },
  });

  assert.match(textOf(result), /nothing to change/i);
  assert.equal(bridge.calls.length, 0);
  await client.close();
});

test("revit_configure_schedule: omitted sections stay out of the payload entirely", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await client.callTool({
    name: "revit_configure_schedule",
    arguments: { schedule_id: 212388, itemized: false },
  });

  // Undefined is dropped by JSON.stringify on the way out, so the bridge sees no
  // "fields" key at all and leaves the columns alone. Sending an empty array
  // instead would read as "replace the sort/group list with nothing".
  const payload = bridge.calls[0].payload;
  assert.equal(payload.groupBy, undefined);
  assert.equal(payload.fields, undefined);
  assert.equal(payload.grandTotal, undefined);
  assert.equal(JSON.stringify(payload).includes("groupBy"), false);
  await client.close();
});

test("revit_configure_schedule: rejects a bad sort order, a zero width and a bad totals name", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await assertRejected(client, bridge, "revit_configure_schedule", {
    schedule_id: 1,
    group_by: [{ field: "Type", sort_order: "Sideways" }],
  });
  await assertRejected(client, bridge, "revit_configure_schedule", {
    schedule_id: 1,
    fields: [{ field: "Count", width_ft: 0 }],
  });
  await assertRejected(client, bridge, "revit_configure_schedule", {
    schedule_id: 1,
    fields: [{ field: "Count", totals: "Average" }],
  });
  await client.close();
});

test("revit_configure_schedule: dry_run false reaches the bridge and bodyRows proves the regroup", async () => {
  const bridge = fakeBridge(async () => ({
    id: 212388,
    before: { isItemized: true, bodyRows: 142 },
    after: { isItemized: false, bodyRows: 27 },
    dryRun: false,
    applied: true,
  }));
  const client = await connect(bridge);

  const result = await client.callTool({
    name: "revit_configure_schedule",
    arguments: { schedule_id: 212388, itemized: false, dry_run: false },
  });

  assert.equal(bridge.calls[0].payload.dryRun, false);
  // The row count is the proof. 142 rows of one plant each becoming 27 species
  // rows is what "aggregated" means; "applied": true on its own is not evidence.
  assert.match(textOf(result), /"bodyRows":142/);
  assert.match(textOf(result), /"bodyRows":27/);
  await client.close();
});

test("revit_configure_schedule: filters map to camelCase and keep their value types", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await client.callTool({
    name: "revit_configure_schedule",
    arguments: {
      schedule_id: 212388,
      filters: [
        { field: "Sheet Number", operator: "BeginsWith", value: "L.02" },
        { field: 3, operator: "GreaterThan", value: 10 },
      ],
    },
  });

  const payload = bridge.calls[0].payload;
  assert.equal(bridge.calls[0].endpoint, "/schedules/configure");
  assert.deepEqual(payload.filters, [
    { field: "Sheet Number", operator: "BeginsWith", value: "L.02" },
    { field: 3, operator: "GreaterThan", value: 10 },
  ]);
  assert.equal(typeof payload.filters[0].value, "string");
  assert.equal(typeof payload.filters[1].value, "number");
  assert.equal(payload.dryRun, true);
  await client.close();
});

test("revit_configure_schedule: omitting filters preserves them, [] clears them", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await client.callTool({
    name: "revit_configure_schedule",
    arguments: { schedule_id: 212388, itemized: false },
  });
  // Absent, not empty: the bridge skips SetFilters entirely and the schedule
  // keeps whatever filters it had.
  assert.equal(bridge.calls[0].payload.filters, undefined);
  assert.equal(JSON.stringify(bridge.calls[0].payload).includes("filters"), false);

  await client.callTool({
    name: "revit_configure_schedule",
    arguments: { schedule_id: 212388, filters: [] },
  });
  // Present and empty: SetFilters([]) runs and every filter goes.
  assert.deepEqual(bridge.calls[1].payload.filters, []);
  await client.close();
});

test("revit_configure_schedule: filters alone are enough to be a real change", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  const result = await client.callTool({
    name: "revit_configure_schedule",
    arguments: {
      schedule_id: 212388,
      filters: [{ field: "Sheet Number", operator: "BeginsWith", value: "L.09" }],
    },
  });

  assert.doesNotMatch(textOf(result), /nothing to change/i);
  assert.equal(bridge.calls.length, 1);
  await client.close();
});

test("revit_configure_schedule: the filter operator allowlist is closed", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  // Real ScheduleFilterType values that this endpoint deliberately does not take.
  for (const operator of ["Contains", "NotEqual", "LessThan", "HasValue"]) {
    await assertRejected(client, bridge, "revit_configure_schedule", {
      schedule_id: 212388,
      filters: [{ field: "Sheet Number", operator, value: "L.02" }],
    });
  }

  // A filter with no value, and one with a value of neither accepted type.
  await assertRejected(client, bridge, "revit_configure_schedule", {
    schedule_id: 212388,
    filters: [{ field: "Sheet Number", operator: "Equal" }],
  });
  await assertRejected(client, bridge, "revit_configure_schedule", {
    schedule_id: 212388,
    filters: [{ field: "Sheet Number", operator: "Equal", value: true }],
  });
  await client.close();
});

test("revit_configure_schedule: a whole-number filter value stays a JSON number", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  // Regression. Filtering Area by GreaterThan 0 came back REVIT_API_ERROR "A
  // filter value is not valid for field/filter type" from SetFilters: the
  // handler picked ScheduleFilter's int constructor because 0 parses as an
  // Int32, and Area is a double. The overload is now chosen from the field's
  // spec, so what this layer has to guarantee is only that the number arrives
  // as a number - 0 must not be dropped, stringified or turned into a bool.
  await client.callTool({
    name: "revit_configure_schedule",
    arguments: {
      schedule_id: 212252,
      filters: [
        { field: "Area", operator: "GreaterThan", value: 0 },
        { field: "Area", operator: "GreaterThan", value: 0.5 },
        { field: "Count", operator: "Equal", value: 3 },
        { field: "Área", operator: "GreaterThan", value: -2 },
      ],
    },
  });

  const { filters } = bridge.calls[0].payload;
  assert.deepEqual(
    filters.map((f) => f.value),
    [0, 0.5, 3, -2],
  );
  for (const filter of filters) {
    assert.equal(typeof filter.value, "number");
  }
  // 0 is falsy: the easiest way to break this is an `if (value)` somewhere.
  assert.equal(Object.prototype.hasOwnProperty.call(filters[0], "value"), true);
  assert.equal(filters[0].value, 0);
  await client.close();
});

test("bridge source: a numeric filter picks its overload from the field's spec, not the JSON", () => {
  const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
  const source = fs.readFileSync(
    path.join(repoRoot, "revit-bridge", "handlers", "src", "Endpoints", "DocumentationEndpoints.cs"),
    "utf8",
  );

  const readFilter = source.slice(
    source.indexOf("private static ScheduleFilter ReadFilter("),
    source.indexOf("private static bool IsIntegerValuedSpec("),
  );
  assert.ok(readFilter.length > 0, "ReadFilter and IsIntegerValuedSpec should both still exist");

  // The spec decides, and the int branch is reached only through it.
  assert.match(readFilter, /ForgeTypeId spec = field\.GetSpecTypeId\(\);/);
  assert.match(readFilter, /if \(IsIntegerValuedSpec\(spec\)\)/);

  // The regression itself: TryGetInt32 must no longer be what selects the
  // constructor. It may only appear inside the integer branch, to reject a
  // fractional value for a field counted in whole numbers.
  const intBranch = readFilter.slice(readFilter.indexOf("if (IsIntegerValuedSpec(spec))"));
  // Comments are stripped: the one above this branch names TryGetInt32 to
  // explain the bug, and that prose is not what the assertion is about.
  const codeBefore = readFilter
    .slice(0, readFilter.indexOf("if (IsIntegerValuedSpec(spec))"))
    .replace(/^\s*\/\/.*$/gm, "");
  assert.doesNotMatch(codeBefore, /TryGetInt32/);
  assert.match(intBranch, /if \(!value\.TryGetInt32\(out whole\)\)/);

  // Anything not integer-valued goes to the double constructor.
  assert.match(readFilter, /return new ScheduleFilter\(field\.FieldId, filterType, value\.GetDouble\(\)\);/);

  // Integer means Integer or YesNo; a measurable spec and an absent one are doubles.
  const classifier = source.slice(source.indexOf("private static bool IsIntegerValuedSpec("));
  assert.match(classifier, /spec == null \|\| spec\.Empty\(\)/);
  assert.match(classifier, /return false;/);
  assert.match(classifier, /spec == SpecTypeId\.Int\.Integer \|\| spec == SpecTypeId\.Boolean\.YesNo/);
});

test("revit_configure_schedule: dry_run false returns the filters Revit actually holds", async () => {
  const bridge = fakeBridge(async () => ({
    id: 212388,
    name: "Índice L.02",
    dryRun: false,
    applied: true,
    before: { filters: [], bodyRows: 56 },
    after: {
      filters: [
        { fieldId: 0, name: "Sheet Number", operator: "BeginsWith", value: "L.02" },
      ],
      bodyRows: 12,
    },
  }));
  const client = await connect(bridge);

  const result = await client.callTool({
    name: "revit_configure_schedule",
    arguments: {
      schedule_id: 212388,
      filters: [{ field: "Sheet Number", operator: "BeginsWith", value: "L.02" }],
      dry_run: false,
    },
  });

  const payload = JSON.parse(textOf(result));
  assert.equal(bridge.calls[0].payload.dryRun, false);
  assert.deepEqual(payload.before.filters, []);
  assert.equal(payload.after.filters[0].operator, "BeginsWith");
  assert.equal(payload.after.filters[0].value, "L.02");
  // The row count is the proof the filter bit, not the echoed request.
  assert.equal(payload.before.bodyRows, 56);
  assert.equal(payload.after.bodyRows, 12);
  await client.close();
});
