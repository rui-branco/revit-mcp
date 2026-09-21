using System;
using System.Collections.Generic;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcpBridge.Endpoints
{
    /// <summary>
    /// Shared read-side helpers: document access, name/id lookups and the "compact row" shape the
    /// query-style endpoints return.
    ///
    /// All lengths crossing the wire are Revit internal units (decimal feet), unconverted in both
    /// directions. The Node side documents feet and converts nothing, so neither does this.
    /// </summary>
    internal static class RevitFacts
    {
        internal static UIDocument RequireUiDocument(UIApplication app)
        {
            UIDocument uiDocument = app.ActiveUIDocument;
            if (uiDocument == null || uiDocument.Document == null)
            {
                throw BridgeException.NoActiveDocument();
            }

            return uiDocument;
        }

        internal static Document RequireDocument(UIApplication app)
        {
            return RequireUiDocument(app).Document;
        }

        internal static Document RequireProjectDocument(UIApplication app)
        {
            Document document = RequireDocument(app);
            if (document.IsFamilyDocument)
            {
                throw BridgeException.NotAProjectDocument();
            }

            return document;
        }

        /// <summary>Element.Name throws for a handful of element kinds; nobody wants that here.</summary>
        internal static string SafeName(Element element)
        {
            try
            {
                return element.Name;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static string CategoryName(Element element)
        {
            Category category = element.Category;
            if (category == null)
            {
                return null;
            }

            return category.Name;
        }

        internal static string TypeName(Document document, Element element)
        {
            ElementId typeId = element.GetTypeId();
            if (typeId == null || typeId == ElementId.InvalidElementId)
            {
                return null;
            }

            Element type = document.GetElement(typeId);
            if (type == null)
            {
                return null;
            }

            return SafeName(type);
        }

        internal static string LevelName(Document document, Element element)
        {
            ElementId levelId = element.LevelId;
            if (levelId == null || levelId == ElementId.InvalidElementId)
            {
                return null;
            }

            Level level = document.GetElement(levelId) as Level;
            if (level == null)
            {
                return null;
            }

            return level.Name;
        }

        /// <summary>The compact row shape shared by query / elements / selection.</summary>
        internal static Dictionary<string, object> CompactRow(Document document, Element element)
        {
            return new Dictionary<string, object>
            {
                // ElementId.Value (Int64), never the deprecated IntegerValue.
                { "id", element.Id.Value },
                { "name", SafeName(element) },
                { "category", CategoryName(element) },
                { "typeName", TypeName(document, element) },
                { "level", LevelName(document, element) },
            };
        }

        internal static Element RequireElement(Document document, long id)
        {
            Element element = document.GetElement(new ElementId(id));
            if (element == null)
            {
                throw BridgeException.NotFound(
                    "ELEMENT_NOT_FOUND",
                    "No element with id " + id + " exists in " + document.Title + ".");
            }

            return element;
        }

        /// <summary>Matches on the display name first, then on the BuiltInCategory enum name.</summary>
        internal static Category ResolveCategory(Document document, string name)
        {
            foreach (Category category in document.Settings.Categories)
            {
                if (string.Equals(category.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return category;
                }
            }

            BuiltInCategory builtIn;
            if (Enum.TryParse<BuiltInCategory>(name, true, out builtIn))
            {
                try
                {
                    Category category = Category.GetCategory(document, builtIn);
                    if (category != null)
                    {
                        return category;
                    }
                }
                catch (Exception)
                {
                    // Not every BuiltInCategory exists in every document; fall through to null.
                }
            }

            return null;
        }

        internal static Level ResolveLevel(Document document, string name)
        {
            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(Level)))
            {
                Level level = element as Level;
                if (level != null && string.Equals(level.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return level;
                }
            }

            return null;
        }

        /// <summary>Reads {x, y, z?} as an XYZ. z defaults to 0. Decimal feet, unconverted.</summary>
        internal static XYZ ReadPoint(JsonElement point, string label)
        {
            if (point.ValueKind != JsonValueKind.Object)
            {
                throw BridgeException.BadRequest(
                    "\"" + label + "\" must be an object {x, y, z?}, but was " + point.ValueKind + ".");
            }

            double x = JsonBody.RequireDouble(point, "x");
            double y = JsonBody.RequireDouble(point, "y");

            double z = 0.0;
            JsonElement value;
            if (JsonBody.TryGet(point, "z", out value))
            {
                z = JsonBody.AsDouble(value, "z");
            }

            return new XYZ(x, y, z);
        }

        /// <summary>The {x, y, z?} carried under <paramref name="name"/>, which must be present.</summary>
        internal static XYZ RequirePoint(JsonElement root, string name)
        {
            JsonElement point;
            if (!JsonBody.TryGet(root, name, out point))
            {
                throw BridgeException.BadRequest("\"" + name + "\" is required and must be {x, y, z?}.");
            }

            return ReadPoint(point, name);
        }

        /// <summary>
        /// Reads an array of {x, y, z?} into XYZ, every one of them at <paramref name="z"/> when
        /// <paramref name="flatten"/> is set - which is what a plan-space boundary wants.
        /// </summary>
        internal static List<XYZ> ReadPoints(JsonElement array, string label, bool flatten, double z)
        {
            List<XYZ> points = new List<XYZ>();

            int index = 0;
            foreach (JsonElement item in array.EnumerateArray())
            {
                XYZ point = ReadPoint(item, label + "[" + index + "]");
                points.Add(flatten ? new XYZ(point.X, point.Y, z) : point);
                index++;
            }

            return points;
        }

        /// <summary>
        /// Builds a closed CurveLoop from a ring of points, closing it when the caller's last point
        /// is not the first. A segment shorter than Revit's short-curve tolerance is a BAD_REQUEST
        /// naming the offending index, rather than an unreadable sketch failure out of Revit.
        /// </summary>
        internal static CurveLoop PolygonLoop(Document document, List<XYZ> points, string label)
        {
            List<XYZ> ring = new List<XYZ>(points);

            // A caller that repeated the first point at the end means the same ring as one that
            // did not; Line.CreateBound would just refuse the zero-length closing segment.
            if (ring.Count > 1 && ring[0].DistanceTo(ring[ring.Count - 1]) < document.Application.ShortCurveTolerance)
            {
                ring.RemoveAt(ring.Count - 1);
            }

            if (ring.Count < 3)
            {
                throw BridgeException.BadRequest(
                    "\"" + label + "\" needs at least 3 distinct points to close a loop; got " + ring.Count + ".");
            }

            CurveLoop loop = new CurveLoop();

            for (int index = 0; index < ring.Count; index++)
            {
                XYZ start = ring[index];
                XYZ end = ring[(index + 1) % ring.Count];

                if (start.DistanceTo(end) < document.Application.ShortCurveTolerance)
                {
                    throw BridgeException.BadRequest(
                        "\"" + label + "\" segment " + index + " is shorter than Revit's short-curve "
                            + "tolerance, so the loop cannot be sketched.");
                }

                loop.Append(Line.CreateBound(start, end));
            }

            return loop;
        }

        internal static List<string> LevelNames(Document document)
        {
            List<string> names = new List<string>();

            foreach (Element element in new FilteredElementCollector(document).OfClass(typeof(Level)))
            {
                string name = SafeName(element);
                if (name != null)
                {
                    names.Add(name);
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>
        /// Looks the parameter up on the instance, then on its type. Returns null when neither has
        /// it, so callers can report a precise PARAMETER_NOT_FOUND.
        /// </summary>
        internal static Parameter FindParameter(Document document, Element element, string name, out bool fromType)
        {
            fromType = false;

            Parameter parameter = element.LookupParameter(name);
            if (parameter != null)
            {
                return parameter;
            }

            ElementId typeId = element.GetTypeId();
            if (typeId == null || typeId == ElementId.InvalidElementId)
            {
                return null;
            }

            Element type = document.GetElement(typeId);
            if (type == null)
            {
                return null;
            }

            parameter = type.LookupParameter(name);
            fromType = parameter != null;
            return parameter;
        }

        internal static Dictionary<string, object> DescribeParameter(Parameter parameter, bool fromType)
        {
            return new Dictionary<string, object>
            {
                { "value", RawValue(parameter) },
                { "display", SafeValueString(parameter) },
                { "storageType", parameter.StorageType.ToString() },
                { "isReadOnly", parameter.IsReadOnly },
                { "source", fromType ? "type" : "instance" },
            };
        }

        internal static object RawValue(Parameter parameter)
        {
            switch (parameter.StorageType)
            {
                case StorageType.Double:
                    return parameter.AsDouble();
                case StorageType.Integer:
                    return parameter.AsInteger();
                case StorageType.String:
                    return parameter.AsString();
                case StorageType.ElementId:
                    ElementId id = parameter.AsElementId();
                    if (id == null)
                    {
                        return null;
                    }

                    return id.Value;
                default:
                    return null;
            }
        }

        internal static string SafeValueString(Parameter parameter)
        {
            try
            {
                return parameter.AsValueString();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
