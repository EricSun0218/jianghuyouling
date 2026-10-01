using System;
using System.Collections.Generic;
using System.Text;
using JianghuYouling.Core.Llm;

namespace JianghuYouling.Core.Text
{
    /// <summary>
    /// Keeps a successfully disclosed in-game secret visible in the committed prose.
    /// The model may phrase the surrounding dialogue freely, but may not replace the
    /// actual fact with a riddle or a generic "I have something to tell you".
    /// </summary>
    public static class SecretDisclosureProjection
    {
        private const string ThirdPartyVisiblePlaceholder =
            "（具体内容只告知了指定之人，未向太吾透露。）";
        private const string ThirdPartyContextPlaceholder =
            "【一桩只告知指定人物、未向太吾公开的秘闻】";

        public static string NormalizeSecret(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return value.Replace("\0", string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Trim();
        }

        public static string EnsureVisible(string reply, IEnumerable<string> disclosedSecrets,
            out bool changed)
        {
            changed = false;
            string result = reply ?? string.Empty;
            if (disclosedSecrets == null) return result;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in disclosedSecrets)
            {
                string secret = NormalizeSecret(raw);
                if (secret.Length == 0 || !seen.Add(secret)
                    || ContainsIgnoringWhitespace(result, secret)) continue;

                if (result.Length > 0)
                    result = result.TrimEnd() + "\n\n";
                result += "「" + secret + "」";
                changed = true;
            }
            return result;
        }

        /// <summary>
        /// Removes exact occurrences and strongly related sentence fragments for secrets
        /// successfully disclosed to somebody other than Taiwu. A secret that was also
        /// disclosed to Taiwu in the same turn remains visible.
        /// </summary>
        public static string HideThirdPartyOnly(string reply,
            IEnumerable<string> thirdPartySecrets,
            IEnumerable<string> taiwuSecrets,
            out bool changed)
        {
            changed = false;
            string result = reply ?? string.Empty;
            if (thirdPartySecrets == null || result.Length == 0) return result;

            List<string> hidden = BuildHiddenSecrets(thirdPartySecrets, taiwuSecrets);
            foreach (string secret in hidden)
            {
                result = ReplaceIgnoringWhitespace(result, secret,
                    ThirdPartyVisiblePlaceholder, out bool replaced);
                changed |= replaced;
                result = ReplaceSecretDerivedSegments(result, secret,
                    ThirdPartyVisiblePlaceholder, out bool relatedReplaced);
                changed |= relatedReplaced;
            }
            return result;
        }

        /// <summary>
        /// Removes the canonical third-party-only secret from every text-bearing field that
        /// can be serialized into a later stateless model request. Provider signatures remain
        /// untouched; callers must not issue another request when an opaque signature may have
        /// been derived from the secret-bearing reasoning stream.
        /// </summary>
        public static bool ScrubRequestMessagesInPlace(IList<LlmMessage> messages,
            IEnumerable<string> thirdPartySecrets, IEnumerable<string> taiwuSecrets)
        {
            if (messages == null || messages.Count == 0) return false;
            List<string> hidden = BuildHiddenSecrets(thirdPartySecrets, taiwuSecrets);
            if (hidden.Count == 0) return false;

            bool changed = false;
            foreach (LlmMessage message in messages)
            {
                if (message == null) continue;
                message.Content = ScrubCanonicalSecrets(message.Content, hidden, out bool contentChanged);
                message.ReasoningContent = ScrubCanonicalSecrets(message.ReasoningContent, hidden,
                    out bool reasoningChanged);
                changed |= contentChanged || reasoningChanged;
                if (message.ToolCalls == null) continue;
                foreach (LlmToolCall call in message.ToolCalls)
                {
                    if (call == null) continue;
                    call.ArgumentsJson = ScrubCanonicalSecrets(call.ArgumentsJson, hidden,
                        out bool argumentsChanged);
                    changed |= argumentsChanged;
                }
            }
            return changed;
        }

        public static bool HasThirdPartyOnlySecrets(IEnumerable<string> thirdPartySecrets,
            IEnumerable<string> taiwuSecrets)
        {
            return BuildHiddenSecrets(thirdPartySecrets, taiwuSecrets).Count > 0;
        }

        private static string ScrubCanonicalSecrets(string value, IList<string> hidden,
            out bool changed)
        {
            changed = false;
            string result = value;
            if (string.IsNullOrEmpty(result) || hidden == null) return result;
            foreach (string secret in hidden)
            {
                result = ReplaceIgnoringWhitespace(result, secret,
                    ThirdPartyContextPlaceholder, out bool replaced);
                changed |= replaced;
            }
            return result;
        }

        private static List<string> BuildHiddenSecrets(IEnumerable<string> thirdPartySecrets,
            IEnumerable<string> taiwuSecrets)
        {
            var visible = new HashSet<string>(StringComparer.Ordinal);
            if (taiwuSecrets != null)
                foreach (string raw in taiwuSecrets)
                {
                    string key = CollapseWhitespace(NormalizeSecret(raw));
                    if (key.Length > 0) visible.Add(key);
                }

            var hiddenSeen = new HashSet<string>(StringComparer.Ordinal);
            var hidden = new List<string>();
            if (thirdPartySecrets != null)
                foreach (string raw in thirdPartySecrets)
                {
                    string secret = NormalizeSecret(raw);
                    string key = CollapseWhitespace(secret);
                    if (key.Length == 0 || visible.Contains(key) || !hiddenSeen.Add(key)) continue;
                    hidden.Add(secret);
                }
            hidden.Sort((left, right) => CollapseWhitespace(right).Length
                .CompareTo(CollapseWhitespace(left).Length));
            return hidden;
        }

        private static string ReplaceSecretDerivedSegments(string text, string secret,
            string replacement, out bool changed)
        {
            changed = false;
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(secret)) return text ?? string.Empty;

            var ranges = new List<Tuple<int, int>>();
            int start = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                bool end = i == text.Length;
                if (!end && !IsSentenceBoundary(text[i])) continue;
                int length = i - start;
                if (length > 0)
                {
                    string segment = text.Substring(start, length);
                    if (!segment.Contains(ThirdPartyVisiblePlaceholder)
                        && LooksDerivedFromSecret(segment, secret))
                        ranges.Add(Tuple.Create(start, i));
                }
                start = i + 1;
            }
            if (ranges.Count == 0) return text;

            var result = new StringBuilder(text);
            for (int i = ranges.Count - 1; i >= 0; i--)
            {
                int rangeStart = ranges[i].Item1;
                int length = ranges[i].Item2 - rangeStart;
                result.Remove(rangeStart, length);
                result.Insert(rangeStart, replacement ?? string.Empty);
            }
            changed = true;
            return result.ToString();
        }

        private static bool LooksDerivedFromSecret(string candidate, string secret)
        {
            string left = SimilarityText(candidate);
            string right = SimilarityText(secret);
            if (left.Length < 3 || right.Length < 3) return false;

            var leftChars = new HashSet<char>(left);
            var rightChars = new HashSet<char>(right);
            int commonChars = 0;
            foreach (char c in rightChars)
                if (leftChars.Contains(c)) commonChars++;

            int commonBigrams = CountCommonBigrams(left, right);
            if (rightChars.Count < 8)
                return commonChars >= 3 && commonBigrams >= 1
                    && commonChars * 100 >= rightChars.Count * 60;
            return commonChars >= 4
                && commonChars * 100 >= rightChars.Count * 30
                && (commonBigrams >= 1 || commonChars >= 6);
        }

        private static string SimilarityText(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            const string stop = "的了着过是在与和及或而并非不把被将向为有无只这那其一个之";
            var result = new StringBuilder(value.Length);
            foreach (char raw in value)
            {
                char c = char.ToLowerInvariant(raw);
                if (!char.IsLetterOrDigit(c) || stop.IndexOf(c) >= 0) continue;
                result.Append(c);
            }
            return result.ToString();
        }

        private static int CountCommonBigrams(string left, string right)
        {
            if (left.Length < 2 || right.Length < 2) return 0;
            var pairs = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < left.Length - 1; i++)
                pairs.Add(left.Substring(i, 2));
            int count = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < right.Length - 1; i++)
            {
                string pair = right.Substring(i, 2);
                if (pairs.Contains(pair) && seen.Add(pair)) count++;
            }
            return count;
        }

        private static bool IsSentenceBoundary(char c)
        {
            return c == '。' || c == '！' || c == '？'
                || c == '!' || c == '?' || c == '；' || c == ';' || c == '\n';
        }

        private static bool ContainsIgnoringWhitespace(string text, string value)
        {
            string haystack = CollapseWhitespace(text);
            string needle = CollapseWhitespace(value);
            return needle.Length > 0
                && haystack.IndexOf(needle, StringComparison.Ordinal) >= 0;
        }

        private static string ReplaceIgnoringWhitespace(string text, string value,
            string replacement, out bool changed)
        {
            changed = false;
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(value)) return text ?? string.Empty;

            string needle = CollapseWhitespace(value);
            if (needle.Length == 0) return text;

            var collapsed = new StringBuilder(text.Length);
            var originalIndexes = new List<int>(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i])) continue;
                collapsed.Append(text[i]);
                originalIndexes.Add(i);
            }

            string searchable = collapsed.ToString();
            int cursor = 0;
            var ranges = new List<Tuple<int, int>>();
            while (cursor <= searchable.Length - needle.Length)
            {
                int found = searchable.IndexOf(needle, cursor, StringComparison.Ordinal);
                if (found < 0) break;
                int start = originalIndexes[found];
                int end = originalIndexes[found + needle.Length - 1] + 1;
                ranges.Add(Tuple.Create(start, end));
                cursor = found + needle.Length;
            }
            if (ranges.Count == 0) return text;

            var result = new StringBuilder(text);
            for (int i = ranges.Count - 1; i >= 0; i--)
            {
                int start = ranges[i].Item1;
                int length = ranges[i].Item2 - start;
                result.Remove(start, length);
                result.Insert(start, replacement ?? string.Empty);
            }
            changed = true;
            return result.ToString();
        }

        private static string CollapseWhitespace(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (!char.IsWhiteSpace(c)) sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
