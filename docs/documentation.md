# Documentation

Eight tools for the half of the job that happens after the model is built:
cropping a view to what the drawing is about, hiding what stands in front of it,
laying a sheet out, reading a schedule back, and getting the set out of Revit as
PDF.

| Tool | Endpoint | Writes? |
| --- | --- | --- |
| `revit_get_view_crop` | `POST /revit-mcp/views/crop` | no |
| `revit_set_view_crop` | `POST /revit-mcp/views/set-crop` | yes, one undo step |
| `revit_hide_elements_in_view` | `POST /revit-mcp/views/hide-elements` | yes, one undo step |
| `revit_read_schedule` | `POST /revit-mcp/schedules/read` | no |
| `revit_configure_schedule` | `POST /revit-mcp/schedules/configure` | yes, one undo step |
| `revit_get_sheet_layout` | `POST /revit-mcp/sheets/layout` | no |
| `revit_set_viewport_position` | `POST /revit-mcp/sheets/set-viewport-position` | yes, one undo step |
| `revit_set_schedule_position` | `POST /revit-mcp/sheets/set-schedule-position` | yes, one undo step |
| `revit_get_browser_organization` | `POST /revit-mcp/sheets/browser-organization` | no, and cannot |
| `revit_export_pdf` | `POST /revit-mcp/export/pdf` | no, writes files |

## The three things to know before calling any of them

**Every write here defaults to `dry_run: true`.** That is not the convention the
modelling tools use and it is deliberate: these edit presentation drawings a
human has already approved, so the change has to be asked for twice — once by
calling, once by turning the dry run off. The dry run reports the current state
and exactly what would be written, and changes nothing.

**The template is the authority.** A view template that owns the crop is not
something to fight. `revit_set_view_crop` refuses a view whose template controls
`Crop View` or `Crop Region Visible` with `CROP_CONTROLLED_BY_TEMPLATE`, naming
the template, rather than writing a value Revit will ignore and reporting a
success the drawing would not show. The fix is to change the template, or to
untick that parameter in the View Template dialog — both decisions for a human,
not workarounds for a tool. `revit_get_view_crop` reports
`templateControlledCrop` on every view so this is visible before you try.

**Model feet and paper feet are different units and never mix.**
`revit_set_view_crop` takes MODEL coordinates in decimal feet.
`revit_set_viewport_position` and everything in `revit_get_sheet_layout` are
feet on the PAPER — an A1 sheet is 1.95 x 1.38, an A0 is 2.76 x 3.90.

## What is proof and what is an assumption

This is the part worth reading twice. Every one of these tools reports both, and
they are labelled in the payloads themselves, not just here.

Measured, every time, after the fact:

- `modelBounds`, and the `after` block of a crop write — read off the view once
  the transaction has committed, so a template quietly overriding the result is
  visible rather than silent.
- `isHidden` — read with `Element.IsHidden(view)`, never echoed from the
  request. An element left invisible by a category switch or a template is not
  credited to the hide call.
- `center`, `bounds`, `labelBounds` on a viewport, and the `before`/`after` of a
  move — all through Revit's own getters.
- `path` and `bytes` on an exported PDF — read off the disk. Revit reporting a
  successful export is not taken as evidence that a file exists; a missing file
  is `EXPORT_PRODUCED_NO_FILE`, never a success.
- `pages` — counted out of the PDF's own page objects.

Assumed, and said so in the payload:

- `pageMapping: "requestedOrder"` — in a COMBINED PDF, the per-view `page`
  numbers are the order the views were handed to Revit. That is an assumption
  about Revit's ordering, not a measurement. With one view per file it is
  `"exact"` and the mapping is certain.
- `pages: null` with `pagesMeasured: false` — the PDF keeps its objects in
  compressed streams and the page count could not be read. Null means "not
  measured", never "empty", and it is never back-filled from the number of views
  exported.
- `groupingLevels` from `revit_get_browser_organization` — derived from the
  folders one sample sheet actually sits in, because a browser organization
  scheme does not expose its own definition. `groupingLevelsSource` names the
  sheet it came from.

And null means "Revit gave no answer", which is a different answer from zero or
false:

- `annotationCrop: null` — this view carries no annotation crop parameter at
  all, as opposed to having one that is off.
- `bounds: null` on a viewport or title block — Revit produced no geometry for
  it, as opposed to a box collapsed at the sheet origin.
- a `null` schedule cell — merged, or holding an image, as opposed to blank.

## `revit_get_view_crop`

Read-only. Takes `view_id` or `view_ids`, not both.

```json
{ "view_ids": [211925, 211926] }
```

```json
{
  "views": [
    {
      "id": 211925,
      "name": "Plano Geral",
      "viewType": "FloorPlan",
      "isTemplate": false,
      "cropSupported": true,
      "cropBoxActive": false,
      "cropBoxVisible": true,
      "annotationCrop": false,
      "modelBounds": {
        "min": { "x": -180.4, "y": -142.9, "z": -9.8 },
        "max": { "x": 265.1, "y": 210.3, "z": 42.0 }
      },
      "localBounds": {
        "min": { "x": -222.7, "y": -176.6, "z": -42.0 },
        "max": { "x": 222.7, "y": 176.6, "z": 9.8 }
      },
      "templateId": 211002,
      "templateName": "L.02 Plantas",
      "templateControlledCrop": []
    }
  ]
}
```

`modelBounds` is the answer you want. `localBounds` is the raw `Min`/`Max` in
the crop box's own coordinate system and is reported only so the two are never
confused — look at the numbers above: they are different boxes for the same
crop. Reading `localBounds` as model coordinates is the classic way to crop a
section to the wrong place, which is precisely why both are named.

## `revit_set_view_crop`

The crop box is a `BoundingBoxXYZ` whose `Min` and `Max` live in the VIEW's
coordinate system, and its `Transform` maps between that and the model. You give
model coordinates; the bridge puts **all eight corners** of your box through
`view.CropBox.Transform.Inverse` and takes the axis-aligned box around the
results. Transforming only `min` and `max` would be wrong the moment a view is
not aligned with the project axes — a section looking north-east would crop to
the wrong region, and the error would look like a Revit bug rather than a maths
one. The view's `Transform` itself is preserved untouched: it belongs to the
view's orientation, not to the crop.

```json
{
  "view_ids": [211925],
  "model_bounds": {
    "min": { "x": -20.0, "y": -15.0, "z": -3.0 },
    "max": { "x": 120.0, "y": 95.0, "z": 30.0 }
  },
  "dry_run": false
}
```

Three things are written every time: the box, `CropBoxActive = true` and
`CropBoxVisible = false` — cropped, with no crop rectangle printed on the sheet.

```json
{
  "dryRun": false,
  "applied": true,
  "views": [
    {
      "id": 211925,
      "name": "Plano Geral",
      "viewType": "FloorPlan",
      "requestedModelBounds": { "min": { "x": -20.0, "y": -15.0, "z": -3.0 },
                                "max": { "x": 120.0, "y": 95.0, "z": 30.0 } },
      "localBounds": { "min": { "x": -70.0, "y": -55.0, "z": -30.0 },
                       "max": { "x": 70.0, "y": 55.0, "z": 3.0 } },
      "before": { "cropBoxActive": false, "modelBounds": { "…": "…" } },
      "after": { "cropBoxActive": true, "cropBoxVisible": false,
                 "modelBounds": { "…": "…" } }
    }
  ]
}
```

Every refusal is checked for EVERY view before anything is written, so the call
is all-or-nothing — a crop applied to half a drawing set and refused on the rest
is worse than one that never started.

| Code | Means |
| --- | --- |
| `CROP_NOT_SUPPORTED` | a sheet, schedule, legend or view template. Revit gives those no crop box. |
| `CROP_CONTROLLED_BY_TEMPLATE` | the view's template owns the crop flags. Named, not worked around. |
| `BAD_REQUEST` | `min` is not strictly below `max` on all three axes. |

**This is what fixes a plan that sits tiny in the corner of its sheet.** An
uncropped view carries section marks and exterior elevation markers far outside
the building, and the viewport is sized to all of it. Crop first, then
`revit_set_view_scale`, then measure with `revit_get_sheet_layout`.

**On a perspective view, the second step is a different tool.** A perspective
camera has no view scale — 1:X describes a projection and a camera is not one —
so `revit_set_view_scale` refuses it with `PERSPECTIVE_VIEW_HAS_NO_SCALE` and
`revit_scale_perspective_crop` is what re-sizes it: one `multiplier`, both axes,
proportions locked, and the camera untouched. The three tools are easy to
confuse and do different jobs:

| Want | Tool |
| --- | --- |
| Change WHAT IS IN SHOT — reframe to a region of the model | `revit_set_view_crop` |
| Change HOW BIG a plan, section or isometric prints — 1:X | `revit_set_view_scale` |
| Change HOW BIG a PERSPECTIVE prints, same shot | `revit_scale_perspective_crop` |

The perspective tool defaults to `dry_run: true` like the rest, reports the
view's outline in PAPER feet on both sides, and answers `cameraUnchanged` —
which is the proof that re-sizing did not quietly reframe the picture.

Two things a verified run measured, both of which will otherwise read as bugs:

- **The model-space crop box does not have to move.** At `multiplier` 5.64896 a
  view went from 0.492 x 0.369 to 2.78 x 2.085 paper feet, and its `cropBox`
  `min`/`max` came back identical either side. That is the composition being kept
  as well as the camera. Judge the call by `outline` and `viewport`.
- **The view title stays where it was.** Revit does not move it with the frame,
  so after a large multiplier the old label offset ends up inside the enlarged
  image. Fix it with `revit_set_viewport_position` and its `label_offset` —
  a separate call on purpose, because where a title belongs is a drawing
  decision, not something a re-size should guess.

## `revit_hide_elements_in_view`

The permanent, per-view "Hide in View > Elements" override, via
`View.HideElements`. Two things it is NOT, both of which look identical on
screen:

- **It is not deletion.** The elements stay in the model, in every other view and
  in the schedules. Hiding entourage trees in front of a presentation elevation
  does not touch the planting design or its quantities.
- **It is not Temporary Hide/Isolate.** That evaporates when the view closes and
  never reaches a sheet. This survives and prints.

```json
{ "view_id": 211925, "ids": [884301, 884302], "hidden": true, "dry_run": false }
```

```json
{
  "dryRun": false,
  "applied": true,
  "viewId": 211925,
  "viewName": "Plano Geral",
  "hidden": true,
  "elements": [
    { "id": 884301, "name": "Tilia cordata", "category": "Planting",
      "canBeHidden": true, "isHidden": true }
  ]
}
```

Every id is checked with `CanBeHidden` **before** anything is hidden, and one
element Revit refuses fails the whole call with `ELEMENT_CANNOT_BE_HIDDEN`
naming it. That is not fussiness: `View.HideElements` refuses the entire batch
when one member cannot be hidden, so there is no half-landed version of this to
report. Groups, arrays, constraints and links are the usual offenders.

`hidden: false` unhides the same list, and ids that are not currently hidden are
filtered out rather than making Revit refuse the batch — so the same list works
both ways. To hide whole categories instead, that is
`revit_hide_view_categories`, and it is the right tool for level datums and
section marks.

## `revit_read_schedule`

Read-only, and the only way to see what a schedule says. The obvious
alternatives do not exist: a schedule cannot be exported as an image
(`Document.ExportImage` does not accept a `ViewSchedule` as a view), and
`ViewSchedule.Export` writes a delimited text file to a disk the caller then
cannot read either.

```json
{ "schedule_id": 424242, "limit": 50, "offset": 0 }
```

```json
{
  "id": 424242,
  "name": "Mapa de Plantação",
  "viewType": "Schedule",
  "columns": [
    { "index": 0, "fieldId": 0, "name": "Family and Type", "heading": "Espécie",
      "isHidden": false, "fieldType": "Instance", "displayType": "Standard",
      "canTotal": false, "canSortGroup": true,
      "gridColumnWidth": 0.4, "sheetColumnWidth": 0.4 },
    { "index": 1, "fieldId": 1, "name": "Count", "heading": "Qtd.",
      "isHidden": false, "fieldType": "Count", "displayType": "Standard",
      "canTotal": true, "canSortGroup": false,
      "gridColumnWidth": 0.2, "sheetColumnWidth": 0.2 }
  ],
  "header": { "totalRows": 1, "totalColumns": 2, "offset": 0, "returnedRows": 1,
              "returnedColumns": 2, "truncatedRows": false, "truncatedColumns": false,
              "cells": [["Mapa de Plantação", null]] },
  "body": { "totalRows": 812, "totalColumns": 2, "offset": 0, "returnedRows": 50,
            "returnedColumns": 2, "truncatedRows": true, "truncatedColumns": false,
            "cells": [["Tilia cordata", "34"], ["Acer platanoides", "12"]] }
}
```

Two independent sources of the column headings come back, and they disagree in a
way that matters. `columns` is the `ScheduleDefinition`: the fields in display
order, each with its field name and the `ColumnHeading` actually printed, which
a user may have retyped. `header` and `body` are the laid-out grids exactly as
Revit draws them — and which of the two holds the heading row depends on the
schedule, so both are returned whole rather than guessed at.

`fieldId`, `canTotal` and `canSortGroup` are there so `revit_configure_schedule`
can be called without guessing. Address a field by `fieldId`, not by `index`:
the index is a position in the current order and moves when fields are
reordered. `canSortGroup` is `false` for a Count column and Revit means it — you
cannot group by a field that has no value until grouping has happened.

**The cells are read through `TableView.GetCellText`, not
`TableSectionData.GetCellText`** — and this is worth stating because the obvious
reader is wrong and fails silently. `TableSectionData.GetCellText` returns text
only "if the cell's type is `CellType.Text` or `ParameterText` or `CustomField`"
and returns **an empty string** for anything else; its own remarks send you to
`TableView` for "the formatted text of the cell regardless of cell type".
Measured: schedule `212388` returned 142 body rows whose `Count` column read
`"1"` and whose `Family and Type` column read `""` on **every single row** — a
cell type that reader could not render, reported as though the parameter were
blank. So in this reply `""` means the cell really is empty, and `null` means
Revit would not give text for it at all. The two are different answers and are
never normalised into each other.

`limit` and `offset` page the BODY rows only; `limit` defaults to 100 and is
clamped to 500 rather than refused. `totalRows` always says how much you did not
get. If it exceeds `returnedRows`, say so instead of treating the page as the
whole schedule.

## `revit_configure_schedule`

Reshapes how an existing schedule presents the rows it already has. It **never
adds a column and never removes one** — `revit_create_schedule` makes a new
schedule; a schedule's columns are its author's decision.

```json
{
  "schedule_id": 212388,
  "itemized": false,
  "group_by": [{ "field": "Family and Type", "sort_order": "Ascending",
                 "show_header": false, "show_footer_count": false }],
  "fields": [{ "field": "Count", "totals": true, "width_ft": 0.2 }],
  "grand_total": { "show": true, "show_count": true, "title": "Total" },
  "dry_run": false
}
```

```json
{
  "id": 212388, "name": "Mapa de quantidades plantações",
  "before": { "isItemized": true, "sortGroupFields": [], "bodyRows": 142 },
  "after": { "isItemized": false,
             "sortGroupFields": [{ "fieldId": 0, "name": "Family and Type",
                                   "sortOrder": "Ascending" }],
             "bodyRows": 27 },
  "dryRun": false, "applied": true
}
```

`itemized: false` is the API behind Revit's "Itemize every instance" tick box
and is the thing that turns 142 rows of one plant each into one row per species
carrying a count. **It does nothing on its own.** Rows collapse only where the
sort/group fields make them equal, so `itemized: false` with no `group_by`
collapses by whatever grouping already exists — which is frequently none. Send
both in the same call.

`group_by` **replaces** the sort/group list rather than appending to it. The
previous list comes back under `before` so it can be restored by hand. Revit
refuses to sort or group by Count, percentage and formula fields, and
`CanSortByField` is checked for every entry before anything is written.

`totals` is `ScheduleField.DisplayType`: `true` means `Totals`, `false` means
`Standard`, or name one of `Standard` / `Totals` / `Min` / `Max` / `MinMax`.
`CanTotal` is checked first — asking a text column to total is an error, not a
silent no-op.

Everything is validated before the transaction opens, so a request that is wrong
in its last entry changes nothing at all. `dry_run` **defaults to true**.

**Read `bodyRows`, not `applied`.** It is the row count Revit actually lays out,
and it is the only honest proof that a regrouping did what was asked: `applied:
true` says a transaction committed, not that 142 rows became 27.

| Code | Means |
| --- | --- |
| `KEY_SCHEDULE_NOT_GROUPABLE` | a key schedule: its rows are the keys, so itemizing and grouping do not apply. |
| `BAD_REQUEST` | an unknown or ambiguous field name, a field Revit will not group by, a non-totalable column, or nothing to change. |

## `revit_set_schedule_position`

```json
{ "instance_id": 211610, "top_left": { "x": 3.10, "y": 2.40 }, "dry_run": false }
```

A separate tool from `revit_set_viewport_position`, and it has to be: a schedule
on a sheet is a `ScheduleSheetInstance`, not a `Viewport`, and Revit anchors it
by `ScheduleSheetInstance.Point` — its **top-left corner** — rather than by the
centre of a box. Passing a schedule instance id to the viewport tool fails. Use
the id from `revit_get_sheet_layout`'s `scheduleInstances`; that is the
**instance** id, not the schedule view's id, and they are different numbers.

The revision schedule inside a title block is refused with
`REVISION_SCHEDULE_IS_FIXED`. Revit prohibits setting its position, and where it
sits belongs to the title block family — the fix is to edit the family.

`before` and `after` carry bounds measured off the sheet rather than the
requested point echoed back, because the common failure is not a misplaced
table: on `moradia.rvt`, `L.09.017` holds a schedule **10.62 ft tall on a
2.76 ft page**, overhanging 9.25 ft below the sheet. Moving that cannot help it.
A table longer than the page needs splitting into segments or fewer rows, and
only the measured bounds tell you which problem you have.

## `revit_get_sheet_layout`

Read-only, and the call to make **before** moving anything. A plan that has come
out a stamp in the corner of an A0 could be the view's crop, the title block's
extent or the viewport's position, and those are three different fixes — this is
what tells them apart.

```json
{ "sheet_id": 211089 }
```

```json
{
  "id": 211089,
  "sheetNumber": "L.02-01",
  "name": "Plano Geral",
  "isPlaceholder": false,
  "outline": { "min": { "x": 0.0, "y": 0.0 }, "max": { "x": 3.897, "y": 2.756 },
               "width": 3.897, "height": 2.756 },
  "titleblocks": [
    { "id": 211090, "name": "A0 metric", "typeName": "A0 metric",
      "bounds": { "min": { "x": 0.0, "y": 0.0 }, "max": { "x": 3.897, "y": 2.756 },
                  "width": 3.897, "height": 2.756 } }
  ],
  "viewports": [
    { "id": 211500, "sheetId": 211089, "viewId": 211925, "viewName": "Plano Geral",
      "viewType": "FloorPlan", "scale": 200, "rotation": "None",
      "center": { "x": 1.62, "y": 0.71 },
      "bounds": { "min": { "x": 1.02, "y": 0.47 }, "max": { "x": 2.22, "y": 0.95 },
                  "width": 1.20, "height": 0.48 },
      "labelBounds": { "min": { "x": 1.02, "y": 0.33 }, "max": { "x": 1.72, "y": 0.44 },
                       "width": 0.70, "height": 0.11 },
      "labelOffset": { "x": 0.0, "y": -0.14 },
      "labelLineLength": 0.70 }
  ],
  "scheduleInstances": [
    { "id": 211610, "scheduleId": 424242, "scheduleName": "Mapa de Plantação",
      "segmentIndex": -1, "isTitleblockRevisionSchedule": false,
      "topLeft": { "x": 3.10, "y": 2.40 },
      "bounds": { "min": { "x": 3.10, "y": 0.20 }, "max": { "x": 3.85, "y": 2.40 },
                  "width": 0.75, "height": 2.20 } }
  ]
}
```

Four things here are separate on purpose:

- **`outline` is NOT the paper.** It is `ViewSheet.Outline`, and it is not a
  reliable page boundary in either direction — do not test "is this on the
  sheet?" against it. Measured on `moradia.rvt`: sheet `L.09.017` reports
  `outline.min.y = -9.2456` because Outline grew to swallow a schedule hanging
  off the bottom of the page, while sheet `L.02.002` reported an outline that
  stopped dead at the page edge even though its viewport stuck out well past it.
  So it over-reports in one case and under-reports in the other. Use
  `titleblocks[].bounds` as the page — that is the rectangle that prints. A
  sheet with no title block has no measurable page, and `outline` is then the
  only thing left, which is worth knowing is a guess.
- `center` is what `revit_set_viewport_position` moves. It is the centre of the
  viewport's box, **not** the bottom-left and not the view's origin.
- `bounds` is what the view occupies with its crop included; `labelBounds` is
  the view title, which sits OUTSIDE the box and is what usually collides with
  the next view. A layout that looks clear on `bounds` alone still overlaps.
- `topLeft` on a schedule is Revit's own anchor for a `ScheduleSheetInstance`:
  the TOP-LEFT, not the centre. Paired with `bounds`, it is what says whether a
  table is running off the bottom of the sheet.

In the reply above the viewport occupies 1.20 x 0.48 feet of a 3.90 x 2.76 sheet
— a sixth of the width. That is the signature of an uncropped view, not of a
misplaced viewport, and moving it would fix nothing.

## `revit_set_viewport_position`

```json
{
  "viewport_id": 211500,
  "center": { "x": 1.75, "y": 1.45 },
  "label_offset": { "x": 0.0, "y": -0.14 },
  "dry_run": false
}
```

Feet on the PAPER. `center` goes through `Viewport.SetBoxCenter`, so it is the
same point `revit_get_sheet_layout` reports — read the layout first and move
relative to what it said, rather than guessing a coordinate. `label_offset` and
`label_line_length` are the view title's position and the line under it; both
are left exactly as they are when not passed.

The reply carries `before` and `after`, both read back through Revit. A viewport
whose positioning is not free does not go where it is told, and a reported
centre that is not the requested one is the only way to see that.

Moving a viewport does not resize it. If the view is the wrong size for the
sheet, that is `revit_set_view_crop` and `revit_set_view_scale`; this only places
the result.

## `revit_get_browser_organization`

Read-only, and read-only **is the finding** rather than a limitation of this
bridge. Checked member by member against the Revit 2025 API rather than assumed:

- `BrowserOrganization` has no `Create` and no `Duplicate`.
- `SortingOrder` and `SortingParameterId` are get-only — the compiled
  `RevitAPI.dll` carries `get_SortingOrder` and `get_SortingParameterId` and no
  matching setters.
- Nothing defines, adds or reorders folder levels, and nothing makes a scheme the
  active one for the browser.
- `ViewSheetSet.SheetOrganizationId` IS settable and is a red herring: it orders
  a print/export set, not the Project Browser tree.

So `canApplyFromApi` is `false` and always will be. What the endpoint does give:
the scheme in force with its sorting parameter, every scheme NAME defined in the
document (the ones a user can pick in the UI), and the actual chain of folders
each sheet sits in with the parameter that produced each one.

```json
{ "sheet_ids": [211089] }
```

```json
{
  "active": { "id": 211011, "name": "all", "type": "Sheets",
              "sortingOrder": "Ascending", "sortingParameterId": -1002115,
              "sortingParameterName": "Sheet Number" },
  "schemes": [{ "id": 211011, "name": "all", "type": "Sheets" }],
  "canApplyFromApi": false,
  "applyLimitation": "The BROWSER ORGANIZATION SCHEME is read-only in Revit's API…",
  "sheetCollectionAlternative": "…Revit 2025 has SheetCollection…",
  "groupingLevels": null,
  "groupingLevelsSource": "No sheet in this document sits in a browser folder, so the scheme's grouping levels are not observable…",
  "totalSheets": 56,
  "returnedSheets": 1,
  "truncated": true,
  "sheets": [
    { "id": 211089, "sheetNumber": "L.02-01", "name": "Plano Geral", "folders": [] }
  ]
}
```

The automatable half of the job is the PARAMETER a scheme would group by: add it
with `revit_create_project_parameter` and stamp it with
`revit_set_sheet_parameters`. A human then points the browser at it once, in the
Revit UI (right-click Sheets > Browser Organization > Edit > Grouping and
Sorting). **Stamping the parameter is not the grouping being applied**, and must
never be reported as such — `folders: []` above is what an ungrouped Sheets list
looks like from the API, no matter how well-filled the parameter is.

There is a separate, writable mechanism: Revit 2025's `SheetCollection`
(`SheetCollection.Create(document, name)`, and `ViewSheet.SheetCollectionId` has
a setter). It gives native collapsible sheet groups, but it is a different thing
from the grouping scheme and it is ONE level deep — a sheet belongs to exactly
one collection, so it is collapsible groups, not a nested hierarchy. The
dedicated sheet collection tools are what drive it; this endpoint stays
read-only and reports the scheme only, and `sheetCollectionAlternative` in the
reply points at them.

## `revit_export_pdf`

Revit's own PDF exporter, through `Document.Export` and `PDFExportOptions`.
Vectors, white paper, real sheet size.

**This is the deliverable format, and the distinction is not cosmetic.**
`revit_export_view_image` captures a view as Revit DRAWS IT ON SCREEN, so a view
with a dark background comes out as a black-paper negative of the drawing — a
white-on-black plan that no PNG setting turns into something printable. Use a
PDF for anything a person will read or plot, and a PNG only for looking at the
3D model. It is also nothing to do with rendering: the API cannot start Revit's
raytracer at all.

```json
{
  "sheet_ids": [211089, 211090],
  "folder": "C:\\Projects\\Quinta\\PDF",
  "filename": "L.02 Estudo Previo",
  "combine": true,
  "overwrite": false
}
```

```json
{
  "folder": "C:\\Projects\\Quinta\\PDF",
  "combine": true,
  "overwrite": false,
  "pageMapping": "requestedOrder",
  "files": [
    {
      "path": "C:\\Projects\\Quinta\\PDF\\L.02 Estudo Previo.pdf",
      "requestedPath": "C:\\Projects\\Quinta\\PDF\\L.02 Estudo Previo.pdf",
      "bytes": 4820913,
      "pages": 2,
      "pagesMeasured": true,
      "views": [
        { "viewId": 211089, "viewName": "Plano Geral", "viewType": "DrawingSheet",
          "sheetNumber": "L.02-01", "page": 1 },
        { "viewId": 211090, "viewName": "Cortes", "viewType": "DrawingSheet",
          "sheetNumber": "L.02-02", "page": 2 }
      ]
    }
  ]
}
```

`view_ids` and `sheet_ids` are explicit lists and at least one is required.
There is deliberately no "export everything" shorthand: a drawing set is a
decision, and a mistyped one that exports 200 views is expensive. Sheets go in
`sheet_ids` and ordinary views in `view_ids`; the wrong way round is a
`BAD_REQUEST` rather than a surprise. Views are exported first, then sheets.

Naming is the bridge's, not Revit's, and that is what makes the rest possible.
`PDFExportOptions.FileName` is honoured only when `Combine` is true, so every
export runs with `Combine = true` — once for the whole set when `combine` is
true, once per view when it is false. Knowing the target path up front is what
lets the endpoint refuse to overwrite, and that is checked for EVERY target
before the first byte is written.

| Code | Means |
| --- | --- |
| `FILE_EXISTS` | a target path is taken and `overwrite` is not true. Nothing was exported. |
| `FOLDER_NOT_WRITABLE` | the folder exists but a probe file could not be written into it. |
| `EXPORT_PRODUCED_NO_FILE` | Revit reported an export and no file appeared. Never reported as a success. |
| `BAD_REQUEST` | a view with `CanBePrinted` false, a relative `folder`, or sheets and views swapped. |

The folder is created if missing and then PROVED writable by writing and
deleting a probe file — a folder that exists and cannot be written to fails
here, with the reason, rather than as an empty export nobody can explain. It
must be absolute: a relative path resolves against whatever working directory
Revit happens to have, which is not somewhere a caller can find a PDF
afterwards.

Not an undo step: exporting changes nothing in the model, and Revit refuses an
export inside a transaction.

## The order that fixes a bad sheet

1. `revit_get_sheet_layout` — measure. Is the viewport small, or is it merely in
   the wrong place?
2. `revit_get_view_crop` — is the view uncropped, and does a template own the
   crop?
3. `revit_set_view_crop` with `dry_run: true`, read the answer, then `false`.
4. `revit_hide_elements_in_view` for anything still in the way that a crop
   cannot exclude.
5. `revit_set_view_scale` — now that the view covers only what it should. On a
   perspective view that is `revit_scale_perspective_crop` with a multiplier
   instead; the scale tool refuses a camera.
6. `revit_get_sheet_layout` again — measure the result rather than assuming it.
7. `revit_set_viewport_position` if it still needs moving.
8. `revit_export_pdf` — and check `path` and `bytes` in the manifest.
