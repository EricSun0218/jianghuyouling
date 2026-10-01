using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Prompt
{
    /// <summary>
    /// 江湖有灵专用世界书交换格式。交换文件保留玩家可编辑的完整条目，
    /// 但不包含存档正文指纹、内置模板修订号等本地状态。
    /// </summary>
    public static class StructuredWorldBookExchange
    {
        public const string FormatName = "jianghu-youling-worldbook";
        public const int CurrentFormatVersion = 1;
        public const int MaxExchangeBytes = 8 * 1024 * 1024;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public sealed class ImportResult
        {
            public StructuredWorldBook.Document Document { get; set; }
            public string Format { get; set; }
            public int EntryCount => Document?.Entries?.Count ?? 0;
        }

        public static bool TryExport(StructuredWorldBook.Document source,
            out string json, out string error)
        {
            json = null;
            error = null;
            StructuredWorldBook.Document document = Clone(source);
            if (!StructuredWorldBook.Validate(document, out error)) return false;

            var entries = new JArray();
            foreach (StructuredWorldBook.Entry entry in document.Entries)
            {
                entries.Add(new JObject
                {
                    ["id"] = entry.Id ?? string.Empty,
                    ["category"] = entry.Category ?? "未分类",
                    ["name"] = entry.Name ?? string.Empty,
                    ["enabled"] = entry.Enabled,
                    ["mode"] = entry.Mode ?? StructuredWorldBook.ModeAlways,
                    ["priority"] = entry.Priority,
                    ["keywords"] = new JArray(SplitKeywords(entry.Keywords)),
                    ["content"] = entry.Content ?? string.Empty,
                });
            }

            var root = new JObject
            {
                ["format"] = FormatName,
                ["format_version"] = CurrentFormatVersion,
                ["entries"] = entries,
                ["legacy_final_instructions"] =
                    new JArray(document.LegacyFinalInstructions ?? new List<string>()),
            };
            json = root.ToString(Formatting.Indented) + "\n";
            if (StrictUtf8.GetByteCount(json) > MaxExchangeBytes)
            {
                json = null;
                error = "导出文件超过 8 MiB 上限";
                return false;
            }
            return true;
        }

        public static bool TryImport(string raw, out ImportResult result, out string error)
        {
            result = null;
            error = null;
            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "导入文件为空";
                return false;
            }
            if (StrictUtf8.GetByteCount(raw) > MaxExchangeBytes)
            {
                error = "导入文件超过 8 MiB 上限";
                return false;
            }

            string trimmed = raw.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            if (!trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                error = "只支持由当前版本导出的江湖有灵世界书 JSON";
                return false;
            }

            JObject root;
            try
            {
                root = JObject.Parse(trimmed, new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                });
            }
            catch (JsonException)
            {
                error = "JSON 格式损坏";
                return false;
            }

            string format = StringValue(Get(root, "format"));
            JToken entriesToken = Get(root, "entries");
            if (!string.Equals(format, FormatName, StringComparison.Ordinal))
            {
                error = "只支持由当前版本导出的江湖有灵世界书 JSON";
                return false;
            }
            int formatVersion = IntValue(Get(root, "format_version"), 0);
            if (formatVersion <= 0 || formatVersion > CurrentFormatVersion)
            {
                error = formatVersion > CurrentFormatVersion
                    ? "该世界书来自更高版本，当前版本无法安全导入"
                    : "江湖有灵世界书格式版本无效";
                return false;
            }
            if (!(entriesToken is JArray entries))
            {
                error = "江湖有灵世界书 entries 必须是数组";
                return false;
            }
            if (entries.Count == 0)
            {
                error = "世界书没有可导入的条目";
                return false;
            }
            if (entries.Count > StructuredWorldBook.MaxEntries)
            {
                error = "世界书条目不能超过 " + StructuredWorldBook.MaxEntries + " 条";
                return false;
            }

            var document = new StructuredWorldBook.Document
            {
                Version = StructuredWorldBook.CurrentVersion,
                SourceFingerprint = null,
                DefaultTemplateRevision = 0,
                LegacyFinalInstructions =
                    RawStringList(Get(root, "legacy_final_instructions")),
            };
            for (int i = 0; i < entries.Count; i++)
            {
                if (!(entries[i] is JObject entry))
                {
                    error = "entries 中包含非对象条目";
                    return false;
                }
                document.Entries.Add(ConvertEntry(entry, i));
            }

            StructuredWorldBook.Normalize(document);
            EnsureUniqueIds(document);
            if (!StructuredWorldBook.Validate(document, out error)) return false;
            result = new ImportResult
            {
                Document = document,
                Format = "江湖有灵世界书 JSON",
            };
            return true;
        }

        private static StructuredWorldBook.Entry ConvertEntry(JObject source, int index)
        {
            string id = StringValue(Get(source, "id"));
            return new StructuredWorldBook.Entry
            {
                Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim(),
                Category = StringValue(Get(source, "category")) ?? "未分类",
                Name = StringValue(Get(source, "name"))
                    ?? "导入条目 " + (index + 1).ToString(CultureInfo.InvariantCulture),
                Enabled = BoolValue(Get(source, "enabled"), true),
                Mode = StringValue(Get(source, "mode")) ?? StructuredWorldBook.ModeAlways,
                Priority = IntValue(Get(source, "priority"),
                    StructuredWorldBook.DefaultPriority),
                Keywords = string.Join(",", StringList(Get(source, "keywords"))),
                Content = StringValue(Get(source, "content")) ?? string.Empty,
            };
        }

        private static StructuredWorldBook.Document Clone(StructuredWorldBook.Document source)
        {
            var clone = new StructuredWorldBook.Document
            {
                Version = source?.Version ?? StructuredWorldBook.CurrentVersion,
                DefaultTemplateRevision = 0,
                SourceFingerprint = null,
                LegacyFinalInstructions = new List<string>(
                    source?.LegacyFinalInstructions ?? new List<string>()),
            };
            foreach (StructuredWorldBook.Entry entry in source?.Entries
                ?? new List<StructuredWorldBook.Entry>())
            {
                if (entry == null)
                {
                    clone.Entries.Add(null);
                    continue;
                }
                clone.Entries.Add(new StructuredWorldBook.Entry
                {
                    Id = entry.Id,
                    Category = entry.Category,
                    Name = entry.Name,
                    Enabled = entry.Enabled,
                    Mode = entry.Mode,
                    Priority = entry.Priority,
                    Keywords = entry.Keywords,
                    Content = entry.Content,
                });
            }
            StructuredWorldBook.Normalize(clone);
            return clone;
        }

        private static void EnsureUniqueIds(StructuredWorldBook.Document document)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (StructuredWorldBook.Entry entry in document?.Entries
                ?? new List<StructuredWorldBook.Entry>())
            {
                if (entry == null) continue;
                string id = (entry.Id ?? string.Empty).Trim();
                if (id.Length == 0 || !ids.Add(id))
                {
                    do { id = Guid.NewGuid().ToString("N"); } while (!ids.Add(id));
                    entry.Id = id;
                }
            }
        }

        private static string[] SplitKeywords(string value)
            => (value ?? string.Empty)
                .Split(new[] { ',', '，', ';', '；', '\n', '\r' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        private static List<string> StringList(JToken token)
        {
            var result = new List<string>();
            if (token == null || token.Type == JTokenType.Null) return result;
            if (token is JArray array)
            {
                foreach (JToken item in array)
                {
                    string value = StringValue(item);
                    if (!string.IsNullOrWhiteSpace(value)) result.Add(value.Trim());
                }
                return result;
            }
            string single = StringValue(token);
            if (!string.IsNullOrWhiteSpace(single)) result.AddRange(SplitKeywords(single));
            return result;
        }

        private static List<string> RawStringList(JToken token)
        {
            var result = new List<string>();
            if (token == null || token.Type == JTokenType.Null) return result;
            IEnumerable<JToken> values = token is JArray array ? array : new[] { token };
            foreach (JToken item in values)
            {
                string value = StringValue(item);
                if (!string.IsNullOrWhiteSpace(value)) result.Add(value.Trim());
            }
            return result;
        }

        private static JToken Get(JToken token, string name)
        {
            if (!(token is JObject value) || string.IsNullOrEmpty(name)) return null;
            return value.GetValue(name, StringComparison.OrdinalIgnoreCase);
        }

        private static string StringValue(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null
                || token.Type == JTokenType.Object || token.Type == JTokenType.Array)
                return null;
            return Convert.ToString(((JValue)token).Value, CultureInfo.InvariantCulture);
        }

        private static bool BoolValue(JToken token, bool fallback)
        {
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token.Type == JTokenType.Boolean) return token.Value<bool>();
            string value = StringValue(token);
            if (bool.TryParse(value, out bool parsed)) return parsed;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int number))
                return number != 0;
            return fallback;
        }

        private static int IntValue(JToken token, int fallback)
        {
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token.Type == JTokenType.Integer)
            {
                long integer;
                try { integer = token.Value<long>(); }
                catch { return fallback; }
                return integer >= int.MinValue && integer <= int.MaxValue
                    ? (int)integer : fallback;
            }
            string value = StringValue(token);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int parsed) ? parsed : fallback;
        }
    }
}
