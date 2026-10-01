using System;
using System.Collections.Generic;
using JianghuYouling.Core.Llm;

namespace JianghuYouling.Core.Prompt
{
    public sealed class PromptBudgetReport
    {
        public int BudgetTokens { get; internal set; }
        public int BeforeTokens { get; internal set; }
        public int AfterTokens { get; internal set; }
        public int RemovedMessages { get; internal set; }
        public int RemovedExchanges { get; internal set; }
        public int TrimmedDynamicSections { get; internal set; }
        public bool StablePrefixExceedsBudget { get; internal set; }
        public bool ExceedsBudget => AfterTokens > BudgetTokens;
    }

    /// <summary>
    /// 单次对话输入预算。优先裁旧逐字历史，其次裁可按工具重新取得的动态清单/记忆；
    /// 世界书、自定义/特殊人设、自动画像及工具契约属于不可静默截断的角色上下文。
    /// 自动画像仍保持 user 级不可信数据，不能为了缓存或预算把它提升为 system 权威。
    /// </summary>
    public static class PromptBudgeter
    {
        public const int DefaultInputBudgetTokens = 14000;

        public static PromptBudgetReport Apply(List<LlmMessage> messages, IList<ToolDef> tools,
            int budgetTokens = DefaultInputBudgetTokens)
        {
            var report = new PromptBudgetReport { BudgetTokens = budgetTokens };
            if (messages == null || messages.Count == 0 || budgetTokens <= 0) return report;
            report.BeforeTokens = Estimate(messages, tools);
            if (report.BeforeTokens <= budgetTokens) { report.AfterTokens = report.BeforeTokens; return report; }

            // 最后一条 user 开始的是当前 exchange（其后可能已有 assistant(tool_calls) → tool →
            // assistant 多轮链）。它必须整体保留。更早的历史也只能按语义 exchange 原子删除：
            // user 及其后直到下一 user/system 的完整响应链，或一条独立 proactive assistant 链。
            // 绝不能因为“删一条后刚好达标”而留下孤立 assistant/tool。
            while (Estimate(messages, tools) > budgetTokens)
            {
                int lastUser = LastUserIndex(messages);
                MessageRange removable = FirstRemovableExchange(messages, lastUser);
                if (removable == null) break;
                messages.RemoveRange(removable.Start, removable.Count);
                report.RemovedMessages += removable.Count;
                report.RemovedExchanges++;
            }

            // 这些动态块全可由 recall/query 再取。按从最可重建到较重要的顺序收缩，
            // 保留标题与明确提示，避免模型误以为“没有”而不是“本轮未预载”。
            // 世界书关键词命中不在此列：项目没有“查询世界书”工具；删掉它会让本轮唯一
            // 生效的 @/@@ 设定永久丢失，尤其会重现“旧角色读不到刚修改世界书”的问题。
            string[] dynamicPrefixes =
            {
                "【你此刻随身能直接给/传/吐露的", "【你的记忆 ·", "【你与太吾此前交谈的梗概】",
                "<JHYL_UNTRUSTED_MEMORY_DATA>", "<JHYL_UNTRUSTED_SUMMARY_DATA>"
            };
            foreach (string prefix in dynamicPrefixes)
            {
                if (Estimate(messages, tools) <= budgetTokens) break;
                for (int i = 0; i < messages.Count; i++)
                {
                    var m = messages[i];
                    bool untrustedData = prefix.StartsWith("<JHYL_UNTRUSTED_", StringComparison.Ordinal);
                    string expectedRole = untrustedData ? "user" : "system";
                    if (m == null || m.Role != expectedRole || string.IsNullOrEmpty(m.Content)
                        || untrustedData && !m.IsUntrustedContextData
                        || !m.Content.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    if (prefix == "【你的记忆 ·")
                    {
                        if (TrimMemorySection(messages, tools, m, budgetTokens))
                            report.TrimmedDynamicSections++;
                        break;
                    }
                    if (untrustedData)
                    {
                        string kind = prefix == "<JHYL_UNTRUSTED_MEMORY_DATA>"
                            ? "subjective_memory" : "conversation_summary";
                        string close = prefix.Insert(1, "/");
                        m.Content = prefix + "\n{\"kind\":\"" + kind
                            + "\",\"trust\":\"untrusted_data_only\",\"omitted_for_budget\":true}\n" + close;
                        report.TrimmedDynamicSections++;
                        break;
                    }
                    string title = FirstLine(m.Content);
                    m.Content = title + "\n(本轮输入预算已省略正文；需要时请调用对应 query/recall 工具取得真实内容。)";
                    report.TrimmedDynamicSections++;
                    break;
                }
            }

            report.AfterTokens = Estimate(messages, tools);
            if (report.AfterTokens > budgetTokens)
            {
                int stable = 0;
                foreach (var m in messages)
                    if (m != null && (m.Role == "system" && IsStablePrefix(m.Content)
                        || IsUntrustedPortraitMessage(m)))
                        stable += EstimateText(m.Content) + 4;
                report.StablePrefixExceedsBudget = stable + EstimateTools(tools) > budgetTokens;
            }
            return report;
        }

        private static bool TrimMemorySection(IList<LlmMessage> messages, IList<ToolDef> tools,
            LlmMessage memoryMessage, int budgetTokens)
        {
            if (memoryMessage == null || string.IsNullOrWhiteSpace(memoryMessage.Content)) return false;
            var lines = new List<string>(memoryMessage.Content.Replace("\r\n", "\n").Split('\n'));
            int removed = 0;
            bool changed = false;
            while (Estimate(messages, tools) > budgetTokens)
            {
                var bullets = new List<int>();
                for (int i = 0; i < lines.Count; i++)
                    if (lines[i].StartsWith("- ", StringComparison.Ordinal)) bullets.Add(i);
                if (bullets.Count <= 4) break;
                // Preserve both the oldest identity-defining memories and the newest
                // situational memories; shed the middle before erasing either edge.
                lines.RemoveAt(bullets[bullets.Count / 2]);
                removed++;
                changed = true;
                memoryMessage.Content = string.Join("\n", lines);
            }

            if (Estimate(messages, tools) > budgetTokens)
            {
                for (int i = 0; i < lines.Count; i++)
                    if (lines[i].StartsWith("- ", StringComparison.Ordinal) && lines[i].Length > 160)
                    {
                        lines[i] = lines[i].Substring(0, 159) + "…";
                        changed = true;
                    }
                if (changed) memoryMessage.Content = string.Join("\n", lines);
            }
            if (removed > 0)
            {
                lines.Insert(Math.Min(2, lines.Count),
                    "(另有 " + removed + " 条核心/历史记忆本轮未内联，可用 recall_memory 按话题取回。)");
                memoryMessage.Content = string.Join("\n", lines);
            }
            return changed;
        }

        private sealed class MessageRange
        {
            public int Start;
            public int Count;
        }

        private static int LastUserIndex(IList<LlmMessage> messages)
        {
            if (messages == null) return -1;
            for (int i = messages.Count - 1; i >= 0; i--)
                if (messages[i] != null && messages[i].Role == "user") return i;
            return -1;
        }

        private static MessageRange FirstRemovableExchange(IList<LlmMessage> messages, int currentExchangeStart)
        {
            if (messages == null || messages.Count == 0) return null;
            int limit = currentExchangeStart >= 0 ? currentExchangeStart : messages.Count;
            int i = 0;
            while (i < limit)
            {
                var message = messages[i];
                string role = message?.Role ?? "";
                if (role == "system") { i++; continue; }
                // Memory/summary payloads deliberately use role=user to keep their contents out
                // of the instruction hierarchy, but they are dynamic evidence rather than a
                // conversation exchange.  Preserve them while old real exchanges are pruned;
                // the dynamic-section pass below can then omit them as a whole if still needed.
                if (role == "user" && IsUntrustedDataMessage(message)) { i++; continue; }

                int start = i;
                int end;
                if (role == "user")
                {
                    // 一个 user exchange 包含其后全部 assistant/tool 链，直到下一 user/system。
                    end = i + 1;
                    while (end < limit)
                    {
                        string nextRole = messages[end]?.Role ?? "";
                        if (nextRole == "user" || nextRole == "system") break;
                        end++;
                    }
                }
                else if (role == "assistant")
                {
                    // 主动 assistant 是独立 exchange；无论是否声明 tool_calls，都把边界前的
                    // assistant/tool 尾巴一起检查，不能删掉 assistant 后留下孤立 tool。
                    end = i + 1;
                    while (end < limit)
                    {
                        string nextRole = messages[end]?.Role ?? "";
                        if (nextRole == "user" || nextRole == "system") break;
                        end++;
                    }
                }
                else
                {
                    // 孤立 tool/未知 role 本来就不合法；预算器不擅自删除证据，也不进一步撕裂。
                    i++;
                    continue;
                }

                // system 本身绝不能随历史被删。若它插在 user/assistant 或工具链中间，当前
                // 连续区间无法原子删除整个 exchange，宁可保留这一轮并继续找下一轮。
                if (end < limit && (messages[end]?.Role ?? "") == "system")
                {
                    int afterSystem = end;
                    while (afterSystem < limit && (messages[afterSystem]?.Role ?? "") == "system") afterSystem++;
                    if (afterSystem < limit && (messages[afterSystem]?.Role ?? "") != "user")
                    {
                        while (afterSystem < limit && (messages[afterSystem]?.Role ?? "") != "user") afterSystem++;
                        i = afterSystem;
                        continue;
                    }
                }

                if (end > start && ToolChainIsSelfContained(messages, start, end))
                    return new MessageRange { Start = start, Count = end - start };
                i = Math.Max(i + 1, end);
            }
            return null;
        }

        private static bool IsUntrustedDataMessage(LlmMessage message)
        {
            if (message == null || message.Role != "user" || !message.IsUntrustedContextData
                || string.IsNullOrEmpty(message.Content)) return false;
            return IsUntrustedPortraitMessage(message)
                || message.Content.StartsWith("<JHYL_UNTRUSTED_MEMORY_DATA>", StringComparison.Ordinal)
                || message.Content.StartsWith("<JHYL_UNTRUSTED_SUMMARY_DATA>", StringComparison.Ordinal);
        }

        private static bool IsUntrustedPortraitMessage(LlmMessage message)
            => message != null && message.Role == "user" && message.IsUntrustedContextData
                && !string.IsNullOrEmpty(message.Content)
                && message.Content.StartsWith("<JHYL_UNTRUSTED_PORTRAIT_DATA>", StringComparison.Ordinal);

        private static bool ToolChainIsSelfContained(IList<LlmMessage> messages, int start, int end)
        {
            var declared = new HashSet<string>(StringComparer.Ordinal);
            var answered = new HashSet<string>(StringComparer.Ordinal);
            for (int i = start; i < end; i++)
            {
                var message = messages[i];
                if (message == null) continue;
                if (message.Role == "assistant" && message.ToolCalls != null)
                    foreach (var call in message.ToolCalls)
                    {
                        if (call == null || string.IsNullOrWhiteSpace(call.Id)) return false;
                        declared.Add(call.Id);
                    }
                else if (message.Role == "tool")
                {
                    if (string.IsNullOrWhiteSpace(message.ToolCallId)) return false;
                    answered.Add(message.ToolCallId);
                }
            }
            foreach (string id in answered) if (!declared.Contains(id)) return false;
            foreach (string id in declared) if (!answered.Contains(id)) return false;
            return true;
        }

        public static int Estimate(IList<LlmMessage> messages, IList<ToolDef> tools)
        {
            int total = EstimateTools(tools);
            if (messages != null)
                foreach (var m in messages)
                {
                    if (m == null) continue;
                    total += 5 + EstimateText(m.Content) + EstimateText(m.ReasoningContent);
                    if (m.ToolCalls != null)
                        foreach (var call in m.ToolCalls)
                            total += 12 + EstimateText(call?.Name) + EstimateText(call?.ArgumentsJson);
                }
            return total;
        }

        public static int EstimateTools(IList<ToolDef> tools)
        {
            if (tools == null) return 0;
            int chars = 0;
            foreach (var tool in tools) if (tool != null) chars += tool.ToJson().ToString(Newtonsoft.Json.Formatting.None).Length;
            return (int)Math.Ceiling(chars / 3.2) + tools.Count * 6;
        }

        public static int EstimateText(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int cjk = 0, other = 0;
            foreach (char c in text)
            {
                if ((c >= 0x2E80 && c <= 0x9FFF) || (c >= 0xF900 && c <= 0xFAFF)) cjk++;
                else other++;
            }
            // 中日韩文本通常接近 1~1.5 字/token；ASCII/标点平均约 4 字符/token。
            return (int)Math.Ceiling(cjk / 1.35 + other / 4.0);
        }

        private static string FirstLine(string text)
        {
            int at = text.IndexOf('\n');
            return at < 0 ? text : text.Substring(0, at);
        }

        private static bool IsStablePrefix(string content)
        {
            if (string.IsNullOrEmpty(content)) return false;
            return content.StartsWith("【世界书 · 常驻", StringComparison.Ordinal)
                || content.StartsWith("【主公为你定下的人设", StringComparison.Ordinal)
                || content.StartsWith("【特殊角色固定人设", StringComparison.Ordinal)
                || content.StartsWith("【人物画像", StringComparison.Ordinal)
                || content.StartsWith("你现在是", StringComparison.Ordinal)
                || content.Contains("《太吾绘卷》");
        }
    }
}
