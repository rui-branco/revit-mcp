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

const GROUPS = [
  { name: "L.02", sheet_ids: [4201, 4202] },
  { name: "L.03", sheet_ids: [4203] },
];

test("revit_list_sheet_collections: hits its endpoint and reports members and loose sheets", async () => {
  const bridge = fakeBridge(async () => ({
    collectionCount: 1,
    collections: [
      {
        id: 512001,
        name: "L.02",
        sheetCount: 2,
        sheets: [
          { id: 4201, number: "L.02.01", name: "Planta geral" },
          { id: 4202, number: "L.02.02", name: "Cortes" },
        ],
      },
    ],
    unassignedSheetCount: 1,
    unassignedSheets: [{ id: 4203, number: "L.03.01", name: "Pormenores" }],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({ name: "revit_list_sheet_collections", arguments: {} });
  assert.deepEqual(bridge.calls, [{ endpoint: "/sheets/collections", payload: undefined }]);
  const payload = JSON.parse(textOf(result));
  assert.equal(payload.collections[0].sheets.length, 2);
  assert.equal(payload.unassignedSheets[0].id, 4203);
  await client.close();
});

test("revit_set_sheet_collections: sheet_ids maps to sheetIds and the whole batch goes in one call", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await client.callTool({
    name: "revit_set_sheet_collections",
    arguments: { collections: GROUPS, dry_run: false },
  });
  assert.deepEqual(bridge.calls, [
    {
      endpoint: "/sheets/set-collections",
      payload: {
        collections: [
          { name: "L.02", sheetIds: [4201, 4202] },
          { name: "L.03", sheetIds: [4203] },
        ],
        dryRun: false,
      },
    },
  ]);
  await client.close();
});

test("revit_set_sheet_collections: omitting dry_run leaves the bridge's default, which is a dry run", async () => {
  const bridge = fakeBridge(async () => ({ dryRun: true, collections: [] }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_sheet_collections",
    arguments: { collections: GROUPS },
  });
  assert.equal(bridge.calls[0].payload.dryRun, undefined);
  assert.equal(JSON.parse(textOf(result)).dryRun, true);
  await client.close();
});

test("revit_set_sheet_collections: rejects an empty batch, a blank name and an empty sheet list", async () => {
  const bridge = fakeBridge();
  const client = await connect(bridge);
  await assertRejected(client, bridge, "revit_set_sheet_collections", { collections: [] });
  await assertRejected(client, bridge, "revit_set_sheet_collections", {
    collections: [{ name: "", sheet_ids: [4201] }],
  });
  await assertRejected(client, bridge, "revit_set_sheet_collections", {
    collections: [{ name: "L.02", sheet_ids: [] }],
  });
  await assertRejected(client, bridge, "revit_set_sheet_collections", {
    collections: [{ name: "L.02", sheet_ids: [4201.5] }],
  });
  await client.close();
});

test("revit_set_sheet_collections: created, reused and unchanged come back per collection", async () => {
  const bridge = fakeBridge(async () => ({
    dryRun: false,
    collectionCount: 2,
    sheetCount: 3,
    collections: [
      {
        name: "L.02",
        action: "created",
        collectionId: 512001,
        requestedSheetCount: 2,
        sheetsBefore: [
          { id: 4201, number: "L.02.01", name: "Planta geral", collectionId: null, collectionName: null },
          { id: 4202, number: "L.02.02", name: "Cortes", collectionId: null, collectionName: null },
        ],
        memberSheets: [
          { id: 4201, number: "L.02.01", name: "Planta geral" },
          { id: 4202, number: "L.02.02", name: "Cortes" },
        ],
        missingSheetIds: [],
        renumberedSheets: [],
        verified: true,
      },
      {
        name: "L.03",
        action: "unchanged",
        collectionId: 512002,
        requestedSheetCount: 1,
        verified: true,
        missingSheetIds: [],
        renumberedSheets: [],
      },
    ],
  }));
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_sheet_collections",
    arguments: { collections: GROUPS, dry_run: false },
  });
  const payload = JSON.parse(textOf(result));
  assert.deepEqual(
    payload.collections.map((row) => [row.name, row.action, row.verified]),
    [
      ["L.02", "created", true],
      ["L.03", "unchanged", true],
    ],
  );
  // The evidence the sheets were not renumbered on the way into the collection.
  assert.deepEqual(payload.collections[0].renumberedSheets, []);
  assert.equal(payload.collections[0].sheetsBefore[0].number, "L.02.01");
  await client.close();
});

test("revit_set_sheet_collections: a sheet claimed by two collections is the bridge's refusal", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      'Revit failed on /sheets/set-collections: Sheet 4201 is listed in both "L.02" and "L.03". A sheet belongs to one collection.',
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_sheet_collections",
    arguments: {
      collections: [
        { name: "L.02", sheet_ids: [4201] },
        { name: "L.03", sheet_ids: [4201] },
      ],
      dry_run: false,
    },
  });
  assert.equal(bridge.calls.length, 1);
  assert.match(textOf(result), /belongs to one collection/);
  await client.close();
});

test("revit_set_sheet_collections: an assembly sheet and a prohibited name come back refused", async () => {
  const bridge = fakeBridge(async () => {
    throw new Error(
      "Revit failed on /sheets/set-collections: Sheet 4299 (A900) is an assembly sheet, and Revit does not allow those in a sheet collection.",
    );
  });
  const client = await connect(bridge);
  const result = await client.callTool({
    name: "revit_set_sheet_collections",
    arguments: { collections: [{ name: "L.02", sheet_ids: [4299] }], dry_run: false },
  });
  assert.match(textOf(result), /assembly sheet/);
  await client.close();
});

test("sheet collections: both tools are registered and the write says what it will not touch", async () => {
  const client = await connect(fakeBridge());
  const { tools } = await client.listTools();
  const names = tools.map((t) => t.name);
  assert.ok(names.includes("revit_list_sheet_collections"));
  assert.ok(names.includes("revit_set_sheet_collections"));

  const write = tools.find((t) => t.name === "revit_set_sheet_collections");
  assert.match(write.description, /dry_run defaults to TRUE/);
  assert.match(write.description, /REUSED/);
  assert.match(write.description, /one undo step/);
  assert.match(write.description, /numbers and names are left as they are/);
  assert.deepEqual(write.inputSchema.required, ["collections"]);
  await client.close();
});
