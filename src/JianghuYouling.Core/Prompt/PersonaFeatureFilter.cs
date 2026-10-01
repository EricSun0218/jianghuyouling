using System;
using System.Collections.Generic;

namespace JianghuYouling.Core.Prompt
{
    /// <summary>
    /// Filters item-shaped placeholder features that the game can expose through the same
    /// field as durable personality traits. This filter is intentionally scoped to persona
    /// evidence; gameplay feature queries continue to see the authoritative raw list.
    /// </summary>
    public static class PersonaFeatureFilter
    {
        private static readonly HashSet<string> IgnoredFeatures = new HashSet<string>(
            new[]
            {
                "一支毛笔",
                "一副人偶",
                "一只刨子",
                "一支玉箫",
                "一根草药",
                "一支拂尘",
                "一颗骰子",
                "一块玉佩",
                "一撮泥土",
                "一把小刀",
                "一串佛珠",
                "一盒胭脂",
            },
            StringComparer.Ordinal);

        private static readonly char[] JoinedSeparators =
            { '、', ',', '，', ';', '；', '\r', '\n', '|' };

        public static bool IsIgnored(string value)
            => !string.IsNullOrWhiteSpace(value) && IgnoredFeatures.Contains(value.Trim());

        public static List<string> Filter(IEnumerable<string> features, int max = 16)
        {
            var clean = new List<string>();
            if (features == null || max <= 0) return clean;
            foreach (string raw in features)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string value = raw.Trim();
                if (IsIgnored(value) || clean.Contains(value)) continue;
                clean.Add(value);
                if (clean.Count >= max) break;
            }
            return clean;
        }

        public static string Join(IEnumerable<string> features, int max = 16)
        {
            List<string> clean = Filter(features, max);
            return clean.Count == 0 ? null : string.Join("、", clean.ToArray());
        }

        /// <summary>
        /// Defense-in-depth for callers that construct prompt profiles directly instead of
        /// going through PortraitStore.BuildProfile.
        /// </summary>
        public static string FilterJoinedText(string featuresText, int max = 16)
        {
            if (string.IsNullOrWhiteSpace(featuresText)) return null;
            return Join(featuresText.Split(JoinedSeparators, StringSplitOptions.RemoveEmptyEntries), max);
        }
    }
}
