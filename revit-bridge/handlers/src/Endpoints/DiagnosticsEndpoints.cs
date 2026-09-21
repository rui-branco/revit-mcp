using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.Revit.UI;

namespace RevitMcpBridge.Endpoints
{
    /// <summary>
    /// The window onto what the bridge answered on the caller's behalf: the dialogs
    /// BridgeDialogWatcher clicked and the transaction warnings BridgeFailuresPreprocessor
    /// resolved. Without this endpoint the two of them would be exactly the silent swallowing they
    /// exist to avoid.
    ///
    /// Like ReadEndpoints.Status, neither of these needs an active document - a Revit sitting on
    /// the start page can still have dismissed a dialog - and neither is a model edit, so neither
    /// is transacted.
    /// </summary>
    internal static class DiagnosticsEndpoints
    {
        /// <summary>Body: {}. Returns {autoDismiss, capacity, dropped, dialogs, failures}.</summary>
        internal static object Read(UIApplication app, JsonElement body)
        {
            return BridgeDiagnostics.Snapshot();
        }

        /// <summary>
        /// Body: {autoDismiss?, clear?}. Both optional: either turns auto-dismiss on/off for the
        /// rest of the session, empties the buffer, or both. Answers with the state afterwards.
        /// </summary>
        internal static object Configure(UIApplication app, JsonElement body)
        {
            JsonElement value;
            if (JsonBody.TryGet(body, "autoDismiss", out value))
            {
                BridgeDiagnostics.AutoDismiss = JsonBody.AsBool(value, "autoDismiss");
            }

            bool clear = JsonBody.OptionalBool(body, "clear", false);
            if (clear)
            {
                BridgeDiagnostics.Clear();
            }

            BridgeLog.Info("Diagnostics config: autoDismiss=" + BridgeDiagnostics.AutoDismiss
                + " cleared=" + clear);

            return new Dictionary<string, object>
            {
                { "autoDismiss", BridgeDiagnostics.AutoDismiss },
                { "cleared", clear },
            };
        }
    }
}
