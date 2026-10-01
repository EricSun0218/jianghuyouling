using System;

namespace JianghuYouling
{
    /// <summary>
    /// One shared cost circuit for the whole monthly digest (event plus every companion).
    /// It limits model requests and a conservative billed-token reservation, not successful game actions:
    /// agents may still choose any supported action, but a runaway query/retry loop cannot
    /// turn one month into thousands of charged requests.
    /// </summary>
    internal static class MonthlyAgentRequestBudget
    {
        // This is deliberately above the full supported surface (event action rounds plus
        // tool-free final-story attempts, and eight companions with the same separation).
        // It is an emergency fuse for an abnormal
        // cross-companion cascade, not a quota that should shorten a legitimate long month.
        internal const int MaxLogicalRequestsPerDigest = 192;
        // Eight companions × (twelve action rounds + final prose) plus the event loop fit.
        // The token ceiling must also admit that entire legal surface at the configured
        // near-million-token context. 300M is therefore an abnormal-cascade fuse, matching
        // the group-chat circuit, rather than a hidden long-context business quota.
        internal const long MaxEstimatedBilledTokensPerDigest = 300000000L;
        internal const int RetryReservationMultiplier = 2;

        private static readonly object Gate = new object();
        private static uint _worldId;
        private static int _taiwuId = -1;
        private static int _date = -1;
        private static int _requests;
        private static long _estimatedBilledTokens;
        private static bool _blocked;

        internal static void Begin(uint worldId, int taiwuId, int date)
        {
            lock (Gate)
            {
                if (_worldId == worldId && _taiwuId == taiwuId && _date == date) return;
                _worldId = worldId;
                _taiwuId = taiwuId;
                _date = date;
                _requests = 0;
                _estimatedBilledTokens = 0;
                _blocked = false;
            }
        }

        internal static bool TryAcquire(uint worldId, int taiwuId, int date, int estimatedInputTokens,
            int estimatedOutputTokens, out string diagnostic)
        {
            lock (Gate)
            {
                if (_worldId != worldId || _taiwuId != taiwuId || _date != date)
                {
                    // Only the digest owner may establish/reset a scope through Begin().
                    // A late coroutine from the previous month/world must not replace the
                    // active scope and thereby reset its counters.
                    diagnostic = "scope_mismatch active=" + _worldId + "/" + _taiwuId + "/" + _date
                        + " requested=" + worldId + "/" + taiwuId + "/" + date;
                    return false;
                }

                long input = Math.Max(0, estimatedInputTokens);
                long output = Math.Max(0, estimatedOutputTokens);
                long estimate = checked((input + output) * RetryReservationMultiplier);
                if (_requests >= MaxLogicalRequestsPerDigest
                    || _estimatedBilledTokens + estimate > MaxEstimatedBilledTokensPerDigest)
                {
                    _blocked = true;
                    diagnostic = "requests=" + _requests + "/" + MaxLogicalRequestsPerDigest
                        + " estimated_billed=" + _estimatedBilledTokens + "/"
                        + MaxEstimatedBilledTokensPerDigest + " next=" + estimate
                        + " input=" + input + " output=" + output;
                    return false;
                }

                _requests++;
                _estimatedBilledTokens += estimate;
                diagnostic = "requests=" + _requests + "/" + MaxLogicalRequestsPerDigest
                    + " estimated_billed=" + _estimatedBilledTokens + "/"
                    + MaxEstimatedBilledTokensPerDigest;
                return true;
            }
        }

        internal static bool IsExhausted(uint worldId, int taiwuId, int date)
        {
            lock (Gate)
                return _worldId == worldId && _taiwuId == taiwuId && _date == date
                    && (_requests >= MaxLogicalRequestsPerDigest
                        || _estimatedBilledTokens >= MaxEstimatedBilledTokensPerDigest || _blocked);
        }
    }
}
