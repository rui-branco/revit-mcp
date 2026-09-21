#!/usr/bin/env node

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { pathToFileURL } from "url";
import { autoInstallBridge } from "./lib/auto-install.js";
import { createBridge } from "./lib/bridge.js";
import { registerReadTools } from "./lib/tools/read.js";
import { registerWriteTools } from "./lib/tools/write.js";
import { registerParameterTools } from "./lib/tools/parameters.js";
import { registerModelTools } from "./lib/tools/model.js";
import { registerGeometryTools } from "./lib/tools/geometry.js";
import { registerMaterialTools } from "./lib/tools/materials.js";
import { registerMaterialAppearanceTools } from "./lib/tools/material-appearance.js";
import { registerDocumentTools } from "./lib/tools/document.js";
import { registerSheetTools } from "./lib/tools/sheets.js";
import { registerSheetCollectionTools } from "./lib/tools/sheet-collections.js";
import { registerTitleblockTools } from "./lib/tools/titleblocks.js";
import { registerViewTools } from "./lib/tools/views.js";
import { registerGraphicsTools } from "./lib/tools/graphics.js";
import { registerDetailTools } from "./lib/tools/detail.js";
import { registerDocumentationTools } from "./lib/tools/documentation.js";
import { registerDiagnosticsTools } from "./lib/tools/diagnostics.js";
import { registerQualityTools } from "./lib/tools/quality.js";
import { registerReloadTools } from "./lib/tools/reload.js";
import { registerInstallTools } from "./lib/tools/install.js";

const instructions = `# Revit MCP

This MCP drives the model that is open in Autodesk Revit on this machine. Node
talks to a bridge add-in running inside Revit over HTTP on localhost.

## When to use these tools

Use these tools whenever the user wants to:
- Ask about the open model (e.g. "what levels are there", "how many walls", "what's selected")
- Read parameters off elements (e.g. "what's the Mark on these doors")
- Change the model (e.g. "add a level at 3m", "set Comments on the selection")
- Populate a project (e.g. "build the site surface", "pave the terrace", "plant trees along the path")
- Start a project or set up drawings (e.g. "new project from the metric template", "create sheets A101-A105")
- Fill sheets (e.g. "make a plan of each level and put it on its sheet", "schedule the planting")
- Draw details (e.g. "a drafting view of the paving build-up, with the layers labelled")
- Tidy a drawing set (e.g. "set every plan to 1:100", "add a Phase parameter to the sheets and stamp L.02 on them")
- Open, save or close a model (e.g. "open C:\\Projects\\House.rvt", "save the model", "close without saving")

## IMPORTANT

- Run revit_status first if anything fails — it tells you whether Revit is even reachable.
- If nothing is listening, the bridge add-in is probably not installed: run revit_install_bridge, then tell the user to restart Revit.
- revit_query_elements caps at 500 rows. It always returns the total match count: if total is larger than what you got, say so instead of pretending the list is complete.
- revit_get_elements returns only the parameters you name in params. Ask for what you need, not everything.
- Every write tool is one undo step for the user. Do not split one logical change across several calls.
- revit_new_project and revit_open_project are the tools that work with no document open; everything else needs a model.
- The bridge answers Revit's modal dialogs and resolves its transaction warnings by itself, so an unattended run never stalls. Call revit_diagnostics after a batch of writes: a warning it resolved silently may mean the model did something you did not intend.
- revit_close_project discards unsaved changes unless save is true. Call revit_save first when the work matters.
- One project lives at one path and is rebuilt in place. When an attempt goes wrong, call revit_new_project again with the SAME save_path and overwrite: true — it closes that project in Revit, deletes the file and its backups, and builds it again. Never version a project into a new filename per iteration.
- Call revit_list_titleblocks before revit_create_sheets: a sheet needs a title block, and an empty list means one has to be loaded in Revit first.
- A sheet stays empty until something is on it: revit_place_views_on_sheets is what fills it, and it handles schedules as well as views.
- A view lives on exactly one sheet. To show the same view twice, revit_duplicate_view first.
- Revit's API cannot create the first legend in a document. revit_list_legends says what exists and revit_create_legend duplicates one; when there is none, ask the user to make one legend in the Revit UI (View tab > Legends > Legend) rather than passing off a drafting view as a legend.
- Collapsible sheet groups ARE settable: revit_set_sheet_collections builds Revit's own sheet collections, so the grouping is in the model when it opens, with nothing to set up in the UI. It is a dry run until you pass dry_run: false, it reuses a collection of the same name rather than duplicating it, and it never renames or renumbers a sheet. revit_list_sheet_collections reads what exists. That is a different thing from the browser organisation scheme below, which groups by a PARAMETER and still cannot be applied from the API.
- Grouping sheets into folders in the Project Browser is not settable from the API. revit_get_browser_organization shows the scheme in force, the scheme names the document has and the folders each sheet really sits in, and it reports canApplyFromApi false — that is the API's limit, not a bug to work around. Add the field with revit_create_project_parameter and stamp it with revit_set_sheet_parameters, then tell the user to point the browser organisation at that parameter once in the UI (right-click Sheets > Browser Organization). Stamping the parameter is NOT the grouping being applied; never report it as such.
- Load real families before falling back to primitives. revit_load_families takes .rfa paths from Autodesk's library — C:\\ProgramData\\Autodesk\\RVT <year>\\Libraries\\<language>\\, under Planting\\, Site\\Accessories\\, Lighting\\Architectural\\External\\, Furniture\\, Doors\\, Windows\\ — and a library one release behind the running Revit still loads, because Revit upgrades it. That is the difference between a model made of real content and one made of spheres on sticks. Only when the library is genuinely not installed do revit_place_planting and revit_create_directshape become the answer; there are no MEP families either, so revit_create_pipes and revit_place_sprinklers model irrigation that way regardless.
- revit_list_family_symbols is what the ids to place come from, and it filters by category, by family_name, or both. revit_place_families takes z as an absolute model elevation like everything else here and reports placedZ read back off each instance — check it rather than assuming.
- Doors and windows are NOT revit_place_families: they have to cut the wall they sit in, and only revit_place_openings hosts them. Unhosted, a door stands in front of an uncut wall and the building stays a sealed box. It finds the nearest wall to each point for you and reports which one it used.
- You can SEE the model: revit_create_3d_view (read modelExtents from it, then aim eye/target), revit_set_view_style with Realistic, then revit_export_view_image. Open the file reported under 'path', never the one you asked for — Revit renames it. This is not a photoreal render; the API cannot start Revit's raytracer. Cast shadows are revit_set_view_graphics, which probes whether the view's shadows parameter is writable and either writes it or posts Revit's own shadows command — read the method it reports rather than assuming it worked.
- Three more calls are what stop an export reading as a screenshot of Revit: revit_set_view_background (a 3D view is drawn on flat dark slate until you give it a sky), revit_hide_view_categories with ['annotation'] (level datums and section marks float through an otherwise finished isometric), and revit_set_view_sun (the direction everything is lit from). Sun settings can be shared between views — check sharesSettings in the response.
- Shadows and repeatable graphics: revit_get_view_graphics diagnoses what a view can actually take, revit_set_view_graphics sets style, detail, the lighting sliders and cast shadows, and revit_capture_view_template + revit_apply_view_template give a whole set of views the same graphics in one undo step. Be straight with the user about two things: a shadows write that reports method 'posted-command' is PENDING and unverified, not done; and a template captured from a view whose shadows were off turns shadows on nowhere.
- A PNG is for looking at the model; a PDF is the deliverable. revit_export_view_image captures the view as Revit draws it on screen, so a dark view background comes out as a black-paper negative of the drawing and no setting makes that printable. revit_export_pdf is Revit's own exporter: vectors, white paper, real sheet size. Use it for anything anyone will read or plot, name the views and sheets explicitly, and trust the manifest's path/bytes — they are read off the disk, and a missing file is an error rather than a success.
- A drawing that comes out tiny in the corner of its sheet is almost always an uncropped view: section marks and elevation markers sit far outside the building and the viewport is sized to all of it. The order that fixes it is revit_set_view_crop (model feet, dry run first), then revit_set_view_scale, then revit_get_sheet_layout to MEASURE where things landed, then revit_set_viewport_position (paper feet, centre of the box) if it still needs moving. Never guess a viewport coordinate — read the layout first. revit_hide_elements_in_view takes out what is merely in the way without deleting it, and revit_read_schedule is the only way to see what a schedule actually says. A PERSPECTIVE view breaks that order at one step: it has no view scale, so revit_set_view_scale refuses it with PERSPECTIVE_VIEW_HAS_NO_SCALE and revit_scale_perspective_crop re-sizes it instead — one multiplier, proportions locked, same shot, camera untouched. Reframing a perspective is still revit_set_view_crop; the two are not interchangeable.
- A schedule on a sheet is not a viewport: it is anchored by its top-left corner, so it moves with revit_set_schedule_position (the instance id from revit_get_sheet_layout, not the schedule id), never with revit_set_viewport_position. A quantities schedule that lists every instance on its own row is not broken data — that is "Itemize every instance", and revit_configure_schedule with itemized false plus group_by is what collapses it to one row per type with a count. Check bodyRows before and after: that is the proof it worked.
- The documentation write tools default dry_run TRUE, unlike everything else here. That is deliberate: they edit drawings a human has approved. Read the dry run's 'before', then call again with dry_run false. A crop refused with CROP_CONTROLLED_BY_TEMPLATE means the view's template owns it — say so and offer to change the template, rather than working around it.
- Elements built that way schedule as one lump unless you say what they are. Put the species, the type or the zone in each entry's comments and mark — that is what makes a quantities schedule a breakdown.
- Nothing has a material unless you give it one, and the whole model renders grey until you do. Materials go on at BUILD time for geometry (material_id on revit_create_directshape, revit_place_planting, revit_create_pipes, revit_place_sprinklers) because the solid carries it; walls and floors take theirs from their type, so make one with revit_create_wall_type / revit_create_floor_type first. revit_assign_material is only for elements that already exist and can take one — it reports the ones that cannot.
- Changing how an EXISTING material looks in a render is revit_get_material_appearance then revit_set_material_appearance: read the appearance asset's real property names and types, then patch the ones you name. Never guess a property name — the asset's schema decides them — and never read a null appearanceAssetId as "it needs a texture": create_generic gives that material a textureless Generic asset to patch. The asset is copied before it is patched, so one material's new finish can never appear on another's surfaces.
- Paving on graded terrain overlaps the surface unless you deal with it: revit_flatten_toposolid levels the region under it, and revit_create_floor takes an offset to lift the slab clear.
- All lengths, coordinates and elevations are Revit internal units: decimal feet. Sheet coordinates are feet on the paper, not model feet.
- Element ids are integers and only valid for the currently open document.
`;

export function createRevitServer(bridge = createBridge()) {
  const server = new McpServer({ name: "revit", version: "1.0.0" }, { instructions });

  registerReadTools(server, bridge);
  registerWriteTools(server, bridge);
  registerParameterTools(server, bridge);
  registerModelTools(server, bridge);
  registerGeometryTools(server, bridge);
  registerMaterialTools(server, bridge);
  registerMaterialAppearanceTools(server, bridge);
  registerDocumentTools(server, bridge);
  registerSheetTools(server, bridge);
  registerSheetCollectionTools(server, bridge);
  registerTitleblockTools(server, bridge);
  registerViewTools(server, bridge);
  registerGraphicsTools(server, bridge);
  registerDetailTools(server, bridge);
  registerDocumentationTools(server, bridge);
  registerDiagnosticsTools(server, bridge);
  registerQualityTools(server, bridge);
  registerReloadTools(server, bridge);

  // The install tools shell out to revit-bridge/install.ps1 and never touch the
  // bridge, so they keep working when Revit is closed or the add-in is missing.
  registerInstallTools(server);

  return server;
}

async function main() {
  const server = createRevitServer();
  const transport = new StdioServerTransport();
  await server.connect(transport);

  // Never awaited: the transport is already up, and a slow or failing install
  // must not delay the handshake or stop the server from serving tool calls.
  // It is startup only, so it stays out of createRevitServer().
  autoInstallBridge();

  process.stderr.write("Revit MCP server running on stdio\n");
}

// Only start the server when run as the entry point — the tests import
// createRevitServer() and must not have stdio hijacked from under them.
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((err) => {
    process.stderr.write(`Fatal: ${err.message}\n`);
    process.exit(1);
  });
}
