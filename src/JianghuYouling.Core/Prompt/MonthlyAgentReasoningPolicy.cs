using System;
using System.Collections.Generic;
using JianghuYouling.Core.Llm;

namespace JianghuYouling.Core.Prompt
{
    /// <summary>
    /// Chooses the monthly agent's reasoning cost from the latest authoritative receipt.
    /// Only a successful tool receipt may enter the lightweight continuation path. Initial
    /// planning, prose/correction messages and any uncertain or failed receipt retain the
    /// provider's full reasoning policy.
    /// </summary>
    public static class MonthlyAgentReasoningPolicy
    {
        public static LlmReasoningPolicy Select(IList<LlmMessage> messages, int round)
        {
            if (round <= 0 || messages == null || messages.Count == 0)
                return LlmReasoningPolicy.Auto;

            // One assistant turn may emit several parallel reads. The next plan may use the
            // lightweight path only when every receipt in that contiguous tool-result group
            // is explicitly successful. A later success must never hide an earlier failure.
            bool sawToolReceipt = false;
            for (int index = messages.Count - 1; index >= 0; index--)
            {
                LlmMessage message = messages[index];
                if (message == null || !string.Equals(message.Role, "tool",
                        StringComparison.Ordinal))
                    break;
                sawToolReceipt = true;
                if (!IsExplicitReliableSuccess(message.Content))
                    return LlmReasoningPolicy.Auto;
            }
            return sawToolReceipt ? LlmReasoningPolicy.Low : LlmReasoningPolicy.Auto;
        }

        private static bool IsExplicitReliableSuccess(string content)
        {
            string value = (content ?? string.Empty).TrimStart();
            return value.StartsWith("OK:", StringComparison.Ordinal)
                || value.StartsWith("成功:", StringComparison.Ordinal)
                || value.StartsWith("权威查询缓存命中：", StringComparison.Ordinal)
                || value.StartsWith("本轮权威查询（", StringComparison.Ordinal)
                || value.StartsWith("复用本轮最近一次相同权威查询", StringComparison.Ordinal);
        }

        public static bool ShouldRetryWithFullReasoning(LlmReasoningPolicy policy,
            LlmToolResult result, bool cancellationRequested)
            => policy == LlmReasoningPolicy.Low
                && !cancellationRequested
                && (result == null || !result.Ok && !result.Canceled);
    }
}
