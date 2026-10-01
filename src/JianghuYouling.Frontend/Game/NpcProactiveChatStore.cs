using System;
using System.Collections.Generic;
using System.IO;

namespace JianghuYouling
{
    /// <summary>
    /// 聊过的 NPC 主动来信设置。总开关与频率分开保存；新安装默认开启 30 至 40 分钟档。
    /// </summary>
    public static class NpcProactiveChatStore
    {
        public const int VeryLow = 0, Low = 1, Mid = 2, High = 3;
        public const int DefaultFrequency = VeryLow;

        public static string EnabledSettingsPath => Path.Combine(
            JianghuYoulingPaths.Settings, "npc_proactive_chat_enabled.txt");
        public static string FrequencySettingsPath => Path.Combine(
            JianghuYoulingPaths.Settings, "npc_proactive_chat_frequency.txt");

        public static bool LoadEnabled()
        {
            try
            {
                if (DurableSettingsStore.TryLoad(EnabledSettingsPath,
                    DurableSettingsStore.Small, DurableSettingsStore.IsLegacyBoolean,
                    out string raw))
                {
                    string value = raw.Trim().ToLowerInvariant();
                    return !(value == "0" || value == "off" || value == "false"
                        || value == "关" || value == "否" || value == "no");
                }
            }
            catch { }
            return true;
        }

        public static bool SaveEnabled(bool enabled)
        {
            try
            {
                return DurableSettingsStore.Save(EnabledSettingsPath, enabled ? "1" : "0",
                    DurableSettingsStore.Small, DurableSettingsStore.IsLegacyBoolean);
            }
            catch { return false; }
        }

        public static int LoadFrequency()
        {
            try
            {
                if (DurableSettingsStore.TryLoad(FrequencySettingsPath,
                    DurableSettingsStore.Small, IsFrequency, out string raw))
                {
                    string value = raw.Trim().ToLowerInvariant();
                    if (value == "0" || value == "极低" || value == "very_low"
                        || value == "verylow") return VeryLow;
                    if (value == "2" || value == "中" || value == "mid") return Mid;
                    if (value == "3" || value == "高" || value == "high") return High;
                    return Low;
                }
            }
            catch { }
            return DefaultFrequency;
        }

        public static bool SaveFrequency(int level)
        {
            level = level < VeryLow ? VeryLow : level > High ? High : level;
            string value = level == VeryLow ? "very_low"
                : level == High ? "high" : level == Mid ? "mid" : "low";
            try
            {
                return DurableSettingsStore.Save(FrequencySettingsPath, value,
                    DurableSettingsStore.Small, IsFrequency);
            }
            catch { return false; }
        }

        public static string Label(int level)
            => level == VeryLow ? "极低" : level == High ? "高" : level == Mid ? "中" : "低";

        public static void IntervalRange(int level, out float min, out float max)
        {
            switch (level)
            {
                case VeryLow: min = 1800f; max = 2400f; break;
                case High: min = 60f; max = 120f; break;
                case Mid: min = 180f; max = 420f; break;
                default: min = 600f; max = 1200f; break;
            }
        }

        /// <summary>
        /// 主动消息候选的轻量亲熟度偏置。总对话和近六月聊天频率合计最多只加 18%，
        /// 所有没有足够历史的人仍保有基础权重；近期刚主动联系过的人再小幅降权，
        /// 避免高频联系人形成“越发越容易再发”的自我强化。
        /// </summary>
        public static double CandidateWeight(int playerConversations,
            int recentPlayerConversations, int recentActiveMonths,
            int monthsSinceLastPlayerTalk, int recentRecipientRank)
        {
            int total = Math.Max(0, playerConversations);
            int recent = Math.Max(0, Math.Min(total, recentPlayerConversations));
            int activeMonths = Math.Max(1, Math.Min(6, recentActiveMonths));
            double depthSignal = Math.Min(1d,
                Math.Log(1d + total) / Math.Log(101d));
            double recentRateSignal = Math.Min(1d, recent / (activeMonths * 4d));
            int gap = Math.Max(0, monthsSinceLastPlayerTalk);
            double recency = gap <= 1 ? 1d : Math.Max(0d, (7d - gap) / 6d);
            double weight = 1d + depthSignal * 0.08d
                + recentRateSignal * recency * 0.10d;
            if (recentRecipientRank == 0) weight *= 0.90d;
            else if (recentRecipientRank == 1) weight *= 0.95d;
            else if (recentRecipientRank == 2) weight *= 0.98d;
            return Math.Max(0.75d, Math.Min(1.18d, weight));
        }

        public static int PickWeightedIndex(IList<double> weights, double sample01)
        {
            if (weights == null || weights.Count == 0) return -1;
            double total = 0d;
            for (int i = 0; i < weights.Count; i++)
                if (!double.IsNaN(weights[i]) && !double.IsInfinity(weights[i])
                    && weights[i] > 0d) total += weights[i];
            if (total <= 0d) return 0;
            double cursor = Math.Max(0d, Math.Min(0.999999999d, sample01)) * total;
            for (int i = 0; i < weights.Count; i++)
            {
                double weight = !double.IsNaN(weights[i]) && !double.IsInfinity(weights[i])
                    && weights[i] > 0d ? weights[i] : 0d;
                cursor -= weight;
                if (cursor < 0d) return i;
            }
            return weights.Count - 1;
        }

        private static bool IsFrequency(string raw)
        {
            if (!DurableSettingsStore.IsSingleLine(raw, 16, false)) return false;
            string value = raw.Trim().ToLowerInvariant();
            return value == "0" || value == "1" || value == "2" || value == "3"
                || value == "极低" || value == "低" || value == "中" || value == "高"
                || value == "very_low" || value == "verylow"
                || value == "low" || value == "mid" || value == "high";
        }
    }
}
