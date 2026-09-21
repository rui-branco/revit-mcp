# Professional QA tools

Three tools for checking what is actually in the model, and two safe ways to fix
the commonest things that are wrong with it. They exist because every other read
in this MCP is deliberately compact (id, name, category, type), and compact
cannot answer:

- is this tree standing in the pool?
- is that chair buried under the slab?
- what grades is the paving being asked to sit on?
- why did the scale I set read back as something else?
- what is Revit itself already complaining about?

| Tool | Endpoint | Writes? |
| --- | --- | --- |
| `revit_inspect_elements` | `POST /revit-mcp/elements/inspect` | no |
| `revit_move_elements` | `POST /revit-mcp/elements/move` | only with `dry_run: false` |
| `revit_excavate_toposolid` | `POST /revit-mcp/toposolid/excavate` | only with `dry_run: false` |
| `revit_get_warnings` | `POST /revit-mcp/document/warnings` | no |
| `revit_list_view_templates` | `POST /revit-mcp/views/templates` | no |

## Coordinates

Every point, vector and bounding box in these responses is in **absolute Revit
model coordinates** — the document's internal origin — in decimal feet,
unconverted. Never relative to a level, a view, the project base point or the
survey point. The responses say so themselves in `coordinateSystem` rather than
leaving it to be assumed, because "the Z is 2.5" means nothing until you know
what it is 2.5 above.

`levelElevation` is `Level.ProjectElevation`, not `Level.Elevation`. The two are
the same number until somebody sets a level's Elevation Base to the shared
(survey) datum, and from then on `Elevation` is measured from a different origin
than every other Z in the response. `ProjectElevation` is always the internal
origin, so the elevations stay comparable with the bounding boxes and locations
beside them.

## `revit_inspect_elements`

Measured facts about up to 500 elements. Over 500 ids is **rejected, not
truncated**: a silently shortened inspection reads as a complete one.

```json
{ "ids": [245473, 210751], "include_parameters": false }
```

Every element carries `id`, `name`, `uniqueId`, `class` (the Revit API type),
`category`, `typeId`, `typeName`, `level` / `levelId` / `levelElevation`,
`hostId`, `pinned`, `groupId`, `location`, `modelBoundingBox` and
`materialIds`. Then, by what it is:

- **Floor** — `floor.level`, `floor.heightOffsetFromLevel`, and the closed loops
  of its top face under `floor.loops`. The top face rather than the sketch: that
  is the boundary as Revit holds it *now*, after every join, opening and shape
  edit. Straight segments report two endpoints; anything curved reports those
  plus a `tessellation`.
- **Toposolid** — `slabShape`, the shape vertices with their absolute positions
  read off `SlabShapeEditor`. This is the only way to see the grades paving and
  planting have to sit on. A legacy `TopographySurface` reports the same reading
  as `topography.points`.
- **FamilyInstance** — `familyInstance.symbolId` / `symbolName` / `familyName`,
  the `facing` and `hand` vectors, and `actualZ`: the elevation the instance is
  really at, read off its location rather than off whatever was asked for when
  it was placed.

Ids with no element behind them come back in `missingIds`, with `requested` and
`found` counts. Nothing is dropped quietly.

```json
{
  "coordinateSystem": "Absolute Revit model coordinates (the document's internal origin), decimal feet. ...",
  "requested": 2,
  "found": 1,
  "missingIds": [999999],
  "elements": [
    {
      "id": 245473,
      "class": "Floor",
      "pinned": false,
      "groupId": null,
      "location": null,
      "modelBoundingBox": {
        "min": { "x": 28.0, "y": 12.0, "z": -0.82021 },
        "max": { "x": 68.0, "y": 42.0, "z": 0.0 },
        "center": { "x": 48.0, "y": 27.0, "z": -0.410105 }
      },
      "materialIds": [1201],
      "floor": {
        "level": "Ground",
        "levelElevation": 0.0,
        "heightOffsetFromLevel": 0.0,
        "boundarySource": "topFace",
        "truncated": false,
        "loops": [
          {
            "closed": true,
            "segmentCount": 4,
            "segments": [
              { "type": "Line", "start": { "x": 28.0, "y": 12.0, "z": 0.0 }, "end": { "x": 68.0, "y": 12.0, "z": 0.0 } }
            ]
          }
        ]
      }
    }
  ]
}
```

### How much geometry comes back

`include_geometry` is a tri-state, and the point of it is that a graded site
surface runs to thousands of vertices:

| `include_geometry` | vertex / point lists |
| --- | --- |
| omitted | included when there are **500 or fewer**, otherwise only the count and the min/max Z |
| `true` | included, capped at 500, with `truncated: true` |
| `false` | never; the count and the Z range still come back |

So `vertexCount`, `minZ` and `maxZ` are always there. A surface is never
reported as empty because it was too big to list.

`include_parameters` (default `false`) adds every instance parameter. Off by
default for the usual reason: it is hundreds of lines per element when three of
them were the question.

### A display name does not identify a parameter

Revit uses one display name for more than one parameter. A family instance
carries **two** called `Level`: `FAMILY_LEVEL_PARAM`, which is read-only, and
`SCHEDULE_LEVEL_PARAM`, which is not. A dictionary keyed by name can hold only
one of them, and `Element.LookupParameter` — what `revit_set_parameters` used to
call — answers with whichever Revit enumerates first. That is how an inspection
reports `Level` as writable and the very next write is refused as read-only:
both answers were true, about different parameters.

So every parameter now carries its own identity, and a repeated name is reported
rather than collapsed:

```json
{
  "parameters": {
    "Level": { "value": 210748, "display": "Cota do jardim", "isReadOnly": false, "id": -1002062, "builtIn": "SCHEDULE_LEVEL_PARAM", "ambiguous": true }
  },
  "duplicateParameters": [
    { "name": "Level", "id": -1001352, "builtIn": "FAMILY_LEVEL_PARAM", "isReadOnly": true, "display": "Cota do jardim", "ambiguous": true },
    { "name": "Level", "id": -1002062, "builtIn": "SCHEDULE_LEVEL_PARAM", "isReadOnly": false, "display": "Cota do jardim", "ambiguous": true }
  ]
}
```

- `id` is Revit's own parameter id — negative for a built-in, positive for a
  shared or project parameter.
- `builtIn` is the `BuiltInParameter` name, or `null` when there is none.
- `ambiguous: true` marks a name the element uses more than once.
- `duplicateParameters` lists every occurrence of every repeated name, in
  enumeration order. It is `[]` when nothing is ambiguous.

The dictionary still answers by name, so nothing that read it before reads
anything different.

`revit_set_parameters` is the other half. A name that matches exactly one
parameter behaves as it always has; a name that matches several is **refused**:

```
AMBIGUOUS_PARAMETER (409): Element 220545 has 2 instance parameters named "Level":
id -1001352 (FAMILY_LEVEL_PARAM, read-only, currently Cota do jardim);
id -1002062 (SCHEDULE_LEVEL_PARAM, writable, currently Cota do jardim).
Nothing was written.
```

Pass the one you mean back as `parameter_id` (`parameterId` on the wire). The
`name` stays required and has to be the name of the parameter that id points at
— an id copied from the wrong row would otherwise write silently to something
nobody named. The result row reports the `parameterId` it wrote, so the answer
says which one it was.

## `revit_move_elements`

Move elements by a vector in feet — lift furniture out of a slab, shift a tree
off a path — without deleting and rebuilding them.

```json
{ "ids": [101, 102], "translation": { "x": 0, "y": 0, "z": 1.5 }, "dry_run": true }
```

**`dry_run` defaults to `true`.** The default call opens no transaction at all:
there is nothing to undo and nothing to roll back. Pass `dry_run: false` to
actually move.

Either way every element comes back with `before` and `after` — both the
location and the model bounding box — so the caller never has to guess where
something ended up:

```json
{
  "dryRun": true,
  "moved": false,
  "count": 1,
  "translation": { "x": 0.0, "y": 0.0, "z": 1.5 },
  "afterSource": "predicted",
  "elements": [
    {
      "id": 101,
      "name": "Chair",
      "category": "Furniture",
      "before": {
        "location": { "kind": "point", "point": { "x": 4.0, "y": 6.0, "z": -0.5 } },
        "boundingBox": { "min": { "x": 3.0, "y": 5.0, "z": -0.5 }, "max": { "x": 5.0, "y": 7.0, "z": 2.0 }, "center": { "x": 4.0, "y": 6.0, "z": 0.75 } }
      },
      "after": {
        "location": { "kind": "point", "point": { "x": 4.0, "y": 6.0, "z": 1.0 } },
        "boundingBox": { "min": { "x": 3.0, "y": 5.0, "z": 1.0 }, "max": { "x": 5.0, "y": 7.0, "z": 3.5 }, "center": { "x": 4.0, "y": 6.0, "z": 2.25 } }
      }
    }
  ]
}
```

`afterSource` says what `after` is: `"predicted"` on a dry run (arithmetic), and
`"readBack"` on a real move — measured off the element after a regeneration,
which is the only way to see Revit having done something other than what was
asked.

### A move that did not happen is a failure, not a success

Revit accepts a move it then declines to apply. Ask a family instance whose
elevation comes from its level for half a metre in Z: `MoveElements` returns, no
warning is raised, the element has not moved, and a naive endpoint answers
`"moved": true`. That answer is worse than an error — everything the caller does
next is built on a position the model does not have.

So a real move is measured before it is allowed to commit. Each element's anchor
— its location point, a curve's start, or failing both the bounding box minimum
— is read before the move and again after the regeneration, still inside the
transaction, and the displacement that actually happened is compared with the one
that was asked for, to the document's `ShortCurveTolerance`. Any element that
fell short takes the **whole group** back with it:

```
MOVE_NOT_APPLIED (409): Revit accepted the move and did not apply it:
220545 asked for (0, 0, 1.5) and got (0, 0, 0) (feet, tolerance 0.002558).
The whole request was rolled back - the model is exactly as it was.
```

Requested against actual, per element, because "it did not move" alone does not
say how far short it fell. An element whose position is driven by something else
— a family instance sitting on a level, a sub-component of another family — has
to be changed through what drives it, not through this endpoint.

Elements with no location and no bounding box cannot be measured. They are
listed in `unverified` on the response rather than counted as moved.

### What it refuses, before touching anything

All-or-nothing: the whole batch moves in one transaction inside one transaction
group, so it is one Ctrl+Z, and a failure part way through rolls every element
back rather than leaving half a relocation in the model. **Nothing is ever
deleted** — the ids in the response are exactly the ids that were asked for.

| Code | HTTP | When |
| --- | --- | --- |
| `ELEMENT_NOT_FOUND` | 404 | any id has no element; every offending id is named |
| `ELEMENTS_PINNED` | 409 | any element is pinned — the bridge will **not** unpin for you |
| `ELEMENTS_GROUPED` | 409 | any element is in a group; moving one member alone puts it out of step with every other instance |
| `BAD_REQUEST` | 400 | `translation` missing, or a component that is NaN or infinite |
| `MOVE_NOT_APPLIED` | 409 | Revit accepted the move and did not apply it; the whole group was rolled back |
| `TRANSACTION_ROLLED_BACK` | 409 | Revit refused the commit; nothing was left in the model |

The first four are raised before a transaction is opened; the last two *are* the
rollback. Each names every offender rather than the first.

## `revit_excavate_toposolid`

Cut the terrain with the things that are meant to be sunk into it — a pool, a
basement, a sunken path — through Revit 2025's native `Toposolid.ExcavateBy`.

```json
{ "toposolid_id": 210751, "ids": [245556], "dry_run": true }
```

This is the **non-destructive** answer to a floor fighting a graded surface.
`revit_flatten_toposolid` rewrites the grades under a region and the ground it
levelled never comes back; an excavation is an *association* between two
elements, so the surface keeps every vertex it has, the hole follows the element
that made it, and Revit can take it off again by itself (Remove Excavation in
the UI). Reach for this first; flatten only when the ground genuinely has to
change shape.

`toposolid_id` may be left out when the document has exactly one toposolid, the
same rule `revit_flatten_toposolid` uses. With several, it is required and the
error lists them. `ids` is what cuts the terrain, never the surface itself.

```json
{
  "dryRun": false,
  "excavated": true,
  "excavatedCount": 1,
  "toposolidId": 210751,
  "toposolidName": "Terreno",
  "count": 1,
  "units": "Revit internal units: decimal feet, volumes in cubic feet",
  "volumeBefore": 12000.5,
  "volumeAfter": 11750.25,
  "volumeRemoved": 250.25,
  "elements": [
    {
      "id": 245556,
      "name": "Pool shell",
      "category": "Floors",
      "alreadyExcavating": false,
      "excavated": true,
      "excavationVolume": 250.25
    }
  ]
}
```

The volume readback is the point of the response. `volumeBefore` and
`volumeAfter` are the toposolid's own computed volume and `volumeRemoved` is the
difference — the one number that says the excavation actually *cut* something
rather than merely being accepted. Per element, `excavationVolume` is the volume
Revit attributes to it: an element that was excavated and still reports `null`
takes nothing out, which means it does not overlap the surface. None of the
three can be predicted (Revit computes the intersection; it is not arithmetic),
so a dry run answers `null` for all of them rather than guessing.

### What it refuses, before touching anything

Every id goes through Revit's own `Toposolid.CanBeExcavatedBy` **before a
transaction is opened**. Revit answers an unsupported element with an
`InvalidOperationException` mid-transaction; a preflight that names every
offender is a better answer than a 500.

| Code | HTTP | When |
| --- | --- | --- |
| `ELEMENT_NOT_FOUND` | 404 | any id has no element; the whole batch is refused |
| `CANNOT_EXCAVATE` | 409 | `CanBeExcavatedBy` said no for one or more ids |
| `NO_TOPOSOLID` | 409 | the document has none — a legacy `TopographySurface` cannot be excavated at all |
| `BAD_REQUEST` | 400 | `toposolid_id` is not a toposolid, several exist and none was named, or an id in `ids` *is* the toposolid |
| `TRANSACTION_ROLLED_BACK` | 409 | Revit refused the commit; nothing was left in the model |

An element that already excavates this toposolid is reported
(`alreadyExcavating: true`) and left alone rather than cut a second time. When
every id is already excavating, no transaction is opened at all — doing nothing
should not cost the user an undo step.

## `revit_get_warnings`

The warnings the model is carrying right now — Revit's own Review Warnings
list, straight off `Document.GetWarnings()`.

```json
{ "limit": 100, "offset": 0 }
```

```json
{
  "total": 37,
  "offset": 0,
  "limit": 100,
  "warnings": [
    {
      "failureDefinitionId": "d0b0d1b9-8b06-4e1a-9d21-1a9f2a6e9a11",
      "severity": "Warning",
      "message": "Toposolid and Floor overlap.",
      "failingElementIds": [210751],
      "additionalElementIds": [245473]
    }
  ]
}
```

`failureDefinitionId` is the GUID of the failure definition, which is the only
stable identity Revit gives a warning kind — the message text is localised and
reworded between releases. `failingElementIds` is what the warning is about;
`additionalElementIds` is the other half of a two-element warning (the second of
the overlapping elements).

**This is not `revit_diagnostics`.** That one is what the bridge suppressed on
your behalf *during your writes*. This one is the standing state of the model,
including everything that was already wrong before the session started. Paged
like the other list endpoints: `total` is the real count, a `limit` over 500 is
clamped rather than refused.

## `revit_list_view_templates`

The view templates in the document, with the parameters each one **controls**.

```json
[
  {
    "id": 1204,
    "name": "Site Plan",
    "viewType": "FloorPlan",
    "controlledParameterCount": 2,
    "controlledParameters": [
      { "id": -1006952, "label": "View Scale" },
      { "id": 883122, "label": "Phase" }
    ]
  }
]
```

Check this before setting a scale, a display style or a category override on a
view: a parameter the template controls is one the view cannot hold its own
value for, which is why a setting you wrote reads back as something else.

`controlledParameters` is `GetTemplateParameterIds()` minus
`GetNonControlledTemplateParameterIds()`. A built-in parameter has a negative id
and its label comes from `LabelUtils`; a project or shared parameter has a
positive one and is named by its `ParameterElement`. A label Revit refuses to
produce comes back `null` rather than the id being dropped.

## Transaction safety (applies to every write in the bridge)

Two central changes went in with these tools, in
`revit-bridge/handlers/src/Endpoints/RevitWrite.cs` and
`revit-bridge/handlers/src/Endpoints/StrictFailures.cs`. They affect **every**
write endpoint, not only `elements/move`.

1. **The commit status is checked.** `Transaction.Commit()` and
   `TransactionGroup.Assimilate()` both return a `TransactionStatus` and both
   can answer `RolledBack` *without throwing*. Ignoring that return is how a
   caller gets a 200 for a write that never happened. Anything but `Committed`
   is now raised as `TRANSACTION_ROLLED_BACK` (409), carrying the transaction
   name, the status and whatever failures Revit raised on the way past.

2. **Errors roll back; they are no longer "resolved".** The previous
   preprocessor asked `HasResolutions()` *before* looking at the severity and
   called `ResolveFailure` whenever the answer was yes. Revit's default
   resolution for many failures is *delete the offending elements*, so an error
   raised by an insert was recorded as `Resolved (Default)` while the elements
   that caused it were deleted — and the commit still reported success.

   `StrictFailuresPreprocessor` looks at severity first and never resolves
   anything:

   - a **warning** is dismissed with `DeleteWarning` and recorded. Dismissing a
     warning cannot change the model; applying Revit's resolution can.
   - anything **else** (Error, DocumentCorruption) is recorded and answered with
     `ProceedWithRollBack`. With `SetClearAfterRollback(true)` on the
     transaction, the failures go back with it and no dialog is raised, so an
     unattended session still never stalls.

   Both paths still land in `BridgeDiagnostics`, so `revit_diagnostics` remains
   the record of what was suppressed. A rolled-back write now shows up there as
   `RolledBack` instead of `Resolved (Default)`.

The trade is deliberate: a write that Revit would only accept by deleting
something now fails loudly instead of succeeding quietly. The loader's
`BridgeFailuresPreprocessor` is left untouched (the loader is resident and
cannot be reloaded) and is simply no longer used.
