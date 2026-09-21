// Hot reload: swap the bridge's routing and endpoint logic without restarting
// Revit.
//
// A Revit restart means closing the model, which is precisely what a long
// unattended run cannot afford — so the add-in is split in two. The half Revit
// pins for the session owns the HTTP listener and the main-thread pump; the
// half that changes (routes and endpoints) is loaded into a collectible load
// context from a shadow copy, and this tool replaces it live.
//
// Maintainer tooling: it only does anything after the logic DLL has actually
// been rebuilt on disk.

export function registerReloadTools(server, bridge) {
  server.tool(
    "revit_reload_bridge",
    "Reload the bridge's endpoint logic from disk without restarting Revit, after rebuilding the add-in. Use this while developing the bridge itself: it swaps the freshly built logic assembly into the running Revit, keeping the open model, the HTTP listener and any queued work alive. The response says whether the previous version was actually unloaded — if unloadedPrevious is false, the new logic IS live but the old one is still in memory, which is worth investigating rather than ignoring. Nothing changes until the DLL on disk changes, so build first.",
    {},
    async () => {
      try {
        const result = await bridge.call("/reload");
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );
}
