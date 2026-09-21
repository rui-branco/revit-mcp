using System;
using Autodesk.Revit.UI;

namespace RevitMcpBridge
{
    /// <summary>
    /// Revit entry point, and a genuinely thin loader: it owns nothing but a <see cref="BridgeHost"/>
    /// and never touches the HTTP server, the routing table or the Revit API directly.
    ///
    /// Thin matters here for a specific reason. This assembly is the one Revit pins for the whole
    /// session; everything a caller is likely to want to change - the route table and every
    /// endpoint - lives in RevitMcpBridge.Handlers.dll, which the loader loads into a *collectible*
    /// AssemblyLoadContext from a shadow copy. POSTing to /revit-mcp/reload loads a freshly built
    /// one and retires the old context, so editing an endpoint no longer costs a Revit restart -
    /// and a restart means closing the model, which is exactly what an unattended run cannot do.
    ///
    /// The rules that keep that possible, and that any change here has to respect:
    ///   - **this assembly must never reference RevitMcpBridge.Handlers at compile time.** The
    ///     dependency runs the other way; the two meet at IBridgeRouter, declared here. A reference
    ///     would bind the handlers in the default context and no reload could ever unload,
    ///   - the listener and the ExternalEvent pump stay here, in the loader, and survive a reload,
    ///   - no type from the collectible context may be held in a static, an event handler Revit
    ///     keeps, or any long-lived field: see HandlerAssembly for how the one reference there is
    ///     dropped, and how the result is verified rather than assumed.
    /// </summary>
    public sealed class BridgeApplication : IExternalApplication
    {
        private BridgeHost _host;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                _host = new BridgeHost();
                _host.Start(application);
            }
            catch (Exception ex)
            {
                // Never take Revit down with us: a broken bridge is an inert add-in, not a crash.
                BridgeLog.Error("OnStartup failed", ex);
                try
                {
                    TaskDialog.Show(
                        "Revit MCP Bridge",
                        "The Revit MCP Bridge failed to start and will be inactive for this session.\n\n"
                            + ex.Message + "\n\nLog: " + BridgeLog.LogPath);
                }
                catch (Exception dialogError)
                {
                    BridgeLog.Error("Could not show the startup failure dialog", dialogError);
                }
            }

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                if (_host != null)
                {
                    _host.Stop();
                }
            }
            catch (Exception ex)
            {
                BridgeLog.Error("OnShutdown failed", ex);
            }

            _host = null;
            return Result.Succeeded;
        }
    }
}
