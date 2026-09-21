using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitMcpBridge.Endpoints
{
    /// <summary>
    /// Answers the failures Revit raises while a transaction commits, without ever letting Revit's
    /// own "fix" delete what the request just made.
    ///
    /// This replaces the loader's BridgeFailuresPreprocessor for every transaction RevitWrite opens.
    /// That one asks <c>HasResolutions()</c> first and calls <c>ResolveFailure</c> whenever the
    /// answer is yes - before it has looked at the severity. Revit's default resolution for a great
    /// many failures is "delete the offending elements", so an error raised by an insert was being
    /// recorded as "Resolved (Default)" while the elements that caused it were removed and the
    /// commit reported success. An unattended caller then read a successful response for a model
    /// that had quietly lost the thing it asked for.
    ///
    /// The rules here, severity first and no resolutions at all:
    ///   1. a warning -> DeleteWarning. Dismissed, recorded, the edit stands. Never ResolveFailure:
    ///      dismissing a warning cannot change the model, applying Revit's resolution can.
    ///   2. anything else (Error, DocumentCorruption) -> recorded, then ProceedWithRollBack. The
    ///      whole transaction goes back, RevitWrite sees the non-Committed status and raises, and
    ///      the transaction group unwinds behind it. Nothing partial survives and nothing is
    ///      deleted on the caller's behalf.
    ///
    /// Every failure, dismissed or rolled back, still lands in BridgeDiagnostics - suppressed is
    /// not swallowed, and /revit-mcp/diagnostics is where it is read back.
    /// </summary>
    internal sealed class StrictFailuresPreprocessor : IFailuresPreprocessor
    {
        private readonly List<string> _errors = new List<string>();

        /// <summary>
        /// The descriptions of the failures that forced the rollback, in the order Revit raised
        /// them. RevitWrite puts them in the BridgeException so the caller is told what broke rather
        /// than just that something did.
        /// </summary>
        internal List<string> Errors
        {
            get { return _errors; }
        }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            string transaction = SafeTransactionName(accessor);
            bool rollback = false;

            // GetFailureMessages hands back a snapshot, so deleting while walking it is safe -
            // which it would not be over a live collection.
            foreach (FailureMessageAccessor failure in accessor.GetFailureMessages())
            {
                FailureSeverity severity = failure.GetSeverity();
                string description = SafeDescription(failure);

                if (severity == FailureSeverity.Warning)
                {
                    accessor.DeleteWarning(failure);
                    BridgeDiagnostics.RecordFailure(
                        transaction, severity.ToString(), description, "DeleteWarning");
                    continue;
                }

                _errors.Add(description);
                BridgeDiagnostics.RecordFailure(
                    transaction, severity.ToString(), description, "RolledBack");
                rollback = true;
            }

            // ProceedWithRollBack, not Continue: Continue would hand the error back to Revit, which
            // for an unattended session means the error dialog and a parked main thread. Rolling
            // back here is the same outcome the bridge wants anyway, reached without a dialog -
            // and RevitWrite asks for SetClearAfterRollback so the failures go back with it.
            return rollback ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }

        private static string SafeTransactionName(FailuresAccessor accessor)
        {
            try
            {
                return accessor.GetTransactionName();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string SafeDescription(FailureMessageAccessor failure)
        {
            try
            {
                return failure.GetDescriptionText();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
