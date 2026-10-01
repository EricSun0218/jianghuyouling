using System;
using System.Collections.Generic;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Prompt;

namespace JianghuYouling.Core.Tools
{
    /// <summary>跨前端/测试复用的工具调用启发式，避免把检索轮误判成动作承诺。</summary>
    public static class ToolCallHeuristics
    {
        // 低推理只允许用于语义完全自足、几乎不可能承接上一轮动作的寒暄。
        // 这里刻意不列任何功法、物品、动作或选择句式：所有可能依赖上下文的表达都交给
        // 已经拿到完整历史的主模型判断，避免本地规则替模型猜测“这句话究竟要做什么”。
        private static readonly HashSet<string> DefiniteStandaloneSmallTalk = new HashSet<string>(StringComparer.Ordinal)
        {
            "你好", "您好", "早安", "午安", "晚安", "早上好", "中午好", "下午好", "晚上好",
            "谢谢", "多谢", "辛苦了", "保重", "再见", "哈哈", "哈哈哈", "呵呵"
        };

        /// <summary>
        /// 仅在能够高置信确认当前输入是独立寒暄、最近上下文也没有待回答问题或工具结果时
        /// 才允许降低推理强度。返回 false 不代表一定要调用工具，只代表应让拿到完整上下文的
        /// 主模型自行判断；因此任何省略、指代、选项名、肯定/否定短答都会保守地使用完整推理。
        /// </summary>
        public static bool IsDefinitelyPureSmallTalk(string playerInput, IList<TalkTurn> history)
        {
            string current = NormalizeStandaloneText(playerInput);
            if (!DefiniteStandaloneSmallTalk.Contains(current)) return false;
            if (history == null || history.Count == 0) return true;

            for (int i = history.Count - 1; i >= 0; i--)
            {
                TalkTurn turn = history[i];
                if (turn == null || TalkTurnKinds.IsNative(turn) || turn.FromPlayer
                    || string.IsNullOrWhiteSpace(turn.Text)) continue;
                // 工具回执/真实动作刚发生时，即使玩家说“谢谢”，也可能是在承接该动作，
                // 后续回复需要理解上下文，而不是被入口层提前认定成纯闲聊。
                if ((turn.Actions != null && turn.Actions.Count > 0)
                    || (turn.ToolResults != null && turn.ToolResults.Count > 0)) return false;

                // 上一句含问号时，当前短语可能是对问题或选择的回答。至于是什么回答，
                // 由拥有完整历史与工具表的 Agent 判断，入口层不再枚举具体语义。
                string previous = turn.Text.Trim();
                if (previous.Contains("？") || previous.Contains("?")) return false;
                return true;
            }
            return true;
        }

        private static string NormalizeStandaloneText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return value.Trim().Trim('「', '」', '『', '』', '《', '》', '“', '”', '‘', '’', '。', '！', '!', ' ', '\t', '\r', '\n');
        }

        public static bool RequiresLandingConfirmation(IList<LlmToolCall> calls)
        {
            if (calls == null) return false;
            foreach (var c in calls)
            {
                if (c == null) continue;
                switch (c.Name)
                {
                    case "barter":
                    case "steal":
                    case "flip_practice":
                    case "gift":
                    case "taiwu_give_item":
                    case "taiwu_write_book":
                    case "trade":
                        return true;
                }
            }
            return false;
        }

        public static bool IsCurrentSpeakerName(string raw, string speakerName)
        {
            string t = NormalizePersonNameToken(raw);
            string n = NormalizePersonNameToken(speakerName);
            return t.Length > 0 && n.Length > 0 && string.Equals(t, n, StringComparison.Ordinal);
        }

        private static string NormalizePersonNameToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            return value.Trim()
                .Trim('「', '」', '『', '』', '《', '》', '"', '\'', '“', '”', '‘', '’', ' ', '\t', '\r', '\n');
        }
    }
}
