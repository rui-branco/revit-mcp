// Cast shadows and the view templates that carry professional graphics.
//
// Split out of views.js because cast shadows are not an ordinary parameter
// write. BuiltInParameter.GRAPHIC_DISPLAY_OPTIONS_SHADOWS exists, but whether
// it can actually be written is a property of the live view — it can be
// missing, non-integer, read-only, or owned by a view template — so the bridge
// probes it and reports what it found rather than claiming either way. When it
// is not writable the bridge posts Revit's own ID_IMAGE_SHADOW_ON/OFF command,
// which is asynchronous and unverifiable at the moment it returns, and these
// descriptions say so instead of reporting success.

import { z } from "zod";

export function registerGraphicsTools(server, bridge) {
  server.tool(
    "revit_get_view_graphics",
    "Read what a view is actually drawn with: display style, detail level, the view template that may be overriding both, the shadow and sunlight intensity sliders, the 3D background, and a full probe of the cast-shadows and photographic-exposure parameters. Nothing is remembered from an earlier write — every field is read off the view. The 'shadows' probe is the point of this tool: it reports whether the view has GRAPHIC_DISPLAY_OPTIONS_SHADOWS at all, its storage type, whether Revit calls it read-only, whether a view template controls it, and 'writable' — true, false, or null when that cannot be determined. IMPORTANT: 'on' is a boolean only when the parameter really is readable as an integer; null means UNKNOWN, never off. Use this before revit_set_view_graphics to see which route a shadows write would take. ambientLightIntensity is always null: the installed Revit API has ShadowIntensity and SunlightIntensity and no ambient equivalent.",
    {
      view_id: z.number().int().optional().describe("Id of one view to read, from revit_list_views"),
      view_ids: z
        .array(z.number().int())
        .min(1)
        .optional()
        .describe("Ids of several views to read in one call. Use this or view_id, not both."),
    },
    async ({ view_id, view_ids }) => {
      try {
        if (view_id === undefined && view_ids === undefined) {
          throw new Error("Pass view_id or view_ids.");
        }
        const result = await bridge.call("/views/graphics", {
          viewId: view_id,
          viewIds: view_ids,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_set_view_graphics",
    "Set a view's graphics, including cast shadows — the one thing revit_set_view_style cannot do. 'style' and 'detail_level' are the same values that tool takes. 'shadow_intensity' and 'sunlight_intensity' are 0-100 and are the Lighting sliders (View.ShadowIntensity / View.SunlightIntensity); ambient light has no API equivalent and asking for it fails the call rather than being ignored. 'shadows' takes one of two routes and the response says WHICH, so you are never told something was done that was not: (1) method 'parameter' — the probe proved GRAPHIC_DISPLAY_OPTIONS_SHADOWS is present, integer, not read-only and not template-controlled, so it was written in the same undo step and READ BACK; only this route reports verified true. (2) method 'posted-command' — it was not writable, so Revit's own ID_IMAGE_SHADOW_ON/OFF was posted after making that view active. A posted command runs when control returns to Revit, so that response is posted true, pending true, unverified true, verified false, and it is NOT a success report — check it with revit_get_view_graphics_command_status and by looking at the view. Method 'unavailable' means Revit refused to post it and nothing was changed. The command route acts on the ACTIVE view and Revit allows one posted command at a time, so when shadows need it, pass a single view_id. Everything else in the call is one undo step.",
    {
      view_id: z.number().int().optional().describe("Id of the view to change. Required when setting 'shadows' on a view whose parameter is not writable."),
      view_ids: z
        .array(z.number().int())
        .min(1)
        .optional()
        .describe("Ids of several views to change in one undo step. Use this or view_id, not both."),
      style: z
        .string()
        .min(1)
        .optional()
        .describe(
          "Display style name: Wireframe, HiddenLine, Shading, ShadingWithEdges, Realistic, RealisticWithEdges, FlatColors or Rendering",
        ),
      detail_level: z
        .string()
        .min(1)
        .optional()
        .describe("Detail level: Coarse, Medium or Fine. Omit it to leave the view's own."),
      shadow_intensity: z
        .number()
        .int()
        .min(0)
        .max(100)
        .optional()
        .describe("How dark cast shadows are drawn, 0-100. Revit's default is 50. This is the slider, not the switch — it changes nothing while cast shadows are off."),
      sunlight_intensity: z
        .number()
        .int()
        .min(0)
        .max(100)
        .optional()
        .describe("How strong the sunlight is, 0-100. Revit's default is 50."),
      shadows: z
        .boolean()
        .optional()
        .describe(
          "Turn cast shadows on or off. The bridge probes the view first and reports whether it wrote the parameter (verified) or posted Revit's own command (pending and unverified).",
        ),
    },
    async ({ view_id, view_ids, style, detail_level, shadow_intensity, sunlight_intensity, shadows }) => {
      try {
        if (view_id === undefined && view_ids === undefined) {
          throw new Error("Pass view_id or view_ids.");
        }
        const result = await bridge.call("/views/set-graphics", {
          viewId: view_id,
          viewIds: view_ids,
          style,
          detailLevel: detail_level,
          shadowIntensity: shadow_intensity,
          sunlightIntensity: sunlight_intensity,
          shadows,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_get_view_graphics_command_status",
    "What became of the last cast-shadows command revit_set_view_graphics posted: when it was posted, whether Revit has been idle since (which is when a posted command actually runs), and a fresh probe of the shadows parameter now, next to the one taken at the moment of posting. 'verified' is true ONLY when the parameter can be read back as an integer and matches what was asked for; when it cannot be read, verified stays false and verifiedBy is null — a posted UI command that nothing can observe is reported as unverified, not as success. Call this after a set-graphics response that said method 'posted-command'.",
    {},
    async () => {
      try {
        const result = await bridge.call("/views/graphics-command-status");
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_capture_view_template",
    "Turn a view that is already drawn the way you want into a reusable view template, and state which parameters it controls. 'mode' is the shorthand for that set: 'graphics' (the default) controls everything the template can EXCEPT the sun, the crop/camera/extents and the phase, so one template can give thirty views the same graphics while each keeps its own sun position, crop and phase; 'shadows' controls only the cast-shadows parameter; 'all' controls everything including sun, crop and phase. Pass 'parameter_ids' instead of 'mode' to state the set exactly. The response lists the controlled and not-controlled parameters by id and name, read back off the template after the write, plus every parameter 'graphics' excluded and why. BE CLEAR ABOUT WHAT THIS CAN DO: a template carries only what the source view HAD, so capturing from a view whose cast shadows are off produces a template that turns shadows on nowhere — the response reports the source view's shadows probe next to the template's so that is visible up front. One call is one undo step.",
    {
      source_view_id: z
        .number()
        .int()
        .describe("Id of the view to capture from. It must be a real view, not a template."),
      name: z.string().min(1).describe("Name for the new view template. A taken name gets ' 2', ' 3', ... appended."),
      mode: z
        .enum(["graphics", "shadows", "all"])
        .optional()
        .describe(
          "'graphics' (default) — everything except sun, crop/camera and phase. 'shadows' — only the cast-shadows parameter. 'all' — everything. Not to be combined with parameter_ids.",
        ),
      parameter_ids: z
        .array(z.number().int())
        .min(1)
        .optional()
        .describe(
          "Exact parameter ids the template should control, from a previous capture's response. Not to be combined with mode.",
        ),
    },
    async ({ source_view_id, name, mode, parameter_ids }) => {
      try {
        const result = await bridge.call("/views/capture-template", {
          sourceViewId: source_view_id,
          name,
          mode,
          parameterIds: parameter_ids,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );

  server.tool(
    "revit_apply_view_template",
    "Put one view template onto many views in a single undo step. 'mode' is the difference between the two things Revit calls applying a template: 'apply' (the default) is a ONE-TIME copy of the template's parameters and leaves no association, so the view stays freely editable; 'assign' attaches the template so it keeps controlling those parameters and they can no longer be set on the view. Assigning onto a view that already has a template would detach the old one and needs replace: true. Every target is validated BEFORE anything is written — it is a view, it is not a template or a sheet, and Revit's own IsValidViewTemplate says the template suits it — and one failure fails the whole call with all the reasons rather than leaving half the batch changed. IMPORTANT: dry_run defaults to TRUE, so a first call changes nothing and reports the plan; call again with dry_run false to write it. The response reports each view's graphics read back afterwards, which is where an 'apply' that the view's own template silently overrode becomes visible. And note what a template cannot do: if its cast shadows are off, applying it turns shadows on nowhere.",
    {
      template_id: z.number().int().describe("Id of the view template, from revit_capture_view_template or revit_list_view_templates"),
      view_ids: z
        .array(z.number().int())
        .min(1)
        .describe("Ids of the views to put it on. All in this one call — it is one undo step."),
      mode: z
        .enum(["apply", "assign"])
        .optional()
        .describe(
          "'apply' (default) copies the template's parameters once and leaves the view unassociated. 'assign' attaches the template so it keeps controlling them.",
        ),
      dry_run: z
        .boolean()
        .optional()
        .describe("Defaults to TRUE: validate and report the plan without changing anything. Pass false to write it."),
      replace: z
        .boolean()
        .optional()
        .describe("Allow 'assign' to detach a template a view already has. Without it, such a view fails the call instead.",),
    },
    async ({ template_id, view_ids, mode, dry_run, replace }) => {
      try {
        const result = await bridge.call("/views/apply-template", {
          templateId: template_id,
          viewIds: view_ids,
          mode,
          dryRun: dry_run,
          replace,
        });
        return { content: [{ type: "text", text: JSON.stringify(result) }] };
      } catch (error) {
        return { content: [{ type: "text", text: `Error: ${error.message}` }] };
      }
    },
  );
}
