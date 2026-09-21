// Title block families, read from the inside.
//
// `revit_list_titleblocks` says which title block types are loaded and nothing
// more. What is actually printed on the sheet — the logo, the consultant
// placeholders somebody typed into the stock family, the label that grows to
// hold a long project name — lives in the FAMILY, and Document.EditFamily is
// the only way to see it from the API.
//
// That call hands back an independent copy of the family as its own document.
// The inspect tool reads it and closes it without saving, inside a finally:
// nothing in the project, the loaded family or the .rfa on disk changes.
//
// `revit_edit_titleblock_family` is the write half, and it takes the ids the
// inspection reported. It edits the same copy and loads it back into the SAME
// project with Document.LoadFamily — no SaveAs, no second project file. Reading
// first is not optional: every id is a family id, and the edit is refused unless
// expected_family_name matches the family those ids came out of.

import { z } from "zod";

// A point on the sheet. Annotation in a title block is paper feet, so z is not a
// dimension it has.
const point2 = z.object({ x: z.number(), y: z.number() });

export function registerTitleblockTools(server, bridge) {
  server.tool(
    "revit_inspect_titleblock_family",
    "Read what is INSIDE a title block family: the only way to find out why a sheet prints a vendor logo, a literal consultant placeholder or a label that overlaps its neighbour. READ-ONLY — the family is opened with Document.EditFamily, which hands back an independent copy, and that copy is closed without saving in a finally; no transaction is opened, so the project, the loaded family and the .rfa on disk are untouched, and nothing here can be undone because nothing is done. Per element it reports id, the Revit API class, category, name, type id/name, the view it is drawn in, its location and its bounding box, plus: text (content, insertion point, width/height, both alignments, text type and that type's text size, and isTextNote — true is literal text somebody typed, false is the other kind of text element); images (size, scale, and the image type's path, source, status and pixel size); imports (whether linked, and the import type's name, which is the file it came from); curves (line style); dimensions (value, isLocked, segments, and the family parameter labelling it — the constraints holding the border together); reference planes (both ends and the normal). The dynamic half is familyParameters: every family parameter with its storage type, formula, instance/type flag, the current type's value, and associatedElementIds — the elements whose own parameters Revit has associated to it. The same association appears on each element row as labelOf. That association is the evidence for which content is parameter-driven and must be preserved and which is a literal string safe to remove; it is reported as the association Revit holds, not as a claim about what a reader sees. Lengths are Revit internal units (decimal feet) and annotation inside a title block is PAPER feet — 5 mm text reads 0.0164. symbol_id is a title block type id from revit_list_titleblocks; anything in another category is refused, and an in-place or non-editable family is refused before anything is opened.",
    {
      symbol_id: z
        .number()
        .int()
        .describe("Id of the loaded title block TYPE (a FamilySymbol), from revit_list_titleblocks"),
    },
    async ({ symbol_id }) => {
      try {
        // Tool arguments stay snake_case; the bridge reads symbolId.
        const result = await bridge.call("/families/titleblock-inspect", { symbolId: symbol_id });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return {
          content: [{ type: "text", text: `Error: ${error.message}` }],
          isError: true,
        };
      }
    },
  );

  server.tool(
    "revit_edit_titleblock_family",
    "Edit the INSIDE of a title block family and load it back into this same project: remove the stock logo and the literal consultant placeholders, retext the captions, resize labels, add notes. The ids are family ids from revit_inspect_titleblock_family, never project ids — inspect first, always. What makes it safe is what it refuses: expected_family_name is required and must match the family those ids came out of, or nothing is even opened; only a TextNote or an ImageInstance can be removed, and a label (a text element bound to a parameter) is refused by name, because deleting one takes that content off every sheet for good; Document.Delete is read back, so anything it would take that you did not name rolls the whole edit back, and so does any label or schedule instance that existed before the edit and not after it. label_sizes does NOT edit the text type in place — a type is shared, and editing it would resize everything on it. An existing type of that size is reused, otherwise the element's own type is duplicated and only the elements named are pointed at the copy; an element Revit will not retype comes back as action 'refused' with the reason, and the removals still stand. Sizes and points are Revit internal units (decimal feet), and annotation inside a title block is PAPER feet: 6 mm text is 0.019685, 3 mm is 0.009843, and 1032 mm across the sheet is 3.385827. dry_run DEFAULTS TO TRUE: it opens the family, resolves every id and every text type, reports exactly what it would do, and closes the copy without saving. Read that, then call again with dry_run false. Applying loads the edited family into the project — every sheet using it redraws — and writes nothing to the .rfa on disk.",
    {
      symbol_id: z
        .number()
        .int()
        .describe("Id of the loaded title block TYPE (a FamilySymbol), from revit_list_titleblocks"),
      expected_family_name: z
        .string()
        .describe(
          "The family name the ids were read from, as revit_inspect_titleblock_family reported it. A mismatch is refused before the family is opened.",
        ),
      remove_ids: z
        .array(z.number().int())
        .optional()
        .describe(
          "Family ids to delete. TextNote and ImageInstance only — the stock logo and the literal placeholders. A label, a line, a dimension or a reference plane is refused.",
        ),
      text_edits: z
        .array(
          z.object({
            id: z.number().int().describe("Family id of a TextNote — literal text, not a label"),
            text: z.string().describe("The text it should read instead"),
          }),
        )
        .optional()
        .describe("Retext literal notes, e.g. translating the stock captions"),
      label_sizes: z
        .array(
          z.object({
            id: z.number().int().describe("Family id of a label or a text note"),
            size: z
              .number()
              .positive()
              .describe("Text size in decimal feet — PAPER feet: 6 mm is 0.019685"),
          }),
        )
        .optional()
        .describe(
          "Retype text elements to a type of this size, reusing one the family has or duplicating theirs",
        ),
      new_notes: z
        .array(
          z.object({
            text: z.string(),
            point: point2.describe("Insertion point on the sheet, in decimal feet"),
            size: z.number().positive().describe("Text size in decimal feet"),
            width: z
              .number()
              .positive()
              .optional()
              .describe(
                "Line-wrapping width in decimal feet. Omit for a single line sized to the text. A width outside what Revit allows for the type is refused with the allowed range.",
              ),
          }),
        )
        .optional()
        .describe("Notes to add in the family's sheet view"),
      view_id: z
        .number()
        .int()
        .optional()
        .describe(
          "View inside the family to draw new_notes in. Defaults to the view the family's existing text is already in; only needed when it draws text in more than one.",
        ),
      dry_run: z
        .boolean()
        .default(true)
        .describe(
          "DEFAULTS TO TRUE. True resolves every id and text type and reports what it would do, changing nothing. Pass false to apply it and load the family back.",
        ),
    },
    async ({
      symbol_id,
      expected_family_name,
      remove_ids,
      text_edits,
      label_sizes,
      new_notes,
      view_id,
      dry_run,
    }) => {
      try {
        const result = await bridge.call("/families/titleblock-edit", {
          symbolId: symbol_id,
          expectedFamilyName: expected_family_name,
          removeIds: remove_ids,
          textEdits: text_edits,
          labelSizes: label_sizes,
          newNotes: new_notes,
          viewId: view_id,
          dryRun: dry_run,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return {
          content: [{ type: "text", text: `Error: ${error.message}` }],
          isError: true,
        };
      }
    },
  );
}
