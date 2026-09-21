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

// --- revit_create_project_parameter ------------------------------------------

test("revit_create_project_parameter: a single category is forwarded as category", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_project_parameter",
    arguments: { name: "Phase", category: "Sheets" },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/parameters/create-project",
    payload: {
      name: "Phase",
      category: "Sheets",
      categories: undefined,
      type: undefined,
      group: undefined,
      instance: undefined,
    },
  });
  await client.close();
});

test("revit_create_project_parameter: several categories are forwarded as categories", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_project_parameter",
    arguments: {
      name: "Zone",
      categories: ["Sheets", "Views"],
      type: "Text",
      group: "IdentityData",
      instance: true,
    },
  });
  assert.deepEqual(bridge.calls[0].payload, {
    name: "Zone",
    category: undefined,
    categories: ["Sheets", "Views"],
    type: "Text",
    group: "IdentityData",
    instance: true,
  });
  await client.close();
});

test("revit_create_project_parameter: neither category nor categories never reaches the bridge", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_project_parameter",
    arguments: { name: "Phase" },
  });
  assert.match(textOf(result), /either category .* or categories/);
  assert.equal(bridge.calls.length, 0);
  await client.close();
});

test("revit_create_project_parameter: both category and categories never reaches the bridge", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_project_parameter",
    arguments: { name: "Phase", category: "Sheets", categories: ["Views"] },
  });
  assert.match(textOf(result), /not both/);
  assert.equal(bridge.calls.length, 0);
  await client.close();
});

test("revit_create_project_parameter: rejects an empty name, an empty category list and unknown enums", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_create_project_parameter", {});
  await assertRejected(client, bridge, "revit_create_project_parameter", {
    name: "",
    category: "Sheets",
  });
  await assertRejected(client, bridge, "revit_create_project_parameter", {
    name: "Phase",
    categories: [],
  });
  await assertRejected(client, bridge, "revit_create_project_parameter", {
    name: "Phase",
    category: "Sheets",
    type: "Currency",
  });
  await assertRejected(client, bridge, "revit_create_project_parameter", {
    name: "Phase",
    category: "Sheets",
    group: "Whatever",
  });
  await assertRejected(client, bridge, "revit_create_project_parameter", {
    name: "Phase",
    category: "Sheets",
    instance: "yes",
  });
  await client.close();
});

test("revit_create_project_parameter: a parameter that already exists is reported, not an error", async () => {
  const bridge = fakeBridge(async () => ({
    name: "Phase",
    guid: "6f8a5e1c-2d4b-4f9a-91f2-6e0a1f4b7c31",
    categories: ["Sheets"],
    created: false,
    alreadyExisted: true,
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_project_parameter",
    arguments: { name: "Phase", category: "Sheets" },
  });
  assert.equal(result.isError, undefined);
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.created, false);
  assert.equal(payload.alreadyExisted, true);
  assert.deepEqual(payload.categories, ["Sheets"]);
  await client.close();
});

test("revit_create_project_parameter: a created parameter comes back with its guid", async () => {
  const bridge = fakeBridge(async () => ({
    name: "Phase",
    guid: "6f8a5e1c-2d4b-4f9a-91f2-6e0a1f4b7c31",
    categories: ["Sheets"],
    instance: true,
    created: true,
    alreadyExisted: false,
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_project_parameter",
    arguments: { name: "Phase", category: "Sheets" },
  });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.created, true);
  assert.match(payload.guid, /^[0-9a-f-]{36}$/);
  await client.close();
});

// --- revit_set_sheet_parameters ----------------------------------------------

test("revit_set_sheet_parameters: sheet_id maps to sheetId inside values", async () => {
  const bridge = fakeBridge(async () => ({ updated: [], failed: [] }));
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_set_sheet_parameters",
    arguments: {
      values: [
        { sheet_id: 101, name: "Phase", value: "L.02" },
        { sheet_id: 102, name: "Phase", value: "L.03" },
      ],
    },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/sheets/set-parameter",
    payload: {
      values: [
        { sheetId: 101, name: "Phase", value: "L.02" },
        { sheetId: 102, name: "Phase", value: "L.03" },
      ],
    },
  });
  await client.close();
});

test("revit_set_sheet_parameters: the whole set goes in one call, so it is one undo step", async () => {
  const bridge = fakeBridge(async () => ({ updated: [], failed: [] }));
  const client = await connect(bridge);
  const values = [];
  for (let index = 0; index < 56; index++) {
    values.push({ sheet_id: 1000 + index, name: "Phase", value: "L.02" });
  }
  await client.callTool({ name: "revit_set_sheet_parameters", arguments: { values } });
  assert.equal(bridge.calls.length, 1);
  assert.equal(bridge.calls[0].payload.values.length, 56);
  await client.close();
});

test("revit_set_sheet_parameters: numbers and booleans are allowed values, not only strings", async () => {
  const bridge = fakeBridge(async () => ({ updated: [], failed: [] }));
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_set_sheet_parameters",
    arguments: {
      values: [
        { sheet_id: 101, name: "Revision Number", value: 3 },
        { sheet_id: 101, name: "Appears In Sheet List", value: false },
      ],
    },
  });
  assert.equal(bridge.calls[0].payload.values[0].value, 3);
  assert.equal(bridge.calls[0].payload.values[1].value, false);
  await client.close();
});

test("revit_set_sheet_parameters: rejects an empty batch, a fractional id and an empty name", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_set_sheet_parameters", {});
  await assertRejected(client, bridge, "revit_set_sheet_parameters", { values: [] });
  await assertRejected(client, bridge, "revit_set_sheet_parameters", {
    values: [{ sheet_id: 1.5, name: "Phase", value: "L.02" }],
  });
  await assertRejected(client, bridge, "revit_set_sheet_parameters", {
    values: [{ sheet_id: 101, name: "", value: "L.02" }],
  });
  await assertRejected(client, bridge, "revit_set_sheet_parameters", {
    values: [{ sheet_id: 101, name: "Phase" }],
  });
  await client.close();
});

test("revit_set_sheet_parameters: a sheet missing the parameter lands in failed while the rest are written", async () => {
  const bridge = fakeBridge(async () => ({
    updated: [
      { sheetId: 101, number: "L.02.001", name: "Phase", value: "L.02", display: "L.02" },
      { sheetId: 102, number: "L.03.001", name: "Phase", value: "L.03", display: "L.03" },
    ],
    failed: [
      {
        sheetId: 103,
        name: "Phase",
        code: "PARAMETER_NOT_FOUND",
        reason: 'Sheet L.09.001 (103) has no instance parameter named "Phase".',
      },
    ],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_sheet_parameters",
    arguments: {
      values: [
        { sheet_id: 101, name: "Phase", value: "L.02" },
        { sheet_id: 102, name: "Phase", value: "L.03" },
        { sheet_id: 103, name: "Phase", value: "L.09" },
      ],
    },
  });
  assert.equal(result.isError, undefined);
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.updated.length, 2);
  assert.equal(payload.failed[0].code, "PARAMETER_NOT_FOUND");
  await client.close();
});

test("revit_set_sheet_parameters: the description points at the per-element tool for one shared value", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  const description = tools.find((t) => t.name === "revit_set_sheet_parameters").description;
  assert.match(description, /one undo step/);
  assert.match(description, /revit_set_parameters/);
  assert.match(description, /Instance parameters only/);
  await client.close();
});
