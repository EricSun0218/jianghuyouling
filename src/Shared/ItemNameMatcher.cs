using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace JianghuYouling.Shared
{
    public static class ItemNameMatcher
    {
        public static string StripCountSuffix(string text)
        {
            return Regex.Replace((text ?? "").Trim(), @"[×xX]\s*\d+\s*$", "").Trim();
        }

        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = StripCountSuffix(StripTags(text));
            text = Regex.Replace(text, @"[\(（][一二三四五六七八九]品[\)）]\s*$", "");
            foreach (var prefix in new[] { "我的", "我这", "这套", "这门", "这把", "这件", "本派的", "本门的", "贵" })
                if (text.StartsWith(prefix, StringComparison.Ordinal)) { text = text.Substring(prefix.Length); break; }

            var chars = new List<char>(text.Length);
            const string strip = "『』「」《》<>“”\"‘’'·、，,。.！!? \t　";
            foreach (var ch in text)
                if (strip.IndexOf(ch) < 0) chars.Add(ch);
            return new string(chars.ToArray());
        }

        public static string StripItemQueryNoise(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            foreach (var token in new[] {
                "我身上", "太吾身上", "身上", "随身", "手里", "包里", "背包", "有没有", "有无", "是否有",
                "可赠", "可给", "可换", "可装备", "能给", "能送", "能换", "查询", "查找", "寻找",
                "给我", "想要", "给", "送", "拿", "找", "想", "要",
                "一件", "一个", "一把", "一条", "一根", "一些", "几件", "几个", "几把", "几条", "几根",
                "这个", "那个", "这件", "那件", "这把", "那把", "这条", "那条", "的", "吧", "吗", "呢"
            })
                text = text.Replace(token, "");
            return text;
        }

        public static IEnumerable<string> QueryAliases(string query)
        {
            query = StripItemQueryNoise(Normalize(query));
            if (string.IsNullOrEmpty(query)) yield break;

            yield return query;
            if (query.EndsWith("子", StringComparison.Ordinal) && query.Length > 1) yield return query.Substring(0, query.Length - 1);
            if (query.EndsWith("儿", StringComparison.Ordinal) && query.Length > 1) yield return query.Substring(0, query.Length - 1);

            if (query.IndexOf("金创", StringComparison.Ordinal) >= 0 || query.IndexOf("创伤", StringComparison.Ordinal) >= 0)
            {
                yield return "金创";
                yield return "创伤";
                yield return "药";
            }
            if (query.IndexOf("毒药", StringComparison.Ordinal) >= 0 || query == "毒")
            {
                yield return "毒";
                yield return "断肠";
                yield return "迷魂";
                yield return "化尸";
                yield return "散";
            }
            if (query.IndexOf("药材", StringComparison.Ordinal) >= 0) yield return "药材";
            if (query == "药" || query.IndexOf("药品", StringComparison.Ordinal) >= 0 || query.IndexOf("丹药", StringComparison.Ordinal) >= 0)
            {
                yield return "药";
                yield return "丹";
                yield return "丸";
                yield return "膏";
                yield return "散";
            }
            if (query.IndexOf("绳", StringComparison.Ordinal) >= 0) yield return "绳";
            if (query.IndexOf("布料", StringComparison.Ordinal) >= 0) yield return "布料";
            if (query.IndexOf("矿", StringComparison.Ordinal) >= 0) { yield return "矿"; yield return "金石"; }
            if (query.IndexOf("木材", StringComparison.Ordinal) >= 0) yield return "木料";
            if (query.IndexOf("食物", StringComparison.Ordinal) >= 0 || query.IndexOf("吃", StringComparison.Ordinal) >= 0) yield return "食";
            if (query.IndexOf("书籍", StringComparison.Ordinal) >= 0 || query == "书") yield return "书";
            if (query.IndexOf("武具", StringComparison.Ordinal) >= 0) yield return "装备";
        }

        public static bool IsHit(string candidate, string query)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(query)) return false;
            if (candidate == query) return true;
            string c = Normalize(candidate);
            if (c.Length == 0) return false;
            foreach (var alias in QueryAliases(query))
            {
                string q = Normalize(alias);
                if (q.Length == 0) continue;
                if (c == q || c.IndexOf(q, StringComparison.Ordinal) >= 0 || q.IndexOf(c, StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }

        private static string StripTags(string text)
        {
            return string.IsNullOrEmpty(text) ? "" : Regex.Replace(text, "<[^>]+>", "").Trim();
        }
    }
}
