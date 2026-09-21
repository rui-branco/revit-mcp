// Installing the bridge add-in. This is the one pair of tools that must work
// with Revit closed and the bridge absent — it is how the bridge gets there in
// the first place — so nothing here touches lib/bridge.js or the HTTP port.
//
// The work is done by revit-bridge/install.ps1, which detects Revit and copies
// the add-in into the Addins folder. The add-in ships prebuilt in
// revit-bridge/dist/, so the usual path needs no .NET SDK and no build; only a
// source checkout with no dist/ falls back to `dotnet build -c Release`. We run
// it with -Json, which makes it print one JSON object and nothing else.

import { spawn } from "child_process";
import path from "path";
import { fileURLToPath } from "url";
import { z } from "zod";

// Resolved from this module, never from process.cwd(): an MCP server is
// started by the client from whatever directory it likes.
export const INSTALL_SCRIPT = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
  "..",
  "revit-bridge",
  "install.ps1",
);

// Installing the bundled add-in is a file copy, but the source-checkout
// fallback still shells out to dotnet build, which on a cold NuGet cache is
// minutes, not seconds.
export const INSTALL_TIMEOUT_MS = 300000;

// PowerShell 7 if it is there, Windows PowerShell otherwise — the script is
// written to run under both.
const SHELLS = ["pwsh.exe", "powershell.exe"];

// install.ps1 exits with a code per failure mode, so none of them has to be
// guessed at from the prose.
export function explainExitCode(code, action = "install") {
  switch (code) {
    case 0:
      if (action === "uninstall") return "Success: the add-in was removed. Restart Revit to unload it.";
      return "Success: the add-in is installed. Revit must be RESTARTED before the bridge works — Revit only scans the Addins folder at startup.";
    case 1:
      return "The installer hit an unhandled failure. The `errors` and `log` fields say what threw.";
    case 2:
      if (action === "uninstall")
        return "Nothing to uninstall: no Revit installation and no Revit Addins folder was found, so there was nothing to remove. Not a failure.";
      return "No Revit installation was found under Program Files\\Autodesk. Install Revit, or pass revit_version to target a version installed somewhere else.";
    case 3:
      return "No supported Revit version. This add-in is .NET 8 and needs Revit 2025 or newer; Revit 2024 and earlier load add-ins on .NET Framework 4.8 and cannot run it. A revit_version that is not a four-digit year also lands here.";
    case 4:
      return "`dotnet build` failed, or the .NET SDK is not installed, so nothing was installed. This only happens in a source checkout with no prebuilt revit-bridge/dist/ — the published package ships one. The build output is in `log`.";
    case 5:
      return "Nothing to install: no prebuilt add-in in revit-bridge/dist/ and no Release build output either. Re-run without skip_build so the add-in gets built, or reinstall the package to restore the bundled binary.";
    default:
      return `install.ps1 exited with code ${code}.`;
  }
}

// Argv, never a command string: a path with a space in it must not become two
// arguments, and nothing here may be shell-interpreted.
export function installerArgv(
  { uninstall = false, revitVersion, skipBuild } = {},
  scriptPath = INSTALL_SCRIPT,
) {
  const argv = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath];
  if (uninstall) argv.push("-Uninstall");
  if (revitVersion) argv.push("-RevitVersion", revitVersion);
  if (skipBuild) argv.push("-SkipBuild");
  argv.push("-Json");
  return argv;
}

function runShell(exe, argv, spawnImpl, timeoutMs) {
  return new Promise((resolve, reject) => {
    const child = spawnImpl(exe, argv, { windowsHide: true });
    let stdout = "";
    let stderr = "";
    let timedOut = false;

    // Stop waiting rather than waiting for the kill to land: a dotnet build
    // that outlives the timeout must not hold the tool call open too.
    const timer = setTimeout(() => {
      timedOut = true;
      child.kill();
      resolve({ exe, code: null, stdout, stderr, timedOut });
    }, timeoutMs);

    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");
    child.stdout.on("data", (chunk) => (stdout += chunk));
    child.stderr.on("data", (chunk) => (stderr += chunk));

    child.on("error", (err) => {
      clearTimeout(timer);
      reject(err);
    });
    child.on("close", (code) => {
      clearTimeout(timer);
      resolve({ exe, code, stdout, stderr, timedOut });
    });
  });
}

async function runAnyShell(argv, spawnImpl, timeoutMs) {
  let lastError;
  for (const exe of SHELLS) {
    try {
      return await runShell(exe, argv, spawnImpl, timeoutMs);
    } catch (err) {
      // Only "that executable does not exist" is worth falling back on.
      if (err.code !== "ENOENT") throw err;
      lastError = err;
    }
  }
  throw new Error(
    `Could not start PowerShell: neither ${SHELLS.join(" nor ")} could be launched (${lastError.message}). The Revit bridge only installs on Windows.`,
  );
}

// -Json promises a single JSON object as the only stdout. Anything else means
// the script died before it got there, and the raw output is the evidence.
function parseResult(stdout) {
  try {
    const parsed = JSON.parse(stdout.trim());
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return null;
    return parsed;
  } catch {
    return null;
  }
}

export async function runInstaller(
  options = {},
  { spawnImpl = spawn, scriptPath = INSTALL_SCRIPT, timeoutMs = INSTALL_TIMEOUT_MS } = {},
) {
  const action = options.uninstall ? "uninstall" : "install";
  const run = await runAnyShell(installerArgv(options, scriptPath), spawnImpl, timeoutMs);

  if (run.timedOut) {
    return {
      ok: false,
      action,
      timedOut: true,
      timeoutMs,
      message: `install.ps1 has not finished after ${timeoutMs}ms. This is not a failure: 'dotnet build' can take longer than that on a cold NuGet cache, and the build it started may still be finishing. Wait a minute and run this tool again — re-running is safe — or check ${scriptPath} manually.`,
      stdout: run.stdout,
      stderr: run.stderr,
    };
  }

  const parsed = parseResult(run.stdout);

  if (!parsed) {
    return {
      ok: false,
      action,
      exitCode: run.code,
      explanation: explainExitCode(run.code, action),
      message: "install.ps1 did not print the JSON object -Json promises. Its raw output follows.",
      stdout: run.stdout,
      stderr: run.stderr,
    };
  }

  return { ...parsed, exitCode: run.code, explanation: explainExitCode(run.code, action) };
}

export function registerInstallTools(server, options = {}) {
  server.tool(
    "revit_install_bridge",
    "Install the Revit MCP bridge add-in: copies the add-in that ships with this package into Revit's Addins folder. No .NET SDK and no build step are involved. Use this when revit_status says nothing is listening, or on a machine where the bridge has never been set up. Revit does NOT need to be running. RESTART REVIT after this succeeds — Revit only scans the Addins folder at startup, so the bridge does not load until Revit is restarted. Re-running is safe.",
    {
      revit_version: z
        .string()
        .optional()
        .describe("Four-digit Revit year, e.g. '2026'. Omit to install into every Revit found on this machine."),
      skip_build: z
        .boolean()
        .optional()
        .describe("Never build, even in a source checkout with no prebuilt add-in. A no-op for the published package, which always installs its bundled binary without building."),
    },
    async ({ revit_version, skip_build }) => {
      try {
        const result = await runInstaller(
          { revitVersion: revit_version, skipBuild: skip_build },
          options,
        );
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_uninstall_bridge",
    "Remove the Revit MCP bridge add-in: deletes the .addin manifest and the install folder for every Revit version that has it. Revit does NOT need to be running, but RESTART REVIT afterwards — a running Revit keeps the already-loaded bridge alive until it closes.",
    {},
    async () => {
      try {
        const result = await runInstaller({ uninstall: true }, options);
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );
}
