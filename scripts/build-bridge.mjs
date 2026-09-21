// Maintainer-only: rebuild the C# add-in and refresh revit-bridge/dist/.
//
// dist/ is the binary that ships on npm, so end users never need the .NET SDK.
// It is committed to git on purpose. Only the DLL and its .deps.json go in —
// the .pdb is debug-only weight nobody installing from npm can use.
//
// Run via `npm run build:bridge`. `prepublishOnly` runs it too, so a stale or
// broken binary cannot reach the registry.

import { spawnSync } from "child_process";
import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const bridgeDir = path.join(repoRoot, "revit-bridge");
const project = path.join(bridgeDir, "RevitMcpBridge.csproj");
const distDir = path.join(bridgeDir, "dist");
// Both halves: the loader Revit pins for the session, and the reloadable logic
// assembly it loads into a collectible context. Shipping one without the other
// is a bridge that does not start, so both are required below.
const SHIPPED = [
  "RevitMcpBridge.dll",
  "RevitMcpBridge.deps.json",
  "RevitMcpBridge.Handlers.dll",
  "RevitMcpBridge.Handlers.deps.json",
];

// No shell: dotnet is a real executable, and a project path with a space in it
// must stay one argument.
const build = spawnSync("dotnet", ["build", project, "-c", "Release", "--nologo"], {
  stdio: "inherit",
});

if (build.error) {
  console.error(`Could not run dotnet: ${build.error.message}. Install the .NET SDK to build the bridge.`);
  process.exit(1);
}

if (build.status !== 0) {
  process.exit(build.status);
}

// Find the newest Release output rather than hard-coding the TFM folder, so a
// TargetFramework bump in the .csproj does not silently ship the old binary.
function findOutputDir() {
  const binRoot = path.join(bridgeDir, "bin", "Release");
  if (!fs.existsSync(binRoot)) return null;

  const candidates = fs
    .readdirSync(binRoot, { withFileTypes: true })
    .filter((entry) => entry.isDirectory())
    .map((entry) => path.join(binRoot, entry.name))
    .filter((dir) => fs.existsSync(path.join(dir, "RevitMcpBridge.dll")))
    .sort(
      (a, b) =>
        fs.statSync(path.join(b, "RevitMcpBridge.dll")).mtimeMs -
        fs.statSync(path.join(a, "RevitMcpBridge.dll")).mtimeMs,
    );

  return candidates[0] || null;
}

const outputDir = findOutputDir();

if (!outputDir) {
  console.error(`Build reported success but no RevitMcpBridge.dll was found under ${path.join(bridgeDir, "bin", "Release")}.`);
  process.exit(1);
}

// Replaced wholesale, never merged into: a file dropped from a newer build must
// not linger in the package.
fs.rmSync(distDir, { recursive: true, force: true });
fs.mkdirSync(distDir, { recursive: true });

for (const name of SHIPPED) {
  const source = path.join(outputDir, name);
  if (!fs.existsSync(source)) {
    console.error(`Missing expected build output: ${source}`);
    process.exit(1);
  }
  fs.copyFileSync(source, path.join(distDir, name));
  console.log(`[build:bridge] ${name} -> revit-bridge/dist/`);
}

console.log(`[build:bridge] refreshed ${distDir} from ${outputDir}`);
