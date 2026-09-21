using System;
using Autodesk.Revit.DB;

namespace RevitMcpBridge
{
    /// <summary>
    /// Answers the failures Revit raises while a transaction commits.
    ///
    /// Without one of these, a warning ("elements are slightly off axis", "the wall overlaps
    /// another") stops the commit and puts up the warning dialog - the same parked main thread and
    /// the same REVIT_BUSY as any other modal dialog, except this one is raised by the bridge's own
    /// write. So every transaction the bridge opens gets this preprocessor, wired in centrally in
    /// RevitWrite.InTransaction rather than per endpoint, where an endpoint could forget it.
    ///
    /// The rules, in order:
    ///   1. the failure offers a resolution  -> ResolveFailure (Revit's own fix for it),
    ///   2. otherwise it is a warning        -> DeleteWarning (dismiss it, the edit stands),
    ///   3. otherwise it is an error         -> left alone.
    /// Step 3 is deliberate: DeleteWarning refuses anything that is not a warning, and suppressing
    /// an error would let a broken edit commit. Revit handles it the way it normally would, which
    /// for the bridge means the transaction fails and RevitWrite rolls the whole group back.
    ///
    /// Every one of the three is recorded in BridgeDiagnostics. A resolved warning is exactly the
    /// case where the model quietly did something the caller did not ask for.
    /// </summary>
    internal sealed class BridgeFailuresPreprocessor : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            string transaction = SafeTransactionName(accessor);
            bool resolved = false;

            // GetFailureMessages hands back a snapshot, so resolving/deleting while walking it is
            // safe - which it would not be over a live collection.
            foreach (FailureMessageAccessor failure in accessor.GetFailureMessages())
            {
                FailureSeverity severity = failure.GetSeverity();
                string description = SafeDescription(failure);

                if (failure.HasResolutions())
                {
                    string resolution = failure.GetCurrentResolutionType().ToString();
                    accessor.ResolveFailure(failure);
                    BridgeDiagnostics.RecordFailure(
                        transaction, severity.ToString(), description, "Resolved (" + resolution + ")");
                    resolved = true;
                    continue;
                }

                if (severity == FailureSeverity.Warning)
                {
                    accessor.DeleteWarning(failure);
                    BridgeDiagnostics.RecordFailure(
                        transaction, severity.ToString(), description, "DeleteWarning");
                    continue;
                }

                BridgeDiagnostics.RecordFailure(
                    transaction, severity.ToString(), description, "Unresolved");
            }

            // ProceedWithCommit is what tells Revit to re-run the checks with the resolutions
            // applied; Continue is the right answer when nothing was resolved, including when
            // warnings were merely deleted.
            return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
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
