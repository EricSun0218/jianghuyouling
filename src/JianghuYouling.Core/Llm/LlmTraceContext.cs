using System;

namespace JianghuYouling.Core.Llm
{
    /// <summary>
    /// One immutable, explicitly-passed LLM trajectory coordinate.  It deliberately contains only
    /// bounded scalar identifiers: prompts, replies and tool arguments never belong in metrics.
    /// No ambient/static "current trace" is used, because Unity coroutines and parallel group turns
    /// can interleave on the same thread.
    /// </summary>
    public sealed class LlmTraceContext
    {
        public string RunId { get; }
        public string ConversationId { get; }
        public string ExchangeId { get; }
        public string AttemptId { get; }
        public int? Round { get; }
        public string OperationId { get; }
        public string Tool { get; }
        public string Outcome { get; }

        public LlmTraceContext(string runId = null, string conversationId = null,
            string exchangeId = null, string attemptId = null, int? round = null,
            string operationId = null, string tool = null, string outcome = null)
        {
            RunId = Normalize(runId, 96);
            ConversationId = Normalize(conversationId, 96);
            ExchangeId = Normalize(exchangeId, 96);
            AttemptId = Normalize(attemptId, 96);
            Round = round >= 0 && round <= 1024 ? round : null;
            OperationId = Normalize(operationId, 96);
            Tool = Normalize(tool, 192);
            Outcome = Normalize(outcome, 48);
        }

        public bool IsEmpty => string.IsNullOrEmpty(RunId) && string.IsNullOrEmpty(ConversationId)
            && string.IsNullOrEmpty(ExchangeId) && string.IsNullOrEmpty(AttemptId) && !Round.HasValue
            && string.IsNullOrEmpty(OperationId) && string.IsNullOrEmpty(Tool)
            && string.IsNullOrEmpty(Outcome);

        public static LlmTraceContext NewRun(string conversationId = null, string exchangeId = null)
            => new LlmTraceContext(Guid.NewGuid().ToString("N"), conversationId, exchangeId);

        public LlmTraceContext WithRound(int round, string attemptId = null)
            => new LlmTraceContext(RunId, ConversationId, ExchangeId,
                attemptId ?? AttemptId, round, OperationId, Tool, Outcome);

        public LlmTraceContext WithOperation(string operationId, string tool = null)
            => new LlmTraceContext(RunId, ConversationId, ExchangeId, AttemptId, Round,
                operationId ?? OperationId, tool ?? Tool, Outcome);

        internal LlmTraceContext WithObservedResult(string tool, string outcome)
            => new LlmTraceContext(RunId, ConversationId, ExchangeId, AttemptId, Round,
                OperationId, string.IsNullOrWhiteSpace(tool) ? Tool : tool,
                string.IsNullOrWhiteSpace(outcome) ? Outcome : outcome);

        private static string Normalize(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string safe = SecretRedactor.Redact(value).Trim();
            var chars = safe.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (char.IsControl(chars[i])) chars[i] = '_';
            safe = new string(chars);
            return safe.Length <= maxLength ? safe : safe.Substring(0, maxLength);
        }
    }
}
