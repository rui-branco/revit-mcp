using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcpBridge.Endpoints
{
    /// <summary>
    /// Documentation: the half of the job that happens after the model is built - cropping a view
    /// to what the drawing is about, hiding what is in the way of it, laying a sheet out, reading a
    /// schedule back, and getting the set out of Revit as PDF.
    ///
    /// Three rules run through all of it, and they are the reason this file exists separately:
    ///
    /// 1. **The template is the authority.** A view template that owns the crop or the graphics is
    ///    not something to fight: views/set-crop refuses a view whose template controls the crop
    ///    parameters rather than writing a value Revit will ignore. The refusal names the template.
    /// 2. **A write says what it would do before it does it.** Every mutating endpoint here takes
    ///    "dryRun" and it DEFAULTS TO TRUE. A caller has to ask for the change explicitly, twice -
    ///    once by calling, once by turning the dry run off - because these operations are applied
    ///    to presentation drawings a human has already approved.
    /// 3. **Nothing is reported that was not read back.** Crop bounds, hidden flags, viewport
    ///    centres and PDF paths all come out of Revit or off the disk after the fact, never echoed
    ///    from the request. export/pdf will not report success for a file that is not there.
    ///
    /// Same conventions as the other endpoints: one request is one TransactionGroup and therefore
    /// one Ctrl+Z, model lengths are Revit internal units (decimal feet) and sheet lengths are feet
    /// on the paper.
    /// </summary>
    internal static class DocumentationEndpoints
    {
        /// <summary>Body rows schedules/read returns when the caller asks for no limit.</summary>
        private const int DefaultScheduleRows = 100;

        /// <summary>Hard cap on body rows in one schedules/read answer. Higher is clamped.</summary>
        private const int MaxScheduleRows = 500;

        /// <summary>Hard cap on columns read out of a schedule section.</summary>
        private const int MaxScheduleColumns = 60;

        /// <summary>Hard cap on the rows read out of a schedule's header section.</summary>
        private const int MaxHeaderRows = 20;

        /// <summary>Longest file name stem export/pdf will build, before ".pdf".</summary>
        private const int MaxFileNameLength = 120;

        /// <summary>A PDF bigger than this is not scanned for its page count.</summary>
        private const long MaxPdfScanBytes = 64L * 1024L * 1024L;

        /// <summary>
        /// The three view parameters a view template can take ownership of between them, in the
        /// order a report should read. Kept as BuiltInParameter rather than display names: the
        /// names are localised and "Crop View" is "Vista de corte" on this machine.
        /// </summary>
        private static readonly KeyValuePair<string, BuiltInParameter>[] CropParameters =
        {
            new KeyValuePair<string, BuiltInParameter>("Crop View", BuiltInParameter.VIEWER_CROP_REGION),
            new KeyValuePair<string, BuiltInParameter>("Crop Region Visible", BuiltInParameter.VIEWER_CROP_REGION_VISIBLE),
            new KeyValuePair<string, BuiltInParameter>("Annotation Crop", BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE),
        };

        /// <summary>
        /// The two of those that views/set-crop actually writes. A template owning either one makes
        /// the write pointless, so the endpoint refuses instead of pretending.
        /// </summary>
        private static readonly BuiltInParameter[] WrittenCropParameters =
        {
            BuiltInParameter.VIEWER_CROP_REGION,
            BuiltInParameter.VIEWER_CROP_REGION_VISIBLE,
        };

        /// <summary>
        /// Body: {viewId} for one, or {viewIds: [...]} for a batch. Read-only.
        ///
        /// What a crop actually is, which is why this endpoint is worth having on its own: the
        /// crop box is a BoundingBoxXYZ whose Min and Max are in the VIEW's own coordinate system,
        /// not the model's, and its Transform is what maps between them. Reading Min and Max raw
        /// and calling them model coordinates is wrong for every view that is not aligned with the
        /// project axes. So "modelBounds" here is the axis-aligned box around all EIGHT corners of
        /// the crop box put through that Transform, and "localBounds" is the raw pair, reported as
        /// well so the two are never confused.
        ///
        /// "cropBoxActive" and "cropBoxVisible" come off the view. "annotationCrop" is the
        /// VIEWER_ANNOTATION_CROP_ACTIVE parameter, which not every view carries - a view without
        /// it reports null rather than false, because "off" and "not a thing here" are different
        /// answers.
        ///
        /// "templateControlledCrop" lists the crop parameters this view's template owns. A view
        /// with a template that controls them cannot be cropped from here at all; see
        /// views/set-crop.
        /// </summary>
        internal static object Crop(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            List<long> viewIds = ViewIds(body);

            List<object> rows = new List<object>();
            foreach (long viewId in viewIds)
            {
                rows.Add(ReadCrop(document, RequireView(document, viewId)));
            }

            return new Dictionary<string, object>
            {
                { "views", rows },
            };
        }

        /// <summary>
        /// Body: {viewId | viewIds: [...], modelBounds: {min: {x, y, z}, max: {x, y, z}}, dryRun?}.
        /// Crop views to a region of the MODEL, in feet.
        ///
        /// The whole point is that the caller thinks in model coordinates and never has to know
        /// about the crop box's own coordinate system. All EIGHT corners of the requested model box
        /// are put through view.CropBox.Transform.Inverse and the axis-aligned box around the
        /// results becomes the new Min/Max. Transforming only min and max would be wrong the moment
        /// the view is not axis-aligned - a section looking north-east crops to the wrong region and
        /// the error looks like a Revit bug rather than a maths one. The Transform itself is
        /// preserved untouched: it belongs to the view's orientation, not to the crop.
        ///
        /// What is written, every time: the box, CropBoxActive = true, CropBoxVisible = false. That
        /// is the state a presentation drawing wants - cropped, with no crop rectangle drawn on the
        /// sheet.
        ///
        /// Refusals, all checked for EVERY view before anything is written, so the call is
        /// all-or-nothing:
        ///   - NOT_A_VIEW / CROP_NOT_SUPPORTED: a sheet, a schedule, a legend or a view template.
        ///     Revit gives those no crop box at all.
        ///   - CROP_CONTROLLED_BY_TEMPLATE: the view's template owns Crop View or Crop Region
        ///     Visible. Writing them would be ignored by Revit, so the endpoint says so and names
        ///     the template rather than reporting a success the drawing will not show.
        ///   - BAD_REQUEST: modelBounds min is not strictly less than max on all three axes.
        ///
        /// "dryRun" DEFAULTS TO TRUE: the answer shows the current state and the exact local box
        /// that would be written, and nothing is changed. Pass dryRun false to apply it.
        ///
        /// The applied answer carries "before" and "after" for every view, both read off the view,
        /// so a template quietly overriding the result is visible rather than silent.
        /// </summary>
        internal static object SetCrop(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            List<long> viewIds = ViewIds(body);
            bool dryRun = JsonBody.OptionalBool(body, "dryRun", true);

            JsonElement bounds;
            if (!JsonBody.TryGet(body, "modelBounds", out bounds))
            {
                throw BridgeException.BadRequest(
                    "\"modelBounds\" is required and must be {min: {x, y, z}, max: {x, y, z}} in "
                        + "feet (Revit internal units), in MODEL coordinates.");
            }

            XYZ requestedMin = RevitFacts.RequirePoint(bounds, "min");
            XYZ requestedMax = RevitFacts.RequirePoint(bounds, "max");

            if (requestedMin.X >= requestedMax.X
                || requestedMin.Y >= requestedMax.Y
                || requestedMin.Z >= requestedMax.Z)
            {
                throw BridgeException.BadRequest(
                    "\"modelBounds\" needs min strictly less than max on all three axes; got min ("
                        + Number(requestedMin.X) + ", " + Number(requestedMin.Y) + ", "
                        + Number(requestedMin.Z) + ") and max (" + Number(requestedMax.X) + ", "
                        + Number(requestedMax.Y) + ", " + Number(requestedMax.Z) + "). A crop with "
                        + "no depth on one axis is not a crop Revit can store.");
            }

            // Every view is resolved and checked before a single one is written: a crop applied to
            // half a drawing set and refused on the rest is worse than one that did not start.
            List<View> views = new List<View>();
            foreach (long viewId in viewIds)
            {
                View view = RequireView(document, viewId);
                RequireCroppable(document, view);
                views.Add(view);
            }

            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            List<BoundingBoxXYZ> boxes = new List<BoundingBoxXYZ>();

            foreach (View view in views)
            {
                BoundingBoxXYZ box = SafeCropBox(view);

                // Local, via the INVERSE of the view's own transform, around all eight corners.
                XYZ localMin;
                XYZ localMax;
                LocalBounds(box.Transform, requestedMin, requestedMax, out localMin, out localMax);

                BoundingBoxXYZ target = new BoundingBoxXYZ();

                // Assigned before Min/Max on purpose: BoundingBoxXYZ interprets the points it is
                // given in the coordinate system of the transform it is carrying at the time.
                target.Transform = box.Transform;
                target.Min = localMin;
                target.Max = localMax;

                boxes.Add(target);

                Dictionary<string, object> row = new Dictionary<string, object>
                {
                    { "id", view.Id.Value },
                    { "name", RevitFacts.SafeName(view) },
                    { "viewType", view.ViewType.ToString() },
                    { "requestedModelBounds", Box(requestedMin, requestedMax) },
                    { "localBounds", Box(localMin, localMax) },
                    { "before", ReadCrop(document, view) },
                    { "after", null },
                };

                rows.Add(row);
            }

            if (dryRun)
            {
                return new Dictionary<string, object>
                {
                    { "dryRun", true },
                    { "applied", false },
                    { "views", rows },
                };
            }

            return RevitWrite.InGroup(document, "MCP: set view crop", delegate
            {
                // One transaction for the whole batch, not one per view: the request is
                // all-or-nothing, and a Revit refusal half way through has to take the rest with it.
                RevitWrite.InTransaction(document, "Set view crop", delegate
                {
                    for (int index = 0; index < views.Count; index++)
                    {
                        views[index].CropBox = boxes[index];
                        views[index].CropBoxActive = true;
                        views[index].CropBoxVisible = false;
                    }
                });

                for (int index = 0; index < views.Count; index++)
                {
                    rows[index]["after"] = ReadCrop(document, views[index]);
                }

                return new Dictionary<string, object>
                {
                    { "dryRun", false },
                    { "applied", true },
                    { "views", rows },
                };
            });
        }

        /// <summary>
        /// Body: {viewId, ids: [...], hidden?, dryRun?}. Hide elements in ONE view.
        ///
        /// This is the permanent, per-view "Hide in View > Elements" - View.HideElements, which is
        /// what the Hidden Elements override in that view stores. Read that sentence twice, because
        /// the two things it is NOT both look the same in the Revit window:
        ///   - It is NOT deletion. The elements stay in the model, in the schedules and in every
        ///     other view. Hiding the entourage trees that stand in front of a presentation
        ///     elevation does not touch the planting design or its quantities.
        ///   - It is NOT Temporary Hide/Isolate (HideElementsTemporary), which evaporates when the
        ///     view is closed and never reaches a sheet.
        /// Pass hidden false to bring them back - the same list, unhidden.
        ///
        /// Every id is checked with Element.CanBeHidden(view) BEFORE anything is hidden, and one
        /// element Revit refuses fails the whole call with ELEMENT_CANNOT_BE_HIDDEN naming it. That
        /// is deliberate: View.HideElements throws on the batch rather than skipping the offender,
        /// so there is no half-landed version of this to report.
        ///
        /// "dryRun" DEFAULTS TO TRUE. The dry run reports canBeHidden and the current isHidden for
        /// every id and changes nothing.
        ///
        /// "isHidden" in the answer is read back with Element.IsHidden(view) after the commit,
        /// never echoed - a category turned off in the view, or a template, can leave an element
        /// invisible for a reason that has nothing to do with this call.
        /// </summary>
        internal static object HideElements(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            long viewId = JsonBody.AsLong(RequireValue(body, "viewId"), "viewId");
            List<long> ids = JsonBody.RequireIds(body, "ids");
            bool hidden = JsonBody.OptionalBool(body, "hidden", true);
            bool dryRun = JsonBody.OptionalBool(body, "dryRun", true);

            View view = RequireView(document, viewId);

            if (view.IsTemplate)
            {
                throw BridgeException.BadRequest(
                    "View \"" + RevitFacts.SafeName(view) + "\" (" + viewId + ") is a view template. "
                        + "A template has no elements of its own to hide; hide them in the views "
                        + "that use it.");
            }

            List<Element> elements = new List<Element>();
            List<string> refused = new List<string>();

            foreach (long id in ids)
            {
                Element element = RevitFacts.RequireElement(document, id);

                if (!element.CanBeHidden(view))
                {
                    refused.Add("\"" + RevitFacts.SafeName(element) + "\" (" + id + ", "
                        + RevitFacts.CategoryName(element) + ")");
                }

                elements.Add(element);
            }

            if (refused.Count > 0)
            {
                throw new BridgeException(
                    409,
                    "ELEMENT_CANNOT_BE_HIDDEN",
                    "Revit reports CanBeHidden false in view \"" + RevitFacts.SafeName(view) + "\" ("
                        + viewId + ", " + view.ViewType + ") for " + string.Join(", ", refused)
                        + ". View.HideElements refuses the whole batch when one element in it cannot "
                        + "be hidden, so nothing was hidden. Drop those ids and retry.");
            }

            if (dryRun)
            {
                return HideReport(document, view, elements, hidden, true);
            }

            return RevitWrite.InGroup(document, "MCP: hide elements in view", delegate
            {
                List<ElementId> targets = new List<ElementId>();

                foreach (Element element in elements)
                {
                    // Unhiding is filtered to what is actually hidden: Revit refuses a batch
                    // containing an element that is not hidden in the first place, and a caller
                    // passing the same list both ways should not have to track which is which.
                    if (hidden || element.IsHidden(view))
                    {
                        targets.Add(element.Id);
                    }
                }

                if (targets.Count > 0)
                {
                    RevitWrite.InTransaction(document, "Hide elements in view", delegate
                    {
                        if (hidden)
                        {
                            view.HideElements(targets);
                        }
                        else
                        {
                            view.UnhideElements(targets);
                        }
                    });
                }

                return HideReport(document, view, elements, hidden, false);
            });
        }

        /// <summary>
        /// Body: {viewIds?: [...], sheetIds?: [...], folder, filename?, combine?, overwrite?}.
        /// Native PDF out of Revit, through Document.Export and PDFExportOptions.
        ///
        /// This is Revit's own PDF exporter - the same one behind File > Export > PDF - and not a
        /// print driver, not a raster image and, to head off the obvious confusion, NOTHING to do
        /// with rendering: the Revit API cannot start the raytracer at all (see views/export-image).
        /// A PDF here is the views exactly as drawn, as vectors.
        ///
        /// "viewIds" and "sheetIds" are both explicit lists and at least one is required. There is
        /// deliberately no "export everything" shorthand: a drawing set is a decision, and a
        /// mistyped one that exports 200 views is expensive. sheetIds must resolve to sheets and
        /// viewIds must not; getting those the wrong way round is a BAD_REQUEST rather than a
        /// surprise. The order exported is viewIds first, then sheetIds.
        ///
        /// Naming is the bridge's, not Revit's naming rule, and that is what makes the rest
        /// possible. PDFExportOptions.FileName is honoured only when Combine is true, so every
        /// export here runs with Combine = true - once for the whole set when "combine" is true,
        /// once per view when it is false. Knowing the target path up front is what lets the
        /// endpoint refuse to overwrite (FILE_EXISTS unless "overwrite" is true), and it is checked
        /// for EVERY target before the first byte is written.
        ///
        /// What is proven and what is not, precisely:
        ///   - "path" and "bytes" are read off the disk after the export. A file that is not there
        ///     is EXPORT_PRODUCED_NO_FILE, never a success - Document.Export returning true is not
        ///     taken as evidence that anything was written.
        ///   - "pages" is counted out of the PDF itself by scanning for its page objects, and is
        ///     null with "pagesMeasured" false when the file's object streams are compressed and
        ///     the scan cannot see them. It is never inferred from the number of views.
        ///   - "pageMapping" says which of those two worlds the per-view "page" numbers are from:
        ///     "exact" when each file holds exactly one view, "requestedOrder" when a combined PDF
        ///     was asked for and the page numbers are the order the views were handed to Revit,
        ///     which is an assumption about Revit's ordering, not a measurement.
        ///
        /// Not transacted: exporting is a read of the document, and Revit refuses it inside a
        /// transaction.
        /// </summary>
        internal static object ExportPdf(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            List<View> views = ExportTargets(document, body);
            bool combine = JsonBody.OptionalBool(body, "combine", true);
            bool overwrite = JsonBody.OptionalBool(body, "overwrite", false);
            string filename = JsonBody.OptionalString(body, "filename");
            string folder = RequireExportFolder(JsonBody.RequireString(body, "folder"));

            List<string> unprintable = new List<string>();
            foreach (View view in views)
            {
                if (!view.CanBePrinted)
                {
                    unprintable.Add("\"" + RevitFacts.SafeName(view) + "\" (" + view.Id.Value + ", "
                        + view.ViewType + ")");
                }
            }

            if (unprintable.Count > 0)
            {
                throw BridgeException.BadRequest(
                    "Revit reports CanBePrinted false for " + string.Join(", ", unprintable)
                        + ", so those cannot go in a PDF. A view template and a browser-only view "
                        + "are the usual ones. Nothing was exported.");
            }

            string stem = null;
            if (filename != null)
            {
                stem = SanitizeFileName(filename);
                if (stem == null)
                {
                    throw BridgeException.BadRequest(
                        "\"filename\" is empty once the characters Windows will not accept in a file "
                            + "name are taken out of it.");
                }
            }

            // Every target path is worked out and checked before the first export runs, so a set
            // that would have clobbered an existing PDF half way through never starts.
            List<List<View>> groups = new List<List<View>>();
            List<string> names = new List<string>();

            if (combine)
            {
                groups.Add(views);
                names.Add(stem == null ? SanitizeFileName(document.Title) : stem);
            }
            else
            {
                HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (View view in views)
                {
                    List<View> single = new List<View>();
                    single.Add(view);
                    groups.Add(single);
                    names.Add(UniqueName(taken, FileNameFor(view, stem)));
                }
            }

            List<string> clashes = new List<string>();
            List<string> targets = new List<string>();

            for (int index = 0; index < names.Count; index++)
            {
                string target = Path.Combine(folder, names[index] + ".pdf");
                targets.Add(target);

                if (!overwrite && File.Exists(target))
                {
                    clashes.Add(target);
                }
            }

            if (clashes.Count > 0)
            {
                throw new BridgeException(
                    409,
                    "FILE_EXISTS",
                    "These files are already there and \"overwrite\" is not true: "
                        + string.Join(", ", clashes) + ". Nothing was exported. Pass overwrite true, "
                        + "a different \"filename\", or a different \"folder\".");
            }

            List<object> files = new List<object>();

            for (int index = 0; index < groups.Count; index++)
            {
                files.Add(ExportOnePdf(document, groups[index], folder, names[index], targets[index]));
            }

            return new Dictionary<string, object>
            {
                { "folder", folder },
                { "combine", combine },
                { "overwrite", overwrite },
                { "pageMapping", combine && views.Count > 1 ? "requestedOrder" : "exact" },
                { "files", files },
            };
        }

        /// <summary>
        /// Body: {scheduleId, offset?, limit?}. Read-only: the text of a schedule, cell by cell.
        ///
        /// This is how to INSPECT a schedule from outside Revit. It is worth saying what it is
        /// instead of, because the obvious alternative does not exist: a schedule cannot be
        /// exported as an image - views/export-image goes through Document.ExportImage, which a
        /// ViewSchedule is not a valid view for - and ViewSchedule.Export writes a delimited text
        /// file to disk, which is a file the caller then cannot see either. So the cells come back
        /// as JSON and the caller reads them.
        ///
        /// Two independent sources of the column headings, both reported, because they disagree in
        /// a way that matters:
        ///   - "columns" comes from the ScheduleDefinition: the fields in display order, each with
        ///     its field name and its ColumnHeading (the text actually printed, which the user may
        ///     have retyped). isHidden marks a field that is in the definition but not drawn.
        ///   - "header" and "body" are the laid-out grids, cell by cell, exactly as Revit draws
        ///     them. Which of the two sections holds the column heading row depends on the
        ///     schedule, so both are returned whole rather than guessed at. The text comes from
        ///     TableView.GetCellText, NOT TableSectionData.GetCellText - see ReadSection for why
        ///     the obvious one quietly returns "" for whole columns.
        /// A cell Revit will not give text for (a merged cell, an image) comes back as null rather
        /// than an empty string, so "blank" and "not readable" stay different answers. An ""
        /// therefore means the cell really is empty; it is not the reader giving up.
        ///
        /// Paged, because a planting schedule for a site is thousands of rows: "offset" and "limit"
        /// apply to the BODY rows only, limit defaults to 100 and is clamped to 500 rather than
        /// refused, and "totalRows" always says how much was not returned. Columns are capped at 60
        /// and the header section at 20 rows, both flagged when they bite.
        /// </summary>
        internal static object ReadSchedule(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            long scheduleId = JsonBody.AsLong(RequireValue(body, "scheduleId"), "scheduleId");
            int offset = JsonBody.OptionalInt(body, "offset", 0);
            int limit = JsonBody.OptionalInt(body, "limit", DefaultScheduleRows);

            if (offset < 0)
            {
                throw BridgeException.BadRequest("\"offset\" cannot be negative; got " + offset + ".");
            }

            if (limit < 1)
            {
                limit = 1;
            }

            if (limit > MaxScheduleRows)
            {
                limit = MaxScheduleRows;
            }

            ViewSchedule schedule = document.GetElement(new ElementId(scheduleId)) as ViewSchedule;
            if (schedule == null)
            {
                throw BridgeException.BadRequest(
                    "Element " + scheduleId + " is not a schedule in this document. Call "
                        + "/revit-mcp/views and look for viewType Schedule.");
            }

            TableData table = schedule.GetTableData();

            Dictionary<string, object> result = new Dictionary<string, object>
            {
                { "id", schedule.Id.Value },
                { "name", RevitFacts.SafeName(schedule) },
                { "viewType", schedule.ViewType.ToString() },
                { "columns", ScheduleColumns(schedule) },
                {
                    "header",
                    ReadSection(
                        schedule,
                        SectionType.Header,
                        table.GetSectionData(SectionType.Header),
                        0,
                        MaxHeaderRows)
                },
                {
                    "body",
                    ReadSection(
                        schedule,
                        SectionType.Body,
                        table.GetSectionData(SectionType.Body),
                        offset,
                        limit)
                },
            };

            return result;
        }

        /// <summary>
        /// Body: {sheetId}. Read-only: where everything on a sheet actually is, in feet on the
        /// paper.
        ///
        /// This exists because laying a sheet out blind does not work. A plan placed at the centre
        /// of an A0 sheet can end up a stamp in the bottom third of it, and from outside Revit
        /// there is no way to tell whether that is the view's crop, the title block's extent or the
        /// viewport's position - they are three different fixes. So all three are reported
        /// together, every one of them measured:
        ///   - "outline" is View.Outline for the sheet: the paper, as a BoundingBoxUV in feet.
        ///   - "titleblocks" carries each title block instance's bounding box in sheet coordinates,
        ///     which is the real drawing area - a title block is not always flush with the paper.
        ///   - "viewports" carries GetBoxCenter (the point sheets/set-viewport-position moves),
        ///     GetBoxOutline (what the view occupies, crop included) and GetLabelOutline (the view
        ///     title, which sits outside the box and is what usually collides with the next view).
        ///   - "scheduleInstances" carries ScheduleSheetInstance.Point, which is the TOP-LEFT of a
        ///     schedule rather than its centre, plus its bounding box - the pair that says whether
        ///     a schedule is running off the bottom of the sheet.
        ///
        /// An outline Revit will not give (a viewport on a placeholder sheet, an element with no
        /// geometry in this view) comes back null rather than as zeroes.
        /// </summary>
        internal static object SheetLayout(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            long sheetId = JsonBody.AsLong(RequireValue(body, "sheetId"), "sheetId");

            ViewSheet sheet = document.GetElement(new ElementId(sheetId)) as ViewSheet;
            if (sheet == null)
            {
                throw BridgeException.BadRequest(
                    "Element " + sheetId + " is not a sheet in this document. Call "
                        + "/revit-mcp/sheets for the ones that are.");
            }

            List<object> titleblocks = new List<object>();
            foreach (Element element in new FilteredElementCollector(document, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType())
            {
                titleblocks.Add(new Dictionary<string, object>
                {
                    { "id", element.Id.Value },
                    { "name", RevitFacts.SafeName(element) },
                    { "typeName", RevitFacts.TypeName(document, element) },
                    { "bounds", SheetBounds(element, sheet) },
                });
            }

            List<object> viewports = new List<object>();
            foreach (ElementId viewportId in sheet.GetAllViewports())
            {
                Viewport viewport = document.GetElement(viewportId) as Viewport;
                if (viewport == null)
                {
                    continue;
                }

                viewports.Add(ReadViewport(document, viewport));
            }

            List<object> schedules = new List<object>();
            foreach (Element element in new FilteredElementCollector(document, sheet.Id)
                .OfClass(typeof(ScheduleSheetInstance)))
            {
                ScheduleSheetInstance instance = element as ScheduleSheetInstance;
                if (instance == null)
                {
                    continue;
                }

                schedules.Add(ReadScheduleInstance(document, instance, sheet));
            }

            return new Dictionary<string, object>
            {
                { "id", sheet.Id.Value },
                { "sheetNumber", sheet.SheetNumber },
                { "name", RevitFacts.SafeName(sheet) },
                { "isPlaceholder", sheet.IsPlaceholder },
                { "outline", OutlineUV(sheet) },
                { "titleblocks", titleblocks },
                { "viewports", viewports },
                { "scheduleInstances", schedules },
            };
        }

        /// <summary>
        /// Body: {sheetIds?: [...], limit?}. READ-ONLY, and read-only is the whole story: the
        /// Project Browser's Sheets grouping can be INSPECTED from the API and cannot be changed
        /// by it. That is a hard limit of Revit 2025's API, not a gap in this bridge, and this
        /// endpoint exists to report it with evidence instead of leaving a caller to discover it
        /// by writing something that silently does nothing.
        ///
        /// What the API gives, all of it read:
        ///   - BrowserOrganization.GetCurrentBrowserOrganizationForSheets: the scheme the Sheets
        ///     section is using right now, with its SortingOrder and SortingParameterId.
        ///   - Every BrowserOrganization element in the document: the scheme NAMES a user could
        ///     pick from in the Revit UI.
        ///   - GetFolderItems(sheetId): for one sheet, the chain of folders it sits in, each with
        ///     the folder name and the id of the PARAMETER that produced it. This is the only way
        ///     the grouping levels are observable at all - the scheme does not expose its own
        ///     definition - so "groupingLevels" here is derived from a sample sheet and is labelled
        ///     as such.
        ///
        /// What the API does NOT give, checked member by member rather than assumed:
        ///   - BrowserOrganization has no Create and no Duplicate.
        ///   - SortingOrder and SortingParameterId are get-only; the compiled RevitAPI.dll carries
        ///     get_SortingOrder and get_SortingParameterId and no matching setters.
        ///   - There is no method to add, remove or reorder grouping levels, and none to make a
        ///     scheme the active one for the browser.
        ///   - ViewSheetSet.SheetOrganizationId IS settable, and it is a red herring: it orders a
        ///     print/export set, not the Project Browser tree.
        /// So "canApplyFromApi" is false and always false. Stamping the grouping parameter on the
        /// sheets is a job this bridge CAN do - that is parameters/create-project and
        /// sheets/set-parameter - and a human then points the browser at it once, in the Revit UI,
        /// right-click Sheets > Browser Organization. Reporting anything else would be a lie.
        /// </summary>
        internal static object BrowserOrganization(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireDocument(app);

            int limit = JsonBody.OptionalInt(body, "limit", DefaultScheduleRows);
            if (limit < 1)
            {
                limit = 1;
            }

            if (limit > MaxScheduleRows)
            {
                limit = MaxScheduleRows;
            }

            List<ViewSheet> sheets = new List<ViewSheet>();

            JsonElement requested;
            if (JsonBody.TryGet(body, "sheetIds", out requested))
            {
                foreach (long sheetId in JsonBody.RequireIds(body, "sheetIds"))
                {
                    ViewSheet sheet = document.GetElement(new ElementId(sheetId)) as ViewSheet;
                    if (sheet == null)
                    {
                        throw BridgeException.BadRequest(
                            "Element " + sheetId + " is not a sheet in this document. Call "
                                + "/revit-mcp/sheets for the ones that are.");
                    }

                    sheets.Add(sheet);
                }
            }
            else
            {
                foreach (Element element in new FilteredElementCollector(document)
                    .OfClass(typeof(ViewSheet)))
                {
                    ViewSheet sheet = element as ViewSheet;
                    if (sheet != null && !sheet.IsTemplate)
                    {
                        sheets.Add(sheet);
                    }
                }
            }

            int totalSheets = sheets.Count;

            Autodesk.Revit.DB.BrowserOrganization active =
                Autodesk.Revit.DB.BrowserOrganization.GetCurrentBrowserOrganizationForSheets(document);

            List<object> rows = new List<object>();
            List<object> groupingLevels = null;
            string groupingSampledFrom = null;

            for (int index = 0; index < sheets.Count && index < limit; index++)
            {
                ViewSheet sheet = sheets[index];
                List<object> folders = FolderChain(document, active, sheet.Id);

                if (groupingLevels == null && folders.Count > 0)
                {
                    // The scheme will not describe its own levels, so they are read off the first
                    // sheet that actually sits in a folder. Said out loud in the answer.
                    groupingLevels = ParameterChain(folders);
                    groupingSampledFrom = sheet.SheetNumber;
                }

                rows.Add(new Dictionary<string, object>
                {
                    { "id", sheet.Id.Value },
                    { "sheetNumber", sheet.SheetNumber },
                    { "name", RevitFacts.SafeName(sheet) },
                    { "folders", folders },
                });
            }

            List<object> schemes = new List<object>();
            try
            {
                foreach (Element element in new FilteredElementCollector(document)
                    .OfClass(typeof(Autodesk.Revit.DB.BrowserOrganization)))
                {
                    Autodesk.Revit.DB.BrowserOrganization scheme =
                        element as Autodesk.Revit.DB.BrowserOrganization;
                    if (scheme == null)
                    {
                        continue;
                    }

                    schemes.Add(DescribeOrganization(document, scheme));
                }
            }
            catch (Exception)
            {
                // Listing the schemes is a bonus; the active one is the answer that matters.
            }

            return new Dictionary<string, object>
            {
                { "active", active == null ? null : DescribeOrganization(document, active) },
                { "schemes", schemes },

                // The point of the endpoint. Not a transient failure and not worth retrying.
                { "canApplyFromApi", false },
                { "applyLimitation",
                    "The BROWSER ORGANIZATION SCHEME is read-only in Revit's API: "
                        + "BrowserOrganization has no Create, SortingOrder and SortingParameterId "
                        + "are get-only, there is no method to define folder levels, and none to "
                        + "make a scheme active. ViewSheetSet.SheetOrganizationId is settable and "
                        + "is a red herring - it orders a print set, not the browser. So the "
                        + "scheme cannot be switched or built from here. The half that IS "
                        + "automatable is the parameter a scheme would group by: create it with "
                        + "/parameters/create-project and stamp the sheets with "
                        + "/sheets/set-parameter. A human then points the browser at it once in "
                        + "the Revit UI: right-click Sheets > Browser Organization > Edit, "
                        + "Grouping and Sorting, group by that parameter." },
                { "sheetCollectionAlternative",
                    "Separately from the scheme, Revit 2025 has SheetCollection - the native "
                        + "collapsible sheet groups in the browser - and THAT one is writable: "
                        + "SheetCollection.Create(document, name) makes one and "
                        + "ViewSheet.SheetCollectionId has a setter, both inside a transaction. "
                        + "It is a different mechanism from the grouping scheme and it is ONE "
                        + "level deep: a sheet belongs to exactly one collection, so it gives "
                        + "collapsible groups, not a nested hierarchy. Use the dedicated sheet "
                        + "collection tools for it; this endpoint stays read-only and reports "
                        + "the scheme only." },
                { "groupingLevels", groupingLevels },
                { "groupingLevelsSource", groupingLevels == null
                    ? "No sheet in this document sits in a browser folder, so the scheme's "
                        + "grouping levels are not observable. That is what a flat, ungrouped "
                        + "Sheets list looks like from the API."
                    : "Derived from the folders sheet " + groupingSampledFrom + " actually sits "
                        + "in - BrowserOrganization does not expose its own definition, so this "
                        + "is a measurement of one sheet, not a read of the scheme." },
                { "totalSheets", totalSheets },
                { "returnedSheets", rows.Count },
                { "truncated", rows.Count < totalSheets },
                { "sheets", rows },
            };
        }

        /// <summary>
        /// Body: {viewportId, center: {x, y}, labelOffset?: {x, y}, labelLineLength?, dryRun?}.
        /// Move one viewport on its sheet. All lengths are feet ON THE PAPER, not model feet.
        ///
        /// "center" goes through Viewport.SetBoxCenter, which takes the centre of the viewport's
        /// box - the same point sheets/layout reports as "center". It is not the bottom-left and it
        /// is not the view's origin; sheets/layout first, then move relative to what it said.
        ///
        /// "labelOffset" and "labelLineLength" are the view title: where it sits relative to the
        /// viewport and how long the line under it is. Both are optional and both are left exactly
        /// as they are when not passed.
        ///
        /// "dryRun" DEFAULTS TO TRUE and reports the current position without moving anything.
        ///
        /// The answer carries "before" and "after", both read back through GetBoxCenter /
        /// GetBoxOutline / GetLabelOutline. Revit does not always put a viewport where it was told
        /// - a viewport whose ViewportPositioning is not free is the case - and a reported centre
        /// that is not the one requested is the only way to see it.
        /// </summary>
        internal static object SetViewportPosition(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            long viewportId = JsonBody.AsLong(RequireValue(body, "viewportId"), "viewportId");
            bool dryRun = JsonBody.OptionalBool(body, "dryRun", true);

            Viewport viewport = document.GetElement(new ElementId(viewportId)) as Viewport;
            if (viewport == null)
            {
                throw BridgeException.BadRequest(
                    "Element " + viewportId + " is not a viewport in this document. Call "
                        + "/revit-mcp/sheets/layout for the viewports on a sheet.");
            }

            XYZ center = RevitFacts.RequirePoint(body, "center");

            JsonElement offsetValue;
            XYZ labelOffset = null;
            if (JsonBody.TryGet(body, "labelOffset", out offsetValue))
            {
                labelOffset = RevitFacts.ReadPoint(offsetValue, "labelOffset");
            }

            JsonElement lengthValue;
            double labelLineLength = 0.0;
            bool setLabelLineLength = JsonBody.TryGet(body, "labelLineLength", out lengthValue);
            if (setLabelLineLength)
            {
                labelLineLength = JsonBody.AsDouble(lengthValue, "labelLineLength");

                if (labelLineLength <= 0.0)
                {
                    throw BridgeException.BadRequest(
                        "\"labelLineLength\" must be a positive length in feet on the paper; got "
                            + Number(labelLineLength) + ".");
                }
            }

            Dictionary<string, object> before = ReadViewport(document, viewport);

            Dictionary<string, object> result = new Dictionary<string, object>
            {
                { "id", viewport.Id.Value },
                { "sheetId", viewport.SheetId.Value },
                { "viewId", viewport.ViewId.Value },
                { "requestedCenter", Point2(center) },
                { "before", before },
                { "after", null },
            };

            if (dryRun)
            {
                result["dryRun"] = true;
                result["applied"] = false;
                return result;
            }

            return RevitWrite.InGroup(document, "MCP: set viewport position", delegate
            {
                RevitWrite.InTransaction(document, "Set viewport position", delegate
                {
                    // z is always 0 on a sheet: the paper has two dimensions, and Revit refuses a
                    // box centre off that plane.
                    viewport.SetBoxCenter(new XYZ(center.X, center.Y, 0.0));

                    if (labelOffset != null)
                    {
                        viewport.LabelOffset = new XYZ(labelOffset.X, labelOffset.Y, 0.0);
                    }

                    if (setLabelLineLength)
                    {
                        viewport.LabelLineLength = labelLineLength;
                    }
                });

                result["dryRun"] = false;
                result["applied"] = true;
                result["after"] = ReadViewport(document, viewport);
                return result;
            });
        }

        /// <summary>
        /// Body: {instanceId, topLeft: {x, y}, dryRun?}. Move one schedule on its sheet.
        ///
        /// This exists because sheets/set-viewport-position CANNOT move a schedule and never
        /// could: a schedule on a sheet is a ScheduleSheetInstance, not a Viewport, and it is
        /// anchored by ScheduleSheetInstance.Point, which is its TOP-LEFT corner rather than the
        /// centre of a box. Passing a schedule instance id to the viewport tool fails; this is the
        /// tool for it. Lengths are feet ON THE PAPER.
        ///
        /// A revision schedule inside a title block is refused. Revit prohibits setting Point on
        /// one, and its position belongs to the title block family, so the refusal names it rather
        /// than letting the API throw something less readable.
        ///
        /// "dryRun" DEFAULTS TO TRUE. The answer carries "before" and "after", both with bounds
        /// measured off the sheet - not the requested point echoed back - because a schedule that
        /// overflows its page is long, not misplaced, and only the measured bounds show that.
        /// </summary>
        internal static object SetSchedulePosition(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            long instanceId = JsonBody.AsLong(RequireValue(body, "instanceId"), "instanceId");
            bool dryRun = JsonBody.OptionalBool(body, "dryRun", true);

            ScheduleSheetInstance instance =
                document.GetElement(new ElementId(instanceId)) as ScheduleSheetInstance;
            if (instance == null)
            {
                throw BridgeException.BadRequest(
                    "Element " + instanceId + " is not a schedule placed on a sheet. Call "
                        + "/revit-mcp/sheets/layout and use an id from \"scheduleInstances\" - note "
                        + "that is the INSTANCE id, not the schedule view's id.");
            }

            if (instance.IsTitleblockRevisionSchedule)
            {
                throw new BridgeException(
                    409,
                    "REVISION_SCHEDULE_IS_FIXED",
                    "Element " + instanceId + " is the revision schedule inside a title block. "
                        + "Revit prohibits setting its position, and where it sits is a property of "
                        + "the title block family, not of this sheet. Edit the family to move it.");
            }

            XYZ topLeft = RevitFacts.RequirePoint(body, "topLeft");

            ViewSheet sheet = document.GetElement(instance.OwnerViewId) as ViewSheet;

            Dictionary<string, object> result = new Dictionary<string, object>
            {
                { "id", instance.Id.Value },
                { "sheetId", sheet == null ? null : (object)sheet.Id.Value },
                { "requestedTopLeft", Point2(topLeft) },
                { "before", ReadScheduleInstance(document, instance, sheet) },
                { "after", null },
            };

            if (dryRun)
            {
                result["dryRun"] = true;
                result["applied"] = false;
                return result;
            }

            return RevitWrite.InGroup(document, "MCP: set schedule position", delegate
            {
                RevitWrite.InTransaction(document, "Set schedule position", delegate
                {
                    // z is always 0 on a sheet.
                    instance.Point = new XYZ(topLeft.X, topLeft.Y, 0.0);
                });

                result["dryRun"] = false;
                result["applied"] = true;
                result["after"] = ReadScheduleInstance(document, instance, sheet);
                return result;
            });
        }

        /// <summary>
        /// Body: {scheduleId, itemized?, groupBy?, fields?, grandTotal?, dryRun?}. Reshape how an
        /// existing schedule presents the rows it already has.
        ///
        /// WHAT THIS DOES NOT DO: it never adds a field and never removes one. A schedule's
        /// columns are its author's decision; this endpoint changes how the rows are grouped,
        /// totalled and headed, nothing else. schedules/create is what makes a new one.
        ///
        /// The interesting one is "itemized". ScheduleDefinition.IsItemized false is what turns
        /// 142 rows of one plant each into one row per species carrying a count - it is the API
        /// behind the "Itemize every instance" tick box, and it does nothing on its own: rows
        /// collapse only where the sort/group fields make them equal, so "itemized": false with no
        /// "groupBy" collapses by whatever grouping already exists, which may be none at all. Send
        /// both together.
        ///
        /// "groupBy" REPLACES the sort/group list rather than appending to it, and the previous
        /// list comes back under "before" so the change is reversible by hand. Each entry takes
        /// {field, sortOrder?, showHeader?, showFooter?, showFooterCount?, showBlankLine?}.
        /// Revit refuses to sort or group by some fields - Count, percentages, and formulas
        /// derived from them, because none of those have a value until after grouping - so
        /// CanSortByField is checked for every entry BEFORE anything is written.
        ///
        /// "fields" adjusts existing columns: {field, heading?, widthFt?, totals?, hidden?}.
        /// "totals" is ScheduleField.DisplayType - true means Totals, false means Standard, or
        /// name one of Standard/Totals/Min/Max/MinMax. CanTotal is checked first: a text column
        /// cannot be summed and asking is an error, not a silent no-op. "widthFt" sets the column
        /// width in feet; Revit keeps the grid and sheet widths aligned, so both are reported.
        ///
        /// A "field" is addressed either by its numeric ScheduleFieldId - which schedules/read
        /// reports as "fieldId" - or by name. A name is matched against both the field name and
        /// the column heading, case-insensitively, and an ambiguous name is refused with the
        /// candidates listed rather than resolved to the first hit.
        ///
        /// Everything is validated before the transaction opens, so a request that is wrong in its
        /// last entry changes nothing at all. "dryRun" DEFAULTS TO TRUE.
        ///
        /// "bodyRows" is reported before and after: it is the row count Revit actually lays out,
        /// and it is the only honest proof that a regrouping did what was asked.
        /// </summary>
        internal static object ConfigureSchedule(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            long scheduleId = JsonBody.AsLong(RequireValue(body, "scheduleId"), "scheduleId");
            bool dryRun = JsonBody.OptionalBool(body, "dryRun", true);

            ViewSchedule schedule = document.GetElement(new ElementId(scheduleId)) as ViewSchedule;
            if (schedule == null)
            {
                throw BridgeException.BadRequest(
                    "Element " + scheduleId + " is not a schedule in this document. Call "
                        + "/revit-mcp/views and look for viewType Schedule.");
            }

            ScheduleDefinition definition = schedule.Definition;

            JsonElement itemizedValue;
            bool setItemized = JsonBody.TryGet(body, "itemized", out itemizedValue);
            bool itemized = setItemized && JsonBody.AsBool(itemizedValue, "itemized");

            JsonElement groupByValue;
            bool setGroupBy = JsonBody.TryGet(body, "groupBy", out groupByValue);

            JsonElement fieldsValue;
            bool setFields = JsonBody.TryGet(body, "fields", out fieldsValue);

            JsonElement grandTotalValue;
            bool setGrandTotal = JsonBody.TryGet(body, "grandTotal", out grandTotalValue);

            JsonElement filtersValue;
            bool setFilters = JsonBody.TryGet(body, "filters", out filtersValue);

            if (!setItemized && !setGroupBy && !setFields && !setGrandTotal && !setFilters)
            {
                throw BridgeException.BadRequest(
                    "Nothing to change. Pass at least one of \"itemized\", \"groupBy\", \"fields\", "
                        + "\"grandTotal\" or \"filters\". To read a schedule instead, call "
                        + "/revit-mcp/schedules/read.");
            }

            if (definition.IsKeySchedule && (setItemized || setGroupBy))
            {
                throw new BridgeException(
                    409,
                    "KEY_SCHEDULE_NOT_GROUPABLE",
                    "\"" + RevitFacts.SafeName(schedule) + "\" is a key schedule. Its rows are the "
                        + "keys themselves, so itemizing and grouping do not apply to it.");
            }

            // --- validate everything first ----------------------------------------------------
            // A request that is wrong in its last entry must change nothing, so every lookup, every
            // CanSortByField and every CanTotal happens here, before the transaction opens.

            List<ScheduleSortGroupField> sortGroup = new List<ScheduleSortGroupField>();
            if (setGroupBy)
            {
                if (groupByValue.ValueKind != JsonValueKind.Array)
                {
                    throw BridgeException.BadRequest("\"groupBy\" must be an array.");
                }

                foreach (JsonElement entry in groupByValue.EnumerateArray())
                {
                    ScheduleField field = ResolveField(definition, RequireValue(entry, "field"));

                    if (!definition.CanSortByField(field.FieldId))
                    {
                        throw BridgeException.BadRequest(
                            "Revit will not sort or group by \"" + SafeFieldName(field) + "\". "
                                + "Count fields, percentages and formulas built on them have no "
                                + "value until after grouping has happened, so they cannot decide "
                                + "it. Group by a parameter field instead.");
                    }

                    ScheduleSortGroupField sort = new ScheduleSortGroupField(
                        field.FieldId,
                        ReadSortOrder(entry));

                    JsonElement flag;
                    if (JsonBody.TryGet(entry, "showHeader", out flag))
                    {
                        sort.ShowHeader = JsonBody.AsBool(flag, "showHeader");
                    }

                    if (JsonBody.TryGet(entry, "showFooter", out flag))
                    {
                        sort.ShowFooter = JsonBody.AsBool(flag, "showFooter");
                    }

                    if (JsonBody.TryGet(entry, "showFooterCount", out flag))
                    {
                        sort.ShowFooterCount = JsonBody.AsBool(flag, "showFooterCount");
                    }

                    if (JsonBody.TryGet(entry, "showBlankLine", out flag))
                    {
                        sort.ShowBlankLine = JsonBody.AsBool(flag, "showBlankLine");
                    }

                    sortGroup.Add(sort);
                }
            }

            List<FieldEdit> fieldEdits = new List<FieldEdit>();
            if (setFields)
            {
                if (fieldsValue.ValueKind != JsonValueKind.Array)
                {
                    throw BridgeException.BadRequest("\"fields\" must be an array.");
                }

                foreach (JsonElement entry in fieldsValue.EnumerateArray())
                {
                    fieldEdits.Add(ReadFieldEdit(definition, entry));
                }
            }

            List<ScheduleFilter> filters = new List<ScheduleFilter>();
            if (setFilters)
            {
                if (filtersValue.ValueKind != JsonValueKind.Array)
                {
                    throw BridgeException.BadRequest(
                        "\"filters\" must be an array. Pass [] to clear every filter; omit it "
                            + "entirely to leave the existing filters alone.");
                }

                foreach (JsonElement entry in filtersValue.EnumerateArray())
                {
                    filters.Add(ReadFilter(definition, entry));
                }
            }

            Dictionary<string, object> result = new Dictionary<string, object>
            {
                { "id", schedule.Id.Value },
                { "name", RevitFacts.SafeName(schedule) },
                { "before", DescribeSchedule(schedule) },
                { "after", null },
            };

            if (dryRun)
            {
                result["dryRun"] = true;
                result["applied"] = false;
                return result;
            }

            return RevitWrite.InGroup(document, "MCP: configure schedule", delegate
            {
                RevitWrite.InTransaction(document, "Configure schedule", delegate
                {
                    if (setItemized)
                    {
                        definition.IsItemized = itemized;
                    }

                    if (setGroupBy)
                    {
                        definition.SetSortGroupFields(sortGroup);
                    }

                    if (setGrandTotal)
                    {
                        ApplyGrandTotal(definition, grandTotalValue);
                    }

                    if (setFilters)
                    {
                        // SetFilters replaces the whole set, which is why omitting "filters"
                        // has to skip this entirely rather than pass an empty list.
                        definition.SetFilters(filters);
                    }

                    for (int index = 0; index < fieldEdits.Count; index++)
                    {
                        fieldEdits[index].Apply();
                    }
                });

                result["dryRun"] = false;
                result["applied"] = true;
                result["after"] = DescribeSchedule(schedule);
                return result;
            });
        }

        // --- schedule configuration helpers ----------------------------------------------------

        /// <summary>One validated change to one existing column, ready to apply.</summary>
        private sealed class FieldEdit
        {
            internal ScheduleField Field;
            internal string Heading;
            internal double Width;
            internal bool SetWidth;
            internal ScheduleFieldDisplayType Display;
            internal bool SetDisplay;
            internal bool Hidden;
            internal bool SetHidden;

            internal void Apply()
            {
                if (Heading != null)
                {
                    Field.ColumnHeading = Heading;
                }

                if (SetWidth)
                {
                    Field.GridColumnWidth = Width;
                }

                if (SetDisplay)
                {
                    Field.DisplayType = Display;
                }

                if (SetHidden)
                {
                    Field.IsHidden = Hidden;
                }
            }
        }

        private static FieldEdit ReadFieldEdit(ScheduleDefinition definition, JsonElement entry)
        {
            FieldEdit edit = new FieldEdit();
            edit.Field = ResolveField(definition, RequireValue(entry, "field"));

            JsonElement value;

            if (JsonBody.TryGet(entry, "heading", out value))
            {
                edit.Heading = JsonBody.AsString(value, "heading");
                if (edit.Heading == null)
                {
                    throw BridgeException.BadRequest("\"heading\" cannot be null.");
                }
            }

            if (JsonBody.TryGet(entry, "widthFt", out value))
            {
                edit.Width = JsonBody.AsDouble(value, "widthFt");
                if (edit.Width <= 0.0)
                {
                    throw BridgeException.BadRequest(
                        "\"widthFt\" must be a positive width in feet on the paper; got "
                            + Number(edit.Width) + ".");
                }

                edit.SetWidth = true;
            }

            if (JsonBody.TryGet(entry, "hidden", out value))
            {
                edit.Hidden = JsonBody.AsBool(value, "hidden");
                edit.SetHidden = true;
            }

            if (JsonBody.TryGet(entry, "totals", out value))
            {
                edit.Display = ReadDisplayType(value);
                edit.SetDisplay = true;

                if (edit.Display != ScheduleFieldDisplayType.Standard && !edit.Field.CanTotal())
                {
                    throw BridgeException.BadRequest(
                        "\"" + SafeFieldName(edit.Field) + "\" cannot be totalled - Revit's own "
                            + "CanTotal says so, which is the case for text and for anything with "
                            + "no unit to add up. Read the schedule's columns for \"canTotal\" "
                            + "before asking for totals.");
                }
            }

            return edit;
        }

        private static ScheduleFieldDisplayType ReadDisplayType(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.True)
            {
                return ScheduleFieldDisplayType.Totals;
            }

            if (value.ValueKind == JsonValueKind.False)
            {
                return ScheduleFieldDisplayType.Standard;
            }

            string name = JsonBody.AsString(value, "totals");
            ScheduleFieldDisplayType parsed;
            if (name != null && Enum.TryParse(name, true, out parsed))
            {
                return parsed;
            }

            throw BridgeException.BadRequest(
                "\"totals\" must be true, false, or one of Standard, Totals, Min, Max, MinMax; got "
                    + (name ?? value.ValueKind.ToString()) + ".");
        }

        private static ScheduleSortOrder ReadSortOrder(JsonElement entry)
        {
            JsonElement value;
            if (!JsonBody.TryGet(entry, "sortOrder", out value))
            {
                return ScheduleSortOrder.Ascending;
            }

            string name = JsonBody.AsString(value, "sortOrder");
            ScheduleSortOrder parsed;
            if (name != null && Enum.TryParse(name, true, out parsed))
            {
                return parsed;
            }

            throw BridgeException.BadRequest(
                "\"sortOrder\" must be Ascending or Descending; got " + (name ?? "null") + ".");
        }

        private static void ApplyGrandTotal(ScheduleDefinition definition, JsonElement grandTotal)
        {
            JsonElement value;

            if (JsonBody.TryGet(grandTotal, "show", out value))
            {
                definition.ShowGrandTotal = JsonBody.AsBool(value, "grandTotal.show");
            }

            if (JsonBody.TryGet(grandTotal, "showCount", out value))
            {
                definition.ShowGrandTotalCount = JsonBody.AsBool(value, "grandTotal.showCount");
            }

            if (JsonBody.TryGet(grandTotal, "showTitle", out value))
            {
                definition.ShowGrandTotalTitle = JsonBody.AsBool(value, "grandTotal.showTitle");
            }

            if (JsonBody.TryGet(grandTotal, "title", out value))
            {
                definition.GrandTotalTitle = JsonBody.AsString(value, "grandTotal.title");
            }
        }

        /// <summary>
        /// One entry of "filters", validated against the field it names. The operator allowlist is
        /// deliberately short - ScheduleFilterType carries eighteen values, most of which need a
        /// shape of request this endpoint does not take - and the value's JSON type has to match
        /// what the field stores, because a string filter on a numeric field throws inside
        /// SetFilters, which is past the point where the whole edit can still be abandoned.
        /// </summary>
        private static ScheduleFilter ReadFilter(ScheduleDefinition definition, JsonElement entry)
        {
            ScheduleField field = ResolveField(definition, RequireValue(entry, "field"));
            string name = SafeFieldName(field);

            if (!definition.CanFilterByValue(field.FieldId))
            {
                throw BridgeException.BadRequest(
                    "Revit will not filter \"" + name + "\" by value. Count fields and the "
                        + "percentages and formulas built on them have no value until the rows "
                        + "have been grouped, so they cannot decide which rows appear.");
            }

            string op = JsonBody.AsString(RequireValue(entry, "operator"), "operator");
            ScheduleFilterType filterType;

            if (string.Equals(op, "BeginsWith", StringComparison.OrdinalIgnoreCase))
            {
                filterType = ScheduleFilterType.BeginsWith;
            }
            else if (string.Equals(op, "Equal", StringComparison.OrdinalIgnoreCase))
            {
                filterType = ScheduleFilterType.Equal;
            }
            else if (string.Equals(op, "GreaterThan", StringComparison.OrdinalIgnoreCase))
            {
                filterType = ScheduleFilterType.GreaterThan;
            }
            else
            {
                throw BridgeException.BadRequest(
                    "Unknown filter operator \"" + op + "\". This endpoint takes BeginsWith, Equal "
                        + "or GreaterThan.");
            }

            JsonElement value = RequireValue(entry, "value");
            bool isText = definition.CanFilterBySubstring(field.FieldId);

            if (value.ValueKind == JsonValueKind.String)
            {
                if (!isText)
                {
                    throw BridgeException.BadRequest(
                        "\"" + name + "\" does not hold text, so it cannot be filtered against the "
                            + "string " + value.GetRawText() + ". Pass a number.");
                }

                if (filterType == ScheduleFilterType.GreaterThan)
                {
                    throw BridgeException.BadRequest(
                        "GreaterThan compares numbers; \"" + name + "\" holds text. Use BeginsWith "
                            + "or Equal on it.");
                }

                return new ScheduleFilter(
                    field.FieldId,
                    filterType,
                    JsonBody.AsString(value, "value"));
            }

            if (value.ValueKind == JsonValueKind.Number)
            {
                if (isText)
                {
                    throw BridgeException.BadRequest(
                        "\"" + name + "\" holds text, so it cannot be filtered against the number "
                            + value.GetRawText() + ". Pass a string.");
                }

                if (filterType == ScheduleFilterType.BeginsWith)
                {
                    throw BridgeException.BadRequest(
                        "BeginsWith compares text; \"" + name + "\" holds a number. Use Equal or "
                            + "GreaterThan on it.");
                }

                // Which ScheduleFilter constructor is legal is decided by what the FIELD
                // stores, never by how the number was written in JSON. 0 and 0.0 are the
                // same JSON number and both parse as an Int32, so keying off TryGetInt32
                // built an integer filter for Area and SetFilters refused it with "A filter
                // value is not valid for field/filter type" - thrown deep inside the commit,
                // past the point where the edit can still be abandoned cleanly.
                ForgeTypeId spec = field.GetSpecTypeId();

                if (IsIntegerValuedSpec(spec))
                {
                    int whole;
                    if (!value.TryGetInt32(out whole))
                    {
                        throw BridgeException.BadRequest(
                            "\"" + name + "\" is counted in whole numbers, so it cannot be "
                                + "filtered against " + value.GetRawText() + ".");
                    }

                    return new ScheduleFilter(field.FieldId, filterType, whole);
                }

                return new ScheduleFilter(field.FieldId, filterType, value.GetDouble());
            }

            throw BridgeException.BadRequest(
                "\"value\" must be a string or a number; got " + value.ValueKind + ".");
        }

        /// <summary>
        /// Whether a schedule field holds a whole number rather than a measurement. Only these
        /// take ScheduleFilter's int constructor; a measurable spec - Length, Area, Volume, Angle
        /// - and the unitless Number are doubles, and handing one an int is refused by SetFilters.
        ///
        /// A field with no spec at all (Empty) is treated as a double, which is what every
        /// measurable field is and what Revit's own filter value defaults to.
        /// </summary>
        private static bool IsIntegerValuedSpec(ForgeTypeId spec)
        {
            if (spec == null || spec.Empty())
            {
                return false;
            }

            return spec == SpecTypeId.Int.Integer || spec == SpecTypeId.Boolean.YesNo;
        }

        /// <summary>
        /// A field reference, either the numeric ScheduleFieldId or a name. A name is matched
        /// against both the field name and the column heading; more than one match is an error
        /// with the candidates listed, because picking the first would be a guess.
        /// </summary>
        private static ScheduleField ResolveField(ScheduleDefinition definition, JsonElement value)
        {
            IList<ScheduleFieldId> order = definition.GetFieldOrder();

            if (value.ValueKind == JsonValueKind.Number)
            {
                ScheduleFieldId id = new ScheduleFieldId((int)JsonBody.AsLong(value, "field"));

                if (!definition.IsValidFieldId(id))
                {
                    throw BridgeException.BadRequest(
                        "There is no field " + id.IntegerValue + " in this schedule. The valid ones "
                            + "are " + string.Join(", ", FieldSummaries(definition, order)) + ".");
                }

                return definition.GetField(id);
            }

            string name = JsonBody.AsString(value, "field");
            if (string.IsNullOrEmpty(name))
            {
                throw BridgeException.BadRequest(
                    "\"field\" must be a field id or a field name; got an empty value.");
            }

            List<ScheduleField> matches = new List<ScheduleField>();
            for (int index = 0; index < order.Count; index++)
            {
                ScheduleField field = definition.GetField(order[index]);
                if (field == null)
                {
                    continue;
                }

                if (string.Equals(SafeFieldName(field), name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(field.ColumnHeading, name, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(field);
                }
            }

            if (matches.Count == 0)
            {
                throw BridgeException.BadRequest(
                    "No field called \"" + name + "\" in this schedule. It has "
                        + string.Join(", ", FieldSummaries(definition, order)) + ".");
            }

            if (matches.Count > 1)
            {
                List<string> ambiguous = new List<string>();
                for (int index = 0; index < matches.Count; index++)
                {
                    ambiguous.Add(
                        matches[index].FieldId.IntegerValue.ToString(CultureInfo.InvariantCulture));
                }

                throw BridgeException.BadRequest(
                    "\"" + name + "\" matches " + matches.Count + " fields in this schedule (ids "
                        + string.Join(", ", ambiguous) + "). Address it by its numeric field id "
                        + "instead - schedules/read reports one per column as \"fieldId\".");
            }

            return matches[0];
        }

        private static List<string> FieldSummaries(
            ScheduleDefinition definition,
            IList<ScheduleFieldId> order)
        {
            List<string> summaries = new List<string>();

            for (int index = 0; index < order.Count; index++)
            {
                ScheduleField field = definition.GetField(order[index]);
                if (field == null)
                {
                    continue;
                }

                summaries.Add(
                    field.FieldId.IntegerValue.ToString(CultureInfo.InvariantCulture)
                        + " \"" + SafeFieldName(field) + "\"");
            }

            return summaries;
        }

        /// <summary>
        /// The state of a schedule that configuring it can change, plus the row count Revit
        /// actually lays out - which is the only measurement that proves a regrouping worked.
        /// </summary>
        private static Dictionary<string, object> DescribeSchedule(ViewSchedule schedule)
        {
            ScheduleDefinition definition = schedule.Definition;

            List<object> sortGroup = new List<object>();
            foreach (ScheduleSortGroupField sort in definition.GetSortGroupFields())
            {
                ScheduleField field = definition.GetField(sort.FieldId);

                sortGroup.Add(new Dictionary<string, object>
                {
                    { "fieldId", sort.FieldId.IntegerValue },
                    { "name", field == null ? null : SafeFieldName(field) },
                    { "sortOrder", sort.SortOrder.ToString() },
                    { "showHeader", sort.ShowHeader },
                    { "showFooter", sort.ShowFooter },
                    { "showFooterCount", sort.ShowFooterCount },
                    { "showBlankLine", sort.ShowBlankLine },
                });
            }

            List<object> filters = new List<object>();
            foreach (ScheduleFilter filter in definition.GetFilters())
            {
                ScheduleField field = definition.GetField(filter.FieldId);

                object value = null;
                if (filter.IsStringValue)
                {
                    value = filter.GetStringValue();
                }
                else if (filter.IsIntegerValue)
                {
                    value = filter.GetIntegerValue();
                }
                else if (filter.IsDoubleValue)
                {
                    value = filter.GetDoubleValue();
                }
                else if (filter.IsElementIdValue)
                {
                    value = filter.GetElementIdValue().Value;
                }

                filters.Add(new Dictionary<string, object>
                {
                    { "fieldId", filter.FieldId.IntegerValue },
                    { "name", field == null ? null : SafeFieldName(field) },
                    { "operator", filter.FilterType.ToString() },
                    { "value", value },
                });
            }

            return new Dictionary<string, object>
            {
                { "isItemized", definition.IsItemized },
                { "isKeySchedule", definition.IsKeySchedule },
                { "isMaterialTakeoff", definition.IsMaterialTakeoff },
                { "showGrandTotal", definition.ShowGrandTotal },
                { "showGrandTotalCount", definition.ShowGrandTotalCount },
                { "showGrandTotalTitle", definition.ShowGrandTotalTitle },
                { "grandTotalTitle", definition.GrandTotalTitle },
                { "sortGroupFields", sortGroup },
                { "filters", filters },
                { "columns", ScheduleColumns(schedule) },
                { "bodyRows", BodyRowCount(schedule) },
            };
        }

        private static object BodyRowCount(ViewSchedule schedule)
        {
            try
            {
                TableSectionData section =
                    schedule.GetTableData().GetSectionData(SectionType.Body);
                return section == null ? null : (object)section.NumberOfRows;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // --- crop helpers --------------------------------------------------------------------

        /// <summary>
        /// The crop state of one view: the flags, the model-space bounds of the crop box and the
        /// raw local pair it was derived from, plus whatever the view's template owns.
        /// </summary>
        private static Dictionary<string, object> ReadCrop(Document document, View view)
        {
            Dictionary<string, object> row = new Dictionary<string, object>
            {
                { "id", view.Id.Value },
                { "name", RevitFacts.SafeName(view) },
                { "viewType", view.ViewType.ToString() },
                { "isTemplate", view.IsTemplate },
                { "cropSupported", false },
                { "cropBoxActive", null },
                { "cropBoxVisible", null },
                { "annotationCrop", null },
                { "modelBounds", null },
                { "localBounds", null },
                { "templateId", null },
                { "templateName", null },
                { "templateControlledCrop", new List<object>() },
            };

            BoundingBoxXYZ box = SafeCropBox(view);
            if (box != null)
            {
                row["cropSupported"] = true;
                row["modelBounds"] = ModelBounds(box);
                row["localBounds"] = Box(box.Min, box.Max);
            }

            try
            {
                row["cropBoxActive"] = view.CropBoxActive;
                row["cropBoxVisible"] = view.CropBoxVisible;
            }
            catch (Exception)
            {
                // A view with no crop at all answers neither; nulls already stand for that.
            }

            // Not every view carries the annotation crop parameter, and null says "this view has
            // no such thing" rather than "it is off".
            Parameter annotation = view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
            if (annotation != null)
            {
                row["annotationCrop"] = annotation.AsInteger() != 0;
            }

            ElementId templateId = view.ViewTemplateId;
            if (templateId != null && templateId != ElementId.InvalidElementId)
            {
                View template = document.GetElement(templateId) as View;
                if (template != null)
                {
                    row["templateId"] = template.Id.Value;
                    row["templateName"] = RevitFacts.SafeName(template);
                    row["templateControlledCrop"] = TemplateControlledCrop(template);
                }
            }

            return row;
        }

        /// <summary>
        /// The crop parameters this template takes ownership of, by their English names. A template
        /// controls a parameter when it is in GetTemplateParameterIds and NOT in
        /// GetNonControlledTemplateParameterIds - the second list is the exceptions, which is the
        /// opposite way round from how the dialog reads.
        /// </summary>
        private static List<object> TemplateControlledCrop(View template)
        {
            HashSet<long> controllable = new HashSet<long>();
            HashSet<long> excluded = new HashSet<long>();

            try
            {
                foreach (ElementId id in template.GetTemplateParameterIds())
                {
                    controllable.Add(id.Value);
                }

                foreach (ElementId id in template.GetNonControlledTemplateParameterIds())
                {
                    excluded.Add(id.Value);
                }
            }
            catch (Exception)
            {
                return new List<object>();
            }

            List<object> controlled = new List<object>();

            foreach (KeyValuePair<string, BuiltInParameter> entry in CropParameters)
            {
                long id = new ElementId(entry.Value).Value;

                if (controllable.Contains(id) && !excluded.Contains(id))
                {
                    controlled.Add(entry.Key);
                }
            }

            return controlled;
        }

        /// <summary>
        /// Refuses a view views/set-crop cannot honestly crop: one with no crop box, and one whose
        /// template owns the crop flags the endpoint writes.
        /// </summary>
        private static void RequireCroppable(Document document, View view)
        {
            if (view.IsTemplate || SafeCropBox(view) == null)
            {
                throw new BridgeException(
                    409,
                    "CROP_NOT_SUPPORTED",
                    "View \"" + RevitFacts.SafeName(view) + "\" (" + view.Id.Value + ", "
                        + view.ViewType + ") has no crop box. A sheet, a schedule, a legend and a "
                        + "view template have none - Revit gives a crop only to a view of the model.");
            }

            ElementId templateId = view.ViewTemplateId;
            if (templateId == null || templateId == ElementId.InvalidElementId)
            {
                return;
            }

            View template = document.GetElement(templateId) as View;
            if (template == null)
            {
                return;
            }

            List<object> controlled = TemplateControlledCrop(template);
            List<string> blocking = new List<string>();

            foreach (BuiltInParameter parameter in WrittenCropParameters)
            {
                foreach (KeyValuePair<string, BuiltInParameter> entry in CropParameters)
                {
                    if (entry.Value == parameter && controlled.Contains(entry.Key))
                    {
                        blocking.Add(entry.Key);
                    }
                }
            }

            if (blocking.Count > 0)
            {
                throw new BridgeException(
                    409,
                    "CROP_CONTROLLED_BY_TEMPLATE",
                    "View \"" + RevitFacts.SafeName(view) + "\" (" + view.Id.Value + ") uses view "
                        + "template \"" + RevitFacts.SafeName(template) + "\" (" + template.Id.Value
                        + "), which controls " + string.Join(" and ", blocking) + ". Revit ignores "
                        + "those written on the view, so cropping it from here would report a "
                        + "success the drawing would not show. Change the template, or take the "
                        + "parameter out of its control in the Revit UI (View Template dialog, "
                        + "clear the Include tick), then retry. Nothing was changed.");
            }
        }

        /// <summary>The crop box, or null for a view that has none. The getter throws on some.</summary>
        private static BoundingBoxXYZ SafeCropBox(View view)
        {
            try
            {
                return view.CropBox;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The axis-aligned MODEL box around a crop box: all eight corners of the local box put
        /// through its Transform, then min/max per axis. Transforming only Min and Max would be
        /// wrong for any view whose transform rotates.
        /// </summary>
        private static Dictionary<string, object> ModelBounds(BoundingBoxXYZ box)
        {
            XYZ min = null;
            XYZ max = null;

            foreach (XYZ corner in Corners(box.Min, box.Max))
            {
                XYZ point = box.Transform.OfPoint(corner);
                Grow(point, ref min, ref max);
            }

            return Box(min, max);
        }

        /// <summary>
        /// The local box that covers a requested model box: all eight model corners through
        /// Transform.Inverse, then min/max per axis. The mirror of ModelBounds, and the reason a
        /// caller of views/set-crop never has to know the view's own coordinate system.
        /// </summary>
        private static void LocalBounds(
            Transform transform,
            XYZ requestedMin,
            XYZ requestedMax,
            out XYZ localMin,
            out XYZ localMax)
        {
            Transform inverse = transform.Inverse;

            XYZ min = null;
            XYZ max = null;

            foreach (XYZ corner in Corners(requestedMin, requestedMax))
            {
                XYZ point = inverse.OfPoint(corner);
                Grow(point, ref min, ref max);
            }

            localMin = min;
            localMax = max;
        }

        /// <summary>The eight corners of the box spanned by two opposite points.</summary>
        private static List<XYZ> Corners(XYZ min, XYZ max)
        {
            List<XYZ> corners = new List<XYZ>();

            double[] xs = new double[] { min.X, max.X };
            double[] ys = new double[] { min.Y, max.Y };
            double[] zs = new double[] { min.Z, max.Z };

            foreach (double x in xs)
            {
                foreach (double y in ys)
                {
                    foreach (double z in zs)
                    {
                        corners.Add(new XYZ(x, y, z));
                    }
                }
            }

            return corners;
        }

        private static void Grow(XYZ point, ref XYZ min, ref XYZ max)
        {
            if (min == null)
            {
                min = point;
                max = point;
                return;
            }

            min = new XYZ(
                Math.Min(min.X, point.X),
                Math.Min(min.Y, point.Y),
                Math.Min(min.Z, point.Z));

            max = new XYZ(
                Math.Max(max.X, point.X),
                Math.Max(max.Y, point.Y),
                Math.Max(max.Z, point.Z));
        }

        // --- hide helpers --------------------------------------------------------------------

        private static Dictionary<string, object> HideReport(
            Document document,
            View view,
            List<Element> elements,
            bool hidden,
            bool dryRun)
        {
            List<object> rows = new List<object>();

            foreach (Element element in elements)
            {
                rows.Add(new Dictionary<string, object>
                {
                    { "id", element.Id.Value },
                    { "name", RevitFacts.SafeName(element) },
                    { "category", RevitFacts.CategoryName(element) },
                    { "canBeHidden", element.CanBeHidden(view) },

                    // Read back off the view, never echoed from the request.
                    { "isHidden", element.IsHidden(view) },
                });
            }

            return new Dictionary<string, object>
            {
                { "dryRun", dryRun },
                { "applied", !dryRun },
                { "viewId", view.Id.Value },
                { "viewName", RevitFacts.SafeName(view) },
                { "viewType", view.ViewType.ToString() },
                { "hidden", hidden },
                { "elements", rows },
            };
        }

        // --- PDF helpers ---------------------------------------------------------------------

        /// <summary>
        /// The views a PDF export names, viewIds first and sheetIds after. Both lists are explicit
        /// and at least one is required; a sheet id in "viewIds" or a view id in "sheetIds" is a
        /// BAD_REQUEST rather than something that quietly works.
        /// </summary>
        private static List<View> ExportTargets(Document document, JsonElement body)
        {
            List<View> views = new List<View>();

            JsonElement value;

            if (JsonBody.TryGet(body, "viewIds", out value))
            {
                foreach (long viewId in JsonBody.RequireIds(body, "viewIds"))
                {
                    View view = RequireView(document, viewId);

                    if (view is ViewSheet)
                    {
                        throw BridgeException.BadRequest(
                            "View " + viewId + " (\"" + RevitFacts.SafeName(view) + "\") is a sheet. "
                                + "Pass sheets in \"sheetIds\", not \"viewIds\".");
                    }

                    views.Add(view);
                }
            }

            if (JsonBody.TryGet(body, "sheetIds", out value))
            {
                foreach (long sheetId in JsonBody.RequireIds(body, "sheetIds"))
                {
                    ViewSheet sheet = document.GetElement(new ElementId(sheetId)) as ViewSheet;

                    if (sheet == null)
                    {
                        throw BridgeException.BadRequest(
                            "Element " + sheetId + " is not a sheet in this document. Call "
                                + "/revit-mcp/sheets for the ones that are, and pass ordinary views "
                                + "in \"viewIds\".");
                    }

                    views.Add(sheet);
                }
            }

            if (views.Count == 0)
            {
                throw BridgeException.BadRequest(
                    "Pass \"viewIds\", \"sheetIds\", or both - each an array of ids. There is no "
                        + "shorthand for exporting every view in the document: a drawing set is "
                        + "named explicitly.");
            }

            return views;
        }

        /// <summary>
        /// The export folder, created if it is not there, and proved writable by writing a probe
        /// file into it. Absolute only: a relative path resolves against whatever directory Revit
        /// happens to have, which is not somewhere a caller can find a PDF afterwards.
        /// </summary>
        private static string RequireExportFolder(string folder)
        {
            string full;

            try
            {
                full = Path.GetFullPath(folder);
            }
            catch (Exception ex)
            {
                throw BridgeException.BadRequest(
                    "\"folder\" is not a usable path: " + ex.Message);
            }

            if (!Path.IsPathRooted(folder))
            {
                throw BridgeException.BadRequest(
                    "\"folder\" must be an absolute path such as C:\\Projects\\PDF; got \"" + folder
                        + "\", which resolved to \"" + full + "\" against Revit's own working "
                        + "directory.");
            }

            try
            {
                Directory.CreateDirectory(full);
            }
            catch (Exception ex)
            {
                throw BridgeException.BadRequest(
                    "\"folder\" \"" + full + "\" could not be created: " + ex.Message);
            }

            // Proved, not assumed: a folder that exists and cannot be written to fails here, with
            // the reason, rather than as an empty export nobody can explain.
            string probe = Path.Combine(full, "." + Guid.NewGuid().ToString("N") + ".revit-mcp");

            try
            {
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                throw new BridgeException(
                    409,
                    "FOLDER_NOT_WRITABLE",
                    "\"folder\" \"" + full + "\" exists but cannot be written to: " + ex.Message
                        + ". Nothing was exported.");
            }

            return full;
        }

        /// <summary>
        /// Exports one group of views to one PDF and reports the file that is actually on disk.
        ///
        /// Combine is true even for a single view: PDFExportOptions.FileName is only honoured when
        /// it is, and a known file name is what the overwrite check and the manifest are built on.
        /// </summary>
        private static Dictionary<string, object> ExportOnePdf(
            Document document,
            List<View> views,
            string folder,
            string name,
            string target)
        {
            List<ElementId> ids = new List<ElementId>();
            foreach (View view in views)
            {
                ids.Add(view.Id);
            }

            Dictionary<string, DateTime> before = SnapshotDirectory(folder);

            PDFExportOptions options = new PDFExportOptions();
            options.Combine = true;
            options.FileName = name;
            options.StopOnError = true;

            bool exported = document.Export(folder, ids, options);

            string written = target;
            if (!File.Exists(written))
            {
                // Revit wrote something else, or nothing. Either way the answer is what is on
                // disk, never what was asked for.
                written = FindWrittenFile(folder, before);
            }

            if (written == null || !File.Exists(written))
            {
                throw new BridgeException(
                    500,
                    "EXPORT_PRODUCED_NO_FILE",
                    "Revit's PDF export returned " + (exported ? "true" : "false") + " for "
                        + string.Join(", ", ViewLabels(views)) + " but no file appeared in \""
                        + folder + "\". Nothing is being reported as exported.");
            }

            FileInfo info = new FileInfo(written);
            int pages = PdfPageCount(written);

            List<object> rows = new List<object>();
            for (int index = 0; index < views.Count; index++)
            {
                View view = views[index];
                ViewSheet sheet = view as ViewSheet;

                rows.Add(new Dictionary<string, object>
                {
                    { "viewId", view.Id.Value },
                    { "viewName", RevitFacts.SafeName(view) },
                    { "viewType", view.ViewType.ToString() },
                    { "sheetNumber", sheet == null ? null : sheet.SheetNumber },
                    { "page", index + 1 },
                });
            }

            return new Dictionary<string, object>
            {
                { "path", written },
                { "requestedPath", target },
                { "bytes", info.Length },
                { "pages", pages > 0 ? (object)pages : null },
                { "pagesMeasured", pages > 0 },
                { "views", rows },
            };
        }

        private static List<string> ViewLabels(List<View> views)
        {
            List<string> labels = new List<string>();

            foreach (View view in views)
            {
                labels.Add("\"" + RevitFacts.SafeName(view) + "\" (" + view.Id.Value + ")");
            }

            return labels;
        }

        /// <summary>The file name stem one view gets when the set is not combined.</summary>
        private static string FileNameFor(View view, string stem)
        {
            ViewSheet sheet = view as ViewSheet;

            string label = sheet == null
                ? RevitFacts.SafeName(view)
                : sheet.SheetNumber + " - " + RevitFacts.SafeName(sheet);

            string name = SanitizeFileName(stem == null ? label : stem + " - " + label);

            // A view whose name is nothing but characters Windows refuses still has an id.
            return name == null ? "view-" + view.Id.Value : name;
        }

        private static string UniqueName(HashSet<string> taken, string name)
        {
            string candidate = name;
            int suffix = 2;

            while (taken.Contains(candidate))
            {
                candidate = name + " " + suffix;
                suffix++;
            }

            taken.Add(candidate);
            return candidate;
        }

        /// <summary>
        /// A file name stem Windows will accept: invalid characters become "-", a trailing ".pdf"
        /// is dropped so Revit does not write "x.pdf.pdf", and the result is capped. Null when
        /// nothing usable is left.
        /// </summary>
        private static string SanitizeFileName(string name)
        {
            if (name == null)
            {
                return null;
            }

            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder builder = new StringBuilder();

            foreach (char character in name)
            {
                builder.Append(Array.IndexOf(invalid, character) >= 0 ? '-' : character);
            }

            string cleaned = builder.ToString().Trim();

            if (cleaned.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Substring(0, cleaned.Length - 4).Trim();
            }

            cleaned = cleaned.Trim('.').Trim();

            if (cleaned.Length > MaxFileNameLength)
            {
                cleaned = cleaned.Substring(0, MaxFileNameLength).Trim();
            }

            return cleaned.Length == 0 ? null : cleaned;
        }

        private static Dictionary<string, DateTime> SnapshotDirectory(string directory)
        {
            Dictionary<string, DateTime> files =
                new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            foreach (string file in Directory.GetFiles(directory))
            {
                files[file] = File.GetLastWriteTimeUtc(file);
            }

            return files;
        }

        /// <summary>The newest PDF that appeared, or changed, in the folder during one export.</summary>
        private static string FindWrittenFile(string directory, Dictionary<string, DateTime> before)
        {
            string best = null;
            DateTime bestWritten = DateTime.MinValue;

            foreach (string file in Directory.GetFiles(directory, "*.pdf"))
            {
                DateTime previous;
                DateTime written = File.GetLastWriteTimeUtc(file);

                if (before.TryGetValue(file, out previous) && previous == written)
                {
                    continue;
                }

                if (best != null && written <= bestWritten)
                {
                    continue;
                }

                best = file;
                bestWritten = written;
            }

            return best;
        }

        /// <summary>
        /// Pages counted out of the PDF's own page objects, or 0 when they cannot be seen - a PDF
        /// that keeps its objects in compressed streams gives nothing to count, and 0 means "not
        /// measured" rather than "empty". Never derived from the number of views exported.
        /// </summary>
        private static int PdfPageCount(string path)
        {
            try
            {
                FileInfo info = new FileInfo(path);
                if (info.Length > MaxPdfScanBytes)
                {
                    return 0;
                }

                // Latin1 maps every byte to one char, so offsets in the text are offsets in the
                // file and no byte is lost to a decoder.
                string text = Encoding.Latin1.GetString(File.ReadAllBytes(path));

                int count = 0;
                int index = 0;

                while (true)
                {
                    index = text.IndexOf("/Type", index, StringComparison.Ordinal);
                    if (index < 0)
                    {
                        break;
                    }

                    int cursor = index + 5;
                    while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
                    {
                        cursor++;
                    }

                    if (cursor + 5 <= text.Length
                        && string.CompareOrdinal(text, cursor, "/Page", 0, 5) == 0)
                    {
                        // "/Pages" is the page TREE, not a page.
                        char next = cursor + 5 < text.Length ? text[cursor + 5] : ' ';
                        if (next != 's')
                        {
                            count++;
                        }
                    }

                    index += 5;
                }

                return count;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // --- schedule helpers ----------------------------------------------------------------

        /// <summary>
        /// The schedule's fields in display order, with both names: GetName is the field, and
        /// ColumnHeading is the text actually printed at the top of the column.
        /// </summary>
        private static List<object> ScheduleColumns(ViewSchedule schedule)
        {
            List<object> columns = new List<object>();

            ScheduleDefinition definition = schedule.Definition;
            IList<ScheduleFieldId> order = definition.GetFieldOrder();

            for (int index = 0; index < order.Count; index++)
            {
                ScheduleField field = definition.GetField(order[index]);
                if (field == null)
                {
                    continue;
                }

                columns.Add(ScheduleColumn(definition, field, index));
            }

            return columns;
        }

        /// <summary>
        /// One field, described so that schedules/configure can be called without guessing.
        /// "fieldId" is the stable ScheduleFieldId, which is what to address a field by - "index"
        /// is its position in the current field order and moves when fields are reordered.
        /// "canTotal" and "canSortGroup" are Revit's own answers, not inferred from the type: a
        /// Count field, for one, can never be grouped by.
        /// </summary>
        private static Dictionary<string, object> ScheduleColumn(
            ScheduleDefinition definition,
            ScheduleField field,
            int index)
        {
            return new Dictionary<string, object>
            {
                { "index", index },
                { "fieldId", field.FieldId.IntegerValue },
                { "name", SafeFieldName(field) },
                { "heading", field.ColumnHeading },
                { "isHidden", field.IsHidden },
                { "fieldType", field.FieldType.ToString() },
                { "displayType", field.DisplayType.ToString() },
                { "canTotal", field.CanTotal() },
                { "canSortGroup", SafeCanSortGroup(definition, field) },
                { "gridColumnWidth", field.GridColumnWidth },
                { "sheetColumnWidth", field.SheetColumnWidth },
            };
        }

        private static object SafeCanSortGroup(ScheduleDefinition definition, ScheduleField field)
        {
            try
            {
                return definition.CanSortByField(field.FieldId);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string SafeFieldName(ScheduleField field)
        {
            try
            {
                return field.GetName();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The grid of one section, read through the VIEW rather than the section data.
        ///
        /// TableSectionData.GetCellText is the wrong reader for a schedule and fails silently.
        /// Its own documentation says so: it "returns the text shown by this cell, IF the cell's
        /// type is CellType.Text or CellType.ParameterText or CellType.CustomField", returns "an
        /// empty string if the type is not" one of those, and refers the caller to TableView for
        /// "the formatted text of the cell regardless of cell type". Measured on this model:
        /// schedule 212388 gave 142 body rows whose Count column read "1" and whose Family and
        /// Type column read "" for every single row - a cell type that reader cannot render,
        /// reported as if the parameter were empty. An empty string that means "I could not read
        /// this" is worse than an error, because it reads as data.
        ///
        /// So TableView.GetCellText(sectionType, row, column) is the reader, and the section data
        /// is kept only for the row and column counts. The fallback exists because TableView's
        /// contract is "standard view schedules": it throws when the section type is not valid for
        /// the view, and an older reading is better than nothing. A cell neither can read stays
        /// null, so "blank" and "not readable" remain different answers.
        /// </summary>
        private static Dictionary<string, object> ReadSection(
            TableView view,
            SectionType sectionType,
            TableSectionData section,
            int offset,
            int limit)
        {
            if (section == null)
            {
                return null;
            }

            int totalRows = section.NumberOfRows;
            int totalColumns = section.NumberOfColumns;

            int columns = Math.Min(totalColumns, MaxScheduleColumns);
            int first = Math.Min(offset, totalRows);
            int last = Math.Min(first + limit, totalRows);

            List<object> cells = new List<object>();

            for (int row = first; row < last; row++)
            {
                List<object> line = new List<object>();

                for (int column = 0; column < columns; column++)
                {
                    line.Add(CellText(view, sectionType, section, row, column));
                }

                cells.Add(line);
            }

            return new Dictionary<string, object>
            {
                { "totalRows", totalRows },
                { "totalColumns", totalColumns },
                { "offset", first },
                { "returnedRows", last - first },
                { "returnedColumns", columns },
                { "truncatedRows", last < totalRows },
                { "truncatedColumns", columns < totalColumns },
                { "cells", cells },
            };
        }

        /// <summary>
        /// One cell's text: the view first, the section data only if the view refuses the section
        /// type, and null if neither will read it.
        /// </summary>
        private static string CellText(
            TableView view,
            SectionType sectionType,
            TableSectionData section,
            int row,
            int column)
        {
            try
            {
                return view.GetCellText(sectionType, row, column);
            }
            catch (Exception)
            {
                // Not a standard view schedule, or a section type this view does not have.
            }

            try
            {
                return section.GetCellText(row, column);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // --- sheet layout helpers ------------------------------------------------------------

        /// <summary>
        /// One schedule placed on a sheet. Anchored by its TOP-LEFT corner, which is Revit's own
        /// convention for a ScheduleSheetInstance and the reason a viewport tool cannot move one.
        /// </summary>
        private static Dictionary<string, object> ReadScheduleInstance(
            Document document,
            ScheduleSheetInstance instance,
            ViewSheet sheet)
        {
            Element scheduleView = document.GetElement(instance.ScheduleId);

            return new Dictionary<string, object>
            {
                { "id", instance.Id.Value },
                { "scheduleId", instance.ScheduleId.Value },
                { "scheduleName", scheduleView == null ? null : RevitFacts.SafeName(scheduleView) },
                { "segmentIndex", instance.SegmentIndex },
                { "isTitleblockRevisionSchedule", instance.IsTitleblockRevisionSchedule },

                // Top-left, not centre. Revit's own convention for a schedule on a sheet.
                { "topLeft", Point2(instance.Point) },
                { "bounds", SheetBounds(instance, sheet) },
            };
        }

        private static Dictionary<string, object> ReadViewport(Document document, Viewport viewport)
        {
            View view = document.GetElement(viewport.ViewId) as View;

            Dictionary<string, object> row = new Dictionary<string, object>
            {
                { "id", viewport.Id.Value },
                { "sheetId", viewport.SheetId.Value },
                { "viewId", viewport.ViewId.Value },
                { "viewName", view == null ? null : RevitFacts.SafeName(view) },
                { "viewType", view == null ? null : view.ViewType.ToString() },
                { "scale", view == null ? null : (object)SafeScale(view) },
                { "rotation", viewport.Rotation.ToString() },
                { "center", null },
                { "bounds", null },
                { "labelBounds", null },
                { "labelOffset", null },
                { "labelLineLength", null },
            };

            try
            {
                row["center"] = Point2(viewport.GetBoxCenter());
            }
            catch (Exception)
            {
                // A viewport on a placeholder sheet has no box; null says so.
            }

            row["bounds"] = SafeOutline(viewport, true);
            row["labelBounds"] = SafeOutline(viewport, false);

            try
            {
                row["labelOffset"] = Point2(viewport.LabelOffset);
                row["labelLineLength"] = viewport.LabelLineLength;
            }
            catch (Exception)
            {
                // Same: nothing to report rather than a fabricated zero.
            }

            return row;
        }

        private static object SafeScale(View view)
        {
            try
            {
                return view.Scale;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Dictionary<string, object> SafeOutline(Viewport viewport, bool box)
        {
            try
            {
                Outline outline = box ? viewport.GetBoxOutline() : viewport.GetLabelOutline();
                if (outline == null)
                {
                    return null;
                }

                return Bounds2(outline.MinimumPoint, outline.MaximumPoint);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The element's bounding box in the sheet's own paper coordinates, or null.</summary>
        private static Dictionary<string, object> SheetBounds(Element element, ViewSheet sheet)
        {
            try
            {
                BoundingBoxXYZ box = element.get_BoundingBox(sheet);
                if (box == null)
                {
                    return null;
                }

                return Bounds2(box.Min, box.Max);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// One browser organization scheme: its id, the name shown in the Revit UI, and the
        /// sorting it applies. Every property here is get-only - see BrowserOrganization.
        /// </summary>
        private static Dictionary<string, object> DescribeOrganization(
            Document document,
            Autodesk.Revit.DB.BrowserOrganization organization)
        {
            Dictionary<string, object> row = new Dictionary<string, object>
            {
                { "id", organization.Id.Value },
                { "name", RevitFacts.SafeName(organization) },
                { "type", null },
                { "sortingOrder", null },
                { "sortingParameterId", null },
                { "sortingParameterName", null },
            };

            try
            {
                row["type"] = organization.Type.ToString();
            }
            catch (Exception)
            {
                // Type arrived in 2023; older schemes in an upgraded file can refuse it.
            }

            try
            {
                row["sortingOrder"] = organization.SortingOrder.ToString();

                ElementId parameterId = organization.SortingParameterId;
                if (parameterId != null && parameterId != ElementId.InvalidElementId)
                {
                    row["sortingParameterId"] = parameterId.Value;
                    row["sortingParameterName"] = ParameterName(document, parameterId);
                }
            }
            catch (Exception)
            {
                // Nothing to report rather than a fabricated default.
            }

            return row;
        }

        /// <summary>
        /// The chain of browser folders one sheet actually sits in, outermost first, each with the
        /// parameter that produced it. GetFolderItems is the only window onto the scheme's
        /// grouping levels - the scheme will not describe itself.
        /// </summary>
        private static List<object> FolderChain(
            Document document,
            Autodesk.Revit.DB.BrowserOrganization organization,
            ElementId sheetId)
        {
            List<object> folders = new List<object>();

            if (organization == null)
            {
                return folders;
            }

            try
            {
                foreach (FolderItemInfo info in organization.GetFolderItems(sheetId))
                {
                    folders.Add(new Dictionary<string, object>
                    {
                        { "name", info.Name },

                        // The FOLDER PARAMETER's id, not the folder's - Revit's own naming.
                        { "parameterId", info.ElementId == null ? null : (object)info.ElementId.Value },
                        { "parameterName", ParameterName(document, info.ElementId) },
                    });
                }
            }
            catch (Exception)
            {
                // A sheet the scheme's filters exclude has no folder chain.
            }

            return folders;
        }

        /// <summary>The grouping levels a folder chain implies: the parameters, in order.</summary>
        private static List<object> ParameterChain(List<object> folders)
        {
            List<object> levels = new List<object>();

            for (int index = 0; index < folders.Count; index++)
            {
                Dictionary<string, object> folder = folders[index] as Dictionary<string, object>;
                if (folder == null)
                {
                    continue;
                }

                levels.Add(new Dictionary<string, object>
                {
                    { "level", index + 1 },
                    { "parameterId", folder["parameterId"] },
                    { "parameterName", folder["parameterName"] },
                    { "sampleFolderName", folder["name"] },
                });
            }

            return levels;
        }

        /// <summary>
        /// A parameter id's display name. A negative id is a BuiltInParameter, whose label is
        /// localised - which is why it is asked for rather than spelled out.
        /// </summary>
        private static string ParameterName(Document document, ElementId parameterId)
        {
            if (parameterId == null || parameterId == ElementId.InvalidElementId)
            {
                return null;
            }

            try
            {
                ParameterElement parameter = document.GetElement(parameterId) as ParameterElement;
                if (parameter != null)
                {
                    return parameter.GetDefinition().Name;
                }
            }
            catch (Exception)
            {
                // Fall through to the built-in label.
            }

            try
            {
                return LabelUtils.GetLabelFor((BuiltInParameter)parameterId.Value);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The sheet's own outline: View.Outline, a BoundingBoxUV in feet on the paper.</summary>
        private static Dictionary<string, object> OutlineUV(ViewSheet sheet)
        {
            try
            {
                BoundingBoxUV outline = sheet.Outline;
                if (outline == null)
                {
                    return null;
                }

                return new Dictionary<string, object>
                {
                    { "min", new Dictionary<string, object> { { "x", outline.Min.U }, { "y", outline.Min.V } } },
                    { "max", new Dictionary<string, object> { { "x", outline.Max.U }, { "y", outline.Max.V } } },
                    { "width", outline.Max.U - outline.Min.U },
                    { "height", outline.Max.V - outline.Min.V },
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        // --- shared shapes -------------------------------------------------------------------

        private static Dictionary<string, object> Box(XYZ min, XYZ max)
        {
            if (min == null || max == null)
            {
                return null;
            }

            return new Dictionary<string, object>
            {
                { "min", Point3(min) },
                { "max", Point3(max) },
            };
        }

        private static Dictionary<string, object> Bounds2(XYZ min, XYZ max)
        {
            return new Dictionary<string, object>
            {
                { "min", Point2(min) },
                { "max", Point2(max) },
                { "width", max.X - min.X },
                { "height", max.Y - min.Y },
            };
        }

        private static Dictionary<string, object> Point3(XYZ point)
        {
            return new Dictionary<string, object>
            {
                { "x", point.X },
                { "y", point.Y },
                { "z", point.Z },
            };
        }

        /// <summary>Sheet points are two-dimensional; z on the paper is always 0 and noise.</summary>
        private static Dictionary<string, object> Point2(XYZ point)
        {
            if (point == null)
            {
                return null;
            }

            return new Dictionary<string, object>
            {
                { "x", point.X },
                { "y", point.Y },
            };
        }

        private static string Number(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        // --- request helpers -----------------------------------------------------------------

        /// <summary>
        /// The view ids a request names: {"viewIds": [...]} for a batch, {"viewId": n} for one.
        /// The same pair views/set-scale and views/export-image take, so there is no second
        /// convention to learn.
        /// </summary>
        private static List<long> ViewIds(JsonElement body)
        {
            JsonElement batch;
            if (JsonBody.TryGet(body, "viewIds", out batch))
            {
                return JsonBody.RequireIds(body, "viewIds");
            }

            List<long> ids = new List<long>();
            ids.Add(JsonBody.AsLong(RequireValue(body, "viewId"), "viewId"));
            return ids;
        }

        private static View RequireView(Document document, long viewId)
        {
            View view = document.GetElement(new ElementId(viewId)) as View;
            if (view == null)
            {
                throw BridgeException.BadRequest(
                    "Element " + viewId + " is not a view in this document. Call /revit-mcp/views "
                        + "for the ones that are.");
            }

            return view;
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
    }
}
