import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { createRevitServer } from "../index.js";

// The icons are read off a real MCP handshake over an in-memory transport, so
// what is checked is what a client receives. The bridge is never called.

const bridge = {
  baseUrl: "http://localhost:48884/revit-mcp",
  timeoutMs: 30000,
  async call() {
    throw new Error("the handshake must not reach the bridge");
  },
};

test("serverInfo carries the logo as a light and a dark base64 SVG data URI", async () => {
  const server = createRevitServer(bridge);
  const client = new Client({ name: "revit-mcp-tests", version: "1.0.0" });
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  await Promise.all([server.connect(serverTransport), client.connect(clientTransport)]);

  const { icons } = client.getServerVersion();
  assert.equal(icons.length, 2);
  const source = fs.readFileSync(new URL("../assets/icon.svg", import.meta.url), "utf8").trim();
  for (const [icon, theme, fill] of [
    [icons[0], "light", "#000000"],
    [icons[1], "dark", "#FFFFFF"],
  ]) {
    assert.equal(icon.mimeType, "image/svg+xml");
    assert.deepEqual(icon.sizes, ["any"]);
    assert.equal(icon.theme, theme);
    const base64 = icon.src.match(/^data:image\/svg\+xml;base64,([A-Za-z0-9+/]+={0,2})$/)?.[1];
    assert.ok(base64, `${theme} icon is not a base64 SVG data URI`);
    const svg = Buffer.from(base64, "base64").toString("utf8");
    assert.equal(Buffer.from(svg).toString("base64"), base64);
    assert.match(svg, /^<svg xmlns="http:\/\/www\.w3\.org\/2000\/svg" viewBox="0 0 24 24" fill="#[0-9A-F]{6}">.*<\/svg>$/);
    assert.equal(svg, source.replace('fill="#000000"', `fill="${fill}"`));
    assert.ok(svg.length < 4096);
  }

  await client.close();
});
