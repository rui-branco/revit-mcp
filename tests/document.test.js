import { test } from "node:test";
import assert from "node:assert/strict";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { createRevitServer } from "../index.js";

// The document lifecycle tools: save, save as, open, close. Same setup as the
// other suites — a real MCP client over an in-memory transport so the schemas
// doing the validating are the SDK's, with the bridge faked. Nothing here
// starts Revit, opens a socket or touches a file.

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

// --- endpoint paths ----------------------------------------------------------

test("the document lifecycle tools hit their own endpoints", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({ name: "revit_save", arguments: {} });
  await client.callTool({
    name: "revit_save_as",
    arguments: { save_path: "C:\\Projects\\House.rvt" },
  });
  await client.callTool({
    name: "revit_open_project",
    arguments: { path: "C:\\Projects\\House.rvt" },
  });
  await client.callTool({ name: "revit_close_project", arguments: {} });
  assert.deepEqual(
    bridge.calls.map((c) => c.endpoint),
    ["/document/save", "/document/save-as", "/document/open", "/document/close"],
  );
  await client.close();
});

// --- revit_save --------------------------------------------------------------

test("revit_save: takes no arguments and forwards an empty body", async () => {
  const bridge = fakeBridge(async () => ({ path: "C:\\Projects\\House.rvt", saved: true }));
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_save", arguments: {} });
  assert.deepEqual(bridge.calls[0], { endpoint: "/document/save", payload: undefined });
  assert.equal(textOf(result), '{"path":"C:\\\\Projects\\\\House.rvt","saved":true}');
  await client.close();
});

test("revit_save: NOT_SAVEABLE is surfaced, not swallowed", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      "Revit failed on /document/save: The active document has never been saved, so it has no path to save to. Use /revit-mcp/document/save-as with a \"savePath\" instead.",
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_save", arguments: {} });
  assert.match(textOf(result), /^Error: Revit failed on \/document\/save/);
  assert.match(textOf(result), /never been saved/);
  await client.close();
});

// --- revit_save_as -----------------------------------------------------------

test("revit_save_as: save_path maps to savePath and overwrite defaults to false", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_save_as",
    arguments: { save_path: "C:\\Projects\\House.rvt" },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/document/save-as",
    payload: { savePath: "C:\\Projects\\House.rvt", overwrite: false },
  });
  await client.close();
});

test("revit_save_as: overwrite is forwarded when the caller asks for it", async () => {
  const bridge = fakeBridge(async () => ({ path: "C:\\Projects\\House.rvt" }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_save_as",
    arguments: { save_path: "C:\\Projects\\House.rvt", overwrite: true },
  });
  assert.equal(bridge.calls[0].payload.overwrite, true);
  assert.equal(textOf(result), '{"path":"C:\\\\Projects\\\\House.rvt"}');
  await client.close();
});

test("revit_save_as: rejects a missing or empty save_path and a non-boolean overwrite", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_save_as", {});
  await assertRejected(client, bridge, "revit_save_as", { save_path: "" });
  await assertRejected(client, bridge, "revit_save_as", {
    save_path: "C:\\Projects\\House.rvt",
    overwrite: "yes",
  });
  await client.close();
});

test("revit_save_as: FILE_EXISTS is surfaced, not swallowed", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      'Revit failed on /document/save-as: "C:\\Projects\\House.rvt" already exists. Pass {"overwrite": true} to replace it.',
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_save_as",
    arguments: { save_path: "C:\\Projects\\House.rvt" },
  });
  assert.match(textOf(result), /already exists/);
  assert.match(textOf(result), /overwrite/);
  await client.close();
});

// --- revit_open_project ------------------------------------------------------

test("revit_open_project: the path is forwarded unchanged", async () => {
  const bridge = fakeBridge(async () => ({ path: "C:\\Projects\\House.rvt", title: "House" }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_open_project",
    arguments: { path: "C:\\Projects\\House.rvt" },
  });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/document/open",
    payload: { path: "C:\\Projects\\House.rvt" },
  });
  assert.match(textOf(result), /"title":"House"/);
  await client.close();
});

test("revit_open_project: rejects a missing or empty path", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_open_project", {});
  await assertRejected(client, bridge, "revit_open_project", { path: "" });
  await assertRejected(client, bridge, "revit_open_project", { path: 42 });
  await client.close();
});

test("revit_open_project: FILE_NOT_FOUND is surfaced, not swallowed", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      'Revit failed on /document/open: No file at "C:\\nope.rvt". Pass the full path of an existing .rvt file.',
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_open_project",
    arguments: { path: "C:\\nope.rvt" },
  });
  assert.match(textOf(result), /^Error: Revit failed on \/document\/open/);
  assert.match(textOf(result), /No file at/);
  await client.close();
});

// --- revit_close_project -----------------------------------------------------

test("revit_close_project: save defaults to false, so changes are discarded", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({ name: "revit_close_project", arguments: {} });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/document/close",
    payload: { save: false },
  });
  await client.close();
});

test("revit_close_project: save true is forwarded", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({ name: "revit_close_project", arguments: { save: true } });
  assert.equal(bridge.calls[0].payload.save, true);
  await client.close();
});

test("revit_close_project: rejects a non-boolean save", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_close_project", { save: "true" });
  await client.close();
});

test("revit_close_project: closing nothing is data, not an error", async () => {
  const bridge = fakeBridge(async () => ({ closed: false }));
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_close_project", arguments: {} });
  assert.equal(result.isError, undefined);
  assert.equal(textOf(result), '{"closed":false}');
  await client.close();
});

test("revit_close_project: a closed document reports its path and title", async () => {
  const bridge = fakeBridge(async () => ({
    closed: true,
    path: "C:\\Projects\\House.rvt",
    title: "House",
  }));
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_close_project", arguments: { save: true } });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.closed, true);
  assert.equal(payload.title, "House");
  await client.close();
});

test("revit_close_project: the response says which document became active", async () => {
  const bridge = fakeBridge(async () => ({
    closed: true,
    path: "C:\\Projects\\House.rvt",
    title: "House",
    activePath: "C:\\Projects\\Garage.rvt",
    activeTitle: "Garage",
    activeIsScratch: false,
  }));
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_close_project", arguments: {} });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.closed, true);
  assert.equal(payload.activePath, "C:\\Projects\\Garage.rvt");
  assert.equal(payload.activeTitle, "Garage");
  assert.equal(payload.activeIsScratch, false);
  await client.close();
});

test("revit_close_project: closing the last document reports the scratch that took its place", async () => {
  const bridge = fakeBridge(async () => ({
    closed: true,
    path: "C:\\Projects\\House.rvt",
    title: "House",
    activePath: "C:\\Users\\me\\AppData\\Local\\Temp\\revit-mcp-scratch.rvt",
    activeTitle: "revit-mcp-scratch",
    activeIsScratch: true,
  }));
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_close_project", arguments: {} });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.activeIsScratch, true);
  assert.match(payload.activePath, /revit-mcp-scratch\.rvt$/);
  await client.close();
});

test("revit_close_project: LAST_DOCUMENT_CANNOT_CLOSE is surfaced, not swallowed", async () => {
  const message =
    "Revit failed on /document/close: This is the only document Revit has open, and Revit will not close the active document from the API. The bridge tried to make a scratch project active in its place so this one could be closed, and could not: Saving is not allowed in the current application mode.";
  const bridge = fakeBridge(async () => {
    throw new Error(message);
  });
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_close_project", arguments: {} });
  assert.equal(textOf(result), `Error: ${message}`);
  assert.match(textOf(result), /only document Revit has open/);
  await client.close();
});

// --- no active document ------------------------------------------------------

test("NO_ACTIVE_DOCUMENT from save / save_as reaches the model verbatim", async () => {
  for (const name of ["revit_save", "revit_save_as"]) {
    const bridge = fakeBridge(async (endpoint) => {
      throw new Error(
        `Revit failed on ${endpoint}: Revit has no active document. Open a project in Revit and make its window active, then retry.`,
      );
    });
    const client = await connect(bridge);
    const result = await client.callTool({
      name,
      arguments: name === "revit_save" ? {} : { save_path: "C:\\Projects\\House.rvt" },
    });
    assert.match(textOf(result), /no active document/);
    await client.close();
  }
});
