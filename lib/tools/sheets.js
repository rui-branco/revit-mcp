// Sheet tools: the two reads a caller needs before creating sheets, and the
// batch create itself.
//
// A sheet cannot exist without a title block family type, so the read that
// lists them is part of this set rather than of the general reads.

import { z } from "zod";

export function registerSheetTools(server, bridge) {
  server.tool(
    "revit_list_titleblocks",
    "List the title block family types loaded in the active document: id, family name and type name. Call this before revit_create_sheets — every sheet needs a title block, and this is where its id comes from. An empty list means no title block family is loaded, so sheets cannot be created yet.",
    {},
    { title: "List Title Blocks", readOnlyHint: true },
    async () => {
      try {
        const result = await bridge.call("/titleblocks");
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_list_sheets",
    "List the sheets in the active document: id, sheet number and name, ordered by sheet number.",
    {},
    { title: "List Sheets", readOnlyHint: true },
    async () => {
      try {
        const result = await bridge.call("/sheets");
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_create_sheets",
    "Create sheets in the active document. Pass every sheet in one call: all of them together are one undo step. A sheet number that already exists is skipped and reported in 'skipped' rather than erroring, so re-running the same call is safe.",
    {
      sheets: z
        .array(
          z.object({
            number: z
              .string()
              .min(1)
              .describe("Sheet number, e.g. 'A101'. Must be unique; an existing one is skipped."),
            name: z.string().min(1).describe("Sheet name, e.g. 'Ground Floor Plan'"),
          }),
        )
        .min(1)
        .describe("Sheets to create, all in this one call"),
      title_block_id: z
        .number()
        .int()
        .optional()
        .describe(
          "Title block family type id from revit_list_titleblocks. Omit it to use the first one loaded.",
        ),
    },
    { title: "Create Sheets", readOnlyHint: false, destructiveHint: false },
    async ({ sheets, title_block_id }) => {
      try {
        // The tool argument stays snake_case like every other tool's, but the
        // bridge reads `titleBlockId` — map it here rather than on the C# side.
        const result = await bridge.call("/sheets/create", {
          sheets,
          titleBlockId: title_block_id,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );
}
