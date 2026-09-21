using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcpBridge.Endpoints
{
    /// <summary>
    /// Project parameters: creating one and binding it to categories, and writing a different
    /// value per element.
    ///
    /// A project parameter in Revit is a shared parameter definition plus a binding, and the
    /// definition lives in a text file outside the model - Application.SharedParametersFilename.
    /// That file is a user-wide Revit setting, so this endpoint treats it as borrowed: when it is
    /// unset the bridge points Revit at one of its own in the temp folder for the length of the
    /// call and puts the original value back on the way out, in a finally.
    ///
    /// Same write rules as everywhere else: one request is one TransactionGroup and therefore one
    /// Ctrl+Z, with one inner transaction per element where a per-element failure has to be
    /// survivable.
    /// </summary>
    internal static class ParameterEndpoints
    {
        /// <summary>The group the bridge's own definitions go in, inside the shared parameter file.</summary>
        private const string DefinitionGroupName = "MCP";

        /// <summary>
        /// Stable on purpose: the same file across calls means the same GUID for a parameter of
        /// the same name, so re-running a call does not mint a second definition.
        /// </summary>
        private const string SharedParameterFileName = "revit-mcp-shared-parameters.txt";

        /// <summary>
        /// Body: {name, category | categories: [...], type?, group?, instance?}.
        ///
        /// Creates a shared parameter and binds it to those categories, which is what Revit calls a
        /// project parameter. "type" defaults to Text and "instance" to true; "group" is the
        /// parameter group it appears under in the properties palette, Identity Data by default.
        ///
        /// A parameter of that name already bound in this document is not an error: nothing is
        /// created, "created" is false and "alreadyExisted" is true, and the categories reported
        /// are the ones it is actually bound to. Re-running the same call is therefore safe.
        ///
        /// Both halves need a transaction (the binding) and a file (the definition), and they fail
        /// differently: a document whose Revit has no usable shared parameter file answers
        /// NO_SHARED_PARAMETER_FILE rather than a null reference out of OpenSharedParameterFile.
        /// </summary>
        internal static object CreateProjectParameter(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);

            string name = JsonBody.RequireString(body, "name");
            ForgeTypeId dataType = ResolveDataType(JsonBody.OptionalString(body, "type"));
            ForgeTypeId groupTypeId = ResolveGroupTypeId(JsonBody.OptionalString(body, "group"));
            bool instance = JsonBody.OptionalBool(body, "instance", true);

            List<Category> categories = ResolveCategories(document, body);

            // Read before anything is created: a second definition with a name the document
            // already binds is how a project ends up with two "Phase" columns.
            Definition bound;
            ElementBinding existing = FindBinding(document, name, out bound);

            if (existing != null)
            {
                return new Dictionary<string, object>
                {
                    { "name", bound.Name },
                    { "guid", SharedParameterGuid(document, name) },
                    { "categories", CategoryNames(existing.Categories) },
                    { "created", false },
                    { "alreadyExisted", true },
                };
            }

            Autodesk.Revit.ApplicationServices.Application application = document.Application;

            CategorySet categorySet = application.Create.NewCategorySet();
            List<object> requested = new List<object>();

            foreach (Category category in categories)
            {
                categorySet.Insert(category);
                requested.Add(category.Name);
            }

            string originalFile = application.SharedParametersFilename;
            bool borrowed = false;

            try
            {
                if (string.IsNullOrWhiteSpace(originalFile) || !File.Exists(originalFile))
                {
                    application.SharedParametersFilename = BridgeSharedParameterFile();
                    borrowed = true;
                }

                DefinitionFile file = application.OpenSharedParameterFile();
                if (file == null)
                {
                    throw new BridgeException(
                        409,
                        "NO_SHARED_PARAMETER_FILE",
                        "Revit could not open a shared parameter file, so no project parameter can "
                            + "be defined. The bridge points Revit at "
                            + application.SharedParametersFilename + " when none is set; check that "
                            + "path is writable.");
                }

                ExternalDefinition definition = FindOrCreateDefinition(file, name, dataType);

                return RevitWrite.InGroup(document, "MCP: create project parameter", delegate
                {
                    RevitWrite.InTransaction(document, "Create project parameter", delegate
                    {
                        Binding binding = instance
                            ? (Binding)application.Create.NewInstanceBinding(categorySet)
                            : (Binding)application.Create.NewTypeBinding(categorySet);

                        if (!document.ParameterBindings.Insert(definition, binding, groupTypeId))
                        {
                            throw new BridgeException(
                                409,
                                "PARAMETER_NOT_BOUND",
                                "Revit refused to bind parameter \"" + name + "\" to the requested "
                                    + "categories. A built-in parameter of that name, or a category "
                                    + "that cannot carry one, is the usual cause.");
                        }
                    });

                    return new Dictionary<string, object>
                    {
                        { "name", name },
                        { "guid", definition.GUID.ToString() },
                        { "categories", requested },
                        { "instance", instance },
                        { "created", true },
                        { "alreadyExisted", false },
                    };
                });
            }
            finally
            {
                if (borrowed)
                {
                    try
                    {
                        application.SharedParametersFilename = originalFile == null ? string.Empty : originalFile;
                    }
                    catch (Exception ex)
                    {
                        // The user's Revit setting is a courtesy to restore, not the job: a failure
                        // here must not turn a parameter that was created into an error, and it
                        // must not replace a real exception on the way out of the finally.
                        BridgeLog.Error("Could not restore Application.SharedParametersFilename", ex);
                    }
                }
            }
        }

        /// <summary>
        /// Body: {values: [{sheetId, name, value}]}.
        ///
        /// Deliberately not the same endpoint as parameters/set, which writes ONE name and ONE
        /// value across a list of ids all-or-nothing. This writes a DIFFERENT value per sheet -
        /// which is what putting a phase on 56 sheets is - in one TransactionGroup, with one inner
        /// transaction per sheet so a sheet that has no such parameter lands in "failed" while the
        /// other 55 are written.
        ///
        /// Instance parameters only, exactly as parameters/set: writing to the type would change
        /// every other sheet using it.
        /// </summary>
        internal static object SetSheetParameters(UIApplication app, JsonElement body)
        {
            Document document = RevitFacts.RequireProjectDocument(app);
            JsonElement values = JsonBody.RequireArray(body, "values");

            List<JsonElement> requests = new List<JsonElement>();
            foreach (JsonElement request in values.EnumerateArray())
            {
                requests.Add(request);
            }

            if (requests.Count == 0)
            {
                throw BridgeException.BadRequest(
                    "\"values\" must contain at least one {sheetId, name, value}.");
            }

            return RevitWrite.InGroup(document, "MCP: set sheet parameters", delegate
            {
                List<object> updated = new List<object>();
                List<object> failed = new List<object>();

                foreach (JsonElement request in requests)
                {
                    // Read outside the try: a malformed entry is a BAD_REQUEST for the whole call,
                    // not a sheet Revit refused.
                    long sheetId = JsonBody.AsLong(RequireValue(request, "sheetId"), "sheetId");
                    string name = JsonBody.RequireString(request, "name");
                    JsonElement value = RequireValue(request, "value");

                    try
                    {
                        updated.Add(SetOne(document, sheetId, name, value));
                    }
                    catch (BridgeException ex)
                    {
                        failed.Add(new Dictionary<string, object>
                        {
                            { "sheetId", sheetId },
                            { "name", name },
                            { "code", ex.Code },
                            { "reason", ex.Message },
                        });
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                    {
                        failed.Add(new Dictionary<string, object>
                        {
                            { "sheetId", sheetId },
                            { "name", name },
                            { "code", "REVIT_API_ERROR" },
                            { "reason", ex.Message },
                        });
                    }
                }

                return new Dictionary<string, object>
                {
                    { "updated", updated },
                    { "failed", failed },
                };
            });
        }

        /// <summary>One sheet, in its own transaction so a refusal rolls back alone.</summary>
        private static Dictionary<string, object> SetOne(
            Document document,
            long sheetId,
            string name,
            JsonElement value)
        {
            ViewSheet sheet = document.GetElement(new ElementId(sheetId)) as ViewSheet;
            if (sheet == null)
            {
                throw BridgeException.BadRequest(
                    "Element " + sheetId + " is not a sheet in this document. Call "
                        + "/revit-mcp/sheets for the ones that are.");
            }

            Parameter parameter = sheet.LookupParameter(name);

            if (parameter == null)
            {
                throw BridgeException.NotFound(
                    "PARAMETER_NOT_FOUND",
                    "Sheet " + sheet.SheetNumber + " (" + sheetId + ") has no instance parameter "
                        + "named \"" + name + "\". Create one with "
                        + "/revit-mcp/parameters/create-project bound to Sheets.");
            }

            if (parameter.IsReadOnly)
            {
                throw BridgeException.BadRequest(
                    "Parameter \"" + name + "\" is read-only on sheet " + sheetId + ".");
            }

            RevitWrite.InTransaction(document, "Set " + name, delegate
            {
                WriteEndpoints.ApplyValue(parameter, value, name, sheetId);
            });

            return new Dictionary<string, object>
            {
                { "sheetId", sheetId },
                { "number", sheet.SheetNumber },
                { "name", name },
                { "value", RevitFacts.RawValue(parameter) },
                { "display", RevitFacts.SafeValueString(parameter) },
            };
        }

        /// <summary>Reads "categories" as a list, or "category" as one. At least one is required.</summary>
        private static List<Category> ResolveCategories(Document document, JsonElement body)
        {
            List<string> names = JsonBody.OptionalStringList(body, "categories");

            if (names == null)
            {
                names = new List<string>();
                names.Add(JsonBody.RequireString(body, "category"));
            }

            if (names.Count == 0)
            {
                throw BridgeException.BadRequest(
                    "\"categories\" must contain at least one category name.");
            }

            List<Category> categories = new List<Category>();

            foreach (string name in names)
            {
                Category category = RevitFacts.ResolveCategory(document, name);

                if (category == null)
                {
                    throw BridgeException.BadRequest(
                        "Unknown category \"" + name + "\". Call /revit-mcp/categories for the "
                            + "categories present in this document, or pass a BuiltInCategory name "
                            + "such as OST_Sheets.");
                }

                if (!category.AllowsBoundParameters)
                {
                    throw BridgeException.BadRequest(
                        "Category \"" + category.Name + "\" does not allow bound parameters, so no "
                            + "project parameter can be attached to it.");
                }

                categories.Add(category);
            }

            return categories;
        }

        /// <summary>
        /// The definition and binding already in this document under that name, or null. The
        /// iterator rather than the indexer: the caller has a name, not a Definition object.
        /// </summary>
        private static ElementBinding FindBinding(Document document, string name, out Definition definition)
        {
            DefinitionBindingMapIterator iterator = document.ParameterBindings.ForwardIterator();

            while (iterator.MoveNext())
            {
                Definition candidate = iterator.Key;

                if (candidate == null || !string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                definition = candidate;
                return iterator.Current as ElementBinding;
            }

            definition = null;
            return null;
        }

        /// <summary>
        /// The GUID of a shared parameter already in the document. A bound shared parameter
        /// surfaces as an InternalDefinition, which carries no GUID, so it is read off the
        /// SharedParameterElement instead. Null when the parameter is not a shared one.
        /// </summary>
        private static string SharedParameterGuid(Document document, string name)
        {
            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(SharedParameterElement)))
            {
                SharedParameterElement shared = element as SharedParameterElement;

                if (shared != null && string.Equals(RevitFacts.SafeName(shared), name, StringComparison.OrdinalIgnoreCase))
                {
                    return shared.GuidValue.ToString();
                }
            }

            return null;
        }

        private static List<object> CategoryNames(CategorySet categories)
        {
            List<object> names = new List<object>();

            if (categories != null)
            {
                foreach (Category category in categories)
                {
                    names.Add(category.Name);
                }
            }

            return names;
        }

        /// <summary>
        /// The definition of that name anywhere in the shared parameter file, creating it in the
        /// bridge's own group when the file has none. Reused rather than re-created so the GUID
        /// stays the same across calls - a second definition with the same name is a different
        /// parameter as far as Revit is concerned.
        /// </summary>
        private static ExternalDefinition FindOrCreateDefinition(
            DefinitionFile file,
            string name,
            ForgeTypeId dataType)
        {
            DefinitionGroup target = null;

            foreach (DefinitionGroup group in file.Groups)
            {
                foreach (Definition definition in group.Definitions)
                {
                    ExternalDefinition external = definition as ExternalDefinition;

                    if (external != null && string.Equals(external.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return external;
                    }
                }

                if (string.Equals(group.Name, DefinitionGroupName, StringComparison.OrdinalIgnoreCase))
                {
                    target = group;
                }
            }

            if (target == null)
            {
                target = file.Groups.Create(DefinitionGroupName);
            }

            ExternalDefinitionCreationOptions options =
                new ExternalDefinitionCreationOptions(name, dataType);

            return (ExternalDefinition)target.Definitions.Create(options);
        }

        /// <summary>
        /// The bridge's own shared parameter file, created empty when it does not exist. Revit
        /// writes the file's header itself the first time a group is added to it, which is why an
        /// empty file is enough and no format is hand-written here.
        /// </summary>
        private static string BridgeSharedParameterFile()
        {
            string path = Path.Combine(Path.GetTempPath(), SharedParameterFileName);

            if (!File.Exists(path))
            {
                using (File.Create(path))
                {
                }
            }

            return path;
        }

        /// <summary>The parameter's data type. Text unless the caller says otherwise.</summary>
        private static ForgeTypeId ResolveDataType(string name)
        {
            if (name == null)
            {
                return SpecTypeId.String.Text;
            }

            switch (name.Trim().ToLowerInvariant())
            {
                case "text":
                    return SpecTypeId.String.Text;
                case "multilinetext":
                    return SpecTypeId.String.MultilineText;
                case "url":
                    return SpecTypeId.String.Url;
                case "integer":
                    return SpecTypeId.Int.Integer;
                case "number":
                    return SpecTypeId.Number;
                case "yesno":
                    return SpecTypeId.Boolean.YesNo;
                case "length":
                    return SpecTypeId.Length;
                case "area":
                    return SpecTypeId.Area;
                case "volume":
                    return SpecTypeId.Volume;
                case "angle":
                    return SpecTypeId.Angle;
            }

            throw BridgeException.BadRequest(
                "Unknown parameter type \"" + name + "\". Use Text (the default), MultilineText, "
                    + "Url, Integer, Number, YesNo, Length, Area, Volume or Angle.");
        }

        /// <summary>The group the parameter appears under in Revit's properties palette.</summary>
        private static ForgeTypeId ResolveGroupTypeId(string name)
        {
            if (name == null)
            {
                return GroupTypeId.IdentityData;
            }

            switch (name.Trim().ToLowerInvariant())
            {
                case "identitydata":
                    return GroupTypeId.IdentityData;
                case "text":
                    return GroupTypeId.Text;
                case "data":
                    return GroupTypeId.Data;
                case "general":
                    return GroupTypeId.General;
                case "graphics":
                    return GroupTypeId.Graphics;
                case "constraints":
                    return GroupTypeId.Constraints;
                case "geometry":
                    return GroupTypeId.Geometry;
                case "phasing":
                    return GroupTypeId.Phasing;
                case "title":
                    return GroupTypeId.Title;
            }

            throw BridgeException.BadRequest(
                "Unknown parameter group \"" + name + "\". Use IdentityData (the default), Text, "
                    + "Data, General, Graphics, Constraints, Geometry, Phasing or Title.");
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
