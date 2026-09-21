import { test } from "node:test";
import assert from "node:assert/strict";
import { createBridge } from "../lib/bridge.js";

// The bridge is tested with its HTTP layer replaced: no Revit, no sockets.
// `request` is the only seam — everything below it is node's http module.

function transportError(code) {
  return () => {
    const err = new Error(`fake ${code}`);
    err.code = code;
    throw err;
  };
}

function responds(statusCode, body) {
  return async () => ({ statusCode, body });
}

// Grab the thrown message without assert.rejects' matcher gymnastics.
async function callError(bridge, endpoint = "/status", payload = {}) {
  try {
    await bridge.call(endpoint, payload);
  } catch (err) {
    return err.message;
  }
  assert.fail("expected bridge.call to throw");
}

// --- configuration -----------------------------------------------------------

test("createBridge: defaults to localhost:48884 and a 30s timeout", () => {
  const bridge = createBridge({ request: responds(200, "{}") });
  assert.equal(bridge.baseUrl, "http://localhost:48884/revit-mcp");
  assert.equal(bridge.timeoutMs, 30000);
});

test("createBridge: REVIT_MCP_URL and REVIT_MCP_TIMEOUT override the defaults", () => {
  process.env.REVIT_MCP_URL = "http://127.0.0.1:9999/other";
  process.env.REVIT_MCP_TIMEOUT = "5000";
  try {
    const bridge = createBridge({ request: responds(200, "{}") });
    assert.equal(bridge.baseUrl, "http://127.0.0.1:9999/other");
    assert.equal(bridge.timeoutMs, 5000);
  } finally {
    delete process.env.REVIT_MCP_URL;
    delete process.env.REVIT_MCP_TIMEOUT;
  }
});

test("createBridge: a trailing slash on the base URL does not double up the path", async () => {
  const seen = [];
  const bridge = createBridge({
    url: "http://localhost:48884/revit-mcp/",
    request: async (url) => {
      seen.push(url);
      return { statusCode: 200, body: "{}" };
    },
  });
  await bridge.call("/status");
  assert.deepEqual(seen, ["http://localhost:48884/revit-mcp/status"]);
});

test("call: posts the payload to baseUrl + endpoint and returns parsed JSON", async () => {
  const seen = [];
  const bridge = createBridge({
    url: "http://localhost:48884/revit-mcp",
    timeoutMs: 1234,
    request: async (url, payload, timeoutMs) => {
      seen.push({ url, payload, timeoutMs });
      return { statusCode: 200, body: '{"total":2,"rows":[]}' };
    },
  });
  const result = await bridge.call("/query", { limit: 100 });
  assert.deepEqual(result, { total: 2, rows: [] });
  assert.deepEqual(seen, [
    {
      url: "http://localhost:48884/revit-mcp/query",
      payload: { limit: 100 },
      timeoutMs: 1234,
    },
  ]);
});

// --- failure mode 1: connection refused --------------------------------------

test("call: ECONNREFUSED explains Revit is not running, never leaks the raw code", async () => {
  const bridge = createBridge({ request: transportError("ECONNREFUSED") });
  const message = await callError(bridge);
  assert.match(message, /connection refused/);
  assert.match(message, /Revit is not running/);
  assert.match(message, /add-in/);
  assert.match(message, /REVIT_MCP_URL/);
  // The whole point: the model must never be handed a bare node error.
  assert.doesNotMatch(message, /ECONNREFUSED/);
});

test("call: other transport errors still produce a bridge-shaped message", async () => {
  const bridge = createBridge({ request: transportError("ECONNRESET") });
  const message = await callError(bridge);
  assert.match(message, /Cannot reach Revit at http:\/\/localhost:48884\/revit-mcp/);
});

// --- failure mode 2: extension not loaded (404) ------------------------------

test("call: 404 blames the bridge add-in, not the connection", async () => {
  const bridge = createBridge({ request: responds(404, "Not Found") });
  const message = await callError(bridge, "/levels");
  assert.match(message, /HTTP 404/);
  assert.match(message, /\/levels/);
  assert.match(message, /add-in is not loaded/);
  assert.match(message, /Addins/);
});

test("call: UNKNOWN_ENDPOINT keeps the add-in advice and adds Revit's own detail", async () => {
  const bridge = createBridge({
    request: responds(
      404,
      JSON.stringify({
        error: {
          code: "UNKNOWN_ENDPOINT",
          message:
            "No such endpoint: /revit-mcp/parameters/set. Known endpoints: levels, status.",
        },
      }),
    ),
  });
  const message = await callError(bridge, "/parameters/set");
  assert.match(message, /add-in is not loaded/);
  assert.match(message, /Known endpoints: levels, status/);
});

test("call: a business 404 keeps its code and message instead of blaming the add-in", async () => {
  // The bug this guards: a handler that ran, looked, and found nothing answers
  // 404 too. Telling the model to restart Revit over it sends it chasing a
  // fault that does not exist while the real reason - the parameter is not on
  // that element - is thrown away.
  const bridge = createBridge({
    request: responds(
      404,
      JSON.stringify({
        error: {
          code: "PARAMETER_NOT_FOUND",
          message:
            'Element 222906 has no parameter "Elevation from Level". Its parameters: Level, Offset.',
        },
      }),
    ),
  });
  const message = await callError(bridge, "/parameters/set");
  assert.match(message, /Revit failed on \/parameters\/set/);
  assert.match(message, /PARAMETER_NOT_FOUND/);
  assert.match(message, /Elevation from Level/);
  assert.doesNotMatch(message, /add-in is not loaded/);
  assert.doesNotMatch(message, /restart Revit/);
});

test("call: a structured code is surfaced on any status, not only 404", async () => {
  const bridge = createBridge({
    request: responds(
      409,
      JSON.stringify({
        error: {
          code: "TRANSACTION_ROLLED_BACK",
          message: "Revit rolled the transaction back. Nothing was changed.",
        },
      }),
    ),
  });
  const message = await callError(bridge, "/elements/move");
  assert.match(
    message,
    /Revit failed on \/elements\/move: TRANSACTION_ROLLED_BACK — Revit rolled/,
  );
});

// --- failure mode 3: timeout -------------------------------------------------

test("call: a timeout points at a busy Revit or a modal dialog", async () => {
  const bridge = createBridge({
    timeoutMs: 2500,
    request: transportError("ETIMEDOUT"),
  });
  const message = await callError(bridge, "/walls/create");
  assert.match(message, /did not answer \/walls\/create within 2500ms/);
  assert.match(message, /modal dialog/);
  assert.match(message, /REVIT_MCP_TIMEOUT/);
});

// --- failure mode 4: the handler blew up inside Revit ------------------------

test("call: an error payload surfaces the Revit-side message and stack", async () => {
  const bridge = createBridge({
    request: responds(
      500,
      JSON.stringify({
        error: {
          message: "The name entered is already in use. Enter a unique name.",
          stack: "at CreateLevels(payload)\nat Dispatch(request)",
        },
      }),
    ),
  });
  const message = await callError(bridge, "/levels/create");
  assert.match(message, /Revit failed on \/levels\/create/);
  assert.match(message, /already in use/);
  assert.match(message, /at CreateLevels\(payload\)/);
});

test("call: an error payload sent with HTTP 200 is still an error", async () => {
  // A handler that catches its own exception may answer 200 with an error body.
  const bridge = createBridge({
    request: responds(200, '{"error":"no document is open"}'),
  });
  const message = await callError(bridge, "/status");
  assert.match(message, /Revit failed on \/status: no document is open/);
});

// --- everything else ---------------------------------------------------------

test("call: a non-JSON body is reported as such, with a snippet", async () => {
  const bridge = createBridge({
    request: responds(200, "<html>Server Error</html>"),
  });
  const message = await callError(bridge);
  assert.match(message, /non-JSON response/);
  assert.match(message, /<html>Server Error<\/html>/);
});

test("call: a 4xx/5xx without an error payload reports the status", async () => {
  const bridge = createBridge({ request: responds(503, '{"busy":true}') });
  const message = await callError(bridge, "/selection");
  assert.match(message, /HTTP 503 for \/selection/);
});
