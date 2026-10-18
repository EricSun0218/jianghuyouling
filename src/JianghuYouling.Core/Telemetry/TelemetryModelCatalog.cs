using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Telemetry
{
    internal static class TelemetryModelCatalog
    {
        private static readonly JArray Models = Load();
        private static readonly HashSet<string> Ids = BuildIds();

        internal static bool IsKnown(string id) => id != null && Ids.Contains(id);

        internal static string Normalize(string model)
        {
            if (string.IsNullOrWhiteSpace(model) || model.Length > 256) return "other";
            string value = model.Trim().ToLowerInvariant();
            if (Ids.Contains(value)) return value;
            // Only emit catalog constants. Provider namespaces, local paths and custom names
            // never leave the machine, even when they contain a recognizable model family.
            int slash = value.LastIndexOf('/');
            if (slash >= 0) value = value.Substring(slash + 1);
            foreach (JToken item in Models)
                foreach (JToken prefixToken in (JArray)item["prefixes"])
                {
                    string prefix = prefixToken.Value<string>();
                    if (value == prefix || (value.StartsWith(prefix, StringComparison.Ordinal)
                        && value.Length > prefix.Length && "-.:".IndexOf(value[prefix.Length]) >= 0))
                        return item.Value<string>("id");
                }
            return "other";
        }

        private static JArray Load()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                "JianghuYouling.Core.Telemetry.Models.json"))
            using (var reader = new StreamReader(stream ?? throw new InvalidOperationException("Missing model catalog")))
                return JArray.Parse(reader.ReadToEnd());
        }

        private static HashSet<string> BuildIds()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (JToken item in Models) ids.Add(item.Value<string>("id"));
            return ids;
        }
    }
}
