using System;
using System.Threading.Tasks;
using JianghuYouling.Core.Llm;

namespace JianghuYouling.Core.Prompt
{
    /// <summary>
    /// Executes the one permitted recovery attempt after a lightweight monthly-agent
    /// continuation fails. Both monthly workflows share this coordinator so a recovered
    /// result cannot be forgotten or followed by an accidental second retry.
    /// </summary>
    public static class MonthlyAgentRoundRecovery
    {
        public sealed class Outcome
        {
            public LlmToolResult Result { get; internal set; }
            public bool RetryAttempted { get; internal set; }
            public bool RetryBudgetDenied { get; internal set; }
            public bool CanceledBeforeRetry { get; internal set; }
            public string RetryExceptionType { get; internal set; }
        }

        public static async Task<Outcome> RecoverAsync(
            LlmReasoningPolicy initialPolicy,
            LlmToolResult initialResult,
            Func<bool> shouldContinue,
            Func<bool> tryAcquireRecoveryBudget,
            Func<LlmReasoningPolicy, Task<LlmToolResult>> sendRetry)
        {
            if (shouldContinue == null) throw new ArgumentNullException(nameof(shouldContinue));
            if (tryAcquireRecoveryBudget == null)
                throw new ArgumentNullException(nameof(tryAcquireRecoveryBudget));
            if (sendRetry == null) throw new ArgumentNullException(nameof(sendRetry));

            var outcome = new Outcome { Result = initialResult };
            bool canContinue = shouldContinue();
            if (!MonthlyAgentReasoningPolicy.ShouldRetryWithFullReasoning(initialPolicy,
                    initialResult, !canContinue))
            {
                outcome.CanceledBeforeRetry = !canContinue;
                return outcome;
            }

            if (!tryAcquireRecoveryBudget())
            {
                outcome.RetryBudgetDenied = true;
                return outcome;
            }
            if (!shouldContinue())
            {
                outcome.CanceledBeforeRetry = true;
                return outcome;
            }

            outcome.RetryAttempted = true;
            try
            {
                // The coordinator owns the recovery policy. The returned value is always
                // authoritative, including a failed result, and is never retried again.
                outcome.Result = await sendRetry(LlmReasoningPolicy.Auto);
            }
            catch (Exception exception)
            {
                outcome.Result = null;
                outcome.RetryExceptionType = exception.GetType().Name;
            }
            return outcome;
        }
    }
}
