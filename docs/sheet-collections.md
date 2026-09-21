# Sheet collections

Revit's own collapsible groups under Sheets in the Project Browser, as a native
`SheetCollection` element. The grouping is in the model when it opens: nothing
to set up in the UI, no browser-organisation scheme to point at a parameter.

| Tool | Endpoint | Writes? |
| --- | --- | --- |
| `revit_list_sheet_collections` | `POST /revit-mcp/sheets/collections` | no |
| `revit_set_sheet_collections` | `POST /revit-mcp/sheets/set-collections` | only with `dryRun: false` |

Collections are one level deep. A collection holds sheets; the views under a
sheet stay where Revit puts them, and there is no nesting to express.

## Reading

```json
POST /revit-mcp/sheets/collections
{}
```

```json
{
  "collectionCount": 1,
  "collections": [
    {
      "id": 512001,
      "name": "L.02",
      "sheetCount": 2,
      "sheets": [
        { "id": 4201, "number": "L.02.01", "name": "Planta geral" },
        { "id": 4202, "number": "L.02.02", "name": "Cortes" }
      ]
    }
  ],
  "unassignedSheetCount": 1,
  "unassignedSheets": [{ "id": 4203, "number": "L.03.01", "name": "Pormenores" }]
}
```

## Writing

```json
POST /revit-mcp/sheets/set-collections
{
  "collections": [
    { "name": "L.02", "sheetIds": [4201, 4202] },
    { "name": "L.03", "sheetIds": [4203] },
    { "name": "L.09", "sheetIds": [4290, 4291] }
  ],
  "dryRun": false
}
```

`dryRun` **defaults to true**: the same body without it reports the plan and
writes nothing. The MCP tool takes `sheet_ids` and `dry_run` in snake_case and
maps them.

Rules, all checked before anything is written:

- Names must be non-empty and unique in the request. Revit prohibits
  `{ } [ ] | ; < > ? ` ~` in a collection name.
- A sheet id may appear in one entry only, and must be a real `ViewSheet`.
  Assembly sheets cannot join a collection.
- A collection whose name matches **exactly** is reused, not duplicated. One
  whose membership already matches is reported `unchanged` and left alone, so
  re-running the same call is a no-op.
- Sheet numbers and names are never touched, and collections this call does not
  mention keep their members. Nothing is deleted or emptied.

Everything happens in one transaction group, so it is one undo step, and a
failure anywhere rolls the whole request back.

### Reply

```json
{
  "dryRun": false,
  "collectionCount": 2,
  "sheetCount": 3,
  "collections": [
    {
      "name": "L.02",
      "action": "created",
      "collectionId": 512001,
      "requestedSheetCount": 2,
      "sheetsBefore": [
        { "id": 4201, "number": "L.02.01", "name": "Planta geral", "collectionId": null, "collectionName": null }
      ],
      "memberSheets": [
        { "id": 4201, "number": "L.02.01", "name": "Planta geral" },
        { "id": 4202, "number": "L.02.02", "name": "Cortes" }
      ],
      "missingSheetIds": [],
      "renumberedSheets": [],
      "verified": true
    }
  ]
}
```

- `action` is `created`, `reused` or `unchanged`.
- `sheetsBefore` is the snapshot taken before the write: each sheet's number,
  name and the collection it was in.
- `memberSheets`, `missingSheetIds` and `renumberedSheets` are read back off the
  committed document after a regenerate, not echoed from the request.
  `verified` is true when every requested sheet is in the collection and no
  sheet number changed. A dry run reports the plan and the snapshot only.
