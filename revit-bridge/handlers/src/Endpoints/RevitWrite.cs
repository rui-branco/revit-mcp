using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitMcpBridge.Endpoints
{
    /// <summary>
    /// Transaction plumbing for the write endpoints.
    ///
    /// Every write request runs inside a TransactionGroup that is Assimilate()d on success and
    /// RollBack()ed on any failure. That is what makes one HTTP request equal exactly one Ctrl+Z in
    /// Revit, and it is what guarantees a request that fails half way through leaves no partial
    /// edit behind. This is a requirement of the bridge, not an optimisation - do not "simplify" a
    /// write endpoint down to a bare Transaction.
    ///
    /// InTransaction also installs the bridge's IFailuresPreprocessor on every transaction it
    /// opens - see StrictFailuresPreprocessor. That is what keeps a commit-time warning from
    /// putting up a modal dialog no unattended caller can answer, and what makes an ERROR roll the
    /// write back rather than being "resolved" by deleting the elements that raised it.
    ///
    /// Neither Commit() nor Assimilate() is trusted to have done what it was asked: both return a
    /// TransactionStatus and both can answer RolledBack without throwing. Ignoring that status is
    /// how a caller gets a 200 for a write that did not happen, so anything but Committed is raised
    /// here as TRANSACTION_ROLLED_BACK.
    /// </summary>
    internal static class RevitWrite
    {
        internal static T InGroup<T>(Document document, string name, Func<T> body)
        {
            using (TransactionGroup group = new TransactionGroup(document, name))
            {
                group.Start();

                try
                {
                    T result = body();

                    // Assimilate collapses every inner transaction into a single undo entry - and
                    // returns a status rather than throwing when it could not.
                    TransactionStatus status = group.Assimilate();
                    if (status != TransactionStatus.Committed)
                    {
                        throw RolledBack(name, status, null);
                    }

                    return result;
                }
                catch (Exception)
                {
                    if (group.HasStarted() && !group.HasEnded())
                    {
                        group.RollBack();
                    }

                    throw;
                }
            }
        }

        internal static void InTransaction(Document document, string name, Action body)
        {
            using (Transaction transaction = new Transaction(document, name))
            {
                transaction.Start();

                // Central on purpose. Every transaction the bridge opens gets the same failure
                // preprocessor, so no endpoint can forget it and leave an unattended run parked on
                // Revit's warning dialog. What it suppressed is readable at /revit-mcp/diagnostics.
                StrictFailuresPreprocessor failures = new StrictFailuresPreprocessor();

                FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(failures);

                // The preprocessor answers an error with ProceedWithRollBack; without this, the
                // messages survive the rollback and Revit raises the dialog anyway - which is the
                // one thing an unattended session cannot answer.
                options.SetClearAfterRollback(true);
                transaction.SetFailureHandlingOptions(options);

                body();

                // If body() throws we never get here and Dispose rolls the transaction back, then
                // InGroup rolls the whole group back on the way out.
                TransactionStatus status = transaction.Commit();
                if (status != TransactionStatus.Committed)
                {
                    // Commit() does not throw when Revit rolled back instead - it says so in the
                    // status, and a bridge that ignores it answers 200 for a write that never
                    // happened. Dispose has already unwound the transaction; raising here takes
                    // the group with it.
                    throw RolledBack(name, status, failures.Errors);
                }
            }
        }

        /// <summary>
        /// The one error for "Revit did not commit this". Carries the transaction name, the status
        /// Revit answered with and whatever the failure preprocessor recorded on the way past, so
        /// the caller is told what broke rather than just that something did.
        /// </summary>
        private static BridgeException RolledBack(string name, TransactionStatus status, List<string> errors)
        {
            string detail = "Revit rolled \"" + name + "\" back instead of committing it (status "
                + status + "). Nothing from this request was left in the model.";

            if (errors != null && errors.Count > 0)
            {
                detail += " Revit raised: " + string.Join(" | ", errors);
            }

            return new BridgeException(
                409,
                "TRANSACTION_ROLLED_BACK",
                detail + " Call /revit-mcp/diagnostics for the full record.");
        }
    }
}
