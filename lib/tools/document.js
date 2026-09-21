// Document-level tools: the lifecycle of the document itself — new, open,
// save, save as, close.
//
// Unlike the write tools none of these is a model edit or an undo step —
// creating, saving, opening and closing a document cannot happen inside a Revit
// transaction — and revit_new_project / revit_open_project are the tools that
// work with no document open, which is exactly the state Revit is in when
// somebody asks for a new project.

import { z } from "zod";

// Revit ships its templates per version under ProgramData. The metric/English
// one is the sane default: metric units with English level and parameter names,
// so everything else in this MCP reads the way the user expects.
export const DEFAULT_TEMPLATE_PATH =
  "C:\\ProgramData\\Autodesk\\RVT 2027\\Templates\\Default_M_ENG.rte";

export function registerDocumentTools(server, bridge) {
  server.tool(
    "revit_new_project",
    "Create a new Revit project from a template and open it, without using the Revit UI. Works when no document is open. The default template is the metric one with English names. Rebuilding a project belongs at the same save_path with overwrite true — do not iterate into a new filename.",
    {
      save_path: z
        .string()
        .min(1)
        .describe(
          "Full path of the .rvt file to create, e.g. 'C:\\\\Projects\\\\House.rvt'. Fails if the file already exists unless overwrite is true; a missing parent folder is created.",
        ),
      template_path: z
        .string()
        .min(1)
        .default(DEFAULT_TEMPLATE_PATH)
        .describe(
          `Full path of the .rte project template (default ${DEFAULT_TEMPLATE_PATH} — metric, English names)`,
        ),
      overwrite: z
        .boolean()
        .optional()
        .describe(
          "Rebuild the project in place: DELETES the existing save_path and the Revit backups beside it (House.0001.rvt, House.0002.rvt) and closes it in Revit first if it is open, then builds the project again at that same path. Defaults to false, which fails with FILE_EXISTS instead. A file something else still holds comes back as FILE_LOCKED — nothing is ever built under a different name.",
        ),
    },
    { title: "New Project", readOnlyHint: false, destructiveHint: true },
    async ({ save_path, template_path, overwrite }) => {
      try {
        // The tool arguments stay snake_case like every other tool's, but the
        // bridge reads `savePath`/`templatePath` — map it here rather than on
        // the C# side.
        const payload = {
          savePath: save_path,
          templatePath: template_path,
        };

        // Left off the body entirely unless the caller said something, so the
        // bridge's own default — never overwrite — is what answers for everyone
        // who does not ask.
        if (overwrite !== undefined) {
          payload.overwrite = overwrite;
        }

        const result = await bridge.call("/document/new", payload);
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_save",
    "Save the active document in place. A model that has never been saved has no path to save to and comes back as NOT_SAVEABLE — use revit_save_as for that one.",
    {},
    { title: "Save Project", readOnlyHint: false, destructiveHint: false },
    async () => {
      try {
        const result = await bridge.call("/document/save");
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_save_as",
    "Save the active document to a new path and carry on working in it. An existing file is an error unless overwrite is true; a missing parent folder is created.",
    {
      save_path: z
        .string()
        .min(1)
        .describe(
          "Full path of the .rvt file to write, e.g. 'C:\\\\Projects\\\\House.rvt'. Must not exist unless overwrite is true.",
        ),
      overwrite: z
        .boolean()
        .default(false)
        .describe("Replace save_path if it already exists. Default false: an existing file fails with FILE_EXISTS."),
    },
    { title: "Save Project As", readOnlyHint: false, destructiveHint: true },
    async ({ save_path, overwrite }) => {
      try {
        // The tool arguments stay snake_case like every other tool's, but the
        // bridge reads `savePath` — map it here rather than on the C# side.
        const result = await bridge.call("/document/save-as", {
          savePath: save_path,
          overwrite,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_open_project",
    "Open an existing .rvt file and make it the active document, without using the Revit UI. Works when no document is open. A path that does not exist comes back as FILE_NOT_FOUND.",
    {
      path: z
        .string()
        .min(1)
        .describe("Full path of the existing .rvt file to open, e.g. 'C:\\\\Projects\\\\House.rvt'"),
    },
    { title: "Open Project", readOnlyHint: false, destructiveHint: false },
    async ({ path }) => {
      try {
        const result = await bridge.call("/document/open", { path });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_close_project",
    "Close the active document. Unsaved changes are DISCARDED unless save is true, so save first if the work matters. Closing when nothing is open is not an error — it answers {\"closed\":false}. Revit will not close the active document outright, so the bridge makes another open document active first — a blank scratch project under %TEMP% when this was the only one — and the response reports what became active in activePath / activeTitle / activeIsScratch.",
    {
      save: z
        .boolean()
        .default(false)
        .describe("Save the document before closing it. Default false: unsaved changes are discarded."),
    },
    { title: "Close Project", readOnlyHint: false, destructiveHint: true },
    async ({ save }) => {
      try {
        const result = await bridge.call("/document/close", { save });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );
}
