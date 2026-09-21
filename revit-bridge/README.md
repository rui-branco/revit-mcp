# Revit MCP Bridge

A Revit add-in that runs a small HTTP server **inside Revit's own process** and dispatches requests
to the Revit API. It is the Revit half of `revit-mcp`: the Node MCP server talks to it over
loopback HTTP, and this add-in does the part that can only happen inside Revit.

- Endpoint base: `http://127.0.0.1:48884/revit-mcp/`
- Target: Revit **2025, 2026, 2027** (`net8.0-windows`, x64)
- Runtime dependencies: none beyond Revit itself and the in-box .NET base class library
  (`System.Net.HttpListener`, `System.Text.Json`)

---

## Install

One command, from this directory. No Visual Studio, no admin rights, **no .NET SDK**:

```powershell
.\install.ps1
```

That detects every installed Revit, copies the add-in to
`%APPDATA%\Autodesk\Revit\Addins\<version>\RevitMcpBridge\`, and writes the `.addin` manifest next
to it. Then **restart Revit to load the bridge**.

The add-in comes from `dist\` — the prebuilt DLL the npm package ships, which is why an end user
needs neither the SDK nor a build step. One binary covers 2025, 2026 and 2027 (see
[Build](#build)). Only a source checkout with no `dist\` falls through to `dotnet build`, and if
the SDK is missing there too, the installer says so and names both ways out rather than failing
obscurely.

| Flag | Effect |
| --- | --- |
| `-RevitVersion 2026` | Only that version. Works even if Revit is not in the default Program Files location. |
| `-Build` | Force a fresh `dotnet build -c Release` and install that instead of `dist\`. Needs the SDK; the contributor path. |
| `-SkipBuild` | Never build. Already the default whenever `dist\` is present, so it only matters in a checkout without one. |
| `-Uninstall` | Remove the manifest and the install folder. |
| `-Json` | Emit a single JSON result object as the **only** stdout output. |

Re-running is safe. The install folder is deleted and recreated rather than merged into, so a file
that disappeared from a newer build cannot linger.

### Uninstall

```powershell
.\install.ps1 -Uninstall
```

Uninstall also cleans versions whose Revit has since been removed but whose add-ins folder is still
there, so it can finish the job after Revit is gone.

### Calling the installer from a tool

Every human-readable line is prefixed and greppable:

```
[revit-bridge] INFO      action=install addin=RevitMcpBridge minimumRevit=2025
[revit-bridge] DETECTED  version=2026 hasExe=True path="C:\Program Files\Autodesk\Revit 2026"
[revit-bridge] BUILD     status=skipped reason=prebuilt-dist
[revit-bridge] OUTPUT    dir="...\revit-bridge\dist"
[revit-bridge] INSTALLED version=2026 files=2 dir="..." manifest="..."
[revit-bridge] RESULT    status=ok action=install versions=2026
[revit-bridge] DONE      Restart Revit to load the bridge
```

Levels are `INFO`, `DETECTED`, `BUILD`, `OUTPUT`, `INSTALLED`, `REMOVED`, `RESULT`, `DONE`, `WARN`,
`ERROR`.

For programmatic use prefer `-Json`, which suppresses all of the above and prints one object:

```jsonc
{
  "ok": true,
  "action": "install",
  "exitCode": 0,
  "message": "Restart Revit to load the bridge",
  "addInName": "RevitMcpBridge",
  "addInId": "8f2c7a41-3e6b-4d19-9c05-1b7a52e4d83f",
  "fullClassName": "RevitMcpBridge.BridgeApplication",
  "vendorId": "com.github.revit-mcp",
  "buildStatus": "prebuilt",
  "outputDir": "...",
  "versions": [ { "version": "2026", "installDir": "...", "manifest": "...", "status": "installed" } ],
  "warnings": [],
  "errors": [],
  "log": [ "[revit-bridge] INFO      ..." ]
}
```

Exit codes:

| Code | Meaning |
| --- | --- |
| 0 | Success |
| 1 | Unhandled failure |
| 2 | No Revit installation detected |
| 3 | Requested version unsupported (2024 or earlier), or bad `-RevitVersion` |
| 4 | `dotnet build` failed, or the .NET SDK is missing and there was no `dist\` to fall back on |
| 5 | Nothing to install: no `dist\` and no Release build output |

The installer runs under **Windows PowerShell 5.1** as well as PowerShell 7 — no ternary, no `??`,
no `&&`/`||`, no `-AsHashtable`.

> **Revit 2024 and earlier are refused.** They host add-ins on .NET Framework 4.8 and physically
> cannot load a `net8.0-windows` assembly. The installer warns and exits non-zero rather than
> installing something that would fail inside Revit with a confusing load error.

---

## Build

Only contributors need this; installing does not.

```powershell
dotnet build -c Release
```

From the repo root, `npm run build:bridge` does the same and then refreshes `dist\` from the
output, which is the only supported way to update the binary that ships on npm.

**Revit does not need to be installed to build this.** The Revit API assemblies come from a
community reference package:

```
Revit_All_Main_Versions_API_x64  2025.0.0   (ships lib/net8.0/RevitAPI.dll + RevitAPIUI.dll)
```

Two things about that reference are deliberate and should not be "cleaned up":

1. **It is pinned to 2025, the lowest supported release.** An add-in compiled against 2025 loads
   fine in 2026 and 2027; one compiled against 2027 would break on 2025. Bumping this pin silently
   drops support for older Revit versions.
2. **`ExcludeAssets=runtime` / `Private=false`.** The API assemblies are reference-only and must
   never be copied next to the add-in — Revit supplies them at runtime, and shipping a second copy
   makes every type identity check fail. A post-build MSBuild target (`AssertNoRevitApiInOutput`)
   fails the build if one ever leaks into the output folder, and `install.ps1` refuses to copy them
   as a second line of defence.

The build output is the two assemblies — `RevitMcpBridge.dll` and `RevitMcpBridge.Handlers.dll` —
with a `.pdb` and a `.deps.json` each. See [Building both halves](#building-both-halves) for why one
`dotnet build` produces both, and [Hot reload](#hot-reload) for why there are two at all.

### `dist\` — the binary that ships

`dist\` holds both DLLs and both `.deps.json` files copied out of `bin\Release`, and
it is **committed to git on purpose**: it is what lets the npm package install without a .NET SDK.
The `.pdb` is left behind — it is debug weight nobody installing from npm can use. `bin\` and
`obj\` stay ignored; `dist\` must not be. `prepublishOnly` re-runs `build:bridge` and the test
suite so a stale or broken binary cannot be published.

---

## Endpoints

Everything is **POST** with a JSON body, under `http://127.0.0.1:48884/revit-mcp/`. Reads carry
their filters in the body; there are no GET endpoints and no query strings. An empty body is
treated as `{}`.

| Endpoint | Request | Response |
| --- | --- | --- |
| `status` | `{}` | `{bridgeVersion, url, units, revitVersion, revitVersionName, revitVersionBuild, revitSubVersion, username, activeDocument}` — `activeDocument` is `null` when no document is open, otherwise `{title, pathName, isWorkshared, isFamilyDocument, isReadOnly, isModified}` |
| `levels` | `{}` | `[{id, name, elevation}]` |
| `categories` | `{}` | `[{category, count}]`, busiest first |
| `query` | `{category?, level?, typeName?, limit?, offset?}` | `{total, offset, limit, rows: [compact]}` |
| `elements` | `{ids: [...], params?: ["Comments", ...]}` | `[compact + {found, parameters?}]` |
| `selection` | `{}` | `{count, ids: [...], elements: [compact]}` |
| `titleblocks` | `{}` | `[{id, familyName, typeName}]` — an empty array when no title block family is loaded |
| `sheets` | `{}` | `[{id, number, name}]`, by sheet number |
| `levels/create` | `[{name?, elevation}]` or `{levels: [...]}` | `[{id, name, elevation}]` |
| `walls/create` | `{level, wallType?, typeName?, height, curves: [{start:{x,y}, end:{x,y}}]}` | `[{id, name, typeName, level, height}]` |
| `parameters/set` | `{ids: [...], name, value}` | `{updated, results: [{id, name, value, display}]}` |
| `parameters/create-project` | `{name, category \| categories: [...], type?, group?, instance?}` | `{name, guid, categories, instance, created, alreadyExisted}` |
| `sheets/set-parameter` | `{values: [{sheetId, name, value}]}` | `{updated: [{sheetId, number, name, value, display}], failed: [{sheetId, name, code, reason}]}` |
| `elements/delete` | `{ids: [...]}` | `{requested, deleted, deletedIds: [...]}` |
| `sheets/create` | `{titleBlockId?, sheets: [{number, name}]}` | `{created: [{id, number, name}], skipped: [{number, reason}]}` |
| `toposolid/create` | `{points: [{x, y, z}], typeName?, level?}` | `{id, type, typeName, level, points}` — `type` is `"Toposolid"` or `"TopographySurface"` |
| `toposolid/flatten` | `{points: [{x, y}], elevation, toposolidId?}` | `{id, elevation, added, creases, flattened, offsetMode, residual}` |
| `floors/create` | `{level, typeName?, boundary: [{x, y}], structural?, offset?}` | `{id, level, typeName, structural, offset, area}` |
| `families/load` | `{paths: [string]}` or `{path: string}` | `{families: [{path, familyName, loaded, alreadyLoaded, symbols: [{id, typeName}], code?, reason?}], warnings: [...]}` |
| `families/symbols` | `{category?, familyName?}` | `[{id, familyName, typeName, category}]` — an empty array when nothing has been loaded into the project |
| `families/place` | `{symbolId, level?, z?, points: [{x, y, z?}], rotation?}` | `{symbolId, familyName, typeName, level, levelElevation, placed: [{id, x, y, z, placedZ}], failed: [{x, y, reason}]}` |
| `openings/place` | `{symbolId, points: [{x, y, z?}], hostWallId?, level?, sillHeight?}` | `{symbolId, familyName, typeName, category, level, levelElevation, placed: [{id, x, y, z, hostWallId, hostWallType, sillHeight, sillHeightOn?, placedZ}], failed: [{x, y, z, code, reason}]}` — `code` is `NO_HOST_WALL` or `REVIT_API_ERROR` |
| `views` | `{}` | `[{id, name, viewType, isTemplate, isPlacedOnSheet, viewDirection?, rightDirection?, upDirection?}]` — non-template views, sheets excluded |
| `views/create-plan` | `{level, name, viewFamilyType?, scale?}` | `{id, name, viewType, viewFamilyType, level, scale}` |
| `views/create-drafting` | `{name, scale?}` | `{id, name, viewType, scale}` |
| `views/create-section` | `{name, origin: {x, y, z}, direction: {x, y}, width, height, depth, scale?}` | `{id, name, viewType, scale, viewDirection, rightDirection, upDirection}` |
| `views/create-3d` | `{name, eye?: {x, y, z}, target?: {x, y, z}, perspective?, scale?}` | `{id, name, viewType, isPerspective, scale, viewDirection, rightDirection, upDirection, modelExtents: {min, max, center} \| null}` |
| `views/set-style` | `{viewId, style, detailLevel?}` | `{viewId, name, viewType, style, detailLevel}` — `shadows` is redirected with `SHADOWS_HANDLED_ELSEWHERE` to `views/set-graphics` |
| `views/set-background` | `{viewId, kind, skyColor?, horizonColor?, groundColor?, imagePath?}` | `{viewId, name, viewType, kind, skyColor?, horizonColor?, groundColor?, imagePath?, imageFlags?}` — 3D views only; `kind: "sky"` reads back as `SunAndClouds` and takes no colours |
| `views/hide-categories` | `{viewId, categories: [string], hidden?}` | `{viewId, name, viewType, categories: [{category, hidden, skipped?, reason?}]}` — `"annotation"` expands to the whole datum/annotation set |
| `views/set-sun` | `{viewId, azimuth?, altitude?}` in degrees, or `{viewId, date?, time?}` | `{viewId, name, viewType, sunSettingsId, sharesSettings, sunAndShadowType, relativeToView, azimuth, altitude, dateAndTime}` — angles read back, not echoed |
| `views/export-image` | `{viewId \| viewIds, path?, width?, height?, format?}` | single: `{viewId, viewName, path, requestedPath, bytes, width, height}`; batch: `{exported: [...]}` |
| `views/legends` | `{}` | `[{id, name, scale}]` — the legend views the document already has, by name |
| `views/create-legend` | `{name, fromLegendId?, scale?}` | `{id, name, viewType, sourceViewId, scale}`, or `NO_LEGEND_TO_DUPLICATE` when the document has no legend |
| `views/duplicate` | `{viewId, name, detailing?}` | `{id, name, viewType, sourceViewId, detailing}` |
| `views/set-scale` | `{viewId, scale}` or `{viewIds: [...], scale}` | `{updated: [{id, name, viewType, scale}], failed: [{viewId, code, reason}]}` — a perspective view fails with `PERSPECTIVE_VIEW_HAS_NO_SCALE` pointing at `views/scale-perspective-crop` |
| `views/scale-perspective-crop` | `{viewId, multiplier, dryRun?}` | `{dryRun, applied, viewId, name, viewType, multiplier, before, after, cameraUnchanged, note}` — `View3D.ScalePerspectiveCropBox`; `dryRun` defaults to **true** and reports no predicted `after`; `before`/`after` are `{isPerspective, scale, outline, cropBoxActive, cropBox, camera, viewport}` measured after a regeneration, `outline` and `viewport` in PAPER feet; refusals are `NOT_A_3D_VIEW`, `VIEW_IS_TEMPLATE`, `VIEW_NOT_PERSPECTIVE` |
| `sheets/place-view` | `{sheetId, viewId, x?, y?}` or `{placements: [{sheetId, viewId, x?, y?}]}` | single: `{viewportId, sheetId, viewId, kind, x, y}`; batch: `{placed: [...], failed: [{sheetId, viewId, code, reason}]}` |
| `schedules/create` | `{category, name, fields: [string], scale?}` | `{id, name, category, fields, skippedFields, availableFields?}` |
| `views/graphics` | `{viewId}` or `{viewIds: [...]}` | `{views: [{id, name, viewType, style, detailLevel, templateId, templateName, shadowIntensity, sunlightIntensity, ambientLightIntensity, background, shadows: <probe>, exposure: <probe>}], notes}` — a probe is `{parameter, parameterId, available, storageType, readOnly, value, display, on, templateId, templateName, controlledByTemplate, writable, writableReason}` and `on: null` means UNKNOWN |
| `views/set-graphics` | `{viewId \| viewIds, style?, detailLevel?, shadowIntensity?, sunlightIntensity?, shadows?}` | `{views: [...], notes, shadows?}` — `shadows.method` is `parameter` (written and read back, `verified`), `posted-command` (`posted`, `pending`, `unverified`), `unavailable` or `blocked`; `ambientLightIntensity` is refused `409 AMBIENT_LIGHT_NOT_EXPOSED` |
| `views/graphics-command-status` | `{}` | `{posted, command, requested, viewId, postedAtUtc, idleSeenAtUtc, pending, probeAtPost, probeAtIdle, probeNow, verified, verifiedBy}` |
| `views/capture-template` | `{sourceViewId, name, mode?: "graphics" \| "shadows" \| "all", parameterIds?: [...]}` | `{templateId, name, viewType, sourceViewId, sourceViewName, mode, controlled: [{id, name}], notControlled, excluded: [{id, name, reason}], ignoredParameterIds, shadows}` |
| `views/apply-template` | `{templateId, viewIds, mode?: "apply" \| "assign", dryRun?, replace?}` | `{dryRun, applied, templateId, templateName, mode, plan: [...], views?, shadows, note?}` — `dryRun` defaults to **true**; the batch is validated before any write |
| `detail/lines` | `{viewId, lines: [{start: {x, y}, end: {x, y}}], lineStyle?}` | `{viewId, elevation, lineStyle, created: [{id, start, end}], failed: [{index, reason}], requestedLineStyle?, availableLineStyles?}` |
| `detail/text` | `{viewId, notes: [{x, y, text, size?}]}` | `{viewId, elevation, created: [{id, x, y, typeName}], failed: [{index, reason}]}` |
| `directshape/create` | `{category, name?, typeName?, materialId?, materialName?, comments?, mark?, shapes: [primitive \| group]}` | `{created: [{id, category, name, typeId, typeName, materialId?, comments?, mark?}], failed: [{index, reason}]}` |
| `planting/place` | `{points: [{x, y, z?, name?, comments?, mark?}], trunkHeight?, trunkRadius?, crownRadius?, name?, typeName?, materialId?, materialName?, comments?, mark?}` | `{created: [{id, category, name, typeId, typeName, materialId?, x, y, comments?, mark?}], failed: [{index, reason}]}` |
| `pipes/create` | `{category?, name?, typeName?, materialId?, materialName?, comments?, mark?, runs: [{points: [{x, y, z?}], radius?, name?, comments?, mark?}]}` | `{created: [{id, category, name, typeId, typeName, materialId?, comments?, mark?}], failed: [{index, reason}]}` |
| `sprinklers/place` | `{points: [{x, y, z?, name?, comments?, mark?}], radius?, height?, name?, typeName?, materialId?, materialName?, comments?, mark?}` | `{created: [{id, category, name, typeId, typeName, materialId?, x, y, comments?, mark?}], failed: [{index, reason}]}` |
| `materials` | `{}` | `[{id, name, colorRgb, appearanceAssetId}]`, by name — `colorRgb` is `{r, g, b}` or `null` |
| `materials/create` | `{name, color: {r, g, b}, transparency?, shininess?, surfaceForegroundPatternId?, appearanceAssetId?, texturePath?}` | `{id, name, created, colorRgb, transparency, shininess, appearanceAssetId, texture?}` |
| `materials/set-texture` | `{materialId \| materialName, texturePath, scale?: {x, y}, rotation?, tint?: {r, g, b}}` | `{materialId, materialName, assetAuthored, assetReason, appearanceAssetId, appearanceAssetName, assetSchema, colorRgb, texturePath, diffuseProperty, textureApplied, set: {property: value}, missing: [property], verified: {connectedAsset, bitmap, scaleFeet: {x, y}, rotation}}` |
| `materials/assign` | `{materialId \| materialName, elementIds: [...]}` | `{materialId, materialName, assigned: [{id, route, appliesToType, typeId?, typeName?, layers?, parameter?}], skipped: [{id, reason}]}` |
| `walltypes/create` | `{name, basedOnTypeName?, thickness?, materialId?, materialName?}` | `{id, name, created, basedOn, thickness, materialId, materialName, layers}` |
| `floortypes/create` | `{name, basedOnTypeName?, thickness?, materialId?, materialName?}` | `{id, name, created, basedOn, thickness, materialId, materialName, layers}` |
| `document/new` | `{templatePath, savePath, overwrite?}` | `{path, title, templatePath}` |
| `document/open` | `{path}` | `{path, title}` — works with no active document |
| `document/save` | `{}` | `{path, saved}` |
| `document/save-as` | `{savePath, overwrite?}` | `{path}` |
| `document/close` | `{save?}` | `{closed, path?, title?, activePath?, activeTitle?, activeIsScratch?}` — `{closed: false}` when nothing is open |
| `diagnostics` | `{}` | `{autoDismiss, capacity, dropped, dialogs: [{at, dialogId, message, result, answered}], failures: [{at, transaction, severity, message, action}]}` |
| `diagnostics/config` | `{autoDismiss?, clear?}` | `{autoDismiss, cleared}` |
| `reload` | `{}` | `{reloaded, version, loadedAt, loadedFrom, collectible, unloadedPrevious, warning?}` |

The **compact** row shape, shared by `query` / `elements` / `selection`:

```json
{ "id": 123456, "name": "Basic Wall", "category": "Walls", "typeName": "Generic - 200mm", "level": "Level 1" }
```

Notes that matter:

- **Element ids are JSON integers** taken from `ElementId.Value` (Int64), never the deprecated
  `ElementId.IntegerValue`.
- **`query` caps `limit` at 500** (default 100). A larger value is clamped, not rejected.
- **`elements` returns only the parameters you name** in `params`. It never dumps every parameter
  an element has. Omit `params` and you get identity fields only. A named parameter that does not
  exist comes back as `null`; a missing element id comes back as `{"id": N, "found": false}` rather
  than failing the whole batch.
- **`parameters/set` writes instance parameters only.** Writing to the type would silently change
  every other instance of that type, which is never what a caller naming specific ids meant.
- **`elements/delete` can report more deleted than requested** — Revit also removes dependent
  elements.
- **`sheets/create` skips instead of failing.** A `number` Revit refuses — practically always one
  that already exists — lands in `skipped` with Revit's own reason, and the rest of the batch still
  gets created, so re-running the same call after a partial run is safe. `titleBlockId` is optional
  and defaults to the first loaded title block; a document with **none** loaded is a `NO_TITLEBLOCK`
  failure rather than a pile of sheets with no title block. An inactive `FamilySymbol` is activated
  first, because `ViewSheet.Create` throws on one that has never been used.
- **`toposolid/create` prefers Toposolid and says when it did not.** `Autodesk.Revit.DB.Toposolid`
  is Revit 2024+ and is present in the 2025 reference assemblies this add-in compiles against, so
  the Toposolid path is the one that is taken whenever the document has a `ToposolidType` — which
  every 2024+ template does. The legacy `TopographySurface` is used **only** when the document has
  no toposolid type at all, and the response says which element kind you got in `type`. `level` is
  optional because `Toposolid.Create` needs one and the caller usually does not care: omitted, it is
  the lowest level in the document. Point `z` is the surface elevation and is never flattened.
- **`floors/create` closes the boundary for you** when the last point is not the first, and takes
  every point at the level's elevation so the floor lands on the level rather than at an accidental
  offset. `area` is `HOST_AREA_COMPUTED` read after a `Regenerate()`, so it is the real computed
  area and not zero. `offset` is the floor's height offset from that level in feet, written to
  `FLOOR_HEIGHTABOVELEVEL_PARAM` - the enum name behind Revit's "Height Offset From Level"; there is
  no `FLOOR_HEIGHTOFFSET_PARAM` in the API - and defaulting to 0, which is exactly what the endpoint
  did before it took one. The response reports the value **read back off the parameter**, not the
  one that was asked for.
- **`toposolid/flatten` is the other half of putting paving on graded terrain.** A floor laid over a
  sloped toposolid gives Revit's "Highlighted toposolid and floor overlap" warning, and there are
  only two honest answers: level the surface under the paving, or lift the paving off it with
  `floors/create`'s `offset`. This endpoint does the first. It adds the ring to the toposolid's
  shape at `elevation` (`SlabShapeEditor.AddPoints`), creases the ring
  (`SlabShapeEditor.AddSplitLine` - `DrawSplitLine` is deprecated in 2025) so the flat region ends
  at its boundary instead of sloping on into the terrain, and moves every existing shape vertex
  inside the ring to the same elevation. Only a Revit 2024+ `Toposolid` can be flattened; a legacy
  `TopographySurface` answers `NO_TOPOSOLID`. `toposolidId` is only needed when the document has
  more than one.

  `SlabShapeEditor.ModifySubElement` takes "the new value of the vertex offset" and the API
  documents **no datum for it**. Rather than guess, the endpoint measures: one vertex is offset by
  1 ft, then by 2 ft, and where it lands answers the question outright - a relative offset stacks
  (the second lands 2 ft above the first), an absolute one does not (it lands 1 ft above), and the
  datum falls out of the first measurement. `offsetMode` says which it found. `residual` is the
  largest distance any vertex in the region is **still** off the target, measured after the fact: a
  caller asserts that rather than trusting any of this. A crease Revit refuses is counted in
  `creases`, not thrown - the region is still flattened, it just meets the terrain across triangles
  instead of along an edge.

  It is worth saying plainly: this is the one endpoint here whose Revit-side behaviour is not
  pinned down by the API documentation, which is exactly why it measures itself. **Read `residual`
  before believing the region is flat.** If it comes back large, the working fallback is the one
  that needs no sub-element editing at all: author the terrain flat under the paved areas
  (`toposolid/create` with the paved region's points already at the paving level) and use
  `floors/create`'s `offset` for the rest.
- **`families/load` is what makes any of the family endpoints useful.** Autodesk's library is an
  optional download that lives outside the project, under
  `C:\ProgramData\Autodesk\RVT <year>\Libraries\<language>\`; until an `.rfa` has been loaded
  into *this* document it does not exist to `families/symbols` or `families/place`.

  It calls **`Document.LoadFamily(string, IFamilyLoadOptions, out Family)`** — the three-argument
  overload, never the bare `LoadFamily(path)`. That is the whole reason a family already in the
  project does not throw: without an `IFamilyLoadOptions` there is nowhere for Revit to ask its
  "this family already exists" question and it raises instead. What the bridge answers:

  | callback | answer | why |
  | --- | --- | --- |
  | `OnFamilyFound` | `true`, `overwriteParameterValues = false` | Reload the definition — that is what the caller asked for — but keep the parameter values **this project** has set on the types. A reload that silently reverts a tuned type to library defaults is worse than one that leaves it. Same as Revit's own "Overwrite the existing version" button. |
  | `OnSharedFamilyFound` | `true`, `source = FamilySource.Project`, `overwriteParameterValues = false` | A nested shared family the project already has stays as the project has it; taking the incoming file's copy would change instances that are already placed and that nobody in the request mentioned. |

  **A library one release behind loads.** Measured, not assumed: `RVT 2026` families
  (`M_RPC Tree - Deciduous`, `M_RPC Shrub`, `M_Park Bench`, `M_Bollard Light`) loaded into Revit
  2027 upgrade on the way in and produce **no** dialog and **no** transaction warning —
  `warnings` came back `[]` and the bridge log recorded nothing. `warnings` carries whatever
  `BridgeDiagnostics` did record during the call, so an upgrade that *does* warn is reported rather
  than left for a caller who would have to know to go and look at `diagnostics`.

  **Read `loaded` and `alreadyLoaded` together.** `alreadyLoaded` is whether the project had the
  family before the call; `loaded` is whether Revit actually wrote it in. `loaded: false` with
  `alreadyLoaded: true` is **not** a failure — Revit found its copy identical to the file and did
  nothing — and that row's `familyName` and `symbols` are still the ones to place with. One
  `TransactionGroup` for the batch, one transaction per file, and a per-file catch: a missing path
  is `FILE_NOT_FOUND` on its own row, an `.rfa` Revit refuses is `LOAD_FAILED` on its own row, and
  every other file in the request still loads.
- **`families/place` catches per point, and does something with `z` you have to know about.** The
  symbol is activated first if it has never been used (its own inner transaction, like
  `sheets/create`), then one inner transaction per point inside the one group: a point Revit refuses
  lands in `failed` and the other 199 still assimilate into a single undo entry. `rotation` is
  **radians** about the vertical axis — radians is Revit's internal angle unit exactly as feet is
  its internal length unit.

  **Which overload, and the elevation trap in it.** Point-based placement uses
  `Document.Create.NewFamilyInstance(XYZ, FamilySymbol, Level, StructuralType)`. Planting and site
  families are `OneLevelBased`, **not** `OneLevelBasedHosted` — a tree is not hosted by the terrain
  it stands on, it sits at an elevation — so the overloads taking an `Element host` or a `Face` are
  the wrong ones for them.

  That overload reads `location.Z` as **the offset from the level, not as a model elevation.**
  Measured against Revit 2027 with a real `M_RPC Tree - Deciduous`:

  | asked | level elevation | `LocationPoint.Z` Revit gave |
  | --- | --- | --- |
  | `z: 0` on `Cota do jardim` | `-1.476378` | `-1.476378` |
  | `z: 0` on `Piso 1` | `9.84252` | `9.84252` |
  | `z: 5` on `Cota do jardim` | `-1.476378` | `3.523622` |

  So a caller passing the absolute elevations every other endpoint here takes would have had every
  instance pushed up or down by the level elevation, in silence. **This endpoint therefore takes
  `z` as an absolute model elevation like the rest of the bridge and subtracts the level elevation
  before handing the point to Revit**, and reports `placedZ` read back off each instance so the
  result is never a matter of trust. `level` is optional and defaults to the lowest level in the
  document; the top-level `z` is an extra offset added to every point.

  One caveat on `placedZ`: it is `LocationPoint.Point.Z`, which for some families is not the base of
  the object. `M_Bollard Light` placed at `-1.476` reports `placedZ` `1.024` while its
  `Elevation from Level` is `0` and it stands correctly on the ground — that family's location
  point is simply defined above its base.
- **`views/create-*` and `views/duplicate` never fail on a name.** View names are unique
  document-wide; a taken one gets `" 2"`, `" 3"`, ... appended and the response says what the view
  is actually called. Losing a batch of plans to one collision is worse than a suffix.
- **`views/create-section` geometry.** `origin` is the model point the section is centred on and the
  cut plane passes through it. **`direction` is the horizontal direction the section LOOKS TOWARD** -
  `{x: 0, y: 1}` is a section looking north. It is what the view faces, not where the viewer stands;
  its `z` is ignored and it is normalised. `width` is the extent across the view along the section
  line, `height` the vertical extent (up is always +Z), both centred on `origin`. `depth` is how far
  in front of `origin` the view sees, and is where the far clip lands.

  **The transform is settled by measurement against a live Revit 2027, not by reading
  `RevitAPI.xml`.** Three sections were created with `BasisZ = -direction`, `BasisX = direction × Z`,
  and the created views' own frames read back off the views:

  | asked `direction` | `viewDirection` | `rightDirection` | `upDirection` |
  | --- | --- | --- | --- |
  | `{x: 0, y: 1}` | `{0, 1, 0}` | `{-1, 0, 0}` | `{0, 0, 1}` |
  | `{x: 0, y: -1}` | `{0, -1, 0}` | `{1, 0, 0}` | `{0, 0, 1}` |
  | `{x: 1, y: 0}` | `{1, 0, 0}` | `{0, 1, 0}` | `{0, 0, 1}` |

  So Revit hands back `viewDirection = -BasisZ` and `rightDirection = -BasisX`: it turns the frame
  it is given by 180° about up. `View.ViewDirection` is "the direction towards the viewer", so a
  `viewDirection` **equal** to the asked direction means those sections looked the opposite way -
  backwards from what this endpoint promises. The right directions say it independently: asked
  `{0, 1}`, Revit reported right `{-1, 0, 0}`, west, which is the right hand of a viewer facing
  **south**. That was a real bug, and it is fixed by negating the direction that goes into the
  transform - the parameter still means what it always said it meant.

  Measured rule, and the one this endpoint is built on: **the view looks along the box's `BasisZ`
  and reports `viewDirection = -BasisZ`.** To look toward `direction`, hand it `BasisZ = direction`.
  `BoundingBoxXYZ.Transform` remarks that "the transform must always be right-handed and
  orthonormal", which then fixes `BasisX = BasisY × BasisZ = Z × direction`; Revit reports
  `rightDirection = -BasisX = direction × Z`, east for a section looking north, which is what is on
  the right of a viewer facing north. `Min`/`Max` are `(-width/2, -height/2, -depth)` /
  `(width/2, height/2, 0)` in that frame, so the far clip distance is `depth` and the near plane is
  the cut plane through `origin`; being in the box frame, the depth region turns with `BasisZ` and
  stays in front of the way the view looks.

  Do not re-derive any of this from `RevitAPI.xml`. `ViewSection.CreateSection` remarks that "the
  view direction of the resulting section will be `sectionBox.Transform.BasisZ`", but its "view
  direction" is the way the view **looks**, the opposite of the `View.ViewDirection` property of the
  same name; its other remark, that `(right, up, view direction)` is "left handed", reads either way
  depending on which of the two is meant. Reading those remarks as `View.ViewDirection` is exactly
  what put the sign the wrong way round in the first place. The measurements are the authority.

  **The created view's own frame is reported precisely so this is assertable.** `views/create-section`
  answers with the view's `viewDirection`, `rightDirection` and `upDirection` as `{x, y, z}`, and so
  does every framed row of `views`: **a caller asserts what Revit did** instead of opening Revit and
  looking at it. For a section looking toward **D**, Revit reports `viewDirection` **-D** - a section
  asked to look at `{x: 0, y: 1}` reports `viewDirection` `{x: 0, y: -1, z: 0}`, and that is the
  assertion, not a bug. A view with no frame at all - a schedule, a legend - simply does not
  carry the three fields.
- **`views/create-3d` aims the camera for you, and tells you where the model is.**
  `ViewOrientation3D` takes `(eyePosition, upDirection, forwardDirection)` and Revit requires `up`
  perpendicular to `forward`, so neither can be passed through raw. From an `eye` and a `target` the
  bridge builds:

  ```
  forward = normalize(target - eye)     the direction the camera looks
  right   = normalize(forward x Z)      horizontal; +X when the camera looks north
  up      = right x forward             world up, tilted with the camera
  ```

  A camera looking straight down or straight up has no horizontal right vector (`forward x Z` is
  zero), and that one case falls back to `right = +X`, which puts north at the top of the image the
  way a plan does. Verified live: `eye {93, 6, 7} -> target {52, 60, 8}` came back with
  `viewDirection {-0.055, -0.997, -0.055}` (Revit's view direction is `-forward`),
  `rightDirection {0.998, -0.055, 0}` and `upDirection {-0.003, -0.055, 0.998}`; straight down gave
  `viewDirection {0, 0, 1}`, `up {0, 1, 0}`, `right {1, 0, 0}`.

  Every response also carries **`modelExtents`** - `{min, max, center}` over everything modelled -
  because a camera cannot be aimed without knowing where the model is and no other endpoint says.
  Create a view with no `eye`/`target`, read the extents, create the one you want. Model categories
  only, and two exclusions that were **measured rather than assumed**: a `ViewSheet` reports a Model
  category with a bounding box at `z = -1000`, and a camera (the glyph standing for a 3D view) sits
  above everything built. Levels and grids drop out with the rest of the annotation categories. On
  `moradia.rvt` that is the difference between `{-328, -328, -1000} .. {328, 328, 87}` and the real
  `{-1.39, -2.22, -4.6} .. {100.75, 83.95, 29}`.

  A perspective view has no view scale - Revit reports `0` and refuses the setter - so `scale` is
  applied only to an isometric.
- **`views/set-style` sets the display style and the detail level; shadows moved out.**
  `View.DisplayStyle` and `View.DetailLevel` are plain settable properties and both are read back
  off the view after the write, because a view template owns them on the views that use one.
  `"HiddenLine"` is accepted for `DisplayStyle.HLR`, which is named after hidden line removal and is
  not what anybody outside the API calls it.

  `shadows` is answered `409 SHADOWS_HANDLED_ELSEWHERE` pointing at `views/set-graphics`. This
  endpoint used to answer `SHADOWS_NOT_EXPOSED` and say cast shadows were impossible; that claim was
  too broad and has been withdrawn. What remains true is that `View` has no
  `EnableSunlightAndShadows`, `View.ShadowIntensity` / `View.SunlightIntensity` are the Lighting
  sliders rather than the switch, and `View.SunAndShadowSettings` is read-only and holds the sun
  *position*. What was wrong is the rest: `BuiltInParameter.GRAPHIC_DISPLAY_OPTIONS_SHADOWS` exists,
  and whether it is a usable toggle is a property of the live view. See `GraphicsEndpoints`.
- **`views/graphics` and `views/set-graphics` probe cast shadows rather than declaring them.**
  The enum member existing proves nothing - Autodesk's own REVIT-222419 is the record of an enum
  member that is not a usable toggle - so `get_Parameter(GRAPHIC_DISPLAY_OPTIONS_SHADOWS)` is asked
  on the live view and everything it answers is reported: `available`, `storageType`, `readOnly`,
  `value`, `on`, whether a view template controls it, and a `writable` verdict with the reason
  behind it. `on: null` means UNKNOWN, never off.

  `views/set-graphics` then takes one of two routes and says which:

  - **`method: "parameter"`** when the probe proves all four conditions - present, `Integer`
    storage, not read-only, not template-controlled. It is written inside the same transaction group
    as `style`/`detailLevel`/the intensities and read back afterwards, so this is the only route that
    can report `verified: true`.
  - **`method: "posted-command"`** otherwise. `RevitCommandId.LookupCommandId("ID_IMAGE_SHADOW_ON"
    / "ID_IMAGE_SHADOW_OFF")` - both strings are present in Revit 2027's `UIFrameworkRes.dll`,
    `DesktopMFC.dll` and `Utility.dll` - is gated on `CanPostCommand` and posted with
    `UIApplication.PostCommand`. There is no `PostableCommand` member for shadows, so a `false` from
    `CanPostCommand` is reported as `method: "unavailable"` rather than worked around. A posted
    command acts on the **active** view, so the target view is made active first with
    `UIDocument.ActiveView` - which Revit only allows while the document is not modifiable, hence
    outside the transaction group, after it has closed. It runs when control returns to Revit, so
    the response is `posted: true, pending: true, unverified: true, verified: false` and never says
    success. Revit allows one posted command at a time, so a second while one is outstanding is
    `method: "blocked"`, and a request that needs this route must name a single view.

  `views/graphics-command-status` reports what became of it, including a fresh live probe. When the
  parameter cannot be read back, `verified` stays `false` with `verifiedBy: null`.

  The Idling handler that timestamps the first idle after a post is **one-shot and unsubscribes
  itself first thing**. That is not tidiness: these handlers live in a collectible
  `AssemblyLoadContext` and a delegate left on Revit's `Idling` event would pin the context and
  break `/revit-mcp/reload`.

  `ambientLightIntensity` is refused `409 AMBIENT_LIGHT_NOT_EXPOSED` rather than accepted and
  ignored: checked against both the 2025 reference assembly and the installed 2027 `RevitAPI.dll`,
  `View` has `ShadowIntensity` and `SunlightIntensity` and no ambient equivalent, and there is no
  ambient `BuiltInParameter` on a view either.
- **`views/capture-template` states what a template controls; `views/apply-template` validates the
  whole batch first.** `View.CreateViewTemplate()` copies the source view and
  `SetNonControlledTemplateParameterIds` is what turns the copy into a template with a deliberate
  scope. `mode: "graphics"` controls everything the template can except `VIEW_GRAPH_SUN*` /
  `VIEW_SOLARSTUDY*` (so each view keeps its own sun), `VIEWER_*` (crop, camera, extents) and
  `VIEW_PHASE` / `VIEW_PHASE_FILTER`; every exclusion comes back with its reason and the controlled
  set is read back off the template rather than echoed.

  `views/apply-template` checks each target is a view, is not a template or a sheet, and passes
  `View.IsValidViewTemplate` **before** writing anything - one failure fails the request with all
  the reasons rather than leaving half the batch changed. `mode: "apply"` is
  `ApplyViewTemplateParameters`, a one-time copy; `mode: "assign"` sets `ViewTemplateId`, a lasting
  association, and will not detach a template a view already has without `replace: true`. `dryRun`
  defaults to **true**.

  Neither endpoint pretends a template can conjure state the source did not have: capturing from a
  view whose cast shadows are off produces a template that turns shadows on nowhere, and both
  responses carry the probes that make that visible.
- **`views/export-image` reports the file Revit wrote, not the one you asked for.** `ExportImage`
  treats `FilePath` as a **stem** and appends ` - <view type> - <view name>` to it: asking for
  `renders\moradia.png` on a view called `MCP Garden Eye` writes
  `renders\moradia - 3D View - MCP Garden Eye.png`. So the directory is listed before and after the
  export and the file that appeared - or, on a re-export, the file whose timestamp moved - is what
  comes back in `path`; `requestedPath` is only there to make the difference visible. A batch is run
  as **one single-view export per view** for exactly this reason: with one view in flight there is
  exactly one file to attribute.

  `ImageExportOptions.GetFileName` is deliberately **not** used as the stem. It returns the suffix
  Revit is about to append (`" - 3D View - <name>"`, with an empty base), so using it writes the
  suffix twice - `renders\ - 3D View - Iso - 3D View - Iso.png`, which is what it actually did
  before this was fixed. The default stem is the document title, the way Revit's own export dialog
  names a file, and the default folder is `<user profile>\RevitProjects
enders\`.

  Size is Revit's model, not a width and a height: one `PixelSize` along one `FitDirection`, with
  the other dimension following the view. `width` fits horizontally (1600 px by default), `height`
  fits vertically, passing both fits the width - and the `width`/`height` in the response are read
  out of the written PNG's IHDR chunk rather than echoed. `HLRandWFViewsFileType` and
  `ShadowViewsFileType` are both set: Revit picks whichever matches how the view is drawn, and a
  shaded view exported with only the first set comes out in the wrong format. Nothing is transacted
  - exporting is a read, and Revit refuses it inside a transaction.

  **This is not a render.** The Revit API cannot start the photoreal raytracer:
  `View3D.GetRenderingSettings` / `SetRenderingSettings` configure what the Render dialog *would*
  do, `Document` has `ExportImage` and `SaveToProjectAsImage` and no render method at all, and
  `IPhotoRenderContext` is the hook for a *third-party* renderer to receive geometry through
  `CustomExporter`, not a way to run Revit's own. What comes out is the view exactly as drawn on
  screen, which is why `views/set-style` is the setting that matters - a `Realistic` export shows
  materials and RPC content properly, a `HiddenLine` one does not. Same view on `moradia.rvt`:
  44 KB hidden-line, 457 KB realistic.
- **`views/create-drafting` is a view of nothing**, which is the point: a drafting view carries
  linework and annotation and no model geometry at all. It is what a detail sheet is made of - fill
  it with `detail/lines` and `detail/text`, then put it on a sheet like any other view.
- **`views/create-plan` takes a view family type by NAME.** `viewFamilyType` is the name Revit shows
  for a `ViewFamilyType` in this document - `"Site"`, `"Ceiling Plan"`, `"Structural Plan"` - not a
  `ViewFamily` enum name, because a Site plan is an ordinary FloorPlan-family type and only its name
  tells it apart from `"Floor Plan"`. Matching on the family would make a Site plan unreachable.
  Omitted, it is the first FloorPlan type, exactly as before. Only the four families `ViewPlan.Create`
  documents are accepted - "the type needs to be a FloorPlan, CeilingPlan, AreaPlan, or StructuralPlan
  ViewType" - and anything else is a `BAD_REQUEST` carrying the plan type names the document does
  have, rather than Revit's "This view family type is not a plan view type".
- **Scale is read back off the view, never echoed.** `views/create-plan`, `views/create-drafting`,
  `views/create-section` and `views/set-scale` all report `scale` as `View.Scale` after the write, so
  a view template that owns the scale shows up as a difference between what was asked for and what
  came back instead of being invisible. `views/set-scale` validates once with `View.IsValidViewScale`,
  which documents the range as 1 to 24,000, then works per view: a schedule, a sheet or a template
  lands in `failed` with `VIEW_SCALE_NOT_SETTABLE` while the rest of the batch is still re-scaled and
  still assimilates into one undo entry. Those three are refused up front on purpose - a schedule and
  a sheet have no view scale, and a view template's scale is inherited by every view using it, so
  re-scaling one from a batch would silently re-scale views nobody named. Anything else is attempted
  and Revit's own refusal comes back per view as `REVIT_API_ERROR`.
- **A perspective view has no view scale, and that is a different endpoint, not a different number.**
  `views/set-scale` refuses one with its own code, `PERSPECTIVE_VIEW_HAS_NO_SCALE`, rather than
  letting Revit's exception on the setter come back as a bare `REVIT_API_ERROR`: 1/X describes a
  projection and a camera is not one. What re-sizes a perspective on its sheet is
  `views/scale-perspective-crop`, which wraps `View3D.ScalePerspectiveCropBox(double)` - Revit's own
  method, present since 2024.1, which scales the crop box on both axes and, in its documentation's
  words, "makes the change analogous to changing the scale of the orthographic view, so that both the
  size and scale of the view on a sheet changes". So `multiplier` 2 doubles the view on the paper and
  0.5 halves it, with the proportions locked and the framing identical. It is **not** a reframe:
  `views/set-crop` changes what is in shot, this changes the size of the shot, and the camera is never
  touched - which is why the response carries `camera` on both sides and `cameraUnchanged` comparing
  them, with `null` there meaning an orientation could not be read rather than that it moved. The four
  checks - element exists, is a `View3D`, is not a template (Revit's method throws on one), is
  perspective - all run before the transaction opens, so a refusal never half-applies. `dryRun`
  defaults to **true** and reports `before` plus the requested `multiplier` and deliberately no
  predicted `after`: the size Revit lands on is a readback, and the document is regenerated inside the
  transaction before it is taken, because `View.Outline` and the viewport's box on the sheet are
  derived geometry that otherwise still read as the size from before the call. Verified live on view
  223065 at `multiplier` 5.64896: `outline` 0.492 x 0.369 -> 2.78 x 2.085 paper feet, the viewport
  0.512 x 0.389 -> 2.8 x 2.105, `cameraUnchanged` true. Two measured nuances came out of that run and
  are worth stating, because both look like faults and neither is: the crop box's **model**
  coordinates did not change at all - composition is kept along with the camera, so `outline` and
  `viewport` are what say the call worked and `cropBox` is not - and Revit left the view **title** at
  its old paper position, so the label offset ended up inside the enlarged image and had to be reset
  through `sheets/set-viewport-position`. That reset is deliberately a separate call: where a view
  title sits is a drawing decision, and an endpoint that re-sizes a view does not get to make it.
- **`views/create-legend` duplicates, because the Revit API cannot create a legend.** This is a
  limitation, not a design choice, and it is worth stating exactly: the API exposes **no legend view
  type at all** (there is no public `ViewLegend`, and no `LegendComponent`); `ViewPlan.Create` takes
  only the four plan families quoted above; and `ViewDrafting.Create` documents
  `ArgumentException` - "viewFamilyTypeId is not a valid ViewFamilyType for a drafting view" - so the
  Legend `ViewFamilyType` every template carries has nothing that will accept it. The documented way
  through is `View.Duplicate`, which is what this endpoint does: `fromLegendId` picks the source,
  omitted it is the first legend `views/legends` would list, and the copy is made with
  `ViewDuplicateOption.Duplicate` so the new legend comes through **empty** rather than carrying
  someone else's key. A document with no legend at all answers `NO_LEGEND_TO_DUPLICATE` and says so
  in the message; it does **not** quietly substitute a drafting view, because a drafting view cannot
  be placed on many sheets and a legend can. What can then be put in it is `detail/lines` and
  `detail/text`. Legend components cannot - there is no creation API for them - and the bridge does
  not pretend there is.
- **`parameters/create-project` borrows the shared parameter file and gives it back.** A project
  parameter in Revit is a shared parameter definition plus a binding, and the definition lives in a
  text file named by `Application.SharedParametersFilename` - a user-wide Revit setting, not a
  property of the model. When it is unset (or names a file that is gone) the bridge points Revit at
  its own file in the temp folder, `revit-mcp-shared-parameters.txt`, for the length of the call and
  restores the original value in a `finally`. The file is stable across calls on purpose: the same
  file means the same GUID for a parameter of the same name, so re-running a call cannot mint a
  second definition that Revit would treat as a different parameter. `type` defaults to `Text`
  (`SpecTypeId.String.Text`), `group` to `IdentityData`, and `instance` to true - a `TypeBinding`
  otherwise. A name already bound in the document is **not** an error: nothing is created,
  `alreadyExisted` is true, and `categories` reports what it is really bound to.
- **`sheets/set-parameter` is not `parameters/set` with a different name.** `parameters/set` writes
  ONE name and ONE value across a list of ids, all-or-nothing. This one writes a **different** value
  per sheet - which is what stamping a phase across 56 sheets is - in one `TransactionGroup` with one
  inner transaction per sheet, so a sheet that has no such parameter lands in `failed` with
  `PARAMETER_NOT_FOUND` while the other 55 are written. Both write instance parameters only; they
  share the same storage-type switch (`WriteEndpoints.ApplyValue`), so a value is coerced identically
  either way.
- **Sheet grouping in the Project Browser is not settable from the API, so there is no endpoint for
  it.** `BrowserOrganization` is read-only in both the 2025 assemblies this add-in compiles against
  and the installed 2027 ones. The whole public surface is
  `GetCurrentBrowserOrganizationFor{Sheets,Views,Schedules}(document)`, `GetFolderItems(elementId)`,
  `AreFiltersSatisfied(elementId)`, and three **get-only** properties -`SortingParameterId`,
  `SortingOrder` and `Type`. There is no setter, no `Create`, no way to add a folder rule and no way
  to apply one; `FolderItemInfo` is equally a read-only report. Shipping a `browser/organize-sheets`
  that quietly did nothing would be worse than not having one, so the bridge has none. Two things do
  work: sheets sort by sheet number, so a numbering scheme like `L.02.xxx` / `L.03.xxx` already
  groups a set in reading order without any parameter at all; and the folder organisation is a
  one-off UI setting - right-click **Sheets** in the Project Browser > **Browser Organization** -
  pointed at the parameter `parameters/create-project` created and `sheets/set-parameter` filled in.
  It is saved in the model once it is set.
- **`detail/lines` and `detail/text` supply the view plane themselves.** `NewDetailCurve` refuses a
  curve that is not in the plane of the view, and `View.Origin` is documented as "not meaningful"
  for a plan, so the elevation comes from the plan's own level (a drafting view is simply 0) and the
  response reports it in `elevation`. `x`/`y` are the view's plan coordinates in feet.
  `detail/lines` refuses anything that is not a drafting view, a plan or a legend with
  `VIEW_CANNOT_HOST_DETAIL`; `detail/text` is wider - a section or an elevation may be annotated -
  and only refuses schedules, sheets and templates, with `VIEW_CANNOT_HOST_TEXT`. An unknown
  `lineStyle` is **not** a failure: the lines are drawn in the view's default style and the response
  carries `requestedLineStyle` plus `availableLineStyles`, the same shape `schedules/create` uses
  for fields. Text size lives on the type rather than on the note, so a `size` no loaded type
  carries means a duplicated `TextNoteType` - once per distinct size, in its own transaction,
  reusing any existing type whose `TEXT_SIZE` already matches.
- **`sheets/place-view` branches on the view kind so the caller does not have to.** A `ViewSchedule`
  goes on a sheet through `ScheduleSheetInstance.Create`, everything else through `Viewport.Create`;
  `kind` in the response says which was used. A view already on a sheet is `VIEW_ALREADY_PLACED`
  (Revit allows a view on exactly one sheet — duplicate it to show it twice); a view
  `Viewport.CanAddViewToSheet` refuses is `CANNOT_PLACE`. A schedule is the exception to the first
  rule: it may legitimately appear on several sheets, so only a repeat of the *same* sheet/schedule
  pair is refused. The **legend** case is a known sharp edge: Revit allows one legend on many sheets
  and the bridge does not, because there is one rule for viewports. `x`/`y` are sheet coordinates in
  feet on the paper — an A1 sheet is 1.95 × 1.38 — and default to the middle of `ViewSheet.Outline`.
  The array form (`placements`) catches per placement and answers `{placed, failed}`; the single
  form throws, so the codes surface as real errors.
- **`schedules/create` reports unknown fields instead of failing.** Names are matched against
  `ScheduleDefinition.GetSchedulableFields()` for that category; anything unmatched goes into
  `skippedFields`, and the response then also carries `availableFields` — the exact names that
  category does offer — so the caller can correct itself without a second round trip.
- **`directshape/create`, `planting/place`, `pipes/create` and `sprinklers/place` exist because
  family content is optional.** They also carry `comments` and `mark` onto every element they
  build, give every element a real `DirectShapeType`, and build its solids with the material named
  in `materialId`. See [Geometry without families](#geometry-without-families).
- **`materials`, `materials/create`, `materials/assign`, `walltypes/create` and `floortypes/create`
  are the only way anything here stops being grey.** Which route applies depends on the element and
  the two are not interchangeable — see [Materials](#materials). `materials/set-texture` is the
  step after that: a colour is flat paint, a bitmap is a texture.
- **The whole `document/*` family is deliberately not transacted.** Creating, opening, saving and
  closing a document are not transactable model edits and Revit throws if they are attempted inside
  a transaction, so none of them goes through `RevitWrite`. They are also where the
  no-active-document rule is least uniform, on purpose: `document/new` and `document/open` work with
  nothing open (that is the state Revit is in when a caller asks for a new project); `document/save`
  and `document/save-as` require an active document; `document/close` requires nothing and answers
  `{"closed": false}` when there is nothing to close, so a caller tidying up never has to ask first.
- **Neither `document/new` nor `document/save-as` overwrites unless it is told to.** An existing
  path is a `FILE_EXISTS` failure; both take `{"overwrite": true}` to replace it, and a missing
  parent directory is created by both. `document/new`'s overwrite is the *rebuild in place* route,
  and it is a real teardown: the file is closed in Revit if it is open (same detour as
  `document/close`), then it and the `<name>.0001.rvt` backups Revit wrote beside it are deleted,
  then the project is built again at that same path. A project is meant to live at one path and be
  rebuilt there — not versioned into a new filename per attempt, which leaves the user a folder of
  junk. Anything still holding a file is a `FILE_LOCKED` naming that exact path; the bridge never
  falls back to building under a different name.
- **`document/save` refuses a model that has never been saved** with `NOT_SAVEABLE` rather than
  letting Revit open its Save As file browser — which, on an unattended session, is a parked main
  thread and a `REVIT_BUSY` for every request after it. Use `document/save-as`.
- **`document/close` discards unsaved changes** unless `{"save": true}`, and it can close the
  *active* document, which `Document.Close` on its own cannot: Revit throws "The active document
  may not be closed from the API". So the endpoint gives Revit something else to be active on
  first — another open document, activated with `UIApplication.OpenAndActivateDocument` on its
  `PathName` (on a file Revit already has open that is an activation, not a second open), or, when
  this is the only document, a blank scratch project created with
  `Application.NewProjectDocument(UnitSystem.Metric)`, saved under `%TEMP%` and activated the same
  way. The response says what took over in `activePath` / `activeTitle` / `activeIsScratch`. The
  scratch is left open on purpose: it costs nothing and the next close finds it as the stand-in
  instead of making another. If the scratch cannot be stood up at all, the answer is
  `LAST_DOCUMENT_CANNOT_CLOSE` carrying Revit's own reason — never a pretend `{"closed": true}`.
- **`diagnostics` is a read, `diagnostics/config` is the switch.** Neither needs an active document.
  See [Unattended operation](#unattended-operation) for why a caller that writes should be reading
  the first one.
- **`reload` is answered by the loader, not by this route table.** It replaces the assembly every
  other endpoint lives in — see [Hot reload](#hot-reload).
- Any request may carry a top-level **`timeoutMs`** to override the server-side wait for that call.

### Geometry without families

`directshape/create`, `planting/place`, `pipes/create` and `sprinklers/place` build solids straight
into the project document, with no family and no family template behind them.

They exist because **family content is an optional Autodesk download**, and a perfectly working
Revit install can have none of it. When the library is genuinely absent there is no `.rfa` to load
and no `.rft` family template to author a replacement from either, so `families/symbols` answers
`[]` and `families/place` has nothing to place.

**This is the fallback, not the default.** `families/symbols` coming back empty almost always means
the library is installed and simply has not been loaded into *this* project yet — the library sits
outside the model, under `C:\ProgramData\Autodesk\RVT <year>\Libraries\<language>\`, and
`families/load` is what brings it in. Check there before reaching for these four: a garden built out
of spheres on cylinders looks exactly like a garden built out of spheres on cylinders.

`DirectShape` needs neither. `GeometryCreationUtilities` builds the solid,
`DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.X))` hangs it off a real Revit
category, and the result is a proper element — visible, selectable, categorised, and schedulable by
`schedules/create`.

`category` is a `BuiltInCategory` name **without** the `OST_` prefix (`Planting`,
`LightingFixtures`, `Furniture`, `Site`, `Walls`, `GenericModel`), matched case-insensitively; the
prefixed form is accepted too. A name that is not a category, or one `DirectShape.IsValidCategoryId`
rejects, is a `BAD_REQUEST` listing examples.

Each entry in `shapes` is one element. The primitives, all in decimal feet:

| `kind` | Fields | Built with |
| --- | --- | --- |
| `cylinder` | `base: {x,y,z}`, `radius`, `height` | circle (two half arcs) extruded +Z |
| `box` | `min: {x,y,z}`, `max: {x,y,z}` | rectangle at `min.z` extruded +Z by `max.z - min.z` |
| `sphere` | `center: {x,y,z}`, `radius` | half-disc in the XZ plane revolved a full turn about +Z |
| `cone` | `base: {x,y,z}`, `radius`, `height` | triangle revolved a full turn about +Z |
| `extrusion` | `profile: [{x,y}]`, `baseZ?`, `height` | closed polygon at `baseZ` extruded +Z |

An entry may instead be `{kind: "group", name?, parts: [primitive, ...]}`, which produces **one**
element carrying several solids — that is how a tree is a trunk plus a crown in a single schedulable
element rather than two unrelated ones. Groups do not nest.

The circle is two half arcs on purpose: `CreateExtrusionGeometry` refuses a `CurveLoop` made of a
single closed curve.

`planting/place` is the convenience wrapper over that: one element per point in the `Planting`
category, each a trunk cylinder with a crown sphere sitting on top of it (the sphere's underside
touches the top of the trunk). Defaults in **feet** are `trunkHeight` 8, `trunkRadius` 0.5,
`crownRadius` 6 — a 20 ft tree.

`pipes/create` and `sprinklers/place` are the same idea for irrigation, because there is no MEP
family content either. A run becomes **one** element built from a cylinder per segment between
consecutive points — a cylinder per segment on purpose, not a real sweep along the polyline, which
is a pile of failure modes for a result nobody can tell apart at 1:100. `radius` is per run and
defaults to 0.08 ft; a sprinkler is a small cylinder, `radius` 0.15 and `height` 0.5 ft by default.

Both of those pick their category at runtime:

| Endpoint | Preferred category | Fallback |
| --- | --- | --- |
| `pipes/create` | `OST_PipeCurves` | `OST_GenericModel` |
| `sprinklers/place` | `OST_Sprinklers` | `OST_GenericModel` |

The preferred one is used **only if `DirectShape.IsValidCategoryId` accepts it in that document**.
Which categories can hold a `DirectShape` is Revit's own rule, it is not published, and it has
changed between releases — so the bridge asks rather than assumes, and **every row of the response
says which category the element actually ended up in**. Read it instead of guessing; a schedule of
the wrong category is an empty schedule. `pipes/create` takes an explicit `category` to override the
choice, and that one is validated the same way `directshape/create` validates its own.

Every element these four build can carry **`comments`** and **`mark`**, written to
`ALL_MODEL_INSTANCE_COMMENTS` and `ALL_MODEL_MARK` after creation and read straight back off the
element into the response. Per entry wins; a top-level `comments`/`mark` is the fallback for every
entry that does not carry its own. This is the difference between a "Mapa de quantidades
plantações" that reads `Planting: 38` and one that breaks down by species, because a schedule can
group by either parameter.

All four are one `TransactionGroup` for the whole batch with one inner transaction per entry, so a
solid Revit refuses rolls back alone and lands in `failed` with its `index`. The one qualification
is where the solids are built: `directshape/create` builds each entry's inside the per-entry catch,
while `planting/place`, `pipes/create` and `sprinklers/place` build theirs up front — so a malformed
run or a non-positive radius is a `BAD_REQUEST` for the whole call rather than one entry in
`failed`.

#### Every element gets a DirectShapeType

A `DirectShape` created by `DirectShape.CreateElement` alone has **no type at all**, and that is not
cosmetic: "Edit Type" in the Properties palette does nothing, the element cannot be scheduled or
filtered by type, and it has no type parameters to read or write. All four endpoints therefore
create the element's type as well — `DirectShapeType.Create(doc, name, categoryId)`, then
`DirectShape.SetTypeId` — and every created row reports the `typeId` and `typeName` it landed on.

The type name is `typeName` when the caller gives one; otherwise it follows the element's own
`name`, and failing that the category. That default is the useful one: a `planting/place` call whose
points already carry species names gets **a type per species** without asking for anything, and a
`directshape/create` with no names at all still gets one type named `Site` rather than none.

Types are **looked up before they are created**, by name within the category, both across calls and
within one: planting 140 trees of four species makes four types, not 140. The lookup is a
`FilteredElementCollector` over `DirectShapeType` the first time a name is seen in a call and a
dictionary hit after that, and the dictionary is only filled once the inner transaction has
committed — a type created in a transaction that then rolled back never existed, and caching its id
would hand the next entry a dead `ElementId`.

`SetTypeId` is called **before** `SetShape`, and once: the API documents it as settable a single
time, and the geometry wanted on the element is the instance's own. The type carries no shape.

#### Material comes from the solid, so it is an argument here

`materialId` (or `materialName`) on any of the four is applied through
`SolidOptions(materialId, graphicsStyleId)` handed to `GeometryCreationUtilities`, so every solid in
the batch is **built** carrying the material. There is no version of this that happens afterwards: a
`Solid`'s material is fixed at construction, which is why `materials/assign` refuses a `DirectShape`
and says what to do instead rather than quietly succeeding. The graphics style is
`InvalidElementId` — the caller asked for a material, not a subcategory.

When no material is named, the geometry is built through the **three-argument** overloads exactly as
it always was. Passing `SolidOptions` carrying `InvalidElementId` would probably mean the same
thing, and "probably" is not a reason to change how every existing call builds its geometry.

### Materials

`materials` reads, `materials/create` authors, `materials/set-texture` gives one a real bitmap (see
[Textures](#textures)), and `materials/assign` puts one on elements that already exist.
`walltypes/create` and `floortypes/create` are here too, because for a wall or a floor the material
is not a property of the element at all.

**There are two routes onto an element and they are not interchangeable:**

| The element | Where its material lives | How it gets one |
| --- | --- | --- |
| Anything from `directshape/create`, `planting/place`, `pipes/create`, `sprinklers/place` | on each `Solid`, fixed at construction | `materialId` on the endpoint that builds it |
| `Wall`, `Floor`, roof, ceiling — and `Toposolid`, whose `ToposolidType` is a `HostObjAttributes` too | on the **type**, in its `CompoundStructure` | `walltypes/create` / `floortypes/create`, then build with that type — or `materials/assign`, which edits the type |
| A family instance with a material parameter | on the instance | `materials/assign` |

`materials/create` sets `Color`, and `Transparency` (0–100) and `Shininess` (0–128) when they are
asked for — omitted, Revit's own defaults are left alone rather than overwritten with a zero nobody
asked for. It also sets **`UseRenderAppearanceForShading` to false** for a colour-only material:
that property is documented as the switch between "shaded views use the render appearance" and
"shaded views use `Color` and `Transparency`", and a material created for its colour is no use if
shaded views ignore it. A material given an `appearanceAssetId` gets `true` instead, because then
the asset is the point.

A name that already exists is **reused**, not overwritten: the response is `{"created": false}` plus
the material's **current** colour, transparency and shininess. Re-running the same call is therefore
safe, and a caller that gets back a colour it did not ask for is looking at a material somebody else
authored — worth knowing rather than trampling.

### Textures

`materials/set-texture` connects a real bitmap to a material's appearance asset, and
`materials/create`'s `texturePath` does the same thing at creation time. The route, all of it
verified against a live Revit 2027:

```
AppearanceAssetElement.Create(document, name, <library "Generic" asset>)   // the asset to edit
using (Transaction)                                                        // REQUIRED, see below
  using (AppearanceAssetEditScope scope)
    Asset editable = scope.Start(assetElementId)
    AssetProperty diffuse = editable.FindByName("generic_diffuse")
    diffuse.AddConnectedAsset("UnifiedBitmap")
    Asset bitmap = diffuse.GetSingleConnectedAsset()
    bitmap.FindByName("unifiedbitmap_Bitmap")      -> AssetPropertyString.Value  = path
    bitmap.FindByName("texture_RealWorldScaleX/Y") -> AssetPropertyDistance.Value = size
    bitmap.FindByName("texture_WAngle")            -> AssetPropertyDouble.Value   = degrees
    scope.Commit(true)
```

**`AppearanceAssetEditScope.Commit` needs a transaction already open around it.** Without one it
throws `InvalidOperationException: EditScope cannot be closed, there is no opened transaction` —
and it throws it at `Commit`, after every edit has apparently succeeded, which is exactly the shape
of failure that looks like working code. `Start` succeeds either way and `scope.IsActive` is `true`
either way, so neither tells you. The scope lives inside `RevitWrite.InTransaction` for that reason.

**The schema problem, and the answer to it.** An asset's properties come from the schema it was
built from: `Generic` has `generic_diffuse`, `Ceramic` has `ceramic_color`, `MasonryCMU` has
`masonrycmu_color`, the Prism schemas have `opaque_albedo`, and `Water` and `Metal` have no
bitmap-bearing colour property at all. Editing whatever asset a material happens to carry is
therefore guesswork. Instead the material is **given** an asset created from Revit's own library
`Generic` asset — `Application.GetAssets(AssetType.Appearance)` returns ~3100 of them on a stock
2027 install, and the one whose `Name` is exactly `Generic` yields a full 58-property Generic asset
— unless the material already has a Generic asset that no other material shares. The report says
`assetAuthored` and `assetReason` so which of those happened is never a mystery. Only if the
library has no `Generic` to offer does it fall back to the asset in hand, trying each schema's
diffuse property in turn. Anything the asset turns out not to carry goes into `missing` rather than
throwing.

**Units.** `texture_RealWorldScaleX/Y` are `AssetPropertyDistance`, and on this machine
`GetUnitTypeId()` is `autodesk.unit.unit:inches` — a fresh bitmap defaults to `12`. `scale` is taken
in feet like every other length in this bridge and converted with `UnitUtils.ConvertFromInternalUnits`
rather than written raw, and `verified.scaleFeet` converts back so the caller reads what it asked
in. `texture_ScaleLock` ships `true`; an `x` and `y` that differ set it `false`, because different
sizes with the lock on is a state the UI would not let anyone author.

**Two side effects worth knowing.** Assigning a new appearance asset makes Revit repaint the
material's shading `Color` to match it — black, for a freshly created Generic one — so the colour
the material had is written back afterwards and reported in `colorRgb`: this endpoint textures a
material, it does not repaint it. And a material being textured gets
`UseRenderAppearanceForShading = true`, because a texture a shaded view ignores is not worth much.

**The bitmap is referenced, not embedded.** `unifiedbitmap_Bitmap` holds a path, so a path that does
not exist is an asset that renders nothing — `File.Exists` is checked up front and a miss is
`TEXTURE_NOT_FOUND` (404), before a material is created in the `materials/create` case. Revit ships
its own texture library under `C:\Program Files\Common Files\Autodesk Shared\Materials\Textures\`
(`1\Mats\`, `2\Mats\`, `3\Mats\` — around 5400 files: `grass_color.jpg`, `fieldstone_bump.jpg`,
`Finishes.Flooring.Wood.Plank.jpg`, `Sitework.Planting.Soil.jpg`, `water_calm.png`).

`materials/create`'s `texturePath` takes the bitmap only — scale, rotation and tint are
`materials/set-texture`'s business — and it does nothing for a material that already exists, which
still comes back untouched with `created: false` as it always has.

**What `appearanceAssetId` on `materials/create` still is:** the other half of this, and unchanged.
It **duplicates** an existing asset in the document (`AppearanceAssetElement.Duplicate`, which
duplicates the asset it holds) and assigns the copy, which is the documented way to give a new
material an existing material's rendered look without the two sharing one asset. `set-texture`
follows the same rule for the same reason: a shared asset is duplicated before it is edited, so one
material's texture can never appear on another's surfaces.

`materials/assign` takes the compound-structure route first for anything that has one — an element
that **is** a `HostObjAttributes` (a wall or floor type named directly) or a `HostObject` whose type
is one — and sets **every layer** of that structure. That changes every element of the type, which
is not what "assign to these elements" sounds like, so the row says `"appliesToType": true` and
names the type it edited. Otherwise it looks for a writable `ElementId` parameter, in order:
`STRUCTURAL_MATERIAL_PARAM`, `MATERIAL_ID_PARAM`, then a parameter literally called `Material`,
which is what a family author's own material parameter is usually called.

Everything else lands in `skipped` with a reason, and the reason is never a shrug. A `DirectShape`
gets the specific one — its material is carried by each solid and fixed when the solid was built, so
the fix is `materialId` on the endpoint that built it. One inner transaction per element, so an
element Revit refuses rolls back alone.

`walltypes/create` and `floortypes/create` **duplicate** an existing type — there is no
`WallType.Create` — and give the duplicate a single-layer `CompoundStructure`
(`CompoundStructure.CreateSingleLayerCompoundStructure(MaterialFunctionAssignment.Structure, width,
materialId)`). An omitted `thickness` keeps the source type's width and an omitted material keeps
the source's first-layer material, so either can be set without disturbing the other; with neither,
the call is a plain duplicate and the source's layering is left alone. A name that already exists is
reused with `{"created": false}` and is **not** re-cut to match the request — the response reports
the thickness and material the type really carries, read back off its structure. A source with no
compound structure at all (a curtain or stacked wall type) is a `BAD_REQUEST` naming it.

**The end cap condition is set per type, never inherited.** A `CompoundStructure` also carries an
`EndCap` — which shell layers wrap at the ends — and only a `WallType` may carry a real one.
`CreateSingleLayerCompoundStructure` hands back a wall's, so passing it straight to a `FloorType`
fails every time with `Input compound structure has wrong EndCap condition for this element type`.
Anything that is not a `WallType` therefore gets `EndCap = EndCapCondition.NoEndCap` before
`SetCompoundStructure`, which is the value the API documents as the one "floors and roofs must use".
It is **`NoEndCap`, not `None`**: `EndCapCondition.None` is a wall's "none of the shell layers
participate in end wrapping", still a wall-only condition, and a floor type rejects it just the
same. Walls keep exactly what Revit built, which is why `walltypes/create` never hit this. Any
future non-wall host type authored here — a ceiling, a roof, a `ToposolidType` — must say
`NoEndCap` too.

Then `walls/create` takes the new type by name — as `wallType`, or as `typeName`, which is what
every other type-taking endpoint here calls it — and `floors/create` already took `typeName`, so a
type authored this way is selected by name with no further work.

### Units

**Revit internal units — decimal feet — in both directions, unconverted.** Elevations, heights and
coordinates are all raw API values. The bridge deliberately converts nothing, so `levels` and
`levels/create` are symmetric.

The one exception is `materials/set-texture`'s `scale`, and it exists to keep that promise rather
than break it: an appearance asset's `texture_RealWorldScaleX/Y` is an `AssetPropertyDistance` in
**inches**, not feet, so the value is converted on the way in and back again in `verified.scaleFeet`.
A caller still speaks feet everywhere.

### Errors

Every failure, at any status code, uses one shape:

```json
{ "error": { "code": "NO_ACTIVE_DOCUMENT", "message": "Revit has no active document...", "stack": "..." } }
```

`message` and `stack` are the contract the Node client reads. `code` is an additive extra so a tool
can branch on a condition without string-matching prose.

| Status | `code` | When |
| --- | --- | --- |
| 400 | `BAD_REQUEST`, `BAD_JSON` | Malformed body, missing or wrong-typed field, unknown category/level/wall/floor/toposolid type, unknown material, a colour channel outside 0-255 or a transparency/shininess outside Revit's range, unknown `detailing` or `kind` |
| 404 | `UNKNOWN_PATH`, `UNKNOWN_ENDPOINT`, `ELEMENT_NOT_FOUND`, `PARAMETER_NOT_FOUND`, `TEMPLATE_NOT_FOUND`, `FILE_NOT_FOUND`, `TEXTURE_NOT_FOUND`, `GENERIC_ASSET_UNAVAILABLE` | No such route, no such element, no template, no file at the path `document/open` was given, no file at the `texturePath` a material was to be textured with, or no `Generic` asset in Revit's library to build an appearance from |
| 405 | `METHOD_NOT_ALLOWED` | Anything that is not POST |
| 409 | `NO_ACTIVE_DOCUMENT`, `NOT_A_PROJECT_DOCUMENT`, `FILE_EXISTS`, `FILE_LOCKED`, `LAST_DOCUMENT_CANNOT_CLOSE`, `NO_TITLEBLOCK`, `NOT_SAVEABLE`, `NO_VIEW_FAMILY_TYPE`, `NO_TEXT_NOTE_TYPE`, `NO_TOPOSOLID`, `VIEW_ALREADY_PLACED`, `CANNOT_PLACE`, `CANNOT_DUPLICATE`, `VIEW_CANNOT_HOST_DETAIL`, `VIEW_CANNOT_HOST_TEXT` | No document open, a family document where a project is required, a path that already exists, a file an overwrite could not delete because something still holds it, the only open document asked to close with no stand-in Revit would accept, no title block family loaded, a `document/save` on a model that has never been saved, no floor-plan/section/drafting view family type or no default text type in the template, no toposolid to flatten, a view already on a sheet, a view Revit will not put on that sheet, a view Revit will not duplicate that way, a view that is not a drafting view or a plan asked for detail lines, or a schedule/sheet/template asked for text |
| 500 | `REVIT_API_ERROR`, `INTERNAL_ERROR`, `RELOAD_FAILED` | The Revit API threw, an unexpected bug, or the handlers assembly could not be reloaded (the previous one is still serving) |
| 503 | `REVIT_BUSY`, `BRIDGE_NOT_READY`, `BRIDGE_SHUTTING_DOWN` | Revit did not pick the work up in time, or the handlers assembly is not loaded |

**No active document returns a structured 409, never a NullReferenceException.** It is the second
most common real-world failure — a request arrives while Revit sits on the start page or between
documents. The guard is **not central**: every endpoint opts in by calling
`RevitFacts.RequireDocument`. `status` does not, since it is how a client discovers the fact, and
neither does `document/new`, which has to work on a Revit with nothing open.

---

## Why the ExternalEvent pump exists

This is the part of the design that is not negotiable, so it is worth understanding before changing
anything in `RevitApiContext`.

**Revit API calls are only legal on Revit's main thread, from inside an event Revit itself raises.**
`HttpListener` hands requests to arbitrary thread-pool threads. Touching a `Document` from one of
those is undefined behaviour: usually an immediate "attempting to modify the model outside of a
transaction" style exception, sometimes a hard crash that takes the user's unsaved model with it.

So every request crosses the thread boundary through a single pump:

```
listener thread                         Revit main thread
---------------                         -----------------
enqueue work item
externalEvent.Raise()  ───────────────► Execute(UIApplication)
block on ManualResetEventSlim             drain queue, run each item,
   (default 30 s)                         capture result OR exception
       ◄───────────────────────────────   signal the item
read result / rethrow
```

Details that are load-bearing:

- **It is a queue, not a single slot.** Two concurrent requests must not clobber each other's
  result.
- **Exceptions are captured and rethrown** on the calling thread via `ExceptionDispatchInfo`, so the
  original stack survives into the `stack` field of the error response.
- **On timeout the bridge answers `503`**, not a hang and not a generic `500`. This is the single
  most common real-world failure: Revit is showing a modal dialog, so the main thread never becomes
  idle and never runs our handler. The message says so explicitly, because "request timed out" sends
  people looking in the wrong place.
- **A timed-out work item is marked abandoned and skipped** rather than executed later. Silently
  mutating the model after the client has already been told the request failed would be the worst
  possible surprise. If the item had *already started* when the timeout fired it cannot be
  cancelled — a Revit API call is not interruptible — so the 503 message says the operation may
  still complete inside Revit.
- **Accept and handle are separate threads.** The accept loop hands each request to the thread pool,
  so one request parked on the pump does not stall the listener for everybody else.

### Transactions

Every write endpoint wraps its work in a **`TransactionGroup` that is `Assimilate()`d on success and
`RollBack()`ed on failure**. That is what makes **one request equal exactly one Ctrl+Z**, and what
guarantees a request failing half way through leaves no partial edit behind. `parameters/set` over
five elements is all-or-nothing. This is a requirement of the bridge, not an optimisation — do not
"simplify" a write endpoint down to a bare `Transaction`.

The group is also what makes a *partially* skippable batch possible: `sheets/create` opens one inner
`Transaction` per sheet inside the one group, so the sheet whose number Revit refused rolls back
alone while the other 28 still assimilate into a single undo entry.

The `document/*` endpoints are the exception, and deliberately so: creating, saving, closing and
opening a document are not transactable model edits, and Revit throws if they are attempted inside a
transaction. That is why they live in `DocumentEndpoints` rather than in `WriteEndpoints`.

Every transaction opened through `RevitWrite.InTransaction` also gets the bridge's
`IFailuresPreprocessor` — see the next section. That is wired in centrally, in one place, precisely
so no endpoint can forget it.

---

## Unattended operation

The bridge is meant to be driven with nobody at the keyboard, and that takes more than an HTTP
listener. Revit stops and asks. A modal dialog parks the main thread inside its own message loop:
the `ExternalEvent` pump never runs, and every request from that moment on answers `REVIT_BUSY`
until a human clicks a button. A commit-time warning does the same thing from inside the bridge's
own write.

Two pieces deal with it, and one exists to keep them honest.

**`BridgeDialogWatcher`** subscribes to `UIControlledApplication.DialogBoxShowing` and answers with
`OverrideResult`: `1` (IDOK / `TaskDialogResult.Ok`) for an ordinary dialog, `2` (IDCANCEL) for
anything whose id or message text matches a destructive-sounding word — delete, remove, unload,
discard, overwrite, replace, purge, save, close, erase, detach, relinquish. The heuristic is crude
on purpose: cancelling a harmless dialog costs one failed request, OK-ing a destructive one costs
the user's model. It is subscribed **after** the listener starts, so the bridge's own "did not
start" dialogs are still shown to the user rather than clicked away unseen.

**`BridgeFailuresPreprocessor`** is installed on every transaction `RevitWrite` opens. In order: a
failure that offers a resolution gets `ResolveFailure`; otherwise a warning gets `DeleteWarning`;
otherwise — an error with no resolution — it is left alone, because `DeleteWarning` refuses anything
that is not a warning and suppressing an error would let a broken edit commit. Revit then fails the
transaction and the whole `TransactionGroup` rolls back, exactly as it would have.

**`BridgeDiagnostics`** is the honesty half. Suppressed is not swallowed: every dismissed dialog
(id, message, the result used) and every resolved warning (transaction name, severity, text, what
was done) goes into a ring buffer of 200 entries each, served by `diagnostics`. Entries evicted past
the cap are counted in `dropped`, so a truncated buffer never reads as a quiet one, and every entry
is also written to `bridge.log` with the same timestamp format so the two can be lined up.

A caller that writes **must** read `diagnostics` afterwards. A silently resolved warning is often
the model doing something the caller never asked for; without reading them, the bridge's convenience
would be indistinguishable from data loss. `diagnostics/config` takes `{"autoDismiss": false}` to
hand the dialogs back to a human — the right setting when somebody is working in Revit at the same
time — and `{"clear": true}` to empty the buffer, which is worth doing before a batch so the read
afterwards covers only that batch.

Dialogs seen while auto-dismiss is **off** are recorded too, with `"answered": false`. That is the
one thing a caller staring at `REVIT_BUSY` most needs to know.

---

## Hot reload

The add-in is two assemblies:

```
RevitMcpBridge.dll            the loader. Revit pins it for the session.
                             IExternalApplication, HttpListener, ExternalEvent pump,
                             dialog watcher, failures preprocessor, diagnostics, JSON.
RevitMcpBridge.Handlers.dll   the reloadable logic: the route table and every endpoint.
```

`HandlerAssembly` shadow-copies the handlers DLL to `%LOCALAPPDATA%\RevitMcpBridge\shadow\<stamp>\`
and loads it there into a **collectible** `AssemblyLoadContext`. POSTing to `reload` loads a freshly
built one, swaps it in, and retires the old context. The model stays open, the listener keeps its
port, queued work is untouched — only the routing and endpoint logic changes.

The rules that make it work, each of which is load-bearing:

- **The loader never references the handlers assembly at compile time.** The dependency runs the
  other way: the handlers project references the loader, implements `IBridgeRouter` (declared in the
  loader) and is found by type name at runtime. A compile-time reference would bind the handlers in
  the default load context and nothing could ever unload. `InternalsVisibleTo` is what lets the two
  halves share `BridgeException`, `JsonBody` and the rest without going public.
- **It is loaded from a shadow copy.** A loaded file is locked, and a locked file cannot be
  overwritten by the rebuild the reload exists to pick up. Old shadow directories are pruned
  best-effort; one still in use simply stays until a later start.
- **The load context resolves nothing itself** (`Load` returns `null`), so the loader assembly, the
  Revit API and the framework all bind to the copies already loaded in the default context. Two
  copies of `RevitAPI` would break type identity in the most confusing way available.
- **`reload` is answered by the loader, before the router is consulted.** It has to be: a router
  unloading the context its own method is executing in would be sawing the branch it stands on.
  Measured outside Revit — a frame still holding a reference to collectible code reliably prevents
  the unload from completing.
- **Whether the old context went is measured, not assumed.** `Retire` nulls every field, calls
  `Unload`, and hands back a `WeakReference`; `WaitForUnload` collects a few times and reports what
  the `WeakReference` says. If something is still holding the old context — most likely a request
  that was still running — the response comes back with `"unloadedPrevious": false` and a warning
  saying the new logic is live but the old context leaked. That is deliberately not smoothed over.
- **If the collectible load fails for any reason, the bridge falls back** to loading the handlers in
  the default context. It then works exactly as before but cannot hot-reload, `reload` says so in
  `collectible` and `warning`, and the reason is in `bridge.log`. A bridge that works but cannot
  reload beats a bridge that will not start.

What is **not** verified: all of the above was exercised outside Revit, against a stub handlers
assembly — load, call, shadow-copy-while-loaded, unload, and a deliberate leak to confirm the check
detects one. Whether Revit itself ends up holding a reference into the collectible context in some
situation is not something a harness can answer. If `unloadedPrevious` comes back `false` on every
reload, that is the signal it does.

---

## Why it binds 127.0.0.1 and not `+`

The listener prefix is the **`127.0.0.1` literal**, never `http://+:48884/` or `http://*:48884/`.

1. **`+` and `*` are "strong" wildcard prefixes and http.sys refuses them to a non-elevated
   process** unless an administrator registered a URL ACL first (`netsh http add urlacl`). Revit
   normally runs unelevated, so a wildcard prefix means the bridge just fails to start with
   *Access is denied* on most machines. Measured on this machine, unelevated:
   `http://127.0.0.1:48884/` starts fine, while `http://+:48885/` fails with
   `HttpListenerException ErrorCode=5`.
2. **A wildcard binds every interface**, which publishes an unauthenticated remote-control API for
   the user's models onto the LAN. The loopback literal needs no admin URL ACL and is simply not
   reachable off-machine.

There is a comment saying exactly this in `HttpBridgeServer.cs` so nobody "helpfully" changes it.

The listener takes the whole port on loopback and the router enforces the `/revit-mcp` base path
itself, so an unmatched path returns a structured JSON 404 instead of the raw http.sys 400 that a
narrower prefix would produce.

### Port already in use

Only one process can own the port, so a second Revit instance with the bridge installed will fail to
bind. That is handled: the add-in **logs it, writes a Revit journal comment, shows a `TaskDialog`,
and leaves Revit running normally** with the bridge inert for that session. It never takes Revit
down. `HttpListener` reports this as `ErrorCode=183` (verified) or `32`, both of which get a message
naming the likely cause.

---

## Configuration

| Variable | Default | Effect |
| --- | --- | --- |
| `REVIT_MCP_BRIDGE_TIMEOUT_MS` | `30000` | How long a request waits for Revit's main thread. Clamped to 1 s – 600 s. |

Per-request `timeoutMs` in the body overrides it. The port is fixed at 48884 by design.

Dialog auto-dismiss has no environment variable on purpose: it starts **on** and is changed through
`diagnostics/config` at runtime, because whether a human is sitting in front of Revit is not a
property of how the process was started.

**Log file:** `%LOCALAPPDATA%\RevitMcpBridge\bridge.log` (truncated past 1 MB). It is under
LocalAppData deliberately — Windows Controlled Folder Access blocks writes to Documents and Pictures
and surfaces them as misleading IO errors.

---

## Layout

```
revit-bridge/
  RevitMcpBridge.csproj      the loader. net8.0-windows, x64, reference-only Revit API.
                             Builds handlers\ after itself, into the same output folder.
  install.ps1                detect / install / uninstall, PS 5.1 compatible
  dist/                      prebuilt add-in that ships on npm (committed) - BOTH assemblies
  src/                       the loader assembly: everything that survives a reload
    BridgeApplication.cs     IExternalApplication entry point (thin, and why)
    BridgeHost.cs            owns the pump, the server, the dialog watcher, the handler host
    RevitApiContext.cs       ExternalEvent pump: worker thread <-> Revit main thread
    HttpBridgeServer.cs      HttpListener, 127.0.0.1 binding, port-in-use handling, reload route
    HandlerHost.cs           which generation of the handlers assembly is current; reload
    HandlerAssembly.cs       collectible AssemblyLoadContext, shadow copy, verified unload
    IBridgeRouter.cs         the one thing the loader knows about the reloadable half
    BridgeDialogWatcher.cs   answers Revit's modal dialogs (unattended operation)
    BridgeFailures.cs        IFailuresPreprocessor: resolves commit-time warnings
    BridgeDiagnostics.cs     ring buffers of what was suppressed; the auto-dismiss switch
    BridgeJson.cs            serializer options + the one error shape
    JsonBody.cs              request-body readers
    BridgeLog.cs             rolling log file
    BridgeException.cs       expected failures with an HTTP status + code
    BridgeInfo.cs            assembly version (of either half)
    AssemblyInfo.cs          InternalsVisibleTo the handlers assembly
  handlers/                  the reloadable assembly: routing + endpoints
    RevitMcpBridge.Handlers.csproj
    src/
      RequestRouter.cs       route table, JSON in/out, error mapping; implements IBridgeRouter
      Endpoints/
        ReadEndpoints.cs         status, levels, categories, query, elements, selection, titleblocks, sheets
        WriteEndpoints.cs        levels/create, walls/create, parameters/set, elements/delete, sheets/create
        ModelEndpoints.cs        toposolid/create, floors/create, families/load, families/symbols,
                                 families/place, openings/place, walltypes/create, floortypes/create
        MaterialEndpoints.cs     materials, materials/create, materials/set-texture,
                                 materials/assign
        ViewEndpoints.cs         views, views/create-plan, views/create-section, views/create-3d,
                                 views/set-style, views/set-background, views/hide-categories,
                                 views/set-sun, views/export-image, views/duplicate,
                                 sheets/place-view, schedules/create
        DirectShapeEndpoints.cs  directshape/create, planting/place, pipes/create, sprinklers/place -
                                 geometry with no family behind it, typed and with a material
        DocumentEndpoints.cs     document/new, open, save, save-as, close - never transacted
        DiagnosticsEndpoints.cs  diagnostics, diagnostics/config
        RevitWrite.cs            TransactionGroup helper (one request == one Ctrl+Z) + failures wiring
        RevitFacts.cs            document access, lookups, compact-row shape, point/loop readers
```

### Building both halves

`dotnet build RevitMcpBridge.csproj -c Release` produces **both** assemblies into
`bin\Release\net8.0-windows\`. The handlers project is not a `ProjectReference` — it references the
loader, so a `ProjectReference` back would be a cycle — and is built instead by a `BuildHandlers`
target that runs `Restore;Build` on it after the loader is compiled. That is why one build command
still gives you a complete add-in, and why `install.ps1` (which copies every file it finds next to
`RevitMcpBridge.dll`) needs no special knowledge of the split.

Shipping one half without the other is a bridge that does not start, so `scripts/build-bridge.mjs`
requires all four files (both DLLs and both `.deps.json`) and fails the build if one is missing.
