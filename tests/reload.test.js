import { test } from "node:test";
import assert from "node:assert/strict";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { createRevitServer } from "../index.js";

// revit_reload_bridge: swapping the bridge's logic assembly without restarting
// Revit. Same setup as the other suites — the bridge is faked, so nothing here
// loads an assembly or talks to Revit.

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

test("revit_reload_bridge: hits /reload and takes no arguments", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({ name: "revit_reload_bridge", arguments: {} });
  assert.deepEqual(bridge.calls[0], { endpoint: "/reload", payload: undefined });
  await client.close();
});

test("revit_reload_bridge: the new version and load time reach the model", async () => {
  const bridge = fakeBridge(async () => ({
    reloaded: true,
    version: "1.0.1",
    loadedAt: "2026-09-20 19:31:07.412",
    collectible: true,
    unloadedPrevious: true,
  }));
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_reload_bridge", arguments: {} });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.reloaded, true);
  assert.equal(payload.version, "1.0.1");
  assert.equal(payload.loadedAt, "2026-09-20 19:31:07.412");
  await client.close();
});

test("revit_reload_bridge: a context that did not unload is reported, not hidden", async () => {
  const bridge = fakeBridge(async () => ({
    reloaded: true,
    version: "1.0.1",
    loadedAt: "2026-09-20 19:31:07.412",
    collectible: true,
    unloadedPrevious: false,
    warning: "The new logic is live, but the previous load context did not unload.",
  }));
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_reload_bridge", arguments: {} });
  assert.equal(result.isError, undefined);
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.unloadedPrevious, false);
  assert.match(payload.warning, /did not unload/);
  await client.close();
});

test("revit_reload_bridge: RELOAD_FAILED is surfaced, not swallowed", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      "Revit failed on /reload: The bridge's handlers assembly is missing: C:\\Addins\\RevitMcpBridge.Handlers.dll.",
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_reload_bridge", arguments: {} });
  assert.match(textOf(result), /^Error: Revit failed on \/reload/);
  assert.match(textOf(result), /handlers assembly is missing/);
  await client.close();
});

test("revit_reload_bridge: its description tells the model to build first", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  const description = tools.find((t) => t.name === "revit_reload_bridge").description;
  assert.match(description, /build first/i);
  assert.match(description, /without restarting Revit/i);
  await client.close();
});
