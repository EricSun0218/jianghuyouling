using System;
using System.Collections.Generic;

namespace JianghuYouling.Core.Behavior
{
    /// <summary>
    /// Selects a bounded, stable-random subset of eligible companions for one settlement month.
    /// The same world/month produces the same order so retries cannot silently change actors.
    /// </summary>
    public static class CompanionMonthlySelectionPolicy
    {
        public const int DefaultCount = 3;
        public const int MaxCount = 8;

        public static List<int> NormalizeCandidates(IList<int> candidates, int excludedCharacterId)
        {
            var unique = new HashSet<int>();
            var ordered = new List<int>();
            if (candidates != null)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    int id = candidates[i];
                    if (id > 0 && id != excludedCharacterId && unique.Add(id)) ordered.Add(id);
                }
            }
            ordered.Sort();
            return ordered;
        }

        public static List<int> Select(IList<int> candidates, int requestedCount,
            uint worldId, int taiwuId, int date)
        {
            var ordered = NormalizeCandidates(candidates, 0);
            if (ordered.Count == 0) return ordered;

            // Zero explicitly disables companion triggering for the economy preset.
            // Negative values remain an invalid/internal fallback to the default count.
            if (requestedCount == 0) return new List<int>();

            int count = Math.Min(requestedCount > 0 ? requestedCount : DefaultCount, MaxCount);
            if (count >= ordered.Count) return ordered;

            uint state = 2166136261u;
            Mix(ref state, worldId);
            Mix(ref state, unchecked((uint)taiwuId));
            Mix(ref state, unchecked((uint)date));
            for (int i = ordered.Count - 1; i > 0; i--)
            {
                state = Next(state);
                int j = (int)(state % (uint)(i + 1));
                int value = ordered[i];
                ordered[i] = ordered[j];
                ordered[j] = value;
            }
            if (ordered.Count > count) ordered.RemoveRange(count, ordered.Count - count);
            return ordered;
        }

        private static void Mix(ref uint state, uint value)
        {
            state ^= value;
            state *= 16777619u;
            state ^= value >> 16;
            state *= 16777619u;
            if (state == 0) state = 0x9E3779B9u;
        }

        private static uint Next(uint state)
        {
            if (state == 0) state = 0x9E3779B9u;
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }
    }
}
