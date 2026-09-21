using System;
using System.IO;
using System.Text;

namespace RevitMcpBridge
{
    /// <summary>
    /// Dirt-simple rolling log. Logging must never throw: a failed write is swallowed, because the
    /// alternative is taking down a Revit session over a log file.
    /// </summary>
    internal static class BridgeLog
    {
        private const long MaxBytes = 1024 * 1024;

        private static readonly object Gate = new object();

        /// <summary>
        /// Under LocalAppData on purpose. Windows Controlled Folder Access blocks writes to
        /// Documents/Pictures and surfaces them as confusing IO errors; LocalAppData is always
        /// writable by the running user.
        /// </summary>
        internal static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RevitMcpBridge",
            "bridge.log");

        internal static void Info(string message)
        {
            Write("INFO ", message, null);
        }

        internal static void Warn(string message)
        {
            Write("WARN ", message, null);
        }

        internal static void Error(string message, Exception error)
        {
            Write("ERROR", message, error);
        }

        private static void Write(string level, string message, Exception error)
        {
            try
            {
                StringBuilder line = new StringBuilder();
                line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                line.Append(' ');
                line.Append(level);
                line.Append(" [");
                line.Append(Environment.CurrentManagedThreadId);
                line.Append("] ");
                line.Append(message);

                if (error != null)
                {
                    line.AppendLine();
                    line.Append(error);
                }

                lock (Gate)
                {
                    string directory = Path.GetDirectoryName(LogPath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes)
                    {
                        File.Delete(LogPath);
                    }

                    File.AppendAllText(LogPath, line.ToString() + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // Intentionally ignored - see the class comment.
            }
        }
    }
}
