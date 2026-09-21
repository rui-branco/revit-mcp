using System;
using System.Collections.Generic;

namespace RevitMcpBridge
{
    /// <summary>
    /// The record of everything the bridge answered on the caller's behalf.
    ///
    /// Unattended operation means nothing may stop on a modal dialog or a transaction warning, so
    /// the bridge answers both itself (see BridgeDialogWatcher and BridgeFailuresPreprocessor).
    /// That is a loaded gun: a warning Revit raised and the bridge resolved may mean the model did
    /// something the caller never asked for. **Suppressed is not the same as swallowed** - every
    /// suppressed item lands here and is served by /revit-mcp/diagnostics, so a caller that writes
    /// can see what it did not see.
    ///
    /// Two ring buffers, oldest evicted past <see cref="Capacity"/>, because a long unattended run
    /// must not grow memory without bound. "dropped" in the snapshot says when that happened, so a
    /// truncated buffer never reads as a quiet one.
    ///
    /// Static state on purpose: the dialog watcher lives on Revit's event, the failures
    /// preprocessor is constructed per transaction, and the endpoints are static methods - there is
    /// no instance any of them could share.
    /// </summary>
    internal static class BridgeDiagnostics
    {
        internal const int Capacity = 200;

        private static readonly Ring Dialogs = new Ring();
        private static readonly Ring Failures = new Ring();

        /// <summary>
        /// On by default: the bridge exists to be driven unattended, and a bridge that parks on the
        /// first dialog is not. Flipped at runtime through /revit-mcp/diagnostics/config.
        /// </summary>
        private static volatile bool _autoDismiss = true;

        internal static bool AutoDismiss
        {
            get { return _autoDismiss; }
            set { _autoDismiss = value; }
        }

        /// <summary>
        /// <paramref name="result"/> is the dialog result the bridge used, or null when it did not
        /// answer - an unanswered dialog is recorded too, because "Revit is sitting on a dialog you
        /// cannot see" is exactly what a caller staring at a REVIT_BUSY needs to be told.
        /// </summary>
        internal static void RecordDialog(string dialogId, string message, object result)
        {
            Dialogs.Add(new Dictionary<string, object>
            {
                { "at", Timestamp() },
                { "dialogId", dialogId },
                { "message", message },
                { "result", result },
                { "answered", result != null },
            });

            BridgeLog.Warn((result == null ? "Dialog not answered: " : "Dialog answered with "
                + result + ": ") + dialogId + " " + Summarise(message));
        }

        internal static void RecordFailure(string transaction, string severity, string message, string action)
        {
            Failures.Add(new Dictionary<string, object>
            {
                { "at", Timestamp() },
                { "transaction", transaction },
                { "severity", severity },
                { "message", message },
                { "action", action },
            });

            BridgeLog.Warn("Failure " + severity + " in \"" + transaction + "\" -> " + action + ": "
                + Summarise(message));
        }

        /// <summary>The /revit-mcp/diagnostics payload.</summary>
        internal static Dictionary<string, object> Snapshot()
        {
            return new Dictionary<string, object>
            {
                { "autoDismiss", AutoDismiss },
                { "capacity", Capacity },
                { "dropped", Dialogs.Dropped + Failures.Dropped },
                { "dialogs", Dialogs.Snapshot() },
                { "failures", Failures.Snapshot() },
            };
        }

        internal static void Clear()
        {
            Dialogs.Clear();
            Failures.Clear();
        }

        /// <summary>Same format as BridgeLog, so an entry can be found in the log file.</summary>
        private static string Timestamp()
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        }

        private static string Summarise(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return "(no message)";
            }

            if (message.Length <= 200)
            {
                return message;
            }

            return message.Substring(0, 200) + "...";
        }

        /// <summary>
        /// Fixed-capacity FIFO, newest last. Locked because dialogs are raised on Revit's main
        /// thread while a Snapshot may be serialised from a work item on it - cheap enough that
        /// being careful costs nothing.
        /// </summary>
        private sealed class Ring
        {
            private readonly object _gate = new object();
            private readonly Queue<Dictionary<string, object>> _entries =
                new Queue<Dictionary<string, object>>();

            private int _dropped;

            internal int Dropped
            {
                get
                {
                    lock (_gate)
                    {
                        return _dropped;
                    }
                }
            }

            internal void Add(Dictionary<string, object> entry)
            {
                lock (_gate)
                {
                    while (_entries.Count >= Capacity)
                    {
                        _entries.Dequeue();
                        _dropped++;
                    }

                    _entries.Enqueue(entry);
                }
            }

            internal List<object> Snapshot()
            {
                lock (_gate)
                {
                    List<object> copy = new List<object>(_entries.Count);
                    foreach (Dictionary<string, object> entry in _entries)
                    {
                        copy.Add(entry);
                    }

                    return copy;
                }
            }

            internal void Clear()
            {
                lock (_gate)
                {
                    _entries.Clear();
                    _dropped = 0;
                }
            }
        }
    }
}
