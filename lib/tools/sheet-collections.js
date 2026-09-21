// Sheet collections: Revit's own collapsible groups under Sheets in the Project
// Browser. A native SheetCollection element, so the grouping is there when the
// model opens — no browser-organisation parameter for the user to wire up by
// hand, and no renaming or renumbering of the sheets themselves.

import { z } from "zod";

export function registerSheetCollectionTools(server, bridge) {
  server.tool(
    "revit_list_sheet_collections",
    "List the sheet collections in the active document — the collapsible groups Revit draws under Sheets in the Project Browser — with each collection's id, name and member sheets (id, number, name). 'unassignedSheets' is every sheet in no collection. Read-only.",
    {},
    { title: "List Sheet Collections", readOnlyHint: true },
    async () => {
      try {
        const result = await bridge.call("/sheets/collections");
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_set_sheet_collections",
    "Put sheets into native sheet collections, so the Project Browser shows them as collapsible groups. Pass every collection in one call: together they are one undo step. A collection whose name already exists is REUSED, not duplicated, and one whose membership already matches is reported 'unchanged' and left alone. dry_run defaults to TRUE — the call reports the plan and writes nothing until you pass dry_run: false. Nothing else about the sheets is touched: numbers and names are left as they are, and collections this call does not mention keep their members. A sheet belongs to one collection, so an id may appear in only one entry, and assembly sheets cannot join a collection at all. Collections are one level deep; the views under a sheet stay where Revit puts them. The reply reports 'action' (created / reused / unchanged) per collection and, once applied, the membership read back off the committed document.",
    {
      collections: z
        .array(
          z.object({
            name: z
              .string()
              .min(1)
              .describe(
                "Collection name as it should read in the browser, e.g. 'L.02'. Matched exactly against existing collections; Revit prohibits the characters {}[]|;<>?`~ in one.",
              ),
            sheet_ids: z
              .array(z.number().int())
              .min(1)
              .describe("Ids of the sheets that belong in it, from revit_list_sheets"),
          }),
        )
        .min(1)
        .describe("The collections to build, all in this one call"),
      dry_run: z
        .boolean()
        .optional()
        .describe("Defaults to true: report the plan without writing. Pass false to apply it."),
    },
    { title: "Group Sheets into Collections", readOnlyHint: false, destructiveHint: false },
    async ({ collections, dry_run }) => {
      try {
        // Tool arguments stay snake_case; the bridge reads sheetIds / dryRun.
        const result = await bridge.call("/sheets/set-collections", {
          collections: collections.map(({ name, sheet_ids }) => ({ name, sheetIds: sheet_ids })),
          dryRun: dry_run,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );
}
