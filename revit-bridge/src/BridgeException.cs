using System;

namespace RevitMcpBridge
{
    /// <summary>
    /// An expected, explainable failure. Carries the HTTP status the router should answer with and
    /// a short machine-readable code. The wire shape is produced by the router:
    /// {"error": {"code": "...", "message": "...", "stack": "..."}}.
    /// </summary>
    internal sealed class BridgeException : Exception
    {
        internal BridgeException(int statusCode, string code, string message)
            : base(message)
        {
            StatusCode = statusCode;
            Code = code;
        }

        internal int StatusCode { get; private set; }

        internal string Code { get; private set; }

        internal static BridgeException BadRequest(string message)
        {
            return new BridgeException(400, "BAD_REQUEST", message);
        }

        internal static BridgeException NotFound(string code, string message)
        {
            return new BridgeException(404, code, message);
        }

        /// <summary>
        /// The second most common real-world failure: a request arrives while Revit sits on the
        /// start page or between documents. Must never surface as a NullReferenceException.
        /// </summary>
        internal static BridgeException NoActiveDocument()
        {
            return new BridgeException(
                409,
                "NO_ACTIVE_DOCUMENT",
                "Revit has no active document. Open a project in Revit and make its window active, "
                    + "then retry. (The /revit-mcp/status endpoint works without a document.)");
        }

        internal static BridgeException NotAProjectDocument()
        {
            return new BridgeException(
                409,
                "NOT_A_PROJECT_DOCUMENT",
                "The active document is a family document. This endpoint needs a project document.");
        }

        /// <summary>
        /// The single most common real-world failure: Revit never got around to running our work
        /// item, almost always because a modal dialog is open and the main thread is parked.
        /// </summary>
        internal static BridgeException Busy(int timeoutMs, bool startedRunning)
        {
            string detail;
            if (startedRunning)
            {
                detail = "The operation had already started on Revit's main thread when the bridge "
                    + "gave up waiting, so it may still complete inside Revit even though this "
                    + "request failed.";
            }
            else
            {
                detail = "The operation never started, so nothing was changed in the model.";
            }

            return new BridgeException(
                503,
                "REVIT_BUSY",
                "Revit did not process the request within " + timeoutMs + " ms. Revit is busy or "
                    + "blocked on a modal dialog - check the Revit window for an open dialog and "
                    + "dismiss it. " + detail);
        }
    }

    /// <summary>Thrown when the HTTP listener itself cannot be started (port in use, ACL, ...).</summary>
    internal sealed class BridgeListenerException : Exception
    {
        internal BridgeListenerException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
