// Documentation: the half of the job that happens after the model is built —
// cropping a view to what the drawing is about, hiding what is in the way of it,
// laying a sheet out, reading a schedule back, and getting the set out of Revit
// as PDF.
//
// Every write tool here takes dry_run and it DEFAULTS TO TRUE. That is not the
// convention the modelling tools use, and it is deliberate: these operate on
// presentation drawings a human has already looked at and approved, so the model
// has to ask for the change twice — once by calling, once by turning the dry run
// off. Read the dry run's "before" and only then apply.
//
// Model coordinates are Revit internal units (decimal feet). Sheet coordinates
// are feet on the PAPER, not model feet — an A1 sheet is 1.95 x 1.38, an A0 is
// 2.76 x 3.90. The two never mix: revit_set_view_crop is model feet,
// revit_set_viewport_position is paper feet.

import { z } from "zod";
import { clampLimit, DEFAULT_QUERY_LIMIT, MAX_QUERY_LIMIT } from "./read.js";

const point3 = z.object({ x: z.number(), y: z.number(), z: z.number() });
const point2 = z.object({ x: z.number(), y: z.number() });

export function registerDocumentationTools(server, bridge) {
  server.tool(
    "revit_get_view_crop",
    "Read the crop of one view or a batch of them: whether it is on, whether the rectangle is drawn, and where it actually is. 'modelBounds' is the answer you want — the crop box's eight corners put through the view's own transform, so it is in MODEL coordinates and comparable with anything else in feet. 'localBounds' is the raw Min/Max in the crop box's own coordinate system and is reported only so the two are never confused: for any view that is not aligned with the project axes they are different boxes, and reading localBounds as model coordinates is the classic way to crop a section to the wrong place. 'annotationCrop' is null on a view that has no such parameter, which is not the same answer as false. 'templateControlledCrop' lists the crop parameters this view's template owns — when it is non-empty, revit_set_view_crop will refuse the view, because Revit would ignore the write.",
    {
      view_id: z
        .number()
        .int()
        .optional()
        .describe("Single view id from revit_list_views. Use this or view_ids, not both."),
      view_ids: z
        .array(z.number().int())
        .min(1)
        .optional()
        .describe("View ids to read, all in this one call. Use this or view_id, not both."),
    },
    async ({ view_id, view_ids }) => {
      // Checked here rather than in the schema: a raw shape cannot express
      // "one of these two", and a call with neither must not reach Revit.
      if (view_id === undefined && view_ids === undefined) {
        return {
          content: [
            { type: "text", text: "Error: pass either view_id (one view) or view_ids (a batch)." },
          ],
        };
      }
      if (view_id !== undefined && view_ids !== undefined) {
        return {
          content: [{ type: "text", text: "Error: pass either view_id or view_ids, not both." }],
        };
      }

      try {
        // The tool arguments stay snake_case like every other tool's; the bridge
        // reads viewId / viewIds — map them here rather than on the C# side.
        const result = await bridge.call("/views/crop", { viewId: view_id, viewIds: view_ids });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_set_view_crop",
    "Crop one view or a batch of them to a region of the MODEL, in feet. You give model coordinates and never have to know the view's own coordinate system: the bridge puts all eight corners of your box through the view's crop transform and takes the box around the result, so a section looking north-east crops to the region you actually named. The view's transform is left alone — it belongs to the view's orientation, not to the crop. Three things are written every time: the box, crop active TRUE and crop region visible FALSE, which is the state a drawing wants — cropped, with no crop rectangle printed on the sheet. This is what fixes a plan that sits tiny in a corner of its sheet: uncropped views carry section marks and elevation markers far outside the building, and the viewport is sized to all of it. Crop first, then re-scale, then check revit_get_sheet_layout. Refusals are checked for every view BEFORE anything is written, so the call is all-or-nothing: CROP_NOT_SUPPORTED for a sheet, schedule, legend or view template, and CROP_CONTROLLED_BY_TEMPLATE when the view's template owns the crop parameters — Revit would ignore the write, so the bridge names the template instead of reporting a success the drawing would not show. dry_run DEFAULTS TO TRUE: it reports the current crop and the box it would write, and changes nothing. Read that, then call again with dry_run false. One applied call is one undo step, and 'after' is read back off each view.",
    {
      view_id: z
        .number()
        .int()
        .optional()
        .describe("Single view id from revit_list_views. Use this or view_ids, not both."),
      view_ids: z
        .array(z.number().int())
        .min(1)
        .optional()
        .describe("View ids to crop, all in this one call. Use this or view_id, not both."),
      model_bounds: z
        .object({
          min: point3.describe("Lower corner in MODEL feet"),
          max: point3.describe("Upper corner in MODEL feet. Must exceed min on all three axes."),
        })
        .describe(
          "The region of the model to crop to, in MODEL coordinates in feet — not the view's own coordinates. Get a sensible box from the modelExtents of a 3D view, or from the bounding box of the elements the drawing is about.",
        ),
      dry_run: z
        .boolean()
        .default(true)
        .describe(
          "DEFAULTS TO TRUE. True reports the current crop and the box that would be written, and changes nothing. Pass false to apply it.",
        ),
    },
    async ({ view_id, view_ids, model_bounds, dry_run }) => {
      if (view_id === undefined && view_ids === undefined) {
        return {
          content: [
            { type: "text", text: "Error: pass either view_id (one view) or view_ids (a batch)." },
          ],
        };
      }
      if (view_id !== undefined && view_ids !== undefined) {
        return {
          content: [{ type: "text", text: "Error: pass either view_id or view_ids, not both." }],
        };
      }

      // Checked here rather than in the schema: zod can say "a number", not
      // "less than the other number", and a zero-depth crop is not a crop.
      const { min, max } = model_bounds;
      if (min.x >= max.x || min.y >= max.y || min.z >= max.z) {
        return {
          content: [
            {
              type: "text",
              text: "Error: model_bounds needs min strictly less than max on all three axes.",
            },
          ],
        };
      }

      try {
        const result = await bridge.call("/views/set-crop", {
          viewId: view_id,
          viewIds: view_ids,
          modelBounds: model_bounds,
          dryRun: dry_run,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_hide_elements_in_view",
    "Hide elements in ONE view, permanently — the 'Hide in View > Elements' override Revit stores on that view. Two things it is NOT, both of which look identical on screen: it is not deletion, so the elements stay in the model, in every other view and in the schedules — hiding entourage that stands in front of a presentation elevation does not touch the planting design or its quantities; and it is not temporary hide/isolate, so it survives closing the view and it reaches the sheet. Pass hidden false to bring the same list back. Every id is checked with CanBeHidden BEFORE anything is hidden and one element Revit refuses — a group, an array, a constraint, a link — fails the whole call with ELEMENT_CANNOT_BE_HIDDEN naming it, because Revit refuses the batch rather than skipping the offender. To hide whole categories instead, that is revit_hide_view_categories, and it is the right tool for level datums and section marks. dry_run DEFAULTS TO TRUE and reports canBeHidden and the current isHidden for every id. 'isHidden' in the answer is read back off the view, so an element left invisible by a category switch or a template is visible as such rather than credited to this call. One applied call is one undo step.",
    {
      view_id: z.number().int().describe("View id from revit_list_views. One view, not a batch."),
      ids: z
        .array(z.number().int())
        .min(1)
        .describe("Element ids to hide or unhide in that view, all in this one call"),
      hidden: z
        .boolean()
        .default(true)
        .describe("True hides them (the default). False unhides the same list."),
      dry_run: z
        .boolean()
        .default(true)
        .describe(
          "DEFAULTS TO TRUE. True reports canBeHidden and the current isHidden for every id and changes nothing. Pass false to apply it.",
        ),
    },
    async ({ view_id, ids, hidden, dry_run }) => {
      try {
        const result = await bridge.call("/views/hide-elements", {
          viewId: view_id,
          ids,
          hidden,
          dryRun: dry_run,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_export_pdf",
    "Export named views and sheets to PDF through Revit's own PDF exporter — vectors, on white paper, at the sheet's real size. THIS IS THE DELIVERABLE FORMAT: revit_export_view_image is a capture of the view as Revit draws it on screen, so a view with a dark background comes out as a black-paper negative of the drawing, and no PNG setting makes that a printable sheet. Use a PDF for anything anyone is meant to read or plot, and a PNG only for looking at the 3D model. It is also nothing to do with rendering — the API cannot start Revit's raytracer at all. 'view_ids' and 'sheet_ids' are explicit lists and at least one is required; there is no 'export everything' shorthand, because a drawing set is a decision. Sheets go in sheet_ids and ordinary views in view_ids — the wrong way round is a BAD_REQUEST rather than a surprise. 'folder' must be absolute and is created if missing; an existing file is never overwritten unless overwrite is true, and that is checked for every target before the first byte is written. What the manifest proves and what it does not: 'path' and 'bytes' are read off the disk afterwards and a file that is not there is an error, never a success — Revit reporting a successful export is not taken as evidence that anything was written; 'pages' is counted out of the PDF itself and is null with pagesMeasured false when it cannot be; and 'pageMapping' says 'exact' when each file holds one view, or 'requestedOrder' when a combined PDF's page numbers are the order the views were handed to Revit, which is an assumption and not a measurement. Not an undo step: exporting does not change the model.",
    {
      view_ids: z
        .array(z.number().int())
        .min(1)
        .optional()
        .describe("Ordinary view ids to export, from revit_list_views. Not sheets."),
      sheet_ids: z
        .array(z.number().int())
        .min(1)
        .optional()
        .describe("Sheet ids to export, from revit_list_sheets. Exported after the views."),
      folder: z
        .string()
        .min(1)
        .describe(
          "Absolute folder the PDFs are written to, e.g. 'C:\\\\Projects\\\\PDF'. Created if it does not exist, and proved writable before anything is exported.",
        ),
      filename: z
        .string()
        .min(1)
        .optional()
        .describe(
          "File name stem, without '.pdf'. With combine true it names the single file; with combine false it prefixes each one. Omit it for the project title (combined) or the view and sheet names (not combined).",
        ),
      combine: z
        .boolean()
        .default(true)
        .describe(
          "TRUE by default: one PDF holding every view and sheet, in the order given. False writes one PDF per view, named after it.",
        ),
      overwrite: z
        .boolean()
        .default(false)
        .describe(
          "False by default: an existing file at any target path fails the whole call with FILE_EXISTS and nothing is exported.",
        ),
    },
    async ({ view_ids, sheet_ids, folder, filename, combine, overwrite }) => {
      // Checked here rather than in the schema: a raw shape cannot express
      // "at least one of these two", and an empty export must not reach Revit.
      if (view_ids === undefined && sheet_ids === undefined) {
        return {
          content: [
            {
              type: "text",
              text: "Error: pass view_ids, sheet_ids, or both. There is no shorthand for exporting the whole document.",
            },
          ],
        };
      }

      try {
        const result = await bridge.call("/export/pdf", {
          viewIds: view_ids,
          sheetIds: sheet_ids,
          folder,
          filename,
          combine,
          overwrite,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_read_schedule",
    "Read the text of a schedule, cell by cell. This is the only way to see what a schedule actually says: a schedule cannot be exported as an image — Revit's image export does not accept one as a view — and Revit's own schedule export writes a text file to a disk you then cannot read either. Two independent sources of the column headings come back and they disagree in a way that matters: 'columns' is the schedule's definition, the fields in display order with the heading text actually printed and an isHidden flag for fields that are in the definition but not drawn; 'header' and 'body' are the raw laid-out grids exactly as Revit builds them, and which of the two holds the heading row depends on the schedule, so both are returned whole rather than guessed at. A cell Revit will not give text for — merged, or holding an image — is null, which is not the same answer as an empty cell. 'offset' and 'limit' page through the BODY rows only and 'totalRows' always says how much you did not get: if it is larger than returnedRows, say so instead of treating the page as the whole schedule. Read-only.",
    {
      schedule_id: z
        .number()
        .int()
        .describe("Schedule id — a view whose viewType is Schedule, from revit_list_views"),
      limit: z
        .number()
        .int()
        .positive()
        .default(DEFAULT_QUERY_LIMIT)
        .describe(
          `Max body rows to return (default ${DEFAULT_QUERY_LIMIT}, hard cap ${MAX_QUERY_LIMIT} — higher values are clamped, not rejected)`,
        ),
      offset: z
        .number()
        .int()
        .min(0)
        .default(0)
        .describe("Body rows to skip, for paging through a long schedule"),
    },
    async ({ schedule_id, limit, offset }) => {
      try {
        const result = await bridge.call("/schedules/read", {
          scheduleId: schedule_id,
          limit: clampLimit(limit),
          offset,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_get_sheet_layout",
    "Measure where everything on a sheet actually is, in feet on the PAPER. Call this before moving anything: a plan that has come out a stamp in the corner of an A0 could be the view's crop, the title block's extent or the viewport's position, and those are three different fixes — this is what tells them apart. 'outline' is the paper itself. 'titleblocks' carries each title block's bounding box, which is the real drawing area, because a title block is not always flush with the paper. Each viewport carries 'center' — the point revit_set_viewport_position moves, and NOT the bottom-left — plus 'bounds', what the view occupies with its crop included, and 'labelBounds', the view title, which sits outside the box and is what usually collides with the next view. 'scheduleInstances' carries each schedule's 'topLeft', which is Revit's own anchor for a schedule rather than its centre, and its bounds: the pair that says whether a table is running off the bottom of the sheet. Anything Revit gives no geometry for is null rather than zeroes. Read-only.",
    {
      sheet_id: z.number().int().describe("Sheet id from revit_list_sheets"),
    },
    async ({ sheet_id }) => {
      try {
        const result = await bridge.call("/sheets/layout", { sheetId: sheet_id });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_get_browser_organization",
    "Inspect how the Project Browser currently groups the Sheets section, and find out honestly what can be done about it. READ-ONLY, and that is the finding, not a limitation of this tool: Revit's API can read the browser organization scheme and cannot change it — there is no Create, the sorting order and sorting parameter are get-only, nothing defines folder levels and nothing makes a scheme active. The response says so in 'canApplyFromApi' (always false) and 'applyLimitation'. It also names the alternative: Revit 2025's SheetCollection is a different, writable mechanism giving native collapsible sheet groups one level deep, not a nested hierarchy — that is what the dedicated sheet collection tools do, and this tool stays read-only. What you do get here: 'active' is the scheme in force with its sorting parameter, 'schemes' lists every scheme defined in the document by name — those are the ones a user can pick in the UI — and each sheet's 'folders' is the actual chain of browser folders it sits in, with the parameter that produced each one. 'groupingLevels' is derived from a sample sheet and labelled as such, because the scheme does not expose its own definition. The automatable half of the job is the parameter the grouping reads: revit_create_project_parameter to add it and revit_set_sheet_parameters to stamp it, after which a human points the browser at it once. Never report the grouping as applied on the strength of having stamped the parameter.",
    {
      sheet_ids: z
        .array(z.number().int())
        .min(1)
        .optional()
        .describe("Sheet ids to report folders for. Omit it for every sheet in the document."),
      limit: z
        .number()
        .int()
        .positive()
        .default(DEFAULT_QUERY_LIMIT)
        .describe(
          `Max sheets to return (default ${DEFAULT_QUERY_LIMIT}, hard cap ${MAX_QUERY_LIMIT} — higher values are clamped, not rejected)`,
        ),
    },
    async ({ sheet_ids, limit }) => {
      try {
        const result = await bridge.call("/sheets/browser-organization", {
          sheetIds: sheet_ids,
          limit: clampLimit(limit),
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_set_viewport_position",
    "Move one viewport on its sheet. All lengths are feet on the PAPER, not model feet. 'center' is the CENTRE of the viewport's box — the same point revit_get_sheet_layout reports as 'center', not the bottom-left and not the view's origin — so call that first and move relative to what it said rather than guessing a coordinate. 'label_offset' and 'label_line_length' are the view title's position relative to the viewport and the length of the line under it; both are left exactly as they are when not passed. Moving a viewport does not resize it: if the view is too big or too small for the sheet, that is revit_set_view_crop and revit_set_view_scale, and this only places the result. dry_run DEFAULTS TO TRUE and reports the current position without moving anything. 'before' and 'after' are both read back through Revit, so a viewport that did not go where it was told — one whose positioning is not free — is visible rather than silent. One applied call is one undo step.",
    {
      viewport_id: z
        .number()
        .int()
        .describe("Viewport id from revit_get_sheet_layout — not the view id and not the sheet id"),
      center: point2.describe(
        "Where to put the CENTRE of the viewport's box, in feet on the paper. Compare with the 'center' revit_get_sheet_layout reported.",
      ),
      label_offset: point2
        .optional()
        .describe(
          "View title position relative to the viewport, in feet on the paper. Omit it to leave the title where it is.",
        ),
      label_line_length: z
        .number()
        .positive()
        .optional()
        .describe(
          "Length of the line under the view title, in feet on the paper. Omit it to leave it as it is.",
        ),
      dry_run: z
        .boolean()
        .default(true)
        .describe(
          "DEFAULTS TO TRUE. True reports the current position and moves nothing. Pass false to apply it.",
        ),
    },
    async ({ viewport_id, center, label_offset, label_line_length, dry_run }) => {
      try {
        const result = await bridge.call("/sheets/set-viewport-position", {
          viewportId: viewport_id,
          center,
          labelOffset: label_offset,
          labelLineLength: label_line_length,
          dryRun: dry_run,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_set_schedule_position",
    "Move ONE schedule on its sheet. This is a different tool from revit_set_viewport_position and it has to be: a schedule on a sheet is a ScheduleSheetInstance, not a Viewport, and Revit anchors it by its TOP-LEFT corner rather than by the centre of a box. Passing a schedule instance id to the viewport tool fails. Use the id from revit_get_sheet_layout's 'scheduleInstances' — that is the INSTANCE id, not the schedule view's id, and the two are different numbers. Lengths are feet ON THE PAPER. The revision schedule inside a title block is refused with REVISION_SCHEDULE_IS_FIXED: Revit prohibits moving it and its position belongs to the title block family, so the fix is to edit the family. dry_run DEFAULTS TO TRUE. 'before' and 'after' both carry bounds measured off the sheet rather than the point echoed back, which is the only way to see the common case — a schedule that is not misplaced but simply longer than the page, where moving it cannot help and the table needs splitting or fewer rows.",
    {
      instance_id: z
        .number()
        .int()
        .describe(
          "ScheduleSheetInstance id from revit_get_sheet_layout's scheduleInstances — not the schedule id and not the sheet id",
        ),
      top_left: point2.describe(
        "Where to put the TOP-LEFT corner of the schedule, in feet on the paper. Compare with the 'topLeft' revit_get_sheet_layout reported.",
      ),
      dry_run: z
        .boolean()
        .default(true)
        .describe(
          "DEFAULTS TO TRUE. True reports the current position and moves nothing. Pass false to apply it.",
        ),
    },
    async ({ instance_id, top_left, dry_run }) => {
      try {
        const result = await bridge.call("/sheets/set-schedule-position", {
          instanceId: instance_id,
          topLeft: top_left,
          dryRun: dry_run,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_configure_schedule",
    "Reshape how an EXISTING schedule presents the rows it already has: grouping, totals, column headings and widths. It never adds a column and never removes one — revit_create_schedule is what makes a new schedule, and a schedule's columns are its author's decision. The one to reach for is itemized: false, which is the API behind Revit's 'Itemize every instance' tick box and the thing that turns 142 rows of one plant each into one row per species carrying a count. It does nothing on its own — rows only collapse where the group_by fields make them equal — so send group_by in the same call. group_by REPLACES the sort/group list rather than adding to it; the previous list comes back under 'before' so it can be put back by hand. Revit refuses to group by Count, percentage and formula fields, because none of them has a value until after grouping has happened, and that is checked before anything is written. For totals, CanTotal is checked first: asking a text column to total is an error, not a silent no-op, and revit_read_schedule reports 'canTotal' per column so you can look before asking. Everything is validated before the transaction opens, so a request that is wrong in its last entry changes nothing at all. dry_run DEFAULTS TO TRUE. Read 'bodyRows' in the before/after — it is the row count Revit actually lays out and the only honest proof a regrouping did what was asked.",
    {
      schedule_id: z
        .number()
        .int()
        .describe("Schedule view id — revit_list_views with viewType Schedule, or the scheduleId from revit_get_sheet_layout"),
      itemized: z
        .boolean()
        .optional()
        .describe(
          "False collapses elements that the grouping makes equal onto one row; true gives every element its own row. Omit to leave it alone. Pair false with group_by or nothing collapses.",
        ),
      group_by: z
        .array(
          z.object({
            field: z
              .union([z.number().int(), z.string()])
              .describe(
                "Field id ('fieldId' from revit_read_schedule) or field name. An ambiguous name is refused with the candidates listed.",
              ),
            sort_order: z.enum(["Ascending", "Descending"]).optional(),
            show_header: z.boolean().optional(),
            show_footer: z.boolean().optional(),
            show_footer_count: z.boolean().optional(),
            show_blank_line: z.boolean().optional(),
          }),
        )
        .optional()
        .describe(
          "REPLACES the whole sort/group list, in order. Omit to leave the existing grouping untouched.",
        ),
      fields: z
        .array(
          z.object({
            field: z
              .union([z.number().int(), z.string()])
              .describe("Field id or field name, same resolution as group_by"),
            heading: z.string().optional().describe("Column heading text as printed"),
            width_ft: z.number().positive().optional().describe("Column width in feet on the paper"),
            totals: z
              .union([z.boolean(), z.enum(["Standard", "Totals", "Min", "Max", "MinMax"])])
              .optional()
              .describe(
                "true means Totals, false means Standard. Refused unless the column's canTotal is true.",
              ),
            hidden: z.boolean().optional(),
          }),
        )
        .optional()
        .describe("Changes to existing columns only. Never adds or removes a column."),
      filters: z
        .array(
          z.object({
            field: z
              .union([z.number().int(), z.string()])
              .describe("Field id or field name, same resolution as group_by"),
            operator: z.enum(["BeginsWith", "Equal", "GreaterThan"]),
            value: z
              .union([z.string(), z.number()])
              .describe(
                "A string for a text field, a number for a numeric one. The two are checked against what the field stores before anything is written.",
              ),
          }),
        )
        .optional()
        .describe(
          "REPLACES every filter on the schedule. Omit to leave the existing filters alone; pass [] to clear them. BeginsWith on Sheet Number is how one sheet list becomes a per-series index.",
        ),
      grand_total: z
        .object({
          show: z.boolean().optional(),
          show_count: z.boolean().optional(),
          show_title: z.boolean().optional(),
          title: z.string().optional(),
        })
        .optional()
        .describe("The grand total row at the bottom of the schedule."),
      dry_run: z
        .boolean()
        .default(true)
        .describe(
          "DEFAULTS TO TRUE. True reports the current configuration and changes nothing. Pass false to apply it.",
        ),
    },
    async ({ schedule_id, itemized, group_by, fields, filters, grand_total, dry_run }) => {
      if (
        itemized === undefined &&
        group_by === undefined &&
        fields === undefined &&
        filters === undefined &&
        grand_total === undefined
      ) {
        return {
          content: [
            {
              type: "text",
              text:
                "Error: nothing to change. Pass at least one of itemized, group_by, fields, " +
                "filters or grand_total. To read a schedule instead, use revit_read_schedule.",
            },
          ],
        };
      }

      try {
        const result = await bridge.call("/schedules/configure", {
          scheduleId: schedule_id,
          itemized,
          groupBy: group_by?.map((entry) => ({
            field: entry.field,
            sortOrder: entry.sort_order,
            showHeader: entry.show_header,
            showFooter: entry.show_footer,
            showFooterCount: entry.show_footer_count,
            showBlankLine: entry.show_blank_line,
          })),
          fields: fields?.map((entry) => ({
            field: entry.field,
            heading: entry.heading,
            widthFt: entry.width_ft,
            totals: entry.totals,
            hidden: entry.hidden,
          })),
          filters: filters?.map((entry) => ({
            field: entry.field,
            operator: entry.operator,
            value: entry.value,
          })),
          grandTotal: grand_total && {
            show: grand_total.show,
            showCount: grand_total.show_count,
            showTitle: grand_total.show_title,
            title: grand_total.title,
          },
          dryRun: dry_run,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );
}
