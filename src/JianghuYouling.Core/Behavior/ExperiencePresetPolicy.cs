using System;
using System.Collections.Generic;

namespace JianghuYouling.Core.Behavior
{
    /// <summary>
    /// The five settings controlled by the one-click experience page. Keeping the mapping in
    /// Core makes the UI, persistence path and offline regression tests share one authority.
    /// </summary>
    public sealed class ExperiencePresetDefinition
    {
        internal ExperiencePresetDefinition(string id, string displayName, bool monthlyEvent,
            bool companionMonthly, int companionCount, int groupRounds, int assistantFrequency)
        {
            Id = id;
            DisplayName = displayName;
            MonthlyEvent = monthlyEvent;
            CompanionMonthly = companionMonthly;
            CompanionCount = companionCount;
            GroupRounds = groupRounds;
            AssistantFrequency = assistantFrequency;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public bool MonthlyEvent { get; }
        public bool CompanionMonthly { get; }
        public int CompanionCount { get; }
        public int GroupRounds { get; }
        public int AssistantFrequency { get; }
    }

    public static class ExperiencePresetPolicy
    {
        public const string EconomyId = "economy";
        public const string BalancedId = "balanced";
        public const string BestId = "best";

        private static readonly ExperiencePresetDefinition Economy =
            new ExperiencePresetDefinition(EconomyId, "省钱", false, false, 0, 1, 0);
        private static readonly ExperiencePresetDefinition Balanced =
            new ExperiencePresetDefinition(BalancedId, "均衡", false, true, 3, 2, 2);
        private static readonly ExperiencePresetDefinition Best =
            new ExperiencePresetDefinition(BestId, "最佳体验", true, true, 5, 3, 3);

        private static readonly ExperiencePresetDefinition[] Definitions =
            { Economy, Balanced, Best };

        public static IReadOnlyList<ExperiencePresetDefinition> All => Definitions;

        public static bool TryGet(string id, out ExperiencePresetDefinition definition)
        {
            definition = null;
            if (string.IsNullOrWhiteSpace(id)) return false;
            for (int i = 0; i < Definitions.Length; i++)
            {
                if (!string.Equals(Definitions[i].Id, id, StringComparison.Ordinal)) continue;
                definition = Definitions[i];
                return true;
            }
            return false;
        }

        public static ExperiencePresetDefinition Match(bool monthlyEvent, bool companionMonthly,
            int companionCount, int groupRounds, int assistantFrequency)
        {
            for (int i = 0; i < Definitions.Length; i++)
            {
                ExperiencePresetDefinition item = Definitions[i];
                if (item.MonthlyEvent == monthlyEvent
                    && item.CompanionMonthly == companionMonthly
                    && item.CompanionCount == companionCount
                    && item.GroupRounds == groupRounds
                    && item.AssistantFrequency == assistantFrequency)
                    return item;
            }
            return null;
        }
    }
}
