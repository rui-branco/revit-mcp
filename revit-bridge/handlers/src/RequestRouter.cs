using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.UI;
using RevitMcpBridge.Endpoints;

namespace RevitMcpBridge
{
    /// <summary>
    /// Maps "/revit-mcp/&lt;name&gt;" onto a handler, marshals the handler onto Revit's main thread
    /// through <see cref="RevitApiContext"/>, and turns whatever comes back - result or exception -
    /// into JSON.
    ///
    /// Handlers are plain static methods of shape (UIApplication, JsonElement) => object. They know
    /// nothing about HTTP - which is what let this class and its endpoints move into the reloadable
    /// satellite assembly without any of them noticing.
    ///
    /// This type is what HandlerAssembly instantiates out of the collectible load context, by name;
    /// the loader only ever sees it as an IBridgeRouter. Renaming it, or changing what its
    /// constructor takes, breaks the reload contract - HandlerAssembly.RouterTypeName goes with it.
    /// </summary>
    internal sealed class RequestRouter : IBridgeRouter
    {
        // Declared in the loader, which needs the same constant to recognise its own reload route.
        internal const string BasePath = HttpBridgeServer.BasePath;

        private const int DefaultTimeoutMs = 30000;
        private const int MinTimeoutMs = 1000;
        private const int MaxTimeoutMs = 600000;

        // Kept alive for the process: supplies the RootElement used when a request has no body.
        private static readonly JsonDocument EmptyBody = JsonDocument.Parse("{}");

        private static readonly int ConfiguredTimeoutMs = ReadConfiguredTimeout();

        private readonly RevitApiContext _context;
        private readonly Dictionary<string, Func<UIApplication, JsonElement, object>> _routes;

        internal RequestRouter(RevitApiContext context)
        {
            _context = context;

            _routes = new Dictionary<string, Func<UIApplication, JsonElement, object>>(StringComparer.OrdinalIgnoreCase)
            {
                { "status", ReadEndpoints.Status },
                { "levels", ReadEndpoints.Levels },
                { "categories", ReadEndpoints.Categories },
                { "query", ReadEndpoints.Query },
                { "elements", ReadEndpoints.Elements },
                { "selection", ReadEndpoints.Selection },
                { "titleblocks", ReadEndpoints.TitleBlocks },
                { "sheets", ReadEndpoints.Sheets },
                { "levels/create", WriteEndpoints.CreateLevels },
                { "walls/create", WriteEndpoints.CreateWalls },
                { "parameters/set", WriteEndpoints.SetParameters },
                { "elements/delete", WriteEndpoints.DeleteElements },
                { "sheets/create", WriteEndpoints.CreateSheets },

                // Native Project Browser grouping for sheets - see SheetCollectionEndpoints.
                { "sheets/collections", SheetCollectionEndpoints.Collections },
                { "sheets/set-collections", SheetCollectionEndpoints.SetCollections },

                // Site, hardscape and family content: what actually populates a project.
                { "toposolid/create", ModelEndpoints.CreateToposolid },
                { "toposolid/flatten", ModelEndpoints.FlattenToposolid },
                { "floors/create", ModelEndpoints.CreateFloor },
                { "families/load", ModelEndpoints.LoadFamilies },
                { "families/symbols", ModelEndpoints.FamilySymbols },
                { "families/place", ModelEndpoints.PlaceFamilies },
                { "openings/place", ModelEndpoints.PlaceOpenings },

                // What is actually printed on a sheet lives in the title block FAMILY, not in the
                // project. Read-only: it opens the family copy EditFamily hands back and closes it
                // without saving. See TitleblockEndpoints.
                { "families/titleblock-inspect", TitleblockEndpoints.Inspect },

                // The write half: it edits that same copy and loads it back into this project.
                // dryRun defaults to TRUE, labels and schedules are verified to have survived, and
                // only a TextNote or an ImageInstance can be removed.
                { "families/titleblock-edit", TitleblockEndpoints.Edit },

                // Materials, and the two type kinds that carry one in their compound structure
                // rather than on the element - see MaterialEndpoints.
                { "materials", MaterialEndpoints.Materials },
                { "materials/create", MaterialEndpoints.CreateMaterial },
                { "materials/assign", MaterialEndpoints.AssignMaterial },
                { "materials/set-texture", MaterialEndpoints.SetTexture },

                // The rendered look behind a material - reading the appearance asset a material
                // really has, and patching named properties of a copy of it. See
                // MaterialAppearanceEndpoints.
                { "materials/appearance", MaterialAppearanceEndpoints.Appearance },
                { "materials/set-appearance", MaterialAppearanceEndpoints.SetAppearance },

                { "walltypes/create", ModelEndpoints.CreateWallType },
                { "floortypes/create", ModelEndpoints.CreateFloorType },

                // Views, schedules and what puts them on a sheet.
                { "views", ViewEndpoints.Views },
                { "views/create-plan", ViewEndpoints.CreatePlan },
                { "views/create-drafting", ViewEndpoints.CreateDrafting },
                { "views/create-section", ViewEndpoints.CreateSection },
                { "views/create-3d", ViewEndpoints.Create3D },
                { "views/set-style", ViewEndpoints.SetStyle },
                { "views/set-background", ViewEndpoints.SetBackground },
                { "views/hide-categories", ViewEndpoints.HideCategories },

                // Its sibling: the same dialog, the other half of it. Overrides are MERGED onto
                // what the view already has, and a view whose template owns V/G is refused rather
                // than written to and drawn the template's way. See ViewOverrideEndpoints.
                { "views/override-categories", ViewOverrideEndpoints.OverrideCategories },

                { "views/set-sun", ViewEndpoints.SetSun },
                { "views/export-image", ViewEndpoints.ExportImage },
                { "views/legends", ViewEndpoints.Legends },
                { "views/create-legend", ViewEndpoints.CreateLegend },
                { "views/set-scale", ViewEndpoints.SetScale },

                // What views/set-scale cannot do: a perspective camera has no view scale, so its
                // size on a sheet is changed by scaling its crop box instead. Locked proportions,
                // same shot, camera untouched.
                { "views/scale-perspective-crop", ViewEndpoints.ScalePerspectiveCrop },

                { "views/duplicate", ViewEndpoints.DuplicateView },
                { "sheets/place-view", ViewEndpoints.PlaceViews },
                { "schedules/create", ViewEndpoints.CreateSchedule },

                // Cast shadows and the view templates that carry professional graphics. Separate
                // from views/set-style because the shadows toggle is not a plain parameter write:
                // it is probed on the live view and falls back to posting Revit's own command. See
                // GraphicsEndpoints.
                { "views/graphics", GraphicsEndpoints.Graphics },
                { "views/set-graphics", GraphicsEndpoints.SetGraphics },
                { "views/graphics-command-status", GraphicsEndpoints.CommandStatus },
                { "views/shadow-command-feasibility", GraphicsEndpoints.ShadowCommandFeasibility },
                { "views/capture-template", GraphicsEndpoints.CaptureTemplate },
                { "views/apply-template", GraphicsEndpoints.ApplyTemplate },

                // Documentation: what happens to a view AFTER it is made - cropping it, hiding
                // what is in the way, laying the sheet out, reading a schedule back and getting
                // the set out as PDF. Every write here defaults to dryRun true. See
                // DocumentationEndpoints.
                { "views/crop", DocumentationEndpoints.Crop },
                { "views/set-crop", DocumentationEndpoints.SetCrop },
                { "views/hide-elements", DocumentationEndpoints.HideElements },
                { "schedules/read", DocumentationEndpoints.ReadSchedule },
                { "schedules/configure", DocumentationEndpoints.ConfigureSchedule },
                { "sheets/layout", DocumentationEndpoints.SheetLayout },
                { "sheets/set-viewport-position", DocumentationEndpoints.SetViewportPosition },
                { "sheets/set-schedule-position", DocumentationEndpoints.SetSchedulePosition },
                { "sheets/browser-organization", DocumentationEndpoints.BrowserOrganization },
                { "export/pdf", DocumentationEndpoints.ExportPdf },

                // Project parameters, and the per-sheet values that go in them.
                { "parameters/create-project", ParameterEndpoints.CreateProjectParameter },
                { "sheets/set-parameter", ParameterEndpoints.SetSheetParameters },

                // Linework and annotation inside one view: what a detail sheet is made of.
                { "detail/lines", DetailEndpoints.CreateDetailLines },
                { "detail/text", DetailEndpoints.CreateTextNotes },

                // Geometry with no family behind it - the only modelling route on a Revit with no
                // family content installed. See DirectShapeEndpoints.
                { "directshape/create", DirectShapeEndpoints.CreateDirectShapes },
                { "planting/place", DirectShapeEndpoints.PlantPlanting },
                { "pipes/create", DirectShapeEndpoints.CreatePipes },
                { "sprinklers/place", DirectShapeEndpoints.PlaceSprinklers },

                // Document lifecycle: not one of these is a transactable model edit - see
                // DocumentEndpoints - and open/close do not need an active document.
                { "document/new", DocumentEndpoints.NewDocument },
                { "document/save", DocumentEndpoints.Save },
                { "document/save-as", DocumentEndpoints.SaveAs },
                { "document/open", DocumentEndpoints.Open },
                { "document/close", DocumentEndpoints.Close },

                // What the bridge suppressed on the caller's behalf, and the switch for it.
                { "diagnostics", DiagnosticsEndpoints.Read },
                { "diagnostics/config", DiagnosticsEndpoints.Configure },

                // Quality assurance: measured geometry, the model's standing warnings, the
                // templates overruling views - and the one edit safe enough to sit beside them.
                { "elements/inspect", QualityEndpoints.InspectElements },
                { "elements/move", QualityEndpoints.MoveElements },
                { "document/warnings", QualityEndpoints.Warnings },
                { "views/templates", QualityEndpoints.ViewTemplates },
                { "toposolid/excavate", QualityEndpoints.ExcavateToposolid },
            };
        }

        public void Handle(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;

            string route = ResolveRoute(request.Url);
            Stopwatch clock = Stopwatch.StartNew();

            try
            {
                if (route == null)
                {
                    throw BridgeException.NotFound(
                        "UNKNOWN_PATH",
                        "No such endpoint: " + request.Url.AbsolutePath + ". Every endpoint lives "
                            + "under " + BasePath + "/ - for example " + BasePath + "/status.");
                }

                // Reads carry their filters in the body, so there is nothing a GET could express.
                if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    throw new BridgeException(
                        405,
                        "METHOD_NOT_ALLOWED",
                        "Use POST with a JSON body. " + request.HttpMethod + " is not supported.");
                }

                Func<UIApplication, JsonElement, object> handler;
                if (!_routes.TryGetValue(route, out handler))
                {
                    throw BridgeException.NotFound(
                        "UNKNOWN_ENDPOINT",
                        "No such endpoint: " + BasePath + "/" + route + ". Known endpoints: "
                            + string.Join(", ", SortedRouteNames()) + ".");
                }

                using (JsonDocument body = ReadBody(request))
                {
                    JsonElement root = body == null ? EmptyBody.RootElement : body.RootElement;
                    int timeoutMs = ResolveTimeout(root);

                    // JsonElement stays valid because Run blocks until the handler is finished.
                    object result = _context.Run(
                        delegate (UIApplication app) { return handler(app, root); },
                        timeoutMs);

                    HttpBridgeServer.WriteJson(response, 200, BridgeJson.Serialize(result));
                    BridgeLog.Info("POST " + BasePath + "/" + route + " -> 200 in " + clock.ElapsedMilliseconds + " ms");
                }
            }
            catch (BridgeException ex)
            {
                BridgeLog.Warn("POST " + BasePath + "/" + route + " -> " + ex.StatusCode + " "
                    + ex.Code + ": " + ex.Message);
                HttpBridgeServer.WriteJson(response, ex.StatusCode, BridgeJson.Error(ex.Code, ex));
            }
            catch (JsonException ex)
            {
                BridgeLog.Warn("POST " + BasePath + "/" + route + " -> 400 BAD_JSON: " + ex.Message);
                HttpBridgeServer.WriteJson(response, 400, BridgeJson.Error("BAD_JSON", ex));
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                // Every Revit API exception derives from this one.
                BridgeLog.Error("POST " + BasePath + "/" + route + " -> 500 REVIT_API_ERROR", ex);
                HttpBridgeServer.WriteJson(response, 500, BridgeJson.Error("REVIT_API_ERROR", ex));
            }
            catch (Exception ex)
            {
                BridgeLog.Error("POST " + BasePath + "/" + route + " -> 500 INTERNAL_ERROR", ex);
                HttpBridgeServer.WriteJson(response, 500, BridgeJson.Error("INTERNAL_ERROR", ex));
            }
        }

        /// <summary>
        /// Returns the route name under the base path, or null when the URL is outside it.
        /// Tolerates a missing or extra trailing slash and any casing.
        /// </summary>
        private static string ResolveRoute(Uri url)
        {
            if (url == null)
            {
                return null;
            }

            string path = url.AbsolutePath;
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            if (!path.StartsWith(BasePath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string remainder = path.Substring(BasePath.Length);
            if (remainder.Length > 0 && remainder[0] != '/')
            {
                // e.g. "/revit-mcp-something-else" - not ours.
                return null;
            }

            return remainder.Trim('/');
        }

        private IEnumerable<string> SortedRouteNames()
        {
            List<string> names = new List<string>(_routes.Keys);

            // Answered by the loader rather than out of this table, but a caller asking what
            // exists should still be told about it.
            names.Add(HandlerHost.ReloadRoute);

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        private static JsonDocument ReadBody(HttpListenerRequest request)
        {
            string text;

            Encoding encoding = request.ContentEncoding;
            if (encoding == null)
            {
                encoding = Encoding.UTF8;
            }

            using (StreamReader reader = new StreamReader(request.InputStream, encoding))
            {
                text = reader.ReadToEnd();
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return JsonDocument.Parse(text);
        }

        /// <summary>
        /// Per-request "timeoutMs" wins, then the REVIT_MCP_BRIDGE_TIMEOUT_MS environment variable,
        /// then 30 s.
        /// </summary>
        private static int ResolveTimeout(JsonElement root)
        {
            int timeout = ConfiguredTimeoutMs;

            JsonElement value;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("timeoutMs", out value))
            {
                int requested;
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out requested))
                {
                    timeout = requested;
                }
            }

            if (timeout < MinTimeoutMs)
            {
                return MinTimeoutMs;
            }

            if (timeout > MaxTimeoutMs)
            {
                return MaxTimeoutMs;
            }

            return timeout;
        }

        private static int ReadConfiguredTimeout()
        {
            try
            {
                string raw = Environment.GetEnvironmentVariable("REVIT_MCP_BRIDGE_TIMEOUT_MS");
                int parsed;
                if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out parsed) && parsed > 0)
                {
                    return parsed;
                }
            }
            catch (Exception ex)
            {
                BridgeLog.Error("Could not read REVIT_MCP_BRIDGE_TIMEOUT_MS", ex);
            }

            return DefaultTimeoutMs;
        }
    }
}
