// Detail linework and annotation: what a detail sheet is actually made of.
//
// Nothing here touches the model. A detail line and a text note are
// view-specific elements that exist in exactly one view, which is the whole
// point of a drafting view — see revit_create_drafting_view.
//
// Coordinates are the view's own plan coordinates in feet (Revit internal
// units). The elevation is supplied by the bridge, because Revit refuses a
// detail curve that is not in the plane of the view, and the response says
// which elevation it used.

import { z } from "zod";

const point2 = z.object({ x: z.number(), y: z.number() });

export function registerDetailTools(server, bridge) {
  server.tool(
    "revit_draw_detail_lines",
    "Draw detail lines in a drafting view or a plan — the linework of a construction detail. Coordinates are x/y in feet (Revit internal units); the bridge puts them in the plane of the view for you. 'line_style' names a line style loaded in the document ('Thin Lines', 'Medium Lines', whatever the template has); an unknown one is not an error — the lines are drawn in the default style and the response lists 'availableLineStyles' so you can correct it. A section, an elevation or a 3D view is refused with VIEW_CANNOT_HOST_DETAIL. Pass every line in one call: the whole batch is one undo step, and a line Revit refuses comes back under 'failed' with its index while the rest are still drawn.",
    {
      view_id: z
        .number()
        .int()
        .describe("Drafting view or plan view id, from revit_list_views or revit_create_drafting_view"),
      lines: z
        .array(
          z.object({
            start: point2.describe("Start point in feet"),
            end: point2.describe("End point in feet"),
          }),
        )
        .min(1)
        .describe("Lines to draw, all in this one call"),
      line_style: z
        .string()
        .min(1)
        .optional()
        .describe("Line style name, e.g. 'Thin Lines'. Unknown names fall back to the default and are reported."),
    },
    { title: "Draw Detail Lines", readOnlyHint: false, destructiveHint: false },
    async ({ view_id, lines, line_style }) => {
      try {
        // The tool arguments stay snake_case like every other tool's; the bridge
        // reads viewId / lineStyle — map them here rather than on the C# side.
        const result = await bridge.call("/detail/lines", {
          viewId: view_id,
          lines,
          lineStyle: line_style,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_add_text_notes",
    "Add text notes to a view — the annotation on a detail, a plan or a section. x/y are in feet (Revit internal units) and place the top-left corner of the note; the bridge puts them in the plane of the view. 'size' is the text height in feet on the PAPER (2.5 mm is 0.0082), and because Revit keeps text size on the type rather than the note, a size no loaded type carries gets a duplicated type — once per distinct size, reused on later calls. Schedules, sheets and view templates are refused with VIEW_CANNOT_HOST_TEXT. Pass every note in one call: the whole batch is one undo step, and a note Revit refuses comes back under 'failed' with its index.",
    {
      view_id: z.number().int().describe("View id from revit_list_views or revit_create_drafting_view"),
      notes: z
        .array(
          z.object({
            x: z.number().describe("X in feet"),
            y: z.number().describe("Y in feet"),
            text: z.string().min(1).describe("The text of the note"),
            size: z
              .number()
              .positive()
              .optional()
              .describe("Text height in feet on the paper, e.g. 0.0082 for 2.5 mm. Omit for the default type."),
          }),
        )
        .min(1)
        .describe("Notes to place, all in this one call"),
    },
    { title: "Add Text Notes", readOnlyHint: false, destructiveHint: false },
    async ({ view_id, notes }) => {
      try {
        const result = await bridge.call("/detail/text", { viewId: view_id, notes });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );
}
