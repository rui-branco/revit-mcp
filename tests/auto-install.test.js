import { test } from "node:test";
import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { PassThrough } from "node:stream";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { autoInstallBridge, isInstalled, OPT_OUT_ENV } from "../lib/auto-install.js";
import { installerArgv } from "../lib/tools/install.js";

// Nothing here touches this machine: PowerShell is faked through the same spawn
// seam the install tool uses, and the Addins folder is a fake fs. install.ps1
// is never run.

function fakeSpawn({ stdout = "", stderr = "", code = 0, missing = [], hang = false } = {}) {
  const calls = [];
  const spawnImpl = (exe, argv, options) => {
    const child = new EventEmitter();
    child.stdout = new PassThrough();
    child.stderr = new PassThrough();
    child.killed = false;
    child.kill = () => (child.killed = true);
    calls.push({ exe, argv, options, child });

    setImmediate(() => {
      if (missing.includes(exe)) {
        const err = new Error(`spawn ${exe} ENOENT`);
        err.code = "ENOENT";
        child.emit("error", err);
        return;
      }
      if (hang) return;
      child.stdout.end(stdout);
      child.stderr.end(stderr);
      setImmediate(() => child.emit("close", code));
    });

    return child;
  };
  spawnImpl.calls = calls;
  return spawnImpl;
}

function okJson() {
  return JSON.stringify({
    ok: true,
    action: "install",
    exitCode: 0,
    message: "Restart Revit to load the bridge",
    versions: [{ version: "2026", status: "installed" }],
    warnings: [],
    errors: [],
  });
}

// An Addins folder with a manifest under each year given.
function fakeFs(years = [], manifests = years) {
  return {
    readdirSync(dir) {
      if (dir !== path.join("C:\\Users\\x\\AppData\\Roaming", "Autodesk", "Revit", "Addins")) {
        const err = new Error(`ENOENT: no such file or directory, scandir '${dir}'`);
        err.code = "ENOENT";
        throw err;
      }
      return years;
    },
    existsSync(file) {
      return manifests.some(
        (year) => file === path.join("C:\\Users\\x\\AppData\\Roaming", "Autodesk", "Revit", "Addins", year, "RevitMcpBridge.addin"),
      );
    },
  };
}

const WINDOWS_ENV = { APPDATA: "C:\\Users\\x\\AppData\\Roaming" };

// A write() that a test can await, so the background install can be observed
// without the production code handing its promise back.
function collectWrites() {
  const lines = [];
  let resolve;
  const written = new Promise((r) => (resolve = r));
  const write = (line) => {
    lines.push(line);
    resolve(line);
  };
  write.lines = lines;
  write.next = () => written;
  return write;
}

// --- detection ---------------------------------------------------------------

test("isInstalled: the manifest under any supported year counts as installed", () => {
  assert.equal(isInstalled({ env: WINDOWS_ENV, fsImpl: fakeFs(["2025"]) }), true);
  assert.equal(isInstalled({ env: WINDOWS_ENV, fsImpl: fakeFs(["2026", "2027"]) }), true);
  assert.equal(isInstalled({ env: WINDOWS_ENV, fsImpl: fakeFs(["2024", "2026"], ["2026"]) }), true);
});

test("isInstalled: an add-ins folder without the manifest is not installed", () => {
  assert.equal(isInstalled({ env: WINDOWS_ENV, fsImpl: fakeFs(["2026"], []) }), false);
});

test("isInstalled: a manifest under a Revit that cannot load this build does not count", () => {
  // 2024 and earlier are .NET Framework; install.ps1 refuses them.
  assert.equal(isInstalled({ env: WINDOWS_ENV, fsImpl: fakeFs(["2024"]) }), false);
});

test("isInstalled: no Addins folder, and no APPDATA at all, are both just 'not installed'", () => {
  assert.equal(isInstalled({ env: WINDOWS_ENV, fsImpl: fakeFs([]) }), false);
  assert.equal(isInstalled({ env: {}, fsImpl: fakeFs(["2026"]) }), false);
});

// --- when it does nothing ----------------------------------------------------

test("autoInstallBridge: already installed means no installer and no output", async () => {
  const spawnImpl = fakeSpawn({ stdout: okJson() });
  const write = collectWrites();
  const status = autoInstallBridge({
    env: WINDOWS_ENV,
    platform: "win32",
    fsImpl: fakeFs(["2026"]),
    spawnImpl,
    write,
  });
  assert.equal(status, "already-installed");
  await new Promise((r) => setImmediate(r));
  assert.equal(spawnImpl.calls.length, 0);
  assert.deepEqual(write.lines, []);
});

test(`autoInstallBridge: ${OPT_OUT_ENV} turns it off`, async () => {
  const spawnImpl = fakeSpawn({ stdout: okJson() });
  const write = collectWrites();
  const status = autoInstallBridge({
    env: { ...WINDOWS_ENV, [OPT_OUT_ENV]: "1" },
    platform: "win32",
    fsImpl: fakeFs([]),
    spawnImpl,
    write,
  });
  assert.equal(status, "opted-out");
  await new Promise((r) => setImmediate(r));
  assert.equal(spawnImpl.calls.length, 0);
  assert.deepEqual(write.lines, []);
});

test("autoInstallBridge: the env var is named REVIT_MCP_NO_AUTO_INSTALL", () => {
  assert.equal(OPT_OUT_ENV, "REVIT_MCP_NO_AUTO_INSTALL");
});

test("autoInstallBridge: off Windows it does nothing at all", async () => {
  for (const platform of ["darwin", "linux"]) {
    const spawnImpl = fakeSpawn({ stdout: okJson() });
    const write = collectWrites();
    const status = autoInstallBridge({
      env: WINDOWS_ENV,
      platform,
      // Reading the Addins folder would be wrong here too, so blow up if it is.
      fsImpl: {
        readdirSync() {
          throw new Error("the Addins folder must not be looked for off Windows");
        },
        existsSync() {
          throw new Error("the Addins folder must not be looked for off Windows");
        },
      },
      spawnImpl,
      write,
    });
    assert.equal(status, "not-windows");
    await new Promise((r) => setImmediate(r));
    assert.equal(spawnImpl.calls.length, 0);
    assert.deepEqual(write.lines, []);
  }
});

// --- when it installs --------------------------------------------------------

test("autoInstallBridge: missing bridge runs the same installer the tool runs", async () => {
  const spawnImpl = fakeSpawn({ stdout: okJson() });
  const write = collectWrites();
  const status = autoInstallBridge({
    env: WINDOWS_ENV,
    platform: "win32",
    fsImpl: fakeFs([]),
    spawnImpl,
    write,
  });
  assert.equal(status, "started");
  await write.next();
  assert.deepEqual(spawnImpl.calls[0].argv, installerArgv());
  assert.match(write.lines[0], /RESTART REVIT/);
  assert.equal(write.lines.length, 1);
});

test("autoInstallBridge: a failed install is reported on stderr, never thrown", async () => {
  const spawnImpl = fakeSpawn({ missing: ["pwsh.exe", "powershell.exe"] });
  const write = collectWrites();
  const status = autoInstallBridge({
    env: WINDOWS_ENV,
    platform: "win32",
    fsImpl: fakeFs([]),
    spawnImpl,
    write,
  });
  assert.equal(status, "started");
  await write.next();
  assert.match(write.lines[0], /Could not start PowerShell/);
  assert.match(write.lines[0], new RegExp(OPT_OUT_ENV));
});

test("autoInstallBridge: install.ps1 failing is swallowed with its explanation", async () => {
  const spawnImpl = fakeSpawn({
    stdout: JSON.stringify({ ok: false, action: "install", exitCode: 2, errors: ["no Revit"] }),
    code: 2,
  });
  const write = collectWrites();
  autoInstallBridge({
    env: WINDOWS_ENV,
    platform: "win32",
    fsImpl: fakeFs([]),
    spawnImpl,
    write,
  });
  await write.next();
  assert.match(write.lines[0], /No Revit installation was found/);
  assert.match(write.lines[0], /revit_install_bridge/);
});

test("autoInstallBridge: returns before the installer does, so startup cannot wait on it", async () => {
  const spawnImpl = fakeSpawn({ hang: true });
  const write = collectWrites();
  const status = autoInstallBridge({
    env: WINDOWS_ENV,
    platform: "win32",
    fsImpl: fakeFs([]),
    spawnImpl,
    write,
    timeoutMs: 25,
  });
  // Back already, with the installer still running and nothing written yet.
  assert.equal(status, "started");
  assert.deepEqual(write.lines, []);
  assert.equal(typeof status, "string", "a promise here would let a caller await the install");

  await write.next();
  assert.match(write.lines[0], /could not install/i);
  assert.equal(spawnImpl.calls[0].child.killed, true);
});

// --- wiring ------------------------------------------------------------------

test("index.js: auto-install runs at startup, after connect and without await", () => {
  const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
  const source = fs.readFileSync(path.join(repoRoot, "index.js"), "utf8");

  const main = source.slice(source.indexOf("async function main()"));
  assert.match(main, /await server\.connect\(transport\);[\s\S]*autoInstallBridge\(\);/);
  assert.equal(/await autoInstallBridge\(/.test(main), false, "awaiting it would delay startup");

  // createRevitServer() is what every test in this suite builds; auto-install
  // belongs to the stdio entry point alone.
  const factory = source.slice(
    source.indexOf("export function createRevitServer("),
    source.indexOf("async function main()"),
  );
  assert.equal(factory.includes("autoInstallBridge"), false);
});

test("auto-install: nothing in it writes to stdout", () => {
  const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
  const source = fs.readFileSync(path.join(repoRoot, "lib", "auto-install.js"), "utf8");

  // stdout is the JSON-RPC channel: one stray byte corrupts the protocol.
  assert.equal(/process\.stdout|console\.(log|info|dir|table)/.test(source), false);
  assert.match(source, /process\.stderr\.write/);
});
