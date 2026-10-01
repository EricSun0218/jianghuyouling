using System;

namespace JianghuYouling.Core.Tools
{
    /// <summary>
    /// Cross-binds a crash-recoverable mutation's exact dispatch envelope to its durable
    /// operation id. Recovery must never replay an edited envelope under an older id.
    /// </summary>
    public static class DispatchEnvelopeBinding
    {
        public static string CreateDigest(string envelopeJson)
            => string.IsNullOrWhiteSpace(envelopeJson) ? null
                : OperationId.FromStableKey("dispatch-envelope|" + envelopeJson);

        public static string CreateOperationId(string stableKey, string envelopeDigest)
            => string.IsNullOrWhiteSpace(stableKey)
                || !OperationId.IsValid(envelopeDigest) ? null
                : OperationId.FromStableKey(stableKey + "|envelope=" + envelopeDigest);

        public static bool Matches(string stableKey, string envelopeJson,
            string envelopeDigest, string operationId)
        {
            if (string.IsNullOrWhiteSpace(stableKey)
                || string.IsNullOrWhiteSpace(envelopeJson)
                || !OperationId.IsValid(envelopeDigest)
                || !OperationId.IsValid(operationId))
                return false;
            string actualDigest = CreateDigest(envelopeJson);
            return string.Equals(actualDigest, envelopeDigest, StringComparison.Ordinal)
                && string.Equals(CreateOperationId(stableKey, envelopeDigest), operationId,
                    StringComparison.Ordinal);
        }
    }
}
