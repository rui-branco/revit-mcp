import { test } from "node:test";
import assert from "node:assert/strict";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { createRevitServer } from "../index.js";
import { clampLimit, DEFAULT_QUERY_LIMIT, MAX_QUERY_LIMIT } from "../lib/tools/read.js";

// The tools are exercised through a real MCP client over an in-memory
// transport, so schemas and validation are the SDK's, not a stand-in. The
// bridge is faked: it records calls and never opens a socket.

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

const READ_TOOLS = [
  "revit_status",
  "revit_list_levels",
  "revit_list_categories",
  "revit_query_elements",
  "revit_get_elements",
  "revit_get_selection",
];
const WRITE_TOOLS = [
  "revit_create_levels",
  "revit_create_walls",
  "revit_set_parameters",
  "revit_delete_elements",
];
const PARAMETER_TOOLS = ["revit_create_project_parameter", "revit_set_sheet_parameters"];
const DOCUMENT_TOOLS = [
  "revit_new_project",
  "revit_save",
  "revit_save_as",
  "revit_open_project",
  "revit_close_project",
];
const MODEL_TOOLS = [
  "revit_create_toposolid",
  "revit_flatten_toposolid",
  "revit_create_floor",
  "revit_load_families",
  "revit_list_family_symbols",
  "revit_place_families",
  "revit_place_openings",
];
const GEOMETRY_TOOLS = [
  "revit_create_directshape",
  "revit_place_planting",
  "revit_create_pipes",
  "revit_place_sprinklers",
];
const MATERIAL_TOOLS = [
  "revit_list_materials",
  "revit_create_material",
  "revit_set_material_texture",
  "revit_assign_material",
  "revit_create_wall_type",
  "revit_create_floor_type",
];
const MATERIAL_APPEARANCE_TOOLS = [
  "revit_get_material_appearance",
  "revit_set_material_appearance",
];
const SHEET_TOOLS = ["revit_list_titleblocks", "revit_list_sheets", "revit_create_sheets"];
const SHEET_COLLECTION_TOOLS = ["revit_list_sheet_collections", "revit_set_sheet_collections"];
const TITLEBLOCK_TOOLS = ["revit_inspect_titleblock_family", "revit_edit_titleblock_family"];
const VIEW_TOOLS = [
  "revit_list_views",
  "revit_create_plan_view",
  "revit_create_drafting_view",
  "revit_create_section_view",
  "revit_list_legends",
  "revit_create_legend",
  "revit_create_3d_view",
  "revit_set_view_style",
  "revit_set_view_background",
  "revit_hide_view_categories",
  "revit_override_view_categories",
  "revit_set_view_sun",
  "revit_export_view_image",
  "revit_duplicate_view",
  "revit_set_view_scale",
  "revit_scale_perspective_crop",
  "revit_place_views_on_sheets",
  "revit_create_schedule",
];
const GRAPHICS_TOOLS = [
  "revit_get_view_graphics",
  "revit_set_view_graphics",
  "revit_get_view_graphics_command_status",
  "revit_capture_view_template",
  "revit_apply_view_template",
];
const DETAIL_TOOLS = ["revit_draw_detail_lines", "revit_add_text_notes"];
const DOCUMENTATION_TOOLS = [
  "revit_get_view_crop",
  "revit_set_view_crop",
  "revit_hide_elements_in_view",
  "revit_export_pdf",
  "revit_read_schedule",
  "revit_get_sheet_layout",
  "revit_get_browser_organization",
  "revit_set_viewport_position",
  "revit_set_schedule_position",
  "revit_configure_schedule",
];
const DIAGNOSTICS_TOOLS = ["revit_diagnostics", "revit_set_auto_dismiss"];
const QUALITY_TOOLS = [
  "revit_inspect_elements",
  "revit_move_elements",
  "revit_excavate_toposolid",
  "revit_get_warnings",
  "revit_list_view_templates",
];
const RELOAD_TOOLS = ["revit_reload_bridge"];
const INSTALL_TOOLS = ["revit_install_bridge", "revit_uninstall_bridge"];

// --- tool surface ------------------------------------------------------------

test("tool list: exactly the read, write, parameter, model, geometry, material, document, sheet, view, detail, diagnostics, reload and install tools", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  assert.deepEqual(
    tools.map((t) => t.name),
    [
      ...READ_TOOLS,
      ...WRITE_TOOLS,
      ...PARAMETER_TOOLS,
      ...MODEL_TOOLS,
      ...GEOMETRY_TOOLS,
      ...MATERIAL_TOOLS,
      ...MATERIAL_APPEARANCE_TOOLS,
      ...DOCUMENT_TOOLS,
      ...SHEET_TOOLS,
      ...SHEET_COLLECTION_TOOLS,
      ...TITLEBLOCK_TOOLS,
      ...VIEW_TOOLS,
      ...GRAPHICS_TOOLS,
      ...DETAIL_TOOLS,
      ...DOCUMENTATION_TOOLS,
      ...DIAGNOSTICS_TOOLS,
      ...QUALITY_TOOLS,
      ...RELOAD_TOOLS,
      ...INSTALL_TOOLS,
    ],
  );
  await client.close();
});

test("tool schemas: every tool has a description and an object input schema", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  for (const tool of tools) {
    assert.ok(tool.description && tool.description.length > 20, `${tool.name} description`);
    assert.equal(tool.inputSchema.type, "object", `${tool.name} schema type`);
    assert.equal(typeof tool.inputSchema.properties, "object", `${tool.name} properties`);
  }
  await client.close();
});

test("tool schemas: required arguments are marked required, optional ones are not", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  const schema = (name) => tools.find((t) => t.name === name).inputSchema;

  assert.deepEqual(schema("revit_get_elements").required, ["ids"]);
  assert.deepEqual(schema("revit_set_parameters").required, ["ids", "name", "value"]);
  assert.deepEqual(schema("revit_create_walls").required, [
    "level",
    "wall_type",
    "height",
    "curves",
  ]);
  assert.deepEqual(schema("revit_create_sheets").required, ["sheets"]);
  assert.deepEqual(schema("revit_create_toposolid").required, ["points"]);
  assert.deepEqual(schema("revit_create_floor").required, ["level", "boundary"]);
  // "level" is deliberately not required: site content defaults to the lowest level.
  assert.deepEqual(schema("revit_place_families").required, ["symbol_id", "points"]);
  assert.deepEqual(schema("revit_create_3d_view").required, ["name"]);
  assert.deepEqual(schema("revit_set_view_style").required, ["view_id", "style"]);
  assert.deepEqual(schema("revit_set_view_background").required, ["view_id", "kind"]);
  assert.deepEqual(schema("revit_hide_view_categories").required, ["view_id", "categories"]);
  // Only view_id: the tool checks that one of the two angle/date groups is there.
  assert.deepEqual(schema("revit_set_view_sun").required, ["view_id"]);
  // "host_wall_id" is deliberately not required: the bridge finds the nearest wall.
  assert.deepEqual(schema("revit_place_openings").required, ["symbol_id", "points"]);
  // Both id forms are optional on their own; the tool checks that one of them is there.
  assert.equal(schema("revit_export_view_image").required, undefined);
  assert.equal(schema("revit_load_families").required, undefined);
  assert.deepEqual(schema("revit_create_plan_view").required, ["level", "name"]);
  assert.deepEqual(schema("revit_create_section_view").required, [
    "name",
    "origin",
    "direction",
    "width",
    "height",
    "depth",
  ]);
  assert.deepEqual(schema("revit_duplicate_view").required, ["view_id", "name"]);
  assert.deepEqual(schema("revit_create_legend").required, ["name"]);
  // view_id and view_ids are both optional in the schema — "one of the two" is
  // not expressible there, so the tool checks it before calling the bridge.
  assert.deepEqual(schema("revit_set_view_scale").required, ["scale"]);
  // dry_run carries a default, so it is not required; the view and the factor are.
  assert.deepEqual(schema("revit_scale_perspective_crop").required, ["view_id", "multiplier"]);
  assert.deepEqual(schema("revit_create_project_parameter").required, ["name"]);
  assert.deepEqual(schema("revit_set_sheet_parameters").required, ["values"]);
  assert.deepEqual(schema("revit_place_views_on_sheets").required, ["placements"]);
  assert.deepEqual(schema("revit_create_schedule").required, ["category", "name", "fields"]);
  assert.deepEqual(schema("revit_create_directshape").required, ["category", "shapes"]);
  assert.deepEqual(schema("revit_place_planting").required, ["points"]);
  // category is the only filter, so listing everything needs nothing.
  assert.equal(schema("revit_list_family_symbols").required, undefined);
  assert.equal(schema("revit_list_views").required, undefined);
  assert.equal(schema("revit_list_legends").required, undefined);
  // template_path carries a default, so only the save path is required.
  assert.deepEqual(schema("revit_new_project").required, ["save_path"]);
  // overwrite defaults to false, so save_as needs nothing but the path.
  assert.deepEqual(schema("revit_save_as").required, ["save_path"]);
  assert.deepEqual(schema("revit_open_project").required, ["path"]);
  assert.deepEqual(schema("revit_set_auto_dismiss").required, ["enabled"]);
  // limit and offset carry defaults, so the model may omit them.
  assert.equal(schema("revit_query_elements").required, undefined);
  // save defaults to false: closing without arguments discards changes.
  assert.equal(schema("revit_close_project").required, undefined);
  assert.equal(schema("revit_save").required, undefined);
  assert.equal(schema("revit_diagnostics").required, undefined);
  assert.equal(schema("revit_reload_bridge").required, undefined);
  await client.close();
});

// --- clampLimit --------------------------------------------------------------

test("clampLimit: caps at the hard max instead of rejecting", () => {
  assert.equal(clampLimit(10000), MAX_QUERY_LIMIT);
  assert.equal(clampLimit(501), MAX_QUERY_LIMIT);
  assert.equal(clampLimit(500), 500);
});

test("clampLimit: passes sane values through and floors to at least 1", () => {
  assert.equal(clampLimit(1), 1);
  assert.equal(clampLimit(100), 100);
  assert.equal(clampLimit(0), 1);
  assert.equal(clampLimit(-5), 1);
  assert.equal(clampLimit(12.7), 12);
});

test("clampLimit: a non-number falls back to the default", () => {
  assert.equal(clampLimit(undefined), DEFAULT_QUERY_LIMIT);
  assert.equal(clampLimit(NaN), DEFAULT_QUERY_LIMIT);
});

// --- revit_query_elements ----------------------------------------------------

test("revit_query_elements: a limit above 500 is clamped before the call", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_query_elements",
    arguments: { category: "Walls", limit: 9999 },
  });
  assert.equal(bridge.calls[0].endpoint, "/query");
  assert.equal(bridge.calls[0].payload.limit, MAX_QUERY_LIMIT);
  await client.close();
});

test("revit_query_elements: omitted limit/offset default to 100/0", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({ name: "revit_query_elements", arguments: {} });
  assert.equal(bridge.calls[0].payload.limit, DEFAULT_QUERY_LIMIT);
  assert.equal(bridge.calls[0].payload.offset, 0);
  await client.close();
});

test("revit_query_elements: filters are forwarded, type_name mapped to typeName", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_query_elements",
    arguments: { category: "Walls", level: "Level 1", type_name: "Generic", limit: 20, offset: 40 },
  });
  assert.deepEqual(bridge.calls[0].payload, {
    category: "Walls",
    level: "Level 1",
    typeName: "Generic",
    limit: 20,
    offset: 40,
  });
  await client.close();
});

test("revit_query_elements: rejects a negative offset and a zero limit", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_query_elements", { offset: -1 });
  await assertRejected(client, bridge, "revit_query_elements", { limit: 0 });
  await assertRejected(client, bridge, "revit_query_elements", { limit: 10.5 });
  await client.close();
});

test("revit_query_elements: the total count reaches the model unchanged", async () => {
  const bridge = fakeBridge(async () => ({ total: 812, offset: 0, limit: 500, rows: [{ id: 1 }] }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_query_elements",
    arguments: { category: "Walls", limit: 500 },
  });
  assert.equal(textOf(result), '{"total":812,"offset":0,"limit":500,"rows":[{"id":1}]}');
  await client.close();
});

// --- read tools with no arguments -------------------------------------------

test("revit_status / levels / categories / selection hit their endpoints", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  for (const name of ["revit_status", "revit_list_levels", "revit_list_categories", "revit_get_selection"]) {
    await client.callTool({ name, arguments: {} });
  }
  assert.deepEqual(
    bridge.calls.map((c) => c.endpoint),
    ["/status", "/levels", "/categories", "/selection"],
  );
  await client.close();
});

// --- revit_get_elements ------------------------------------------------------

test("revit_get_elements: ids and params are forwarded", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_get_elements",
    arguments: { ids: [12345, 12346], params: ["Mark", "Comments"] },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/elements",
    payload: { ids: [12345, 12346], params: ["Mark", "Comments"] },
  });
  await client.close();
});

test("revit_get_elements: rejects missing, empty and non-integer ids", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_get_elements", {});
  await assertRejected(client, bridge, "revit_get_elements", { ids: [] });
  await assertRejected(client, bridge, "revit_get_elements", { ids: ["12345"] });
  await assertRejected(client, bridge, "revit_get_elements", { ids: [1.5] });
  await client.close();
});

// --- write tools -------------------------------------------------------------

test("revit_create_levels: forwards the level specs", async () => {
  const bridge = fakeBridge(async () => ({ created: [{ id: 9, name: "L3", elevation: 20 }] }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_levels",
    arguments: { levels: [{ name: "L3", elevation: 20 }] },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/levels/create",
    payload: { levels: [{ name: "L3", elevation: 20 }] },
  });
  assert.match(textOf(result), /"created"/);
  await client.close();
});

test("revit_create_levels: rejects an empty batch and a nameless level", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_create_levels", { levels: [] });
  await assertRejected(client, bridge, "revit_create_levels", { levels: [{ elevation: 10 }] });
  await assertRejected(client, bridge, "revit_create_levels", { levels: [{ name: "", elevation: 10 }] });
  await assertRejected(client, bridge, "revit_create_levels", { levels: [{ name: "L3", elevation: "20" }] });
  await client.close();
});

test("revit_create_walls: forwards level, height and curves, wall_type mapped to wallType", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  const args = {
    level: "Level 1",
    wall_type: "Generic - 200mm",
    height: 10,
    curves: [{ start: { x: 0, y: 0 }, end: { x: 30, y: 0 } }],
  };
  await client.callTool({ name: "revit_create_walls", arguments: args });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/walls/create",
    payload: {
      level: "Level 1",
      wallType: "Generic - 200mm",
      height: 10,
      curves: [{ start: { x: 0, y: 0 }, end: { x: 30, y: 0 } }],
    },
  });
  await client.close();
});

test("revit_create_walls: rejects no curves, a half curve and a non-positive height", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  const base = { level: "Level 1", wall_type: "Generic - 200mm", height: 10 };
  await assertRejected(client, bridge, "revit_create_walls", { ...base, curves: [] });
  await assertRejected(client, bridge, "revit_create_walls", {
    ...base,
    curves: [{ start: { x: 0, y: 0 } }],
  });
  await assertRejected(client, bridge, "revit_create_walls", {
    ...base,
    curves: [{ start: { x: 0 }, end: { x: 1, y: 1 } }],
  });
  await assertRejected(client, bridge, "revit_create_walls", {
    ...base,
    height: 0,
    curves: [{ start: { x: 0, y: 0 }, end: { x: 1, y: 1 } }],
  });
  await assertRejected(client, bridge, "revit_create_walls", {
    level: "Level 1",
    height: 10,
    curves: [{ start: { x: 0, y: 0 }, end: { x: 1, y: 1 } }],
  });
  await client.close();
});

test("revit_set_parameters: accepts string, number and boolean values", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  for (const value of ["seen by MCP", 12.5, true]) {
    await client.callTool({
      name: "revit_set_parameters",
      arguments: { ids: [1001], name: "Comments", value },
    });
  }
  assert.deepEqual(
    bridge.calls.map((c) => c.payload.value),
    ["seen by MCP", 12.5, true],
  );
  assert.equal(bridge.calls[0].endpoint, "/parameters/set");
  await client.close();
});

test("revit_set_parameters: parameter_id is left out unless given, and forwarded as parameterId", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await client.callTool({
    name: "revit_set_parameters",
    arguments: { ids: [220545], name: "Level", value: 245866 },
  });
  assert.deepEqual(bridge.calls[0].payload, { ids: [220545], name: "Level", value: 245866 });

  await client.callTool({
    name: "revit_set_parameters",
    arguments: { ids: [220545], name: "Level", value: 245866, parameter_id: -1002062 },
  });
  assert.deepEqual(bridge.calls[1].payload, {
    ids: [220545],
    name: "Level",
    value: 245866,
    parameterId: -1002062,
  });
  await client.close();
});

test("revit_set_parameters: an ambiguous name is an error naming the ids, never a guess", async () => {
  // The live bug: element 220545 carries two parameters called "Level", one of
  // them read-only. LookupParameter picked that one and the write was refused
  // while the writable one sat untouched.
  const bridge = fakeBridge(async () => {
    throw new Error(
      'Revit failed on /parameters/set: AMBIGUOUS_PARAMETER — Element 220545 has 2 instance parameters named "Level": ' +
        "id -1001352 (FAMILY_LEVEL_PARAM, read-only, currently Cota do jardim); " +
        "id -1002062 (SCHEDULE_LEVEL_PARAM, writable, currently Cota do jardim). Nothing was written.",
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_parameters",
    arguments: { ids: [220545], name: "Level", value: 245866 },
  });
  assert.equal(result.isError, true);
  assert.match(textOf(result), /AMBIGUOUS_PARAMETER/);
  assert.match(textOf(result), /-1001352/);
  assert.match(textOf(result), /-1002062/);
  assert.match(textOf(result), /Nothing was written/);
  await client.close();
});

test("revit_set_parameters: rejects a missing value, empty name and empty ids", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_set_parameters", { ids: [1], name: "Comments" });
  await assertRejected(client, bridge, "revit_set_parameters", { ids: [1], name: "", value: "x" });
  await assertRejected(client, bridge, "revit_set_parameters", { ids: [], name: "Comments", value: "x" });
  await assertRejected(client, bridge, "revit_set_parameters", {
    ids: [1],
    name: "Comments",
    value: { nested: true },
  });
  // A parameter id is one of Revit's, not a decimal.
  await assertRejected(client, bridge, "revit_set_parameters", {
    ids: [1],
    name: "Level",
    value: 2,
    parameter_id: -1002062.5,
  });
  await client.close();
});

test("revit_delete_elements: forwards ids, rejects an empty list", async () => {
  const bridge = fakeBridge(async () => ({ deleted: 3, ids: [1, 2, 3] }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_delete_elements",
    arguments: { ids: [1] },
  });
  assert.deepEqual(bridge.calls[0], { endpoint: "/elements/delete", payload: { ids: [1] } });
  assert.equal(textOf(result), '{"deleted":3,"ids":[1,2,3]}');
  await assertRejected(client, bridge, "revit_delete_elements", { ids: [] });
  await client.close();
});

// --- bridge failures reaching the model --------------------------------------

test("a bridge failure comes back as a readable Error, not a crash", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error("Cannot reach Revit at http://localhost:48884/revit-mcp — connection refused.");
  });
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_status", arguments: {} });
  assert.equal(result.isError, undefined);
  assert.match(textOf(result), /^Error: Cannot reach Revit/);
  await client.close();
});

test("a bridge failure on a write tool is reported, not swallowed", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error("Revit failed on /levels/create: The name entered is already in use.");
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_levels",
    arguments: { levels: [{ name: "Level 1", elevation: 0 }] },
  });
  assert.match(textOf(result), /already in use/);
  await client.close();
});
