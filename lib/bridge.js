// HTTP client for the Revit-side bridge.
//
// Node cannot call the Revit API: it is in-process .NET inside revit.exe. So
// every tool call becomes a small JSON POST to the bridge add-in listening on
// localhost, which does the Revit work and answers with compact JSON.
//
// The whole point of this module is that the model never sees a raw socket
// error. Each failure mode gets a message that says what is broken and what to
// do about it.

import http from "http";

const DEFAULT_URL = "http://localhost:48884/revit-mcp";
const DEFAULT_TIMEOUT_MS = 30000;

// Raw POST. Resolves { statusCode, body } for ANY status code — status handling
// belongs to call(), which knows the endpoint and can phrase a useful error.
// Rejects only on transport failure (refused, reset, timeout).
function httpRequest(url, payload, timeoutMs) {
  return new Promise((resolve, reject) => {
    const target = new URL(url);
    const data = JSON.stringify(payload);
    const req = http.request(
      {
        hostname: target.hostname,
        port: target.port || 80,
        path: target.pathname + target.search,
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Content-Length": Buffer.byteLength(data),
        },
      },
      (res) => {
        let body = "";
        res.setEncoding("utf8");
        res.on("data", (chunk) => (body += chunk));
        res.on("end", () => resolve({ statusCode: res.statusCode, body }));
      },
    );
    // Covers both "never connected" and "connected, then went quiet" — the
    // second is what a modal dialog in Revit looks like from out here.
    req.setTimeout(timeoutMs, () => {
      const err = new Error(`no response within ${timeoutMs}ms`);
      err.code = "ETIMEDOUT";
      req.destroy(err);
    });
    req.on("error", reject);
    req.write(data);
    req.end();
  });
}

// The bridge reports handler failures as a JSON body with an `error` key,
// either a bare string or { code, message, stack }. Returns null when the body
// is not an error payload.
function handlerError(parsed) {
  if (!parsed || typeof parsed !== "object") return null;
  const error = parsed.error;
  if (!error) return null;
  if (typeof error === "string") return { code: "", message: error, stack: "" };
  return {
    code: error.code || "",
    message: error.message || "unknown error",
    stack: error.stack || error.stackTrace || "",
  };
}

// The only two codes that mean "this URL is not a route here". The router
// answers an unrecognised path with UNKNOWN_PATH and an unrecognised route name
// with UNKNOWN_ENDPOINT; every other 404 comes from a handler that ran and
// found nothing (ELEMENT_NOT_FOUND, PARAMETER_NOT_FOUND, ...). Those two cases
// need opposite advice, so the status code alone cannot decide it.
const ROUTE_MISSING_CODES = new Set(["UNKNOWN_PATH", "UNKNOWN_ENDPOINT"]);

function noHandlerMessage(endpoint, baseUrl, detail) {
  return (
    `Revit answered but has no handler for ${endpoint} (HTTP 404). Something is listening on ${baseUrl}, so the bridge add-in is not loaded or is an older build. Check the .addin manifest in %APPDATA%\\Autodesk\\Revit\\Addins\\<version>\\ and restart Revit.` +
    (detail ? `\n${detail}` : "")
  );
}

export function createBridge({ url, timeoutMs, request = httpRequest } = {}) {
  // Trailing slashes make the joined path double up, and REVIT_MCP_URL is
  // hand-typed often enough to be worth normalising here.
  const baseUrl = (url || process.env.REVIT_MCP_URL || DEFAULT_URL).replace(
    /\/+$/,
    "",
  );
  const timeout =
    timeoutMs ||
    Number(process.env.REVIT_MCP_TIMEOUT) ||
    DEFAULT_TIMEOUT_MS;

  return {
    baseUrl,
    timeoutMs: timeout,

    async call(endpoint, payload = {}) {
      let response;
      try {
        response = await request(`${baseUrl}${endpoint}`, payload, timeout);
      } catch (err) {
        if (err.code === "ETIMEDOUT" || err.code === "ESOCKETTIMEDOUT") {
          throw new Error(
            `Revit did not answer ${endpoint} within ${timeout}ms. Revit is busy or blocked on a modal dialog — switch to Revit, dismiss any open dialog, and retry. Set REVIT_MCP_TIMEOUT (milliseconds) higher for long operations.`,
          );
        }
        if (err.code === "ECONNREFUSED") {
          throw new Error(
            `Cannot reach Revit at ${baseUrl} — connection refused. Revit is not running, or the revit-mcp bridge add-in is not listening. Start Revit and confirm the add-in loaded, then retry. Override the address with REVIT_MCP_URL.`,
          );
        }
        throw new Error(
          `Cannot reach Revit at ${baseUrl} — ${err.code || "request failed"}: ${err.message}`,
        );
      }

      let parsed;
      try {
        parsed = JSON.parse(response.body);
      } catch {
        // A 404 that is not even JSON is not this bridge answering.
        if (response.statusCode === 404) {
          throw new Error(noHandlerMessage(endpoint, baseUrl, ""));
        }
        throw new Error(
          `Revit returned a non-JSON response for ${endpoint} (HTTP ${response.statusCode}): ${response.body.slice(0, 200)}`,
        );
      }

      // A handler that blew up inside Revit — surface the Revit-side code,
      // message and stack verbatim, that is the only place the real cause
      // exists.
      const failure = handlerError(parsed);

      if (
        response.statusCode === 404 &&
        (!failure || ROUTE_MISSING_CODES.has(failure.code))
      ) {
        throw new Error(
          noHandlerMessage(endpoint, baseUrl, failure ? failure.message : ""),
        );
      }

      if (failure) {
        throw new Error(
          `Revit failed on ${endpoint}: ${failure.code ? `${failure.code} — ` : ""}${failure.message}${failure.stack ? `\n${failure.stack}` : ""}`,
        );
      }

      if (response.statusCode >= 400) {
        throw new Error(
          `Revit returned HTTP ${response.statusCode} for ${endpoint}: ${response.body.slice(0, 200)}`,
        );
      }

      return parsed;
    },
  };
}
