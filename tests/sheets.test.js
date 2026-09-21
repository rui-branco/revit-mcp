import { test } from "node:test";
import assert from "node:assert/strict";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { createRevitServer } from "../index.js";
import { DEFAULT_TEMPLATE_PATH } from "../lib/tools/document.js";

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

// --- revit_new_project -------------------------------------------------------

test("revit_new_project: save_path and template_path map to savePath/templatePath", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_new_project",
    arguments: {
      save_path: "C:\\Projects\\House.rvt",
      template_path: "C:\\Templates\\Custom.rte",
    },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/document/new",
    payload: {
      savePath: "C:\\Projects\\House.rvt",
      templatePath: "C:\\Templates\\Custom.rte",
    },
  });
  await client.close();
});

test("revit_new_project: an omitted template_path becomes the metric English template", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_new_project",
    arguments: { save_path: "C:\\Projects\\House.rvt" },
  });
  assert.equal(bridge.calls[0].payload.templatePath, DEFAULT_TEMPLATE_PATH);
  assert.match(DEFAULT_TEMPLATE_PATH, /Default_M_ENG\.rte$/);
  await client.close();
});

test("revit_new_project: rejects a missing or empty save_path", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_new_project", {});
  await assertRejected(client, bridge, "revit_new_project", { save_path: "" });
  await assertRejected(client, bridge, "revit_new_project", {
    save_path: "C:\\Projects\\House.rvt",
    template_path: "",
  });
  await client.close();
});

test("revit_new_project: the created path and title reach the model", async () => {
  const bridge = fakeBridge(async () => ({
    path: "C:\\Projects\\House.rvt",
    title: "House",
    templatePath: DEFAULT_TEMPLATE_PATH,
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_new_project",
    arguments: { save_path: "C:\\Projects\\House.rvt" },
  });
  assert.match(textOf(result), /"title":"House"/);
  await client.close();
});

test("revit_new_project: overwrite is forwarded when the caller asks to rebuild in place", async () => {
  const bridge = fakeBridge(async () => ({
    path: "C:\\Projects\\House.rvt",
    title: "House",
    templatePath: DEFAULT_TEMPLATE_PATH,
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_new_project",
    arguments: { save_path: "C:\\Projects\\House.rvt", overwrite: true },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/document/new",
    payload: {
      savePath: "C:\\Projects\\House.rvt",
      templatePath: DEFAULT_TEMPLATE_PATH,
      overwrite: true,
    },
  });
  assert.match(textOf(result), /"title":"House"/);
  await client.close();
});

test("revit_new_project: overwrite is absent from the body unless the caller passes it", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_new_project",
    arguments: { save_path: "C:\\Projects\\House.rvt" },
  });
  // Absent, not false: the bridge's own default answers for anyone who never
  // mentions it, so a caller that says nothing can never trigger a delete.
  assert.equal("overwrite" in bridge.calls[0].payload, false);
  await client.close();
});

test("revit_new_project: an explicit overwrite false is forwarded as given", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_new_project",
    arguments: { save_path: "C:\\Projects\\House.rvt", overwrite: false },
  });
  assert.equal(bridge.calls[0].payload.overwrite, false);
  await client.close();
});

test("revit_new_project: rejects a non-boolean overwrite", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_new_project", {
    save_path: "C:\\Projects\\House.rvt",
    overwrite: "yes",
  });
  await client.close();
});

test("revit_new_project: FILE_LOCKED names the file it could not delete", async () => {
  const message =
    'Revit failed on /document/new: "C:\\Projects\\House.rvt" could not be deleted: The process cannot access the file because it is being used by another process. Something still has that file open - Revit itself, an Explorer preview, or another application.';
  const bridge = fakeBridge(async () => {
    throw new Error(message);
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_new_project",
    arguments: { save_path: "C:\\Projects\\House.rvt", overwrite: true },
  });
  assert.equal(textOf(result), `Error: ${message}`);
  assert.match(textOf(result), /could not be deleted/);
  await client.close();
});

test("revit_new_project: FILE_EXISTS and TEMPLATE_NOT_FOUND are surfaced, not swallowed", async () => {
  for (const message of [
    'Revit failed on /document/new: "C:\\Projects\\House.rvt" already exists. Pick a different "savePath".',
    'Revit failed on /document/new: No project template at "C:\\nope.rte".',
  ]) {
    const bridge = fakeBridge(async () => {
      throw new Error(message);
    });
    const client = await connect(bridge);
    const result = await client.callTool({
      name: "revit_new_project",
      arguments: { save_path: "C:\\Projects\\House.rvt" },
    });
    assert.equal(textOf(result), `Error: ${message}`);
    await client.close();
  }
});

// --- revit_list_titleblocks / revit_list_sheets ------------------------------

test("revit_list_titleblocks / revit_list_sheets hit their endpoints", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  for (const name of ["revit_list_titleblocks", "revit_list_sheets"]) {
    await client.callTool({ name, arguments: {} });
  }
  assert.deepEqual(
    bridge.calls.map((c) => c.endpoint),
    ["/titleblocks", "/sheets"],
  );
  await client.close();
});

test("revit_list_titleblocks: no loaded title block is an empty list, not an error", async () => {
  const bridge = fakeBridge(async () => []);
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_list_titleblocks", arguments: {} });
  assert.equal(result.isError, undefined);
  assert.equal(textOf(result), "[]");
  await client.close();
});

// --- revit_create_sheets -----------------------------------------------------

test("revit_create_sheets: forwards the batch, title_block_id mapped to titleBlockId", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_sheets",
    arguments: {
      sheets: [
        { number: "A101", name: "Site Plan" },
        { number: "A102", name: "Ground Floor Plan" },
      ],
      title_block_id: 123456,
    },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/sheets/create",
    payload: {
      sheets: [
        { number: "A101", name: "Site Plan" },
        { number: "A102", name: "Ground Floor Plan" },
      ],
      titleBlockId: 123456,
    },
  });
  await client.close();
});

test("revit_create_sheets: an omitted title_block_id is left for the bridge to resolve", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_create_sheets",
    arguments: { sheets: [{ number: "A101", name: "Site Plan" }] },
  });
  assert.equal(bridge.calls[0].endpoint, "/sheets/create");
  assert.deepEqual(bridge.calls[0].payload.sheets, [{ number: "A101", name: "Site Plan" }]);
  assert.equal(bridge.calls[0].payload.titleBlockId, undefined);
  await client.close();
});

test("revit_create_sheets: the whole batch goes in one call, so it is one undo step", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  const sheets = Array.from({ length: 29 }, (_, i) => ({
    number: `A${101 + i}`,
    name: `Sheet ${i + 1}`,
  }));
  await client.callTool({ name: "revit_create_sheets", arguments: { sheets } });
  assert.equal(bridge.calls.length, 1);
  assert.equal(bridge.calls[0].payload.sheets.length, 29);
  await client.close();
});

test("revit_create_sheets: rejects an empty batch and a sheet missing number or name", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_create_sheets", {});
  await assertRejected(client, bridge, "revit_create_sheets", { sheets: [] });
  await assertRejected(client, bridge, "revit_create_sheets", { sheets: [{ name: "Site Plan" }] });
  await assertRejected(client, bridge, "revit_create_sheets", { sheets: [{ number: "A101" }] });
  await assertRejected(client, bridge, "revit_create_sheets", {
    sheets: [{ number: "", name: "Site Plan" }],
  });
  await assertRejected(client, bridge, "revit_create_sheets", {
    sheets: [{ number: "A101", name: "" }],
  });
  await assertRejected(client, bridge, "revit_create_sheets", {
    sheets: [{ number: 101, name: "Site Plan" }],
  });
  await assertRejected(client, bridge, "revit_create_sheets", {
    sheets: [{ number: "A101", name: "Site Plan" }],
    title_block_id: 1.5,
  });
  await client.close();
});

test("revit_create_sheets: skipped duplicates come back as data, not an error", async () => {
  const bridge = fakeBridge(async () => ({
    created: [{ id: 987, number: "A102", name: "Ground Floor Plan" }],
    skipped: [{ number: "A101", reason: "The name entered is already in use. Enter a unique name." }],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_sheets",
    arguments: {
      sheets: [
        { number: "A101", name: "Site Plan" },
        { number: "A102", name: "Ground Floor Plan" },
      ],
    },
  });
  assert.equal(result.isError, undefined);
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.created.length, 1);
  assert.deepEqual(payload.skipped, [
    { number: "A101", reason: "The name entered is already in use. Enter a unique name." },
  ]);
  await client.close();
});

test("revit_create_sheets: NO_TITLEBLOCK is surfaced, not swallowed", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      "Revit failed on /sheets/create: This document has no title block family loaded, so no sheet can be created. Load a title block family in Revit and retry.",
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_create_sheets",
    arguments: { sheets: [{ number: "A101", name: "Site Plan" }] },
  });
  assert.match(textOf(result), /^Error: Revit failed on \/sheets\/create/);
  assert.match(textOf(result), /no title block family loaded/);
  await client.close();
});
