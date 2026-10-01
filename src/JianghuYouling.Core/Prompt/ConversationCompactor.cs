using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using JianghuYouling.Core.Llm;

namespace JianghuYouling.Core.Prompt
{
    /// <summary>
    /// 上下文压缩(仿 Claude):对话变长、逐字历史超预算时,把【更早的一段对话】+【已有梗概】压缩成
    /// 更新后的滚动梗概,保留关键事实/情绪/承诺/态度变化,删冗余寒暄。之后逐字只留最近若干轮 + 这份梗概。
    /// 纯逻辑,可单测。
    /// </summary>
    public static class ConversationCompactor
    {
        public const int MaxSummaryChars = 600;
        private static readonly Regex FenceMarker = new Regex("```(?:json|markdown|md|text|txt)?[ \\t]*\\r?\\n?|```", RegexOptions.IgnoreCase);

        public static List<LlmMessage> BuildMessages(string npcName, string priorSummary, IList<TalkTurn> turnsToCompact)
        {
            var msgs = new List<LlmMessage>();
            msgs.Add(LlmMessage.System(
@"你在维护【你与太吾的私聊，以及你参加过的群聊】的滚动梗概(供你日后回忆交流脉络)。
现给你:已有梗概 + 更早的一段对话原文。请输出【更新后的梗概】:

- 把更早对话里的关键内容并入已有梗概:谈了什么、各自态度、动了什么情绪、许了什么诺、起了什么嫌隙或恩义。
- 遇到“群聊实录”必须保留群聊属性和真实发言人，绝不能把其他群成员的话写成该 NPC 或太吾的私聊发言。
- 删去寒暄客套与重复;只留日后影响你与太吾相处的要点。
- 第三人称、一段成文、200 字内,不分点、不加标题、不要前后缀。"));

            var sb = new StringBuilder();
            sb.Append("【已有梗概】\n").Append(string.IsNullOrWhiteSpace(priorSummary) ? "(暂无)" : priorSummary.Trim()).Append("\n\n");
            sb.Append("【更早的一段对话】\n");
            if (turnsToCompact != null)
                foreach (var t in turnsToCompact)
                    if (t != null && !string.IsNullOrWhiteSpace(t.Text))
                        sb.Append(TalkTurnKinds.ContextSpeaker(t,
                            string.IsNullOrEmpty(npcName) ? "你" : npcName))
                            .Append(':').Append(t.Text.Trim()).Append('\n');
            sb.Append("\n请输出更新后的梗概。");

            msgs.Add(LlmMessage.User(sb.ToString()));
            return msgs;
        }

        public static string Clean(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            StreamingToolCollector.SplitThink(raw, out var visible, out _);
            string s = FenceMarker.Replace(visible ?? "", "");
            s = s.Trim();
            if (s.Length <= MaxSummaryChars) return s;
            int end = -1;
            for (int i = MaxSummaryChars - 1; i >= MaxSummaryChars / 2; i--)
                if ("。！？!?；;\n".IndexOf(s[i]) >= 0) { end = i + 1; break; }
            return s.Substring(0, end > 0 ? end : MaxSummaryChars).TrimEnd()
                + (end > 0 ? "" : "…");
        }
    }
}
