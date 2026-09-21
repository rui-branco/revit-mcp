# revit-mcp

[![Revit](https://img.shields.io/badge/Revit-2025%20%7C%202026%20%7C%202027-006666)](https://www.autodesk.com/products/revit/overview)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D4)](#windows-only-by-nature)
[![Node.js](https://img.shields.io/badge/node-%E2%89%A518-339933)](https://nodejs.org)
[![Tests](https://img.shields.io/badge/tests-507%20passing-success)](#development)
[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

**Read and edit the Autodesk Revit model that is open on your desktop, from Claude
or any other MCP client.**

`revit-mcp` exposes the running Revit session as MCP tools: list levels and
categories, query and filter elements, read and write parameters, create levels,
walls, floors and toposolids, place families and planting, build sheets,
schedules, sections and views, export images and PDFs, and drive the document
itself — new, open, save, close.

```
MCP client (Claude Code, Claude Desktop, ...)
    |  stdio (MCP)
    v
revit-mcp             Node, this package
    |  HTTP POST, JSON in / JSON out
    |  http://localhost:48884/revit-mcp
    v
Revit bridge add-in   C#, running INSIDE Revit.exe
    |  direct API calls on Revit's main thread
    v
the open model
```

It is built to run **unattended**: Revit's modal dialogs and its transaction
warnings are answered by the bridge instead of parking Revit until somebody
clicks, and every one of them is recorded for you to read back. See
[Unattended operation](#unattended-operation) — that record is not optional
reading.

> **Status, honestly.** The Node half is covered by 507 tests and the C# add-in
> compiles clean with 0 warnings. The full chain — install, dialog handling, the
> transaction failure preprocessor, and hot reload (`unloadedPrevious: true`) —
> has been exercised against a **live Revit 2027.3 session**, driving a real
> project from empty model to an exported 13-sheet PDF set. Revit **2025 and
> 2026 are compile-verified but have not been exercised live**; one binary
> covers all three, so they are expected to work, but treat a first run as a
> first run and work on a copy of your model.

## Contents

- [Why an add-in is required](#why-an-add-in-is-required)
- [Requirements](#requirements)
- [Installation](#installation)
  - [Claude Code](#claude-code)
  - [Claude Desktop](#claude-desktop)
  - [Other MCP clients](#other-mcp-clients)
- [Tools](#tools)
- [Unattended operation](#unattended-operation)
- [Environment variables](#environment-variables)
- [Troubleshooting](#troubleshooting)
- [Security](#security)
- [Development](#development)
- [License](#license)

## Why an add-in is required

Revit has **no external API**. There is no REST endpoint, no CLI, no COM
automation surface — the Revit API is in-process .NET that only exists inside
`Revit.exe`, and it may only be called from Revit's own main thread. No separate
process can reach it. So anything that touches a model has to be *code Revit
itself loaded*. That is why this project has two halves: a Node MCP server, and
a small C# add-in that Revit loads at startup and that listens on loopback HTTP
so the Node half has something to talk to.

The add-in ships **precompiled** with this package, so installing it is a file
copy — no .NET SDK required.

### Windows only, by nature

Revit has no macOS build, so the half that matters only ever runs on Windows.
The Node half is platform-agnostic ESM and will start anywhere; off Windows it
simply has nothing to talk to.

## Requirements

| | |
| --- | --- |
| **OS** | Windows |
| **Revit** | 2025, 2026 or 2027 — one prebuilt add-in covers all three |
| **Node.js** | 18 or newer |
| **.NET SDK** | **Not required.** Only for building the C# side yourself — see [Development](#development) |

- The add-in is compiled against the **Revit 2025 reference assemblies**, which
  load unchanged in 2026 and 2027 — which is why one binary covers all three.
- **Revit LT is not supported and never will be** — it has no add-in API at all,
  so there is nothing for the bridge to attach to.
- **Revit 2024 and earlier are refused** by the installer. They host add-ins on
  .NET Framework 4.8 and physically cannot load this `net8.0-windows` assembly.

## Installation

Installation is two things: registering the MCP server with your client, then
installing the bridge add-in into Revit. Pick your client below for step 1, then
follow steps 2–4, which are the same for everyone.

> **Not yet on npm.** Install straight from GitHub with
> `npx -y github:rui-branco/revit-mcp`, as shown below. Once the package is
> published, `npx -y @rui.branco/revit-mcp` will work the same way.

### Step 1 — Register the MCP server

#### Claude Code

One command:

```bash
claude mcp add revit --scope user -- npx -y github:rui-branco/revit-mcp
```

`--scope user` makes it available in every project. Use `--scope project`
instead to commit it to a repo's `.mcp.json` and share it with your team.

Verify it registered:

```bash
claude mcp list
```

<details>
<summary>Or configure it by hand</summary>

Add this to `~/.claude.json` under `mcpServers`:

```json
{
  "mcpServers": {
    "revit": {
      "type": "stdio",
      "command": "npx",
      "args": ["-y", "github:rui-branco/revit-mcp"],
      "env": {}
    }
  }
}
```

</details>

#### Claude Desktop

1. Open **Settings → Developer → Edit Config**, which opens
   `%APPDATA%\Claude\claude_desktop_config.json`.
2. Add the `revit` server. Keep any servers already in the file:

   ```json
   {
     "mcpServers": {
       "revit": {
         "command": "npx",
         "args": ["-y", "github:rui-branco/revit-mcp"]
       }
     }
   }
   ```

3. **Restart Claude Desktop completely** — close it from the system tray, not
   just the window. It only reads that file at startup.
4. The Revit tools then appear under the tools icon in the chat box.

> **If the server shows as failed**, Claude Desktop could not find `npx` on its
> `PATH`. Give it absolute paths instead — `where.exe node` and
> `where.exe npx.cmd` print them:
>
> ```json
> {
>   "mcpServers": {
>     "revit": {
>       "command": "C:\\Program Files\\nodejs\\npx.cmd",
>       "args": ["-y", "github:rui-branco/revit-mcp"]
>     }
>   }
> }
> ```
>
> Running from a local clone avoids the question entirely: set `command` to your
> `node.exe` and `args` to `["C:\\path\\to\\revit-mcp\\index.js"]`.

#### Other MCP clients

Any client that speaks MCP over stdio works. Run `npx -y github:rui-branco/revit-mcp`
as the server command; it needs no arguments and no environment beyond the
optional [environment variables](#environment-variables).

### Step 2 — Install the Revit bridge add-in

Ask Claude to **install the Revit bridge**. That is the whole step, in plain
words. It runs the `revit_install_bridge` tool, which copies the bundled add-in
and its `.addin` manifest into `%APPDATA%\Autodesk\Revit\Addins\<version>\` for
every Revit it finds.

Revit does **not** need to be running, and this tool works before the bridge
exists — it never talks to the bridge, it shells out to a PowerShell installer.
Re-running it is safe. (Doing it by hand instead:
[Manual install](#manual-install-without-claude).)

### Step 3 — Restart Revit and approve the add-in

Revit only scans the Addins folder at startup, so the bridge does not load until
Revit is restarted.

On that first load Revit shows a **"Security - Unsigned Add-In"** dialog naming
`RevitMcpBridge` and an unknown publisher. **Expect this — it is not a failure.**
The add-in is not code-signed.

Choose **Always Load**. *Load Once* means the dialog returns at every launch;
*Do Not Load* means the bridge never starts and every tool call will report that
nothing is listening — and Revit remembers that answer.

If you are on a machine where someone else decides what may run, that is the
decision point; see [Security](#security).

### Step 4 — Verify the chain

Open a model, then ask Claude to run `revit_status`. It reports the bridge
version, the Revit version and the active document — which confirms client →
Node → add-in → model end to end.

### Uninstalling

Ask Claude to uninstall the Revit bridge (`revit_uninstall_bridge`), then restart
Revit. Remove the server from your client with `claude mcp remove revit`, or by
deleting the entry from `claude_desktop_config.json`.

### Manual install, without Claude

The same work, straight from the installed package. From the package's
`revit-bridge` directory:

```powershell
.\install.ps1
```

| Flag | Effect |
| --- | --- |
| `-RevitVersion 2026` | Only that version, even if Revit is not in the default Program Files location. |
| `-Build` | Compile a fresh add-in with `dotnet build -c Release` instead of using the bundled one. **Needs the .NET SDK**; for contributors. |
| `-SkipBuild` | Never build. This is already the default whenever the bundled add-in is present. |
| `-Uninstall` | Remove the manifest and the install folder. |
| `-Json` | Emit one JSON result object as the only stdout output — the mode the MCP tools use. |

Exit codes: `0` success, `1` unhandled failure, `2` no Revit found (on
uninstall: nothing to remove), `3` no supported Revit version, `4` the build
failed or the .NET SDK is missing, `5` no add-in to install. The installer runs
under Windows PowerShell 5.1 as well as PowerShell 7.

## Tools

| Tool | What it does |
| --- | --- |
| `revit_status` | Bridge and Revit version, units, user, and the active document (`null` when none is open). Run this first when anything misbehaves. |
| `revit_list_levels` | Every level: `{ id, name, elevation }`. |
| `revit_list_categories` | Every category present in the model with its element count, busiest first. |
| `revit_query_elements` | Filter elements by category, level and type name, with `limit` / `offset`. Answers with the full match count plus a page of compact rows. |
| `revit_get_elements` | Rows for specific element ids, carrying **only** the parameters you name in `params`. |
| `revit_get_selection` | What the user currently has selected in the Revit UI. |
| `revit_create_levels` | Create levels from `{ name, elevation }` pairs. |
| `revit_create_walls` | Create walls on a level from a wall type, a height and a list of `{ start, end }` curves. |
| `revit_set_parameters` | Set one named parameter to one value across a batch of element ids. Instance parameters only. |
| `revit_create_project_parameter` | Create a project parameter and bind it to categories — a Phase on Sheets, say. Text instance parameter under Identity Data by default. A name already bound is reported, not an error, so re-running is safe. |
| `revit_set_sheet_parameters` | Write a **different** parameter value per sheet, the whole set in one call and one undo step. A sheet missing the parameter comes back under `failed` while the rest are written. |
| `revit_delete_elements` | Delete elements by id. Revit may remove dependent elements too, so the count can exceed what you asked for. |
| `revit_create_toposolid` | Build the site surface from survey points. Uses a Revit 2024+ Toposolid when the document has a toposolid type, and the legacy TopographySurface when it has none — the response says which. |
| `revit_flatten_toposolid` | Level a region of the site surface to one elevation so paving can sit on it. The fix for Revit's "toposolid and floor overlap" warning; the response reports the residual error so you can check it worked. |
| `revit_create_floor` | Create a floor from a closed boundary: paving, a pool deck, a terrace. The ring is closed automatically and taken at the level's elevation. `offset` lifts it off the level, the other half of the overlap fix. |
| `revit_load_families` | **Load `.rfa` files into the project.** Autodesk's library lives outside the model, under `C:\ProgramData\Autodesk\RVT <year>\Libraries\<language>\`, so nothing in it exists until this has run. A library one Revit release behind still loads — Revit upgrades the family on the way in. Every row carries the family name and its type ids, so you can place straight away. A family already in the project is reloaded, not refused; `loaded: false` with `alreadyLoaded: true` just means Revit found it identical and did nothing. A missing file is `FILE_NOT_FOUND` on its own row and the rest of the batch still loads. |
| `revit_list_family_symbols` | The family types loaded in the model, filtered by `category`, by `family_name`, or both. Empty means nothing has been loaded yet — `revit_load_families` is the fix, not a fallback to primitives. |
| `revit_place_families` | Place one family instance per point: trees, shrubs, light fittings, furniture. The symbol is activated for you. `z` is an **absolute model elevation** like everywhere else here, and each placed point reports `placedZ` read back off the instance. `level` is optional and defaults to the lowest level. A point Revit refuses is reported, not fatal. |
| `revit_place_openings` | Place doors and windows **in** walls — the instances that have to cut their host. `host_wall_id` is optional: omitted, each point is hosted in the nearest wall within 3 feet and the row reports the wall it landed in, read back off the instance. A point with no wall near it is `NO_HOST_WALL` and the rest of the batch still lands. `sill_height` is feet above the level and defaults to 3 for a window; the row says whether it went on the instance or the type. |
| `revit_create_directshape` | Build elements from raw solids — cylinder, box, sphere, cone, extrusion, or a group of them as one element — with no family involved. The route that works when no family content is installed. Each entry can carry its own comments and mark; the batch takes a `type_name` and a `material_id`. |
| `revit_place_planting` | Plant trees: one Planting element per point, each a trunk cylinder with a crown sphere on top. No family needed. Per-point comments and mark are what make the planting schedule a species breakdown. |
| `revit_create_pipes` | Model pipe runs — irrigation, drainage — as geometry: one element per run, a cylinder per segment. No MEP family needed or used. |
| `revit_place_sprinklers` | Place sprinkler heads: one small element per point. The response says which category Revit allowed them in. |
| `revit_list_materials` | Every material in the model: `{ id, name, colorRgb, appearanceAssetId }`. Where a `material_id` comes from. |
| `revit_create_material` | Create a material with a colour, transparency and shininess — and a `texture_path`, to make it textured in the same call. A name that already exists is reused untouched and comes back `created: false`. |
| `revit_set_material_texture` | Put a real bitmap on a material: `texture_path`, plus `scale` (the size of one tile, `{x, y}` in feet), `rotation` and `tint`. Revit ships the bitmaps — see Textures below. The material gets its own Generic appearance asset so texturing one can never change another's look, and the reply reports every asset property written and what the saved asset reads back as. A file that is not there is `TEXTURE_NOT_FOUND`. |
| `revit_assign_material` | Put an existing material on elements that already exist — through the type's compound structure for walls, floors and toposolids, through a material parameter for anything that has one. Elements that can take neither (every DirectShape) come back under `skipped` with the reason. |
| `revit_create_wall_type` | Create a wall type carrying a material and a thickness, by duplicating an existing type. A wall's material is a **type** property, so this is what makes walls anything other than grey. |
| `revit_create_floor_type` | The same for floors: the type that makes paving a colour rather than the template's default. |
| `revit_new_project` | Create a project from an `.rte` template and open it, without touching the Revit UI. Works with no document open. `overwrite: true` rebuilds the project **in place**: it closes that file in Revit, deletes it and its `.0001.rvt` backups, and builds it again at the same path. Default false. |
| `revit_open_project` | Open an existing `.rvt` and make it the active document. Also works with no document open. |
| `revit_save` | Save the active document in place. A model that has never been saved is a `NOT_SAVEABLE` error, not a silent no-op. |
| `revit_save_as` | Save the active document to a new path and carry on in it. Refuses an existing file unless `overwrite` is true. |
| `revit_close_project` | Close the active document — Revit refuses that from the API, so the bridge makes another document active first and reports which one in `activePath` / `activeTitle` / `activeIsScratch`. Discards unsaved changes unless `save` is true. Closing nothing answers `{"closed": false}` rather than failing. |
| `revit_list_titleblocks` | The title block family types loaded in the model. Call this before creating sheets — that is where the title block id comes from. |
| `revit_inspect_titleblock_family` | What is actually printed on the sheet: the vendor logo, the consultant placeholders left in the stock family, the labels and their paper sizes. Opens the family with `EditFamily`, reads it and closes it without saving — nothing in the project, the loaded family or the `.rfa` changes. |
| `revit_edit_titleblock_family` | The write half: strip the stock logo and the literal placeholders, retext captions (`text_edits`), resize labels (`label_sizes`), add notes (`new_notes`). Ids are **family** ids from `revit_inspect_titleblock_family`, never project ids, and `expected_family_name` must match the family they came from or nothing is opened. Only a TextNote or an ImageInstance can be removed — a label is refused by name, because deleting one takes that content off every sheet. `Document.Delete` is read back and the whole edit rolls back if it would take anything you did not name, or if a label or schedule instance disappears. `label_sizes` never edits a text type in place: it reuses a type of that size or duplicates the element's own. Loads back into the **same** project with `LoadFamily`; the `.rfa` on disk is untouched. Sizes are PAPER feet (6 mm is 0.019685). **`dry_run` defaults to true.** |
| `revit_list_sheets` | Every sheet: `{ id, number, name }`, ordered by sheet number. |
| `revit_create_sheets` | Create a batch of sheets from `{ number, name }` pairs. Numbers that already exist are skipped and reported. |
| `revit_list_views` | Every non-template view: id, name, view type, whether it is already on a sheet, and the view's own direction vectors when it has them. Sheets themselves are not listed. |
| `revit_create_plan_view` | Create a plan on a level — a floor plan, or a Site / Ceiling / Structural plan via `view_family_type`, which is the view family type's **name** as Revit shows it. An unknown name comes back listing the plan type names the document has. A taken view name gets a numeric suffix rather than failing. |
| `revit_create_drafting_view` | Create a drafting view: linework and notes, no model geometry. What a detail sheet is made of. |
| `revit_create_3d_view` | Create a 3D view — isometric, or a perspective camera with `perspective: true`. Give `eye` and `target` and the bridge works out the up/forward vectors Revit's `ViewOrientation3D` needs. The response carries `modelExtents` (`min`/`max`/`center` of everything modelled), which is how you find out where to point the camera in the first place. |
| `revit_set_view_style` | Set a view's display style (`Realistic`, `ShadingWithEdges`, `HiddenLine`, …) and detail level. Both are read back off the view, because a view template can override what you asked for. Cast shadows are not set here — pass `shadows` to `revit_set_view_graphics`, and this tool answers `SHADOWS_HANDLED_ELSEWHERE` pointing at it. |
| `revit_set_view_background` | Put a sky behind a 3D view, which is otherwise drawn on flat dark slate and exports that way. `kind` is `sky` (Revit's own sky and clouds, and it takes **no** colours), `gradient` (`sky_color`/`horizon_color`/`ground_color` as `{r,g,b}`) or `image` (`image_path`). A non-3D view is a `BAD_REQUEST`. The background is read back off the view afterwards, and `sky` reads back as Revit's enum name `SunAndClouds`. |
| `revit_hide_view_categories` | Turn categories off in one view: `['annotation']` is the shorthand for Levels, Grids, ReferencePlanes, Sections, Elevations, Cameras, SunPath and Lines. `OST_*` names work too. An unknown name is a `BAD_REQUEST` listing the accepted ones; a category Revit refuses is a skipped row with a reason while the rest still hide. Every row's `hidden` is read back off the view. |
| `revit_override_view_categories` | The other half of Visibility/Graphics: override whole categories in ONE view — paving light, planting green, context halftoned — so a set stops reading as the same CAD export printed five times. Nothing about the model changes; it is stored on the view. Overrides are **merged** onto what the view already has, so asking for a colour cannot wipe a fill or line pattern somebody set in the dialog; the patterns appear in `before`/`after` to prove they survived. The colours are LINE colours only. Line weights are 1-16 or -1 to clear; `surface_transparency` is 0-100. A view whose **template** owns the V/G is refused with `OVERRIDES_CONTROLLED_BY_TEMPLATE` — Revit would accept the write and keep drawing the template's way. A sheet, schedule or legend is `OVERRIDES_NOT_SUPPORTED`. Every name and value is validated before anything is written, so the batch is all-or-nothing, and visibility is untouched. **`dry_run` defaults to true.** |
| `revit_set_view_sun` | Move the sun: `azimuth`/`altitude` in **degrees** (Lighting mode), or `date`/`time` (Still Image mode, Revit computes the angles from the project location). The angles in the response are read back. Sun settings can be **shared between views** — `sunSettingsId` and `sharesSettings` say whether moving this one moved others. This only aims the sun; cast shadows are `revit_set_view_graphics`. |
| `revit_get_view_graphics` | What a view is really drawn with: style, detail level, the view template overriding them, the shadow/sunlight intensity sliders, the background, and a **probe** of the cast-shadows and exposure parameters — present, storage type, read-only, template-controlled, and `writable`. `on: null` means the bridge could not read it, never "off". |
| `revit_set_view_graphics` | Style, detail level, `shadow_intensity`/`sunlight_intensity` (0-100) and **cast shadows**, in one undo step. The response's `shadows.method` says which route was taken: `parameter` (written and read back — the only one that reports `verified: true`), `posted-command` (Revit's own `ID_IMAGE_SHADOW_ON`/`OFF` was posted; `pending`, `unverified`), or `unavailable` (nothing was changed). `ambient_light_intensity` does not exist and is refused. |
| `revit_get_view_graphics_command_status` | What became of the last posted shadows command: when it went, whether Revit has idled since, and a fresh probe now beside the one taken at posting. `verified` is true only when the parameter can actually be read back. |
| `revit_capture_view_template` | Turn a view that is drawn the way you want into a view template, and say what it controls: `mode` `graphics` (everything except sun, crop/camera and phase, so each view keeps its own), `shadows` (only that parameter) or `all`. The controlled set is read back off the template. |
| `revit_apply_view_template` | Put one template onto many views in one undo step. `mode` `apply` is a one-time parameter copy; `assign` attaches the template so it keeps controlling them (and needs `replace: true` to displace an existing one). Every target is validated **before** anything is written, so a batch is all or nothing. **`dry_run` defaults to true.** |
| `revit_export_view_image` | Export views to PNG/JPEG on disk. **Revit renames the file**: it appends ` - <view type> - <view name>` to whatever path you give it, so the response reports the real path under `path` and what you asked for under `requestedPath`. Open the former. |
| `revit_create_section_view` | Create a section from an origin, a look direction, and a width/height/depth in feet. `direction` is the way the section LOOKS TOWARD. The response carries the created view's own viewDirection/rightDirection/upDirection precisely so you can assert it: Revit's `viewDirection` is the direction towards the **viewer**, so a section looking toward **D** reports `viewDirection` **-D** — asked `{x: 0, y: 1}`, it reports `{x: 0, y: -1, z: 0}`. |
| `revit_list_legends` | The legend views in the model: `{ id, name, scale }`. An empty list means the document has none — and the Revit API cannot make the first one. |
| `revit_create_legend` | Create a legend by **duplicating** one the document already has, which is the only route the API has. A document with no legend answers `NO_LEGEND_TO_DUPLICATE` rather than handing back a drafting view pretending to be one. |
| `revit_duplicate_view` | Duplicate a view — Duplicate, WithDetailing or AsDependent. Needed to show one view on a second sheet. |
| `revit_set_view_scale` | Set 1:X on one view or a whole batch in one undo step. A schedule, a sheet or a view template comes back under `failed` while the rest are re-scaled, and the scale reported is read back off each view. A **perspective** view is refused with `PERSPECTIVE_VIEW_HAS_NO_SCALE` — a camera has no 1:X — pointing at `revit_scale_perspective_crop`. |
| `revit_scale_perspective_crop` | Make one **perspective** view bigger or smaller on its sheet, proportions locked: Revit's `View3D.ScalePerspectiveCropBox`, which changes the size and the scale of the view on the paper together. `multiplier` 2 doubles it, 0.5 halves it. It is **not** a reframe — the camera is never touched, and `cameraUnchanged` compares the orientation before and after to prove it; changing what is in shot is `revit_set_view_crop`. Refusals come before anything is written: `NOT_A_3D_VIEW`, `VIEW_IS_TEMPLATE`, `VIEW_NOT_PERSPECTIVE`. `before`/`after` carry the view's outline in **paper feet**, the crop box, the camera and the viewport's box on the sheet, all measured after a regeneration. Judge it by `outline`/`viewport`, **not** by `cropBox` — a verified run grew a view 5.65x on the paper with the crop box's model coordinates identical either side, because the composition is kept too. Revit leaves the view **title** at its old paper position, so after a large multiplier put the label back with `revit_set_viewport_position`. **`dry_run` defaults to true**, and the dry run reports no predicted `after` on purpose. |
| `revit_place_views_on_sheets` | Put views on sheets, the whole batch in one call. Handles schedules automatically; a refused placement comes back with a code. **This is what makes sheets stop being empty.** |
| `revit_create_schedule` | Create a schedule for a category with the named fields. Unknown field names are skipped and the response lists the ones that exist. |
| `revit_configure_schedule` | Turn a schedule that lists every instance on its own row into one row per type with a count: `itemized: false` plus `group_by`. Also sorting, grand totals, column headings and widths. Fields are resolved by id or by name and the whole edit is validated before anything is written; it never adds or removes a field. **`dry_run` defaults to true** — compare `bodyRows` before and after, that is the proof it worked. |
| `revit_set_schedule_position` | Move a schedule on a sheet. A schedule is not a viewport: it is anchored by its **top-left** corner, so `revit_set_viewport_position` cannot move it. Takes the instance id from `revit_get_sheet_layout`, refuses a title block revision schedule, and reads the placed bounds back. **`dry_run` defaults to true.** |
| `revit_draw_detail_lines` | Draw detail lines in a drafting view or a plan. An unknown line style falls back to the default and is reported, not fatal. |
| `revit_add_text_notes` | Add text notes to a view. `size` is paper feet; a size no loaded type carries gets a duplicated text type. |
| `revit_diagnostics` | What the bridge suppressed on your behalf: dialogs it answered, warnings it resolved. **Check it after a batch of writes.** |
| `revit_set_auto_dismiss` | Turn dialog auto-dismiss on or off at runtime, and optionally empty the diagnostics buffer. |
| `revit_reload_bridge` | Reload the add-in's endpoint logic from disk without restarting Revit. Maintainer tooling — rebuild first. |
| `revit_install_bridge` | Install the bundled add-in into Revit's Addins folder. Works with Revit closed and the bridge absent. |
| `revit_uninstall_bridge` | Remove the add-in and its manifest. |

Things worth knowing before you read a number wrong:

- **Units are Revit internal units — decimal feet — in both directions,
  unconverted.** Every elevation, height and coordinate, in and out, is a raw
  API value. A level at 3 metres is `9.8425`, not `3`. Nothing in either half
  converts anything, which is what keeps reads and writes symmetric.
- **`revit_query_elements` caps at 500 rows** (default 100). A larger `limit`
  is clamped, not rejected, and the response always carries the **total** match
  count — so when the total exceeds the rows you got, you know the list is a
  page and not the whole answer.
- **`revit_get_elements` never dumps every parameter.** Ask for what you need
  in `params`; without it you get identity fields only.
- **One tool call is one undo step.** Every write is wrapped in a transaction
  group that is assimilated on success and rolled back on failure, so a call
  that fails half way leaves nothing behind. That includes
  `revit_create_sheets`: 29 sheets in one call are one Ctrl+Z, so pass them all
  at once rather than one call per sheet.
- **`revit_create_sheets` skips, it does not fail.** A sheet number that already
  exists comes back under `skipped` with Revit's reason while the rest are
  created, so re-running the same call is safe. It does need a title block: if
  `revit_list_titleblocks` is empty, load a title block family in Revit first.
- **A sheet stays empty until something is on it.** Creating sheets and
  creating views are two different things; `revit_place_views_on_sheets` is the
  one that joins them, and it takes the whole phase in a single call. It also
  knows that a schedule goes on a sheet as a schedule instance rather than a
  viewport, so you never have to branch on the kind of view you are holding.
- **A view lives on exactly one sheet.** Placing one that is already on a sheet
  comes back as `VIEW_ALREADY_PLACED`; `revit_duplicate_view` is how you show
  the same view twice. (Revit itself lets a legend sit on many sheets — the
  bridge does not, because there is one rule for all viewports.)
- **Revit's API cannot author the first legend in a document, and this tool set
  does not pretend otherwise.** There is no legend view type in the API at all;
  `ViewPlan.Create` documents that its type "needs to be a FloorPlan,
  CeilingPlan, AreaPlan, or StructuralPlan ViewType", and `ViewDrafting.Create`
  throws when "viewFamilyTypeId is not a valid ViewFamilyType for a drafting
  view" — so the Legend view family type every template carries has nothing that
  accepts it. The documented way through is to **duplicate** an existing legend,
  which is what `revit_create_legend` does; a document with none answers
  `NO_LEGEND_TO_DUPLICATE` and someone has to make one legend in the Revit UI
  (View tab > Legends > Legend) or start from a template that has one. What can
  then go in it is linework and text: `revit_draw_detail_lines` and
  `revit_add_text_notes` both accept a legend. Legend **components** — the
  elements that show a real family type at scale — have no creation API either,
  and there is no workaround for that one.
- **Sheets do not group themselves in the Project Browser, and the API cannot
  make them.** `BrowserOrganization` is read-only in the Revit API — both in the
  2025 assemblies this add-in compiles against and in the installed 2027 ones:
  `GetCurrentBrowserOrganizationForSheets(document)` hands back an object whose
  `SortingParameterId`, `SortingOrder` and `Type` are all get-only, and the type
  has no setter, no create, and no apply anywhere on it. So there is deliberately
  **no endpoint and no tool for it** rather than one that quietly does nothing.
  Two things that do work: sheets sort by sheet number, so a numbering scheme
  like `L.02.xxx` / `L.03.xxx` already groups the set in reading order; and the
  folder organisation itself is a one-off UI setting — right-click **Sheets** in
  the Project Browser > **Browser Organization**, then group by the parameter
  `revit_create_project_parameter` created and `revit_set_sheet_parameters`
  filled in. Do that once and it is saved in the model.
- **Family content lives outside the project and has to be loaded in.**
  `revit_list_family_symbols` coming back empty does **not** mean the machine has
  no families — it means none have been loaded into *this* project yet. The
  Autodesk library is installed under
  `C:\ProgramData\Autodesk\RVT <year>\Libraries\<language>\`, with
  `Planting\`, `Site\Accessories\`, `Lighting\Architectural\External\`,
  `Furniture\`, `Doors\` and `Windows\` the folders that matter for a house
  and its garden. Point `revit_load_families` at the `.rfa` paths, then place
  the type ids it hands back. **A library one Revit release behind loads fine**:
  an `RVT 2026` family opened by Revit 2027 is upgraded silently on the way in
  — measured, not assumed, and anything Revit did say comes back in
  `warnings`.
- **Only when the library genuinely is not installed** do
  `revit_place_planting` and `revit_create_directshape` become the answer: they
  build solids directly in the project document, with no family behind them, and
  the result is a real element — visible, selectable, categorised, and
  schedulable by `revit_create_schedule`. They are the fallback, not the default;
  a garden modelled out of spheres on cylinders looks like a garden modelled out
  of spheres on cylinders. There are no MEP families either, so
  `revit_create_pipes` and `revit_place_sprinklers` model irrigation the same
  way. Those two pick their category at runtime — `PipeCurves` and `Sprinklers`
  when Revit allows a DirectShape in them, `GenericModel` when it does not — and
  **every created row says which category it actually got**. Read it: a schedule
  of the wrong category is an empty schedule.
- **Geometry built that way schedules as one lump unless you say what it is.**
  `comments` and `mark` on each entry (or once at the top level, as the fallback)
  are written to the element's real Comments and Mark parameters, and a schedule
  can group by either. That is the difference between a planting schedule that
  reads `Planting: 38` and one that breaks down by species.
- **Every element those four build gets a real element type.** Without one,
  "Edit Type" in Revit's Properties palette is dead, the elements cannot be
  scheduled or filtered by type, and they have no type parameters at all.
  `type_name` names it; omitted, it follows the element's own name, so a batch
  that already names its species gets a type per species and a batch that names
  nothing gets one named after the category. Types are **reused** — by name and
  category, within the call and across calls — so planting 140 trees makes one
  type per species, not 140 types. Every created row reports the `typeId` and
  `typeName` it got.
- **Nothing has a material unless you give it one, and the model renders grey
  until you do.** There are two routes and they are not interchangeable:
  - **Geometry carries its material on the solid**, fixed when the solid is
    built. So `material_id` is an argument of `revit_create_directshape`,
    `revit_place_planting`, `revit_create_pipes` and `revit_place_sprinklers`,
    and `revit_assign_material` **cannot** fix one of those afterwards — it says
    so per element rather than pretending.
  - **A wall or a floor takes its material from its type**, in the type's
    compound structure. `revit_create_wall_type` and `revit_create_floor_type`
    author one; `revit_create_walls` (`wall_type`) and `revit_create_floor`
    (`type_name`) then build with it by name. `revit_assign_material` can edit
    an existing element's type instead, which changes **every** element of that
    type — the row says `appliesToType` when it did that.

  `revit_create_material` makes the material itself, and reuses a name that
  already exists rather than overwriting somebody's material — the response says
  `created: false` and reports the colour it really has. Shaded views are set to
  follow the colour rather than a render appearance, which is what makes the
  colour visible without a rendering pass.
- **A colour is still flat paint — textures are a separate step.**
  `revit_set_material_texture` connects a real bitmap to the material's
  appearance asset, and `revit_create_material`'s `texture_path` does it in the
  same call. Revit ships thousands of bitmaps to do it with, under
  `C:\Program Files\Common Files\Autodesk Shared\Materials\Textures\1\Mats\` —
  `grass_color.jpg`, `fieldstone_bump.jpg`, `Finishes.Flooring.Wood.Plank.jpg`,
  `Concrete.Cast-In-Place.Flat.Grey.1.jpg`, `Sitework.Planting.Soil.jpg`,
  `water_calm.png`. The bitmap is **referenced**, not copied into the model, so
  the path has to keep existing; one that does not exist yet is
  `TEXTURE_NOT_FOUND` rather than an asset pointing at nothing.

  `scale` is the real-world size of one tile in feet — `{x: 3, y: 3}` makes a
  paving texture read as 3-foot slabs — and Revit stores it in inches, so it is
  converted for you. The material is given its **own** Generic appearance asset
  unless it already has one nobody else shares, which is what stops texturing
  one material from re-texturing another; the reply says whether that happened
  and why. It also reports `set` (every asset property written), `missing`
  (anything the asset turned out not to carry, reported rather than thrown) and
  `verified` (what the committed asset reads back as).
- **Paving on graded terrain overlaps the surface.** Revit warns
  "Highlighted toposolid and floor overlap" and it is right. Two ways out, and
  they compose: `revit_flatten_toposolid` levels the region under the paving to
  one elevation, and `revit_create_floor`'s `offset` lifts the slab off its
  level. The flatten reports a `residual` — the largest distance any point in
  the region is still off the target — so check that rather than assume.
- **You can see the model, and you should.** `revit_create_3d_view` →
  `revit_set_view_style` (`Realistic`) → `revit_export_view_image` produces a
  PNG on disk. Aim the camera from `modelExtents`, which
  `revit_create_3d_view` returns on every call: create one with no `eye`/`target`
  to learn where the model is, then create the one you actually want. Three
  things about it that will otherwise catch you out:
  - **Revit renames the exported file.** It treats the path you give as a stem
    and appends ` - <view type> - <view name>` to it, so asking for
    `renders\moradia.png` on a view called `Garden Eye` writes
    `renders\moradia - 3D View - Garden Eye.png`. The bridge watches the folder
    and reports what was really written under `path`; `requestedPath` is only
    there to make the difference visible. **Open `path`.**
  - **It is not a render.** Revit's photoreal raytracer is not reachable from
    the API at all: `View3D.GetRenderingSettings` / `SetRenderingSettings`
    configure what the Render dialog *would* do, and there is no method anywhere
    that starts it (`IPhotoRenderContext` is the hook for *third-party*
    renderers to receive geometry, not a way to run Revit's own). What comes out
    is the view exactly as drawn on screen, which is why the display style is
    the setting that matters.
  - **Cast shadows are probed, not assumed.** `View` has no
    `EnableSunlightAndShadows`; `View.ShadowIntensity` / `SunlightIntensity` are
    the Lighting sliders, not the switch; `View.SunAndShadowSettings` is
    read-only and holds the sun *position*. But
    `BuiltInParameter.GRAPHIC_DISPLAY_OPTIONS_SHADOWS` **does** exist, and
    whether it is a writable toggle is a property of the live view — it can be
    missing, storage type `None`, read-only, or controlled by a view template.
    So `revit_get_view_graphics` reports what it found rather than asserting,
    and `revit_set_view_graphics` writes the parameter when the probe proves it
    and otherwise posts Revit's own `ID_IMAGE_SHADOW_ON`/`OFF` command. A posted
    command runs after control returns to Revit and cannot be read back, so that
    response is `pending` and `unverified` — never a success. Passing `shadows`
    to `revit_set_view_style` answers `SHADOWS_HANDLED_ELSEWHERE` pointing at the
    graphics tool.
- **Sheet coordinates are feet on the paper, not model feet.** An A1 sheet is
  1.95 × 1.38. Omit `x`/`y` and the view lands in the middle of the sheet.
- **One project lives at one path and is rebuilt in place.** When a build goes
  wrong, call `revit_new_project` again with the **same** `save_path` and
  `overwrite: true` — it closes that project in Revit, deletes the `.rvt` and
  the `.0001.rvt` backups Revit left beside it, and builds it again there.
  Versioning a project into `House-2.rvt`, `House-final.rvt` and so on is not
  the workflow; it just leaves the user with a folder of junk to delete.
- **`revit_new_project` never overwrites by default.** Without
  `overwrite: true` an existing `save_path` is a `FILE_EXISTS` error, not a
  silent replacement; a missing parent folder is created either way. A file
  something else still has open comes back as `FILE_LOCKED` naming that path —
  the bridge never quietly builds to a different name instead.
- **`revit_close_project` discards unsaved work** unless `save` is true. Call
  `revit_save` first when the work matters.
- **Closing the active document takes a detour, and the response says so.**
  Revit's API refuses to close whatever document is active ("The active
  document may not be closed from the API"), so the bridge activates another
  open document first, or opens a blank scratch project under `%TEMP%` when
  there is no other. The response reports `activePath`, `activeTitle` and
  `activeIsScratch`. A scratch left active is harmless and is reused as the
  stand-in next time; close it in Revit whenever you like.
- **Element ids are only valid for the document that is currently open.**

## Unattended operation

Revit is a desktop application that stops and asks. A modal dialog — a warning,
a "do you want to save", an unexpected prompt — parks Revit's main thread, and
the bridge can only run work when that thread goes idle. With nobody at the
keyboard, one dialog means every call from then on fails with a timeout. The
same is true of the warnings Revit raises when a transaction commits: they open
the warning dialog and the commit waits.

So the bridge answers both itself:

- **Dialogs are auto-dismissed** (on by default). Ordinary dialogs get OK;
  anything whose id or message sounds destructive — delete, remove, overwrite,
  discard, unload, save, close — gets Cancel, on the principle that the safe
  answer to a question nobody read is *no*.
- **Transaction warnings are resolved** by a failure preprocessor installed on
  every transaction the bridge opens: Revit's own resolution where one exists,
  otherwise the warning is deleted. Genuine *errors* are never suppressed — they
  fail the transaction, and the whole call rolls back as usual.

**This is a trade, and `revit_diagnostics` is the other half of it.** A warning
that was resolved silently often means the model did something you did not ask
for — walls joined in a way you did not intend, an element removed as a side
effect, a family placed off-axis. Every dismissed dialog and every resolved
warning goes into a 200-entry ring buffer on the Revit side with its text and
the answer used, and `revit_diagnostics` is the only place you can read it.

Check it after a batch of writes. Empty lists mean the writes went through
cleanly; anything else deserves a look before you move on. `revit_set_auto_dismiss`
with `clear: true` empties the buffer first, so the next read covers only the
batch you care about.

Turn auto-dismiss **off** (`revit_set_auto_dismiss` with `enabled: false`) when
somebody is working in Revit at the same time and should be answering their own
dialogs. With it off, a dialog makes calls fail with `REVIT_BUSY` until it is
dismissed by hand — which is the honest behaviour when a human is present, and
the reason it is a runtime switch rather than a build-time one.

## Environment variables

| Variable | Default | What it does |
| --- | --- | --- |
| `REVIT_MCP_URL` | `http://localhost:48884/revit-mcp` | Base URL of the bridge. Change it for a second Revit instance, or if the add-in listens elsewhere. |
| `REVIT_MCP_TIMEOUT` | `30000` | Per-request timeout in milliseconds. |
| `REVIT_MCP_BRIDGE_TIMEOUT_MS` | `30000` | Read by the **add-in**: how long a request waits for Revit's main thread. Clamped to 1–600 s. |

## Troubleshooting

Four failure modes account for nearly everything, and each has its own message.

**"connection refused" — nothing is listening.** Either Revit is not running,
or it is running without the add-in. In order: start Revit; check that
`%APPDATA%\Autodesk\Revit\Addins\<version>\` contains `RevitMcpBridge.addin`
and a `RevitMcpBridge` folder; if it does not, run `revit_install_bridge` and
restart Revit. If the manifest *is* there and Revit has been restarted, you
were most likely shown the unsigned-add-in dialog and answered *Do Not Load* —
Revit remembers that. Re-run the installer and choose **Always Load**.

**"HTTP 404 ... add-in is not loaded" — something answered, but not the
bridge.** The add-in failed to load even though the manifest is present.
Usual causes: the DLL was blocked by Windows because it came out of a zip
(file properties → **Unblock**), or a stale install from an older version.
Reinstall the bridge and restart Revit. The add-in's own log is at
`%LOCALAPPDATA%\RevitMcpBridge\bridge.log`.

**"did not answer within Nms" — Revit is busy or showing a dialog.** This is
the most common real failure. The bridge can only run work when Revit's main
thread goes idle, and a **modal dialog** — a warning, a save prompt, a file
browser — stops that from ever happening. With auto-dismiss on this should be
rare, so check `revit_diagnostics` first: a dialog the bridge saw but could not
answer is recorded there with `answered: false`. Otherwise switch to Revit,
clear whatever is on screen, retry. If the operation is genuinely long, raise `REVIT_MCP_TIMEOUT`.
Note that a write which timed out may still complete inside Revit: a Revit API
call already under way cannot be cancelled.

**"no active document" — Revit is open but empty.** The request arrived at a
Revit sitting on the start page, or between documents. Open a model and retry.
`revit_status` is the one call that deliberately tolerates this, which is how
you can tell this case apart from the three above.

## Security

What this actually grants, stated plainly, so the decision is an informed one.

- **The add-in runs inside `Revit.exe` with full Revit API access.** It can read,
  modify and delete anything in the open model, and open, save or close
  documents. There is no read-only mode.
- **It opens an HTTP listener inside Revit**, bound to the `127.0.0.1` literal
  rather than a wildcard, on port `48884`. It is not reachable from the network,
  but it is **unauthenticated**: any local process that can reach loopback can
  drive your Revit session. On a shared or untrusted machine, that matters.
- **The add-in is not code-signed**, which is why Revit shows the unsigned
  add-in dialog. Signing costs money and buys you nothing you cannot get by
  reading the source, which is all here.
- **Auto-dismiss answers Revit's dialogs for you.** Destructive-sounding prompts
  get Cancel and ordinary ones get OK, but a prompt nobody read is still a
  prompt nobody read. Turn it off with `revit_set_auto_dismiss` when you are at
  the keyboard, and read `revit_diagnostics` after a batch of writes.
- **Work on a copy of any model you care about**, especially on a first run.
  Revit's undo history is per session and a timed-out write may still have
  landed.

Found something? Open an issue, or report it privately through
[GitHub security advisories](https://github.com/rui-branco/revit-mcp/security/advisories/new).

## Development

```bash
git clone https://github.com/rui-branco/revit-mcp.git
cd revit-mcp
npm install
npm test
```

`npm test` is `node --test` over `tests/`. It touches no Revit and opens no
socket: the HTTP layer is stubbed and the tools are exercised through a real MCP
client over an in-memory transport.

Working on the C# side needs the **.NET SDK** (end users do not):

```bash
npm run build:bridge          # dotnet build -c Release, then refresh revit-bridge/dist/
```

`revit-bridge/dist/` is the compiled add-in that ships on npm. It is committed
to git on purpose — that is what spares end users the SDK. `prepublishOnly`
re-runs `build:bridge` and the tests, so a stale or broken binary cannot be
published.

The add-in is **two** assemblies, and both have to be installed:
`RevitMcpBridge.dll` is the loader Revit pins for the session (HTTP listener,
main-thread pump, dialog handling) and `RevitMcpBridge.Handlers.dll` is the
routing and endpoint logic, which the loader loads into a collectible
`AssemblyLoadContext` from a shadow copy. One `dotnet build` produces both.

That split is what makes **hot reload** possible: rebuild, then call
`revit_reload_bridge`, and the new endpoint logic is live in the running Revit
with the model still open. The response says whether the previous version was
actually unloaded; if `unloadedPrevious` is `false` the new logic is still live,
but the old context is leaking and that is worth a look.

To install a locally compiled bridge instead of the bundled one:

```powershell
cd revit-bridge
.\install.ps1 -Build
```

`revit-bridge/README.md` documents the add-in's internals: the `ExternalEvent`
pump that marshals every request onto Revit's main thread, why the listener
binds the `127.0.0.1` literal rather than a wildcard, the transaction model, and
the full HTTP contract.

## License

MIT — see [LICENSE](LICENSE).
