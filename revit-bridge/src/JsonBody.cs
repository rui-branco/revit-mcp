using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace RevitMcpBridge
{
    /// <summary>
    /// Small readers over the request body. Every failure is a BAD_REQUEST with a message that
    /// names the offending field, because "Object reference not set" tells an MCP client nothing.
    /// Numbers are accepted as JSON numbers or as numeric strings - LLM-generated payloads quote
    /// numbers often enough that being strict here is just self-harm.
    /// </summary>
    internal static class JsonBody
    {
        internal static bool TryGet(JsonElement root, string name, out JsonElement value)
        {
            value = default(JsonElement);

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty(name, out value))
            {
                return false;
            }

            return value.ValueKind != JsonValueKind.Null;
        }

        internal static string OptionalString(JsonElement root, string name)
        {
            JsonElement value;
            if (!TryGet(root, name, out value))
            {
                return null;
            }

            string text = AsString(value, name);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return text;
        }

        internal static string RequireString(JsonElement root, string name)
        {
            string text = OptionalString(root, name);
            if (text == null)
            {
                throw BridgeException.BadRequest("\"" + name + "\" is required and must be a non-empty string.");
            }

            return text;
        }

        internal static int OptionalInt(JsonElement root, string name, int fallback)
        {
            JsonElement value;
            if (!TryGet(root, name, out value))
            {
                return fallback;
            }

            return (int)AsDouble(value, name);
        }

        internal static double OptionalDouble(JsonElement root, string name, double fallback)
        {
            JsonElement value;
            if (!TryGet(root, name, out value))
            {
                return fallback;
            }

            return AsDouble(value, name);
        }

        internal static bool OptionalBool(JsonElement root, string name, bool fallback)
        {
            JsonElement value;
            if (!TryGet(root, name, out value))
            {
                return fallback;
            }

            return AsBool(value, name);
        }

        internal static double RequireDouble(JsonElement root, string name)
        {
            JsonElement value;
            if (!TryGet(root, name, out value))
            {
                throw BridgeException.BadRequest("\"" + name + "\" is required and must be a number.");
            }

            return AsDouble(value, name);
        }

        internal static JsonElement RequireArray(JsonElement root, string name)
        {
            JsonElement value;
            if (!TryGet(root, name, out value) || value.ValueKind != JsonValueKind.Array)
            {
                throw BridgeException.BadRequest("\"" + name + "\" is required and must be an array.");
            }

            return value;
        }

        /// <summary>
        /// Accepts either a bare top-level array or an object carrying the array under
        /// <paramref name="name"/>, so both {"levels": [...]} and [...] work.
        /// </summary>
        internal static JsonElement ArrayOrProperty(JsonElement root, string name)
        {
            if (root.ValueKind == JsonValueKind.Array)
            {
                return root;
            }

            return RequireArray(root, name);
        }

        internal static List<long> RequireIds(JsonElement root, string name)
        {
            JsonElement array;

            if (root.ValueKind == JsonValueKind.Array)
            {
                array = root;
            }
            else
            {
                array = RequireArray(root, name);
            }

            List<long> ids = new List<long>();

            foreach (JsonElement item in array.EnumerateArray())
            {
                ids.Add(AsLong(item, name));
            }

            if (ids.Count == 0)
            {
                throw BridgeException.BadRequest("\"" + name + "\" must contain at least one element id.");
            }

            return ids;
        }

        internal static List<string> OptionalStringList(JsonElement root, string name)
        {
            JsonElement value;
            if (!TryGet(root, name, out value))
            {
                return null;
            }

            if (value.ValueKind != JsonValueKind.Array)
            {
                throw BridgeException.BadRequest("\"" + name + "\" must be an array of strings.");
            }

            List<string> items = new List<string>();

            foreach (JsonElement item in value.EnumerateArray())
            {
                string text = AsString(item, name);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    items.Add(text);
                }
            }

            return items;
        }

        internal static string AsString(JsonElement value, string name)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return value.GetString();
                case JsonValueKind.Number:
                    return value.GetRawText();
                case JsonValueKind.True:
                    return "true";
                case JsonValueKind.False:
                    return "false";
                default:
                    throw BridgeException.BadRequest(
                        "\"" + name + "\" must be a string, but was " + value.ValueKind + ".");
            }
        }

        internal static bool AsBool(JsonElement value, string name)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.True:
                    return true;

                case JsonValueKind.False:
                    return false;

                case JsonValueKind.String:
                    bool parsed;
                    if (bool.TryParse(value.GetString(), out parsed))
                    {
                        return parsed;
                    }

                    break;
            }

            throw BridgeException.BadRequest(
                "\"" + name + "\" must be true or false, but was " + value.ValueKind + ".");
        }

        internal static double AsDouble(JsonElement value, string name)
        {
            if (value.ValueKind == JsonValueKind.Number)
            {
                return value.GetDouble();
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                double parsed;
                if (double.TryParse(
                        value.GetString(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out parsed))
                {
                    return parsed;
                }
            }

            throw BridgeException.BadRequest(
                "\"" + name + "\" must be a number, but was " + value.ValueKind + ".");
        }

        internal static long AsLong(JsonElement value, string name)
        {
            if (value.ValueKind == JsonValueKind.Number)
            {
                long number;
                if (value.TryGetInt64(out number))
                {
                    return number;
                }
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                long parsed;
                if (long.TryParse(
                        value.GetString(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out parsed))
                {
                    return parsed;
                }
            }

            throw BridgeException.BadRequest(
                "\"" + name + "\" must contain integer element ids, but found " + value.ValueKind + ".");
        }
    }
}
