// Quality assurance: the tools that measure what is in the model rather than
// add to it, plus the one edit safe enough to sit beside them.
//
// Every other read here is deliberately compact — id, name, category, type —
// and compact cannot answer "is this tree standing in the pool", "is that chair
// buried under the slab", "what grades is the paving sitting on". That needs
// measured geometry, so this is the one place that reports it at length, and it
// still bounds what comes back.
//
// All coordinates are absolute Revit model coordinates in decimal feet — the
// document's internal origin, the same frame locations and bounding boxes are
// reported in, never relative to a level or a view. The responses say so in
// `coordinateSystem` rather than leaving it to be assumed.
//
// Every catch here answers with `isError: true`. A refusal these tools exist to
// make — a pinned element, CanBeExcavatedBy saying no, a rolled-back
// transaction — arrives as a bridge exception, and without the flag an MCP
// client reads it as a successful call whose text happens to start with
// "Error:". A write that did not happen must not look like one that did.

import { z } from "zod";
import { clampLimit, DEFAULT_QUERY_LIMIT, MAX_QUERY_LIMIT } from "./read.js";

// The bridge refuses more than this per inspect call rather than clamping: a
// silently shortened inspection reads as a complete one.
export const MAX_INSPECT_IDS = 500;

export function registerQualityTools(server, bridge) {
  server.tool(
    "revit_inspect_elements",
    "Measure elements: everything revit_get_elements leaves out. Per element — id, name, uniqueId, Revit class, category, typeId, typeName, level, hostId, pinned, groupId, its location (a point, or a curve's endpoints), its model bounding box and its material ids. A floor also reports its level, height offset and the closed loops of its top face; a toposolid (or legacy topography) reports its shape vertices with absolute positions, which is the only way to see the grades paving and planting sit on; a family instance reports its symbol, facing and hand vectors and the Z it ACTUALLY sits at. All coordinates are absolute model feet. Ids with no element come back in missingIds — nothing is dropped silently. Max 500 ids per call. With include_parameters, every parameter also reports its own id and BuiltInParameter name, and duplicateParameters lists the names Revit uses TWICE on one element (a family instance has two called 'Level', one read-only) — read that before writing anything by name with revit_set_parameters.",
    {
      ids: z
        .array(z.number().int())
        .min(1)
        .max(MAX_INSPECT_IDS)
        .describe(
          `Element ids to inspect (max ${MAX_INSPECT_IDS} — over that is rejected, not truncated)`,
        ),
      include_parameters: z
        .boolean()
        .default(false)
        .describe(
          "Also return every instance parameter, each with its parameter id and built-in name, plus duplicateParameters for names Revit uses more than once. Off by default: it is hundreds of lines per element when three of them were the question.",
        ),
      include_geometry: z
        .boolean()
        .optional()
        .describe(
          "Force vertex/segment lists on or off. Omit it and they come back whenever there are 500 or fewer, and collapse to a count plus a min/max Z above that. true asks for them anyway, capped at 500 with truncated: true.",
        ),
    },
    async ({ ids, include_parameters, include_geometry }) => {
      try {
        // The tool arguments stay snake_case like every other tool's, but the
        // bridge reads camelCase — map it here rather than on the C# side.
        const result = await bridge.call("/elements/inspect", {
          ids,
          includeParameters: include_parameters,
          includeGeometry: include_geometry,
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

  server.tool(
    "revit_move_elements",
    "Move elements by a vector in feet — lift furniture out of a slab, shift a tree off a path — without rebuilding them. DRY RUN BY DEFAULT: dry_run is true unless you pass false, and a dry run opens no transaction and changes nothing. Either way every element comes back with before and after location AND bounding box, so you never have to guess where something ended up; on a real move the after is read back off the element, on a dry run it is arithmetic (afterSource says which). All-or-nothing: the whole batch moves in one transaction and one undo step, and the request is refused before anything is touched if any id is missing, pinned (the bridge will NOT unpin for you) or in a group. Nothing is ever deleted. A real move is MEASURED before it commits: Revit accepts a move it then does not apply — a family instance whose elevation comes from its level is the everyday case — so if any element did not take the displacement asked for, the whole request is rolled back with MOVE_NOT_APPLIED naming requested against actual, never reported as moved. Ids with nothing measurable come back in unverified.",
    {
      ids: z.array(z.number().int()).min(1).describe("Element ids to move"),
      translation: z
        .object({
          x: z.number().describe("East/west offset in feet"),
          y: z.number().describe("North/south offset in feet"),
          z: z.number().describe("Vertical offset in feet — positive is up"),
        })
        .describe("How far to move, in feet (Revit internal units). Finite numbers only."),
      dry_run: z
        .boolean()
        .default(true)
        .describe(
          "true (the default) measures and reports what the move would do without changing the model. Pass false to actually move.",
        ),
    },
    async ({ ids, translation, dry_run }) => {
      try {
        const result = await bridge.call("/elements/move", {
          ids,
          translation,
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

  server.tool(
    "revit_excavate_toposolid",
    "Cut a toposolid with the elements that should be sunk into it — a pool, a basement, a sunken path — using Revit's native Toposolid.ExcavateBy. This is the NON-DESTRUCTIVE way to make a floor or a pool clear the terrain: revit_flatten_toposolid rewrites the grades under a region and that ground never comes back, while an excavation is an association, so the surface keeps every vertex and the hole follows the element that made it. DRY RUN BY DEFAULT: dry_run is true unless you pass false, and a dry run opens no transaction. Every id is checked with Revit's own CanBeExcavatedBy before anything is opened, and the whole batch goes in one transaction and one undo step. The response reports the toposolid's volume before and after and the volume removed, plus the volume Revit attributes to each element — an element that was excavated but removed nothing does not actually overlap the surface. Nothing is ever deleted.",
    {
      toposolid_id: z
        .number()
        .int()
        .optional()
        .describe(
          "The toposolid to cut. Optional only when the document has exactly one — with several, it is required, and the error lists them.",
        ),
      ids: z
        .array(z.number().int())
        .min(1)
        .describe(
          "Ids of the elements that cut the terrain (the pool, the floor, the mass) — not the toposolid itself",
        ),
      dry_run: z
        .boolean()
        .default(true)
        .describe(
          "true (the default) runs the CanBeExcavatedBy preflight and reports the current volume without changing the model. Pass false to actually excavate.",
        ),
    },
    async ({ toposolid_id, ids, dry_run }) => {
      try {
        const result = await bridge.call("/toposolid/excavate", {
          toposolidId: toposolid_id,
          ids,
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

  server.tool(
    "revit_get_warnings",
    "Read the warnings the model is carrying right now — Revit's own Review Warnings list. Each one carries the GUID of its failure definition (the only stable identity a warning kind has; the message text is localised), its severity, its message, the element ids it is about and any additional ids. Read-only. This is NOT revit_diagnostics: that one is what the bridge suppressed during your writes, this one is the standing state of the model. Answers with the full total, so paging with offset tells you what you have not seen.",
    {
      limit: z
        .number()
        .int()
        .positive()
        .default(DEFAULT_QUERY_LIMIT)
        .describe(
          `Max warnings to return (default ${DEFAULT_QUERY_LIMIT}, hard cap ${MAX_QUERY_LIMIT} — higher values are clamped, not rejected)`,
        ),
      offset: z
        .number()
        .int()
        .min(0)
        .default(0)
        .describe("Warnings to skip, for paging through a noisy model"),
    },
    async ({ limit, offset }) => {
      try {
        const result = await bridge.call("/document/warnings", {
          limit: clampLimit(limit),
          offset,
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

  server.tool(
    "revit_list_view_templates",
    "List the view templates in the model: id, name, view type, and the parameters each template CONTROLS with their labels. Read-only. Check this before setting a scale, a display style or a category override on a view — a parameter the template controls is one the view cannot hold its own value for, which is why a setting you wrote reads back as something else.",
    {},
    async () => {
      try {
        const result = await bridge.call("/views/templates");
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
