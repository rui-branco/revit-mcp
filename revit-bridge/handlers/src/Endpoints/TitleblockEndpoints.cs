using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcpBridge.Endpoints
{
    /// <summary>
    /// Title block families, read from the inside.
    ///
    /// Everything else here reads a title block from the project: /revit-mcp/titleblocks says which
    /// family types are loaded and nothing more. What is actually printed on the sheet - the logo,
    /// the consultant placeholders somebody typed into the stock family, the label that grows to
    /// hold a long project name - lives in the FAMILY, and the only way to see it from the API is
    /// Document.EditFamily.
    ///
    /// That call hands back an INDEPENDENT COPY of the family as its own Document. Editing it
    /// changes nothing until the copy is loaded back with LoadFamily, and this endpoint never does
    /// that: it opens the copy, reads it, and closes it with saveModified false in a finally. No
    /// transaction is opened in either document, so there is nothing to undo and nothing to roll
    /// back. The project, the loaded family and the file on disk are exactly as they were.
    ///
    /// Same units as the rest of the bridge: Revit internal units, decimal feet, unconverted. For
    /// annotation inside a title block family that means PAPER feet - a 5 mm label reads 0.0164.
    /// </summary>
    internal static class TitleblockEndpoints
    {
        /// <summary>
        /// Hard ceiling on element rows. A stock title block runs to a couple of hundred elements;
        /// past this the counts still come back, so nothing is hidden, only abbreviated.
        /// </summary>
        private const int MaxElements = 2000;

        /// <summary>Two text sizes closer than this are the same size. Decimal feet, so 1e-5 is
        /// three thousandths of a millimetre.</summary>
        private const double TextSizeTolerance = 1e-5;

        /// <summary>
        /// Body: {symbolId}. Everything in the title block family behind that loaded type: its
        /// family parameters, and every non-type element in the family document.
        ///
        /// Per element: id, the Revit API class, category, name, type id/name, the view it is drawn
        /// in, its location, its bounding box, and then by what it is -
        /// - a **text element** (a plain note and a label are both TextElement) reports its content,
        ///   insertion point, box width/height, both alignments, its text type and that type's text
        ///   size, plus "isTextNote" - true is a literal note, false is the other kind;
        /// - an **image** reports its size and scale plus the ImageType behind it: path, source,
        ///   status and pixel dimensions;
        /// - an **import** reports whether it is linked and the import type's name, which is the
        ///   file it came from;
        /// - a **curve** reports its line style;
        /// - a **dimension** reports its value, whether it is locked, its segments and the family
        ///   parameter labelling it - the constraints holding the sheet border together;
        /// - a **reference plane** reports its two ends and its normal.
        ///
        /// The dynamic half of the title block is "familyParameters": each family parameter with its
        /// storage type, formula, instance/type flag and the value the current family type carries,
        /// plus "associatedElementIds" - the elements in the family whose own parameters are
        /// associated to it, read off FamilyParameter.AssociatedParameters. The same association is
        /// repeated on each element row as "labelOf". That is the evidence for which content is
        /// driven by a parameter and which is literal text; it is reported as the association Revit
        /// holds, not as a claim about what the user sees.
        ///
        /// Read-only, and it says so on the response.
        /// </summary>
        internal static object Inspect(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            long symbolId = JsonBody.AsLong(RequireValue(body, "symbolId"), "symbolId");

            FamilySymbol symbol = RequireTitleblockSymbol(document, symbolId, "inspects");
            Family family = RequireEditableFamily(symbol, symbolId, "read");

            Document familyDocument = null;
            Dictionary<string, object> report;

            try
            {
                familyDocument = OpenForEditing(document, family, "read or changed");

                report = Describe(document, symbol, family, familyDocument);
            }
            finally
            {
                if (familyDocument != null)
                {
                    // saveModified false: the copy EditFamily handed back is discarded whole.
                    familyDocument.Close(false);
                }
            }

            return report;
        }

        /// <summary>
        /// Body: {symbolId, expectedFamilyName, removeIds?: [id], textEdits?: [{id, text}],
        /// labelSizes?: [{id, size}], newNotes?: [{text, point: {x, y}, size, width?}], viewId?,
        /// dryRun?}. The write half of the inspection: strip the stock content out of a title block
        /// family, retext and resize what stays, add notes - and load the result back into THIS
        /// project.
        ///
        /// The same EditFamily copy the inspection reads is what gets edited here, so nothing is
        /// touched until Document.LoadFamily puts the copy back. Everything between is refusable,
        /// and this endpoint is built out of refusals:
        ///
        /// 1. **"expectedFamilyName" is required and must match.** Every id in the request was read
        ///    out of one family document; sending them at another one would delete whatever happens
        ///    to carry those ids. The name is checked before the family is even opened.
        /// 2. **Only a TextNote or an ImageInstance can be removed.** That is the stock logo and the
        ///    literal placeholders somebody typed - the things the inspection proves are not
        ///    parameter-driven. A label (a TextElement that is not a TextNote) is refused by name:
        ///    deleting one is how a title block loses "Project Name" for good.
        /// 3. **Deletion is verified, not trusted.** Document.Delete reports everything it took,
        ///    dependents included; anything in that set the caller did not name rolls the whole
        ///    transaction back. So does a label or a schedule instance that existed before the edit
        ///    and does not exist after it.
        /// 4. **Resizing duplicates the text type rather than editing it.** A type is shared: a 13 mm
        ///    label and every other element on that type would all move together. An existing type of
        ///    the requested size is reused, otherwise the element's own type is duplicated and only
        ///    the named elements are pointed at the copy. An element Revit will not retype is
        ///    reported as refused and left alone - the removals still stand.
        /// 5. **"dryRun" DEFAULTS TO TRUE.** The default call opens the family, resolves every id and
        ///    every text type, reports what it would do, and closes the copy unsaved. Pass dryRun
        ///    false to apply it.
        ///
        /// On the way out the copy is closed with saveModified false in a finally, applied or not:
        /// the .rfa on disk is never written. Applying means the family in this project is the
        /// edited one and every sheet using it redraws; nothing is saved to disk there either until
        /// the project is saved.
        ///
        /// Lengths are Revit internal units (decimal feet), and annotation in a title block is PAPER
        /// feet: 6 mm text is 0.019685, and a point 1032 mm across the sheet is 3.385827.
        /// </summary>
        internal static object Edit(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            long symbolId = JsonBody.AsLong(RequireValue(body, "symbolId"), "symbolId");
            string expectedFamilyName = JsonBody.RequireString(body, "expectedFamilyName");
            bool dryRun = JsonBody.OptionalBool(body, "dryRun", true);

            FamilySymbol symbol = RequireTitleblockSymbol(document, symbolId, "edits");
            Family family = RequireEditableFamily(symbol, symbolId, "changed");

            string familyName = RevitFacts.SafeName(family);
            if (!string.Equals(familyName, expectedFamilyName, StringComparison.Ordinal))
            {
                // The guard that makes the ids in the request mean something.
                throw new BridgeException(
                    409,
                    "FAMILY_NAME_MISMATCH",
                    "Family type " + symbolId + " belongs to \"" + familyName + "\", not \""
                        + expectedFamilyName + "\". The element ids in this request were read from "
                        + "some other family document, so nothing was opened and nothing was "
                        + "changed. Inspect this family and resend the ids it reports.");
            }

            Document familyDocument = null;

            try
            {
                familyDocument = OpenForEditing(document, family, "changed");

                return ApplyEdit(document, symbol, family, familyDocument, body, dryRun);
            }
            finally
            {
                if (familyDocument != null)
                {
                    // The copy is discarded whole either way: what LoadFamily already wrote into
                    // the project stays, and the .rfa on disk is never touched.
                    familyDocument.Close(false);
                }
            }
        }

        /// <summary>
        /// Everything that happens inside the family copy: read the state that has to survive,
        /// resolve every id in the request against this document, then either report the plan
        /// (dryRun) or run it in one transaction and load the family back.
        /// </summary>
        private static Dictionary<string, object> ApplyEdit(
            Document document,
            FamilySymbol symbol,
            Family family,
            Document familyDocument,
            JsonElement body,
            bool dryRun)
        {
            // Read before anything is planned: these are the ids the edit has to give back.
            List<long> labelIds = new List<long>();
            List<long> scheduleIds = new List<long>();
            Dictionary<string, int> before = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (Element element in new FilteredElementCollector(familyDocument)
                .WhereElementIsNotElementType())
            {
                Count(before, element.GetType().Name);

                TextElement text = element as TextElement;
                if (text != null && !(text is TextNote))
                {
                    labelIds.Add(element.Id.Value);
                }

                if (element is ScheduleSheetInstance)
                {
                    scheduleIds.Add(element.Id.Value);
                }
            }

            List<Element> removals = PlanRemovals(familyDocument, body);
            List<TextEdit> textEdits = PlanTextEdits(familyDocument, body);
            List<SizeEdit> sizeEdits = PlanSizeEdits(familyDocument, body);
            List<NewNote> newNotes = PlanNewNotes(body);

            // Repurposing a stock placeholder and removing it are alternatives, not a sequence: an
            // element this request deletes cannot then be retexted or retyped. Asked here, where it
            // is a readable refusal, rather than at commit time as a dead-element failure.
            RequireNotRemoved(removals, textEdits, sizeEdits);

            ElementId noteViewId = null;
            ElementId noteSourceTypeId = null;

            if (newNotes.Count > 0)
            {
                noteViewId = NoteView(familyDocument, body);
                noteSourceTypeId = NoteSourceType(familyDocument);
            }

            Dictionary<string, object> report = new Dictionary<string, object>
            {
                { "dryRun", dryRun },
                { "units", "Revit internal units (decimal feet). Annotation inside a title block is "
                    + "PAPER feet - 6 mm text reads 0.019685." },
                { "symbol", new Dictionary<string, object>
                    {
                        { "id", symbol.Id.Value },
                        { "familyName", symbol.FamilyName },
                        { "typeName", RevitFacts.SafeName(symbol) },
                    }
                },
                { "family", new Dictionary<string, object>
                    {
                        { "id", family.Id.Value },
                        { "name", RevitFacts.SafeName(family) },
                    }
                },
                { "preserved", new Dictionary<string, object>
                    {
                        { "labelIds", labelIds },
                        { "scheduleInstanceIds", scheduleIds },
                    }
                },
                { "before", Tally(before) },
            };

            if (noteViewId != null)
            {
                Element view = familyDocument.GetElement(noteViewId);
                report["noteView"] = new Dictionary<string, object>
                {
                    { "id", noteViewId.Value },
                    { "name", RevitFacts.SafeName(view) },
                };
            }

            // Read off the elements while they are still there: an element this request deletes
            // cannot be asked what it was afterwards.
            List<object> removalRows = RemovalRows(removals, !dryRun);
            List<object> textRows = TextEditRows(textEdits, !dryRun);

            if (dryRun)
            {
                report["applied"] = false;
                report["note"] = "Nothing was changed. The family was opened with "
                    + "Document.EditFamily, every id and text type below was resolved against that "
                    + "copy, and the copy was closed without saving. No transaction was opened and "
                    + "the family was not loaded back. Send dryRun false to apply this.";

                report["removals"] = removalRows;
                report["textEdits"] = textRows;
                report["labelSizes"] = SizePlanRows(familyDocument, sizeEdits);
                report["newNotes"] = NotePlanRows(familyDocument, newNotes, noteSourceTypeId);

                return report;
            }

            Dictionary<string, ElementId> typeCache = new Dictionary<string, ElementId>(StringComparer.Ordinal);
            List<object> sizeRows = new List<object>();
            List<object> noteRows = new List<object>();

            RevitWrite.InTransaction(familyDocument, "Edit title block family", delegate
            {
                Remove(familyDocument, removals);

                foreach (TextEdit edit in textEdits)
                {
                    edit.Note.Text = edit.Text;
                }

                foreach (SizeEdit edit in sizeEdits)
                {
                    sizeRows.Add(Resize(familyDocument, edit, typeCache));
                }

                foreach (NewNote note in newNotes)
                {
                    noteRows.Add(Add(familyDocument, note, noteViewId, noteSourceTypeId, typeCache));
                }

                // Inside the transaction on purpose: a label or a schedule that did not survive the
                // edit rolls the edit back rather than being reported after the fact.
                RequireSurvivors(familyDocument, labelIds, "label");
                RequireSurvivors(familyDocument, scheduleIds, "schedule instance");
            });

            Dictionary<string, int> after = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Element element in new FilteredElementCollector(familyDocument)
                .WhereElementIsNotElementType())
            {
                Count(after, element.GetType().Name);
            }

            Family reloaded;

            try
            {
                // The copy goes back into the document it came out of. No SaveAs, no second project:
                // the family in THIS project becomes the edited one and every sheet using it redraws.
                reloaded = familyDocument.LoadFamily(document, new BridgeFamilyLoadOptions());
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                throw new BridgeException(
                    409,
                    "FAMILY_RELOAD_FAILED",
                    "The edits were made in the family copy but Revit refused to load it back: "
                        + ex.Message + " The copy has been discarded, so the project, the loaded "
                        + "family and the .rfa on disk are as they were.");
            }

            report["applied"] = true;
            report["note"] = "The family was edited in the copy Document.EditFamily handed back and "
                + "that copy was loaded into this project with Document.LoadFamily. The .rfa on "
                + "disk was not written; the project holds the edited family until it is saved.";

            report["removals"] = removalRows;
            report["textEdits"] = textRows;
            report["labelSizes"] = sizeRows;
            report["newNotes"] = noteRows;
            report["after"] = Tally(after);
            report["reload"] = new Dictionary<string, object>
            {
                { "familyId", reloaded == null ? (object)null : reloaded.Id.Value },
                { "familyName", reloaded == null ? null : RevitFacts.SafeName(reloaded) },
            };

            return report;
        }

        // --- the plan -----------------------------------------------------------------------------

        private sealed class TextEdit
        {
            internal TextNote Note;
            internal string From;
            internal string Text;
        }

        private sealed class SizeEdit
        {
            internal TextElement Element;
            internal double Size;
        }

        private sealed class NewNote
        {
            internal string Text;
            internal XYZ Point;
            internal double Size;
            internal double Width;
        }

        /// <summary>
        /// The elements "removeIds" names, refused unless every one of them is a literal note or an
        /// image. This is the whole safety story of the delete half: the classes here are the two the
        /// inspection reports as content nobody's parameters point at.
        /// </summary>
        private static List<Element> PlanRemovals(Document familyDocument, JsonElement body)
        {
            List<Element> removals = new List<Element>();

            foreach (long id in OptionalIds(body, "removeIds"))
            {
                Element element = RequireInFamily(familyDocument, id, "removeIds");

                if (element is TextNote || element is ImageInstance)
                {
                    removals.Add(element);
                    continue;
                }

                TextElement text = element as TextElement;
                if (text != null)
                {
                    throw BridgeException.BadRequest(
                        "Element " + id + " (\"" + text.Text + "\") is a label, not a literal text "
                            + "note: it is driven by a parameter and deleting it would take that "
                            + "content off every sheet for good. This endpoint removes TextNote and "
                            + "ImageInstance elements only. Nothing was changed.");
                }

                throw BridgeException.BadRequest(
                    "Element " + id + " is a " + element.GetType().Name + ". This endpoint removes "
                        + "TextNote and ImageInstance elements only - the stock logo and the literal "
                        + "placeholders. Lines, dimensions and reference planes hold the title block "
                        + "together and are refused. Nothing was changed.");
            }

            return removals;
        }

        private static List<TextEdit> PlanTextEdits(Document familyDocument, JsonElement body)
        {
            List<TextEdit> edits = new List<TextEdit>();

            foreach (JsonElement row in Rows(body, "textEdits"))
            {
                long id = JsonBody.AsLong(RequireValue(row, "id"), "textEdits[].id");
                string text = JsonBody.RequireString(row, "text");

                Element element = RequireInFamily(familyDocument, id, "textEdits");

                TextNote note = element as TextNote;
                if (note == null)
                {
                    throw BridgeException.BadRequest(
                        "Element " + id + " is a " + element.GetType().Name + ", not a TextNote. "
                            + "Only literal text can be retyped; a label's content comes from the "
                            + "parameter it is bound to, not from this request. Nothing was changed.");
                }

                edits.Add(new TextEdit { Note = note, From = note.Text, Text = text });
            }

            return edits;
        }

        private static List<SizeEdit> PlanSizeEdits(Document familyDocument, JsonElement body)
        {
            List<SizeEdit> edits = new List<SizeEdit>();

            foreach (JsonElement row in Rows(body, "labelSizes"))
            {
                long id = JsonBody.AsLong(RequireValue(row, "id"), "labelSizes[].id");
                double size = JsonBody.RequireDouble(row, "size");

                if (size <= 0.0)
                {
                    throw BridgeException.BadRequest(
                        "\"size\" for element " + id + " is " + size.ToString("0.######", CultureInfo.InvariantCulture)
                            + "; text size is a length in decimal feet and must be positive. 3 mm is "
                            + "0.009843. Nothing was changed.");
                }

                Element element = RequireInFamily(familyDocument, id, "labelSizes");

                TextElement text = element as TextElement;
                if (text == null)
                {
                    throw BridgeException.BadRequest(
                        "Element " + id + " is a " + element.GetType().Name + ", not a text element. "
                            + "\"labelSizes\" retypes labels and text notes. Nothing was changed.");
                }

                edits.Add(new SizeEdit { Element = text, Size = size });
            }

            return edits;
        }

        private static List<NewNote> PlanNewNotes(JsonElement body)
        {
            List<NewNote> notes = new List<NewNote>();

            foreach (JsonElement row in Rows(body, "newNotes"))
            {
                string text = JsonBody.RequireString(row, "text");
                XYZ point = RevitFacts.RequirePoint(row, "point");
                double size = JsonBody.RequireDouble(row, "size");
                double width = JsonBody.OptionalDouble(row, "width", 0.0);

                if (size <= 0.0)
                {
                    throw BridgeException.BadRequest(
                        "\"size\" on a new note is " + size.ToString("0.######", CultureInfo.InvariantCulture)
                            + "; text size is a length in decimal feet and must be positive. 3 mm is "
                            + "0.009843. Nothing was changed.");
                }

                notes.Add(new NewNote
                {
                    Text = text,

                    // Annotation in a title block is drawn on the sheet plane; whatever z was sent
                    // is not a third dimension there.
                    Point = new XYZ(point.X, point.Y, 0.0),
                    Size = size,
                    Width = width,
                });
            }

            return notes;
        }

        /// <summary>
        /// An id cannot be both removed and edited. Keeping a stock placeholder and retexting it is
        /// the way to put one line on every sheet without adding an element; deleting it is the
        /// other way. Asking for both is a contradiction, and it is refused before anything is
        /// written rather than raising on a dead element half way through the transaction.
        /// </summary>
        private static void RequireNotRemoved(
            List<Element> removals,
            List<TextEdit> textEdits,
            List<SizeEdit> sizeEdits)
        {
            HashSet<long> removed = new HashSet<long>();
            foreach (Element element in removals)
            {
                removed.Add(element.Id.Value);
            }

            if (removed.Count == 0)
            {
                return;
            }

            foreach (TextEdit edit in textEdits)
            {
                if (removed.Contains(edit.Note.Id.Value))
                {
                    throw BridgeException.BadRequest(
                        "Element " + edit.Note.Id.Value + " is in \"removeIds\" and in \"textEdits\": "
                            + "it cannot be deleted and retexted by the same request. Keep it and "
                            + "retext it, or remove it - not both. Nothing was changed.");
                }
            }

            foreach (SizeEdit edit in sizeEdits)
            {
                if (removed.Contains(edit.Element.Id.Value))
                {
                    throw BridgeException.BadRequest(
                        "Element " + edit.Element.Id.Value + " is in \"removeIds\" and in "
                            + "\"labelSizes\": it cannot be deleted and resized by the same request. "
                            + "Keep it and resize it, or remove it - not both. Nothing was changed.");
                }
            }
        }

        /// <summary>
        /// The view a new note is drawn in: "viewId" when the caller named one, otherwise the view
        /// the family's existing text is already in. A family with text in more than one view is
        /// ambiguous and is asked rather than guessed at.
        /// </summary>
        private static ElementId NoteView(Document familyDocument, JsonElement body)
        {
            JsonElement value;
            if (JsonBody.TryGet(body, "viewId", out value))
            {
                long id = JsonBody.AsLong(value, "viewId");

                View named = familyDocument.GetElement(new ElementId(id)) as View;
                if (named == null)
                {
                    throw BridgeException.BadRequest(
                        "\"viewId\" " + id + " is not a view in this family document. Inspect the "
                            + "family and use the ownerViewId its text elements report. Nothing was "
                            + "changed.");
                }

                return named.Id;
            }

            List<ElementId> views = new List<ElementId>();
            List<string> names = new List<string>();

            // Collected by cast rather than by OfClass: the class behind a label is not one
            // FilteredElementCollector takes, and a label is text on the sheet like any other.
            foreach (Element element in new FilteredElementCollector(familyDocument)
                .WhereElementIsNotElementType())
            {
                if (!(element is TextElement))
                {
                    continue;
                }

                ElementId viewId = element.OwnerViewId;
                if (viewId == null || viewId == ElementId.InvalidElementId)
                {
                    continue;
                }

                if (views.Contains(viewId))
                {
                    continue;
                }

                views.Add(viewId);
                names.Add(RevitFacts.SafeName(familyDocument.GetElement(viewId)) + " (" + viewId.Value + ")");
            }

            if (views.Count == 1)
            {
                return views[0];
            }

            if (views.Count == 0)
            {
                throw BridgeException.BadRequest(
                    "This family has no text to take a view from, so there is nowhere to put a new "
                        + "note. Name one with \"viewId\". Nothing was changed.");
            }

            throw BridgeException.BadRequest(
                "This family draws text in " + views.Count + " views (" + string.Join(", ", names)
                    + "), so \"newNotes\" is ambiguous. Name the one you mean with \"viewId\". "
                    + "Nothing was changed.");
        }

        /// <summary>The text type a new note's type is copied from: the family's default, or the
        /// first one it has.</summary>
        private static ElementId NoteSourceType(Document familyDocument)
        {
            ElementId defaultId = familyDocument.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            if (defaultId != null && defaultId != ElementId.InvalidElementId)
            {
                return defaultId;
            }

            foreach (Element element in new FilteredElementCollector(familyDocument).OfClass(typeof(TextNoteType)))
            {
                return element.Id;
            }

            throw BridgeException.BadRequest(
                "This family has no text note type, so a new note has no style to be drawn in. "
                    + "Nothing was changed.");
        }

        // --- the edit -----------------------------------------------------------------------------

        /// <summary>
        /// Deletes what was planned and reads back what Revit actually took. Document.Delete reports
        /// dependents as well as the elements named, and anything in that set the caller did not ask
        /// for raises - which rolls the transaction, and with it the whole edit, back.
        /// </summary>
        private static void Remove(Document familyDocument, List<Element> removals)
        {
            if (removals.Count == 0)
            {
                return;
            }

            List<ElementId> ids = new List<ElementId>();
            HashSet<long> asked = new HashSet<long>();

            foreach (Element element in removals)
            {
                ids.Add(element.Id);
                asked.Add(element.Id.Value);
            }

            ICollection<ElementId> deleted = familyDocument.Delete(ids);

            if (deleted == null)
            {
                return;
            }

            List<long> collateral = new List<long>();

            foreach (ElementId id in deleted)
            {
                if (!asked.Contains(id.Value))
                {
                    collateral.Add(id.Value);
                }
            }

            if (collateral.Count > 0)
            {
                throw new BridgeException(
                    409,
                    "UNEXPECTED_DELETION",
                    "Deleting the " + removals.Count + " element(s) named would have taken "
                        + collateral.Count + " more with them (" + string.Join(", ", collateral)
                        + "), which this request did not ask for. The edit was rolled back and the "
                        + "family was not loaded back into the project.");
            }
        }

        /// <summary>
        /// Points one text element at a type of the requested size. The type is reused if the family
        /// already has one that size and duplicated from this element's own type otherwise, so the
        /// other elements sharing that type keep the size they had. An element Revit will not retype
        /// is reported, not raised: the rest of the edit stands.
        /// </summary>
        private static Dictionary<string, object> Resize(
            Document familyDocument,
            SizeEdit edit,
            Dictionary<string, ElementId> typeCache)
        {
            Dictionary<string, object> row = new Dictionary<string, object>
            {
                { "id", edit.Element.Id.Value },
                { "class", edit.Element.GetType().Name },
                { "text", SafeText(edit.Element) },
                { "size", edit.Size },
            };

            ElementType source = familyDocument.GetElement(edit.Element.GetTypeId()) as ElementType;
            if (source == null)
            {
                row["action"] = "refused";
                row["reason"] = "The element has no text type to resize or copy.";
                return row;
            }

            row["fromTypeId"] = source.Id.Value;
            row["fromTypeName"] = RevitFacts.SafeName(source);
            row["fromSize"] = SizeOf(source);

            bool duplicated;
            ElementId typeId = TextTypeId(familyDocument, source, edit.Size, typeCache, out duplicated);

            Element target = familyDocument.GetElement(typeId);
            row["toTypeId"] = typeId.Value;
            row["toTypeName"] = RevitFacts.SafeName(target);

            if (!edit.Element.IsValidType(typeId))
            {
                // The documented way out: report the refusal and leave this element as it was.
                row["action"] = "refused";
                row["reason"] = "Revit reports that text type " + typeId.Value + " is not valid for "
                    + "this element, so its size was left unchanged.";
                return row;
            }

            try
            {
                edit.Element.ChangeTypeId(typeId);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                row["action"] = "refused";
                row["reason"] = "Revit refused the retype: " + ex.Message
                    + " The element was left at its original size.";
                return row;
            }

            row["action"] = duplicated ? "duplicated" : "reused";
            return row;
        }

        private static Dictionary<string, object> Add(
            Document familyDocument,
            NewNote note,
            ElementId viewId,
            ElementId sourceTypeId,
            Dictionary<string, ElementId> typeCache)
        {
            ElementType source = (ElementType)familyDocument.GetElement(sourceTypeId);

            bool duplicated;
            ElementId typeId = TextTypeId(familyDocument, source, note.Size, typeCache, out duplicated);

            TextNote created;

            if (note.Width > 0.0)
            {
                double minimum = TextElement.GetMinimumAllowedWidth(familyDocument, typeId);
                double maximum = TextElement.GetMaximumAllowedWidth(familyDocument, typeId);

                if (note.Width < minimum || note.Width > maximum)
                {
                    throw BridgeException.BadRequest(
                        "\"width\" " + note.Width.ToString("0.######", CultureInfo.InvariantCulture)
                            + " is outside what Revit allows for a note of this type ("
                            + minimum.ToString("0.######", CultureInfo.InvariantCulture) + " to "
                            + maximum.ToString("0.######", CultureInfo.InvariantCulture)
                            + " decimal feet). The edit was rolled back.");
                }

                created = TextNote.Create(familyDocument, viewId, note.Point, note.Width, note.Text, typeId);
            }
            else
            {
                // No width: one line per line break, and Revit sizes the box to the longest line.
                created = TextNote.Create(familyDocument, viewId, note.Point, note.Text, typeId);
            }

            Element target = familyDocument.GetElement(typeId);

            return new Dictionary<string, object>
            {
                { "id", created.Id.Value },
                { "text", note.Text },
                { "point", Point(note.Point) },
                { "size", note.Size },
                { "width", created.Width },
                { "typeId", typeId.Value },
                { "typeName", RevitFacts.SafeName(target) },
                { "typeAction", duplicated ? "duplicated" : "reused" },
            };
        }

        /// <summary>
        /// The ids that were there before the edit and have to be there after it. A label carries
        /// content no request can put back - it is bound to a parameter - and the revision schedule
        /// is the same: if either went, the edit rolls back rather than being loaded into a project.
        /// </summary>
        private static void RequireSurvivors(Document familyDocument, List<long> ids, string what)
        {
            List<long> missing = new List<long>();

            foreach (long id in ids)
            {
                if (familyDocument.GetElement(new ElementId(id)) == null)
                {
                    missing.Add(id);
                }
            }

            if (missing.Count == 0)
            {
                return;
            }

            throw new BridgeException(
                409,
                "PRESERVED_ELEMENT_DELETED",
                "This edit removed " + missing.Count + " " + what + "(s) that existed before it ("
                    + string.Join(", ", missing) + "). That content is driven by the family, not by "
                    + "this request, so the edit was rolled back and the family was not loaded back "
                    + "into the project.");
        }

        /// <summary>
        /// The text type carrying <paramref name="size"/>: one the family already has, or a
        /// duplicate of <paramref name="source"/> with its size set. Duplicating rather than editing
        /// the type in place is the difference between resizing the elements named and resizing
        /// everything that shares their type.
        /// </summary>
        private static ElementId TextTypeId(
            Document familyDocument,
            ElementType source,
            double size,
            Dictionary<string, ElementId> typeCache,
            out bool duplicated)
        {
            // Types are matched within their own class: a label's type and a text note's type are
            // not interchangeable even when both read "3 mm".
            string key = source.GetType().Name + "|" + size.ToString("R", CultureInfo.InvariantCulture);

            ElementId cached;
            if (typeCache.TryGetValue(key, out cached))
            {
                duplicated = false;
                return cached;
            }

            ElementId existing = FindTextType(familyDocument, source, size);
            if (existing != null)
            {
                typeCache[key] = existing;
                duplicated = false;
                return existing;
            }

            ElementType copy = source.Duplicate(TextTypeName(familyDocument, size));

            Parameter textSize = copy.get_Parameter(BuiltInParameter.TEXT_SIZE);
            if (textSize == null || textSize.IsReadOnly)
            {
                throw BridgeException.BadRequest(
                    "Text type \"" + RevitFacts.SafeName(source) + "\" has no writable text size, so "
                        + "a copy of it cannot be resized. The edit was rolled back.");
            }

            textSize.Set(size);

            typeCache[key] = copy.Id;
            duplicated = true;
            return copy.Id;
        }

        /// <summary>A type of the same class as <paramref name="source"/> already carrying that text
        /// size, or null. FilteredElementCollector.OfClass is deliberately not used: the class behind
        /// a label's type is not one it accepts.</summary>
        private static ElementId FindTextType(Document familyDocument, ElementType source, double size)
        {
            Type wanted = source.GetType();

            foreach (Element element in new FilteredElementCollector(familyDocument).WhereElementIsElementType())
            {
                if (element.GetType() != wanted)
                {
                    continue;
                }

                Parameter textSize = element.get_Parameter(BuiltInParameter.TEXT_SIZE);
                if (textSize != null && Math.Abs(textSize.AsDouble() - size) < TextSizeTolerance)
                {
                    return element.Id;
                }
            }

            return null;
        }

        /// <summary>A free element-type name for a text type of that size.</summary>
        private static string TextTypeName(Document familyDocument, double size)
        {
            HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Element element in new FilteredElementCollector(familyDocument).WhereElementIsElementType())
            {
                string existing = RevitFacts.SafeName(element);
                if (existing != null)
                {
                    taken.Add(existing);
                }
            }

            string name = "MCP Text " + size.ToString("0.####", CultureInfo.InvariantCulture) + " ft";

            string candidate = name;
            int suffix = 2;

            while (taken.Contains(candidate))
            {
                candidate = name + " " + suffix;
                suffix++;
            }

            return candidate;
        }

        // --- the report ---------------------------------------------------------------------------

        private static List<object> RemovalRows(List<Element> removals, bool applied)
        {
            List<object> rows = new List<object>();

            foreach (Element element in removals)
            {
                Dictionary<string, object> row = new Dictionary<string, object>
                {
                    { "id", element.Id.Value },
                    { "class", element.GetType().Name },
                    { "category", RevitFacts.CategoryName(element) },
                    { "removed", applied },
                };

                TextElement text = element as TextElement;
                if (text != null)
                {
                    row["text"] = SafeText(text);
                }

                ImageInstance image = element as ImageInstance;
                if (image != null)
                {
                    // Read before the delete when applying, so the row still says what went.
                    row["imageTypeName"] = RevitFacts.TypeName(element.Document, element);
                }

                rows.Add(row);
            }

            return rows;
        }

        private static List<object> TextEditRows(List<TextEdit> edits, bool applied)
        {
            List<object> rows = new List<object>();

            foreach (TextEdit edit in edits)
            {
                rows.Add(new Dictionary<string, object>
                {
                    { "id", edit.Note.Id.Value },
                    { "from", edit.From },
                    { "to", edit.Text },
                    { "applied", applied },
                });
            }

            return rows;
        }

        /// <summary>What a resize would do, resolved against the family but writing nothing: the type
        /// that would be reused, or the type that would be duplicated to make it.</summary>
        private static List<object> SizePlanRows(Document familyDocument, List<SizeEdit> edits)
        {
            List<object> rows = new List<object>();

            foreach (SizeEdit edit in edits)
            {
                Dictionary<string, object> row = new Dictionary<string, object>
                {
                    { "id", edit.Element.Id.Value },
                    { "class", edit.Element.GetType().Name },
                    { "text", SafeText(edit.Element) },
                    { "size", edit.Size },
                };

                ElementType source = familyDocument.GetElement(edit.Element.GetTypeId()) as ElementType;
                if (source == null)
                {
                    row["action"] = "refused";
                    row["reason"] = "The element has no text type to resize or copy.";
                    rows.Add(row);
                    continue;
                }

                row["fromTypeId"] = source.Id.Value;
                row["fromTypeName"] = RevitFacts.SafeName(source);
                row["fromSize"] = SizeOf(source);

                ElementId existing = FindTextType(familyDocument, source, edit.Size);

                if (existing == null)
                {
                    row["action"] = "duplicate";
                    row["toTypeId"] = null;
                    row["toTypeName"] = TextTypeName(familyDocument, edit.Size);
                }
                else
                {
                    row["action"] = "reuse";
                    row["toTypeId"] = existing.Value;
                    row["toTypeName"] = RevitFacts.SafeName(familyDocument.GetElement(existing));

                    // The one thing that can still refuse on the day, asked here rather than found out
                    // then.
                    row["isValidType"] = edit.Element.IsValidType(existing);
                }

                rows.Add(row);
            }

            return rows;
        }

        private static List<object> NotePlanRows(
            Document familyDocument,
            List<NewNote> notes,
            ElementId sourceTypeId)
        {
            List<object> rows = new List<object>();

            foreach (NewNote note in notes)
            {
                ElementType source = (ElementType)familyDocument.GetElement(sourceTypeId);
                ElementId existing = FindTextType(familyDocument, source, note.Size);

                Dictionary<string, object> row = new Dictionary<string, object>
                {
                    { "text", note.Text },
                    { "point", Point(note.Point) },
                    { "size", note.Size },
                    { "width", note.Width },
                    { "typeAction", existing == null ? "duplicate" : "reuse" },
                    { "typeId", existing == null ? (object)null : existing.Value },
                    { "typeName", existing == null
                        ? TextTypeName(familyDocument, note.Size)
                        : RevitFacts.SafeName(familyDocument.GetElement(existing)) },
                };

                if (existing != null && note.Width > 0.0)
                {
                    row["minimumWidth"] = TextElement.GetMinimumAllowedWidth(familyDocument, existing);
                    row["maximumWidth"] = TextElement.GetMaximumAllowedWidth(familyDocument, existing);
                }

                rows.Add(row);
            }

            return rows;
        }

        private static object SizeOf(ElementType type)
        {
            Parameter size = type.get_Parameter(BuiltInParameter.TEXT_SIZE);
            return size == null ? (object)null : size.AsDouble();
        }

        private static string SafeText(TextElement text)
        {
            try
            {
                return text.Text;
            }
            catch (Exception)
            {
                // Reported as unknown rather than as empty.
                return null;
            }
        }

        // --- shared with the inspection -------------------------------------------------------------

        private static FamilySymbol RequireTitleblockSymbol(Document document, long symbolId, string verb)
        {
            FamilySymbol symbol = document.GetElement(new ElementId(symbolId)) as FamilySymbol;
            if (symbol == null)
            {
                throw BridgeException.BadRequest(
                    "Element " + symbolId + " is not a loaded family type in this document. Call "
                        + "/revit-mcp/titleblocks for the title block types this document has.");
            }

            Category category = symbol.Category;
            if (category == null || category.Id.Value != (long)BuiltInCategory.OST_TitleBlocks)
            {
                throw BridgeException.BadRequest(
                    "Family type " + symbolId + " (" + symbol.FamilyName + " : "
                        + RevitFacts.SafeName(symbol) + ") is in category "
                        + (category == null ? "none" : category.Name) + ", not Title Blocks. This "
                        + "endpoint " + verb + " title block families; call /revit-mcp/titleblocks "
                        + "for the ones loaded here.");
            }

            return symbol;
        }

        private static Family RequireEditableFamily(FamilySymbol symbol, long symbolId, string done)
        {
            Family family = symbol.Family;
            if (family == null)
            {
                throw BridgeException.BadRequest(
                    "Family type " + symbolId + " has no family behind it, so there is nothing to "
                        + "open for editing.");
            }

            if (family.IsInPlace)
            {
                throw new BridgeException(
                    409,
                    "FAMILY_NOT_EDITABLE",
                    "\"" + family.Name + "\" is an in-place family, and Revit's API cannot open one "
                        + "for editing. Nothing was " + done + ".");
            }

            if (!family.IsEditable)
            {
                throw new BridgeException(
                    409,
                    "FAMILY_NOT_EDITABLE",
                    "\"" + family.Name + "\" reports IsEditable false, so Revit will not open it for "
                        + "editing. Nothing was " + done + ".");
            }

            return family;
        }

        private static Document OpenForEditing(Document document, Family family, string done)
        {
            try
            {
                return document.EditFamily(family);
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            {
                // Already open for editing, or the document is modifiable / read-only right now.
                throw new BridgeException(
                    409,
                    "FAMILY_NOT_OPENABLE",
                    "Revit would not open \"" + family.Name + "\" for editing: " + ex.Message
                        + " A family already open in a Revit tab cannot be opened a second time "
                        + "- close it in Revit and retry. Nothing was " + done + ".");
            }
        }

        /// <summary>
        /// Reloading the edited copy is what makes the change; without an IFamilyLoadOptions there is
        /// nowhere for Revit to answer its own "this family already exists" question and the load
        /// raises instead. The answer is "take this version, leave the project's parameter values
        /// alone" - the same as the Overwrite button, and the same as families/load.
        /// </summary>
        private sealed class BridgeFamilyLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = false;
                return true;
            }

            public bool OnSharedFamilyFound(
                Family sharedFamily,
                bool familyInUse,
                out FamilySource source,
                out bool overwriteParameterValues)
            {
                source = FamilySource.Project;
                overwriteParameterValues = false;
                return true;
            }
        }

        // --- the family document ----------------------------------------------------------------

        private static Dictionary<string, object> Describe(
            Document document,
            FamilySymbol symbol,
            Family family,
            Document familyDocument)
        {
            // Built first: the element rows carry the same association back the other way.
            Dictionary<long, List<string>> labelsByElement = new Dictionary<long, List<string>>();
            List<object> parameters = FamilyParameters(familyDocument, labelsByElement);

            List<Element> elements = new List<Element>();
            foreach (Element element in new FilteredElementCollector(familyDocument)
                .WhereElementIsNotElementType())
            {
                elements.Add(element);
            }

            Dictionary<string, int> byClass = new Dictionary<string, int>(StringComparer.Ordinal);
            Dictionary<string, int> byCategory = new Dictionary<string, int>(StringComparer.Ordinal);

            List<object> rows = new List<object>();

            foreach (Element element in elements)
            {
                Count(byClass, element.GetType().Name);
                Count(byCategory, RevitFacts.CategoryName(element) ?? "(none)");

                if (rows.Count < MaxElements)
                {
                    rows.Add(DescribeElement(familyDocument, element, labelsByElement));
                }
            }

            FamilyManager manager = familyDocument.FamilyManager;
            List<object> types = new List<object>();
            string currentType = null;

            if (manager != null)
            {
                foreach (FamilyType type in manager.Types)
                {
                    types.Add(type.Name);
                }

                if (manager.CurrentType != null)
                {
                    currentType = manager.CurrentType.Name;
                }
            }

            Category familyCategory = family.FamilyCategory;

            return new Dictionary<string, object>
            {
                { "readOnly", true },
                { "note", "The family was opened with Document.EditFamily, which hands back an "
                    + "independent copy, and that copy was closed without saving. No transaction was "
                    + "opened: the project, the loaded family and the .rfa on disk are unchanged." },
                { "units", "Revit internal units (decimal feet). Annotation inside a title block is "
                    + "PAPER feet - 5 mm text reads 0.0164." },
                { "symbol", new Dictionary<string, object>
                    {
                        { "id", symbol.Id.Value },
                        { "familyName", symbol.FamilyName },
                        { "typeName", RevitFacts.SafeName(symbol) },
                    }
                },
                { "family", new Dictionary<string, object>
                    {
                        { "id", family.Id.Value },
                        { "name", RevitFacts.SafeName(family) },
                        { "category", familyCategory == null ? null : familyCategory.Name },
                        { "isEditable", family.IsEditable },
                        { "isInPlace", family.IsInPlace },
                    }
                },
                { "familyDocument", new Dictionary<string, object>
                    {
                        { "title", familyDocument.Title },

                        // Empty for the copy EditFamily makes: it has no file behind it.
                        { "pathName", familyDocument.PathName },
                        { "isModified", familyDocument.IsModified },
                    }
                },
                { "familyTypes", types },
                { "currentType", currentType },
                { "familyParameters", parameters },
                { "elementCount", elements.Count },
                { "truncated", elements.Count > rows.Count },
                { "byClass", Tally(byClass) },
                { "byCategory", Tally(byCategory) },
                { "elements", rows },
            };
        }

        /// <summary>
        /// Every family parameter, and on the way the element-to-parameter map the rows use.
        /// FamilyParameter.AssociatedParameters is what Revit holds: the parameters of elements
        /// inside the family that are wired to this family parameter.
        /// </summary>
        private static List<object> FamilyParameters(
            Document familyDocument,
            Dictionary<long, List<string>> labelsByElement)
        {
            List<object> rows = new List<object>();

            FamilyManager manager = familyDocument.FamilyManager;
            if (manager == null)
            {
                return rows;
            }

            FamilyType current = manager.CurrentType;

            foreach (FamilyParameter parameter in manager.GetParameters())
            {
                Definition definition = parameter.Definition;
                string name = definition == null ? null : definition.Name;

                List<object> associated = new List<object>();

                foreach (Parameter bound in parameter.AssociatedParameters)
                {
                    Element owner = bound.Element;
                    if (owner == null)
                    {
                        continue;
                    }

                    associated.Add(owner.Id.Value);

                    if (name == null)
                    {
                        continue;
                    }

                    List<string> labels;
                    if (!labelsByElement.TryGetValue(owner.Id.Value, out labels))
                    {
                        labels = new List<string>();
                        labelsByElement[owner.Id.Value] = labels;
                    }

                    if (!labels.Contains(name))
                    {
                        labels.Add(name);
                    }
                }

                Dictionary<string, object> row = new Dictionary<string, object>
                {
                    { "id", parameter.Id.Value },
                    { "name", name },
                    { "isInstance", parameter.IsInstance },
                    { "isShared", parameter.IsShared },
                    { "isReadOnly", parameter.IsReadOnly },
                    { "isReporting", parameter.IsReporting },
                    { "userModifiable", parameter.UserModifiable },
                    { "storageType", parameter.StorageType.ToString() },
                    { "formula", parameter.Formula },
                    { "associatedElementIds", associated },
                };

                ReadValue(current, parameter, row);

                rows.Add(row);
            }

            return rows;
        }

        /// <summary>The value the current family type carries for this parameter, or null.</summary>
        private static void ReadValue(
            FamilyType type,
            FamilyParameter parameter,
            Dictionary<string, object> row)
        {
            row["value"] = null;
            row["display"] = null;

            if (type == null || !type.HasValue(parameter))
            {
                return;
            }

            switch (parameter.StorageType)
            {
                case StorageType.String:
                    row["value"] = type.AsString(parameter);
                    break;

                case StorageType.Double:
                    double? number = type.AsDouble(parameter);
                    row["value"] = number.HasValue ? (object)number.Value : null;
                    break;

                case StorageType.Integer:
                    int? whole = type.AsInteger(parameter);
                    row["value"] = whole.HasValue ? (object)whole.Value : null;
                    break;

                case StorageType.ElementId:
                    ElementId id = type.AsElementId(parameter);
                    row["value"] = id == null ? (object)null : id.Value;
                    break;
            }

            try
            {
                row["display"] = type.AsValueString(parameter);
            }
            catch (Exception)
            {
                // AsValueString is for values with units; a text parameter has none.
            }
        }

        // --- one element --------------------------------------------------------------------------

        private static Dictionary<string, object> DescribeElement(
            Document familyDocument,
            Element element,
            Dictionary<long, List<string>> labelsByElement)
        {
            Dictionary<string, object> row = new Dictionary<string, object>
            {
                { "id", element.Id.Value },
                { "class", element.GetType().Name },
                { "category", RevitFacts.CategoryName(element) },
                { "name", RevitFacts.SafeName(element) },
            };

            ElementId typeId = element.GetTypeId();
            if (typeId != null && typeId != ElementId.InvalidElementId)
            {
                row["typeId"] = typeId.Value;
                row["typeName"] = RevitFacts.TypeName(familyDocument, element);
            }

            View owner = OwnerView(familyDocument, element);
            if (owner != null)
            {
                row["ownerViewId"] = owner.Id.Value;
                row["ownerViewName"] = RevitFacts.SafeName(owner);
            }

            object location = Location(element);
            if (location != null)
            {
                row["location"] = location;
            }

            Dictionary<string, object> bounds = Bounds(element, owner);
            if (bounds != null)
            {
                row["bounds"] = bounds;
            }

            List<string> labels;
            if (labelsByElement.TryGetValue(element.Id.Value, out labels))
            {
                row["labelOf"] = labels;
            }

            TextElement text = element as TextElement;
            if (text != null)
            {
                row["text"] = Text(familyDocument, text);
            }

            ImageInstance image = element as ImageInstance;
            if (image != null)
            {
                row["image"] = Image(familyDocument, image);
            }

            ImportInstance import = element as ImportInstance;
            if (import != null)
            {
                row["import"] = Import(familyDocument, import);
            }

            CurveElement curve = element as CurveElement;
            if (curve != null)
            {
                GraphicsStyle style = curve.LineStyle as GraphicsStyle;
                row["lineStyle"] = style == null ? null : RevitFacts.SafeName(style);
            }

            Dimension dimension = element as Dimension;
            if (dimension != null)
            {
                row["dimension"] = Constraint(dimension);
            }

            ReferencePlane plane = element as ReferencePlane;
            if (plane != null)
            {
                row["referencePlane"] = new Dictionary<string, object>
                {
                    { "bubbleEnd", Point(plane.BubbleEnd) },
                    { "freeEnd", Point(plane.FreeEnd) },
                    { "normal", Point(plane.Normal) },
                };
            }

            return row;
        }

        /// <summary>
        /// A plain note and a label are both TextElement. "isTextNote" is what separates them: the
        /// literal text somebody typed is a TextNote, the other kind is not.
        /// </summary>
        private static Dictionary<string, object> Text(Document familyDocument, TextElement text)
        {
            Dictionary<string, object> info = new Dictionary<string, object>
            {
                { "isTextNote", text is TextNote },
            };

            try
            {
                info["value"] = text.Text;
                info["coord"] = Point(text.Coord);
                info["width"] = text.Width;
                info["height"] = text.Height;
                info["horizontalAlignment"] = text.HorizontalAlignment.ToString();
                info["verticalAlignment"] = text.VerticalAlignment.ToString();
            }
            catch (Exception ex)
            {
                // Reported rather than swallowed: a missing field must not read as an empty one.
                info["readError"] = ex.Message;
            }

            Element type = familyDocument.GetElement(text.GetTypeId());
            if (type != null)
            {
                info["textTypeId"] = type.Id.Value;
                info["textTypeName"] = RevitFacts.SafeName(type);

                Parameter size = type.get_Parameter(BuiltInParameter.TEXT_SIZE);
                if (size != null)
                {
                    info["textSize"] = size.AsDouble();
                    info["textSizeDisplay"] = RevitFacts.SafeValueString(size);
                }
            }

            return info;
        }

        private static Dictionary<string, object> Image(Document familyDocument, ImageInstance image)
        {
            Dictionary<string, object> info = new Dictionary<string, object>();

            try
            {
                info["width"] = image.Width;
                info["height"] = image.Height;
                info["widthScale"] = image.WidthScale;
                info["heightScale"] = image.HeightScale;
                info["lockProportions"] = image.LockProportions;
                info["drawLayer"] = image.DrawLayer.ToString();
            }
            catch (Exception ex)
            {
                info["readError"] = ex.Message;
            }

            ImageType type = familyDocument.GetElement(image.GetTypeId()) as ImageType;
            if (type != null)
            {
                info["imageTypeId"] = type.Id.Value;
                info["imageTypeName"] = RevitFacts.SafeName(type);

                try
                {
                    info["path"] = type.Path;
                    info["pathType"] = type.PathType.ToString();
                    info["source"] = type.Source.ToString();
                    info["status"] = type.Status.ToString();
                    info["resolution"] = type.Resolution;
                    info["widthInPixels"] = type.WidthInPixels;
                    info["heightInPixels"] = type.HeightInPixels;
                }
                catch (Exception ex)
                {
                    info["typeReadError"] = ex.Message;
                }
            }

            return info;
        }

        private static Dictionary<string, object> Import(Document familyDocument, ImportInstance import)
        {
            Dictionary<string, object> info = new Dictionary<string, object>
            {
                { "isLinked", import.IsLinked },
                { "pinned", import.Pinned },
            };

            Element type = familyDocument.GetElement(import.GetTypeId());
            if (type != null)
            {
                info["importTypeId"] = type.Id.Value;

                // The import type's name is the file it was imported from.
                info["importTypeName"] = RevitFacts.SafeName(type);
            }

            return info;
        }

        /// <summary>A dimension is how a title block's border is held together - and what a label
        /// on it makes parametric. FamilyLabel is null for an unlabelled one.</summary>
        private static Dictionary<string, object> Constraint(Dimension dimension)
        {
            Dictionary<string, object> info = new Dictionary<string, object>
            {
                { "isLocked", dimension.IsLocked },
                { "numberOfSegments", dimension.NumberOfSegments },
                { "areSegmentsEqual", dimension.AreSegmentsEqual },
                { "hasLeader", dimension.HasLeader },
                { "shape", dimension.DimensionShape.ToString() },
            };

            try
            {
                double? value = dimension.Value;
                info["value"] = value.HasValue ? (object)value.Value : null;
                info["valueString"] = dimension.ValueString;
            }
            catch (Exception)
            {
                // A multi-segment dimension has no single value to report.
                info["value"] = null;
                info["valueString"] = null;
            }

            FamilyParameter label = dimension.FamilyLabel;
            info["familyLabel"] = label == null || label.Definition == null
                ? null
                : label.Definition.Name;

            return info;
        }

        // --- shared readers -----------------------------------------------------------------------

        private static View OwnerView(Document familyDocument, Element element)
        {
            ElementId viewId = element.OwnerViewId;
            if (viewId == null || viewId == ElementId.InvalidElementId)
            {
                return null;
            }

            return familyDocument.GetElement(viewId) as View;
        }

        private static object Location(Element element)
        {
            LocationPoint point = element.Location as LocationPoint;
            if (point != null)
            {
                return new Dictionary<string, object>
                {
                    { "kind", "point" },
                    { "point", Point(point.Point) },
                };
            }

            LocationCurve curve = element.Location as LocationCurve;
            if (curve != null && curve.Curve != null)
            {
                return new Dictionary<string, object>
                {
                    { "kind", "curve" },
                    { "curveType", curve.Curve.GetType().Name },
                    { "start", Point(curve.Curve.GetEndPoint(0)) },
                    { "end", Point(curve.Curve.GetEndPoint(1)) },
                };
            }

            return null;
        }

        /// <summary>
        /// Title block content is drawn in one view of the family, so the view-independent box is
        /// null for most of it. The owner view is tried second, and "source" says which answered.
        /// </summary>
        private static Dictionary<string, object> Bounds(Element element, View owner)
        {
            BoundingBoxXYZ box = SafeBox(element, null);
            string source = "model";

            if (box == null && owner != null)
            {
                box = SafeBox(element, owner);
                source = "view";
            }

            if (box == null)
            {
                return null;
            }

            return new Dictionary<string, object>
            {
                { "min", Point(box.Min) },
                { "max", Point(box.Max) },
                { "center", Point((box.Min + box.Max) / 2.0) },
                { "source", source },
            };
        }

        private static BoundingBoxXYZ SafeBox(Element element, View view)
        {
            try
            {
                return element.get_BoundingBox(view);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return null;
            }
        }

        private static Dictionary<string, object> Point(XYZ point)
        {
            if (point == null)
            {
                return null;
            }

            return new Dictionary<string, object>
            {
                { "x", point.X },
                { "y", point.Y },
                { "z", point.Z },
            };
        }

        private static void Count(Dictionary<string, int> tally, string key)
        {
            int current;
            tally.TryGetValue(key, out current);
            tally[key] = current + 1;
        }

        private static List<object> Tally(Dictionary<string, int> counts)
        {
            List<KeyValuePair<string, int>> ordered = new List<KeyValuePair<string, int>>(counts);
            ordered.Sort(delegate (KeyValuePair<string, int> left, KeyValuePair<string, int> right)
            {
                int byCount = right.Value.CompareTo(left.Value);
                if (byCount != 0)
                {
                    return byCount;
                }

                return string.Compare(left.Key, right.Key, StringComparison.OrdinalIgnoreCase);
            });

            List<object> rows = new List<object>();
            foreach (KeyValuePair<string, int> entry in ordered)
            {
                rows.Add(new Dictionary<string, object>
                {
                    { "name", entry.Key },
                    { "count", entry.Value },
                });
            }

            return rows;
        }

        private static JsonElement RequireValue(JsonElement root, string name)
        {
            JsonElement value;
            if (!JsonBody.TryGet(root, name, out value))
            {
                throw BridgeException.BadRequest("\"" + name + "\" is required.");
            }

            return value;
        }

        /// <summary>An id list the caller may leave out entirely.</summary>
        private static List<long> OptionalIds(JsonElement root, string name)
        {
            JsonElement value;
            if (!JsonBody.TryGet(root, name, out value))
            {
                return new List<long>();
            }

            return JsonBody.RequireIds(root, name);
        }

        /// <summary>The rows of an optional array of objects.</summary>
        private static List<JsonElement> Rows(JsonElement root, string name)
        {
            List<JsonElement> rows = new List<JsonElement>();

            JsonElement value;
            if (!JsonBody.TryGet(root, name, out value))
            {
                return rows;
            }

            if (value.ValueKind != JsonValueKind.Array)
            {
                throw BridgeException.BadRequest("\"" + name + "\" must be an array of objects.");
            }

            foreach (JsonElement row in value.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                {
                    throw BridgeException.BadRequest("\"" + name + "\" must be an array of objects.");
                }

                rows.Add(row);
            }

            return rows;
        }

        /// <summary>
        /// The element that id names INSIDE the family. Project ids and family ids are different
        /// numbering: an id that is not in the family copy is refused rather than resolved against
        /// the project by accident.
        /// </summary>
        private static Element RequireInFamily(Document familyDocument, long id, string field)
        {
            Element element = familyDocument.GetElement(new ElementId(id));
            if (element == null)
            {
                throw BridgeException.BadRequest(
                    "\"" + field + "\" names element " + id + ", which is not in this family "
                        + "document. These are ids from inside the family - the ones "
                        + "/families/titleblock-inspect reports - not project ids. Nothing was "
                        + "changed.");
            }

            return element;
        }
    }
}
