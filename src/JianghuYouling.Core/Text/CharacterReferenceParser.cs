using System;
using System.Text.RegularExpressions;

namespace JianghuYouling.Core.Text
{
    public static class CharacterReferenceParser
    {
        private static readonly Regex AnnotatedId = new Regex(
            @"^(?:[^#()（）\r\n]+)?[（(]#(?<id>[0-9]+)(?:[,，][^#()（）\r\n]*)?[)）]$",
            RegexOptions.CultureInvariant);

        // Accept the exact roster notation as well as an explicit ID. This parses a
        // reference only; callers must still resolve the live entity and its permissions.
        public static int ParseId(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            string value = raw.Trim();
            string numeric = value.StartsWith("#", StringComparison.Ordinal) ? value.Substring(1) : value;
            if (int.TryParse(numeric, out int id) && id > 0) return id;
            Match match = AnnotatedId.Match(value);
            return match.Success && int.TryParse(match.Groups["id"].Value, out id) && id > 0 ? id : 0;
        }
    }
}
