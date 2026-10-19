using System;
using System.Collections.Generic;
using JianghuYouling.Core.Prompt;

namespace JianghuYouling
{
    /// <summary>纯显示顺序：累计梗概跟在已压缩原文后、未压缩新消息前，不改变模型上下文。</summary>
    internal static class ConversationHistoryLayout
    {
        internal static int SummaryInsertionIndex(IReadOnlyList<TalkTurn> complete,
            IReadOnlyList<TalkTurn> live)
        {
            if (complete == null) return 0;
            var liveIds = new HashSet<string>(StringComparer.Ordinal);
            var liveWithoutId = new List<TalkTurn>();
            if (live != null)
                foreach (var turn in live)
                {
                    if (turn == null) continue;
                    if (!string.IsNullOrEmpty(turn.Id)) liveIds.Add(turn.Id);
                    else liveWithoutId.Add(turn);
                }
            for (int i = 0; i < complete.Count; i++)
            {
                TalkTurn turn = complete[i];
                if (turn == null) continue;
                if (!string.IsNullOrEmpty(turn.Id))
                {
                    if (liveIds.Contains(turn.Id)) return i;
                }
                else if (liveWithoutId.Exists(current => current.FromPlayer == turn.FromPlayer
                    && current.Date == turn.Date
                    && string.Equals(current.Kind, turn.Kind, StringComparison.Ordinal)
                    && string.Equals(current.Text, turn.Text, StringComparison.Ordinal))) return i;
            }
            // 手动总结覆盖了全部 live turns：把新梗概显示在所有原文之后，而不是屏幕之外的顶部。
            return complete.Count;
        }
    }
}
