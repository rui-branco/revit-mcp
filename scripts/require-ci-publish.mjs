// Run by `prepublishOnly`: refuse `npm publish` anywhere but GitHub Actions.
//
// Releases go out through .github/workflows/publish.yml, which rebuilds the
// add-in, checks it matches the committed revit-bridge/dist/, and publishes with
// provenance. A publish from a laptop skips all of that, and it also takes the
// version first, so the workflow then fails on "cannot publish over" it.

if (process.env.GITHUB_ACTIONS !== "true") {
  console.error(
    "npm publish runs in CI only. Bump the version, push, and publish a GitHub release (vX.Y.Z); the publish workflow does the rest.",
  );
  process.exit(1);
}
