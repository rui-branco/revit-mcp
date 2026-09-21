using System;
using System.Reflection;

namespace RevitMcpBridge
{
    internal static class BridgeInfo
    {
        internal static readonly string Version = VersionOf(typeof(BridgeInfo).Assembly);

        /// <summary>
        /// Takes the assembly rather than reading its own, because the two halves of the bridge
        /// version independently: /revit-mcp/reload reports the version of the handlers assembly it
        /// just loaded, which is the number that actually changed.
        /// </summary>
        internal static string VersionOf(Assembly assembly)
        {
            try
            {
                AssemblyInformationalVersionAttribute informational =
                    assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();

                if (informational != null && !string.IsNullOrEmpty(informational.InformationalVersion))
                {
                    string value = informational.InformationalVersion;

                    // Strip the "+<commit sha>" the SDK appends via SourceLink.
                    int plus = value.IndexOf('+');
                    if (plus > 0)
                    {
                        value = value.Substring(0, plus);
                    }

                    return value;
                }

                Version fileVersion = assembly.GetName().Version;
                return fileVersion == null ? "unknown" : fileVersion.ToString();
            }
            catch
            {
                return "unknown";
            }
        }
    }
}
