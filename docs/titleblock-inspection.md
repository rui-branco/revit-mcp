# Title block inspection and editing

`revit_list_titleblocks` says which title block types are loaded. What is
actually printed on the sheet — the vendor logo, the consultant placeholders
somebody typed into the stock family, the label that grows to hold a long
project name — lives in the **family**, and `Document.EditFamily` is the only
way to see it from the API.

| Tool | Endpoint | Writes? |
| --- | --- | --- |
| `revit_inspect_titleblock_family` | `POST /revit-mcp/families/titleblock-inspect` | **no** |
| `revit_edit_titleblock_family` | `POST /revit-mcp/families/titleblock-edit` | yes, `dryRun` defaults **true** |

`EditFamily` hands back an *independent copy* of the family as its own
document; edits to it reach the project only through `LoadFamily`. The
inspection never calls that, and the edit calls it once, at the end, into the
**same project** — there is no `SaveAs` and no second project file anywhere in
either. Both close the copy with `Close(false)` in a `finally`, so the `.rfa`
on disk is never written.

Lengths are Revit internal units (decimal feet) like everywhere else here, so
annotation inside a title block is **paper feet**: 5 mm text reads `0.0164`.

## Reading

```json
POST /revit-mcp/families/titleblock-inspect
{ "symbolId": 28295 }
```

`symbolId` is a loaded title block **type** id. A type in any other category is
a `BAD_REQUEST`; an in-place or non-editable family is `409
FAMILY_NOT_EDITABLE`, and a family already open in a Revit tab is `409
FAMILY_NOT_OPENABLE` — all three refuse before anything is opened.

### Reply

```json
{
  "readOnly": true,
  "note": "The family was opened with Document.EditFamily ... closed without saving.",
  "symbol": { "id": 28295, "familyName": "A0 metric", "typeName": "A0 metric" },
  "family": { "id": 28290, "name": "A0 metric", "category": "Title Blocks", "isEditable": true, "isInPlace": false },
  "familyDocument": { "title": "A0 metric.rfa", "pathName": "", "isModified": false },
  "familyTypes": ["A0 metric"],
  "currentType": "A0 metric",
  "familyParameters": [
    {
      "id": 31001,
      "name": "Project Name",
      "isInstance": false,
      "isShared": false,
      "storageType": "String",
      "formula": null,
      "value": null,
      "display": null,
      "associatedElementIds": [7101]
    }
  ],
  "elementCount": 148,
  "truncated": false,
  "byClass": [{ "name": "DetailLine", "count": 96 }],
  "byCategory": [{ "name": "Generic Annotations", "count": 41 }],
  "elements": [
    {
      "id": 7101,
      "class": "TextElement",
      "category": "Generic Annotations",
      "labelOf": ["Project Name"],
      "bounds": { "min": {}, "max": {}, "center": {}, "source": "view" },
      "text": { "isTextNote": false, "value": "Project Name", "textTypeName": "8mm", "textSize": 0.0164 }
    }
  ]
}
```

Per element: `id`, `class` (the Revit API type), `category`, `name`, `typeId` /
`typeName`, the view it is drawn in, its `location`, its `bounds` (the
view-independent box first, the owner view's second — `source` says which
answered), and then by what it is:

- **text** — a plain note and a label are both `TextElement`: content,
  insertion point, box width/height, both alignments, the text type and that
  type's text size. `isTextNote` separates them — `true` is literal text
  somebody typed, `false` is the other kind.
- **image** — size and scale, plus the `ImageType`: path, source, status,
  pixel dimensions.
- **import** — whether it is linked, and the import type's name, which is the
  file it came from.
- **curve** — its line style.
- **dimension** — value, `isLocked`, segments, and the family parameter
  labelling it: the constraints holding the border together.
- **reference plane** — both ends and the normal.

### Which content is dynamic

`familyParameters[].associatedElementIds` is
`FamilyParameter.AssociatedParameters` read off the family: the elements whose
own parameters Revit has associated to that family parameter. The same
association is repeated on each element row as `labelOf`.

That is the evidence for what must be preserved and what is a literal string:
reported as the association Revit holds, not as a claim about what a reader
sees on the paper.

Element rows are capped at 2000. `elementCount` and the `byClass` /
`byCategory` tallies count every element either way, and `truncated` says when
the rows are the shorter list.

## Editing

```json
POST /revit-mcp/families/titleblock-edit
{
  "symbolId": 28295,
  "expectedFamilyName": "A0 metric",
  "removeIds": [205861, 205617],
  "textEdits": [{ "id": 205635, "text": "Escala" }],
  "labelSizes": [{ "id": 205697, "size": 0.019685 }],
  "newNotes": [
    { "text": "MORADIA", "point": { "x": 3.385827, "y": 2.591864 }, "size": 0.019685, "width": 0.393701 }
  ],
  "dryRun": true
}
```

Every id is a **family** id — the ones the inspection reports — not a project
id. Read the family first: that is what the `expectedFamilyName` guard is for,
and it is checked before the family is even opened.

`dryRun` **defaults to true**. The default call opens the family, resolves
every id and every text type against it, reports exactly what it would do and
closes the copy unsaved. Pass `dryRun: false` to apply it.

### What it refuses

- **A name that does not match** — `409 FAMILY_NAME_MISMATCH`, before opening.
- **Removing anything but a `TextNote` or an `ImageInstance`** — the stock logo
  and the literal placeholders. A label (a `TextElement` that is not a
  `TextNote`) is refused by name: it is bound to a parameter, and deleting one
  takes that content off every sheet for good. Lines, dimensions and reference
  planes hold the border together and are refused too.
- **Deletions nobody asked for** — `Document.Delete` reports dependents as well
  as the elements named; anything in that set the request did not name is `409
  UNEXPECTED_DELETION` and rolls the whole edit back.
- **Losing a label or the revision schedule** — the label ids and
  `ScheduleSheetInstance` ids are read *before* the edit and checked *inside*
  the transaction; a missing one is `409 PRESERVED_ELEMENT_DELETED` and the
  family is never loaded back.
- **Removing and editing the same id** — keeping a stock placeholder and
  retexting it is one way to put a line on every sheet; deleting it is the
  other. Asking for both is a `BAD_REQUEST` before anything is written.
- **A reload Revit will not take** — `409 FAMILY_RELOAD_FAILED`, with the copy
  discarded.

The ids that must survive come back on every call, dry run included:

```json
"preserved": { "labelIds": [205697, 205701], "scheduleInstanceIds": [205777] }
```

### Resizing text

`labelSizes` does **not** edit a text type in place. A type is shared: editing
it would resize a 13 mm label *and* everything else drawn in 13 mm. Instead a
type of the requested size is reused if the family already has one, and
otherwise the element's own type is duplicated (`MCP Text 0.0197 ft`) with its
size set, and only the elements named are pointed at the copy.

Each row reports `action`:

| `action` | meaning |
| --- | --- |
| `reuse` / `reused` | a type of that size already existed |
| `duplicate` / `duplicated` | the element's type was copied and resized |
| `refused` | Revit will not retype this element — `reason` says so, its size is unchanged, and the rest of the edit still stands |

A dry run answers `reuse` / `duplicate` for what it *would* do, and adds
`isValidType` when the target type already exists; an applied call answers
`reused` / `duplicated` / `refused` for what happened.

### Adding notes

`newNotes` draws in the family's sheet view, which is what makes it the way to
put one line — a status stamp, a drawing title — on **every** sheet using the
family at once. Retexting a placeholder that would otherwise be removed
(`textEdits`) does the same thing without adding an element; the two are
alternatives for the same id, and asking for both is refused.

The view is the one the family's existing text is already in; a family that draws text in more than one view is
ambiguous and asks for `viewId` rather than guessing. `width` is optional — omit
it for a single line sized to the text — and a width outside
`TextElement.GetMinimumAllowedWidth` / `GetMaximumAllowedWidth` for the type is
refused with the allowed range.

Sizes and points are decimal feet, and annotation in a title block is paper
feet: 6 mm is `0.019685`, 3.5 mm is `0.011483`, 3 mm is `0.009843`, and
1032 mm across the sheet is `3.385827`.

### Applying

An applied call answers `applied: true`, the rows for everything it did, the
`byClass` tally `after` the edit, and:

```json
"reload": { "familyId": 205505, "familyName": "A0 metric" }
```

That is `familyDoc.LoadFamily(project, IFamilyLoadOptions)`: the family **in
this project** becomes the edited one and every sheet using it redraws. The
load answers Revit's "this family already exists" with *take this version,
leave the project's parameter values alone* — the same as the Overwrite button.
Nothing is written to disk until the project is saved.
