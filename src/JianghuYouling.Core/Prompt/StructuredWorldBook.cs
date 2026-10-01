using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace JianghuYouling.Core.Prompt
{
    /// <summary>
    /// 酒馆玩家熟悉的精简结构化世界书。分类只负责整理；真正影响提示词的只有
    /// 条目开关、常驻/关键词两种触发方式和正文。运行时仍编译为原有纯文本协议，
    /// 因而旧存档、@/@@/! 语法、前缀缓存和世界书指纹全部继续兼容。
    /// </summary>
    public static class StructuredWorldBook
    {
        public const int CurrentVersion = 2;
        public const int MaxEntries = 1000;
        public const int MaxCategories = 100;
        public const int MaxNameChars = 80;
        public const int MaxCategoryChars = 40;
        public const int MaxKeywordsChars = 1000;
        public const int DefaultPriority = 100;
        public const int MinPriority = -9999;
        public const int MaxPriority = 9999;
        public const int CurrentDefaultTemplateRevision = 4;

        public const string ModeAlways = "always";
        public const string ModeKeyword = "keyword";
        public const string ReservedAllCategory = "全部";

        public sealed class Document
        {
            public int Version { get; set; } = CurrentVersion;
            /// <summary>
            /// 非零表示这份布局由内置默认世界书模板生成。玩家保存后继续携带该版本，
            /// 因而升级时只迁移旧的粗粒度默认布局，不覆盖玩家已经整理过的新布局。
            /// </summary>
            public int DefaultTemplateRevision { get; set; }
            public string SourceFingerprint { get; set; }
            public List<string> Categories { get; set; } = new List<string>();
            public List<Entry> Entries { get; set; } = new List<Entry>();
            /// <summary>
            /// 旧版 ! 临场铁令不暴露为第三种新建模式，但必须逐字保留并继续生效。
            /// </summary>
            public List<string> LegacyFinalInstructions { get; set; } = new List<string>();
        }

        public sealed class Entry
        {
            public string Id { get; set; }
            public string Category { get; set; }
            public string Name { get; set; }
            public bool Enabled { get; set; } = true;
            public string Mode { get; set; } = ModeAlways;
            /// <summary>
            /// 同一注入层内数值越大越靠后，因而离本轮出话更近；相同数值保持玩家原顺序。
            /// 常驻、关键词和旧版临场铁令的层级关系不受此字段改变。
            /// </summary>
            public int Priority { get; set; } = DefaultPriority;
            public string Keywords { get; set; }
            public string Content { get; set; }
        }

        public static Entry NewEntry(string category = null)
        {
            return new Entry
            {
                Id = Guid.NewGuid().ToString("N"),
                Category = NormalizeCategory(category),
                Name = "新条目",
                Enabled = true,
                Mode = ModeAlways,
                Priority = DefaultPriority,
                Keywords = "",
                Content = "",
            };
        }

        public static Document Import(string effectiveWorldBook)
        {
            string normalized = NormalizeNewlines(effectiveWorldBook);
            var document = new Document
            {
                SourceFingerprint = WorldBookFilter.ContentFingerprint(normalized),
            };
            var stable = new StringBuilder();
            string[] lines = normalized.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = (lines[i] ?? "").Trim();
                if (IsBlockHeader(trimmed, out string blockKeys, out bool isEnd))
                {
                    if (isEnd) continue;
                    var body = new StringBuilder();
                    while (++i < lines.Length)
                    {
                        string candidate = (lines[i] ?? "").Trim();
                        if (IsBlockHeader(candidate, out _, out bool candidateIsEnd))
                        {
                            // 旧文本不一定写 @@结束。遇到下一个块头时让外层循环重新消费它，
                            // 否则相邻关键词块会被吞掉一个。
                            if (!candidateIsEnd) i--;
                            break;
                        }
                        AppendRawLine(body, lines[i] ?? "");
                    }
                    AddKeywordEntry(document, blockKeys, body.ToString(),
                        FirstKeyword(blockKeys, "关键词条"));
                    continue;
                }

                if (IsSingleKeywordEntry(trimmed, out string keys, out string content))
                {
                    AddKeywordEntry(document, keys, content, FirstKeyword(keys, "关键词条"));
                    continue;
                }

                if (trimmed.Length > 0 && (trimmed[0] == '!' || trimmed[0] == '！'))
                {
                    string instruction = trimmed.Substring(1).Trim();
                    if (instruction.Length > 0) document.LegacyFinalInstructions.Add(instruction);
                    continue;
                }

                AppendRawLine(stable, lines[i] ?? "");
            }

            string stableText = stable.ToString().Trim();
            if (stableText.Length > 0)
            {
                document.Entries.Insert(0, new Entry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Category = "通用设定",
                    Name = "原有常驻设定",
                    Enabled = true,
                    Mode = ModeAlways,
                    Priority = DefaultPriority,
                    Keywords = "",
                    Content = stableText,
                });
            }

            Normalize(document);
            return document;
        }

        /// <summary>
        /// 把内置默认世界书按其顶层编号章节拆成稳定的语义分类。该方法只由存储层在确认
        /// 当前生效正文就是内置默认正文时调用；任意玩家纯文本仍走 Import，绝不猜测或
        /// 重写玩家自己的分类语义。
        /// </summary>
        public static Document ImportDefault(string defaultWorldBook)
        {
            string normalized = NormalizeNewlines(defaultWorldBook);
            var document = new Document
            {
                DefaultTemplateRevision = CurrentDefaultTemplateRevision,
                SourceFingerprint = WorldBookFilter.ContentFingerprint(normalized),
            };
            string currentCategory = null;
            string currentName = null;
            int sectionIndex = -1;
            var body = new StringBuilder();

            Action flush = () =>
            {
                string content = body.ToString().Trim();
                if (content.Length == 0 || sectionIndex < 0) return;
                var entry = new Entry
                {
                    Id = "default-" + sectionIndex.ToString("D2"),
                    Category = currentCategory,
                    Name = currentName,
                    Enabled = true,
                    Mode = ModeAlways,
                    Priority = DefaultPriority,
                    Keywords = "",
                    Content = content,
                };
                ConfigureDefaultTrigger(entry, sectionIndex);
                document.Entries.Add(entry);
                body.Clear();
            };

            foreach (string line in normalized.Split('\n'))
            {
                string trimmed = (line ?? "").Trim();
                if (TryParseDefaultSectionHeader(trimmed, out int index,
                    out string category, out string name))
                {
                    flush();
                    sectionIndex = index;
                    currentCategory = category;
                    currentName = name;
                }
                if (sectionIndex >= 0) AppendRawLine(body, line ?? "");
            }
            flush();

            // 内置资源格式意外变化时宁可回退为原有兼容导入，也不能漏掉正文。
            if (document.Entries.Count == 0) return Import(defaultWorldBook);
            Normalize(document);
            return document;
        }

        public static bool TryUpgradeDefaultTemplateLayout(Document document)
        {
            if (document == null
                || document.DefaultTemplateRevision < 1
                || document.DefaultTemplateRevision > CurrentDefaultTemplateRevision)
                return false;

            if (document.DefaultTemplateRevision < 2)
            {
                Entry first = document.Entries?.FirstOrDefault(x =>
                    string.Equals(x?.Id, "default-00", StringComparison.Ordinal));
                if (first != null
                    && string.Equals(first.Name, "最高优先级：回复格式与文风总纲",
                        StringComparison.Ordinal))
                    first.Name = "文风总纲";
                document.DefaultTemplateRevision = 2;
            }
            if (document.DefaultTemplateRevision < 3)
            {
                foreach (Entry entry in document.Entries ?? new List<Entry>())
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.Id)
                        || !entry.Id.StartsWith("default-", StringComparison.Ordinal)
                        || !int.TryParse(entry.Id.Substring("default-".Length), out int index))
                        continue;
                    ConfigureDefaultTrigger(entry, index);
                }
                document.DefaultTemplateRevision = 3;
            }
            if (document.DefaultTemplateRevision < 4)
            {
                document.Entries = document.Entries ?? new List<Entry>();
                bool alreadyPresent = document.Entries.Any(x =>
                    string.Equals(x?.Id, "default-14", StringComparison.Ordinal));
                if (!alreadyPresent)
                {
                    Entry safety = ImportDefault(DefaultWorldBook.Text).Entries.FirstOrDefault(x =>
                        string.Equals(x?.Id, "default-14", StringComparison.Ordinal));
                    if (safety == null) return false;
                    document.Entries.Add(new Entry
                    {
                        Id = safety.Id,
                        Category = safety.Category,
                        Name = safety.Name,
                        Enabled = safety.Enabled,
                        Mode = safety.Mode,
                        Priority = safety.Priority,
                        Keywords = safety.Keywords,
                        Content = safety.Content,
                    });
                }
                document.DefaultTemplateRevision = 4;
            }

            Normalize(document);
            return document.DefaultTemplateRevision == CurrentDefaultTemplateRevision;
        }

        public static string Compile(Document document)
        {
            if (document == null) return "";
            Normalize(document);
            var output = new StringBuilder();
            // OrderBy is stable: equal priorities retain the player's existing list order.
            // Lower values are emitted first, so the largest value is closest to the end of
            // its effective layer after WorldBookFilter separates always/keyword content.
            foreach (Entry entry in document.Entries.OrderBy(x => x?.Priority ?? DefaultPriority))
            {
                if (entry == null || !entry.Enabled || string.IsNullOrWhiteSpace(entry.Content)) continue;
                string content = NormalizeNewlines(entry.Content).Trim();
                if (content.Length == 0) continue;
                if (string.Equals(entry.Mode, ModeKeyword, StringComparison.OrdinalIgnoreCase))
                {
                    string keywords = NormalizeKeywords(entry.Keywords);
                    if (keywords.Length == 0) continue;
                    AppendSection(output, "@@" + keywords + "\n" + content + "\n@@结束");
                }
                else
                {
                    AppendSection(output, content);
                }
            }

            foreach (string instruction in document.LegacyFinalInstructions ?? new List<string>())
            {
                string value = CleanSingleLine(instruction);
                if (value.Length > 0) AppendSection(output, "!" + value);
            }
            return output.ToString().Trim();
        }

        public static bool Validate(Document document, out string error)
        {
            error = null;
            if (document == null) { error = "世界书条目数据为空"; return false; }
            Normalize(document);
            if (document.Entries.Count > MaxEntries) { error = "世界书条目不能超过 " + MaxEntries + " 条"; return false; }
            if (document.Categories.Count > MaxCategories) { error = "世界书分类不能超过 " + MaxCategories + " 个"; return false; }
            if (document.Categories.Contains(ReservedAllCategory, StringComparer.Ordinal))
            { error = "“全部”是筛选器保留名称，请换一个分类名"; return false; }
            foreach (Entry entry in document.Entries)
            {
                if (entry == null) { error = "世界书包含空条目"; return false; }
                if (entry.Name.Length == 0) { error = "每个条目都需要名称"; return false; }
                if (entry.Name.Length > MaxNameChars) { error = "条目名称不能超过 " + MaxNameChars + " 字"; return false; }
                if (entry.Category.Length > MaxCategoryChars) { error = "分类名称不能超过 " + MaxCategoryChars + " 字"; return false; }
                if ((entry.Keywords ?? "").Length > MaxKeywordsChars) { error = "关键词不能超过 " + MaxKeywordsChars + " 字"; return false; }
                if (entry.Priority < MinPriority || entry.Priority > MaxPriority)
                { error = "优先级必须在 " + MinPriority + " 到 " + MaxPriority + " 之间"; return false; }
                if (entry.Enabled && string.IsNullOrWhiteSpace(entry.Content))
                { error = "启用的条目“" + entry.Name + "”没有正文"; return false; }
                if (entry.Enabled && string.Equals(entry.Mode, ModeKeyword, StringComparison.Ordinal)
                    && string.IsNullOrWhiteSpace(NormalizeKeywords(entry.Keywords)))
                { error = "关键词条目“" + entry.Name + "”至少需要一个关键词"; return false; }
            }
            return true;
        }

        public static void Normalize(Document document)
        {
            if (document == null) return;
            document.Version = CurrentVersion;
            document.Entries = document.Entries ?? new List<Entry>();
            document.Categories = new List<string>();
            foreach (Entry entry in document.Entries)
            {
                if (entry == null) continue;
                entry.Id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id.Trim();
                entry.Category = NormalizeCategory(entry.Category);
                entry.Name = CleanSingleLine(entry.Name);
                entry.Mode = string.Equals(entry.Mode, ModeKeyword, StringComparison.OrdinalIgnoreCase)
                    ? ModeKeyword : ModeAlways;
                entry.Keywords = NormalizeKeywords(entry.Keywords);
                entry.Content = NormalizeNewlines(entry.Content).Trim();
                if (!document.Categories.Contains(entry.Category, StringComparer.Ordinal))
                    document.Categories.Add(entry.Category);
            }
            if (!document.Categories.Contains("未分类", StringComparer.Ordinal))
                document.Categories.Insert(0, "未分类");
            document.LegacyFinalInstructions = (document.LegacyFinalInstructions ?? new List<string>())
                .Select(CleanSingleLine)
                .Where(x => x.Length > 0)
                .ToList();
        }

        private static void AddKeywordEntry(Document document, string keys, string content, string name)
        {
            string normalizedContent = NormalizeNewlines(content).Trim();
            string normalizedKeys = NormalizeKeywords(keys);
            if (normalizedContent.Length == 0 || normalizedKeys.Length == 0) return;
            document.Entries.Add(new Entry
            {
                Id = Guid.NewGuid().ToString("N"),
                Category = "关键词条",
                Name = Truncate(CleanSingleLine(name), MaxNameChars),
                Enabled = true,
                Mode = ModeKeyword,
                Priority = DefaultPriority,
                Keywords = normalizedKeys,
                Content = normalizedContent,
            });
        }

        private static bool TryParseDefaultSectionHeader(string line, out int index,
            out string category, out string name)
        {
            index = -1;
            category = "";
            name = "";
            if (string.IsNullOrEmpty(line) || line[0] != '【' || line[line.Length - 1] != '】')
                return false;

            string[] numerals =
            {
                "零", "一", "二", "三", "四", "五", "六",
                "七", "八", "九", "十", "十一", "十二", "十三", "十四"
            };
            for (int i = 0; i < numerals.Length; i++)
            {
                string prefix = "【" + numerals[i] + "、";
                if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
                index = i;
                name = line.Substring(prefix.Length, line.Length - prefix.Length - 1).Trim();
                if (i == 0) name = "文风总纲";
                category = DefaultCategoryForSection(i);
                return name.Length > 0;
            }
            return false;
        }

        private static string DefaultCategoryForSection(int index)
        {
            switch (index)
            {
                case 0: return "叙事规则";
                case 14: return "叙事规则";
                case 1: return "世界设定";
                case 2: return "相枢与剑冢";
                case 3: return "太吾传承";
                case 4: return "门派概览";
                case 5:
                case 8: return "武学体系";
                case 6:
                case 7:
                case 10: return "人物与社会";
                case 9: return "技艺体系";
                case 11: return "地理与交通";
                case 12: return "世界常识";
                case 13: return "门派功法";
                default: return "世界设定";
            }
        }

        private static void ConfigureDefaultTrigger(Entry entry, int index)
        {
            if (entry == null) return;
            entry.Mode = index <= 1 || index == 14 ? ModeAlways : ModeKeyword;
            entry.Priority = index == 14 ? MaxPriority : DefaultPriority;
            switch (index)
            {
                case 0:
                case 1:
                case 14:
                    entry.Keywords = "";
                    break;
                case 2:
                    entry.Keywords = "相枢,入魔,剑冢,侵袭,失心,心魔";
                    break;
                case 3:
                    entry.Keywords = "太吾传人,太吾村,伏虞剑柄,传承,铭刻,玄灰";
                    break;
                case 4:
                    entry.Keywords = "门派,门规,拜师,入派,少林,武当,峨眉,百花,璇女,然山,界青,伏龙,铸剑,空桑,元山,狮相,五仙,血犼,无量";
                    break;
                case 5:
                    entry.Keywords = "内力,五行,金刚,紫霞,玄阴,纯阳,归元,真气";
                    break;
                case 6:
                    entry.Keywords = "处世,立场,刚正,仁善,中庸,叛逆,唯我,性情,品性";
                    break;
                case 7:
                    entry.Keywords = "人物属性,膂力,体质,灵敏,根骨,悟性,定力,魅力,身龄,命龄";
                    break;
                case 8:
                    entry.Keywords = "功法,武学,内功,摧破,轻灵,护体,绝技,运功,功法品阶";
                    break;
                case 9:
                    entry.Keywords = "技艺,音律,弈棋,诗书,绘画,术数,品鉴,锻造,制木,医术,毒术,织锦,巧匠,道法,佛学,厨艺,杂学";
                    break;
                case 10:
                    entry.Keywords = "身份,地位,关系,同道,亲族,夫妻,爱慕,结义,师徒,仇敌,恩义";
                    break;
                case 11:
                    entry.Keywords = "地图,地块,州域,城镇,村落,驿站,交通,移动,赶路,所在地";
                    break;
                case 12:
                    entry.Keywords = "常识,银钱,物品,装备,品级,伤势,中毒,精纯,名誉,戒心,好感";
                    break;
                case 13:
                    entry.Keywords = "门派功法,功法全览,不传之秘,真传,绝学,门派武学";
                    break;
                default:
                    entry.Mode = ModeAlways;
                    entry.Keywords = "";
                    break;
            }
        }

        private static bool IsBlockHeader(string line, out string keys, out bool isEnd)
        {
            keys = "";
            isEnd = false;
            if (string.IsNullOrEmpty(line) || line.Length < 2) return false;
            char first = line[0], second = line[1];
            if ((first != '@' && first != '＠') || (second != '@' && second != '＠')) return false;
            keys = line.Substring(2).Trim();
            isEnd = keys.Length == 0 || keys.Equals("end", StringComparison.OrdinalIgnoreCase) || keys == "结束";
            return true;
        }

        private static bool IsSingleKeywordEntry(string line, out string keys, out string content)
        {
            keys = "";
            content = "";
            if (string.IsNullOrEmpty(line) || (line[0] != '@' && line[0] != '＠')
                || (line.Length > 1 && (line[1] == '@' || line[1] == '＠'))) return false;
            string body = line.Substring(1);
            int separator = IndexOfSeparator(body);
            if (separator < 0) return false;
            keys = body.Substring(0, separator).Trim();
            content = body.Substring(separator + 1).Trim();
            return keys.Length > 0 && content.Length > 0;
        }

        private static int IndexOfSeparator(string text)
        {
            int best = -1;
            foreach (char separator in new[] { ':', '：', '|', '｜' })
            {
                int index = (text ?? "").IndexOf(separator);
                if (index >= 0 && (best < 0 || index < best)) best = index;
            }
            return best;
        }

        private static string FirstKeyword(string keys, string fallback)
        {
            string normalized = NormalizeKeywords(keys);
            if (normalized.Length == 0) return fallback;
            int comma = normalized.IndexOf(',');
            return comma < 0 ? normalized : normalized.Substring(0, comma);
        }

        private static string NormalizeKeywords(string value)
        {
            char[] separators = { ',', '，', '、', '/', '／', '\r', '\n' };
            return string.Join(",", (value ?? "").Split(separators)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase));
        }

        private static string NormalizeCategory(string value)
        {
            string normalized = CleanSingleLine(value);
            return normalized.Length == 0 ? "未分类" : normalized;
        }

        private static string CleanSingleLine(string value)
            => (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();

        private static string Truncate(string value, int maxChars)
            => (value ?? "").Length <= maxChars ? (value ?? "") : value.Substring(0, maxChars);

        private static string NormalizeNewlines(string value)
            => (value ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

        private static void AppendRawLine(StringBuilder builder, string line)
        {
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(line ?? "");
        }

        private static void AppendSection(StringBuilder builder, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (builder.Length > 0) builder.Append("\n\n");
            builder.Append(text.Trim());
        }
    }
}
