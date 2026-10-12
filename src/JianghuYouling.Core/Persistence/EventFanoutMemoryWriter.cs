using System;
using JianghuYouling.Core.Memory;

namespace JianghuYouling.Core.Persistence
{
    /// <summary>Commits one source-keyed rumor before the fanout journal advances.</summary>
    public static class EventFanoutMemoryWriter
    {
        public const string SourceKind = "monthly_event_fanout";

        public static bool TryCommit(string directory, int taiwuId, int recipientId,
            MemoryEntry entry, out string failure)
        {
            failure = null;
            if (taiwuId <= 0 || recipientId <= 0 || entry == null || !entry.Valid || entry.SourceKind != SourceKind
                || string.IsNullOrWhiteSpace(entry.SourceId))
            { failure = "invalid_memory_source"; return false; }
            try
            {
                // Always reload: an in-memory entry left by a failed Save is not a durable receipt.
                NpcMemoryStore memory = NpcMemoryStore.Load(directory,
                    taiwuId.ToString(), recipientId.ToString());
                if (!memory.LoadReliable) { failure = "memory_unreadable"; return false; }
                // A previous attempt may have committed the memory but not its recipient checkpoint.
                if (HasSource(memory, entry.SourceId)) return true;
                MemoryEntry stored = memory.AddOrMerge(entry, out _);
                if (stored == null) { failure = "memory_snapshot_stale"; return false; }
                memory.Prune(entry.WorldDate, retainedEntry: stored);
                if (!memory.Save()) { failure = "memory_save_rejected"; return false; }
                if (!HasSource(memory, entry.SourceId))
                { failure = "memory_source_missing_after_save"; return false; }
                return true;
            }
            catch (Exception ex)
            { failure = "memory_commit_" + ex.GetType().Name; return false; }
        }

        private static bool HasSource(NpcMemoryStore memory, string sourceId)
        {
            foreach (MemoryEntry entry in memory.All)
                if (entry != null && entry.Valid && entry.SourceKind == SourceKind
                    && string.Equals(entry.SourceId, sourceId, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
