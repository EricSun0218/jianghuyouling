using System.Globalization;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Tools
{
    /// <summary>Strict, non-throwing coercion for cross-provider tool arguments.</summary>
    public static class JsonArgumentReader
    {
        public static bool TryReadInt(JObject args, string key, int fallback, out int value)
        {
            value = fallback;
            if (args == null || string.IsNullOrEmpty(key)) return false;
            JToken token = args[key];
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
                return true;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.String) return false;
            return int.TryParse(token.ToString().Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value);
        }

        public static int ReadIntOrDefault(JObject args, string key, int fallback = 0)
            => TryReadInt(args, key, fallback, out int value) ? value : fallback;

        public static bool TryReadBool(JObject args, string key, bool fallback, out bool value)
        {
            value = fallback;
            if (args == null || string.IsNullOrEmpty(key)) return false;
            JToken token = args[key];
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
                return true;
            if (token.Type == JTokenType.Boolean)
            {
                value = token.Value<bool>();
                return true;
            }
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.String) return false;
            string raw = token.ToString().Trim().ToLowerInvariant();
            if (raw == "1" || raw == "true") { value = true; return true; }
            if (raw == "0" || raw == "false") { value = false; return true; }
            return false;
        }

        public static bool ReadBoolOrDefault(JObject args, string key, bool fallback = false)
            => TryReadBool(args, key, fallback, out bool value) ? value : fallback;
    }
}
