namespace JianghuYouling.Core.Behavior
{
    /// <summary>A read-only observation from the currently loaded game, never a disk-cache assertion.</summary>
    public sealed class CharacterProxyIdentity
    {
        public const int MaxQueryBatch = 128;
        public int RequestedId;
        public int OriginalId;
        public int ResolvedId;
        public short DisplayTemplateId = -1;
        public bool IsProxy;
        public bool RequiresPreparation;
        public bool IsDead;
        public string Error;

        public bool IsValidFor(int taiwuId)
        {
            if (!string.IsNullOrEmpty(Error) || RequestedId <= 0 || OriginalId <= 0
                || ResolvedId <= 0 || taiwuId <= 0 || DisplayTemplateId < -1)
                return false;
            if (RequestedId != taiwuId && (OriginalId == taiwuId || ResolvedId == taiwuId))
                return false;
            if (RequestedId == taiwuId && (OriginalId != taiwuId || ResolvedId != taiwuId || IsProxy))
                return false;
            return IsProxy
                ? OriginalId != ResolvedId && !RequiresPreparation
                    && OriginalId != taiwuId && ResolvedId != taiwuId
                : OriginalId == ResolvedId && DisplayTemplateId == -1;
        }

        public static bool HasConsistentMapping(int sourceId, int proxyId,
            int reverseSourceId, int forwardProxyId, int taiwuId)
            => sourceId > 0 && proxyId > 0 && sourceId != proxyId
                && sourceId != taiwuId && proxyId != taiwuId
                && reverseSourceId == sourceId && forwardProxyId == proxyId;
    }
}
