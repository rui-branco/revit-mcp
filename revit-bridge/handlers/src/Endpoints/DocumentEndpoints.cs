using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcpBridge.Endpoints
{
    /// <summary>
    /// Document-level endpoints: things that act on the set of open documents rather than on the
    /// contents of one.
    ///
    /// Three deliberate differences from every other endpoint:
    ///
    /// 1. **No transaction and no TransactionGroup.** Creating, saving, closing and opening a
    ///    document are not transactable model edits - Revit throws if they happen inside a
    ///    transaction - so RevitWrite is not used here.
    /// 2. **No active document is required *centrally*.** The NO_ACTIVE_DOCUMENT guard is opt-in:
    ///    each endpoint calls RevitFacts.RequireDocument itself. Save / SaveAs do; NewDocument and
    ///    Open do not, which is what makes them usable from a Revit sitting on the start page - the
    ///    exact situation a caller creating or opening a project is normally in. Close does not
    ///    either, but for a different reason: closing nothing is answered {"closed": false} rather
    ///    than raised as an error, so a caller tidying up never has to check first.
    /// 3. **Closing takes a detour.** Revit refuses Document.Close on the *active* document, so
    ///    Close gives Revit something else to be active on first - see CloseDocument. That, with
    ///    NewDocument's "overwrite", is what lets one project be wiped and rebuilt at the same path
    ///    instead of being versioned into a new filename on every attempt.
    /// </summary>
    internal static class DocumentEndpoints
    {
        /// <summary>
        /// Body: {templatePath, savePath, overwrite?}. Creates a project from the template, saves
        /// it to savePath and opens it as the active document.
        ///
        /// {"overwrite": true} is how a project is rebuilt in place: the existing file is closed if
        /// Revit has it open, deleted together with Revit's own "&lt;name&gt;.0001.rvt" backups,
        /// and built again at the same path. Without it an existing file is FILE_EXISTS exactly as
        /// before - the default overwrites nothing.
        /// </summary>
        internal static object NewDocument(UIApplication app, JsonElement body)
        {
            string templatePath = JsonBody.RequireString(body, "templatePath");
            string savePath = JsonBody.RequireString(body, "savePath");
            bool overwrite = JsonBody.OptionalBool(body, "overwrite", false);

            if (!File.Exists(templatePath))
            {
                throw BridgeException.NotFound(
                    "TEMPLATE_NOT_FOUND",
                    "No project template at \"" + templatePath + "\". Revit installs its templates "
                        + "under C:\\ProgramData\\Autodesk\\RVT <year>\\Templates; pass the full path "
                        + "to an .rte file that exists.");
            }

            // Never silently overwrite somebody's project: only an explicit overwrite may.
            if (File.Exists(savePath))
            {
                if (!overwrite)
                {
                    throw new BridgeException(
                        409,
                        "FILE_EXISTS",
                        "\"" + savePath + "\" already exists. Pass {\"overwrite\": true} to delete "
                            + "it and its backups and rebuild the project in place, or pick a "
                            + "different \"savePath\" - the bridge never overwrites a project by "
                            + "default.");
                }

                // Revit keeps an open project's file locked, so it has to let go of it before
                // anything on disk can be deleted.
                Document open = FindOpenDocument(app, savePath);
                if (open != null)
                {
                    CloseDocument(app, open, false);
                }

                DeleteProjectAndBackups(savePath);
            }

            string directory = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Document created = app.Application.NewProjectDocument(templatePath);
            created.SaveAs(savePath);

            // Closed and reopened through the UI application: NewProjectDocument hands back a
            // database-only document, and only OpenAndActivateDocument gives it a window and makes
            // it the active document the other endpoints work against.
            created.Close(false);

            UIDocument opened = app.OpenAndActivateDocument(savePath);
            Document document = opened.Document;

            return new Dictionary<string, object>
            {
                { "path", document.PathName },
                { "title", document.Title },
                { "templatePath", templatePath },
            };
        }

        /// <summary>
        /// Body: {}. Saves the active document in place.
        ///
        /// A document that has never been saved has nowhere to save to. Revit's own answer to that
        /// is the Save As file browser - a modal dialog, i.e. a parked main thread and a REVIT_BUSY
        /// for everybody - so the bridge refuses up front with NOT_SAVEABLE instead.
        /// </summary>
        internal static object Save(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            // Empty, not null, for a model that has never been saved - see ReadEndpoints.Status.
            if (string.IsNullOrEmpty(document.PathName))
            {
                throw new BridgeException(
                    409,
                    "NOT_SAVEABLE",
                    "The active document has never been saved, so it has no path to save to. Use "
                        + "/revit-mcp/document/save-as with a \"savePath\" instead.");
            }

            document.Save();

            return new Dictionary<string, object>
            {
                { "path", document.PathName },
                { "saved", true },
            };
        }

        /// <summary>
        /// Body: {savePath, overwrite?}. Saves the active document to a new path and keeps working
        /// in it. Refuses to replace an existing file unless {"overwrite": true} says so, on the
        /// same principle as NewDocument: the bridge never silently overwrites a project.
        /// </summary>
        internal static object SaveAs(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            string savePath = JsonBody.RequireString(body, "savePath");
            bool overwrite = JsonBody.OptionalBool(body, "overwrite", false);

            if (File.Exists(savePath) && !overwrite)
            {
                throw new BridgeException(
                    409,
                    "FILE_EXISTS",
                    "\"" + savePath + "\" already exists. Pass {\"overwrite\": true} to replace it, "
                        + "or pick a different \"savePath\" - the bridge never overwrites a project "
                        + "by default.");
            }

            string directory = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Without the options overload Revit throws rather than replacing the file, even when
            // the caller has already said overwrite.
            SaveAsOptions options = new SaveAsOptions();
            options.OverwriteExistingFile = overwrite;

            document.SaveAs(savePath, options);

            return new Dictionary<string, object>
            {
                { "path", document.PathName },
            };
        }

        /// <summary>
        /// Body: {path}. Opens an existing .rvt and makes it the active document. Works with no
        /// active document, which is the whole point of it.
        /// </summary>
        internal static object Open(UIApplication app, JsonElement body)
        {
            string path = JsonBody.RequireString(body, "path");

            // Checked here so a typo is a clean 404 rather than a Revit file-browser dialog on a
            // main thread nobody is watching.
            if (!File.Exists(path))
            {
                throw BridgeException.NotFound(
                    "FILE_NOT_FOUND",
                    "No file at \"" + path + "\". Pass the full path of an existing .rvt file.");
            }

            UIDocument opened = app.OpenAndActivateDocument(path);
            Document document = opened.Document;

            return new Dictionary<string, object>
            {
                { "path", document.PathName },
                { "title", document.Title },
            };
        }

        /// <summary>
        /// Body: {save?}. Closes the active document, discarding changes unless {"save": true},
        /// and reports what is active once it is gone.
        ///
        /// Closing nothing is not an error: it answers {"closed": false}. A caller tidying up at
        /// the end of an unattended run should not have to ask whether anything is open first.
        /// </summary>
        internal static object Close(UIApplication app, JsonElement body)
        {
            bool save = JsonBody.OptionalBool(body, "save", false);

            // Deliberately not RequireDocument - see the summary.
            UIDocument uiDocument = app.ActiveUIDocument;
            if (uiDocument == null || uiDocument.Document == null)
            {
                return new Dictionary<string, object>
                {
                    { "closed", false },
                };
            }

            return CloseDocument(app, uiDocument.Document, save);
        }

        /// <summary>
        /// Closes <paramref name="document"/> - active or not - and reports what is active
        /// afterwards.
        ///
        /// Revit refuses Document.Close on the active document outright ("The active document may
        /// not be closed from the API", InvalidOperationException) and offers no way to make
        /// nothing active. The way through is to give Revit something else to be active on first:
        /// another open document, or a blank scratch project when this is the only one. A document
        /// that is not the active one is closed directly, with none of that.
        /// </summary>
        private static Dictionary<string, object> CloseDocument(UIApplication app, Document document, bool save)
        {
            // Read before Close(): the Document object is dead afterwards and touching it throws.
            string path = document.PathName;
            string title = document.Title;

            bool scratch = false;
            if (IsActiveDocument(app, document))
            {
                scratch = ActivateAnotherDocument(app, document);
            }

            bool closed = document.Close(save);

            Document active = app.ActiveUIDocument == null ? null : app.ActiveUIDocument.Document;

            return new Dictionary<string, object>
            {
                { "closed", closed },
                { "path", path },
                { "title", title },
                { "activePath", active == null ? null : active.PathName },
                { "activeTitle", active == null ? null : active.Title },
                { "activeIsScratch", scratch },
            };
        }

        private static bool IsActiveDocument(UIApplication app, Document document)
        {
            UIDocument uiDocument = app.ActiveUIDocument;
            if (uiDocument == null || uiDocument.Document == null)
            {
                return false;
            }

            // Document overrides Equals against the underlying Revit document, which is what makes
            // this safe: iterating Application.Documents can hand back a different wrapper object
            // for the very document that is already active.
            return uiDocument.Document.Equals(document);
        }

        /// <summary>
        /// Makes some document other than <paramref name="document"/> the active one so that
        /// document can be closed. Returns true when that meant standing up a scratch project.
        ///
        /// OpenAndActivateDocument on a file Revit already has open activates it instead of opening
        /// it twice, which is what makes the first branch a pure activation. It needs a path, so a
        /// sibling that has never been saved is no use here and is skipped.
        /// </summary>
        private static bool ActivateAnotherDocument(UIApplication app, Document document)
        {
            foreach (Document candidate in app.Application.Documents)
            {
                if (candidate.Equals(document) || candidate.IsLinked || string.IsNullOrEmpty(candidate.PathName))
                {
                    continue;
                }

                app.OpenAndActivateDocument(candidate.PathName);
                return false;
            }

            OpenScratchDocument(app);
            return true;
        }

        /// <summary>
        /// Opens a blank project in the temp directory and makes it active, purely so the document
        /// the caller asked about stops being the active one. Once Revit has opened a document
        /// something is always active, so closing the last real project means putting something in
        /// its place.
        ///
        /// Built the way NewDocument builds a project: NewProjectDocument hands back a
        /// database-only document with no window, and only OpenAndActivateDocument gives it one.
        /// The scratch is harmless - a blank metric project under %TEMP% - and it is deliberately
        /// left open, because the next close finds it as the other document and needs no new one.
        /// </summary>
        private static void OpenScratchDocument(UIApplication app)
        {
            string scratchPath = Path.Combine(Path.GetTempPath(), "revit-mcp-scratch.rvt");

            // The scratch itself is what is being closed: it cannot stand in for itself, and Revit
            // has its file locked, so this one needs a name of its own.
            if (FindOpenDocument(app, scratchPath) != null)
            {
                scratchPath = Path.Combine(
                    Path.GetTempPath(),
                    "revit-mcp-scratch-"
                        + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                        + ".rvt");
            }

            try
            {
                // No template: NewProjectDocument(UnitSystem) needs nothing installed, and this
                // document exists only to hold Revit's attention for one call.
                Document scratch = app.Application.NewProjectDocument(UnitSystem.Metric);

                SaveAsOptions options = new SaveAsOptions();
                options.OverwriteExistingFile = true;
                scratch.SaveAs(scratchPath, options);
                scratch.Close(false);

                app.OpenAndActivateDocument(scratchPath);
            }
            catch (Exception error)
            {
                throw new BridgeException(
                    409,
                    "LAST_DOCUMENT_CANNOT_CLOSE",
                    "This is the only document Revit has open, and Revit will not close the active "
                        + "document from the API. The bridge tried to make a scratch project at \""
                        + scratchPath + "\" active in its place so this one could be closed, and "
                        + "could not: " + error.Message + " Open another project and retry, or "
                        + "close this one from Revit's own UI.");
            }
        }

        /// <summary>
        /// The open document whose file is <paramref name="path"/>, or null. Revit locks the file
        /// of every open document, so this is what says whether a path can be deleted.
        /// </summary>
        private static Document FindOpenDocument(UIApplication app, string path)
        {
            string full = Path.GetFullPath(path);

            foreach (Document candidate in app.Application.Documents)
            {
                if (candidate.IsLinked || string.IsNullOrEmpty(candidate.PathName))
                {
                    continue;
                }

                // Deliberately no GetFullPath on the candidate: a cloud model's PathName is not a
                // file path and would throw. An open local document already reports its full path.
                if (string.Equals(candidate.PathName, full, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// Deletes the project file and the backups Revit writes next to it. Anything still holding
        /// one of them is a FILE_LOCKED naming that exact path - never a quiet fallback to a
        /// different filename, which is how a project ends up versioned into junk.
        /// </summary>
        private static void DeleteProjectAndBackups(string savePath)
        {
            List<string> targets = new List<string>();
            targets.Add(savePath);
            targets.AddRange(BackupsOf(savePath));

            foreach (string target in targets)
            {
                try
                {
                    File.Delete(target);
                }
                catch (Exception error)
                {
                    throw new BridgeException(
                        409,
                        "FILE_LOCKED",
                        "\"" + target + "\" could not be deleted: " + error.Message + " Something "
                            + "still has that file open - Revit itself, an Explorer preview, or "
                            + "another application. Close it and retry; the bridge will not build "
                            + "the project under a different name instead.");
                }
            }
        }

        /// <summary>
        /// Revit's backups of "C:\Projects\House.rvt" are "C:\Projects\House.0001.rvt",
        /// "House.0002.rvt" and so on. Matched on exactly that shape - four digits between the name
        /// and the extension - so a "House.old.rvt" of the user's own is never swept up with them.
        /// </summary>
        private static List<string> BackupsOf(string savePath)
        {
            List<string> backups = new List<string>();

            string directory = Path.GetDirectoryName(savePath);
            string name = Path.GetFileNameWithoutExtension(savePath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return backups;
            }

            foreach (string candidate in Directory.GetFiles(directory, name + ".????.rvt"))
            {
                // The wildcard is only a pre-filter - Windows matches "?" loosely - so the shape is
                // checked properly here.
                string stem = Path.GetFileNameWithoutExtension(candidate);
                if (stem.Length != name.Length + 5)
                {
                    continue;
                }

                bool numeric = true;
                foreach (char digit in stem.Substring(name.Length + 1))
                {
                    if (!char.IsDigit(digit))
                    {
                        numeric = false;
                        break;
                    }
                }

                if (numeric)
                {
                    backups.Add(candidate);
                }
            }

            return backups;
        }
    }
}
