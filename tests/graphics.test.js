import { test } from "node:test";
import assert from "node:assert/strict";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { createRevitServer } from "../index.js";

// Same setup as views.test.js: a real MCP client over an in-memory transport,
// so the schemas doing the validating are the SDK's, with the bridge faked.
//
// What these tests are actually guarding is honesty. The shadows tools can come
// back saying a command was POSTED rather than applied, and the danger is a
// description or a payload that lets that read as success. So as well as the
// usual mapping and validation, there are assertions on the tool descriptions
// themselves — they are the only thing the model reads before deciding what to
// tell the user.

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

async function descriptionOf(client, name) {
  const { tools } = await client.listTools();
  return tools.find((t) => t.name === name).description;
}

// --- revit_get_view_graphics -------------------------------------------------

test("revit_get_view_graphics: view_id maps to camelCase", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({ name: "revit_get_view_graphics", arguments: { view_id: 212657 } });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/views/graphics",
    payload: { viewId: 212657, viewIds: undefined },
  });
  await client.close();
});

test("revit_get_view_graphics: view_ids goes through as a batch", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({ name: "revit_get_view_graphics", arguments: { view_ids: [1, 2, 3] } });
  assert.deepEqual(bridge.calls[0].payload.viewIds, [1, 2, 3]);
  await client.close();
});

test("revit_get_view_graphics: neither id form is an error before the bridge is called", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_get_view_graphics", arguments: {} });
  assert.match(textOf(result), /view_id or view_ids/);
  assert.equal(bridge.calls.length, 0);
  await client.close();
});

test("revit_get_view_graphics: the probe reaches the model unchanged", async () => {
  // A null "on" is the case that matters: it means the bridge could not read
  // the parameter, and it must not be flattened into false anywhere on the way.
  const bridge = fakeBridge(async () => ({
    views: [
      {
        id: 212657,
        shadows: { available: true, storageType: "None", readOnly: true, on: null, writable: false },
      },
    ],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_get_view_graphics", arguments: { view_id: 212657 } });
  assert.equal(JSON.parse(textOf(result)).views[0].shadows.on, null);
  assert.equal(JSON.parse(textOf(result)).views[0].shadows.writable, false);
  await client.close();
});

test("revit_get_view_graphics: rejects a non-integer id and an empty batch", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_get_view_graphics", { view_id: 1.5 });
  await assertRejected(client, bridge, "revit_get_view_graphics", { view_ids: [] });
  await client.close();
});

// --- revit_set_view_graphics -------------------------------------------------

test("revit_set_view_graphics: every argument maps to its camelCase field", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_set_view_graphics",
    arguments: {
      view_id: 212657,
      style: "Realistic",
      detail_level: "Fine",
      shadow_intensity: 60,
      sunlight_intensity: 70,
      shadows: true,
    },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/views/set-graphics",
    payload: {
      viewId: 212657,
      viewIds: undefined,
      style: "Realistic",
      detailLevel: "Fine",
      shadowIntensity: 60,
      sunlightIntensity: 70,
      shadows: true,
    },
  });
  await client.close();
});

test("revit_set_view_graphics: shadows false is forwarded, not dropped as falsy", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_set_view_graphics",
    arguments: { view_id: 1, shadows: false },
  });
  assert.equal(bridge.calls[0].payload.shadows, false);
  await client.close();
});

test("revit_set_view_graphics: intensities outside 0-100 and non-integers are rejected", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_set_view_graphics", { view_id: 1, shadow_intensity: 101 });
  await assertRejected(client, bridge, "revit_set_view_graphics", { view_id: 1, shadow_intensity: -1 });
  await assertRejected(client, bridge, "revit_set_view_graphics", { view_id: 1, sunlight_intensity: 150 });
  await assertRejected(client, bridge, "revit_set_view_graphics", { view_id: 1, sunlight_intensity: 12.5 });
  await client.close();
});

test("revit_set_view_graphics: neither id form is an error before the bridge is called", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_view_graphics",
    arguments: { shadows: true },
  });
  assert.match(textOf(result), /view_id or view_ids/);
  assert.equal(bridge.calls.length, 0);
  await client.close();
});

test("revit_set_view_graphics: a verified parameter write comes back as written", async () => {
  const bridge = fakeBridge(async () => ({
    views: [{ id: 1, shadows: { on: true, writable: true } }],
    shadows: { requested: true, method: "parameter", posted: false, pending: false, verified: true },
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_view_graphics",
    arguments: { view_id: 1, shadows: true },
  });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.shadows.method, "parameter");
  assert.equal(payload.shadows.verified, true);
  await client.close();
});

test("revit_set_view_graphics: a posted command reaches the model as pending and unverified", async () => {
  // The whole point of the fallback: the bridge cannot know whether the command
  // worked, so pending/unverified must survive intact to whoever reads it.
  const bridge = fakeBridge(async () => ({
    views: [{ id: 1 }],
    shadows: {
      requested: true,
      method: "posted-command",
      command: "ID_IMAGE_SHADOW_ON",
      posted: true,
      pending: true,
      verified: false,
      unverified: true,
    },
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_view_graphics",
    arguments: { view_id: 1, shadows: true },
  });
  const shadows = JSON.parse(textOf(result)).shadows;
  assert.equal(shadows.method, "posted-command");
  assert.equal(shadows.pending, true);
  assert.equal(shadows.unverified, true);
  assert.equal(shadows.verified, false);
  assert.doesNotMatch(textOf(result), /"success"/);
  await client.close();
});

test("revit_set_view_graphics: an ambient-light refusal is reported, not swallowed", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error("Revit failed on /views/set-graphics: AMBIENT_LIGHT_NOT_EXPOSED");
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_view_graphics",
    arguments: { view_id: 1, shadow_intensity: 50 },
  });
  assert.match(textOf(result), /AMBIENT_LIGHT_NOT_EXPOSED/);
  await client.close();
});

test("revit_set_view_graphics: the description says a posted command is unverified and offers no ambient light", async () => {
  const client = await connect(fakeBridge());
  const description = await descriptionOf(client, "revit_set_view_graphics");
  assert.match(description, /posted-command/);
  assert.match(description, /unverified/i);
  assert.match(description, /pending/i);
  assert.doesNotMatch(description, /ambient_light_intensity/);
  await client.close();
});

// --- revit_get_view_graphics_command_status ----------------------------------

test("revit_get_view_graphics_command_status: hits its endpoint with no payload", async () => {
  const bridge = fakeBridge(async () => ({ posted: false, pending: false, verified: false }));
  const client = await connect(bridge);
  await client.callTool({ name: "revit_get_view_graphics_command_status", arguments: {} });
  assert.equal(bridge.calls[0].endpoint, "/views/graphics-command-status");
  assert.equal(bridge.calls[0].payload, undefined);
  await client.close();
});

test("revit_get_view_graphics_command_status: an unverifiable result stays unverified", async () => {
  const bridge = fakeBridge(async () => ({
    posted: true,
    pending: false,
    verified: false,
    verifiedBy: null,
    probeNow: { on: null },
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_get_view_graphics_command_status",
    arguments: {},
  });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.verified, false);
  assert.equal(payload.verifiedBy, null);
  assert.equal(payload.probeNow.on, null);
  await client.close();
});

// --- revit_capture_view_template ---------------------------------------------

test("revit_capture_view_template: source_view_id and parameter_ids map to camelCase", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_capture_view_template",
    arguments: { source_view_id: 212657, name: "Presentation 3D", parameter_ids: [-1006951] },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/views/capture-template",
    payload: {
      sourceViewId: 212657,
      name: "Presentation 3D",
      mode: undefined,
      parameterIds: [-1006951],
    },
  });
  await client.close();
});

test("revit_capture_view_template: mode is forwarded as given", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  for (const mode of ["graphics", "shadows", "all"]) {
    await client.callTool({
      name: "revit_capture_view_template",
      arguments: { source_view_id: 1, name: `T ${mode}`, mode },
    });
  }
  assert.deepEqual(
    bridge.calls.map((c) => c.payload.mode),
    ["graphics", "shadows", "all"],
  );
  await client.close();
});

test("revit_capture_view_template: rejects an unknown mode, a missing name and a bad id", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_capture_view_template", {
    source_view_id: 1,
    name: "T",
    mode: "everything",
  });
  await assertRejected(client, bridge, "revit_capture_view_template", { source_view_id: 1 });
  await assertRejected(client, bridge, "revit_capture_view_template", { source_view_id: 1, name: "" });
  await assertRejected(client, bridge, "revit_capture_view_template", { name: "T" });
  await assertRejected(client, bridge, "revit_capture_view_template", {
    source_view_id: 1.5,
    name: "T",
  });
  await client.close();
});

test("revit_capture_view_template: the controlled set and the source's shadows reach the model", async () => {
  const bridge = fakeBridge(async () => ({
    templateId: 900,
    name: "Presentation 3D",
    controlled: [{ id: -1006951, name: "Shadows (GRAPHIC_DISPLAY_OPTIONS_SHADOWS)" }],
    excluded: [{ id: -1006952, name: "Sun Path", reason: "sun - left per-view" }],
    shadows: { templateControlsShadows: true, sourceProbe: { on: null } },
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_capture_view_template",
    arguments: { source_view_id: 1, name: "Presentation 3D" },
  });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.controlled[0].id, -1006951);
  assert.match(payload.excluded[0].reason, /sun/);
  assert.equal(payload.shadows.sourceProbe.on, null);
  await client.close();
});

test("revit_capture_view_template: the description says a template only carries what the source had", async () => {
  const client = await connect(fakeBridge());
  const description = await descriptionOf(client, "revit_capture_view_template");
  assert.match(description, /only what the source view HAD/);
  await client.close();
});

// --- revit_apply_view_template -----------------------------------------------

test("revit_apply_view_template: template_id, view_ids and dry_run map to camelCase", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_apply_view_template",
    arguments: { template_id: 900, view_ids: [1, 2], mode: "assign", dry_run: false, replace: true },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/views/apply-template",
    payload: { templateId: 900, viewIds: [1, 2], mode: "assign", dryRun: false, replace: true },
  });
  await client.close();
});

test("revit_apply_view_template: dry_run is left undefined so the bridge's true default stands", async () => {
  // The safety default lives on the Revit side. Sending false from here by
  // accident would turn a plan request into a write.
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_apply_view_template",
    arguments: { template_id: 900, view_ids: [1] },
  });
  assert.equal(bridge.calls[0].payload.dryRun, undefined);
  assert.notEqual(bridge.calls[0].payload.dryRun, false);
  await client.close();
});

test("revit_apply_view_template: rejects an empty batch, an unknown mode and non-integer ids", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_apply_view_template", { template_id: 900, view_ids: [] });
  await assertRejected(client, bridge, "revit_apply_view_template", {
    template_id: 900,
    view_ids: [1],
    mode: "attach",
  });
  await assertRejected(client, bridge, "revit_apply_view_template", { view_ids: [1] });
  await assertRejected(client, bridge, "revit_apply_view_template", {
    template_id: 900,
    view_ids: [1.5],
  });
  await client.close();
});

test("revit_apply_view_template: a whole-batch refusal is reported, not partially applied", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      "Revit failed on /views/apply-template: Nothing was changed. 1 of 2 views cannot take this template",
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_apply_view_template",
    arguments: { template_id: 900, view_ids: [1, 2], dry_run: false },
  });
  assert.match(textOf(result), /Nothing was changed/);
  await client.close();
});

test("revit_apply_view_template: the description says dry_run defaults to true", async () => {
  const client = await connect(fakeBridge());
  const description = await descriptionOf(client, "revit_apply_view_template");
  assert.match(description, /dry_run defaults to TRUE/);
  await client.close();
});
