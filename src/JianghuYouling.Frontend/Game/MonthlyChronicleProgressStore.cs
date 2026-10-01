using System;
using System.Collections.Generic;

namespace JianghuYouling
{
    /// <summary>当前世界内的过月纪事生成进度。只供历史窗口展示，不落盘也不参与未读计数。</summary>
    public sealed class MonthlyChronicleProgress
    {
        public int Date;
        public int TaiwuId;
        public int CompanionPending;
        public int Generation;
        public uint WorldId;
        public bool EventExpected;
        public bool EventDone;
        public bool CompanionExpected;
        public bool CompanionDone;
        public string StopReason;

        public bool IsGenerating => EventExpected && !EventDone
            || CompanionExpected && !CompanionDone;
    }

    public static class MonthlyChronicleProgressStore
    {
        static readonly object Gate = new object();
        static readonly Dictionary<string, MonthlyChronicleProgress> Values =
            new Dictionary<string, MonthlyChronicleProgress>(StringComparer.Ordinal);

        static string Key(uint worldId, int taiwuId, int date)
            => worldId + ":" + taiwuId + ":" + date;

        public static void Publish(int date, int taiwuId, bool eventExpected, bool eventDone,
            bool companionExpected, bool companionDone, int companionPending,
            int generation, uint worldId, string stopReason = null)
        {
            if (date < 0 || taiwuId <= 0 || worldId == 0
                || !WorldLifecycle.IsSameWorld(generation) || WorldLifecycle.WorldId != worldId) return;
            string boundedReason = string.IsNullOrWhiteSpace(stopReason)
                ? null : stopReason.Trim();
            if (boundedReason != null && boundedReason.Length > 512)
                boundedReason = boundedReason.Substring(0, 512);
            var value = new MonthlyChronicleProgress
            {
                Date = date,
                TaiwuId = taiwuId,
                EventExpected = eventExpected,
                EventDone = eventDone,
                CompanionExpected = companionExpected,
                CompanionDone = companionDone,
                CompanionPending = Math.Max(0, companionPending),
                Generation = generation,
                WorldId = worldId,
                StopReason = boundedReason,
            };
            lock (Gate)
            {
                string key = Key(worldId, taiwuId, date);
                if (string.IsNullOrWhiteSpace(value.StopReason)
                    && Values.TryGetValue(key, out MonthlyChronicleProgress existing)
                    && existing != null && !string.IsNullOrWhiteSpace(existing.StopReason))
                    value.StopReason = existing.StopReason;
                Values[key] = value;
            }
        }

        public static bool TryGet(int taiwuId, int date, out MonthlyChronicleProgress value)
        {
            value = null;
            uint worldId = WorldLifecycle.WorldId;
            if (taiwuId <= 0 || date < 0 || worldId == 0) return false;
            lock (Gate)
            {
                if (!Values.TryGetValue(Key(worldId, taiwuId, date), out MonthlyChronicleProgress found)
                    || found == null || !WorldLifecycle.IsSameWorld(found.Generation)) return false;
                value = found;
                return true;
            }
        }

        public static List<int> Dates(int taiwuId)
        {
            var result = new List<int>();
            uint worldId = WorldLifecycle.WorldId;
            if (taiwuId <= 0 || worldId == 0) return result;
            lock (Gate)
            {
                foreach (MonthlyChronicleProgress value in Values.Values)
                    if (value != null && value.WorldId == worldId && value.TaiwuId == taiwuId
                        && WorldLifecycle.IsSameWorld(value.Generation)) result.Add(value.Date);
            }
            return result;
        }

        public static void ResetForWorldExit()
        {
            lock (Gate) Values.Clear();
        }
    }
}
