using System;

namespace JianghuYouling.Core.Behavior
{
    /// <summary>
    /// Recovery policy for the durable monthly-digest attempt counter.
    /// Reaching the retry limit is terminal for network work: recovery may only finish
    /// the completion checkpoint and must never lower the counter to make another attempt.
    /// </summary>
    public static class MonthlyDigestAttemptPolicy
    {
        public static int NormalizeRecoveredAttempts(int priorAttempts, int retryLimit)
        {
            if (retryLimit < 0) throw new ArgumentOutOfRangeException(nameof(retryLimit));
            return Math.Max(0, Math.Min(retryLimit, priorAttempts));
        }

        public static bool MayRunNetwork(int consumedAttempts, int retryLimit)
            => retryLimit > 0
                && NormalizeRecoveredAttempts(consumedAttempts, retryLimit) < retryLimit;
    }
}
