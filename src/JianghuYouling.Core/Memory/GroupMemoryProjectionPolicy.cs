namespace JianghuYouling.Core.Memory
{
    /// <summary>
    /// Commit rules for the durable group-transcript -> per-member hidden conversation context.
    /// The transcript marker remains authoritative after child journals have completed.
    /// </summary>
    public static class GroupMemoryProjectionPolicy
    {
        public const int MaxStartupRecoveryAttempts = 3;

        public static bool NeedsRecovery(bool durableProjectionPending)
            => durableProjectionPending;

        /// <summary>
        /// Startup recovery is an idempotent projection of an already committed
        /// transcript.  Retry only while the projection is still pending and keep the
        /// retry count bounded so a persistently unreadable store cannot spin forever.
        /// completedAttempts includes the attempt which just failed.
        /// </summary>
        public static bool ShouldRetryStartupRecovery(bool stillPending, int completedAttempts)
            => stillPending && completedAttempts > 0
                && completedAttempts < MaxStartupRecoveryAttempts;

        public static float StartupRecoveryDelaySeconds(int completedAttempts)
            => completedAttempts <= 1 ? 0.5f : 1.5f;

        public static bool CanCommit(bool durableProjectionPending, bool allMemberStoresSaved,
            bool cleanupTransactionPending, long capturedTranscriptRevision,
            long authoritativeTranscriptRevision)
            => durableProjectionPending
                && allMemberStoresSaved
                && !cleanupTransactionPending
                && capturedTranscriptRevision > 0
                && capturedTranscriptRevision == authoritativeTranscriptRevision;
    }
}
