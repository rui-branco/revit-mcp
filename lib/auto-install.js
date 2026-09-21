// Installing the bridge add-in without being asked.
//
// The add-in already ships inside this package, so a new user should not have
// to ask for it to be copied into place: on startup, when no Revit has the
// manifest, we run the very installer revit_install_bridge runs.
//
// Two rules shape everything here. Nothing is awaited and every failure is
// swallowed — the MCP handshake must never wait on this, and a machine with no
// Revit on it is not an error. And stdout belongs to the JSON-RPC channel, so
// every byte written from here goes to stderr instead.

import fs from "fs";
import path from "path";
import { runInstaller } from "./tools/install.js";

// Set it to anything to never auto-install; revit_install_bridge still works.
export const OPT_OUT_ENV = "REVIT_MCP_NO_AUTO_INSTALL";

// Where install.ps1 puts the manifest, and what it calls it. Revit 2024 and
// earlier load add-ins on .NET Framework 4.8 and the installer refuses them, so
// a manifest under one of those years is not this add-in installed.
const MANIFEST_FILE = "RevitMcpBridge.addin";
const MINIMUM_REVIT_YEAR = 2025;

export function isInstalled({ env = process.env, fsImpl = fs } = {}) {
  if (!env.APPDATA) return false;

  const root = path.join(env.APPDATA, "Autodesk", "Revit", "Addins");
  let years;

  try {
    years = fsImpl.readdirSync(root);
  } catch {
    // No add-ins folder at all, so nothing is installed.
    return false;
  }

  return years.some(
    (year) =>
      /^\d{4}$/.test(year) &&
      Number(year) >= MINIMUM_REVIT_YEAR &&
      fsImpl.existsSync(path.join(root, year, MANIFEST_FILE)),
  );
}

async function install(options, write) {
  try {
    const result = await runInstaller({}, options);

    if (result.ok) {
      write(
        "revit-mcp: installed the Revit bridge add-in. RESTART REVIT to load it — Revit only scans the Addins folder at startup.\n",
      );
      return;
    }

    write(
      `revit-mcp: could not install the Revit bridge add-in automatically. ${result.explanation || result.message} Run revit_install_bridge to see the full result, or set ${OPT_OUT_ENV}=1 to stop trying.\n`,
    );
  } catch (error) {
    write(
      `revit-mcp: could not install the Revit bridge add-in automatically: ${error.message} Set ${OPT_OUT_ENV}=1 to stop trying.\n`,
    );
  }
}

// Returns as soon as it has decided what to do. The install itself is
// deliberately left running: its promise is never handed back, so no caller can
// accidentally wait on it, and nothing it does can fail the server's startup.
export function autoInstallBridge(options = {}) {
  const {
    env = process.env,
    platform = process.platform,
    write = (line) => process.stderr.write(line),
  } = options;

  if (platform !== "win32") return "not-windows";
  if (env[OPT_OUT_ENV]) return "opted-out";
  if (isInstalled(options)) return "already-installed";

  install(options, write);
  return "started";
}
