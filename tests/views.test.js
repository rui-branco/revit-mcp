import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
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

const SECTION = {
  name: "Section A-A",
  origin: { x: 0, y: 0, z: 0 },
  direction: { x: 0, y: 1 },
  width: 100,
  height: 30,
  depth: 60,
};

// --- revit_list_views --------------------------------------------------------

test("revit_list_views: hits its endpoint", async () => {
  const bridge = fakeBridge(async () => []);
  const client = await connect(bridge);
  await client.callTool({ name: "revit_list_views", arguments: {} });
  assert.deepEqual(
    bridge.calls.map((c) => c.endpoint),
    ["/views"],
  );
  await client.close();
});

test("revit_list_views: isPlacedOnSheet comes through so a caller can tell what is free", async () => {
  const bridge = fakeBridge(async () => [
    { id: 1, name: "Level 1", viewType: "FloorPlan", isTemplate: false, isPlacedOnSheet: true },
    { id: 2, name: "Site", viewType: "FloorPlan", isTemplate: false, isPlacedOnSheet: false },
  ]);
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_list_views", arguments: {} });
  const rows = JSON.parse(textOf(result));
  assert.equal(rows.filter((row) => !row.isPlacedOnSheet).length, 1);
  await client.close();
});

test("revit_list_views: a view's frame comes through when it has one", async () => {
  const bridge = fakeBridge(async () => [
    {
      id: 7001,
      name: "Section A-A",
      viewType: "Section",
      isTemplate: false,
      isPlacedOnSheet: false,
      viewDirection: { x: 0, y: -1, z: 0 },
      rightDirection: { x: 1, y: 0, z: 0 },
      upDirection: { x: 0, y: 0, z: 1 },
    },
    { id: 7002, name: "Quantities", viewType: "Schedule", isTemplate: false, isPlacedOnSheet: true },
  ]);
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_list_views", arguments: {} });
  const rows = JSON.parse(textOf(result));
  assert.deepEqual(rows[0].viewDirection, { x: 0, y: -1, z: 0 });
  // A schedule has no frame at all, so it carries no direction fields.
  assert.equal(rows[1].viewDirection, undefined);
  await client.close();
});

// --- revit_create_plan_view --------------------------------------------------

test("revit_create_plan_view: forwards level, name and scale", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_plan_view",
    arguments: { level: "Level 1", name: "Ground Floor Plan", scale: 100 },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/views/create-plan",
    // viewFamilyType rides along unset: omitted, the bridge picks the first
    // floor plan type, which is what this tool has always done.
    payload: { level: "Level 1", name: "Ground Floor Plan", viewFamilyType: undefined, scale: 100 },
  });
  await client.close();
});

test("revit_create_plan_view: view_family_type maps to viewFamilyType, so a Site plan is reachable", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_plan_view",
    arguments: { level: "Level 1", name: "Site Plan", view_family_type: "Site", scale: 500 },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/views/create-plan",
    payload: { level: "Level 1", name: "Site Plan", viewFamilyType: "Site", scale: 500 },
  });
  await client.close();
});

test("revit_create_plan_view: an unknown view family type comes back with the names that exist", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      'Revit failed on /views/create-plan: Unknown plan view family type "Sight". Plan view family types in this document: Ceiling Plan, Floor Plan, Site.',
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_plan_view",
    arguments: { level: "Level 1", name: "Site Plan", view_family_type: "Sight" },
  });
  assert.match(textOf(result), /Unknown plan view family type/);
  assert.match(textOf(result), /Ceiling Plan, Floor Plan, Site/);
  await client.close();
});

test("revit_create_plan_view: the scale reported is the one the view ended up with", async () => {
  // Asked 100, got 50: a view template on the type owns the scale. The bridge
  // reads it back off the created view rather than echoing the request, and the
  // difference has to survive the trip out.
  const bridge = fakeBridge(async () => ({
    id: 556,
    name: "Ground Floor Plan",
    viewType: "FloorPlan",
    viewFamilyType: "Floor Plan",
    level: "Level 1",
    scale: 50,
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_plan_view",
    arguments: { level: "Level 1", name: "Ground Floor Plan", scale: 100 },
  });
  assert.equal(JSON.parse(textOf(result)).scale, 50);
  await client.close();
});

test("revit_create_plan_view: an omitted scale is left for the bridge to resolve", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_plan_view",
    arguments: { level: "Level 1", name: "Ground Floor Plan" },
  });
  assert.equal(bridge.calls[0].payload.scale, undefined);
  await client.close();
});

test("revit_create_plan_view: rejects an empty name, empty level and a fractional scale", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_create_plan_view", {});
  await assertRejected(client, bridge, "revit_create_plan_view", { level: "Level 1" });
  await assertRejected(client, bridge, "revit_create_plan_view", { level: "", name: "Plan" });
  await assertRejected(client, bridge, "revit_create_plan_view", { level: "Level 1", name: "" });
  await assertRejected(client, bridge, "revit_create_plan_view", {
    level: "Level 1",
    name: "Plan",
    scale: 1.5,
  });
  await assertRejected(client, bridge, "revit_create_plan_view", {
    level: "Level 1",
    name: "Plan",
    scale: 0,
  });
  await client.close();
});

test("revit_create_plan_view: a name collision comes back renamed, not as an error", async () => {
  const bridge = fakeBridge(async () => ({
    id: 555,
    name: "Ground Floor Plan 2",
    viewType: "FloorPlan",
    level: "Level 1",
    scale: 100,
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_plan_view",
    arguments: { level: "Level 1", name: "Ground Floor Plan" },
  });
  assert.equal(result.isError, undefined);
  assert.equal(JSON.parse(textOf(result)).name, "Ground Floor Plan 2");
  await client.close();
});

// --- revit_create_drafting_view ----------------------------------------------

test("revit_create_drafting_view: forwards name and scale to its own endpoint", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_drafting_view",
    arguments: { name: "Pormenor 1 - Pavimento", scale: 20 },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/views/create-drafting",
    payload: { name: "Pormenor 1 - Pavimento", scale: 20 },
  });
  await client.close();
});

test("revit_create_drafting_view: an omitted scale is left for the bridge to resolve", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_drafting_view",
    arguments: { name: "Pormenor 2" },
  });
  assert.equal(bridge.calls[0].payload.scale, undefined);
  await client.close();
});

test("revit_create_drafting_view: rejects an empty name and a fractional scale", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_create_drafting_view", {});
  await assertRejected(client, bridge, "revit_create_drafting_view", { name: "" });
  await assertRejected(client, bridge, "revit_create_drafting_view", { name: "P1", scale: 1.5 });
  await assertRejected(client, bridge, "revit_create_drafting_view", { name: "P1", scale: 0 });
  await client.close();
});

test("revit_create_drafting_view: a name collision comes back renamed, not as an error", async () => {
  const bridge = fakeBridge(async () => ({
    id: 9001,
    name: "Pormenor 1 2",
    viewType: "DraftingView",
    scale: 20,
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_drafting_view",
    arguments: { name: "Pormenor 1" },
  });
  assert.equal(result.isError, undefined);
  assert.equal(JSON.parse(textOf(result)).name, "Pormenor 1 2");
  await client.close();
});

// --- revit_create_3d_view ----------------------------------------------------

test("revit_create_3d_view: eye and target reach the bridge unconverted", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_3d_view",
    arguments: {
      name: "Garden Eye",
      eye: { x: 93, y: 6, z: 7 },
      target: { x: 52, y: 60, z: 8 },
      perspective: true,
    },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/views/create-3d",
    payload: {
      name: "Garden Eye",
      eye: { x: 93, y: 6, z: 7 },
      target: { x: 52, y: 60, z: 8 },
      perspective: true,
      scale: undefined,
    },
  });
  await client.close();
});

test("revit_create_3d_view: no camera at all is left for the bridge to default", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({ name: "revit_create_3d_view", arguments: { name: "Overview" } });
  assert.equal(bridge.calls[0].payload.eye, undefined);
  assert.equal(bridge.calls[0].payload.target, undefined);
  assert.equal(bridge.calls[0].payload.perspective, undefined);
  await client.close();
});

test("revit_create_3d_view: rejects a missing name and a point missing a coordinate", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_create_3d_view", {});
  await assertRejected(client, bridge, "revit_create_3d_view", { name: "" });
  // A camera point is fully three-dimensional; z is not optional on one.
  await assertRejected(client, bridge, "revit_create_3d_view", {
    name: "Eye",
    eye: { x: 0, y: 0 },
    target: { x: 1, y: 1, z: 1 },
  });
  await client.close();
});

test("revit_create_3d_view: modelExtents comes back as data for aiming the next one", async () => {
  const bridge = fakeBridge(async () => ({
    id: 212657,
    name: "Overview",
    isPerspective: false,
    modelExtents: {
      min: { x: -1.39, y: -2.22, z: -4.6 },
      max: { x: 100.75, y: 83.95, z: 29 },
      center: { x: 49.68, y: 40.86, z: 12.2 },
    },
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_3d_view",
    arguments: { name: "Overview" },
  });
  assert.equal(result.isError, undefined);
  assert.equal(JSON.parse(textOf(result)).modelExtents.max.x, 100.75);
  await client.close();
});

// --- revit_set_view_style ----------------------------------------------------

test("revit_set_view_style: view_id and detail_level map to camelCase", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_set_view_style",
    arguments: { view_id: 212657, style: "Realistic", detail_level: "Fine" },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/views/set-style",
    payload: {
      viewId: 212657,
      style: "Realistic",
      detailLevel: "Fine",
      shadows: undefined,
    },
  });
  await client.close();
});

test("revit_set_view_style: rejects a missing style and a non-integer view_id", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_set_view_style", { view_id: 1 });
  await assertRejected(client, bridge, "revit_set_view_style", { view_id: 1, style: "" });
  await assertRejected(client, bridge, "revit_set_view_style", { view_id: 1.5, style: "Realistic" });
  await client.close();
});

test("revit_set_view_style: shadows is forwarded so the bridge can redirect it, not dropped here", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      "Revit failed on /views/set-style: SHADOWS_HANDLED_ELSEWHERE — send \"shadows\" to /revit-mcp/views/set-graphics instead.",
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_view_style",
    arguments: { view_id: 1, style: "Realistic", shadows: true },
  });
  assert.equal(bridge.calls[0].payload.shadows, true);
  assert.match(textOf(result), /SHADOWS_HANDLED_ELSEWHERE/);
  assert.match(textOf(result), /set-graphics/);
  await client.close();
});

test("revit_set_view_style: its description points at the shadows tool instead of claiming Revit has no switch", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  const style = tools.find((t) => t.name === "revit_set_view_style").description;
  const sun = tools.find((t) => t.name === "revit_set_view_sun").description;

  // The old wording said cast shadows cannot be set at all. That was a claim
  // about the whole API made from one view's parameters, and it is not the
  // bridge's to make — the graphics endpoint probes the live view instead.
  assert.doesNotMatch(style, /SHADOWS_NOT_EXPOSED|does not expose/i);
  assert.match(style, /revit_set_view_graphics/);
  assert.doesNotMatch(sun, /does not expose/i);
  assert.match(sun, /revit_set_view_graphics/);
  await client.close();
});

// --- revit_set_view_background -----------------------------------------------

test("revit_set_view_background: view_id and the colour names map to camelCase", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_set_view_background",
    arguments: {
      view_id: 220554,
      kind: "gradient",
      sky_color: { r: 62, g: 128, b: 200 },
      horizon_color: { r: 205, g: 228, b: 245 },
      ground_color: { r: 140, g: 128, b: 110 },
    },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/views/set-background",
    payload: {
      viewId: 220554,
      kind: "gradient",
      skyColor: { r: 62, g: 128, b: 200 },
      horizonColor: { r: 205, g: 228, b: 245 },
      groundColor: { r: 140, g: 128, b: 110 },
      imagePath: undefined,
    },
  });
  await client.close();
});

test("revit_set_view_background: kind sky needs nothing else", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_set_view_background",
    arguments: { view_id: 1, kind: "sky" },
  });
  assert.deepEqual(bridge.calls[0].payload, {
    viewId: 1,
    kind: "sky",
    skyColor: undefined,
    horizonColor: undefined,
    groundColor: undefined,
    imagePath: undefined,
  });
  await client.close();
});

test("revit_set_view_background: rejects an unknown kind and an out-of-range channel", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_set_view_background", { view_id: 1, kind: "clouds" });
  await assertRejected(client, bridge, "revit_set_view_background", { view_id: 1 });
  await assertRejected(client, bridge, "revit_set_view_background", {
    view_id: 1,
    kind: "gradient",
    sky_color: { r: 300, g: 0, b: 0 },
  });
  await client.close();
});

test("revit_set_view_background: a colour passed with kind sky is forwarded so the bridge can refuse it", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error("Revit failed on /views/set-background: BAD_REQUEST CreateSky() takes no parameters");
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_view_background",
    arguments: { view_id: 1, kind: "sky", sky_color: { r: 1, g: 2, b: 3 } },
  });
  assert.deepEqual(bridge.calls[0].payload.skyColor, { r: 1, g: 2, b: 3 });
  assert.match(textOf(result), /CreateSky/);
  await client.close();
});

test("revit_set_view_background: the background is reported back as Revit's own enum name", async () => {
  const bridge = fakeBridge(async () => ({
    viewId: 220554,
    name: "Garden",
    viewType: "ThreeD",
    kind: "SunAndClouds",
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_view_background",
    arguments: { view_id: 220554, kind: "sky" },
  });
  // Asking for "sky" reports "SunAndClouds": that is Revit's word for it, and the
  // tool must not launder the read-back into the word that was asked for.
  assert.equal(JSON.parse(textOf(result)).kind, "SunAndClouds");
  await client.close();
});

// --- revit_hide_view_categories ----------------------------------------------

test("revit_hide_view_categories: view_id maps to camelCase and hidden defaults to the bridge", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_hide_view_categories",
    arguments: { view_id: 220554, categories: ["Levels", "OST_Grids"] },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/views/hide-categories",
    payload: { viewId: 220554, categories: ["Levels", "OST_Grids"], hidden: undefined },
  });
  await client.close();
});

test("revit_hide_view_categories: the annotation shorthand goes through untouched", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_hide_view_categories",
    arguments: { view_id: 1, categories: ["annotation"], hidden: false },
  });
  assert.deepEqual(bridge.calls[0].payload, {
    viewId: 1,
    categories: ["annotation"],
    hidden: false,
  });
  await client.close();
});

test("revit_hide_view_categories: rejects an empty category list", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_hide_view_categories", { view_id: 1, categories: [] });
  await assertRejected(client, bridge, "revit_hide_view_categories", { view_id: 1 });
  await assertRejected(client, bridge, "revit_hide_view_categories", {
    view_id: 1,
    categories: ["Levels"],
    hidden: "yes",
  });
  await client.close();
});

test("revit_hide_view_categories: a refused category comes back as its own row, the rest still hide", async () => {
  const bridge = fakeBridge(async () => ({
    viewId: 1,
    name: "Iso",
    viewType: "ThreeD",
    categories: [
      { category: "Levels", hidden: true },
      {
        category: "SunPath",
        hidden: false,
        skipped: true,
        reason: "Revit reports CanCategoryBeHidden false for OST_SunPath1",
      },
    ],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_hide_view_categories",
    arguments: { view_id: 1, categories: ["Levels", "SunPath"] },
  });
  const rows = JSON.parse(textOf(result)).categories;
  assert.equal(rows.filter((row) => row.hidden).length, 1);
  assert.equal(rows.filter((row) => row.skipped).length, 1);
  await client.close();
});

// --- revit_override_view_categories ------------------------------------------

const OVERRIDE_PLAN = {
  dryRun: true,
  applied: false,
  viewId: 220554,
  name: "Site Plan",
  viewType: "FloorPlan",
  template: null,
  categories: [
    {
      category: "Planting",
      categoryId: -2001360,
      categoryType: "Model",
      hidden: false,
      requested: { projectionColor: { r: 90, g: 150, b: 70 } },
      before: {
        projectionLineColor: null,
        projectionLineWeight: null,
        halftone: false,
        surfaceTransparency: 0,
        surfaceForegroundPatternId: 5100,
        surfaceForegroundPatternVisible: true,
      },
      would: {
        projectionLineColor: { r: 90, g: 150, b: 70 },
        projectionLineWeight: null,
        halftone: false,
        surfaceTransparency: 0,
        surfaceForegroundPatternId: 5100,
        surfaceForegroundPatternVisible: true,
      },
    },
  ],
};

test("revit_override_view_categories: every setting maps to camelCase and dry_run defaults true", async () => {
  const bridge = fakeBridge(async () => OVERRIDE_PLAN);
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_override_view_categories",
    arguments: {
      view_id: 220554,
      overrides: [
        {
          category: "Planting",
          projection_color: { r: 90, g: 150, b: 70 },
          cut_color: { r: 40, g: 80, b: 30 },
          projection_line_weight: 2,
          cut_line_weight: 4,
          halftone: false,
          surface_transparency: 20,
        },
        { category: "OST_Roads", halftone: true, projection_line_weight: -1 },
      ],
    },
  });

  assert.deepEqual(bridge.calls[0], {
    endpoint: "/views/override-categories",
    payload: {
      viewId: 220554,
      overrides: [
        {
          category: "Planting",
          projectionColor: { r: 90, g: 150, b: 70 },
          cutColor: { r: 40, g: 80, b: 30 },
          projectionLineWeight: 2,
          cutLineWeight: 4,
          halftone: false,
          surfaceTransparency: 20,
        },
        {
          category: "OST_Roads",
          projectionColor: undefined,
          cutColor: undefined,
          projectionLineWeight: -1,
          cutLineWeight: undefined,
          halftone: true,
          surfaceTransparency: undefined,
        },
      ],

      // The argument nobody passes and the drawing depends on.
      dryRun: true,
    },
  });
  await client.close();
});

test("revit_override_view_categories: dry_run false is passed through", async () => {
  const bridge = fakeBridge(async () => ({ ...OVERRIDE_PLAN, dryRun: false, applied: true }));
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_override_view_categories",
    arguments: {
      view_id: 220554,
      overrides: [{ category: "Planting", halftone: true }],
      dry_run: false,
    },
  });
  assert.equal(bridge.calls[0].payload.dryRun, false);
  await client.close();
});

test("revit_override_view_categories: a row with no setting never reaches the bridge", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_override_view_categories",
    arguments: { view_id: 1, overrides: [{ category: "Planting" }] },
  });
  assert.match(textOf(result), /names no setting/);
  assert.equal(bridge.calls.length, 0);
  await client.close();
});

test("revit_override_view_categories: out-of-range values are refused before the call", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  await assertRejected(client, bridge, "revit_override_view_categories", { view_id: 1 });
  await assertRejected(client, bridge, "revit_override_view_categories", {
    view_id: 1,
    overrides: [],
  });
  await assertRejected(client, bridge, "revit_override_view_categories", {
    view_id: 1,
    overrides: [{ category: "Planting", projection_line_weight: 17 }],
  });
  await assertRejected(client, bridge, "revit_override_view_categories", {
    view_id: 1,
    overrides: [{ category: "Planting", surface_transparency: 101 }],
  });
  await assertRejected(client, bridge, "revit_override_view_categories", {
    view_id: 1,
    overrides: [{ category: "Planting", projection_color: { r: 256, g: 0, b: 0 } }],
  });
  await assertRejected(client, bridge, "revit_override_view_categories", {
    view_id: 1,
    overrides: [{ category: "Planting", halftone: "yes" }],
  });
  await client.close();
});

test("revit_override_view_categories: the dry run shows the merge keeping the fill pattern", async () => {
  const bridge = fakeBridge(async () => OVERRIDE_PLAN);
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_override_view_categories",
    arguments: {
      view_id: 220554,
      overrides: [{ category: "Planting", projection_color: { r: 90, g: 150, b: 70 } }],
    },
  });
  const row = JSON.parse(textOf(result)).categories[0];

  assert.equal(row.applied, undefined);
  assert.deepEqual(row.would.projectionLineColor, { r: 90, g: 150, b: 70 });

  // The whole point of merging rather than replacing.
  assert.equal(row.would.surfaceForegroundPatternId, row.before.surfaceForegroundPatternId);
  assert.equal(row.would.surfaceForegroundPatternVisible, true);
  await client.close();
});

test("revit_override_view_categories: a template-owned view is reported, not silently applied", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      'Revit failed on /views/override-categories: View template "Site - Presentation" (220100) owns the Visibility/Graphics overrides for Planting (VIS_GRAPHICS_MODEL). Nothing was changed.',
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_override_view_categories",
    arguments: {
      view_id: 220554,
      overrides: [{ category: "Planting", halftone: true }],
      dry_run: false,
    },
  });
  assert.match(textOf(result), /owns the Visibility\/Graphics overrides/);
  assert.match(textOf(result), /Nothing was changed/);
  await client.close();
});

test("revit_override_view_categories: the tool is registered and says what it merges and refuses", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  const tool = tools.find((t) => t.name === "revit_override_view_categories");
  assert.ok(tool);
  assert.match(tool.description, /MERGED/);
  assert.match(tool.description, /OVERRIDES_CONTROLLED_BY_TEMPLATE/);
  assert.match(tool.description, /dry_run DEFAULTS TO TRUE/);
  assert.deepEqual(tool.inputSchema.required, ["view_id", "overrides"]);
  await client.close();
});

// --- revit_set_view_sun ------------------------------------------------------

test("revit_set_view_sun: azimuth and altitude go through in degrees", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_set_view_sun",
    arguments: { view_id: 220554, azimuth: 135, altitude: 40 },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/views/set-sun",
    payload: { viewId: 220554, azimuth: 135, altitude: 40, date: undefined, time: undefined },
  });
  await client.close();
});

test("revit_set_view_sun: date and time go through as strings", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_set_view_sun",
    arguments: { view_id: 1, date: "2026-06-21", time: "15:30" },
  });
  assert.deepEqual(bridge.calls[0].payload, {
    viewId: 1,
    azimuth: undefined,
    altitude: undefined,
    date: "2026-06-21",
    time: "15:30",
  });
  await client.close();
});

test("revit_set_view_sun: neither route, or both at once, never reaches the bridge", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);

  const none = await client.callTool({ name: "revit_set_view_sun", arguments: { view_id: 1 } });
  assert.match(textOf(none), /pass azimuth/i);

  const both = await client.callTool({
    name: "revit_set_view_sun",
    arguments: { view_id: 1, azimuth: 135, date: "2026-06-21" },
  });
  assert.match(textOf(both), /not both/i);

  assert.equal(bridge.calls.length, 0);
  await client.close();
});

test("revit_set_view_sun: rejects an azimuth or altitude outside Revit's range", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_set_view_sun", { view_id: 1, azimuth: 400 });
  await assertRejected(client, bridge, "revit_set_view_sun", { view_id: 1, altitude: 120 });
  await assertRejected(client, bridge, "revit_set_view_sun", { view_id: 1.5, azimuth: 90 });
  await client.close();
});

test("revit_set_view_sun: sharesSettings and sunSettingsId come through, so a shared sun is visible", async () => {
  const bridge = fakeBridge(async () => ({
    viewId: 1,
    name: "Iso",
    viewType: "ThreeD",
    sunSettingsId: 220556,
    sharesSettings: true,
    sunAndShadowType: "Lighting",
    relativeToView: false,
    azimuth: 135,
    altitude: 40,
    dateAndTime: "2026-06-21 14:30",
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_view_sun",
    arguments: { view_id: 1, azimuth: 135, altitude: 40 },
  });
  const report = JSON.parse(textOf(result));
  assert.equal(report.sharesSettings, true);
  assert.equal(report.sunSettingsId, 220556);
  await client.close();
});

// --- revit_export_view_image -------------------------------------------------

test("revit_export_view_image: one view, with the defaults left to the bridge", async () => {
  const bridge = fakeBridge(async () => ({ viewId: 1, path: "C:\\out\\x.png" }));
  const client = await connect(bridge);
  await client.callTool({ name: "revit_export_view_image", arguments: { view_id: 212657 } });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/views/export-image",
    payload: {
      viewId: 212657,
      viewIds: undefined,
      path: undefined,
      width: undefined,
      height: undefined,
      format: undefined,
    },
  });
  await client.close();
});

test("revit_export_view_image: view_ids wins and view_id is not sent alongside it", async () => {
  const bridge = fakeBridge(async () => ({ exported: [] }));
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_export_view_image",
    arguments: { view_id: 1, view_ids: [2, 3], path: "C:\\renders", width: 2400, format: "PNG" },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/views/export-image",
    payload: {
      viewId: undefined,
      viewIds: [2, 3],
      path: "C:\\renders",
      width: 2400,
      height: undefined,
      format: "PNG",
    },
  });
  await client.close();
});

test("revit_export_view_image: neither view_id nor view_ids never reaches the bridge", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_export_view_image", arguments: {} });
  assert.match(textOf(result), /pass 'view_id'/);
  assert.equal(bridge.calls.length, 0);
  await client.close();
});

test("revit_export_view_image: rejects an empty view_ids and a zero width", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_export_view_image", { view_ids: [] });
  await assertRejected(client, bridge, "revit_export_view_image", { view_id: 1, width: 0 });
  await assertRejected(client, bridge, "revit_export_view_image", { view_id: 1.5 });
  await client.close();
});

test("revit_export_view_image: the real path Revit wrote comes back, not the one asked for", async () => {
  const bridge = fakeBridge(async () => ({
    viewId: 212668,
    viewName: "MCP Garden Eye",
    path: "C:\\Users\\me\\RevitProjects\\renders\\moradia - 3D View - MCP Garden Eye.png",
    requestedPath: "C:\\Users\\me\\RevitProjects\\renders\\moradia.png",
    bytes: 2484355,
    width: 1600,
    height: 1200,
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_export_view_image",
    arguments: { view_id: 212668 },
  });
  const payload = JSON.parse(textOf(result));
  assert.notEqual(payload.path, payload.requestedPath);
  assert.match(payload.path, /MCP Garden Eye\.png$/);
  assert.equal(payload.bytes, 2484355);
  await client.close();
});

// --- revit_create_section_view -----------------------------------------------

test("revit_create_section_view: forwards the whole section geometry unchanged", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({ name: "revit_create_section_view", arguments: { ...SECTION, scale: 50 } });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/views/create-section",
    payload: { ...SECTION, scale: 50 },
  });
  await client.close();
});

test("revit_create_section_view: direction reaches the bridge with its sign untouched", async () => {
  // The look-direction fix lives C#-side, in the section box transform. Node
  // forwards 'direction' verbatim and must keep doing so: a sign flip here as
  // well would put every section straight back the wrong way round.
  const bridge = fakeBridge();
  const client = await connect(bridge);
  const directions = [
    { x: 0, y: 1 },
    { x: 0, y: -1 },
    { x: 1, y: 0 },
    { x: 0, y: 5 },
  ];
  for (const direction of directions) {
    await client.callTool({ name: "revit_create_section_view", arguments: { ...SECTION, direction } });
  }
  assert.deepEqual(
    bridge.calls.map((call) => call.endpoint),
    directions.map(() => "/views/create-section"),
  );
  assert.deepEqual(
    bridge.calls.map((call) => call.payload.direction),
    directions,
  );
  await client.close();
});

test("revit_create_section_view: rejects a zero or negative width, height or depth", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_create_section_view", { ...SECTION, width: 0 });
  await assertRejected(client, bridge, "revit_create_section_view", { ...SECTION, height: -1 });
  await assertRejected(client, bridge, "revit_create_section_view", { ...SECTION, depth: 0 });
  await client.close();
});

test("revit_create_section_view: rejects a missing origin z and a half-given direction", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_create_section_view", {});
  // origin is a model point, so all three coordinates are required.
  await assertRejected(client, bridge, "revit_create_section_view", {
    ...SECTION,
    origin: { x: 0, y: 0 },
  });
  // direction is horizontal by definition, and both components are needed.
  await assertRejected(client, bridge, "revit_create_section_view", {
    ...SECTION,
    direction: { x: 0 },
  });
  await assertRejected(client, bridge, "revit_create_section_view", { ...SECTION, name: "" });
  await client.close();
});

test("revit_create_section_view: the created view's own directions come back, so the frame is assertable", async () => {
  // viewDirection is Revit's direction towards the VIEWER, so a section asked
  // to look at {x: 0, y: 1} reports {x: 0, y: -1, z: 0} — the assertion, not a bug.
  // Those are the numbers a live Revit 2027 gives back for SECTION, measured, with
  // rightDirection {1, 0, 0}: east, the right hand of a viewer facing north.
  const bridge = fakeBridge(async () => ({
    id: 7001,
    name: "Section A-A",
    viewType: "Section",
    scale: 50,
    viewDirection: { x: 0, y: -1, z: 0 },
    rightDirection: { x: 1, y: 0, z: 0 },
    upDirection: { x: 0, y: 0, z: 1 },
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_section_view",
    arguments: SECTION,
  });
  const payload = JSON.parse(textOf(result));
  assert.deepEqual(payload.viewDirection, { x: 0, y: -1, z: 0 });
  assert.deepEqual(payload.rightDirection, { x: 1, y: 0, z: 0 });
  assert.deepEqual(payload.upDirection, { x: 0, y: 0, z: 1 });
  await client.close();
});

test("revit_create_section_view: the measured crop comes back beside the requested origin", async () => {
  // Every number below is the readback measured off live view 247787: origin
  // (53, 40, 8.5), looking along +X, width 86, height 33, depth 8, created with
  // the depth interval the right way round. The section cuts ON 53 and sees
  // forward to 61; the bug cut at 45 and looked back from there. The tool must
  // hand all of it back untouched — a dropped cutPlaneOrigin or modelBounds is
  // an offset regression nobody can see.
  const bridge = fakeBridge(async () => ({
    id: 247787,
    name: "Section A-A",
    viewType: "Section",
    scale: 50,
    requestedOrigin: { x: 53, y: 40, z: 8.5 },
    viewDirection: { x: -1, y: 0, z: 0 },
    rightDirection: { x: 0, y: -1, z: 0 },
    upDirection: { x: 0, y: 0, z: 1 },
    // A corner, and nothing like the asked origin: view.Origin is its own
    // property and is read as one.
    viewOrigin: { x: 53, y: 83, z: -8 },
    // Revit rewrote the frame it was handed: origin moved to a corner and basisZ
    // turned back at the viewer, so basisZ equals viewDirection, not the way the
    // section looks.
    cropTransform: {
      origin: { x: 53, y: 83, z: -8 },
      basisX: { x: 0, y: -1, z: 0 },
      basisY: { x: 0, y: 0, z: 1 },
      basisZ: { x: -1, y: 0, z: 0 },
    },
    cropLocalBounds: { min: { x: 0, y: 0, z: -8 }, max: { x: 86, y: 33, z: 0 } },
    modelBounds: { min: { x: 53, y: -3, z: -8 }, max: { x: 61, y: 83, z: 25 } },
    cutPlaneOrigin: { x: 53, y: 40, z: 8.5 },
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_section_view",
    arguments: {
      name: "Section A-A",
      origin: { x: 53, y: 40, z: 8.5 },
      direction: { x: 1, y: 0 },
      width: 86,
      height: 33,
      depth: 8,
    },
  });
  const payload = JSON.parse(textOf(result));

  // The one assertion an offset regression cannot survive.
  assert.deepEqual(payload.requestedOrigin, { x: 53, y: 40, z: 8.5 });
  assert.deepEqual(payload.cutPlaneOrigin, payload.requestedOrigin);

  // The view volume runs origin -> origin + direction * depth: 53..61 along the
  // look direction, never 45..53.
  assert.equal(payload.modelBounds.min.x, 53);
  assert.equal(payload.modelBounds.max.x, 61);

  // viewOrigin is measured, not the request echoed back, and on a section it is
  // a corner of the crop rather than the point the cut passes through.
  assert.deepEqual(payload.viewOrigin, { x: 53, y: 83, z: -8 });
  assert.notDeepEqual(payload.viewOrigin, payload.requestedOrigin);

  // basisZ points back at the viewer, which is why the cut is the Max.Z face
  // here, and the local pair is the same -8..0 the BUGGY view reported — it is
  // carried to be seen, never to be judged.
  assert.deepEqual(payload.cropTransform.basisZ, payload.viewDirection);
  assert.deepEqual(payload.cropLocalBounds.min, { x: 0, y: 0, z: -8 });
  assert.deepEqual(payload.cropLocalBounds.max, { x: 86, y: 33, z: 0 });
  await client.close();
});

// Source contract. The depth interval only shows itself against a live Revit —
// the readback above is what a caller sees, but what puts the cut on the asked
// origin is the pair below, and a sign flip there is invisible to every test
// that does not open Revit. Measured on view 247769: Min.Z = -depth with
// Max.Z = 0 cut at 45 for an origin asked at 53.
test("bridge source: the section box depth interval runs 0..depth, never -depth..0", () => {
  const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
  const source = fs.readFileSync(
    path.join(repoRoot, "revit-bridge", "handlers", "src", "Endpoints", "ViewEndpoints.cs"),
    "utf8",
  );

  assert.match(source, /sectionBox\.Min = new XYZ\(-width \/ 2\.0, -height \/ 2\.0, 0\.0\);/);
  assert.match(source, /sectionBox\.Max = new XYZ\(width \/ 2\.0, height \/ 2\.0, depth\);/);
  assert.doesNotMatch(source, /sectionBox\.Min = new XYZ\([^)]*-depth\)/);
  assert.doesNotMatch(source, /sectionBox\.Max = new XYZ\([^)]*, 0\.0\)/);

  // The look direction is not part of this fix and stays measured as it was.
  assert.match(source, /XYZ right = up\.CrossProduct\(look\);/);
  assert.match(source, /transform\.BasisZ = look;/);
});

test("bridge source: the create-section response is measured off the created view", () => {
  const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
  const source = fs.readFileSync(
    path.join(repoRoot, "revit-bridge", "handlers", "src", "Endpoints", "ViewEndpoints.cs"),
    "utf8",
  );

  // The only echoed field is the requested origin, and it is named as such so it
  // can never be mistaken for a measurement.
  assert.match(source, /created\["requestedOrigin"\] = Vector\(origin\);/);
  assert.match(source, /AddCropReadback\(created, view\);/);

  // view.Origin and the crop transform are read separately: neither is assumed
  // to match the box handed to CreateSection, because Revit re-origins it.
  assert.match(source, /crop = view\.CropBox;/);
  assert.match(source, /viewOrigin = view\.Origin;/);

  // Eight corners through the crop's own transform — transforming Min and Max
  // alone is wrong for any section whose frame is turned.
  assert.match(source, /foreach \(XYZ corner in Corners\(crop\.Min, crop\.Max\)\)/);
  assert.match(source, /XYZ point = frame\.OfPoint\(corner\);/);

  // The cut face is MEASURED, not assumed. Revit hands the frame back with
  // BasisZ pointing at the viewer, so on view 247787 the near face was Max.Z and
  // reading Min.Z outright reported the far clip — (61, 40, 8.5) for a section
  // that correctly cut on (53, 40, 8.5). Both faces are projected on
  // ViewDirection and the larger wins, which holds either way round.
  assert.match(source, /XYZ minFace = frame\.OfPoint\(new XYZ\(centreX, centreY, crop\.Min\.Z\)\);/);
  assert.match(source, /XYZ maxFace = frame\.OfPoint\(new XYZ\(centreX, centreY, crop\.Max\.Z\)\);/);
  assert.match(
    source,
    /maxFace\.DotProduct\(viewDirection\) >= minFace\.DotProduct\(viewDirection\)/,
  );
  assert.match(source, /row\["modelBounds"\]/);

  // The near face is never picked by hardcoding one end of the local interval.
  assert.doesNotMatch(source, /row\["cutPlaneOrigin"\] = Vector\(frame\.OfPoint\(new XYZ\(/);
});

test("revit_create_section_view: the section geometry is documented in the tool description", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  const description = tools.find((t) => t.name === "revit_create_section_view").description;
  assert.match(description, /direction the section LOOKS TOWARD/);
  assert.match(description, /centred on origin/);
  assert.match(description, /towards the VIEWER/);
  assert.match(description, /looking toward D reports viewDirection -D/);
  assert.match(description, /feet \(Revit internal units\)/);

  // The readback is part of the contract: a caller has to be told which fields
  // settle the geometry and which cannot.
  assert.match(description, /'cutPlaneOrigin' is the model point the section really cuts on/);
  assert.match(description, /'modelBounds'/);
  assert.match(description, /origin -> origin \+ direction \* depth/);
  assert.match(description, /never by the local numbers/);
  await client.close();
});

// --- revit_list_legends ------------------------------------------------------

test("revit_list_legends: hits its endpoint", async () => {
  const bridge = fakeBridge(async () => []);
  const client = await connect(bridge);
  await client.callTool({ name: "revit_list_legends", arguments: {} });
  assert.deepEqual(
    bridge.calls.map((c) => c.endpoint),
    ["/views/legends"],
  );
  await client.close();
});

test("revit_list_legends: an empty list is the signal that a legend must be made in the UI first", async () => {
  const bridge = fakeBridge(async () => []);
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_list_legends", arguments: {} });
  assert.equal(result.isError, undefined);
  assert.deepEqual(JSON.parse(textOf(result)), []);
  await client.close();
});

test("revit_list_legends: id, name and scale come through", async () => {
  const bridge = fakeBridge(async () => [
    { id: 8001, name: "Legend - Paving", scale: 20 },
    { id: 8002, name: "Legend - Planting", scale: 50 },
  ]);
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_list_legends", arguments: {} });
  const rows = JSON.parse(textOf(result));
  assert.equal(rows[0].scale, 20);
  assert.equal(rows[1].name, "Legend - Planting");
  await client.close();
});

// --- revit_create_legend -----------------------------------------------------

test("revit_create_legend: from_legend_id maps to fromLegendId", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_legend",
    arguments: { name: "Legend - Materials", from_legend_id: 8001, scale: 20 },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/views/create-legend",
    payload: { name: "Legend - Materials", fromLegendId: 8001, scale: 20 },
  });
  await client.close();
});

test("revit_create_legend: omitting from_legend_id leaves the choice to the bridge", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_legend",
    arguments: { name: "Legend - Materials" },
  });
  assert.equal(bridge.calls[0].payload.fromLegendId, undefined);
  assert.equal(bridge.calls[0].payload.scale, undefined);
  await client.close();
});

test("revit_create_legend: NO_LEGEND_TO_DUPLICATE is surfaced, not swallowed", async () => {
  // The one failure a caller must not paper over: Revit cannot author the first
  // legend, so the answer is "make one in the UI", never a drafting view.
  const bridge = fakeBridge(async () => {
    throw new Error(
      "Revit failed on /views/create-legend: This document has no legend view, and the Revit API cannot create the first one",
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_legend",
    arguments: { name: "Legend - Materials" },
  });
  assert.match(textOf(result), /cannot create the first one/);
  await client.close();
});

test("revit_create_legend: rejects an empty name, a fractional id and a fractional scale", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_create_legend", {});
  await assertRejected(client, bridge, "revit_create_legend", { name: "" });
  await assertRejected(client, bridge, "revit_create_legend", { name: "L", from_legend_id: 1.5 });
  await assertRejected(client, bridge, "revit_create_legend", { name: "L", scale: 0 });
  await assertRejected(client, bridge, "revit_create_legend", { name: "L", scale: 1.5 });
  await client.close();
});

test("revit_create_legend: the description says Revit cannot author the first legend", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  const description = tools.find((t) => t.name === "revit_create_legend").description;
  assert.match(description, /duplicating one the document already has/);
  assert.match(description, /NO_LEGEND_TO_DUPLICATE/);
  assert.match(description, /do not substitute a drafting view/);
  await client.close();
});

// --- revit_duplicate_view ----------------------------------------------------

test("revit_duplicate_view: view_id maps to viewId", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_duplicate_view",
    arguments: { view_id: 777, name: "Planting Plan", detailing: "WithDetailing" },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/views/duplicate",
    payload: { viewId: 777, name: "Planting Plan", detailing: "WithDetailing" },
  });
  await client.close();
});

test("revit_duplicate_view: only the three ViewDuplicateOption names are accepted", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  for (const detailing of ["Duplicate", "WithDetailing", "AsDependent"]) {
    await client.callTool({
      name: "revit_duplicate_view",
      arguments: { view_id: 1, name: "Copy", detailing },
    });
  }
  assert.equal(bridge.calls.length, 3);
  await assertRejected(client, bridge, "revit_duplicate_view", {
    view_id: 1,
    name: "Copy",
    detailing: "withdetailing",
  });
  await assertRejected(client, bridge, "revit_duplicate_view", {
    view_id: 1,
    name: "Copy",
    detailing: "Dependent",
  });
  await assertRejected(client, bridge, "revit_duplicate_view", { view_id: 1.5, name: "Copy" });
  await assertRejected(client, bridge, "revit_duplicate_view", { view_id: 1, name: "" });
  await client.close();
});

test("revit_duplicate_view: CANNOT_DUPLICATE is surfaced, not swallowed", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      'Revit failed on /views/duplicate: Revit will not duplicate view "Legend" (777) with option AsDependent.',
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_duplicate_view",
    arguments: { view_id: 777, name: "Copy", detailing: "AsDependent" },
  });
  assert.match(textOf(result), /^Error: Revit failed on \/views\/duplicate/);
  assert.match(textOf(result), /will not duplicate/);
  await client.close();
});

// --- revit_set_view_scale ----------------------------------------------------

test("revit_set_view_scale: a single view_id maps to viewId", async () => {
  const bridge = fakeBridge(async () => ({ updated: [], failed: [] }));
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_set_view_scale",
    arguments: { view_id: 7001, scale: 100 },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/views/set-scale",
    payload: { viewId: 7001, viewIds: undefined, scale: 100 },
  });
  await client.close();
});

test("revit_set_view_scale: the whole batch goes in one call, so it is one undo step", async () => {
  const bridge = fakeBridge(async () => ({ updated: [], failed: [] }));
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_set_view_scale",
    arguments: { view_ids: [7001, 7002, 7003], scale: 200 },
  });
  assert.equal(bridge.calls.length, 1);
  assert.deepEqual(bridge.calls[0].payload.viewIds, [7001, 7002, 7003]);
  assert.equal(bridge.calls[0].payload.viewId, undefined);
  await client.close();
});

test("revit_set_view_scale: neither view_id nor view_ids never reaches the bridge", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_view_scale",
    arguments: { scale: 100 },
  });
  assert.match(textOf(result), /either view_id .* or view_ids/);
  assert.equal(bridge.calls.length, 0);
  await client.close();
});

test("revit_set_view_scale: both view_id and view_ids never reaches the bridge", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_view_scale",
    arguments: { view_id: 7001, view_ids: [7002], scale: 100 },
  });
  assert.match(textOf(result), /not both/);
  assert.equal(bridge.calls.length, 0);
  await client.close();
});

test("revit_set_view_scale: rejects a missing, zero, negative or fractional scale", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_set_view_scale", { view_id: 7001 });
  await assertRejected(client, bridge, "revit_set_view_scale", { view_id: 7001, scale: 0 });
  await assertRejected(client, bridge, "revit_set_view_scale", { view_id: 7001, scale: -100 });
  await assertRejected(client, bridge, "revit_set_view_scale", { view_id: 7001, scale: 1.5 });
  await assertRejected(client, bridge, "revit_set_view_scale", { view_ids: [], scale: 100 });
  await client.close();
});

test("revit_set_view_scale: a view that cannot be scaled lands in failed while the rest are updated", async () => {
  const bridge = fakeBridge(async () => ({
    updated: [
      { id: 7001, name: "Level 1", viewType: "FloorPlan", scale: 100 },
      { id: 7002, name: "Level 2", viewType: "FloorPlan", scale: 100 },
    ],
    failed: [
      {
        viewId: 7003,
        code: "VIEW_SCALE_NOT_SETTABLE",
        reason:
          'View "Planting Schedule" (7003, Schedule) is a schedule, a sheet or a view template, and the bridge does not scale those.',
      },
    ],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_view_scale",
    arguments: { view_ids: [7001, 7002, 7003], scale: 100 },
  });
  assert.equal(result.isError, undefined);
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.updated.length, 2);
  assert.equal(payload.failed[0].code, "VIEW_SCALE_NOT_SETTABLE");
  await client.close();
});

test("revit_set_view_scale: the scale reported is read back off each view", async () => {
  const bridge = fakeBridge(async () => ({
    // Asked 100; a view template owns this one's scale and it came back 50.
    updated: [{ id: 7001, name: "Level 1", viewType: "FloorPlan", scale: 50 }],
    failed: [],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_view_scale",
    arguments: { view_id: 7001, scale: 100 },
  });
  assert.equal(JSON.parse(textOf(result)).updated[0].scale, 50);
  await client.close();
});

// --- revit_scale_perspective_crop --------------------------------------------

test("revit_scale_perspective_crop: view_id/multiplier map to viewId/multiplier", async () => {
  const bridge = fakeBridge(async () => ({ dryRun: false, applied: true }));
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_scale_perspective_crop",
    arguments: { view_id: 7010, multiplier: 2, dry_run: false },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/views/scale-perspective-crop",
    payload: { viewId: 7010, multiplier: 2, dryRun: false },
  });
  await client.close();
});

test("revit_scale_perspective_crop: dry_run defaults to TRUE when it is not passed", async () => {
  const bridge = fakeBridge(async () => ({ dryRun: true, applied: false }));
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_scale_perspective_crop",
    arguments: { view_id: 7010, multiplier: 0.5 },
  });
  assert.equal(bridge.calls[0].payload.dryRun, true);
  await client.close();
});

test("revit_scale_perspective_crop: rejects a missing, zero or negative multiplier", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_scale_perspective_crop", { view_id: 7010 });
  await assertRejected(client, bridge, "revit_scale_perspective_crop", {
    view_id: 7010,
    multiplier: 0,
  });
  await assertRejected(client, bridge, "revit_scale_perspective_crop", {
    view_id: 7010,
    multiplier: -2,
  });
  await assertRejected(client, bridge, "revit_scale_perspective_crop", { multiplier: 2 });
  await client.close();
});

test("revit_scale_perspective_crop: the dry run reports the size now and no predicted after", async () => {
  const bridge = fakeBridge(async () => ({
    dryRun: true,
    applied: false,
    viewId: 7010,
    multiplier: 2,
    before: {
      isPerspective: true,
      outline: { min: { x: 0, y: 0 }, max: { x: 0.5, y: 0.35 }, width: 0.5, height: 0.35 },
      cropBox: { width: 40, height: 28 },
    },
    after: null,
    cameraUnchanged: null,
    note: "Nothing was changed.",
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_scale_perspective_crop",
    arguments: { view_id: 7010, multiplier: 2 },
  });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.applied, false);
  assert.equal(payload.before.outline.width, 0.5);
  // No invented "after": the size Revit lands on is a readback, not arithmetic.
  assert.equal(payload.after, null);
  await client.close();
});

test("revit_scale_perspective_crop: an applied call reports before and after measured, camera unchanged", async () => {
  const bridge = fakeBridge(async () => ({
    dryRun: false,
    applied: true,
    viewId: 7010,
    multiplier: 2,
    before: { outline: { width: 0.5, height: 0.35 }, cropBox: { width: 40, height: 28 } },
    after: { outline: { width: 1.0, height: 0.7 }, cropBox: { width: 80, height: 56 } },
    cameraUnchanged: true,
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_scale_perspective_crop",
    arguments: { view_id: 7010, multiplier: 2, dry_run: false },
  });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.after.outline.width, 1.0);
  assert.equal(payload.cameraUnchanged, true);
  await client.close();
});

test("revit_scale_perspective_crop: the bridge's refusals come back with their codes", async () => {
  for (const code of ["VIEW_IS_TEMPLATE", "VIEW_NOT_PERSPECTIVE", "NOT_A_3D_VIEW"]) {
    const bridge = fakeBridge(async () => {
      throw new Error(`${code}: that view cannot be scaled this way`);
    });
    const client = await connect(bridge);
    const result = await client.callTool({
      name: "revit_scale_perspective_crop",
      arguments: { view_id: 7010, multiplier: 2, dry_run: false },
    });
    assert.match(textOf(result), new RegExp(code));
    await client.close();
  }
});

test("bridge source: scale-perspective-crop calls Revit's own method and never moves the camera", () => {
  const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
  const source = fs.readFileSync(
    path.join(repoRoot, "revit-bridge", "handlers", "src", "Endpoints", "ViewEndpoints.cs"),
    "utf8",
  );

  // The handler on its own: the rest of the file creates views and does aim
  // cameras, and this endpoint must not.
  const handler = source.slice(
    source.indexOf("internal static object ScalePerspectiveCrop("),
    source.indexOf("internal static object SetStyle("),
  );
  assert.ok(handler.length > 0, "ScalePerspectiveCrop not found in ViewEndpoints.cs");

  // The exact API this endpoint wraps, and nothing else: no crop box written by
  // hand, no orientation set, so the framing cannot drift.
  assert.match(handler, /view\.ScalePerspectiveCropBox\(multiplier\);/);
  assert.doesNotMatch(handler, /SetOrientation\(/);
  assert.doesNotMatch(handler, /\.CropBox = /);

  // Everything checked before the transaction opens, so a refusal changes nothing.
  assert.match(source, /View3D view = RequirePerspectiveView\(document, viewId\);/);
  assert.match(source, /"NOT_A_3D_VIEW"/);
  assert.match(source, /"VIEW_IS_TEMPLATE"/);
  assert.match(source, /"VIEW_NOT_PERSPECTIVE"/);

  // dryRun defaults TRUE, and the dry run has no "after" to be wrong about.
  assert.match(source, /bool dryRun = JsonBody\.OptionalBool\(body, "dryRun", true\);/);

  // One transaction - one Ctrl+Z - and a regeneration before the readback,
  // because View.Outline and the viewport's box are derived geometry.
  assert.match(source, /RevitWrite\.InGroup\(document, "MCP: scale perspective crop"/);
  assert.match(source, /document\.Regenerate\(\);/);
  assert.match(source, /report\["after"\] = ReadPerspectiveSize\(document, view, afterCamera\);/);
  assert.match(
    source,
    /report\["cameraUnchanged"\] = CameraUnchanged\(beforeCamera, afterCamera\);/,
  );
});

test("bridge source: set-scale refuses a perspective view instead of letting Revit throw", () => {
  const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
  const source = fs.readFileSync(
    path.join(repoRoot, "revit-bridge", "handlers", "src", "Endpoints", "ViewEndpoints.cs"),
    "utf8",
  );

  // Its own code, and it lands in "failed" like every other refusal there, so a
  // perspective in the list still leaves the rest of the batch re-scaled.
  assert.match(source, /if \(IsPerspectiveView\(view\)\)/);
  assert.match(source, /"PERSPECTIVE_VIEW_HAS_NO_SCALE"/);
  assert.match(source, /failed\.Add\(ScaleFailure\(\s*viewId,\s*"PERSPECTIVE_VIEW_HAS_NO_SCALE"/);
  assert.match(source, /views\/scale-perspective-crop/);
});

test("bridge source: views/scale-perspective-crop is routed", () => {
  const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
  const source = fs.readFileSync(
    path.join(repoRoot, "revit-bridge", "handlers", "src", "RequestRouter.cs"),
    "utf8",
  );

  assert.match(
    source,
    /\{ "views\/scale-perspective-crop", ViewEndpoints\.ScalePerspectiveCrop \},/,
  );
});

// --- revit_place_views_on_sheets ---------------------------------------------

test("revit_place_views_on_sheets: sheet_id/view_id map to sheetId/viewId inside placements", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_place_views_on_sheets",
    arguments: {
      placements: [
        { sheet_id: 100, view_id: 200, x: 0.975, y: 0.69 },
        { sheet_id: 101, view_id: 201 },
      ],
    },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/sheets/place-view",
    payload: {
      placements: [
        { sheetId: 100, viewId: 200, x: 0.975, y: 0.69 },
        { sheetId: 101, viewId: 201, x: undefined, y: undefined },
      ],
    },
  });
  await client.close();
});

test("revit_place_views_on_sheets: the whole phase goes in one call, so it is one undo step", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  const placements = Array.from({ length: 29 }, (_, i) => ({
    sheet_id: 100 + i,
    view_id: 200 + i,
  }));
  await client.callTool({ name: "revit_place_views_on_sheets", arguments: { placements } });
  assert.equal(bridge.calls.length, 1);
  assert.equal(bridge.calls[0].payload.placements.length, 29);
  await client.close();
});

test("revit_place_views_on_sheets: rejects an empty batch and non-integer ids", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_place_views_on_sheets", {});
  await assertRejected(client, bridge, "revit_place_views_on_sheets", { placements: [] });
  await assertRejected(client, bridge, "revit_place_views_on_sheets", {
    placements: [{ sheet_id: 100 }],
  });
  await assertRejected(client, bridge, "revit_place_views_on_sheets", {
    placements: [{ view_id: 200 }],
  });
  await assertRejected(client, bridge, "revit_place_views_on_sheets", {
    placements: [{ sheet_id: 100.5, view_id: 200 }],
  });
  await assertRejected(client, bridge, "revit_place_views_on_sheets", {
    placements: [{ sheet_id: 100, view_id: 200, x: "middle" }],
  });
  await client.close();
});

test("revit_place_views_on_sheets: VIEW_ALREADY_PLACED and CANNOT_PLACE come back per placement", async () => {
  const bridge = fakeBridge(async () => ({
    placed: [{ viewportId: 900, sheetId: 100, viewId: 200, kind: "Viewport", x: 0.975, y: 0.69 }],
    failed: [
      {
        sheetId: 101,
        viewId: 201,
        code: "VIEW_ALREADY_PLACED",
        reason: 'View "Level 1" (201) is already on sheet 100.',
      },
      {
        sheetId: 102,
        viewId: 202,
        code: "CANNOT_PLACE",
        reason: 'Revit will not put view "Legend" (202, Legend) on sheet A103 (102).',
      },
    ],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_place_views_on_sheets",
    arguments: {
      placements: [
        { sheet_id: 100, view_id: 200 },
        { sheet_id: 101, view_id: 201 },
        { sheet_id: 102, view_id: 202 },
      ],
    },
  });
  assert.equal(result.isError, undefined);
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.placed.length, 1);
  assert.deepEqual(
    payload.failed.map((f) => f.code),
    ["VIEW_ALREADY_PLACED", "CANNOT_PLACE"],
  );
  await client.close();
});

test("revit_place_views_on_sheets: a schedule comes back as a ScheduleSheetInstance, not a Viewport", async () => {
  const bridge = fakeBridge(async () => ({
    placed: [
      {
        viewportId: 901,
        sheetId: 100,
        viewId: 300,
        kind: "ScheduleSheetInstance",
        x: 0.975,
        y: 0.69,
      },
    ],
    failed: [],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_place_views_on_sheets",
    arguments: { placements: [{ sheet_id: 100, view_id: 300 }] },
  });
  assert.equal(JSON.parse(textOf(result)).placed[0].kind, "ScheduleSheetInstance");
  await client.close();
});

// --- revit_create_schedule ---------------------------------------------------

test("revit_create_schedule: forwards category, name and fields in order", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_schedule",
    arguments: {
      category: "Planting",
      name: "Planting Schedule",
      fields: ["Family and Type", "Count", "Comments"],
    },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/schedules/create",
    payload: {
      category: "Planting",
      name: "Planting Schedule",
      fields: ["Family and Type", "Count", "Comments"],
      scale: undefined,
    },
  });
  await client.close();
});

test("revit_create_schedule: rejects an empty category, name or field list", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_create_schedule", {});
  await assertRejected(client, bridge, "revit_create_schedule", {
    category: "Planting",
    name: "S",
    fields: [],
  });
  await assertRejected(client, bridge, "revit_create_schedule", {
    category: "",
    name: "S",
    fields: ["Count"],
  });
  await assertRejected(client, bridge, "revit_create_schedule", {
    category: "Planting",
    name: "",
    fields: ["Count"],
  });
  await assertRejected(client, bridge, "revit_create_schedule", {
    category: "Planting",
    name: "S",
    fields: [""],
  });
  await client.close();
});

test("revit_create_schedule: a skipped field brings back the available names, not an error", async () => {
  const bridge = fakeBridge(async () => ({
    id: 4242,
    name: "Planting Schedule",
    category: "Planting",
    fields: ["Family and Type", "Count"],
    skippedFields: ["Botanical Name"],
    availableFields: ["Comments", "Count", "Family and Type", "Mark"],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_schedule",
    arguments: {
      category: "Planting",
      name: "Planting Schedule",
      fields: ["Family and Type", "Count", "Botanical Name"],
    },
  });
  assert.equal(result.isError, undefined);
  const payload = JSON.parse(textOf(result));
  assert.deepEqual(payload.skippedFields, ["Botanical Name"]);
  assert.ok(payload.availableFields.includes("Mark"));
  await client.close();
});

test("revit_create_schedule: the Sheets category keeps the same interface", async () => {
  const bridge = fakeBridge(async () => ({
    id: 5150,
    name: "Índice L.02",
    category: "Sheets",
    fields: ["Sheet Number", "Sheet Name"],
  }));
  const client = await connect(bridge);

  await client.callTool({
    name: "revit_create_schedule",
    arguments: {
      category: "Sheets",
      name: "Índice L.02",
      fields: ["Sheet Number", "Sheet Name"],
    },
  });

  // Sheets is routed to a different Revit API call inside the bridge, but that
  // is the bridge's business: the payload is the same shape as any other
  // category, so nothing about the tool's interface changes.
  assert.deepEqual(bridge.calls[0].payload, {
    category: "Sheets",
    name: "Índice L.02",
    fields: ["Sheet Number", "Sheet Name"],
    scale: undefined,
  });
  await client.close();
});

// Source contract. ViewSchedule.CreateSchedule(document, OST_Sheets) throws an
// ArgumentException in Revit — a sheet list is built with CreateSheetList — and
// that only shows up against a live model, so the branch is pinned here.
test("bridge source: a Sheets schedule is created with CreateSheetList", () => {
  const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
  const source = fs.readFileSync(
    path.join(repoRoot, "revit-bridge", "handlers", "src", "Endpoints", "ViewEndpoints.cs"),
    "utf8",
  );

  assert.match(source, /BuiltInCategory\.OST_Sheets\s*$/m);
  assert.match(source, /ViewSchedule\.CreateSheetList\(document\)/);
  assert.match(source, /ViewSchedule\.CreateSchedule\(document, category\.Id\)/);
});
