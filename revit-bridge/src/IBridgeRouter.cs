using System;
using System.Net;

namespace RevitMcpBridge
{
    /// <summary>
    /// The one thing the loader knows about the reloadable half.
    ///
    /// It is declared here, in the loader, on purpose. The handlers assembly references the loader
    /// and implements this interface; the loader never references the handlers assembly at compile
    /// time, because a compile-time reference would bind the type in the default load context and
    /// the collectible one could then never unload. That direction is the whole trick - see
    /// HandlerAssembly.
    ///
    /// Everything crossing the boundary is a shared type (HttpListenerContext from the framework,
    /// Revit types from the assemblies Revit itself loaded), so both sides agree on identity
    /// without a third "contract" assembly.
    /// </summary>
    internal interface IBridgeRouter
    {
        /// <summary>Answers one HTTP request, exceptions and all. Never throws.</summary>
        void Handle(HttpListenerContext context);
    }
}
