import { test } from "node:test";
import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { PassThrough } from "node:stream";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import {
  INSTALL_SCRIPT,
  INSTALL_TIMEOUT_MS,
  explainExitCode,
  installerArgv,
  registerInstallTools,
  runInstaller,
} from "../lib/tools/install.js";

// spawn is the only seam: no PowerShell, no dotnet, no Revit is ever started
// here. The fake child is a real EventEmitter with real streams so the
// production code's setEncoding/data/close handling is what gets exercised.

function fakeSpawn({ stdout = "", stderr = "", code = 0, missing = [], hang = false } = {}) {
  const calls = [];
  const spawnImpl = (exe, argv, options) => {
    const child = new EventEmitter();
    child.stdout = new PassThrough();
    child.stderr = new PassThrough();
    child.killed = false;
    child.kill = () => (child.killed = true);
    calls.push({ exe, argv, options, child, cwd: process.cwd() });

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

function okJson(extra = {}) {
  return JSON.stringify({
    ok: true,
    action: "install",
    exitCode: 0,
    message: "Restart Revit to load the bridge",
    addInName: "RevitMcpBridge",
    versions: [{ version: "2026", status: "installed" }],
    warnings: [],
    errors: [],
    ...extra,
  });
}

async function connectInstallTools(spawnImpl) {
  const server = new McpServer({ name: "revit", version: "1.0.0" });
  registerInstallTools(server, { spawnImpl });
  const client = new Client({ name: "revit-mcp-tests", version: "1.0.0" });
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  await Promise.all([server.connect(serverTransport), client.connect(clientTransport)]);
  return client;
}

function textOf(result) {
  return result.content.map((c) => c.text).join("\n");
}

// --- argv construction -------------------------------------------------------

test("installerArgv: the plain install command", () => {
  assert.deepEqual(installerArgv(), [
    "-NoProfile",
    "-NonInteractive",
    "-ExecutionPolicy",
    "Bypass",
    "-File",
    INSTALL_SCRIPT,
    "-Json",
  ]);
});

test("installerArgv: revit_version becomes -RevitVersion as its own argument", () => {
  const argv = installerArgv({ revitVersion: "2026" }, "C:\\x\\install.ps1");
  assert.deepEqual(argv.slice(4), ["-File", "C:\\x\\install.ps1", "-RevitVersion", "2026", "-Json"]);
});

test("installerArgv: skip_build becomes -SkipBuild, and only when asked for", () => {
  assert.ok(installerArgv({ skipBuild: true }).includes("-SkipBuild"));
  assert.equal(installerArgv({ skipBuild: false }).includes("-SkipBuild"), false);
  assert.equal(installerArgv({}).includes("-SkipBuild"), false);
});

test("installerArgv: uninstall adds -Uninstall, install never does", () => {
  assert.ok(installerArgv({ uninstall: true }).includes("-Uninstall"));
  assert.equal(installerArgv().includes("-Uninstall"), false);
});

test("installerArgv: both flags together, -Json always last", () => {
  const argv = installerArgv({ revitVersion: "2025", skipBuild: true }, "C:\\x\\install.ps1");
  assert.deepEqual(argv, [
    "-NoProfile",
    "-NonInteractive",
    "-ExecutionPolicy",
    "Bypass",
    "-File",
    "C:\\x\\install.ps1",
    "-RevitVersion",
    "2025",
    "-SkipBuild",
    "-Json",
  ]);
});

test("installerArgv: an argv, never a command string", () => {
  // A space in the path must stay one argument, and nothing may be quoted or
  // concatenated into something a shell would re-split.
  const argv = installerArgv({ revitVersion: "2026" }, "C:\\Program Files\\rb\\install.ps1");
  assert.ok(argv.includes("C:\\Program Files\\rb\\install.ps1"));
  for (const arg of argv) assert.equal(arg.includes(" -"), false, `looks concatenated: ${arg}`);
});

test("runInstaller: spawns with the argv it built, never a shell", async () => {
  const spawnImpl = fakeSpawn({ stdout: okJson() });
  await runInstaller({ revitVersion: "2026", skipBuild: true }, { spawnImpl });
  assert.equal(spawnImpl.calls.length, 1);
  assert.deepEqual(
    spawnImpl.calls[0].argv,
    installerArgv({ revitVersion: "2026", skipBuild: true }),
  );
  assert.equal(spawnImpl.calls[0].options.shell, undefined);
});

// --- which PowerShell --------------------------------------------------------

test("runInstaller: prefers pwsh.exe", async () => {
  const spawnImpl = fakeSpawn({ stdout: okJson() });
  await runInstaller({}, { spawnImpl });
  assert.deepEqual(
    spawnImpl.calls.map((c) => c.exe),
    ["pwsh.exe"],
  );
});

test("runInstaller: falls back to powershell.exe when pwsh.exe is absent", async () => {
  const spawnImpl = fakeSpawn({ stdout: okJson(), missing: ["pwsh.exe"] });
  const result = await runInstaller({}, { spawnImpl });
  assert.deepEqual(
    spawnImpl.calls.map((c) => c.exe),
    ["pwsh.exe", "powershell.exe"],
  );
  assert.equal(result.ok, true);
});

test("runInstaller: neither shell available is a readable error", async () => {
  const spawnImpl = fakeSpawn({ missing: ["pwsh.exe", "powershell.exe"] });
  await assert.rejects(runInstaller({}, { spawnImpl }), /Could not start PowerShell/);
});

// --- the -Json result --------------------------------------------------------

test("runInstaller: the script's JSON object is parsed and returned", async () => {
  const spawnImpl = fakeSpawn({ stdout: okJson() });
  const result = await runInstaller({}, { spawnImpl });
  assert.equal(result.ok, true);
  assert.equal(result.action, "install");
  assert.equal(result.addInName, "RevitMcpBridge");
  assert.deepEqual(result.versions, [{ version: "2026", status: "installed" }]);
  assert.equal(result.exitCode, 0);
  assert.match(result.explanation, /RESTARTED/);
});

test("runInstaller: trailing newlines around the JSON do not break parsing", async () => {
  const spawnImpl = fakeSpawn({ stdout: `\r\n${okJson()}\r\n` });
  const result = await runInstaller({}, { spawnImpl });
  assert.equal(result.ok, true);
});

test("runInstaller: uninstall passes -Uninstall and reports the uninstall action", async () => {
  const spawnImpl = fakeSpawn({
    stdout: JSON.stringify({ ok: true, action: "uninstall", exitCode: 0, versions: [] }),
  });
  const result = await runInstaller({ uninstall: true }, { spawnImpl });
  assert.ok(spawnImpl.calls[0].argv.includes("-Uninstall"));
  assert.equal(result.action, "uninstall");
  assert.match(result.explanation, /removed/);
});

// --- exit codes --------------------------------------------------------------

test("explainExitCode: every code install.ps1 uses has its own explanation", () => {
  const messages = [0, 1, 2, 3, 4, 5].map((code) => explainExitCode(code));
  assert.equal(new Set(messages).size, 6);
  assert.match(messages[0], /Success/);
  assert.match(messages[1], /unhandled failure/i);
  assert.match(messages[2], /No Revit installation was found/);
  assert.match(messages[3], /Revit 2025 or newer/);
  assert.match(messages[4], /dotnet build. failed/);
  assert.match(messages[5], /skip_build/);
});

test("explainExitCode: code 2 means 'nothing to uninstall' when uninstalling", () => {
  assert.match(explainExitCode(2, "uninstall"), /Nothing to uninstall/);
  assert.match(explainExitCode(2, "uninstall"), /Not a failure/);
  assert.notEqual(explainExitCode(2, "uninstall"), explainExitCode(2, "install"));
});

test("explainExitCode: an unknown code still names itself", () => {
  assert.match(explainExitCode(9), /exited with code 9/);
});

test("runInstaller: a non-zero exit is surfaced with its code, not as a generic failure", async () => {
  for (const code of [1, 2, 3, 4, 5]) {
    const spawnImpl = fakeSpawn({
      stdout: JSON.stringify({ ok: false, action: "install", exitCode: code, errors: ["boom"] }),
      code,
    });
    const result = await runInstaller({}, { spawnImpl });
    assert.equal(result.exitCode, code);
    assert.equal(result.explanation, explainExitCode(code, "install"));
    assert.deepEqual(result.errors, ["boom"]);
  }
});

test("runInstaller: exit 2 on uninstall is explained as nothing to remove", async () => {
  const spawnImpl = fakeSpawn({
    stdout: JSON.stringify({ ok: false, action: "uninstall", exitCode: 2 }),
    code: 2,
  });
  const result = await runInstaller({ uninstall: true }, { spawnImpl });
  assert.equal(result.exitCode, 2);
  assert.match(result.explanation, /Nothing to uninstall/);
});

test("runInstaller: exit 3 explains the Revit 2025+ floor", async () => {
  const spawnImpl = fakeSpawn({
    stdout: JSON.stringify({ ok: false, action: "install", exitCode: 3 }),
    code: 3,
  });
  const result = await runInstaller({ revitVersion: "2024" }, { spawnImpl });
  assert.equal(result.exitCode, 3);
  assert.match(result.explanation, /\.NET Framework 4\.8/);
});

// --- output that is not the promised JSON ------------------------------------

test("runInstaller: non-JSON stdout comes back raw instead of throwing", async () => {
  const spawnImpl = fakeSpawn({
    stdout: "install.ps1 : File cannot be loaded because running scripts is disabled\r\n",
    stderr: "At line:1 char:1",
    code: 1,
  });
  const result = await runInstaller({}, { spawnImpl });
  assert.equal(result.ok, false);
  assert.equal(result.exitCode, 1);
  assert.match(result.stdout, /running scripts is disabled/);
  assert.equal(result.stderr, "At line:1 char:1");
  assert.match(result.message, /did not print the JSON object/);
  assert.match(result.explanation, /unhandled failure/i);
});

test("runInstaller: empty stdout is surfaced, not parsed into nothing", async () => {
  const spawnImpl = fakeSpawn({ stdout: "", stderr: "access denied", code: 1 });
  const result = await runInstaller({}, { spawnImpl });
  assert.equal(result.ok, false);
  assert.equal(result.stdout, "");
  assert.equal(result.stderr, "access denied");
});

test("runInstaller: JSON that is not an object is treated as raw output", async () => {
  const spawnImpl = fakeSpawn({ stdout: '"just a string"', code: 0 });
  const result = await runInstaller({}, { spawnImpl });
  assert.equal(result.ok, false);
  assert.equal(result.stdout, '"just a string"');
});

// --- timeout -----------------------------------------------------------------

test("runInstaller: a timeout says the build is still running, not that it failed", async () => {
  const spawnImpl = fakeSpawn({ hang: true });
  const result = await runInstaller({}, { spawnImpl, timeoutMs: 25 });
  assert.equal(result.timedOut, true);
  assert.equal(result.timeoutMs, 25);
  assert.match(result.message, /not a failure/i);
  assert.match(result.message, /still be finishing|still running/i);
  assert.equal(spawnImpl.calls[0].child.killed, true);
});

test("the install timeout is generous enough for a dotnet build", () => {
  assert.equal(INSTALL_TIMEOUT_MS, 300000);
});

// --- script path -------------------------------------------------------------

test("INSTALL_SCRIPT: resolved from the module, not from the working directory", () => {
  const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
  assert.equal(INSTALL_SCRIPT, path.join(repoRoot, "revit-bridge", "install.ps1"));
  assert.ok(path.isAbsolute(INSTALL_SCRIPT));
  assert.ok(fs.existsSync(INSTALL_SCRIPT), "install.ps1 is not where the tool will look for it");
});

test("the prebuilt add-in is where install.ps1 expects it", () => {
  // The whole distribution model: the package ships a compiled DLL so an end
  // user needs neither the .NET SDK nor a build step. prepublishOnly runs
  // build:bridge and then this suite, so publishing without it cannot happen.
  const dll = path.join(path.dirname(INSTALL_SCRIPT), "dist", "RevitMcpBridge.dll");
  assert.ok(fs.existsSync(dll), `no prebuilt add-in at ${dll} — run: npm run build:bridge`);
});

test("runInstaller: the script path does not move when the working directory does", async () => {
  const original = process.cwd();
  try {
    process.chdir(os.tmpdir());
    const spawnImpl = fakeSpawn({ stdout: okJson() });
    await runInstaller({}, { spawnImpl });
    assert.equal(spawnImpl.calls[0].argv[5], INSTALL_SCRIPT);
    assert.notEqual(path.dirname(spawnImpl.calls[0].cwd), path.dirname(INSTALL_SCRIPT));
  } finally {
    process.chdir(original);
  }
});

// --- the tools themselves ----------------------------------------------------

test("revit_install_bridge: arguments reach the script, result reaches the model", async () => {
  const spawnImpl = fakeSpawn({ stdout: okJson() });
  const client = await connectInstallTools(spawnImpl);
  const result = await client.callTool({
    name: "revit_install_bridge",
    arguments: { revit_version: "2026", skip_build: true },
  });
  const argv = spawnImpl.calls[0].argv;
  assert.ok(argv.includes("-RevitVersion") && argv.includes("2026"));
  assert.ok(argv.includes("-SkipBuild"));
  assert.match(textOf(result), /"ok":true/);
  assert.match(textOf(result), /RESTARTED/);
  await client.close();
});

test("revit_install_bridge: omitted arguments mean no flags", async () => {
  const spawnImpl = fakeSpawn({ stdout: okJson() });
  const client = await connectInstallTools(spawnImpl);
  await client.callTool({ name: "revit_install_bridge", arguments: {} });
  assert.deepEqual(spawnImpl.calls[0].argv, installerArgv());
  await client.close();
});

test("revit_uninstall_bridge: runs the same script with -Uninstall", async () => {
  const spawnImpl = fakeSpawn({
    stdout: JSON.stringify({ ok: true, action: "uninstall", exitCode: 0 }),
  });
  const client = await connectInstallTools(spawnImpl);
  const result = await client.callTool({ name: "revit_uninstall_bridge", arguments: {} });
  assert.deepEqual(spawnImpl.calls[0].argv, installerArgv({ uninstall: true }));
  assert.match(textOf(result), /"action":"uninstall"/);
  await client.close();
});

test("install tools: both descriptions tell the model to restart Revit", async () => {
  const client = await connectInstallTools(fakeSpawn({ stdout: okJson() }));
  const { tools } = await client.listTools();
  assert.deepEqual(
    tools.map((t) => t.name),
    ["revit_install_bridge", "revit_uninstall_bridge"],
  );
  for (const tool of tools) {
    assert.match(tool.description, /RESTART/i, `${tool.name} does not mention restarting Revit`);
  }
  await client.close();
});

test("install tools: a failure to start PowerShell is reported, not thrown", async () => {
  const spawnImpl = fakeSpawn({ missing: ["pwsh.exe", "powershell.exe"] });
  const client = await connectInstallTools(spawnImpl);
  const result = await client.callTool({ name: "revit_install_bridge", arguments: {} });
  assert.match(textOf(result), /^Error: Could not start PowerShell/);
  await client.close();
});
