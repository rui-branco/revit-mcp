using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace RevitMcpBridge
{
    /// <summary>
    /// One loaded generation of the handlers assembly: the collectible AssemblyLoadContext, the
    /// router instance created out of it, and what it takes to let go of both again.
    ///
    /// Deliberately free of any Revit type. Two reasons: the rules that decide whether an
    /// AssemblyLoadContext can actually unload are subtle enough that this file has to be testable
    /// outside Revit, and the router's constructor arguments are passed as plain object[] so this
    /// code never needs to know what a UIApplication is.
    ///
    /// The rules being obeyed here, all of them load-bearing:
    ///   - the DLL is loaded from a **shadow copy**, never from the installed path: a loaded file
    ///     is locked, and a locked file cannot be overwritten by the rebuild that a reload exists
    ///     to pick up;
    ///   - the load context resolves **nothing** itself (Load returns null), so the loader
    ///     assembly, the Revit API and the framework all bind to the copies already loaded in the
    ///     default context - one identity for every shared type;
    ///   - unloading **nulls every field first** and happens in a NoInlining method, so no local
    ///     and no field is still pointing into the collectible context when the GC runs;
    ///   - whether it worked is decided by a WeakReference, not by hope. The caller reports what
    ///     the WeakReference says.
    /// </summary>
    internal sealed class HandlerAssembly
    {
        internal const string FileName = "RevitMcpBridge.Handlers.dll";

        /// <summary>
        /// The type the loader instantiates out of the handlers assembly. Found by name, never by
        /// a compile-time reference - that is the point.
        /// </summary>
        private const string RouterTypeName = "RevitMcpBridge.RequestRouter";

        private AssemblyLoadContext _context;
        private Assembly _assembly;
        private IBridgeRouter _router;

        private HandlerAssembly()
        {
        }

        internal IBridgeRouter Router
        {
            get { return _router; }
        }

        internal string Version { get; private set; }

        internal string LoadedAt { get; private set; }

        internal string LoadedFrom { get; private set; }

        /// <summary>
        /// False when the fallback below was used: the assembly is loaded and the bridge works, but
        /// it can never be replaced without restarting Revit, and /revit-mcp/reload says so.
        /// </summary>
        internal bool Collectible { get; private set; }

        /// <summary>
        /// Loads the handlers assembly into a fresh collectible context and builds its router.
        ///
        /// Falls back to the default load context if anything about the collectible path fails -
        /// shadow copy, load, activation. A bridge that works but cannot hot-reload is worth far
        /// more than a bridge that will not start, and the fallback is reported rather than hidden.
        /// </summary>
        internal static HandlerAssembly Load(string sourcePath, object[] routerArguments)
        {
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException(
                    "The bridge's handlers assembly is missing: " + sourcePath + ". It is installed "
                        + "next to RevitMcpBridge.dll; reinstall the add-in (revit_install_bridge) "
                        + "and restart Revit.",
                    sourcePath);
            }

            try
            {
                return LoadCollectible(sourcePath, routerArguments);
            }
            catch (Exception ex)
            {
                BridgeLog.Error(
                    "Could not load " + FileName + " into a collectible context; falling back to "
                        + "the default one. The bridge will work but /revit-mcp/reload cannot.",
                    ex);

                return LoadInDefaultContext(sourcePath, routerArguments);
            }
        }

        private static HandlerAssembly LoadCollectible(string sourcePath, object[] routerArguments)
        {
            string shadowPath = ShadowCopy(sourcePath);

            HandlerLoadContext context = new HandlerLoadContext();
            Assembly assembly = context.LoadFromAssemblyPath(shadowPath);

            HandlerAssembly loaded = new HandlerAssembly();
            loaded._context = context;
            loaded._assembly = assembly;
            loaded._router = CreateRouter(assembly, routerArguments);
            loaded.Version = BridgeInfo.VersionOf(assembly);
            loaded.LoadedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            loaded.LoadedFrom = shadowPath;
            loaded.Collectible = true;

            BridgeLog.Info("Loaded " + FileName + " " + loaded.Version + " from " + shadowPath
                + " (collectible)");

            return loaded;
        }

        private static HandlerAssembly LoadInDefaultContext(string sourcePath, object[] routerArguments)
        {
            Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(sourcePath);

            HandlerAssembly loaded = new HandlerAssembly();
            loaded._assembly = assembly;
            loaded._router = CreateRouter(assembly, routerArguments);
            loaded.Version = BridgeInfo.VersionOf(assembly);
            loaded.LoadedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            loaded.LoadedFrom = sourcePath;
            loaded.Collectible = false;

            BridgeLog.Warn("Loaded " + FileName + " " + loaded.Version + " from " + sourcePath
                + " into the default context - this session cannot hot-reload");

            return loaded;
        }

        /// <summary>
        /// Retires a generation: nulls everything that points into the context, asks it to unload,
        /// and hands back the only thing left pointing at it - a weak reference, which is how the
        /// caller can tell the truth about whether it worked. Null means there was nothing
        /// collectible to unload, which is not the same as having unloaded it.
        ///
        /// NoInlining so this frame, with its references to the context, is gone before the caller
        /// starts collecting.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static WeakReference Retire(HandlerAssembly generation)
        {
            AssemblyLoadContext context = generation._context;

            generation._router = null;
            generation._assembly = null;
            generation._context = null;

            if (context == null)
            {
                // The fallback put this generation in the default context. It stays there for the
                // rest of the session, and pretending otherwise would be the one thing worse than
                // not reloading at all.
                return null;
            }

            WeakReference weak = new WeakReference(context, true);
            context.Unload();
            return weak;
        }

        /// <summary>
        /// Unload is a request, not an instruction: it completes only once nothing from the context
        /// is referenced any more, which takes a collection or three. A handful of attempts is the
        /// documented way to wait for it; if it is still alive afterwards something is holding it
        /// and the caller must say so.
        /// </summary>
        internal static bool WaitForUnload(WeakReference context)
        {
            if (context == null)
            {
                return false;
            }

            for (int attempt = 0; attempt < 10 && context.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            return !context.IsAlive;
        }

        private static IBridgeRouter CreateRouter(Assembly assembly, object[] routerArguments)
        {
            Type type = assembly.GetType(RouterTypeName, false);
            if (type == null)
            {
                throw new TypeLoadException(
                    assembly.GetName().Name + " does not contain " + RouterTypeName + ". The two "
                        + "halves of the bridge are out of step - reinstall the add-in.");
            }

            // NonPublic: the router is internal, like everything else in the bridge.
            object instance = Activator.CreateInstance(
                type,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                routerArguments,
                null);

            IBridgeRouter router = instance as IBridgeRouter;
            if (router == null)
            {
                throw new TypeLoadException(
                    RouterTypeName + " does not implement IBridgeRouter. The two halves of the "
                        + "bridge are out of step - reinstall the add-in.");
            }

            return router;
        }

        /// <summary>
        /// Copies the DLL somewhere nobody rebuilds into, under a unique name, and leaves the
        /// previous copies alone: they stay locked until their context finishes unloading, so
        /// deleting them is best-effort and never worth failing a load over.
        /// </summary>
        private static string ShadowCopy(string sourcePath)
        {
            // LocalAppData for the same reason BridgeLog uses it: Controlled Folder Access blocks
            // Documents and Pictures and reports it as a misleading IO error.
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevitMcpBridge",
                "shadow");

            string directory = Path.Combine(root, DateTime.Now.ToString("yyyyMMddHHmmssfff"));
            Directory.CreateDirectory(directory);

            string target = Path.Combine(directory, Path.GetFileName(sourcePath));
            File.Copy(sourcePath, target, true);

            PruneOldShadowCopies(root, directory);

            return target;
        }

        private static void PruneOldShadowCopies(string root, string keep)
        {
            try
            {
                foreach (string directory in Directory.GetDirectories(root))
                {
                    if (string.Equals(directory, keep, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        Directory.Delete(directory, true);
                    }
                    catch (IOException)
                    {
                        // Still loaded, or still unloading. It will go on a later start.
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                BridgeLog.Error("Could not prune old shadow copies under " + root, ex);
            }
        }

        /// <summary>
        /// Resolves nothing on purpose. Returning null sends every dependency - the loader
        /// assembly, RevitAPI, RevitAPIUI, the framework - to the default context, where Revit
        /// already loaded them. Loading a second copy of any of those would break type identity in
        /// the most confusing way possible.
        /// </summary>
        private sealed class HandlerLoadContext : AssemblyLoadContext
        {
            internal HandlerLoadContext()
                : base("RevitMcpBridgeHandlers", true)
            {
            }

            protected override Assembly Load(AssemblyName assemblyName)
            {
                return null;
            }
        }
    }
}
