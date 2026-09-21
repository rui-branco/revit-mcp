using System;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace RevitMcpBridge
{
    /// <summary>
    /// Answers Revit's modal dialogs so an unattended run cannot stall on one.
    ///
    /// A dialog is fatal to this bridge in a way an exception is not: Revit's main thread parks
    /// inside the dialog's own message loop, the ExternalEvent pump never gets to run, and every
    /// request in flight and after it times out with REVIT_BUSY until a human clicks the button.
    /// There is nobody to click it in an unattended run - so the bridge clicks it.
    ///
    /// The answer is deliberately conservative: OK for an ordinary dialog, Cancel for anything
    /// whose id or text sounds destructive, on the principle that the safe answer to a question
    /// nobody read is "no". Both are recorded in BridgeDiagnostics, as is a dialog seen while
    /// auto-dismiss is off, because a dialog nobody can see is the whole reason REVIT_BUSY happens.
    /// </summary>
    internal sealed class BridgeDialogWatcher : IDisposable
    {
        /// <summary>
        /// The Win32 dialog result ids OverrideResult takes: 1 = IDOK, 2 = IDCANCEL.
        /// Autodesk.Revit.UI.TaskDialogResult numbers itself the same way (Ok = 1, Cancel = 2), so
        /// one pair of constants covers both kinds of dialog.
        /// </summary>
        private const int ResultOk = 1;

        private const int ResultCancel = 2;

        /// <summary>
        /// Matched against the dialog id and its message. Crude on purpose: the cost of cancelling
        /// a harmless dialog is one failed request, the cost of OK-ing a destructive one is the
        /// user's model.
        /// </summary>
        private static readonly string[] DestructiveHints =
        {
            "delete", "remove", "unload", "discard", "overwrite", "replace", "purge", "save",
            "close", "erase", "detach", "relinquish",
        };

        private UIControlledApplication _application;

        /// <summary>Called on Revit's main thread from BridgeHost.Start.</summary>
        internal void Start(UIControlledApplication application)
        {
            _application = application;
            _application.DialogBoxShowing += OnDialogBoxShowing;
        }

        public void Dispose()
        {
            UIControlledApplication application = _application;
            _application = null;

            if (application != null)
            {
                try
                {
                    application.DialogBoxShowing -= OnDialogBoxShowing;
                }
                catch (Exception ex)
                {
                    BridgeLog.Error("Could not unsubscribe from DialogBoxShowing", ex);
                }
            }
        }

        private void OnDialogBoxShowing(object sender, DialogBoxShowingEventArgs args)
        {
            string dialogId = args.DialogId;
            string message = MessageOf(args);

            if (!BridgeDiagnostics.AutoDismiss)
            {
                // Recorded, not answered: the dialog stays on screen for whoever is at the machine.
                BridgeDiagnostics.RecordDialog(dialogId, message, null);
                return;
            }

            int result = ChooseResult(dialogId, message);

            try
            {
                args.OverrideResult(result);
            }
            catch (Exception ex)
            {
                // A dialog that refuses the override is still on screen and the request that
                // triggered it will time out with REVIT_BUSY. Nothing to do but say so.
                BridgeLog.Error("Could not answer dialog " + dialogId, ex);
                BridgeDiagnostics.RecordDialog(dialogId, message, null);
                return;
            }

            BridgeDiagnostics.RecordDialog(dialogId, message, result);
        }

        private static int ChooseResult(string dialogId, string message)
        {
            if (SoundsDestructive(dialogId) || SoundsDestructive(message))
            {
                return ResultCancel;
            }

            return ResultOk;
        }

        private static bool SoundsDestructive(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (string hint in DestructiveHints)
            {
                if (text.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The base event args carry an id but no text; only the TaskDialog and MessageBox
        /// specialisations know what the dialog actually said. Null is an honest answer for the
        /// rest - a dialog Revit only knows by id.
        /// </summary>
        private static string MessageOf(DialogBoxShowingEventArgs args)
        {
            TaskDialogShowingEventArgs taskDialog = args as TaskDialogShowingEventArgs;
            if (taskDialog != null)
            {
                return taskDialog.Message;
            }

            MessageBoxShowingEventArgs messageBox = args as MessageBoxShowingEventArgs;
            if (messageBox != null)
            {
                return messageBox.Message;
            }

            return null;
        }
    }
}
