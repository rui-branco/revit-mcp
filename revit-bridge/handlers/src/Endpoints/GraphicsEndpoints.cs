using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace RevitMcpBridge.Endpoints
{
    /// <summary>
    /// Cast shadows, the rest of the Graphic Display Options, and the view templates that carry
    /// them. This is the half of "make the export look like architecture" that views/set-style
    /// cannot reach.
    ///
    /// The whole file is written around one uncomfortable fact, so read this before changing any
    /// of it:
    ///
    ///   **BuiltInParameter.GRAPHIC_DISPLAY_OPTIONS_SHADOWS exists in the enum. That proves
    ///   nothing about whether it can be read or written on a view.** The member is in the
    ///   installed Revit 2027 RevitAPI.dll and in the 2025 reference assembly this project
    ///   compiles against, and Autodesk's own REVIT-222419 is the record of an enum member that
    ///   is not a usable toggle. get_Parameter for it can come back null, or come back with
    ///   StorageType.None (the Graphic Display Options dialog launcher, which is not a 0/1), or
    ///   come back read-only, or come back owned by a view template. Only the live view can say
    ///   which, so every endpoint here PROBES and reports what it found - available, storage
    ///   type, read-only, value, and whether a template controls it - rather than asserting.
    ///
    /// Where the probe cannot answer, the answer stays null. Null means UNKNOWN here, never
    /// "off": a bridge that reports false for something it could not read is worse than one that
    /// says it does not know.
    ///
    /// When the parameter turns out not to be writable there is exactly one other route, and it
    /// is not a model edit at all: Revit's own UI commands ID_IMAGE_SHADOW_ON and
    /// ID_IMAGE_SHADOW_OFF, posted with UIApplication.PostCommand. Both strings are present in
    /// Revit 2027's UIFrameworkRes.dll, DesktopMFC.dll and Utility.dll. There is no PostableCommand
    /// member for either, and the API documents PostCommand as accepting members of PostableCommand
    /// and external commands only - so CanPostCommand is asked first and a false answer is reported
    /// as "unavailable" rather than worked around. A posted command runs after control returns from
    /// the API context, against whatever view is ACTIVE, which is why the requested view is made
    /// active first, outside every transaction (Revit refuses the active-view change while the
    /// document is modifiable). Nothing about that route is verified at the moment it is posted, and
    /// nothing in the response pretends otherwise: it reports method "posted-command",
    /// pending: true, unverified: true and verified: false, and never the word success.
    ///
    /// Transactions follow the same rule as every other write endpoint - one request is one
    /// TransactionGroup and therefore one Ctrl+Z. The posted command is outside it, because it is
    /// not part of the transaction and cannot be undone with it.
    /// </summary>
    internal static class GraphicsEndpoints
    {
        /// <summary>The parameter this file exists for. Present in the enum; everything else about it is probed.</summary>
        private const BuiltInParameter ShadowsParameter = BuiltInParameter.GRAPHIC_DISPLAY_OPTIONS_SHADOWS;

        /// <summary>The Photographic Exposure entry of the same dialog, probed exactly the same way.</summary>
        private const BuiltInParameter ExposureParameter = BuiltInParameter.GRAPHIC_DISPLAY_OPTIONS_PHOTO_EXPOSURE;

        private const string ShadowOnCommand = "ID_IMAGE_SHADOW_ON";
        private const string ShadowOffCommand = "ID_IMAGE_SHADOW_OFF";

        /// <summary>
        /// Guards the posted-command state. Revit allows one posted command at a time - PostCommand
        /// throws InvalidOperationException for a second - so the bridge refuses to pile a second
        /// one on rather than letting Revit throw.
        /// </summary>
        private static readonly object CommandGate = new object();

        private static PendingShadowCommand _lastCommand;
        private static EventHandler<IdlingEventArgs> _idleWatcher;

        /// <summary>
        /// Body: {viewId} or {viewIds: [...]}. What each view is actually drawn with, read off the
        /// view rather than remembered: display style, detail level, the view template that may be
        /// overriding both, the two Lighting sliders, the background, and a full probe of the
        /// shadows and exposure parameters.
        ///
        /// Nothing here is echoed back from a previous write. "ambientLightIntensity" is always
        /// null and says so in "notes": the installed Revit 2027 RevitAPI.dll has no
        /// View.AmbientLightIntensity and no ambient-light BuiltInParameter on a view - only
        /// ShadowIntensity and SunlightIntensity exist.
        /// </summary>
        internal static object Graphics(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            List<long> viewIds = RequireViewIds(body);

            List<object> rows = new List<object>();

            foreach (long viewId in viewIds)
            {
                rows.Add(ReadGraphics(document, RequireView(document, viewId)));
            }

            return new Dictionary<string, object>
            {
                { "views", rows },
                { "notes", ReadNotes() },
            };
        }

        /// <summary>
        /// Body: {viewId | viewIds, style?, detailLevel?, shadowIntensity?, sunlightIntensity?,
        /// shadows?}. Everything a view's graphics can be set to from the API in one undo step,
        /// plus the honest attempt at cast shadows.
        ///
        /// "shadowIntensity" and "sunlightIntensity" are integers 0-100 and go straight onto
        /// View.ShadowIntensity / View.SunlightIntensity, which are real, documented, settable
        /// properties. "ambientLightIntensity" is refused rather than ignored - there is no such
        /// property and no such parameter.
        ///
        /// "shadows" is the interesting one, and it takes whichever of two routes the live view
        /// allows:
        ///   1. The parameter route, used ONLY when the probe proves it: the view really has
        ///      GRAPHIC_DISPLAY_OPTIONS_SHADOWS, its storage type really is Integer, Revit does not
        ///      report it read-only, and no view template controls it. Then it is set inside the
        ///      transaction with the rest and read back, and the response can honestly say
        ///      verified: true.
        ///   2. The command route, when it cannot. Revit's own ID_IMAGE_SHADOW_ON /
        ///      ID_IMAGE_SHADOW_OFF is looked up, gated on CanPostCommand, the target view is made
        ///      active, and the command is posted AFTER the transaction group has closed. It runs
        ///      when control returns to Revit, so the response reports it as posted and pending and
        ///      explicitly unverified. Exactly one command may be posted per request, so this route
        ///      needs the request to name exactly one view.
        /// </summary>
        internal static object SetGraphics(UIApplication app, JsonElement body)
        {
            UIDocument uiDocument = RevitFacts.RequireUiDocument(app);
            Document document = uiDocument.Document;

            if (document.IsFamilyDocument)
            {
                throw BridgeException.NotAProjectDocument();
            }

            List<long> viewIds = RequireViewIds(body);

            string style = JsonBody.OptionalString(body, "style");
            string detailLevelName = JsonBody.OptionalString(body, "detailLevel");
            int shadowIntensity = OptionalIntensity(body, "shadowIntensity");
            int sunlightIntensity = OptionalIntensity(body, "sunlightIntensity");

            JsonElement ambient;
            if (JsonBody.TryGet(body, "ambientLightIntensity", out ambient))
            {
                throw new BridgeException(
                    409,
                    "AMBIENT_LIGHT_NOT_EXPOSED",
                    "Revit exposes no ambient-light intensity to the API. Checked against the "
                        + "installed Revit 2027 RevitAPI.dll: View has ShadowIntensity and "
                        + "SunlightIntensity and no AmbientLightIntensity, and there is no ambient "
                        + "BuiltInParameter on a view either. This is refused rather than accepted "
                        + "and ignored; drop \"ambientLightIntensity\" and retry.");
            }

            JsonElement shadowsValue;
            bool hasShadows = JsonBody.TryGet(body, "shadows", out shadowsValue);
            bool shadows = hasShadows && JsonBody.AsBool(shadowsValue, "shadows");

            if (style == null && detailLevelName == null && shadowIntensity < 0
                && sunlightIntensity < 0 && !hasShadows)
            {
                throw BridgeException.BadRequest(
                    "Nothing to set. Pass \"style\", \"detailLevel\", \"shadowIntensity\", "
                        + "\"sunlightIntensity\" (0-100) and/or \"shadows\".");
            }

            DisplayStyle displayStyle = style == null
                ? DisplayStyle.Undefined
                : ViewEndpoints.ResolveDisplayStyle(style);
            ViewDetailLevel detailLevel = ViewEndpoints.ResolveDetailLevel(detailLevelName);

            List<View> views = new List<View>();
            foreach (long viewId in viewIds)
            {
                View view = RequireView(document, viewId);

                if (style != null && !view.CanModifyDisplayStyle())
                {
                    throw BridgeException.BadRequest(
                        "Revit will not set the display style of view \"" + RevitFacts.SafeName(view)
                            + "\" (" + viewId + ", " + view.ViewType + "). A schedule, a sheet and a "
                            + "legend have no display style.");
                }

                views.Add(view);
            }

            // Probed before anything is written: this is what decides which shadows route is even
            // legal, and the report carries it so the choice is inspectable rather than magic.
            Dictionary<long, Dictionary<string, object>> probes =
                new Dictionary<long, Dictionary<string, object>>();
            foreach (View view in views)
            {
                probes[view.Id.Value] = Probe(document, view, ShadowsParameter);
            }

            bool fallbackNeeded = false;
            if (hasShadows)
            {
                foreach (View view in views)
                {
                    object writable = probes[view.Id.Value]["writable"];
                    if (!(writable is bool) || !(bool)writable)
                    {
                        fallbackNeeded = true;
                    }
                }

                if (fallbackNeeded && views.Count > 1)
                {
                    throw BridgeException.BadRequest(
                        "Cast shadows cannot be written as a parameter on at least one of these "
                            + views.Count + " views, so the bridge would have to fall back to "
                            + "posting Revit's own ID_IMAGE_SHADOW_ON/OFF command - and that acts "
                            + "on the ACTIVE view, one command per request. Call "
                            + "/revit-mcp/views/set-graphics once per view with a single \"viewId\" "
                            + "when \"shadows\" is in the request, or call /revit-mcp/views/graphics "
                            + "first to see which views can take the parameter route.");
                }
            }

            bool writesModel = style != null
                || detailLevelName != null
                || shadowIntensity >= 0
                || sunlightIntensity >= 0
                || (hasShadows && !fallbackNeeded);

            Dictionary<string, object> result;

            if (writesModel)
            {
                result = RevitWrite.InGroup(document, "MCP: set view graphics", delegate
                {
                    RevitWrite.InTransaction(document, "Set view graphics", delegate
                    {
                        foreach (View view in views)
                        {
                            if (style != null)
                            {
                                view.DisplayStyle = displayStyle;
                            }

                            if (detailLevelName != null)
                            {
                                view.DetailLevel = detailLevel;
                            }

                            if (shadowIntensity >= 0)
                            {
                                view.ShadowIntensity = shadowIntensity;
                            }

                            if (sunlightIntensity >= 0)
                            {
                                view.SunlightIntensity = sunlightIntensity;
                            }

                            if (hasShadows && !fallbackNeeded)
                            {
                                // Reached only when the probe proved all four conditions on THIS
                                // view. Anything less and we are on the command route instead.
                                view.get_Parameter(ShadowsParameter).Set(shadows ? 1 : 0);
                            }
                        }
                    });

                    return ReadBackRows(document, views);
                });
            }
            else
            {
                result = ReadBackRows(document, views);
            }

            if (hasShadows)
            {
                result["shadows"] = fallbackNeeded

                    // Outside the group on purpose: Revit refuses to change the active view while
                    // the document is modifiable, and a posted command is not part of the undo step.
                    ? PostShadowCommand(app, uiDocument, views[0], shadows, probes[views[0].Id.Value])
                    : ParameterRoute(document, views, shadows);
            }

            return result;
        }

        /// <summary>
        /// Body: {}. READ-ONLY. Asks Revit which of the three shadow-related UI commands it will
        /// accept, and whether the pieces a dialog-driving fallback would need are present. It
        /// looks commands up and asks CanPostCommand; it posts nothing, opens nothing, and touches
        /// no UI.
        ///
        /// This exists because CanPostCommand answered false for ID_IMAGE_SHADOW_ON on a live view,
        /// which makes the posted-command route unavailable on this build and raises the question
        /// of whether Revit's Graphic Display Options dialog could be opened and driven instead.
        /// The answer to the first half of that is already no and does not need a live call -
        /// PostableCommand has 588 members in Revit 2027 and not one is the Graphic Display
        /// Options dialog, so there is no supported way to open it. The ids are still asked about
        /// here because the measurement is cheap and a guess is not.
        ///
        /// The allowlist is fixed and hard-coded on purpose. This is not a route for running
        /// arbitrary Revit commands by name and must never become one.
        /// </summary>
        internal static object ShadowCommandFeasibility(UIApplication app, JsonElement body)
        {
            List<object> commands = new List<object>();

            foreach (string name in new string[]
            {
                ShadowOnCommand,
                ShadowOffCommand,

                // The dialog that actually carries the Cast Shadows tick box.
                "ID_GRAPHIC_DISPLAY_OPTIONS",
            })
            {
                Dictionary<string, object> row = new Dictionary<string, object> { { "command", name } };

                RevitCommandId id;
                try
                {
                    id = RevitCommandId.LookupCommandId(name);
                }
                catch (Exception ex)
                {
                    id = null;
                    row["lookupError"] = ex.Message;
                }

                row["known"] = id != null;
                row["commandId"] = id == null ? null : (object)id.Id;
                row["hasBinding"] = id == null ? (object)null : id.HasBinding;

                if (id == null)
                {
                    row["canPostCommand"] = false;
                }
                else
                {
                    try
                    {
                        row["canPostCommand"] = app.CanPostCommand(id);
                    }
                    catch (Exception ex)
                    {
                        row["canPostCommand"] = false;
                        row["canPostError"] = ex.Message;
                    }
                }

                commands.Add(row);
            }

            // Compile-time facts, restated here so a caller reading this response does not have to
            // take them on trust from a document. Checked against the installed 2027 RevitAPIUI.dll.
            Dictionary<string, object> postable = new Dictionary<string, object>
            {
                { "hasGraphicDisplayOptionsMember", false },
                { "hasShadowsMember", false },
                { "note", "PostableCommand has no member for the Graphic Display Options dialog and "
                    + "none for cast shadows. The API documents PostCommand as accepting members of "
                    + "PostableCommand and external commands only, which is why a raw command id "
                    + "answers CanPostCommand false. There is no supported call that opens that "
                    + "dialog." },
            };

            return new Dictionary<string, object>
            {
                { "commands", commands },
                { "postableCommand", postable },
                { "uiAutomationAvailable", UiAutomationAvailable() },
                { "autoDismissDialogs", BridgeDiagnostics.AutoDismiss },
                { "note", "Read-only. Nothing was posted and no UI was touched. autoDismissDialogs "
                    + "matters to any dialog-driving plan: while it is true the bridge answers every "
                    + "DialogBoxShowing with OverrideResult, so a Graphic Display Options dialog "
                    + "would be closed the instant it appeared." },
            };
        }

        /// <summary>
        /// Whether UIAutomationClient can be resolved in this process, asked by type name so that
        /// nothing is referenced at compile time and no UI is inspected. A true here says only that
        /// the assembly exists - it is not a statement that driving Revit's dialogs is workable.
        /// </summary>
        private static object UiAutomationAvailable()
        {
            try
            {
                Type type = Type.GetType(
                    "System.Windows.Automation.AutomationElement, UIAutomationClient",
                    false);

                return type != null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Body: {}. What became of the last ID_IMAGE_SHADOW_ON/OFF this session posted: when it
        /// went, whether Revit has been idle since (which is when a posted command runs), what the
        /// shadows parameter said at the moment of posting, at that first idle, and right now.
        ///
        /// "verified" is true only when the parameter can actually be READ back as an integer and
        /// matches what was asked for. When it cannot be read, verified stays false with
        /// verifiedBy null - a posted UI command that nothing can observe is exactly the case this
        /// endpoint must not paper over.
        /// </summary>
        internal static object CommandStatus(UIApplication app, JsonElement body)
        {
            PendingShadowCommand pending;
            lock (CommandGate)
            {
                pending = _lastCommand;
            }

            if (pending == null)
            {
                return new Dictionary<string, object>
                {
                    { "posted", false },
                    { "pending", false },
                    { "verified", false },
                    { "note", "No cast-shadows command has been posted in this Revit session. "
                        + "/revit-mcp/views/set-graphics posts one only when the shadows parameter "
                        + "turns out not to be writable on the view." },
                };
            }

            Dictionary<string, object> status = new Dictionary<string, object>
            {
                { "posted", true },
                { "command", pending.Command },
                { "requested", pending.Requested },
                { "viewId", pending.ViewId },
                { "postedAtUtc", pending.PostedAtUtc },
                { "idleSeenAtUtc", pending.ObservedAtUtc },

                // Pending until Revit has been idle at least once since the post - that is the
                // moment a posted command gets to run.
                { "pending", pending.ObservedAtUtc == null },
                { "probeAtPost", pending.ProbeAtPost },
                { "probeAtIdle", pending.ProbeAtIdle },
            };

            Dictionary<string, object> now = null;

            Document document = null;
            try
            {
                document = RevitFacts.RequireDocument(app);
            }
            catch (BridgeException)
            {
                // No document open any more. The record still stands; the live probe does not.
                document = null;
            }

            if (document != null)
            {
                View view = document.GetElement(new ElementId(pending.ViewId)) as View;
                if (view != null)
                {
                    now = Probe(document, view, ShadowsParameter);
                }
            }

            status["probeNow"] = now;

            object on = now == null ? null : now["on"];
            if (on is bool)
            {
                status["verified"] = (bool)on == pending.Requested;
                status["verifiedBy"] = "parameter-readback";
            }
            else
            {
                status["verified"] = false;
                status["verifiedBy"] = null;
                status["note"] = "The shadows parameter cannot be read back as an integer on this "
                    + "view, so whether the posted command did anything is UNVERIFIED. Look at the "
                    + "view in Revit, or export an image and look at that - nothing here can "
                    + "confirm it.";
            }

            return status;
        }

        /// <summary>
        /// Body: {sourceViewId, name, mode?, parameterIds?}. Turns a view that is already drawn the
        /// way you want into a reusable view template, and - the part that makes it a template
        /// rather than a snapshot - says which parameters it controls.
        ///
        /// View.CreateViewTemplate() copies the source view, and the new template starts out
        /// controlling Revit's own default set. What it controls is then stated explicitly through
        /// SetNonControlledTemplateParameterIds, from either "parameterIds" (exact ids, intersected
        /// with what this template can control at all) or "mode":
        ///   - "graphics" (the default): everything the template can control EXCEPT the sun
        ///     (VIEW_GRAPH_SUN*, VIEW_SOLARSTUDY*), the crop, camera and view extents (VIEWER_*),
        ///     and the phase (VIEW_PHASE, VIEW_PHASE_FILTER). That is what lets one template give
        ///     thirty views the same graphics while each keeps its own sun, crop and phase.
        ///   - "shadows": GRAPHIC_DISPLAY_OPTIONS_SHADOWS and nothing else.
        ///   - "all": everything the template can control, sun and crop and phase included.
        /// Every excluded id comes back with the reason, and the controlled set is read back off
        /// the template after the write, so this is auditable rather than declared.
        ///
        /// A template carries only what the source view HAD. The response therefore reports the
        /// source view's shadows probe next to the template's own: capturing from a view whose
        /// cast shadows are off produces a template that turns nothing on, and that has to be
        /// visible here rather than discovered later.
        /// </summary>
        internal static object CaptureTemplate(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            long sourceViewId = JsonBody.AsLong(RequireValue(body, "sourceViewId"), "sourceViewId");
            string name = JsonBody.RequireString(body, "name");
            string mode = JsonBody.OptionalString(body, "mode");
            List<long> parameterIds = OptionalIds(body, "parameterIds");

            if (mode != null && parameterIds != null)
            {
                throw BridgeException.BadRequest(
                    "Pass either \"mode\" or \"parameterIds\", not both: \"parameterIds\" states the "
                        + "controlled set exactly and \"mode\" is the shorthand for one.");
            }

            if (mode == null)
            {
                mode = "graphics";
            }

            if (parameterIds == null && !IsKnownCaptureMode(mode))
            {
                throw BridgeException.BadRequest(
                    "Unknown \"mode\" value \"" + mode + "\". Use \"graphics\" (everything except "
                        + "sun, crop/camera and phase), \"shadows\" (only "
                        + "GRAPHIC_DISPLAY_OPTIONS_SHADOWS) or \"all\".");
            }

            View source = RequireView(document, sourceViewId);

            if (source.IsTemplate)
            {
                throw BridgeException.BadRequest(
                    "View " + sourceViewId + " (\"" + RevitFacts.SafeName(source) + "\") is already a "
                        + "view template. Capture from a real view that is drawn the way you want.");
            }

            if (!source.IsViewValidForTemplateCreation())
            {
                throw BridgeException.BadRequest(
                    "Revit will not make a view template from view \"" + RevitFacts.SafeName(source)
                        + "\" (" + sourceViewId + ", " + source.ViewType
                        + "): View.IsViewValidForTemplateCreation is false for it.");
            }

            Dictionary<string, object> sourceShadows = Probe(document, source, ShadowsParameter);

            return RevitWrite.InGroup(document, "MCP: capture view template", delegate
            {
                View template = null;

                RevitWrite.InTransaction(document, "Create view template", delegate
                {
                    template = source.CreateViewTemplate();
                    ViewEndpoints.ApplyName(document, template, name);
                });

                // A second transaction rather than a Regenerate: the template is a fully realised
                // element only once the first one has committed, and that is what
                // GetTemplateParameterIds has to be asked about. Both collapse into one Ctrl+Z.
                List<ElementId> all = new List<ElementId>(template.GetTemplateParameterIds());
                List<ElementId> controlled = new List<ElementId>();
                List<object> excluded = new List<object>();
                List<long> ignored = new List<long>();

                if (parameterIds != null)
                {
                    foreach (ElementId id in all)
                    {
                        if (parameterIds.Contains(id.Value))
                        {
                            controlled.Add(id);
                        }
                    }

                    foreach (long id in parameterIds)
                    {
                        if (!ContainsValue(all, id))
                        {
                            ignored.Add(id);
                        }
                    }
                }
                else if (string.Equals(mode, "all", StringComparison.OrdinalIgnoreCase))
                {
                    controlled.AddRange(all);
                }
                else if (string.Equals(mode, "shadows", StringComparison.OrdinalIgnoreCase))
                {
                    long shadowsId = new ElementId(ShadowsParameter).Value;

                    foreach (ElementId id in all)
                    {
                        if (id.Value == shadowsId)
                        {
                            controlled.Add(id);
                        }
                    }
                }
                else
                {
                    foreach (ElementId id in all)
                    {
                        string reason = GraphicsExclusionReason(id);

                        if (reason == null)
                        {
                            controlled.Add(id);
                        }
                        else
                        {
                            excluded.Add(new Dictionary<string, object>
                            {
                                { "id", id.Value },
                                { "name", ParameterName(document, id) },
                                { "reason", reason },
                            });
                        }
                    }
                }

                List<ElementId> nonControlled = new List<ElementId>();
                foreach (ElementId id in all)
                {
                    if (!ContainsValue(controlled, id.Value))
                    {
                        nonControlled.Add(id);
                    }
                }

                RevitWrite.InTransaction(document, "Set view template controlled parameters", delegate
                {
                    template.SetNonControlledTemplateParameterIds(nonControlled);
                });

                // Read back off the template, not echoed: what Revit kept is the only set worth
                // reporting, and SetNonControlledTemplateParameterIds ignores ids it does not know.
                List<ElementId> nonControlledAfter =
                    new List<ElementId>(template.GetNonControlledTemplateParameterIds());

                List<object> controlledRows = new List<object>();
                List<object> nonControlledRows = new List<object>();

                foreach (ElementId id in all)
                {
                    Dictionary<string, object> row = new Dictionary<string, object>
                    {
                        { "id", id.Value },
                        { "name", ParameterName(document, id) },
                    };

                    if (ContainsValue(nonControlledAfter, id.Value))
                    {
                        nonControlledRows.Add(row);
                    }
                    else
                    {
                        controlledRows.Add(row);
                    }
                }

                return new Dictionary<string, object>
                {
                    { "templateId", template.Id.Value },

                    // Read back: a taken name gets a numeric suffix rather than failing the call.
                    { "name", RevitFacts.SafeName(template) },
                    { "viewType", template.ViewType.ToString() },
                    { "sourceViewId", sourceViewId },
                    { "sourceViewName", RevitFacts.SafeName(source) },
                    { "mode", parameterIds == null ? mode : null },
                    { "controlled", controlledRows },
                    { "notControlled", nonControlledRows },
                    { "excluded", excluded },
                    { "ignoredParameterIds", ignored },
                    { "shadows", CapturedShadows(document, source, template, sourceShadows) },
                };
            });
        }

        /// <summary>
        /// Body: {templateId, viewIds, mode?, dryRun?, replace?}. Puts one template onto many
        /// views, and refuses to do half of it.
        ///
        /// Every target is validated BEFORE anything is written - the element is a view, it is not
        /// a template or a sheet, and View.IsValidViewTemplate says the template is compatible with
        /// it - and one failure fails the whole request with all the reasons, rather than leaving
        /// nineteen views changed and one not. The write itself is one transaction inside one group,
        /// so it is one Ctrl+Z.
        ///
        /// "mode" is the difference between the two things Revit calls applying a template:
        ///   - "apply" (the default) is View.ApplyViewTemplateParameters, a ONE-TIME copy. No
        ///     association survives it and the view is left with no template assigned.
        ///   - "assign" sets View.ViewTemplateId, a lasting association: the template keeps
        ///     controlling those parameters and they can no longer be set on the view.
        /// "assign" onto a view that already has a template would detach the old one, so it needs
        /// replace: true. "apply" never detaches anything, but a view that already has a template
        /// will not take the parameters that template controls - that view's row says so.
        ///
        /// dryRun defaults to TRUE. A call with no dryRun changes nothing and reports the plan.
        /// </summary>
        internal static object ApplyTemplate(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            long templateId = JsonBody.AsLong(RequireValue(body, "templateId"), "templateId");
            List<long> viewIds = JsonBody.RequireIds(body, "viewIds");
            string mode = JsonBody.OptionalString(body, "mode");
            bool dryRun = JsonBody.OptionalBool(body, "dryRun", true);
            bool replace = JsonBody.OptionalBool(body, "replace", false);

            if (mode == null)
            {
                mode = "apply";
            }

            bool assign = string.Equals(mode, "assign", StringComparison.OrdinalIgnoreCase);

            if (!assign && !string.Equals(mode, "apply", StringComparison.OrdinalIgnoreCase))
            {
                throw BridgeException.BadRequest(
                    "Unknown \"mode\" value \"" + mode + "\". Use \"apply\" for a one-time copy of "
                        + "the template's parameters, or \"assign\" to attach the template so it "
                        + "keeps controlling them.");
            }

            View template = document.GetElement(new ElementId(templateId)) as View;
            if (template == null)
            {
                throw BridgeException.BadRequest(
                    "Element " + templateId + " is not a view in this document, so it cannot be a "
                        + "view template.");
            }

            if (!template.IsTemplate)
            {
                throw BridgeException.BadRequest(
                    "View \"" + RevitFacts.SafeName(template) + "\" (" + templateId + ", "
                        + template.ViewType + ") is a view, not a view template. Make one from it "
                        + "with /revit-mcp/views/capture-template first.");
            }

            ElementId templateElementId = new ElementId(templateId);

            List<View> targets = new List<View>();
            List<object> plan = new List<object>();
            List<string> blockers = new List<string>();

            foreach (long viewId in viewIds)
            {
                View view = document.GetElement(new ElementId(viewId)) as View;

                if (view == null)
                {
                    blockers.Add("element " + viewId + " is not a view in this document");
                    continue;
                }

                string label = "view \"" + RevitFacts.SafeName(view) + "\" (" + viewId + ", "
                    + view.ViewType + ")";

                if (view.IsTemplate)
                {
                    blockers.Add(label + " is itself a view template");
                    continue;
                }

                if (view is ViewSheet)
                {
                    blockers.Add(label + " is a sheet, and a sheet takes no view template");
                    continue;
                }

                bool compatible;
                try
                {
                    compatible = view.IsValidViewTemplate(templateElementId);
                }
                catch (Exception)
                {
                    compatible = false;
                }

                if (!compatible)
                {
                    blockers.Add(label + " is not compatible with template \""
                        + RevitFacts.SafeName(template) + "\" (" + template.ViewType
                        + ") - View.IsValidViewTemplate says no, which is usually a template made "
                        + "from a different kind of view");
                    continue;
                }

                ElementId existing = SafeViewTemplateId(view);
                bool hasExisting = existing != null && existing != ElementId.InvalidElementId;

                if (assign && hasExisting && !replace)
                {
                    blockers.Add(label + " already has view template \""
                        + RevitFacts.SafeName(document.GetElement(existing)) + "\" ("
                        + existing.Value + ") assigned, and assigning this one would detach it - "
                        + "pass replace: true if that is what you want");
                    continue;
                }

                targets.Add(view);
                plan.Add(new Dictionary<string, object>
                {
                    { "id", viewId },
                    { "name", RevitFacts.SafeName(view) },
                    { "viewType", view.ViewType.ToString() },
                    { "compatible", true },
                    { "currentTemplateId", hasExisting ? (object)existing.Value : null },
                    { "currentTemplateName", hasExisting
                        ? RevitFacts.SafeName(document.GetElement(existing))
                        : null },
                    { "replacesExisting", assign && hasExisting },

                    // ApplyViewTemplateParameters copies only the parameters the view's CURRENT
                    // template does not control, so an assigned template silently limits what lands.
                    { "limitedByExistingTemplate", !assign && hasExisting },
                });
            }

            if (blockers.Count > 0)
            {
                throw BridgeException.BadRequest(
                    "Nothing was changed. " + blockers.Count + " of " + viewIds.Count + " views "
                        + "cannot take this template, and the bridge validates the whole batch "
                        + "before writing any of it: " + string.Join(" | ", blockers) + ".");
            }

            Dictionary<string, object> shadows = TemplateShadows(document, template);

            if (dryRun)
            {
                return new Dictionary<string, object>
                {
                    { "dryRun", true },
                    { "applied", false },
                    { "templateId", templateId },
                    { "templateName", RevitFacts.SafeName(template) },
                    { "mode", assign ? "assign" : "apply" },
                    { "plan", plan },
                    { "shadows", shadows },
                    { "note", "Nothing was changed: dryRun defaults to true. Every view above was "
                        + "validated against the template. Call again with dryRun: false to write "
                        + "it." },
                };
            }

            return RevitWrite.InGroup(document, "MCP: apply view template", delegate
            {
                RevitWrite.InTransaction(document, "Apply view template", delegate
                {
                    foreach (View view in targets)
                    {
                        if (assign)
                        {
                            view.ViewTemplateId = templateElementId;
                        }
                        else
                        {
                            view.ApplyViewTemplateParameters(template);
                        }
                    }
                });

                List<object> rows = new List<object>();
                foreach (View view in targets)
                {
                    rows.Add(ReadGraphics(document, view));
                }

                return new Dictionary<string, object>
                {
                    { "dryRun", false },
                    { "applied", true },
                    { "templateId", templateId },
                    { "templateName", RevitFacts.SafeName(template) },
                    { "mode", assign ? "assign" : "apply" },
                    { "plan", plan },

                    // Read back off every view afterwards, never echoed - which is where an "apply"
                    // that a view's own template quietly overrode becomes visible.
                    { "views", rows },
                    { "shadows", shadows },
                };
            });
        }

        // --- reading -------------------------------------------------------------------------

        private static Dictionary<string, object> ReadBackRows(Document document, List<View> views)
        {
            List<object> rows = new List<object>();
            foreach (View view in views)
            {
                rows.Add(ReadGraphics(document, view));
            }

            return new Dictionary<string, object>
            {
                { "views", rows },
                { "notes", ReadNotes() },
            };
        }

        private static Dictionary<string, object> ReadGraphics(Document document, View view)
        {
            Dictionary<string, object> row = new Dictionary<string, object>
            {
                { "id", view.Id.Value },
                { "name", RevitFacts.SafeName(view) },
                { "viewType", view.ViewType.ToString() },
                { "isTemplate", view.IsTemplate },
            };

            row["style"] = SafeDisplayStyle(view);
            row["detailLevel"] = SafeDetailLevel(view);

            ElementId templateId = SafeViewTemplateId(view);
            bool hasTemplate = templateId != null && templateId != ElementId.InvalidElementId;

            row["templateId"] = hasTemplate ? (object)templateId.Value : null;
            row["templateName"] = hasTemplate
                ? RevitFacts.SafeName(document.GetElement(templateId))
                : null;

            row["shadowIntensity"] = SafeShadowIntensity(view);
            row["sunlightIntensity"] = SafeSunlightIntensity(view);

            // Always null. There is no View.AmbientLightIntensity and no ambient BuiltInParameter
            // on a view in the installed Revit 2027 API - see "notes".
            row["ambientLightIntensity"] = null;

            row["background"] = ReadBackground(view);
            row["shadows"] = Probe(document, view, ShadowsParameter);
            row["exposure"] = Probe(document, view, ExposureParameter);

            return row;
        }

        /// <summary>
        /// What one Graphic Display Options parameter actually is on THIS view. Every field is
        /// measured, and null means unknown rather than off.
        ///
        /// "on" is a boolean only when Revit is really storing an integer for the parameter. The
        /// BuiltInParameter member existing proves nothing - see the class comment and Autodesk
        /// REVIT-222419 - so storage type None, or no parameter at all, reports on: null.
        /// </summary>
        private static Dictionary<string, object> Probe(Document document, View view, BuiltInParameter builtIn)
        {
            Dictionary<string, object> probe = new Dictionary<string, object>
            {
                { "parameter", builtIn.ToString() },
                { "parameterId", new ElementId(builtIn).Value },
            };

            Parameter parameter;
            try
            {
                parameter = view.get_Parameter(builtIn);
            }
            catch (Exception)
            {
                parameter = null;
            }

            probe["available"] = parameter != null;
            probe["storageType"] = parameter == null ? null : parameter.StorageType.ToString();
            probe["readOnly"] = parameter == null ? (object)null : parameter.IsReadOnly;
            probe["value"] = parameter == null ? null : RevitFacts.RawValue(parameter);
            probe["display"] = parameter == null ? null : RevitFacts.SafeValueString(parameter);

            bool integerStorage = parameter != null && parameter.StorageType == StorageType.Integer;
            probe["on"] = integerStorage ? (object)(parameter.AsInteger() != 0) : null;

            object controlled = AddTemplateControl(document, view, builtIn, probe);

            if (parameter == null)
            {
                probe["writable"] = false;
                probe["writableReason"] = "the view has no parameter for "
                    + builtIn + " at all, so there is nothing to write";
            }
            else if (!integerStorage)
            {
                probe["writableReason"] = "storage type is " + parameter.StorageType
                    + ", not Integer, so it is not a 0/1 toggle the API can set - "
                    + "StorageType.None is the Graphic Display Options dialog launcher";
                probe["writable"] = false;
            }
            else if (parameter.IsReadOnly)
            {
                probe["writable"] = false;
                probe["writableReason"] = "Revit reports the parameter read-only on this view";
            }
            else if (controlled == null)
            {
                // Genuinely unknown: there IS a template but what it controls could not be read.
                probe["writable"] = null;
                probe["writableReason"] = "the view has a view template but which parameters it "
                    + "controls could not be read, so whether this one can be written is unknown";
            }
            else if ((bool)controlled)
            {
                probe["writable"] = false;
                probe["writableReason"] = "the view template controls this parameter, and a "
                    + "template-controlled parameter cannot be set on the view";
            }
            else
            {
                probe["writable"] = true;
                probe["writableReason"] = "Integer storage, not read-only, and no view template "
                    + "controls it";
            }

            return probe;
        }

        /// <summary>
        /// Fills in templateId / templateName / controlledByTemplate on a probe and returns the
        /// controlledByTemplate value: true, false, or null when a template exists but what it
        /// controls could not be read.
        /// </summary>
        private static object AddTemplateControl(
            Document document,
            View view,
            BuiltInParameter builtIn,
            Dictionary<string, object> probe)
        {
            probe["templateId"] = null;
            probe["templateName"] = null;
            probe["controlledByTemplate"] = false;

            ElementId templateId = SafeViewTemplateId(view);
            if (templateId == null || templateId == ElementId.InvalidElementId)
            {
                return false;
            }

            probe["templateId"] = templateId.Value;

            View template = document.GetElement(templateId) as View;
            if (template == null)
            {
                probe["controlledByTemplate"] = null;
                return null;
            }

            probe["templateName"] = RevitFacts.SafeName(template);

            ICollection<ElementId> controllable;
            ICollection<ElementId> notControlled;
            try
            {
                controllable = template.GetTemplateParameterIds();
                notControlled = template.GetNonControlledTemplateParameterIds();
            }
            catch (Exception)
            {
                probe["controlledByTemplate"] = null;
                return null;
            }

            long parameterId = new ElementId(builtIn).Value;
            bool controlled = ContainsValue(controllable, parameterId)
                && !ContainsValue(notControlled, parameterId);

            probe["controlledByTemplate"] = controlled;
            return controlled;
        }

        private static object ReadBackground(View view)
        {
            View3D view3D = view as View3D;
            if (view3D == null)
            {
                return null;
            }

            try
            {
                return ViewEndpoints.ReadBackground(view3D.GetBackground());
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static object SafeDisplayStyle(View view)
        {
            try
            {
                return view.DisplayStyle.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static object SafeDetailLevel(View view)
        {
            try
            {
                return view.DetailLevel.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static object SafeShadowIntensity(View view)
        {
            try
            {
                return view.ShadowIntensity;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static object SafeSunlightIntensity(View view)
        {
            try
            {
                return view.SunlightIntensity;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static ElementId SafeViewTemplateId(View view)
        {
            try
            {
                return view.ViewTemplateId;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static List<string> ReadNotes()
        {
            return new List<string>
            {
                "A null \"on\" in a probe means UNKNOWN, never off: the parameter could not be read "
                    + "as an integer on that view.",
                "GRAPHIC_DISPLAY_OPTIONS_SHADOWS existing in the BuiltInParameter enum does not "
                    + "mean it is a writable toggle - see \"writable\" and \"writableReason\" per "
                    + "view, which are measured on the live view.",
                "ambientLightIntensity is always null: the installed Revit API has "
                    + "View.ShadowIntensity and View.SunlightIntensity and no ambient equivalent.",
            };
        }

        // --- the two shadows routes ------------------------------------------------------------

        /// <summary>
        /// The report for the parameter route: the one route that can honestly claim verification,
        /// because the value was read back off the view after the commit.
        /// </summary>
        private static Dictionary<string, object> ParameterRoute(Document document, List<View> views, bool requested)
        {
            List<object> readBack = new List<object>();
            bool verified = true;

            foreach (View view in views)
            {
                Dictionary<string, object> probe = Probe(document, view, ShadowsParameter);

                readBack.Add(new Dictionary<string, object>
                {
                    { "viewId", view.Id.Value },
                    { "on", probe["on"] },
                });

                object on = probe["on"];
                if (!(on is bool) || (bool)on != requested)
                {
                    verified = false;
                }
            }

            return new Dictionary<string, object>
            {
                { "requested", requested },
                { "method", "parameter" },
                { "posted", false },
                { "pending", false },
                { "verified", verified },
                { "unverified", !verified },
                { "readBack", readBack },
                { "note", verified
                    ? "Written as GRAPHIC_DISPLAY_OPTIONS_SHADOWS and read back off the view, which "
                        + "is why this one can say verified."
                    : "The parameter was written but did not read back as asked. Something else - a "
                        + "view template, or Revit itself - is overriding it. Treat it as not set." },
            };
        }

        /// <summary>
        /// The command route. Nothing here is verified at the moment it returns, and none of it
        /// says success: it reports what was posted and that the result is pending and unverified.
        /// </summary>
        private static Dictionary<string, object> PostShadowCommand(
            UIApplication app,
            UIDocument uiDocument,
            View view,
            bool requested,
            Dictionary<string, object> probe)
        {
            string commandName = requested ? ShadowOnCommand : ShadowOffCommand;

            Dictionary<string, object> report = new Dictionary<string, object>
            {
                { "requested", requested },
                { "method", "posted-command" },
                { "command", commandName },
                { "viewId", view.Id.Value },
                { "probe", probe },
                { "posted", false },
                { "pending", false },
                { "verified", false },
                { "unverified", true },
            };

            RevitCommandId commandId;
            try
            {
                commandId = RevitCommandId.LookupCommandId(commandName);
            }
            catch (Exception)
            {
                commandId = null;
            }

            if (commandId == null)
            {
                report["method"] = "unavailable";
                report["canPostCommand"] = false;
                report["reason"] = "Revit does not know a command called \"" + commandName
                    + "\" in this session. Nothing was posted and the view's shadows are unchanged.";
                return report;
            }

            report["commandId"] = commandId.Id;

            bool canPost;
            try
            {
                canPost = app.CanPostCommand(commandId);
            }
            catch (Exception)
            {
                canPost = false;
            }

            report["canPostCommand"] = canPost;

            if (!canPost)
            {
                report["method"] = "unavailable";
                report["reason"] = "Revit refuses to post \"" + commandName
                    + "\": CanPostCommand is false. The API documents PostCommand as accepting "
                    + "members of PostableCommand and external commands only, and there is no "
                    + "PostableCommand for cast shadows. Nothing was posted and the view's shadows "
                    + "are unchanged - turn them on in the Revit UI (the Graphic Display Options "
                    + "dialog, or the view control bar), or apply a view template captured from a "
                    + "view that already has them.";
                return report;
            }

            lock (CommandGate)
            {
                if (_lastCommand != null && _lastCommand.ObservedAtUtc == null)
                {
                    report["method"] = "blocked";
                    report["reason"] = "A \"" + _lastCommand.Command + "\" posted at "
                        + _lastCommand.PostedAtUtc + " has not been seen to run yet, and Revit "
                        + "allows one posted command at a time. Nothing was posted. Call "
                        + "/revit-mcp/views/graphics-command-status and retry once it has cleared.";
                    return report;
                }

                long activeBefore = 0;
                bool activated = false;

                try
                {
                    View active = uiDocument.ActiveView;
                    activeBefore = active == null ? 0 : active.Id.Value;

                    if (active == null || active.Id.Value != view.Id.Value)
                    {
                        // A posted command acts on whatever view is active when it runs, so this is
                        // not a nicety. It has to happen with no transaction open, which is why the
                        // whole command route sits outside the transaction group.
                        uiDocument.ActiveView = view;
                        activated = true;
                    }
                }
                catch (Exception ex)
                {
                    report["method"] = "unavailable";
                    report["reason"] = "View " + view.Id.Value + " could not be made active ("
                        + ex.Message + "), and a posted command acts on whatever view IS active. "
                        + "Nothing was posted rather than risk toggling shadows on the wrong view.";
                    return report;
                }

                report["activatedView"] = activated;
                report["activeViewIdBefore"] = activeBefore;

                try
                {
                    app.PostCommand(commandId);
                }
                catch (Exception ex)
                {
                    report["method"] = "unavailable";
                    report["reason"] = "Revit refused the post (" + ex.Message
                        + "). Nothing is queued and the view's shadows are unchanged.";
                    return report;
                }

                _lastCommand = new PendingShadowCommand
                {
                    Command = commandName,
                    ViewId = view.Id.Value,
                    Requested = requested,
                    PostedAtUtc = Timestamp(),
                    ProbeAtPost = probe,
                };

                WatchIdle(app);
            }

            report["posted"] = true;
            report["pending"] = true;
            report["note"] = "Posted, not applied. Revit runs a posted command when control returns "
                + "to it, against the active view, and the bridge cannot read this parameter back "
                + "on this view - so whether cast shadows are now on is UNVERIFIED. Call "
                + "/revit-mcp/views/graphics-command-status for what is known, and confirm by "
                + "looking at the view or exporting an image.";

            BridgeLog.Info("Posted " + commandName + " for view " + view.Id.Value
                + " (unverified: the shadows parameter is not readable on it)");

            return report;
        }

        /// <summary>
        /// Watches for the first Idling after a post - the moment a posted command gets to run -
        /// and records a fresh probe at it.
        ///
        /// One shot, and it takes itself off the event first thing. That is not tidiness: this
        /// assembly is loaded into a collectible AssemblyLoadContext so /revit-mcp/reload can
        /// replace it, and a delegate left on Revit's Idling event would pin the context and make
        /// the unload impossible. Nothing here may outlive the request that armed it by more than
        /// one idle tick.
        /// </summary>
        private static void WatchIdle(UIApplication app)
        {
            if (_idleWatcher != null)
            {
                try
                {
                    app.Idling -= _idleWatcher;
                }
                catch (Exception)
                {
                    // Already gone, or Revit will not have it removed here. Either way it must not
                    // stop the post being recorded.
                }

                _idleWatcher = null;
            }

            _idleWatcher = delegate (object sender, IdlingEventArgs args)
            {
                OnIdle(sender);
            };

            try
            {
                app.Idling += _idleWatcher;
            }
            catch (Exception ex)
            {
                _idleWatcher = null;
                BridgeLog.Error("Could not watch Idling for a posted shadows command", ex);
            }
        }

        private static void OnIdle(object sender)
        {
            UIApplication app = sender as UIApplication;

            lock (CommandGate)
            {
                if (_idleWatcher != null && app != null)
                {
                    try
                    {
                        app.Idling -= _idleWatcher;
                    }
                    catch (Exception)
                    {
                        // Nothing useful to do; the handler below is a no-op from here on anyway.
                    }
                }

                _idleWatcher = null;

                PendingShadowCommand pending = _lastCommand;
                if (pending == null || pending.ObservedAtUtc != null)
                {
                    return;
                }

                pending.ObservedAtUtc = Timestamp();
                pending.ProbeAtIdle = ProbeAtIdle(app, pending.ViewId);
            }
        }

        private static Dictionary<string, object> ProbeAtIdle(UIApplication app, long viewId)
        {
            try
            {
                if (app == null)
                {
                    return null;
                }

                UIDocument uiDocument = app.ActiveUIDocument;
                if (uiDocument == null || uiDocument.Document == null)
                {
                    return null;
                }

                Document document = uiDocument.Document;

                View view = document.GetElement(new ElementId(viewId)) as View;
                if (view == null)
                {
                    return null;
                }

                return Probe(document, view, ShadowsParameter);
            }
            catch (Exception ex)
            {
                BridgeLog.Error("Could not probe shadows at the first idle after a posted command", ex);
                return null;
            }
        }

        // --- view templates ---------------------------------------------------------------------

        /// <summary>
        /// Why a parameter is left out of a "graphics" capture, or null when it belongs in one.
        ///
        /// Matched on the BuiltInParameter name rather than a hand-listed set of members: VIEWER_*
        /// alone is thirty-odd members and the list moves between releases, and a rule that reads
        /// as the sentence it implements is easier to check than a table nobody can audit.
        /// </summary>
        private static string GraphicsExclusionReason(ElementId id)
        {
            string name = BuiltInParameterName(id);
            if (name == null)
            {
                // A project or shared parameter the template can control. Not sun, crop or phase,
                // so it stays in.
                return null;
            }

            if (name.StartsWith("VIEW_GRAPH_SUN", StringComparison.Ordinal)
                || name.StartsWith("VIEW_SOLARSTUDY", StringComparison.Ordinal))
            {
                return "sun - left per-view so each view keeps its own sun position and study";
            }

            if (name.StartsWith("VIEWER_", StringComparison.Ordinal))
            {
                return "crop, camera and view extents - left per-view";
            }

            if (string.Equals(name, "VIEW_PHASE", StringComparison.Ordinal)
                || string.Equals(name, "VIEW_PHASE_FILTER", StringComparison.Ordinal))
            {
                return "phase - left per-view";
            }

            return null;
        }

        /// <summary>
        /// The source view's shadows next to the new template's, with the sentence that has to be
        /// said out loud: a template only carries what the source had.
        /// </summary>
        private static Dictionary<string, object> CapturedShadows(
            Document document,
            View source,
            View template,
            Dictionary<string, object> sourceProbe)
        {
            long shadowsId = new ElementId(ShadowsParameter).Value;

            object controls;
            try
            {
                controls = ContainsValue(template.GetTemplateParameterIds(), shadowsId)
                    && !ContainsValue(template.GetNonControlledTemplateParameterIds(), shadowsId);
            }
            catch (Exception)
            {
                controls = null;
            }

            return new Dictionary<string, object>
            {
                { "templateControlsShadows", controls },
                { "sourceProbe", sourceProbe },
                { "templateProbe", Probe(document, template, ShadowsParameter) },
                { "sourceViewId", source.Id.Value },
                { "note", "A view template carries only what the source view had. If the source's "
                    + "cast shadows were off - or if \"on\" is null, which means the bridge could "
                    + "not read them - applying this template will not turn shadows on anywhere, "
                    + "and nothing in this response should be read as saying it will." },
            };
        }

        private static Dictionary<string, object> TemplateShadows(Document document, View template)
        {
            long shadowsId = new ElementId(ShadowsParameter).Value;

            object controls;
            try
            {
                controls = ContainsValue(template.GetTemplateParameterIds(), shadowsId)
                    && !ContainsValue(template.GetNonControlledTemplateParameterIds(), shadowsId);
            }
            catch (Exception)
            {
                controls = null;
            }

            return new Dictionary<string, object>
            {
                { "templateControlsShadows", controls },
                { "templateProbe", Probe(document, template, ShadowsParameter) },
                { "note", "Applying a template copies what the template holds and nothing else. If "
                    + "its cast shadows are off, or its \"on\" reads null because the parameter "
                    + "cannot be read, this call cannot turn shadows on - capture the template from "
                    + "a view that already has the state you want." },
            };
        }

        private static string ParameterName(Document document, ElementId id)
        {
            string builtIn = BuiltInParameterName(id);
            if (builtIn != null)
            {
                string label = null;
                try
                {
                    label = LabelUtils.GetLabelFor((BuiltInParameter)id.Value);
                }
                catch (Exception)
                {
                    label = null;
                }

                return label == null ? builtIn : label + " (" + builtIn + ")";
            }

            Element element = document.GetElement(id);
            return element == null ? null : RevitFacts.SafeName(element);
        }

        /// <summary>The BuiltInParameter member name behind a parameter id, or null when it is not one.</summary>
        private static string BuiltInParameterName(ElementId id)
        {
            if (id == null || id.Value >= 0 || id.Value < int.MinValue)
            {
                return null;
            }

            try
            {
                BuiltInParameter builtIn = (BuiltInParameter)id.Value;
                if (!Enum.IsDefined(typeof(BuiltInParameter), builtIn))
                {
                    return null;
                }

                return builtIn.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        // --- request plumbing ---------------------------------------------------------------

        private static List<long> RequireViewIds(JsonElement body)
        {
            JsonElement batch;
            if (JsonBody.TryGet(body, "viewIds", out batch))
            {
                return JsonBody.RequireIds(body, "viewIds");
            }

            List<long> viewIds = new List<long>();
            viewIds.Add(JsonBody.AsLong(RequireValue(body, "viewId"), "viewId"));
            return viewIds;
        }

        private static View RequireView(Document document, long viewId)
        {
            View view = document.GetElement(new ElementId(viewId)) as View;
            if (view == null)
            {
                throw BridgeException.BadRequest(
                    "Element " + viewId + " is not a view in this document. Call /revit-mcp/views "
                        + "for the ones that are.");
            }

            return view;
        }

        /// <summary>An intensity 0-100, or -1 when the caller did not name one.</summary>
        private static int OptionalIntensity(JsonElement body, string name)
        {
            JsonElement value;
            if (!JsonBody.TryGet(body, name, out value))
            {
                return -1;
            }

            double raw = JsonBody.AsDouble(value, name);
            if (raw < 0.0 || raw > 100.0)
            {
                throw BridgeException.BadRequest(
                    "\"" + name + "\" is an integer from 0 to 100; got " + raw + ".");
            }

            return (int)raw;
        }

        private static List<long> OptionalIds(JsonElement body, string name)
        {
            JsonElement value;
            if (!JsonBody.TryGet(body, name, out value))
            {
                return null;
            }

            return JsonBody.RequireIds(body, name);
        }

        private static bool IsKnownCaptureMode(string mode)
        {
            return string.Equals(mode, "graphics", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "shadows", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "all", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsValue(IEnumerable<ElementId> ids, long value)
        {
            foreach (ElementId id in ids)
            {
                if (id != null && id.Value == value)
                {
                    return true;
                }
            }

            return false;
        }

        private static string Timestamp()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        private static JsonElement RequireValue(JsonElement root, string name)
        {
            JsonElement value;
            if (!JsonBody.TryGet(root, name, out value))
            {
                throw BridgeException.BadRequest("\"" + name + "\" is required.");
            }

            return value;
        }

        /// <summary>
        /// One posted ID_IMAGE_SHADOW_ON/OFF and everything known about what became of it. Mutable
        /// and guarded by <see cref="CommandGate"/>: the Idling watcher fills in the second half.
        /// </summary>
        private sealed class PendingShadowCommand
        {
            internal string Command;
            internal long ViewId;
            internal bool Requested;
            internal string PostedAtUtc;
            internal string ObservedAtUtc;
            internal Dictionary<string, object> ProbeAtPost;
            internal Dictionary<string, object> ProbeAtIdle;
        }
    }
}
