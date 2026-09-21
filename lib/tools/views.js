// Views, schedules, and what puts them on a sheet. This is the set that makes a
// sheet stop being an empty title block.
//
// Model coordinates are Revit internal units (decimal feet). Sheet coordinates
// are feet on the paper, not model feet — an A1 sheet is 1.95 x 1.38.
//
// The placement tool takes the whole batch in one call on purpose: the Revit
// side wraps it in one transaction group, so a phase of sheets is one undo step.

import { z } from "zod";

// A colour as Revit stores one: three 0-255 channels, no alpha.
const rgb = z.object({
  r: z.number().int().min(0).max(255),
  g: z.number().int().min(0).max(255),
  b: z.number().int().min(0).max(255),
});

export function registerViewTools(server, bridge) {
  server.tool(
    "revit_list_views",
    "List the non-template views in the document: id, name, view type, and whether each is already placed on a sheet. A view with a frame also carries viewDirection, rightDirection and upDirection as {x,y,z} — viewDirection is Revit's direction towards the VIEWER, so a section looking north reports {x:0,y:-1,z:0}. Sheets themselves are not listed — use revit_list_sheets for those. A view that is already on a sheet cannot be placed on another one; duplicate it first with revit_duplicate_view.",
    {},
    async () => {
      try {
        const result = await bridge.call("/views");
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_create_plan_view",
    "Create a plan view on a level — a floor plan unless 'view_family_type' names another one. 'view_family_type' is the NAME Revit shows for a view family type in this document ('Site', 'Ceiling Plan', 'Structural Plan'), not a family enum: 'Site' and 'Floor Plan' are both floor-plan-family types and only the name tells them apart. An unknown name comes back as a BAD_REQUEST listing the plan type names this document does have, so you can correct it without guessing. 'scale' is the denominator X in 1:X and the response reports the scale the view actually ended up with, which is not always the one asked for when a view template controls it. If the name is already taken, a numeric suffix is appended rather than failing — the response says what the view is actually called. One call is one undo step.",
    {
      level: z.string().min(1).describe("Level name the plan is cut on"),
      name: z.string().min(1).describe("View name. A taken name gets ' 2', ' 3', ... appended."),
      view_family_type: z
        .string()
        .min(1)
        .optional()
        .describe(
          "View family type name, e.g. 'Site' or 'Ceiling Plan'. Omit it for the first floor plan type. Only floor plan, ceiling plan, area plan and structural plan types are accepted — that is what ViewPlan.Create takes.",
        ),
      scale: z
        .number()
        .int()
        .positive()
        .optional()
        .describe("View scale denominator, e.g. 100 for 1:100. Omit it to keep the type's default."),
    },
    async ({ level, name, view_family_type, scale }) => {
      try {
        // The tool arguments stay snake_case like every other tool's; the bridge
        // reads viewFamilyType — map it here rather than on the C# side.
        const result = await bridge.call("/views/create-plan", {
          level,
          name,
          viewFamilyType: view_family_type,
          scale,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_create_drafting_view",
    "Create a drafting view: a sheet of linework and notes that is not a view of the model at all. This is what a detail sheet is made of — fill it with revit_draw_detail_lines and revit_add_text_notes, then put it on a sheet with revit_place_views_on_sheets. If the name is already taken, a numeric suffix is appended rather than failing. One call is one undo step.",
    {
      name: z.string().min(1).describe("View name. A taken name gets ' 2', ' 3', ... appended."),
      scale: z
        .number()
        .int()
        .positive()
        .optional()
        .describe("View scale denominator, e.g. 20 for 1:20. Omit it to keep the type's default."),
    },
    async ({ name, scale }) => {
      try {
        const result = await bridge.call("/views/create-drafting", { name, scale });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_create_section_view",
    "Create a section view. The geometry, precisely, all in feet (Revit internal units): 'origin' is the model point the section is centred on and the cut plane passes through it; 'direction' is the horizontal direction the section LOOKS TOWARD (z is ignored, and it is normalised, so {x:0,y:1} and {x:0,y:5} are the same); 'width' is the extent across the view along the section line, centred on origin; 'height' is the vertical extent, centred on origin, with up always +Z; 'depth' is how far in front of origin, along direction, the view sees, which is where the far clip lands. So the crop is width x height centred on origin, and the view volume runs from origin to origin + direction * depth. The response carries the created view's own viewDirection, rightDirection and upDirection as {x,y,z} precisely so you can assert the section came out the way you asked: Revit's viewDirection is the direction towards the VIEWER, so a section looking toward D reports viewDirection -D — asked {x:0,y:1}, it reports {x:0,y:-1,z:0}, and rightDirection {x:1,y:0,z:0}. That is measured against Revit, not inferred. The response also carries the crop as Revit actually made it, every number read back off the created view: 'cutPlaneOrigin' is the model point the section really cuts on and 'requestedOrigin' is the origin you asked for, echoed — equal means the cut landed where you asked, and a gap of exactly 'depth' means it did not; 'modelBounds' is the axis-aligned model box the view volume really covers, which runs origin -> origin + direction * depth. 'viewOrigin', 'cropTransform' and 'cropLocalBounds' are reported to be seen, not judged: Revit rewrites the frame into its own convention — origin moved to a corner, basisZ turned back at the viewer, depth expressed as -depth..0 whatever it was given — so a section cutting where you asked and one cutting 'depth' feet away report the SAME local pair. Judge the geometry by cutPlaneOrigin and modelBounds, never by the local numbers. A taken name gets a numeric suffix rather than failing. One call is one undo step.",
    {
      name: z.string().min(1).describe("View name. A taken name gets ' 2', ' 3', ... appended."),
      origin: z
        .object({ x: z.number(), y: z.number(), z: z.number() })
        .describe("Model point the section is centred on, in feet. The cut plane passes through it."),
      direction: z
        .object({ x: z.number(), y: z.number() })
        .describe(
          "Horizontal direction the section looks TOWARD, in plan. Normalised, so only the direction matters. The created view reports viewDirection -direction, since Revit's viewDirection points back at the viewer.",
        ),
      width: z
        .number()
        .positive()
        .describe("Extent across the view along the section line, in feet, centred on origin"),
      height: z
        .number()
        .positive()
        .describe("Vertical extent in feet, centred on origin. Up is always +Z."),
      depth: z
        .number()
        .positive()
        .describe("How far in front of origin, along direction, the view sees, in feet. The far clip lands here."),
      scale: z
        .number()
        .int()
        .positive()
        .optional()
        .describe("View scale denominator, e.g. 50 for 1:50. Omit it to keep the type's default."),
    },
    async ({ name, origin, direction, width, height, depth, scale }) => {
      try {
        const result = await bridge.call("/views/create-section", {
          name,
          origin,
          direction,
          width,
          height,
          depth,
          scale,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_list_legends",
    "List the legend views in the document: id, name and scale. Read-only, and that is the point — Revit's API cannot author the first legend in a document, so this is the set revit_create_legend has to duplicate from. An empty list means a legend has to be made once in the Revit UI (View tab > Legends > Legend), or come from the template, before any legend can be created from here.",
    {},
    async () => {
      try {
        const result = await bridge.call("/views/legends");
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_create_legend",
    "Create a legend view by duplicating one the document already has — the only way the Revit API can make a legend. There is no creation call for the FIRST legend: the API exposes no legend view type, ViewPlan.Create takes only plan types and ViewDrafting.Create refuses a Legend view family type. A document with no legend therefore fails with NO_LEGEND_TO_DUPLICATE and you should tell the user to make one legend in the Revit UI (View tab > Legends > Legend) or use a template that has one — do not substitute a drafting view and call it a legend. The copy comes through empty (Revit's Duplicate option), so fill it with revit_draw_detail_lines and revit_add_text_notes; legend components, the elements that show a real family type at scale, have no creation API at all. A taken name gets a numeric suffix rather than failing. One call is one undo step.",
    {
      name: z.string().min(1).describe("Name for the new legend. A taken name gets ' 2', ' 3', ... appended."),
      from_legend_id: z
        .number()
        .int()
        .optional()
        .describe("Id of the legend to duplicate, from revit_list_legends. Omit it for the first legend in the document."),
      scale: z
        .number()
        .int()
        .positive()
        .optional()
        .describe("View scale denominator, e.g. 50 for 1:50. Omit it to keep the source legend's scale."),
    },
    async ({ name, from_legend_id, scale }) => {
      try {
        // The tool arguments stay snake_case like every other tool's; the bridge
        // reads fromLegendId — map it here rather than on the C# side.
        const result = await bridge.call("/views/create-legend", {
          name,
          fromLegendId: from_legend_id,
          scale,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_create_3d_view",
    "Create a 3D view — an isometric by default, or a perspective camera with perspective: true. Give 'eye' and 'target' (both, or neither) to aim it: eye is where the camera stands, target is what it looks at, both x/y/z in feet (Revit internal units). The bridge builds the up and forward vectors Revit needs from those two points, so you never have to. The response carries 'modelExtents' — the bounding box {min, max, center} of everything modelled in the document — which is how you work out where to put the camera in the first place: create a view with no eye/target, read the extents, then create the one you actually want. It also reports the view's own viewDirection/rightDirection/upDirection, which is Revit's direction toward the VIEWER (so the opposite of the way the camera looks). Follow this with revit_set_view_style and revit_export_view_image to actually SEE the model. A perspective view has no view scale and 'scale' is ignored on one. A taken name gets a numeric suffix rather than failing. One call is one undo step.",
    {
      name: z.string().min(1).describe("View name. A taken name gets ' 2', ' 3', ... appended."),
      eye: z
        .object({ x: z.number(), y: z.number(), z: z.number() })
        .optional()
        .describe("Camera position in feet. Pass it together with 'target', or neither."),
      target: z
        .object({ x: z.number(), y: z.number(), z: z.number() })
        .optional()
        .describe("Point the camera looks at, in feet. Pass it together with 'eye', or neither."),
      perspective: z
        .boolean()
        .optional()
        .describe("True for a perspective camera, false (the default) for an isometric"),
      scale: z
        .number()
        .int()
        .positive()
        .optional()
        .describe("View scale denominator, e.g. 100 for 1:100. Ignored on a perspective view."),
    },
    async ({ name, eye, target, perspective, scale }) => {
      try {
        const result = await bridge.call("/views/create-3d", {
          name,
          eye,
          target,
          perspective,
          scale,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_set_view_style",
    "Set how a view is drawn, which is most of what decides whether an exported image reads as a model or as a diagram. 'style' is Wireframe, HiddenLine, Shading, ShadingWithEdges, Realistic, RealisticWithEdges, FlatColors or Rendering — Realistic is the one that shows materials and RPC content properly. 'detail_level' is Coarse, Medium or Fine. Both are read back off the view in the response, because a view template can override what you asked for. Cast shadows are NOT set here: pass 'shadows' to revit_set_view_graphics instead, which takes the same style and detail_level, probes the shadows parameter on the live view and reports what it found. Passing 'shadows' here fails with SHADOWS_HANDLED_ELSEWHERE pointing at that tool. One call is one undo step.",
    {
      view_id: z.number().int().describe("Id of the view to restyle"),
      style: z
        .string()
        .min(1)
        .describe(
          "Display style name: Wireframe, HiddenLine, Shading, ShadingWithEdges, Realistic, RealisticWithEdges, FlatColors or Rendering",
        ),
      detail_level: z
        .string()
        .min(1)
        .optional()
        .describe("Detail level: Coarse, Medium or Fine. Omit it to leave the view's own."),
      shadows: z
        .boolean()
        .optional()
        .describe(
          "Not handled here. Send it to revit_set_view_graphics, which probes whether the shadows parameter is writable on that view and says which route it took. Passing it here fails the call rather than silently doing nothing.",
        ),
    },
    async ({ view_id, style, detail_level, shadows }) => {
      try {
        const result = await bridge.call("/views/set-style", {
          viewId: view_id,
          style,
          detailLevel: detail_level,
          shadows,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_set_view_background",
    "Put a sky behind a 3D view. This is the single biggest difference between an export that reads as a visualisation and one that reads as a screenshot of Revit: a default 3D view is drawn on a flat dark slate colour, and it survives into the PNG. 'kind' is 'sky' for Revit's own sky and clouds, 'gradient' for a three-band sky you colour yourself, or 'image' for a photographic backdrop. IMPORTANT: 'sky' takes NO colours — Revit's factory for it is ViewDisplayBackground.CreateSky(), which has no parameters, and passing sky_color/horizon_color/ground_color with it fails the call rather than being ignored; use 'gradient' when you want to choose the colours. Colours are {r,g,b}, 0-255, and the gradient defaults to a daylight sky (70/130/190 sky, 205/225/240 horizon, 130/120/105 ground). Only a 3D view has a background — a plan, a section or a sheet is a BAD_REQUEST, with the message saying so. The response reports the background read back off the view after the write, and 'kind' comes back as Revit's own enum name, so asking for 'sky' reports 'SunAndClouds' — that is Revit's word for it, not a different background. Pair it with revit_hide_view_categories and revit_set_view_style before revit_export_view_image. One call is one undo step.",
    {
      view_id: z.number().int().describe("Id of the 3D view to give a background to"),
      kind: z
        .enum(["sky", "gradient", "image"])
        .describe(
          "'sky' for Revit's own sky and clouds (no colours), 'gradient' for sky/horizon/ground colours, 'image' for a file",
        ),
      sky_color: z
        .object({ r: z.number().int().min(0).max(255), g: z.number().int().min(0).max(255), b: z.number().int().min(0).max(255) })
        .optional()
        .describe("Top band of a gradient, {r,g,b} 0-255. Only valid with kind 'gradient'."),
      horizon_color: z
        .object({ r: z.number().int().min(0).max(255), g: z.number().int().min(0).max(255), b: z.number().int().min(0).max(255) })
        .optional()
        .describe("Middle band of a gradient, {r,g,b} 0-255. Only valid with kind 'gradient'."),
      ground_color: z
        .object({ r: z.number().int().min(0).max(255), g: z.number().int().min(0).max(255), b: z.number().int().min(0).max(255) })
        .optional()
        .describe("Bottom band of a gradient, {r,g,b} 0-255. Only valid with kind 'gradient'."),
      image_path: z
        .string()
        .min(1)
        .optional()
        .describe(
          "Full path of the backdrop image, required with kind 'image'. Revit reads it off disk every time it draws the view, so the file has to stay there.",
        ),
    },
    async ({ view_id, kind, sky_color, horizon_color, ground_color, image_path }) => {
      try {
        // The tool arguments stay snake_case like every other tool's; the bridge
        // reads viewId/skyColor/... — map them here rather than on the C# side.
        const result = await bridge.call("/views/set-background", {
          viewId: view_id,
          kind,
          skyColor: sky_color,
          horizonColor: horizon_color,
          groundColor: ground_color,
          imagePath: image_path,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_hide_view_categories",
    "Turn whole categories off in one view. This is what takes the level datums, section marks, elevation tags and reference planes out of a presentation view — they float through an otherwise finished isometric and mark it instantly as somebody's working view. Pass categories: ['annotation'] for the whole set at once, which is what a presentation view wants; the set is Levels, Grids, ReferencePlanes, Sections, Elevations, Cameras, SunPath and Lines. Names are case-insensitive and a literal OST_* BuiltInCategory name is accepted too, so anything outside the friendly set is still reachable. An unknown name is a BAD_REQUEST listing the accepted ones, and nothing is hidden — that is a typo worth fixing. A category Revit will not hide in that view comes back as its own row with skipped: true and a reason while the rest are still hidden, so one refusal cannot cost you the other seven. MEASURED, so you are not surprised: Cameras and SunPath refuse in every view tested on Revit 2027 — CanCategoryBeHidden is false for OST_Cameras and every OST_Sun* category, because the sun path is a view-control-bar toggle (SunAndShadowSettings.Visible), not a visibility/graphics category. Every row's 'hidden' is read back off the view, so a view template overriding what you asked for is visible rather than silent. Set hidden: false to bring a category back. One call is one undo step.",
    {
      view_id: z.number().int().describe("Id of the view to change, from revit_list_views"),
      categories: z
        .array(z.string().min(1))
        .min(1)
        .describe(
          "Category names: Levels, Grids, ReferencePlanes, Sections, Elevations, Cameras, SunPath, Lines, the shorthand 'annotation' for all of them, or any OST_* name. All in this one call.",
        ),
      hidden: z
        .boolean()
        .optional()
        .describe("True (the default) hides them; false brings them back."),
    },
    async ({ view_id, categories, hidden }) => {
      try {
        const result = await bridge.call("/views/hide-categories", {
          viewId: view_id,
          categories,
          hidden,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_override_view_categories",
    "Override the graphics of whole categories in ONE view — the other half of the Visibility/Graphics dialog, and what stops a drawing set reading as the same CAD export printed five times: paving drawn light so it sits behind the planting, planting green, context halftoned, the thing the drawing is about drawn heavy. Nothing about the model changes; it is all stored on the view. The overrides are MERGED onto what the view already has: Revit's current settings for that category are copied and only the fields you name are replaced, so asking for a colour cannot wipe a fill pattern or a line pattern somebody set in the dialog — the patterns are reported in 'before' and 'after' precisely so you can see they survived. The colours are LINE colours (Projection/Surface > Lines and Cut > Lines); surface and cut PATTERN colours are read and reported but never written here. Line weights are 1-16 or -1 to clear the override; surface_transparency is 0 (opaque) to 100. IMPORTANT — a view whose TEMPLATE owns the V/G overrides is refused with OVERRIDES_CONTROLLED_BY_TEMPLATE naming the template and the categories: Revit would accept the write, commit it and keep drawing the view the template's way, and a success message for a change nobody can see is worse than a refusal. Every category name, every value and every category's IsCategoryOverridable are checked BEFORE anything is written, so the call is all-or-nothing, and a view that has no V/G at all (a sheet, a schedule, a legend) is OVERRIDES_NOT_SUPPORTED. Visibility is untouched: 'hidden' is reported on both sides and 'hiddenPreserved' says so. dry_run DEFAULTS TO TRUE and answers with the current override plus 'would' — the exact merged override it would write. Read that, then call again with dry_run false. One applied call is one undo step for the whole batch, and 'after' is read back off the view.",
    {
      view_id: z
        .number()
        .int()
        .describe("Id of the view to override categories in, from revit_list_views"),
      overrides: z
        .array(
          z.object({
            category: z
              .string()
              .min(1)
              .describe(
                "Category display name ('Planting', 'Site', 'Topography') or a literal OST_* BuiltInCategory name",
              ),
            projection_color: rgb
              .optional()
              .describe("Projection/Surface LINE colour, {r,g,b} 0-255"),
            cut_color: rgb.optional().describe("Cut LINE colour, {r,g,b} 0-255"),
            projection_line_weight: z
              .number()
              .int()
              .min(-1)
              .max(16)
              .optional()
              .describe("Projection line weight 1-16, or -1 to clear the override"),
            cut_line_weight: z
              .number()
              .int()
              .min(-1)
              .max(16)
              .optional()
              .describe("Cut line weight 1-16, or -1 to clear the override"),
            halftone: z.boolean().optional().describe("Draw the category halftone — how context is pushed back"),
            surface_transparency: z
              .number()
              .int()
              .min(0)
              .max(100)
              .optional()
              .describe("Surface transparency percent, 0 opaque to 100 fully transparent"),
          }),
        )
        .min(1)
        .describe("One row per category, each with at least one setting. All in this one call."),
      dry_run: z
        .boolean()
        .default(true)
        .describe(
          "DEFAULTS TO TRUE. True reports each category's current override and the merged override that would be written, and changes nothing. Pass false to apply it.",
        ),
    },
    async ({ view_id, overrides, dry_run }) => {
      const rows = overrides.map((row) => ({
        category: row.category,
        projectionColor: row.projection_color,
        cutColor: row.cut_color,
        projectionLineWeight: row.projection_line_weight,
        cutLineWeight: row.cut_line_weight,
        halftone: row.halftone,
        surfaceTransparency: row.surface_transparency,
      }));

      // Checked here rather than in the schema: zod can say "these fields are
      // optional", not "at least one of them". A row naming none is a request
      // that would write nothing and report a change.
      const empty = rows.find((row) =>
        Object.keys(row).every((key) => key === "category" || row[key] === undefined),
      );
      if (empty) {
        return {
          content: [
            {
              type: "text",
              text: `Error: the overrides row for "${empty.category}" names no setting. Pass at least one of projection_color, cut_color, projection_line_weight, cut_line_weight, halftone or surface_transparency.`,
            },
          ],
        };
      }

      try {
        const result = await bridge.call("/views/override-categories", {
          viewId: view_id,
          overrides: rows,
          dryRun: dry_run,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_set_view_sun",
    "Move the sun for a view, which is most of what decides whether a shaded export reads as architecture: it sets the direction every face is lit from. Two routes, and they are two different Revit modes — pass one or the other, never both. (1) azimuth/altitude in DEGREES puts the view in Lighting mode and states the sun position directly: azimuth is a compass bearing 0-360 off project north, altitude is 0-90 above the horizon. The bridge converts to the radians Revit stores. (2) date (YYYY-MM-DD) and time (HH:MM, 24-hour) put the view in Still Image mode and let Revit compute the sun itself from the project's latitude, longitude and time zone — the honest route for 'half three on a June afternoon'. Watch two things on the date route, both measured against a live model: Revit applies the project's daylight saving rule, so 15:30 on 21 June came back stored as 14:30 while 15:30 on 21 January round-tripped exactly; and the azimuth/altitude in the response are the position Revit COMPUTED, read back off the active frame rather than echoed. IMPORTANT — SUN SETTINGS CAN BE SHARED BETWEEN VIEWS: a view either owns its settings or sits on the document's shared ones, and in the second case moving the sun here moves it in every other view sharing them. The response reports sunSettingsId and sharesSettings so you can see which: two views reporting the same sunSettingsId are one sun. Note this only aims the sun — it does not turn CAST SHADOWS on. That is revit_set_view_graphics, which probes the shadows parameter on the view and either writes it or posts Revit's own shadows command, reporting which. One call is one undo step.",
    {
      view_id: z.number().int().describe("Id of the view whose sun to move, from revit_list_views"),
      azimuth: z
        .number()
        .min(0)
        .max(360)
        .optional()
        .describe(
          "Compass bearing of the sun in degrees, 0 is north and it runs clockwise. Puts the view in Lighting mode. Not to be combined with date/time.",
        ),
      altitude: z
        .number()
        .min(-90)
        .max(90)
        .optional()
        .describe(
          "Height of the sun above the horizon in degrees. Puts the view in Lighting mode. Not to be combined with date/time.",
        ),
      date: z
        .string()
        .min(1)
        .optional()
        .describe("Date as YYYY-MM-DD, e.g. '2026-06-21'. Puts the view in Still Image mode. Not to be combined with azimuth/altitude."),
      time: z
        .string()
        .min(1)
        .optional()
        .describe("Time as HH:MM on a 24-hour clock, e.g. '15:30'. Puts the view in Still Image mode. Not to be combined with azimuth/altitude."),
    },
    async ({ view_id, azimuth, altitude, date, time }) => {
      // Checked here rather than in the schema: a raw shape cannot express
      // "one of these two groups", and a call with neither must not reach Revit.
      const angles = azimuth !== undefined || altitude !== undefined;
      const clock = date !== undefined || time !== undefined;

      if (!angles && !clock) {
        return {
          content: [
            { type: "text", text: "Error: pass azimuth and/or altitude (degrees), or date and/or time." },
          ],
        };
      }
      if (angles && clock) {
        return {
          content: [
            {
              type: "text",
              text: "Error: pass either azimuth/altitude or date/time, not both — they are two different Revit sun modes.",
            },
          ],
        };
      }

      try {
        const result = await bridge.call("/views/set-sun", {
          viewId: view_id,
          azimuth,
          altitude,
          date,
          time,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_export_view_image",
    "Export a view to a raster image on disk — the only way anything outside Revit gets to see what the model looks like. Pair it with revit_create_3d_view and revit_set_view_style. IMPORTANT: Revit appends its own suffix to the file name (' - <view type> - <view name>'), so the file it writes is NOT the path you asked for — the response reports the real absolute path under 'path' and echoes what you asked for under 'requestedPath'. Open the one under 'path'. 'path' may be a folder or a file; omit it for <your home>\\RevitProjects\\renders\\. Size is one dimension and Revit fits the other: 'width' fits horizontally (default 1600 px), 'height' fits vertically, and the width/height in the response are read out of the PNG that was written. This is NOT a photoreal render — the Revit API cannot start the raytracer at all, so what you get is the view exactly as drawn on screen, which is why the display style matters. Not an undo step: nothing in the model changes.",
    {
      view_id: z.number().int().optional().describe("Id of the view to export"),
      view_ids: z
        .array(z.number().int())
        .min(1)
        .optional()
        .describe("Ids of several views to export, each written as its own file"),
      path: z
        .string()
        .min(1)
        .optional()
        .describe(
          "Output folder, or a file path whose name Revit will append the view to. Omit it for <your home>\\RevitProjects\\renders\\.",
        ),
      width: z.number().int().positive().optional().describe("Image width in pixels. Defaults to 1600."),
      height: z
        .number()
        .int()
        .positive()
        .optional()
        .describe("Image height in pixels, used only when width is omitted"),
      format: z
        .string()
        .min(1)
        .optional()
        .describe("PNG (the default), JPEG, JPEGLossless, JPEGMedium, JPEGSmallest, BMP, TIFF or TARGA"),
    },
    async ({ view_id, view_ids, path, width, height, format }) => {
      try {
        if (view_id === undefined && !view_ids) {
          return {
            content: [{ type: "text", text: "Error: pass 'view_id' or 'view_ids'." }],
          };
        }
        const result = await bridge.call("/views/export-image", {
          viewId: view_ids ? undefined : view_id,
          viewIds: view_ids,
          path,
          width,
          height,
          format,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_duplicate_view",
    "Duplicate a view. 'detailing' picks Revit's duplicate option: Duplicate (geometry only, the default), WithDetailing (carries annotation across) or AsDependent (stays linked to the original). A taken name gets a numeric suffix rather than failing. One call is one undo step.",
    {
      view_id: z.number().int().describe("Id of the view to duplicate, from revit_list_views"),
      name: z.string().min(1).describe("Name for the copy. A taken name gets ' 2', ' 3', ... appended."),
      detailing: z
        .enum(["Duplicate", "WithDetailing", "AsDependent"])
        .optional()
        .describe("Revit's ViewDuplicateOption. Defaults to Duplicate."),
    },
    async ({ view_id, name, detailing }) => {
      try {
        const result = await bridge.call("/views/duplicate", {
          viewId: view_id,
          name,
          detailing,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_set_view_scale",
    "Set the view scale on one view or a batch of them. 'scale' is the denominator X in 1:X — 100 is 1:100 — and Revit allows 1 to 24000. Pass every view in one call: the whole batch is one undo step. A view the bridge will not scale — a schedule or a sheet, which have no view scale, a view template, whose scale belongs to every view using it, and a PERSPECTIVE 3D view, which has no scale either and comes back as PERSPECTIVE_VIEW_HAS_NO_SCALE pointing at revit_scale_perspective_crop — comes back under 'failed' with a code and a reason while the rest are still re-scaled, so one schedule in the list cannot cost you thirty plans. The scale in 'updated' is read back off each view, so a view template overriding what you asked for is visible rather than silent.",
    {
      view_id: z
        .number()
        .int()
        .optional()
        .describe("Single view id from revit_list_views. Use this or view_ids, not both."),
      view_ids: z
        .array(z.number().int())
        .min(1)
        .optional()
        .describe("View ids to re-scale, all in this one call. Use this or view_id, not both."),
      scale: z
        .number()
        .int()
        .positive()
        .describe("View scale denominator, e.g. 100 for 1:100. Revit's range is 1 to 24000."),
    },
    async ({ view_id, view_ids, scale }) => {
      // Checked here rather than in the schema: a raw shape cannot express
      // "one of these two", and a call with neither must not reach Revit.
      if (view_id === undefined && view_ids === undefined) {
        return {
          content: [
            { type: "text", text: "Error: pass either view_id (one view) or view_ids (a batch)." },
          ],
        };
      }
      if (view_id !== undefined && view_ids !== undefined) {
        return {
          content: [
            { type: "text", text: "Error: pass either view_id or view_ids, not both." },
          ],
        };
      }

      try {
        const result = await bridge.call("/views/set-scale", {
          viewId: view_id,
          viewIds: view_ids,
          scale,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_scale_perspective_crop",
    "Make ONE perspective 3D view bigger or smaller on its sheet, proportions locked. This is the tool for a perspective, because a perspective camera has no view scale: revit_set_view_scale writes the 1:X denominator and refuses a perspective with PERSPECTIVE_VIEW_HAS_NO_SCALE. It scales the view's crop box on both axes — Revit's own View3D.ScalePerspectiveCropBox, which changes the size AND the scale of the view on the sheet together — so multiplier 2 doubles it on the paper, 0.5 halves it, and the framing is identical: the same shot, printed larger. It is NOT a reframe, and that is the other tool to know: revit_set_view_crop crops to a region of the MODEL and changes what is in shot. The camera is never touched here, and the answer proves it with 'cameraUnchanged' comparing the orientation before and after. Refusals come before anything is written: NOT_A_3D_VIEW (a plan or a section re-scales with revit_set_view_scale), VIEW_IS_TEMPLATE (Revit throws on a template — name the views using it), VIEW_NOT_PERSPECTIVE (an isometric 3D view has a real scale, so use revit_set_view_scale). dry_run DEFAULTS TO TRUE and reports the view's current size with the multiplier you asked for and deliberately no predicted 'after' — the size Revit lands on is read back off an applied call, never calculated here. 'before' and 'after' carry the view's Outline in PAPER feet, the crop box, the camera and the viewport's box on the sheet, all measured. Read 'outline' and 'viewport' to judge it, NOT 'cropBox': a verified run grew a view 5.65x on the paper with the crop box's model coordinates identical either side — camera and composition are both kept, which is what this tool is for. One last thing to expect: Revit leaves the view TITLE at its old paper position, so after a large multiplier the label can sit over the enlarged image — put it back with revit_set_viewport_position (label_offset), which is a separate call because where a title belongs is a drawing decision. One applied call is one undo step.",
    {
      view_id: z
        .number()
        .int()
        .describe("Perspective 3D view id from revit_list_views"),
      multiplier: z
        .number()
        .positive()
        .describe(
          "How much bigger the view gets on the sheet. 2 doubles it, 0.5 halves it, 1 changes nothing. Must be greater than zero.",
        ),
      dry_run: z
        .boolean()
        .default(true)
        .describe(
          "DEFAULTS TO TRUE. True reports the view's current size and the multiplier asked for, and changes nothing. Pass false to apply it.",
        ),
    },
    async ({ view_id, multiplier, dry_run }) => {
      try {
        const result = await bridge.call("/views/scale-perspective-crop", {
          viewId: view_id,
          multiplier,
          dryRun: dry_run,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_place_views_on_sheets",
    "Put views on sheets. Pass every placement in one call: the whole batch is one undo step, and a placement Revit refuses comes back under 'failed' with its code while the rest still land. Schedules are handled automatically — they go on a sheet as a schedule instance, not a viewport, and you do not have to know which kind of view you are holding. x and y are sheet coordinates in FEET on the paper (an A1 sheet is 1.95 x 1.38), and default to the centre of the sheet. Failure codes: VIEW_ALREADY_PLACED (a view lives on exactly one sheet — duplicate it to place it again), CANNOT_PLACE (Revit refuses that view on that sheet).",
    {
      placements: z
        .array(
          z.object({
            sheet_id: z.number().int().describe("Sheet id from revit_list_sheets"),
            view_id: z.number().int().describe("View id from revit_list_views"),
            x: z
              .number()
              .optional()
              .describe("Sheet x in feet on the paper. Omit for the centre of the sheet."),
            y: z
              .number()
              .optional()
              .describe("Sheet y in feet on the paper. Omit for the centre of the sheet."),
          }),
        )
        .min(1)
        .describe("Placements to make, all in this one call"),
    },
    async ({ placements }) => {
      try {
        // The tool arguments stay snake_case like every other tool's; the bridge
        // reads sheetId / viewId — map them here rather than on the C# side.
        const result = await bridge.call("/sheets/place-view", {
          placements: placements.map((placement) => ({
            sheetId: placement.sheet_id,
            viewId: placement.view_id,
            x: placement.x,
            y: placement.y,
          })),
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_create_schedule",
    "Create a schedule for a category, with the named fields added in order. A field name Revit does not know for that category is not an error: it comes back under 'skippedFields', and the response then also lists 'availableFields' — the exact names that category does offer — so you can correct the call without guessing. Place the result on a sheet with revit_place_views_on_sheets, which handles schedules for you. One call is one undo step.",
    {
      category: z
        .string()
        .min(1)
        .describe("Category to schedule, e.g. 'Planting' or 'OST_LightingFixtures'"),
      name: z.string().min(1).describe("Schedule name. A taken name gets ' 2', ' 3', ... appended."),
      fields: z
        .array(z.string().min(1))
        .min(1)
        .describe("Field names to add, in order. Unknown ones are skipped and reported, not fatal."),
      scale: z
        .number()
        .int()
        .positive()
        .optional()
        .describe("View scale denominator. Schedules have no meaningful scale; omit it unless you know otherwise."),
    },
    async ({ category, name, fields, scale }) => {
      try {
        const result = await bridge.call("/schedules/create", { category, name, fields, scale });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );
}
