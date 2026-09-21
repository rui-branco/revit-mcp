import { test } from "node:test";
import assert from "node:assert/strict";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { createRevitServer } from "../index.js";

// The two unattended-operation tools: reading what the bridge suppressed, and
// switching the suppression off. Same setup as the other suites — a real MCP
// client over an in-memory transport with the bridge faked.

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

// --- revit_diagnostics -------------------------------------------------------

test("revit_diagnostics: hits /diagnostics and takes no arguments", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({ name: "revit_diagnostics", arguments: {} });
  assert.deepEqual(bridge.calls[0], { endpoint: "/diagnostics", payload: undefined });
  await client.close();
});

test("revit_diagnostics: a quiet buffer comes back as empty lists, not an error", async () => {
  const bridge = fakeBridge(async () => ({
    autoDismiss: true,
    capacity: 200,
    dropped: 0,
    dialogs: [],
    failures: [],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_diagnostics", arguments: {} });
  assert.equal(result.isError, undefined);
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.autoDismiss, true);
  assert.deepEqual(payload.dialogs, []);
  assert.deepEqual(payload.failures, []);
  await client.close();
});

test("revit_diagnostics: dismissed dialogs and resolved warnings reach the model intact", async () => {
  const bridge = fakeBridge(async () => ({
    autoDismiss: true,
    capacity: 200,
    dropped: 3,
    dialogs: [
      {
        at: "2026-09-20 19:31:07.412",
        dialogId: "TaskDialog_Unused_Levels",
        message: "Would you like to delete the unused levels?",
        result: 2,
        answered: true,
      },
    ],
    failures: [
      {
        at: "2026-09-20 19:31:08.004",
        transaction: "Create walls",
        severity: "Warning",
        message: "Highlighted walls overlap.",
        action: "DeleteWarning",
      },
    ],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_diagnostics", arguments: {} });
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.dialogs[0].result, 2);
  assert.equal(payload.dialogs[0].answered, true);
  assert.equal(payload.failures[0].action, "DeleteWarning");
  // "dropped" is how a caller learns the ring buffer overflowed rather than
  // reading a truncated list as a quiet one.
  assert.equal(payload.dropped, 3);
  await client.close();
});

test("revit_diagnostics: a bridge failure is reported, not swallowed", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error("Cannot reach Revit at http://localhost:48884/revit-mcp — connection refused.");
  });
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_diagnostics", arguments: {} });
  assert.match(textOf(result), /^Error: Cannot reach Revit/);
  await client.close();
});

// --- revit_set_auto_dismiss --------------------------------------------------

test("revit_set_auto_dismiss: enabled maps to autoDismiss, clear defaults to false", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({ name: "revit_set_auto_dismiss", arguments: { enabled: false } });
  assert.deepEqual(bridge.calls[0], {
    endpoint: "/diagnostics/config",
    payload: { autoDismiss: false, clear: false },
  });
  await client.close();
});

test("revit_set_auto_dismiss: clear is forwarded so a batch starts from an empty buffer", async () => {
  const bridge = fakeBridge(async () => ({ autoDismiss: true, cleared: true }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_auto_dismiss",
    arguments: { enabled: true, clear: true },
  });
  assert.deepEqual(bridge.calls[0].payload, { autoDismiss: true, clear: true });
  assert.equal(textOf(result), '{"autoDismiss":true,"cleared":true}');
  await client.close();
});

test("revit_set_auto_dismiss: rejects a missing or non-boolean enabled", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_set_auto_dismiss", {});
  await assertRejected(client, bridge, "revit_set_auto_dismiss", { enabled: "false" });
  await assertRejected(client, bridge, "revit_set_auto_dismiss", { enabled: 0 });
  await assertRejected(client, bridge, "revit_set_auto_dismiss", {
    enabled: true,
    clear: "yes",
  });
  await client.close();
});

// --- the descriptions carry the warning --------------------------------------

test("the diagnostics tools tell the model to check them after writing", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  const describe = (name) => tools.find((t) => t.name === name).description;
  assert.match(describe("revit_diagnostics"), /after every batch of writes/i);
  assert.match(describe("revit_diagnostics"), /did not ask for/i);
  assert.match(describe("revit_set_auto_dismiss"), /unattended/i);
  await client.close();
});
