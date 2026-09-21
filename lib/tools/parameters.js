// Project parameters: making one, and writing a different value per element.
//
// A project parameter in Revit is a shared parameter definition plus a binding
// to categories. The definition lives in a text file outside the model, which
// is a user-wide Revit setting — the bridge borrows it for the length of the
// call and puts the original back, so creating a parameter never leaves the
// user's Revit pointing somewhere new.
//
// revit_set_parameters (in write.js) is the other half of this and a different
// job: it writes ONE name and ONE value across a list of ids, all or nothing.
// The tool here writes a DIFFERENT value per sheet, which is what stamping a
// phase across 56 sheets is.

import { z } from "zod";

export function registerParameterTools(server, bridge) {
  server.tool(
    "revit_create_project_parameter",
    "Create a project parameter and bind it to one or more categories — the way to add a field Revit does not have, such as a Phase on sheets that the Project Browser can group by. Defaults to a Text instance parameter under Identity Data. Pass 'category' for one category or 'categories' for several; both name categories as Revit shows them ('Sheets') or as BuiltInCategory names ('OST_Sheets'). A parameter of that name already bound in this document is NOT an error: nothing is created, 'created' is false and 'alreadyExisted' is true, and the categories reported are the ones it is really bound to — so re-running the same call is safe. One call is one undo step. Write the values afterwards with revit_set_sheet_parameters (per sheet) or revit_set_parameters (one value across many elements).",
    {
      name: z.string().min(1).describe("Parameter name as it will appear in Revit, e.g. 'Phase'"),
      category: z
        .string()
        .min(1)
        .optional()
        .describe("Single category to bind to, e.g. 'Sheets' or 'OST_Sheets'. Use this or categories, not both."),
      categories: z
        .array(z.string().min(1))
        .min(1)
        .optional()
        .describe("Categories to bind to. Use this or category, not both."),
      type: z
        .enum([
          "Text",
          "MultilineText",
          "Url",
          "Integer",
          "Number",
          "YesNo",
          "Length",
          "Area",
          "Volume",
          "Angle",
        ])
        .optional()
        .describe("Parameter data type. Defaults to Text. Length, Area, Volume and Angle are in Revit internal units (feet, square feet, cubic feet, radians)."),
      group: z
        .enum([
          "IdentityData",
          "Text",
          "Data",
          "General",
          "Graphics",
          "Constraints",
          "Geometry",
          "Phasing",
          "Title",
        ])
        .optional()
        .describe("Group the parameter appears under in the Properties palette. Defaults to IdentityData."),
      instance: z
        .boolean()
        .optional()
        .describe("True (the default) binds it per element; false binds it to the type, so every element of that type shares one value."),
    },
    { title: "Create Project Parameter", readOnlyHint: false, destructiveHint: false },
    async ({ name, category, categories, type, group, instance }) => {
      // Checked here rather than in the schema: a raw shape cannot express
      // "one of these two", and a call with neither must not reach Revit.
      if (category === undefined && categories === undefined) {
        return {
          content: [
            {
              type: "text",
              text: "Error: pass either category (one) or categories (several).",
            },
          ],
        };
      }
      if (category !== undefined && categories !== undefined) {
        return {
          content: [
            { type: "text", text: "Error: pass either category or categories, not both." },
          ],
        };
      }

      try {
        const result = await bridge.call("/parameters/create-project", {
          name,
          category,
          categories,
          type,
          group,
          instance,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_set_sheet_parameters",
    "Write a parameter value per sheet — a different value on each, which is what stamping a phase or a discipline across a set of sheets is. Pass every sheet in one call: the whole batch is one undo step, and a sheet whose parameter is missing or read-only comes back under 'failed' with its code while the rest are still written. Instance parameters only: writing to the type would change every other sheet using it. Create the parameter first with revit_create_project_parameter bound to Sheets — a name no sheet carries comes back as PARAMETER_NOT_FOUND. Use revit_set_parameters instead when one value goes on many elements.",
    {
      values: z
        .array(
          z.object({
            sheet_id: z.number().int().describe("Sheet id from revit_list_sheets"),
            name: z
              .string()
              .min(1)
              .describe("Parameter name as it appears in Revit, e.g. 'Phase'"),
            value: z
              .union([z.string(), z.number(), z.boolean()])
              .describe("Value for this sheet. Numbers are in Revit internal units; booleans map to Yes/No parameters."),
          }),
        )
        .min(1)
        .describe("Sheet/name/value triples, all in this one call"),
    },
    { title: "Set Sheet Parameters", readOnlyHint: false, destructiveHint: false },
    async ({ values }) => {
      try {
        // The tool arguments stay snake_case like every other tool's; the bridge
        // reads sheetId — map it here rather than on the C# side.
        const result = await bridge.call("/sheets/set-parameter", {
          values: values.map((entry) => ({
            sheetId: entry.sheet_id,
            name: entry.name,
            value: entry.value,
          })),
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );
}
