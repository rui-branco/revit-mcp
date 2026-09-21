using System;
using System.Collections.Generic;
using System.IO;

namespace RevitMcpBridge
{
    /// <summary>
    /// Owns the reloadable half of the bridge: which generation of the handlers assembly is
    /// current, and the /revit-mcp/reload that replaces it.
    ///
    /// Reload exists so that changing an endpoint does not cost a Revit restart - a restart means
    /// closing the model, which is exactly what an unattended run cannot do. The listener, the
    /// ExternalEvent pump and everything else the session depends on stay here in the loader and
    /// survive untouched; only the routing and endpoint logic is swapped.
    ///
    /// The order below is the careful part: the new generation is loaded **first** and only becomes
    /// current once it exists, so a failed reload leaves the working one serving requests and
    /// answers with an error. Retiring the old one comes last, and whether it actually went is
    /// reported rather than assumed.
    /// </summary>
    internal sealed class HandlerHost
    {
        /// <summary>
        /// Handled by HttpBridgeServer before the router ever sees it, and it has to be: the router
        /// lives in the context this endpoint unloads, so a reload routed through it would be
        /// unloading an assembly whose method is still on the stack.
        /// </summary>
        internal const string ReloadRoute = "reload";

        private readonly RevitApiContext _context;
        private readonly string _handlersPath;

        private volatile HandlerAssembly _current;

        internal HandlerHost(RevitApiContext context)
        {
            _context = context;
            _handlersPath = ResolveHandlersPath();
        }

        /// <summary>Null only before Start or after Stop.</summary>
        internal IBridgeRouter Router
        {
            get
            {
                HandlerAssembly current = _current;
                return current == null ? null : current.Router;
            }
        }

        internal void Start()
        {
            _current = HandlerAssembly.Load(_handlersPath, new object[] { _context });
        }

        internal void Stop()
        {
            // Not unloaded on the way out: Revit is closing anyway, and running a GC loop during
            // shutdown buys nothing.
            _current = null;
        }

        /// <summary>
        /// Loads the handlers assembly again from disk and makes it current. Answers with what
        /// happened, including the uncomfortable parts.
        /// </summary>
        internal Dictionary<string, object> Reload()
        {
            HandlerAssembly next = HandlerAssembly.Load(_handlersPath, new object[] { _context });

            HandlerAssembly previous = _current;
            _current = next;

            bool hadPrevious = previous != null;
            bool unloaded = false;

            if (hadPrevious)
            {
                WeakReference retired = HandlerAssembly.Retire(previous);
                previous = null;

                unloaded = HandlerAssembly.WaitForUnload(retired);
            }

            Dictionary<string, object> result = new Dictionary<string, object>
            {
                { "reloaded", true },
                { "version", next.Version },
                { "loadedAt", next.LoadedAt },
                { "loadedFrom", next.LoadedFrom },
                { "collectible", next.Collectible },
                { "unloadedPrevious", !hadPrevious || unloaded },
            };

            if (!next.Collectible)
            {
                result["warning"] = "The handlers assembly could not be loaded into a collectible "
                    + "load context and is in the default one instead, so this generation can never "
                    + "be replaced: further reloads will keep leaking a generation each. Restart "
                    + "Revit to get back to a reloadable session, and see "
                    + BridgeLog.LogPath + " for why the collectible load failed.";
            }
            else if (hadPrevious && !unloaded)
            {
                result["warning"] = "The new logic is live, but the previous load context did not "
                    + "unload - something is still referencing it, most likely a request that was "
                    + "still running. It leaks until the process exits. If this repeats on every "
                    + "reload, treat it as a bug rather than as noise.";
            }

            BridgeLog.Info("Reloaded " + HandlerAssembly.FileName + " " + next.Version
                + " (collectible=" + next.Collectible + " unloadedPrevious="
                + (!hadPrevious || unloaded) + ")");

            return result;
        }

        /// <summary>
        /// Next to the loader, which is where install.ps1 puts it - it copies every file in the
        /// build output into the add-in folder, so the two halves always travel together.
        /// </summary>
        private static string ResolveHandlersPath()
        {
            string location = typeof(HandlerHost).Assembly.Location;

            string directory = string.IsNullOrEmpty(location)
                ? AppContext.BaseDirectory
                : Path.GetDirectoryName(location);

            return Path.Combine(directory, HandlerAssembly.FileName);
        }
    }
}
