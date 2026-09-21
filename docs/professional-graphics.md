# Professional view graphics

Cast shadows, the Graphic Display Options behind them, and the view templates
that make one view's graphics repeatable across a whole set.

This is the half of "make the export look like architecture" that
`revit_set_view_style` cannot reach. It is also the part of this bridge with the
most opportunity to lie to you, so most of what follows is about how it avoids
doing that.

## The uncomfortable fact this is built around

`BuiltInParameter.GRAPHIC_DISPLAY_OPTIONS_SHADOWS` exists. It is in the
installed Revit 2027 `RevitAPI.dll` and in the 2025 reference assembly this
project compiles against.

**That proves nothing about whether it can be read or written on a view.**
Autodesk's own REVIT-222419 is the record of an enum member that is not a usable
toggle. On a live view, `get_Parameter` for it can:

- come back `null` — the view has no such parameter at all;
- come back with `StorageType.None` — the Graphic Display Options *dialog
  launcher*, which is not a 0/1 you can set;
- come back read-only;
- come back owned by a view template, which makes it unsettable on the view.

Only the live view can say which. So every endpoint here **probes** and reports
what it found rather than asserting anything about "the API".

The repo used to claim the opposite — `views/set-style` answered
`SHADOWS_NOT_EXPOSED` and said cast shadows were impossible from the API. That
claim was too broad and has been withdrawn. What survives from it, and is still
true:

- `View` has no `EnableSunlightAndShadows` and no shadows boolean property.
- `View.ShadowIntensity` and `View.SunlightIntensity` are the Lighting
  *sliders*, not the switch. They change nothing while cast shadows are off.
- `View.SunAndShadowSettings` is read-only and holds the sun **position**.
- There is no ambient light anywhere: no `View.AmbientLightIntensity`, no
  ambient `BuiltInParameter` on a view, in either API version.

## Null means unknown

Everywhere in these responses, a `null` is the bridge saying it could not find
out. It is never a stand-in for "off".

```json
"shadows": {
  "parameter": "GRAPHIC_DISPLAY_OPTIONS_SHADOWS",
  "available": true,
  "storageType": "None",
  "readOnly": true,
  "on": null,
  "controlledByTemplate": false,
  "writable": false,
  "writableReason": "storage type is None, not Integer, so it is not a 0/1 toggle the API can set - StorageType.None is the Graphic Display Options dialog launcher"
}
```

`on: null` there means: the parameter is present, but nothing readable as a
boolean came back, so **whether this view casts shadows is unknown to the
bridge**. A bridge that answered `false` for that would be worse than useless —
it would be confidently wrong.

`writable` is the verdict the write endpoint acts on, and it is itself
three-valued: `true`, `false`, or `null` when the view has a template whose
controlled set could not be read.

## Reading: `revit_get_view_graphics`

Call this first. It reports, read off the view rather than remembered:

| Field | What it is |
| --- | --- |
| `style`, `detailLevel` | `View.DisplayStyle` / `View.DetailLevel` |
| `templateId`, `templateName` | the view template overriding them, if any |
| `shadowIntensity`, `sunlightIntensity` | the Lighting sliders, 0-100 |
| `ambientLightIntensity` | always `null` — see above |
| `background` | 3D views only |
| `shadows`, `exposure` | full probes, shape as above |

```
revit_get_view_graphics { view_ids: [212657, 212658] }
```

## Writing: `revit_set_view_graphics`

Style, detail level, both intensities and cast shadows, in one undo step.

`shadow_intensity` and `sunlight_intensity` are 0-100 and go onto the real,
documented, settable properties. `ambient_light_intensity` is **refused** with
`409 AMBIENT_LIGHT_NOT_EXPOSED` rather than accepted and silently ignored.

`shadows` takes one of two routes, and the response says which.

### Route 1 — `method: "parameter"`

Taken only when the probe proves all four conditions on that view: the parameter
is present, its storage type is `Integer`, Revit does not call it read-only, and
no view template controls it. Then it is written inside the same transaction as
everything else and **read back**:

```json
"shadows": {
  "requested": true,
  "method": "parameter",
  "posted": false,
  "pending": false,
  "verified": true,
  "readBack": [{ "viewId": 212657, "on": true }]
}
```

This is the only route that can honestly report `verified: true`, because the
value was read off the view after the commit. If it wrote but did not read back
as asked, `verified` is `false` and the note says something else is overriding
it — treat it as not set.

### Route 2 — `method: "posted-command"`

When the parameter is not writable, there is exactly one other route, and it is
not a model edit at all: Revit's own UI commands. `ID_IMAGE_SHADOW_ON` and
`ID_IMAGE_SHADOW_OFF` are both present as strings in Revit 2027's
`UIFrameworkRes.dll`, `DesktopMFC.dll` and `Utility.dll`.

```json
"shadows": {
  "requested": true,
  "method": "posted-command",
  "command": "ID_IMAGE_SHADOW_ON",
  "viewId": 212657,
  "canPostCommand": true,
  "activatedView": true,
  "posted": true,
  "pending": true,
  "verified": false,
  "unverified": true,
  "probe": { "...": "the probe that forced this route" },
  "note": "Posted, not applied. ..."
}
```

Read that as: *a command was queued, and nothing here knows whether it worked.*
It is not a success report and the word does not appear in it. Things worth
knowing about this route:

- **A posted command acts on the ACTIVE view.** So the bridge makes the target
  view active first, with `UIDocument.ActiveView`. Revit only allows that while
  the document is not modifiable, which is why the whole route sits outside the
  transaction group and runs after it has closed. If the view cannot be made
  active, nothing is posted — better than toggling shadows on the wrong view.
- **`CanPostCommand` is asked first.** The API documents `PostCommand` as
  accepting members of `PostableCommand` and external commands, and there is no
  `PostableCommand` member for shadows. A `false` answer comes back as
  `method: "unavailable"` with the reason, and nothing is changed — it is not
  worked around.
- **One posted command at a time.** Revit throws on a second. A request made
  while one is outstanding comes back `method: "blocked"`, having changed
  nothing.
- **One view per request.** Because the command acts on the active view, a
  request that needs this route must name a single `view_id`. Asking for
  `shadows` on a batch where any view needs the fallback is a `BAD_REQUEST` that
  tells you to split it.

### Checking afterwards: `revit_get_view_graphics_command_status`

```json
{
  "posted": true,
  "command": "ID_IMAGE_SHADOW_ON",
  "requested": true,
  "postedAtUtc": "2026-09-21 00:41:02Z",
  "idleSeenAtUtc": "2026-09-21 00:41:03Z",
  "pending": false,
  "probeAtPost": { "on": null },
  "probeAtIdle": { "on": null },
  "probeNow": { "on": null },
  "verified": false,
  "verifiedBy": null
}
```

`pending` clears once Revit has been idle at least once since the post — that is
when a posted command gets to run. `verified` is true **only** when the
parameter can be read back as an integer and matches what was asked. When it
cannot be read, `verified` stays `false` with `verifiedBy: null` and a note
saying so. A posted UI command that nothing can observe is reported as
unverified, not as done.

> Implementation note for anyone editing this: the `Idling` handler that
> timestamps that first idle is one-shot and unsubscribes itself before doing
> anything else. These handlers live in a collectible `AssemblyLoadContext`, and
> a delegate left on Revit's `Idling` event would pin the context and break
> `/revit-mcp/reload`.

## View templates

### `revit_capture_view_template`

Turns a view that is already drawn the way you want into a reusable template,
and — the part that makes it a template rather than a snapshot — states what it
controls.

`mode` is the shorthand for that set:

| `mode` | Controls |
| --- | --- |
| `graphics` (default) | everything the template can, **except** the sun (`VIEW_GRAPH_SUN*`, `VIEW_SOLARSTUDY*`), the crop/camera/extents (`VIEWER_*`) and the phase (`VIEW_PHASE`, `VIEW_PHASE_FILTER`) |
| `shadows` | only `GRAPHIC_DISPLAY_OPTIONS_SHADOWS` |
| `all` | everything, sun and crop and phase included |

`graphics` is the one you want for a drawing set: it gives thirty views the same
graphics while each keeps its own sun position, its own crop and its own phase.
Pass `parameter_ids` instead to state the set exactly.

Every exclusion comes back with its reason, and the controlled set is read back
off the template after the write rather than echoed —
`SetNonControlledTemplateParameterIds` ignores ids it does not recognise, so the
only set worth reporting is the one Revit kept.

### `revit_apply_view_template`

```
revit_apply_view_template { template_id: 900, view_ids: [1, 2, 3] }          # plans it
revit_apply_view_template { template_id: 900, view_ids: [1, 2, 3], dry_run: false }
```

**`dry_run` defaults to true.** A first call changes nothing and reports the
plan.

Every target is validated before anything is written — it is a view, it is not a
template or a sheet, and `View.IsValidViewTemplate` says the template suits it —
and one failure fails the whole request with all the reasons, rather than
leaving nineteen views changed and one not. The write is one transaction inside
one group: one Ctrl+Z.

`mode` is the difference between the two things Revit calls applying a template:

- **`apply`** (default) is `ApplyViewTemplateParameters`, a one-time copy. No
  association survives it. A view that already has a template will not take the
  parameters that template controls, and that view's row says
  `limitedByExistingTemplate: true`.
- **`assign`** sets `ViewTemplateId`, a lasting association: the template keeps
  controlling those parameters and they can no longer be set on the view.
  Assigning onto a view that already has one would detach it, so that needs
  `replace: true`.

Each view's graphics are read back afterwards, which is where an `apply` that
the view's own template silently overrode becomes visible.

## Category overrides: `revit_override_view_categories`

`POST /revit-mcp/views/override-categories` — the per-category half of the
Visibility/Graphics dialog, on one view. This is what makes a set of drawings
read as drawings rather than as one CAD export repeated: paving light so it sits
behind the planting, planting green, context halftoned, the subject drawn heavy.
Nothing about the model changes; it is all stored on the view.

```json
{
  "viewId": 220554,
  "overrides": [
    { "category": "Planting", "projectionColor": { "r": 90, "g": 150, "b": 70 } },
    { "category": "Site", "halftone": true, "projectionLineWeight": 1 },
    { "category": "OST_Roads", "surfaceTransparency": 40, "cutLineWeight": -1 }
  ],
  "dryRun": true
}
```

Every member used here was checked against **both** the installed Revit 2027
`RevitAPI.xml` and the 2025 reference assembly this project compiles against:
`View.AreGraphicsOverridesAllowed`, `View.IsCategoryOverridable`,
`View.GetCategoryOverrides` / `SetCategoryOverrides`, the
`OverrideGraphicSettings` copy constructor, its six setters used here and
`InvalidPenNumber`. They are identical in the two.

### It merges, it does not replace

`GetCategoryOverrides` hands back **everything** already overridden on that
category — both fill patterns, both line patterns, the detail level. The current
settings are copied and only the named fields are replaced, so a call asking for
a colour cannot wipe a solid fill somebody set in the dialog. The pattern fields
are reported in `before` and `after` for exactly that reason: if a merge had
dropped one, it would show up there.

| Field | Writes |
| --- | --- |
| `projectionColor`, `cutColor` | `SetProjectionLineColor` / `SetCutLineColor` — **line** colours, `{r,g,b}` 0-255 |
| `projectionLineWeight`, `cutLineWeight` | 1-16, or `-1` to clear the override (`InvalidPenNumber`) |
| `halftone` | `SetHalftone` |
| `surfaceTransparency` | 0 (opaque) to 100 |

Surface and cut **pattern** colours are read and reported, never written. A
solid-fill colour is a different override from a line colour, and this endpoint
does not pretend to set one.

### What it refuses, before a transaction opens

- **A view with no V/G at all** — a sheet, a schedule, a legend:
  `409 OVERRIDES_NOT_SUPPORTED`.
- **A category this view will not override** — `View.IsCategoryOverridable`
  false: `409 CATEGORY_NOT_OVERRIDABLE`.
- **A view whose template owns the overrides** — `409
  OVERRIDES_CONTROLLED_BY_TEMPLATE`, naming the template and the categories.
  Revit would accept the write, commit it, and keep drawing the view the
  template's way; a success message for a change nobody can see is worse than a
  refusal. Ownership is `GetTemplateParameterIds` minus
  `GetNonControlledTemplateParameterIds`, matched per category kind:
  `VIS_GRAPHICS_MODEL`, `VIS_GRAPHICS_ANNOTATION`,
  `VIS_GRAPHICS_ANALYTICAL_MODEL`. A category kind that maps to none of them
  reports `templateParameter: null` — unknown, not "safe".
- **An unknown category name, a duplicate category, a row with no settings, or a
  value out of range** — `BAD_REQUEST`, nothing written.

The value check is Revit's own: the setters validate on the settings object,
with no transaction open, so a value Revit will not take is a refusal rather
than a rollback. Note the API documents transparency as *greater than* 0 and
*less than* 100 while the dialog offers 0 — the bridge passes 0-100 through and
lets Revit answer.

### Visibility is untouched

Overrides and visibility are two different things. `hidden` is reported from
before the write, `hiddenAfter` from after it, and `hiddenPreserved` says
whether they match. A hidden category that is overridden stays hidden.

`dryRun` **defaults to true** and answers with each category's current override
plus `would`: the exact merged settings the call would write, built the same way
the write builds them. One applied call is one transaction for the whole batch —
one undo step — and `after` is read back off the view.

## What none of this can do

**Applying a template with shadows off does not create shadows.** A view
template carries only what the source view had. If you capture from a view whose
cast shadows were off — or whose shadows probe read `on: null`, meaning the
bridge could not tell — the resulting template turns shadows on nowhere.

Both `capture-template` and `apply-template` report the relevant probes next to
the template so that is visible up front rather than discovered later. The
honest sequence is:

1. `revit_get_view_graphics` on the source view. Look at `shadows.on`.
2. If it is not a confident `true`, get it there first — `revit_set_view_graphics`,
   or the Revit UI (Graphic Display Options, or the view control bar).
3. Confirm it, by looking at the view or exporting an image.
4. *Then* capture the template and apply it to the set.

Capturing from a view you have not checked produces a template that spreads an
unknown state across thirty views, quietly.
