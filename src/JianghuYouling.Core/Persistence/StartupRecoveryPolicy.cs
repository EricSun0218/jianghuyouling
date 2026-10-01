namespace JianghuYouling.Core.Persistence
{
    /// <summary>Shared bounded retry policy for idempotent startup reconciliation scans.</summary>
    public static class StartupRecoveryPolicy
    {
        public const int MaxAttempts = 3;

        /// <param name="completedAttempts">Includes the attempt which just completed.</param>
        public static bool ShouldRetry(bool stillPending, int completedAttempts)
            => stillPending && completedAttempts > 0 && completedAttempts < MaxAttempts;

        public static float DelaySeconds(int completedAttempts)
            => completedAttempts <= 1 ? 0.5f : 1.5f;
    }
}
