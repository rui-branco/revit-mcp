using System;
using Autodesk.Revit.UI;

namespace RevitMcpBridge
{
    /// <summary>
    /// Owns the moving parts of the bridge: the API-context pump (<see cref="RevitApiContext"/>),
    /// the HTTP listener (<see cref="HttpBridgeServer"/>), the dialog watcher that makes unattended
    /// operation possible (<see cref="BridgeDialogWatcher"/>) and the reloadable handlers assembly
    /// (<see cref="HandlerHost"/>). Everything here is owned by the loader and survives a reload;
    /// only the routing and endpoint logic is swapped.
    /// </summary>
    internal sealed class BridgeHost
    {
        private RevitApiContext _context;
        private HandlerHost _handlers;
        private HttpBridgeServer _server;
        private BridgeDialogWatcher _dialogs;

        /// <summary>Called on Revit's main thread from IExternalApplication.OnStartup.</summary>
        internal void Start(UIControlledApplication application)
        {
            BridgeLog.Info("Revit MCP Bridge " + BridgeInfo.Version + " starting up");

            // ExternalEvent.Create is only legal on Revit's main thread, which is where OnStartup
            // runs. Doing this lazily from a listener thread would throw.
            _context = new RevitApiContext();
            _context.Initialize();

            // The routing and endpoint logic lives in a separate assembly loaded into a collectible
            // context, so /revit-mcp/reload can replace it without a Revit restart. Everything this
            // class owns stays put across that - see HandlerHost.
            _handlers = new HandlerHost(_context);
            _handlers.Start();

            _server = new HttpBridgeServer(_handlers);

            try
            {
                _server.Start();
                BridgeLog.Info("Listening on " + HttpBridgeServer.UrlPrefix);

                // Only once the bridge is actually serving. Started any earlier it would answer -
                // and so hide - the two failure dialogs below, which are the only way the user
                // hears about a bridge that did not start.
                _dialogs = new BridgeDialogWatcher();
                _dialogs.Start(application);
                BridgeLog.Info("Dialog auto-dismiss is "
                    + (BridgeDiagnostics.AutoDismiss ? "on" : "off"));
            }
            catch (BridgeListenerException ex)
            {
                // Most common cause by far: a second Revit instance already grabbed the port.
                // Log it, tell the user, and leave Revit perfectly usable.
                _server = null;
                BridgeLog.Error("HTTP listener could not start", ex);
                WriteJournalComment(application, "Revit MCP Bridge did not start: " + ex.Message);

                try
                {
                    TaskDialog.Show(
                        "Revit MCP Bridge",
                        ex.Message + "\n\nRevit is unaffected; the bridge is simply inactive for "
                            + "this session.\n\nLog: " + BridgeLog.LogPath);
                }
                catch (Exception dialogError)
                {
                    BridgeLog.Error("Could not show the listener failure dialog", dialogError);
                }
            }
        }

        /// <summary>Called on Revit's main thread from IExternalApplication.OnShutdown.</summary>
        internal void Stop()
        {
            if (_dialogs != null)
            {
                _dialogs.Dispose();
                _dialogs = null;
            }

            if (_server != null)
            {
                _server.Dispose();
                _server = null;
            }

            if (_handlers != null)
            {
                _handlers.Stop();
                _handlers = null;
            }

            if (_context != null)
            {
                // Disposing the ExternalEvent must also happen on the main thread.
                _context.Dispose();
                _context = null;
            }

            BridgeLog.Info("Revit MCP Bridge stopped");
        }

        private static void WriteJournalComment(UIControlledApplication application, string text)
        {
            try
            {
                application.ControlledApplication.WriteJournalComment(text, false);
            }
            catch (Exception ex)
            {
                BridgeLog.Error("Could not write a journal comment", ex);
            }
        }
    }
}
