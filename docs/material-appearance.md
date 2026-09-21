# Material appearance

Two tools for the look a material renders with, as opposed to the shading colour
`revit_create_material` sets. They exist because "this stucco is too pale" and
"take the orange out of the window frames" are edits to a material that already
exists, and nothing else in this MCP can make one.

| Tool | Endpoint | Writes? |
| --- | --- | --- |
| `revit_get_material_appearance` | `POST /revit-mcp/materials/appearance` | no |
| `revit_set_material_appearance` | `POST /revit-mcp/materials/set-appearance` | yes, one undo step |

## The two things to know before calling either

**An appearance asset has no fixed set of properties.** What it carries depends
on the SCHEMA it was built from. A `Generic` asset has `generic_diffuse`,
`generic_glossiness` and `generic_transparency`; a `Ceramic` one has
`ceramic_color` and no `generic_` anything; a `Water` one has neither. So there
is no property name to guess and none to memorise: read the asset, then patch
what it lists. A name the schema does not have fails the call.

**The asset is copied before it is patched.** Two materials very often point at
one `AppearanceAssetElement`, and Revit gives no warning when editing it repaints
both. `duplicate` defaults to true for that reason, and `"duplicate": false` is
refused outright when anybody else shares the asset. There is deliberately no
way to force a shared edit.

## `revit_get_material_appearance`

Read-only: it opens no transaction and changes nothing.

```json
{ "material_id": 4101 }
```

`material_name` works as the alternative. The reply has three parts: the
material's shading side, the asset it points at, and the asset's properties.

```json
{
  "materialId": 4101,
  "materialName": "Reboco claro",
  "colorRgb": { "r": 236, "g": 230, "b": 214 },
  "transparency": 0,
  "shininess": 64,
  "smoothness": 50,
  "useRenderAppearanceForShading": true,
  "appearanceAssetId": 212630,
  "appearanceAssetName": "Reboco claro",
  "genericAssetAvailable": true,
  "assetSchema": "Generic",
  "assetTitle": "Generic",
  "assetLibrary": null,
  "sharedWithMaterialIds": [4102, 4103],
  "propertyCount": 31,
  "properties": [
    {
      "name": "generic_diffuse",
      "type": "Double4",
      "runtimeType": "AssetPropertyDoubleArray4d",
      "patchType": "color",
      "readOnly": true,
      "value": { "r": 236, "g": 230, "b": 214 },
      "valueDoubles": [0.925, 0.902, 0.839, 1.0],
      "valueFeet": null,
      "unit": null,
      "connectedCount": 0,
      "connected": null
    },
    {
      "name": "generic_bump_map",
      "type": "Asset",
      "runtimeType": "AssetPropertyReference",
      "patchType": null,
      "readOnly": true,
      "value": null,
      "connectedCount": 1,
      "connected": [
        {
          "schema": "UnifiedBitmap",
          "title": "Unified Bitmap",
          "bitmap": "C:\\Program Files\\Common Files\\Autodesk Shared\\Materials\\Textures\\1\\Mats\\stucco.jpg",
          "scaleFeet": { "x": 3.0, "y": 3.0 },
          "rotation": 0.0
        }
      ]
    }
  ]
}
```

- **`patchType`** is the answer to the only question a caller really has: which
  of the five patch types can write this property, or `null` when none of them
  can (a list, a nested property set, a distance). It is what you match `type`
  against when you write the patch.
- **`readOnly`** is *not* that test. Revit hands the rendering asset out
  read-only outside an edit scope, so it is true for essentially every property
  of every asset. `revit_set_material_appearance` is the test, because it
  validates inside an `AppearanceAssetEditScope` where `IsValidValue` is legal.
- **`sharedWithMaterialIds`** is every OTHER material pointing at the same asset.
  Non-empty means an in-place edit would repaint them too.
- **`connected`** summarises what is plugged into a property: the bitmap's file,
  its tile size in feet and its rotation. A connected texture is what renders,
  which is why a colour patch under one is refused.
- **`valueDoubles`** is the raw 0-1 colour as the asset holds it; `value` is the
  same colour as 0-255 channels. Both are reported so neither has to be assumed.

### A material with no appearance asset

`appearanceAssetId` comes back `null`, `properties` is empty, and a `note` says
what to do. This is the normal state of a material created through the API: it
renders from `Color` and `Transparency` alone. It does **not** mean the material
needs a bitmap. `genericAssetAvailable` says up front whether Revit's libraries
can supply a Generic asset on this machine.

```json
{
  "materialId": 4104,
  "materialName": "Água",
  "colorRgb": { "r": 60, "g": 110, "b": 140 },
  "transparency": 80,
  "appearanceAssetId": null,
  "genericAssetAvailable": true,
  "sharedWithMaterialIds": [],
  "propertyCount": 0,
  "properties": [],
  "note": "This material has no AppearanceAssetElement, which is the normal state of a material created through the API ..."
}
```

## `revit_set_material_appearance`

Every patch is `{name, type, value}`. The `type` is checked against the
property's real runtime type and the value against Revit's own `IsValidValue`,
inside the edit scope. One patch that does not fit rolls the WHOLE request back:
the scope is cancelled, the transaction never commits, and the transaction group
undoes the asset swap on the way out. It never reports success for a change that
did not happen.

| `type` | Written to | `value` |
| --- | --- | --- |
| `color` | `AssetPropertyDoubleArray4d` | `{ "r": 0-255, "g": 0-255, "b": 0-255 }`, stored as doubles 0-1 |
| `double` | `AssetPropertyDouble`, `AssetPropertyFloat` | a number |
| `integer` | `AssetPropertyInteger`, `AssetPropertyEnum` | a whole number |
| `boolean` | `AssetPropertyBoolean` | `true` / `false` |
| `string` | `AssetPropertyString` | text |

Booleans are the one type Revit offers no `IsValidValue` for; the reply says so
per patch, in `validated`.

### Which asset gets patched

In order of precedence:

1. `create_generic: true` builds a new asset from Revit's library `Generic`
   asset, assigns it to the material and patches that. This is the route for a
   material whose `appearanceAssetId` is null. No bitmap is involved.
2. `source_appearance_asset_id` duplicates that asset and assigns the **copy**
   to this material. The source is never touched, so the material it belongs to
   keeps its look exactly.
3. Otherwise the material's own asset, duplicated first (`duplicate` defaults to
   true). `"duplicate": false` edits in place and is refused when the asset is
   shared.

The two flags that only do something when explicitly true:

- **`disconnect_texture`** removes a bitmap connected to a property being patched
  as a colour. Without it, a colour patch on a property carrying a texture is
  refused rather than written underneath where nothing would render it.
- **`sync_shading_color`** also copies the first patched colour onto
  `Material.Color`, which is what SHADED views draw when
  `useRenderAppearanceForShading` is false. Otherwise the shading colour the
  material had is preserved: Revit repaints it to match a newly assigned asset
  (black, for a fresh Generic one), and this endpoint puts it back.

### Reply

```json
{
  "materialId": 4101,
  "materialName": "Reboco claro",
  "previousAppearanceAssetId": 212630,
  "previousSharedWithMaterialIds": [4102, 4103],
  "assetAction": "duplicated",
  "assetReason": "the asset was duplicated before it was patched, which is the default, ...",
  "appearanceAssetId": 212645,
  "appearanceAssetName": "Reboco claro 2",
  "assetSchema": "Generic",
  "applied": [
    {
      "name": "generic_diffuse",
      "type": "color",
      "assetPropertyType": "Double4",
      "runtimeType": "AssetPropertyDoubleArray4d",
      "disconnectedTexture": false,
      "validated": true,
      "value": { "r": 198, "g": 186, "b": 162 }
    }
  ],
  "verified": [
    {
      "name": "generic_diffuse",
      "runtimeType": "AssetPropertyDoubleArray4d",
      "value": { "r": 198, "g": 186, "b": 162 },
      "valueDoubles": [0.776, 0.729, 0.635, 1.0],
      "connectedCount": 0
    }
  ],
  "sharedWithMaterialIds": [],
  "shadingColorRgb": { "r": 236, "g": 230, "b": 214 },
  "shadingColorSynced": false,
  "useRenderAppearanceForShading": true
}
```

`verified` is the committed asset read back, not an echo of what was asked for.
`assetAction` is one of `created-generic`, `duplicated-source`, `duplicated` or
`in-place`, each with an `assetReason` saying why. `sharedWithMaterialIds` is
computed against the RESULTING asset, so an empty list is the evidence the edit
cannot have leaked onto another material's surfaces.

## Errors

Every one of these leaves the model untouched.

| Code | Status | When |
| --- | --- | --- |
| `SHARED_APPEARANCE_ASSET` | 409 | `"duplicate": false` on an asset another material points at. The message names the ids. |
| `NO_APPEARANCE_ASSET` | 404 | The material has no asset, and neither `create_generic` nor `source_appearance_asset_id` was given. |
| `GENERIC_ASSET_UNAVAILABLE` | 404 | `create_generic` with no `Generic` asset in the libraries installed on this machine. |
| `BAD_REQUEST` | 400 | An unknown property name, a type that does not match the property, a value `IsValidValue` refuses, a colour patch on a property with a texture connected, a non-whole number as `integer`, an unknown patch type, no patches and nothing else to do. |

## Recipes

The property names below are what a **Generic** asset lists, which is what these
materials normally have. Read the material first and patch what the read
reports: a `Ceramic` or `Metal` asset has entirely different names, and the call
fails rather than guessing.

### Pale stucco, given some body

Read it, then darken the diffuse and take the sheen off. A stucco from Revit's
library usually has a bitmap on `generic_diffuse`, so the colour needs
`disconnect_texture` to be what renders.

```json
{
  "material_name": "Reboco claro",
  "patches": [
    { "name": "generic_diffuse", "type": "color", "value": { "r": 198, "g": 186, "b": 162 } },
    { "name": "generic_glossiness", "type": "double", "value": 0.08 }
  ],
  "disconnect_texture": true,
  "sync_shading_color": true
}
```

### Stained orange window frames, to a restrained dark finish

The frames are usually one material shared across every opening, so the default
duplicate is what keeps the edit off anything else that happened to share the
asset. A dark, slightly satin timber rather than another flat colour:

```json
{
  "material_name": "Caixilho madeira",
  "patches": [
    { "name": "generic_diffuse", "type": "color", "value": { "r": 54, "g": 48, "b": 44 } },
    { "name": "generic_glossiness", "type": "double", "value": 0.25 },
    { "name": "generic_reflectivity_at_0deg", "type": "double", "value": 0.06 }
  ],
  "disconnect_texture": true,
  "sync_shading_color": true
}
```

Check `verified` afterwards: if the frames still read orange, the orange was in
a bitmap on a property you did not patch, and the read endpoint's `connected`
rows say which one.

### Water

Water made with `revit_create_material` has no appearance asset at all, so this
is the `create_generic` route: transparency, a high gloss and a tinted diffuse
are what make it read as water rather than as blue paint.

```json
{
  "material_name": "Água",
  "create_generic": true,
  "patches": [
    { "name": "generic_diffuse", "type": "color", "value": { "r": 38, "g": 92, "b": 112 } },
    { "name": "generic_transparency", "type": "double", "value": 0.82 },
    { "name": "generic_glossiness", "type": "double", "value": 0.96 },
    { "name": "generic_reflectivity_at_90deg", "type": "double", "value": 1.0 }
  ]
}
```

### Lawn

The same route, aimed the other way: a deeper green and almost no gloss, because
a shiny lawn reads as plastic.

```json
{
  "material_name": "Relva",
  "create_generic": true,
  "patches": [
    { "name": "generic_diffuse", "type": "color", "value": { "r": 74, "g": 108, "b": 52 } },
    { "name": "generic_glossiness", "type": "double", "value": 0.05 }
  ],
  "sync_shading_color": true
}
```

### Giving a material the look of one that already works

When one material is right and another should match it without the two sharing
an asset from then on:

```json
{
  "material_name": "Reboco sul",
  "source_appearance_asset_id": 212645,
  "patches": [
    { "name": "generic_diffuse", "type": "color", "value": { "r": 188, "g": 176, "b": 154 } }
  ]
}
```

The source asset is duplicated, the copy goes on this material only, and the
patch lands on the copy. `sharedWithMaterialIds` in the reply comes back empty.
