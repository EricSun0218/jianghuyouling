namespace JianghuYouling.Shared
{
    /// <summary>
    /// Pure decision table for partially committed operation receipts. A terminal result may be exposed only after
    /// the intended receipt or its conservative fallback has been semantically read back. Ledger indexing is tracked
    /// separately: it must be repaired, but an index-only failure must never overwrite an exact business result.
    /// </summary>
    public enum OperationReceiptCommitDecision
    {
        ProceedWithMutation,
        ReturnPrimaryTerminal,
        ReturnFallbackTerminal,
        ReturnRetryableUnknown,
    }

    public readonly struct OperationReceiptCommitState
    {
        public readonly bool ExactReceiptVerified;
        public readonly bool LedgerIndexed;

        public OperationReceiptCommitState(bool exactReceiptVerified, bool ledgerIndexed)
        {
            ExactReceiptVerified = exactReceiptVerified;
            LedgerIndexed = ledgerIndexed;
        }

        public bool FullyIndexed => ExactReceiptVerified && LedgerIndexed;
    }

    public static class OperationReceiptCommitPolicy
    {
        public static OperationReceiptCommitDecision ResolvePrewrite(bool pendingVerified, bool rejectionVerified)
        {
            if (pendingVerified) return OperationReceiptCommitDecision.ProceedWithMutation;
            if (rejectionVerified) return OperationReceiptCommitDecision.ReturnFallbackTerminal;
            return OperationReceiptCommitDecision.ReturnRetryableUnknown;
        }

        public static OperationReceiptCommitDecision ResolvePrewrite(OperationReceiptCommitState pending,
            OperationReceiptCommitState rejection)
        {
            // A direct per-operation receipt is the dedupe authority. A missing ledger index
            // must be repaired, but must never cause the exact pending/rejection to be overwritten.
            if (pending.ExactReceiptVerified) return OperationReceiptCommitDecision.ProceedWithMutation;
            if (rejection.ExactReceiptVerified) return OperationReceiptCommitDecision.ReturnFallbackTerminal;
            return OperationReceiptCommitDecision.ReturnRetryableUnknown;
        }

        public static OperationReceiptCommitDecision ResolveTerminal(bool terminalVerified, bool fallbackVerified)
        {
            if (terminalVerified) return OperationReceiptCommitDecision.ReturnPrimaryTerminal;
            if (fallbackVerified) return OperationReceiptCommitDecision.ReturnFallbackTerminal;
            return OperationReceiptCommitDecision.ReturnRetryableUnknown;
        }

        public static OperationReceiptCommitDecision ResolveTerminal(OperationReceiptCommitState terminal,
            OperationReceiptCommitState fallback)
        {
            if (terminal.ExactReceiptVerified) return OperationReceiptCommitDecision.ReturnPrimaryTerminal;
            if (fallback.ExactReceiptVerified) return OperationReceiptCommitDecision.ReturnFallbackTerminal;
            return OperationReceiptCommitDecision.ReturnRetryableUnknown;
        }
    }
}
