// Tools that change the model. The Revit side wraps every one of these in a
// transaction group that is assimilated on success, so one tool call collapses
// into exactly one Ctrl+Z for the user — and a failure rolls the whole call
// back instead of leaving half a change behind.
//
// All lengths and coordinates are Revit internal units: decimal feet.

import { z } from "zod";

export function registerWriteTools(server, bridge) {
  server.tool(
    "revit_create_levels",
    "Create levels in the active document. One call is one undo step.",
    {
      levels: z
        .array(
          z.object({
            name: z.string().min(1).describe("Level name, must be unique in the document"),
            elevation: z.number().describe("Elevation in feet (Revit internal units)"),
          }),
        )
        .min(1)
        .describe("Levels to create"),
    },
    { title: "Create Levels", readOnlyHint: false, destructiveHint: false },
    async ({ levels }) => {
      try {
        const result = await bridge.call("/levels/create", { levels });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_create_walls",
    "Create walls on a level from 2D curves. Use revit_list_levels for level names and revit_query_elements (category 'Walls') to find an existing wall type name. One call is one undo step.",
    {
      level: z.string().min(1).describe("Level name the walls are hosted on"),
      wall_type: z.string().min(1).describe("Wall type name, e.g. 'Generic - 200mm'"),
      height: z
        .number()
        .positive()
        .describe("Unconnected height in feet (Revit internal units)"),
      curves: z
        .array(
          z.object({
            start: z.object({ x: z.number(), y: z.number() }),
            end: z.object({ x: z.number(), y: z.number() }),
          }),
        )
        .min(1)
        .describe("Wall centrelines in plan, in feet. One wall per curve."),
    },
    { title: "Create Walls", readOnlyHint: false, destructiveHint: false },
    async ({ level, wall_type, height, curves }) => {
      try {
        // The tool argument stays snake_case like every other tool's, but the
        // bridge reads `wallType` — map it here rather than on the C# side.
        const result = await bridge.call("/walls/create", {
          level,
          wallType: wall_type,
          height,
          curves,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_set_parameters",
    "Set one parameter to the same value on one or more elements. One call is one undo step. Revit uses one display name for more than one parameter — a family instance has TWO called 'Level', one of them read-only — so a name that matches several comes back as AMBIGUOUS_PARAMETER listing each candidate's id rather than being guessed at. Pass that id back as parameter_id to write the one you mean. Read the ids with revit_inspect_elements (include_parameters), which also reports duplicateParameters.",
    {
      ids: z.array(z.number().int()).min(1).describe("Element ids to change"),
      name: z.string().min(1).describe("Parameter name as it appears in Revit, e.g. 'Comments'"),
      value: z
        .union([z.string(), z.number(), z.boolean()])
        .describe("New value. Numbers are in Revit internal units; booleans map to Yes/No parameters."),
      parameter_id: z
        .number()
        .int()
        .optional()
        .describe(
          "Revit's own parameter id, for when the name matches more than one: negative for a built-in (e.g. -1002062 SCHEDULE_LEVEL_PARAM), positive for a shared or project parameter. Must be a parameter of the name given. Only needed when the name is ambiguous.",
        ),
    },
    { title: "Set Parameters", readOnlyHint: false, destructiveHint: false },
    async ({ ids, name, value, parameter_id }) => {
      try {
        // Left out entirely when it was not given: the bridge only asks which
        // parameter when the name is genuinely ambiguous.
        const payload = { ids, name, value };
        if (parameter_id !== undefined) payload.parameterId = parameter_id;

        const result = await bridge.call("/parameters/set", payload);
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
    "revit_delete_elements",
    "Delete elements by id. Deleting a host also deletes what it hosts, so the response reports every id Revit actually removed. One call is one undo step.",
    {
      ids: z.array(z.number().int()).min(1).describe("Element ids to delete"),
    },
    { title: "Delete Elements", readOnlyHint: false, destructiveHint: true },
    async ({ ids }) => {
      try {
        const result = await bridge.call("/elements/delete", { ids });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );
}
