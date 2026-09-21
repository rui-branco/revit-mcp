// Diagnostics: what the bridge answered on your behalf while you were not
// looking.
//
// Unattended operation only works because the bridge clicks Revit's modal
// dialogs and resolves its transaction warnings itself — otherwise Revit's main
// thread parks on a dialog nobody is there to read and every call after it
// times out. That is a trade, not a free win: a warning Revit raised and the
// bridge resolved may mean the model did something the caller never asked for.
// So suppressed is not swallowed — it all goes into a ring buffer on the Revit
// side, and these are the tools that read it and switch it off.

import { z } from "zod";

export function registerDiagnosticsTools(server, bridge) {
  server.tool(
    "revit_diagnostics",
    "Read what the bridge suppressed: the Revit dialogs it answered automatically and the transaction warnings it resolved, oldest first, plus whether auto-dismiss is currently on. CHECK THIS AFTER EVERY BATCH OF WRITES. A silently resolved warning usually means Revit changed something you did not ask for — walls joined differently, an element deleted as a side effect of another — and this buffer is the only record of it. An empty dialogs/failures list means the writes went through cleanly.",
    {},
    async () => {
      try {
        const result = await bridge.call("/diagnostics");
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_set_auto_dismiss",
    "Turn automatic dialog dismissal on or off, and optionally empty the diagnostics buffer. It is ON by default and that is what makes unattended runs possible: Revit's dialogs get an answer (OK, or Cancel for anything that sounds destructive) instead of parking Revit until a human clicks. Turn it OFF when somebody is working in Revit at the same time and should answer their own dialogs — with it off, a dialog makes calls fail with REVIT_BUSY until it is dismissed by hand. Clearing the buffer before a batch of writes makes the following revit_diagnostics show only that batch.",
    {
      enabled: z
        .boolean()
        .describe(
          "true = the bridge answers Revit's dialogs itself (the default); false = dialogs wait for a human and calls time out meanwhile",
        ),
      clear: z
        .boolean()
        .default(false)
        .describe("Also empty the dialog/warning buffer that revit_diagnostics reads"),
    },
    async ({ enabled, clear }) => {
      try {
        // The tool argument stays snake_case-free and plain, but the bridge
        // reads `autoDismiss` — map it here rather than on the C# side.
        const result = await bridge.call("/diagnostics/config", {
          autoDismiss: enabled,
          clear,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );
}
