using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RevitMcpBridge
{
    /// <summary>
    /// The JSON the bridge writes: one serializer configuration and the one error shape.
    ///
    /// It lives in the loader rather than next to the router for two reasons. The loader needs it
    /// itself - the reload endpoint and the last-resort failure handler in HttpBridgeServer both
    /// answer without a router - and keeping the single JsonSerializerOptions instance out of the
    /// collectible assembly means one less static that a reload has to shake loose.
    /// </summary>
    internal static class BridgeJson
    {
        internal static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            WriteIndented = false,

            // Model element names are full of characters the default encoder escapes into \uXXXX
            // noise. This response never lands in an HTML context, so relaxed escaping is safe and
            // keeps payloads readable.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        internal static string Serialize(object value)
        {
            return JsonSerializer.Serialize(value, SerializerOptions);
        }

        /// <summary>
        /// The one error shape every failure uses:
        /// {"error": {"code": "...", "message": "...", "stack": "..."}}.
        /// The Node client reads "message" and "stack"; "code" is an additive extra so a tool can
        /// branch on REVIT_BUSY or NO_ACTIVE_DOCUMENT without string-matching prose.
        /// </summary>
        internal static string Error(string code, Exception error)
        {
            Dictionary<string, object> detail = new Dictionary<string, object>
            {
                { "code", code },
                { "message", error.Message },

                // ToString() rather than StackTrace: it carries the exception type and any inner
                // exceptions, which makes it the closest analogue of a JavaScript err.stack.
                { "stack", error.ToString() },
            };

            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "error", detail },
            };

            return Serialize(payload);
        }
    }
}
