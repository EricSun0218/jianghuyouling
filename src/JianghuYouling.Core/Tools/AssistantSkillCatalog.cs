using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace JianghuYouling.Core.Tools
{
    /// <summary>灵儿知识技能目录。本地确定性选择，不发起额外模型请求，也不改变工具权限。</summary>
    public static class AssistantSkillCatalog
    {
        const string ResourcePrefix = "JianghuYouling.Core.AssistantSkills.";
        const string ResourceSuffix = ".SKILL.md";

        public sealed class Selection
        {
            public string Id { get; internal set; }
            public string Instructions { get; internal set; }
        }

        sealed class Skill
        {
            public string Id;
            public string Description;
            public string[] Triggers;
            public string Body;
        }

        static readonly Skill[] All = LoadAll();

        /// <summary>
        /// 为省略主语的短追问保留一轮主题粘性。当前问题能独立命中时绝不混入旧主题，
        /// 只有“那远程呢/这个怎么用”一类短追问完全未命中时才复用上一条用户问题。
        /// </summary>
        public static IReadOnlyList<Selection> Select(string input, string previousUserInput, int maxSkills = 5)
        {
            IReadOnlyList<Selection> current = Select(input, maxSkills);
            if (current.Count > 0 || !LooksLikeShortFollowUp(input)) return current;
            return Select(previousUserInput, maxSkills);
        }

        public static IReadOnlyList<Selection> Select(string input, int maxSkills = 5)
        {
            string query = (input ?? string.Empty).Trim();
            if (query.Length == 0 || maxSkills <= 0) return Array.Empty<Selection>();
            bool broad = ContainsAny(query, "全部功能", "所有功能", "能做什么", "功能介绍", "完整功能", "怎么玩", "使用说明");
            var scored = new List<Tuple<Skill, int>>();
            foreach (Skill skill in All)
            {
                int score = 0;
                foreach (string trigger in skill.Triggers)
                    if (query.IndexOf(trigger, StringComparison.OrdinalIgnoreCase) >= 0)
                        score += Math.Max(2, Math.Min(12, trigger.Length));
                if (broad && skill.Id == "mod-overview") score += 100;
                if (broad && skill.Id != "storage-troubleshooting") score += 20;
                if (score > 0) scored.Add(Tuple.Create(skill, score));
            }
            if (scored.Count == 0 && ContainsAny(query, "江湖有灵", "mod", "灵儿"))
            {
                Skill overview = All.FirstOrDefault(x => x.Id == "mod-overview");
                if (overview != null) scored.Add(Tuple.Create(overview, 1));
            }
            int take = broad ? All.Length : maxSkills;
            return scored.OrderByDescending(x => x.Item2).ThenBy(x => x.Item1.Id, StringComparer.Ordinal)
                .Take(Math.Min(take, All.Length)).Select(x => new Selection
                {
                    Id = x.Item1.Id,
                    Instructions = "【灵儿按需技能：" + x.Item1.Description + "】\n" + x.Item1.Body,
                }).ToArray();
        }

        public static IReadOnlyList<string> SkillIds => All.Select(x => x.Id).ToArray();

        public static bool TryGet(string id, out string instructions)
        {
            Skill skill = All.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
            instructions = skill == null ? null : skill.Body;
            return skill != null;
        }

        static Skill[] LoadAll()
        {
            Assembly assembly = typeof(AssistantSkillCatalog).Assembly;
            string[] resources = assembly.GetManifestResourceNames()
                .Where(x => x.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                    && x.EndsWith(ResourceSuffix, StringComparison.Ordinal))
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (resources.Length == 0) throw new InvalidOperationException("没有找到嵌入的灵儿知识技能");
            var result = new List<Skill>();
            foreach (string resource in resources)
            {
                using (Stream stream = assembly.GetManifestResourceStream(resource))
                using (var reader = stream == null ? null : new StreamReader(stream, new UTF8Encoding(false, true), true))
                {
                    if (reader == null) throw new InvalidOperationException("无法读取灵儿知识技能: " + resource);
                    result.Add(Parse(reader.ReadToEnd(), resource));
                }
            }
            string duplicate = result.GroupBy(x => x.Id, StringComparer.Ordinal)
                .Where(x => x.Count() > 1).Select(x => x.Key).FirstOrDefault();
            if (duplicate != null) throw new InvalidOperationException("灵儿知识技能 id 重复: " + duplicate);
            return result.ToArray();
        }

        static Skill Parse(string source, string resource)
        {
            string text = (source ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            if (!text.StartsWith("---\n", StringComparison.Ordinal))
                throw new InvalidOperationException("灵儿知识技能缺少 frontmatter: " + resource);
            int end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            if (end < 0) throw new InvalidOperationException("灵儿知识技能 frontmatter 未闭合: " + resource);
            var meta = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in text.Substring(4, end - 4).Split('\n'))
            {
                int separator = raw.IndexOf(':');
                if (separator <= 0) continue;
                meta[raw.Substring(0, separator).Trim()] = raw.Substring(separator + 1).Trim();
            }
            string id = meta.ContainsKey("name") ? meta["name"] : null;
            string description = meta.ContainsKey("description") ? meta["description"] : null;
            string triggers = meta.ContainsKey("triggers") ? meta["triggers"] : null;
            string body = text.Substring(end + 5).Trim();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(description)
                || string.IsNullOrWhiteSpace(triggers) || string.IsNullOrWhiteSpace(body))
                throw new InvalidOperationException("灵儿知识技能字段不完整: " + resource);
            return new Skill
            {
                Id = id,
                Description = description,
                Triggers = triggers.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Body = body,
            };
        }

        static bool ContainsAny(string value, params string[] terms)
        {
            foreach (string term in terms)
                if (value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        static bool LooksLikeShortFollowUp(string input)
        {
            string value = (input ?? string.Empty).Trim();
            if (value.Length == 0 || value.Length > 48) return false;
            return ContainsAny(value, "那", "这个", "这种", "这样", "它", "呢", "的话", "还有", "然后",
                "远程", "当面", "为什么", "怎么", "可以吗", "能吗", "行吗", "具体", "继续");
        }
    }
}
