using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using JianghuYouling.Core.Llm;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Memory
{
    /// <summary>
    /// 仿 Claude 记忆机制的【索引】:把每条记忆渲染成一行(编号 + 类型 + 第一人称一句话)。
    /// 召回 = 把索引整体载入上下文,让扮演用的 LLM 自己读索引、判断此刻该想起哪些(语义判断,非关键词匹配)。
    /// 仅当索引大到塞不下时,才用一次轻量"选取"调用让模型从索引里挑相关编号(Claude 式"读索引→取相关")。
    /// 纯逻辑,可单测。
    /// </summary>
    public static class MemoryIndex
    {
        private const int MonthlyOutcomePromptMaxChars = 2200;
        private const int MonthlyOutcomePromptMaxLineChars = 240;

        /// <summary>渲染成 prompt 行(无编号),供整体载入上下文。now=当下世界月计数(&gt;0 时给每条记忆标"距今多久",让 NPC 有时间概念)。</summary>
        public static List<string> Render(IList<MemoryEntry> entries, long now = 0)
        {
            var lines = new List<string>();
            if (entries == null) return lines;
            foreach (var e in entries)
                if (e != null && !string.IsNullOrWhiteSpace(e.Content))
                    lines.Add(AgoTag(e.WorldDate, now) + "[" + TypeLabel(e.Type) + "] " + e.Content.Trim());
            return lines;
        }

        /// <summary>
        /// Render memory for an LLM prompt. Durable memory remains byte-for-byte complete, while
        /// potentially large monthly receipt collections are projected to bounded fact lines so
        /// one old month cannot dominate every later chat, group or monthly request.
        /// </summary>
        public static List<string> RenderForPrompt(IList<MemoryEntry> entries, long now = 0)
        {
            var lines = new List<string>();
            if (entries == null) return lines;
            foreach (var entry in entries)
            {
                string content = ProjectContentForPrompt(entry);
                if (string.IsNullOrWhiteSpace(content)) continue;
                lines.Add(AgoTag(entry.WorldDate, now) + "[" + TypeLabel(entry.Type) + "] "
                    + content);
            }
            return lines;
        }

        /// <summary>
        /// Produces a bounded model-facing projection for receipt-heavy monthly memories without
        /// modifying the persisted entry. Other memory kinds are returned unchanged.
        /// </summary>
        public static string ProjectContentForPrompt(MemoryEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Content)) return string.Empty;
            string content = entry.Content.Trim();
            bool companion = string.Equals(entry.SourceKind, "companion_monthly_outcomes",
                StringComparison.Ordinal);
            bool eventFanout = string.Equals(entry.SourceKind, "monthly_event_fanout",
                StringComparison.Ordinal);
            if (!companion && !eventFanout) return content;

            string[] rawLines = content.Replace("\r", string.Empty)
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var facts = new List<string>();
            foreach (string raw in rawLines)
            {
                string fact = (raw ?? string.Empty).Trim();
                if (fact.Length == 0
                    || fact.StartsWith("本月真实工具结果", StringComparison.Ordinal)
                    || fact.StartsWith("本月江湖事件真实工具结果", StringComparison.Ordinal))
                    continue;
                if (fact.StartsWith("- ", StringComparison.Ordinal)) fact = fact.Substring(2).Trim();
                if (fact.Length > MonthlyOutcomePromptMaxLineChars)
                    fact = TruncateWithEllipsis(fact,
                        MonthlyOutcomePromptMaxLineChars);
                if (fact.Length > 0) facts.Add(fact);
            }
            if (facts.Count == 0)
                return TruncateWithEllipsis(content, MonthlyOutcomePromptMaxChars);

            var projected = new StringBuilder(companion
                ? "本月真实工具结果（模型用紧凑投影；完整回执已耐久保存）："
                : "本月江湖事件真实工具结果（模型用紧凑投影；完整回执已耐久保存）：");
            int included = 0;
            for (int i = 0; i < facts.Count; i++)
            {
                string line = "\n- " + facts[i];
                int omittedAfterAppend = facts.Count - included - 1;
                string footerAfterAppend = omittedAfterAppend > 0
                    ? BuildOmittedFooter(omittedAfterAppend) : string.Empty;
                if (projected.Length + line.Length + footerAfterAppend.Length
                    > MonthlyOutcomePromptMaxChars) break;
                projected.Append(line);
                included++;
            }
            int omitted = facts.Count - included;
            if (omitted > 0)
                projected.Append(BuildOmittedFooter(omitted));
            return TruncateWithEllipsis(projected.ToString(),
                MonthlyOutcomePromptMaxChars);
        }

        private static string BuildOmittedFooter(int omitted)
            => "\n- （另有" + Math.Max(0, omitted)
                + "项完整工具回执仅在耐久记忆中保留）";

        private static string TruncateWithEllipsis(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
                return value ?? string.Empty;
            if (maxChars <= 1) return "…".Substring(0, Math.Max(0, maxChars));
            int contentLength = maxChars - 1;
            if (contentLength > 0 && contentLength < value.Length
                && char.IsHighSurrogate(value[contentLength - 1])
                && char.IsLowSurrogate(value[contentLength]))
                contentLength--;
            return value.Substring(0, contentLength) + "…";
        }

        // 距今多久(世界月计数差)→ 可读前缀,让 NPC 知道某事是新近还是陈年旧事。now&lt;=0 或无有效日期 → 不标,免瞎标。
        internal static string AgoTag(long worldDate, long now)
        {
            if (now <= 0 || worldDate <= 0) return "";
            long gap = now - worldDate;
            if (gap <= 0) return "(本月)";
            if (gap == 1) return "(上月)";
            if (gap < 12) return "(约" + gap + "月前)";
            return "(约" + (gap / 12) + "年前)";
        }

        /// <summary>渲染成带编号的索引文本,供模型按编号选取。</summary>
        public static string RenderNumbered(IList<MemoryEntry> entries)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e == null || string.IsNullOrWhiteSpace(e.Content)) continue;
                sb.Append(i + 1).Append(". [").Append(TypeLabel(e.Type)).Append("] ").Append(e.Content.Trim()).Append('\n');
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Render a numbered prompt index using the same bounded receipt projection.</summary>
        public static string RenderNumberedForPrompt(IList<MemoryEntry> entries)
        {
            var sb = new StringBuilder();
            if (entries == null) return string.Empty;
            for (int i = 0; i < entries.Count; i++)
            {
                MemoryEntry entry = entries[i];
                string content = ProjectContentForPrompt(entry);
                if (string.IsNullOrWhiteSpace(content)) continue;
                sb.Append(i + 1).Append(". [").Append(TypeLabel(entry.Type)).Append("] ")
                    .Append(content).Append('\n');
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>组"从索引里选取此刻相关编号"的消息(仅索引超预算时用)。</summary>
        public static List<LlmMessage> BuildSelectMessages(string indexText, string topic, int maxPick)
        {
            var msgs = new List<LlmMessage>();
            msgs.Add(LlmMessage.System(
@"你是一名《太吾绘卷》角色。下面是你记得的一切的【索引】,每条一个编号。你正与太吾交谈。
请像真人回忆那样,挑出【此刻会在你脑中浮现、与当前话题或情境相关】的条目编号:沾边的、能影响你态度的都可纳入,但别硬凑无关的。
只回一个 JSON 数字数组,例如 [3,7,12];最多 " + maxPick + " 个;若实在无相关条目,回 []。不要任何解释。"));
            msgs.Add(LlmMessage.User("【记忆索引】\n" + indexText + "\n\n【当前话题】" + (string.IsNullOrWhiteSpace(topic) ? "(太吾默然)" : topic)));
            return msgs;
        }

        private static readonly Regex Num = new Regex("\\d+");
        private static readonly Regex Arr = new Regex(@"\[[\d,\s]*\]");   // 首个 JSON 数字数组

        /// <summary>从模型回复里抽取编号(1..count),去重,限 maxPick 个。
        /// 只在【首个 [..] 数组】内取数,避免把模型 reasoning/解释里的数字(年龄/年份/好感数等)误当成记忆编号。</summary>
        public static List<int> ParseSelection(string reply, int count, int maxPick)
        {
            if (TryParseSelection(reply, count, maxPick, out var parsed)) return parsed;

            // 兼容旧 provider 的非数组回复；新的记忆路由会用 TryParseSelection 区分
            // “合法空数组”与“没有给出数组”，不会再把 [] 错当成调用失败。
            var res = new List<int>();
            if (string.IsNullOrEmpty(reply)) return res;
            var am = Arr.Match(reply);
            string scope = am.Success ? am.Value : reply;   // 优先首个数组;无数组才退回全文(兜底旧式回复)
            var seen = new HashSet<int>();
            foreach (Match m in Num.Matches(scope))
            {
                if (res.Count >= maxPick) break;
                if (int.TryParse(m.Value, out int n) && n >= 1 && n <= count && seen.Add(n)) res.Add(n);
            }
            return res;
        }

        /// <summary>
        /// 严格识别模型是否真的返回了一个有效编号数组。返回 true 时 selection 可以为空：
        /// [] 是“没有相关记忆”的有效决定；非数组、非整数或越界编号才是畸形输出。
        /// </summary>
        public static bool TryParseSelection(string reply, int count, int maxPick, out List<int> selection)
        {
            selection = new List<int>();
            if (string.IsNullOrWhiteSpace(reply) || count < 0 || maxPick < 0) return false;
            var match = Arr.Match(reply);
            if (!match.Success) return false;

            JArray array;
            try { array = JArray.Parse(match.Value); }
            catch { return false; }

            var seen = new HashSet<int>();
            foreach (var token in array)
            {
                if (token == null || token.Type != JTokenType.Integer) return false;
                int number;
                try { number = token.Value<int>(); }
                catch { return false; }
                if (number < 1 || number > count) return false;
                if (selection.Count < maxPick && seen.Add(number)) selection.Add(number);
            }
            return true;
        }

        public static string TypeLabel(MemoryType t)
        {
            switch (t)
            {
                case MemoryType.Favor: return "恩情";
                case MemoryType.Grudge: return "仇怨";
                case MemoryType.Promise: return "承诺";
                case MemoryType.Secret: return "秘闻";
                case MemoryType.Event: return "经历";
                default: return "印象";
            }
        }
    }
}
