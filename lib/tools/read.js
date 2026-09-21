// Read-only tools. Intent-level, not a mirror of the Revit API: every response
// is compact JSON — element ids plus only the parameters that were asked for.
// Dumping full parameter sets would bury the model in noise for no gain.

import { z } from "zod";

export const DEFAULT_QUERY_LIMIT = 100;
export const MAX_QUERY_LIMIT = 500;

// A model that asks for 10000 rows gets 500, not an error — the response always
// carries `total`, so it can see what it did not get and page with offset.
export function clampLimit(limit) {
  if (!Number.isFinite(limit)) return DEFAULT_QUERY_LIMIT;
  return Math.min(Math.max(Math.trunc(limit), 1), MAX_QUERY_LIMIT);
}

export function registerReadTools(server, bridge) {
  server.tool(
    "revit_status",
    "Check whether Revit is reachable: version, active document name/path, and whether the model is workshared. Run this first when any other Revit tool fails.",
    {},
    { title: "Revit Status", readOnlyHint: true },
    async () => {
      try {
        const result = await bridge.call("/status");
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_list_levels",
    "List the levels in the active document: id, name and elevation (Revit internal units, decimal feet).",
    {},
    { title: "List Levels", readOnlyHint: true },
    async () => {
      try {
        const result = await bridge.call("/levels");
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_list_categories",
    "List the categories present in the active document with an element count each. Use this to find the exact category name to pass to revit_query_elements.",
    {},
    { title: "List Categories", readOnlyHint: true },
    async () => {
      try {
        const result = await bridge.call("/categories");
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_query_elements",
    "Find elements by category and/or level and/or type name. Returns compact rows (id, name, category, level, type) plus the total match count, so you always know how much you did not see. Page with offset.",
    {
      category: z
        .string()
        .optional()
        .describe("Category name as shown by revit_list_categories (e.g. 'Walls')"),
      level: z.string().optional().describe("Level name (e.g. 'Level 1')"),
      type_name: z
        .string()
        .optional()
        .describe("Element type name, matched case-insensitively as a substring"),
      limit: z
        .number()
        .int()
        .positive()
        .default(DEFAULT_QUERY_LIMIT)
        .describe(
          `Max rows to return (default ${DEFAULT_QUERY_LIMIT}, hard cap ${MAX_QUERY_LIMIT} — higher values are clamped, not rejected)`,
        ),
      offset: z
        .number()
        .int()
        .min(0)
        .default(0)
        .describe("Rows to skip, for paging through a large result"),
    },
    { title: "Find Elements", readOnlyHint: true },
    async ({ category, level, type_name, limit, offset }) => {
      try {
        // The tool argument stays snake_case like every other tool's, but the
        // bridge reads `typeName` — map it here rather than on the C# side.
        const result = await bridge.call("/query", {
          category,
          level,
          typeName: type_name,
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
    "revit_get_elements",
    "Read specific elements by id. Pass params to get exactly those parameters and nothing else; without params you get identity only (id, name, category, type).",
    {
      ids: z
        .array(z.number().int())
        .min(1)
        .describe("Element ids (from revit_query_elements or revit_get_selection)"),
      params: z
        .array(z.string())
        .optional()
        .describe("Parameter names to read, e.g. ['Comments', 'Mark', 'Unconnected Height']"),
    },
    { title: "Read Elements", readOnlyHint: true },
    async ({ ids, params }) => {
      try {
        const result = await bridge.call("/elements", { ids, params });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_get_selection",
    "Read what the user currently has selected in the Revit UI. Use this when the user says 'this wall', 'the selected elements' or similar.",
    {},
    { title: "Read Current Selection", readOnlyHint: true },
    async () => {
      try {
        const result = await bridge.call("/selection");
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );
}
