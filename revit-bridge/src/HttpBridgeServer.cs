using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace RevitMcpBridge
{
    /// <summary>
    /// In-process HTTP front end. Accepts on a dedicated background thread and hands each request
    /// to the thread pool, so one request parked on the API-context pump cannot stall the accept
    /// loop for everybody else.
    /// </summary>
    internal sealed class HttpBridgeServer : IDisposable
    {
        internal const int Port = 48884;

        /// <summary>
        /// Every endpoint lives under this. It is declared here, in the loader, because the loader
        /// has to recognise its own /revit-mcp/reload route before handing anything to the
        /// reloadable router - which takes its copy of this constant from here.
        /// </summary>
        internal const string BasePath = "/revit-mcp";

        /// <summary>
        /// Bound to the 127.0.0.1 literal - NOT the "+" or "*" wildcard forms.
        ///
        /// DO NOT "helpfully" change this to a wildcard prefix. Two concrete reasons:
        ///   1. Wildcard prefixes are "strong" prefixes and http.sys refuses them to a
        ///      non-elevated process unless an admin registered a URL ACL first
        ///      (netsh http add urlacl). Revit normally runs unelevated, so the bridge would just
        ///      fail to start with Access Denied on most machines.
        ///   2. A wildcard binds every interface, which publishes an unauthenticated
        ///      remote-control API for the user models onto the LAN. The 127.0.0.1 literal needs
        ///      no admin URL ACL and is not reachable off-machine.
        ///
        /// The listener takes the whole port on loopback and RequestRouter enforces the
        /// /revit-mcp base path itself, so an unmatched path gets a structured JSON 404 instead of
        /// the raw http.sys 400 that a narrower prefix produces.
        /// </summary>
        internal const string UrlPrefix = "http://127.0.0.1:48884/";

        private readonly HandlerHost _handlers;

        private HttpListener _listener;
        private Thread _acceptThread;
        private volatile bool _stopping;

        /// <summary>
        /// Takes the handler host rather than a router: the router is reloadable and this object is
        /// not, so it asks for the current one per request instead of holding a reference that a
        /// reload would have to reach in and swap - or worse, that would keep the old load context
        /// alive for ever.
        /// </summary>
        internal HttpBridgeServer(HandlerHost handlers)
        {
            _handlers = handlers;
        }

        internal void Start()
        {
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add(UrlPrefix);

            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                listener.Close();
                throw new BridgeListenerException(DescribeStartFailure(ex), ex);
            }
            catch (Exception ex)
            {
                listener.Close();
                throw new BridgeListenerException(
                    "The Revit MCP Bridge could not listen on " + UrlPrefix + ": " + ex.Message, ex);
            }

            _listener = listener;

            _acceptThread = new Thread(AcceptLoop);
            _acceptThread.IsBackground = true;
            _acceptThread.Name = "revit-mcp-bridge-accept";
            _acceptThread.Start();
        }

        private static string DescribeStartFailure(HttpListenerException ex)
        {
            // 183 = ERROR_ALREADY_EXISTS, 32 = ERROR_SHARING_VIOLATION. Both mean somebody else
            // already owns this prefix - almost always a second Revit instance with the bridge
            // installed, or a stale process still holding the port.
            if (ex.ErrorCode == 183 || ex.ErrorCode == 32)
            {
                return "Port " + Port + " is already in use, so the Revit MCP Bridge did not start. "
                    + "Another Revit instance with the bridge installed is most likely already "
                    + "running - only one of them can own the port. Close the other instance (or "
                    + "whatever is holding the port) and restart Revit.";
            }

            if (ex.ErrorCode == 5)
            {
                return "Access was denied binding " + UrlPrefix + " (error 5). That is unexpected "
                    + "for a 127.0.0.1 prefix; check for a URL ACL or a policy restricting http.sys.";
            }

            return "The Revit MCP Bridge could not listen on " + UrlPrefix + " (Windows error "
                + ex.ErrorCode + "): " + ex.Message;
        }

        private void AcceptLoop()
        {
            while (!_stopping)
            {
                HttpListenerContext context;

                try
                {
                    context = _listener.GetContext();
                }
                catch (HttpListenerException ex)
                {
                    if (_stopping)
                    {
                        return;
                    }

                    BridgeLog.Error("Accept loop error", ex);
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (InvalidOperationException)
                {
                    return;
                }

                ThreadPool.QueueUserWorkItem(HandleContext, context);
            }
        }

        private void HandleContext(object state)
        {
            HttpListenerContext context = (HttpListenerContext)state;

            try
            {
                if (IsReloadRequest(context.Request))
                {
                    HandleReload(context);
                    return;
                }

                IBridgeRouter router = _handlers.Router;

                if (router == null)
                {
                    WriteJson(context.Response, 503, BridgeJson.Error(
                        "BRIDGE_NOT_READY",
                        new InvalidOperationException(
                            "The bridge is not accepting work (Revit is starting up or shutting "
                                + "down, or the handlers assembly failed to load - see "
                                + BridgeLog.LogPath + ").")));
                    return;
                }

                router.Handle(context);
            }
            catch (Exception ex)
            {
                // The router handles its own failures; this is the last line of defence so a stray
                // exception cannot tear down a thread-pool thread.
                BridgeLog.Error("Unhandled error while answering a request", ex);

                try
                {
                    WriteJson(context.Response, 500, BridgeJson.Error("INTERNAL_ERROR", ex));
                }
                catch (Exception writeError)
                {
                    BridgeLog.Error("Could not write the failure response", writeError);
                }
            }
            finally
            {
                try
                {
                    context.Response.Close();
                }
                catch (Exception closeError)
                {
                    BridgeLog.Error("Could not close the response", closeError);
                }
            }
        }

        /// <summary>
        /// The one route the loader answers itself. Reload replaces the assembly every other route
        /// lives in, so it cannot be one of them - a router unloading its own load context would be
        /// standing on the branch it is sawing.
        /// </summary>
        private static bool IsReloadRequest(HttpListenerRequest request)
        {
            if (request.Url == null)
            {
                return false;
            }

            string path = request.Url.AbsolutePath;
            if (path == null || !path.StartsWith(BasePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string route = path.Substring(BasePath.Length).Trim('/');
            return string.Equals(route, HandlerHost.ReloadRoute, StringComparison.OrdinalIgnoreCase);
        }

        private void HandleReload(HttpListenerContext context)
        {
            if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(context.Response, 405, BridgeJson.Error(
                    "METHOD_NOT_ALLOWED",
                    new InvalidOperationException(
                        "Use POST with a JSON body. " + context.Request.HttpMethod
                            + " is not supported.")));
                return;
            }

            try
            {
                WriteJson(context.Response, 200, BridgeJson.Serialize(_handlers.Reload()));
            }
            catch (Exception ex)
            {
                // A reload that failed changed nothing: the previous generation is still current
                // and still serving. Say what broke rather than leaving the caller guessing.
                BridgeLog.Error("Reload failed", ex);
                WriteJson(context.Response, 500, BridgeJson.Error("RELOAD_FAILED", ex));
            }
        }

        internal static void WriteJson(HttpListenerResponse response, int statusCode, string json)
        {
            byte[] payload = Encoding.UTF8.GetBytes(json);

            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = payload.LongLength;

            using (Stream output = response.OutputStream)
            {
                output.Write(payload, 0, payload.Length);
            }
        }

        public void Dispose()
        {
            _stopping = true;

            HttpListener listener = _listener;
            _listener = null;

            if (listener != null)
            {
                try
                {
                    listener.Stop();
                }
                catch (Exception ex)
                {
                    BridgeLog.Error("Error stopping the listener", ex);
                }

                try
                {
                    listener.Close();
                }
                catch (Exception ex)
                {
                    BridgeLog.Error("Error closing the listener", ex);
                }
            }

            Thread acceptThread = _acceptThread;
            _acceptThread = null;

            if (acceptThread != null)
            {
                acceptThread.Join(TimeSpan.FromSeconds(2));
            }
        }
    }
}
