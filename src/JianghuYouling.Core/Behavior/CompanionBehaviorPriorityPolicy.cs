using System;
using System.Collections.Generic;

namespace JianghuYouling.Core.Behavior
{
    /// <summary>One successful companion-monthly action observed before the current month.</summary>
    public sealed class CompanionBehaviorObservation
    {
        public string ToolName { get; set; }
        public int MonthsAgo { get; set; }
    }

    /// <summary>Relative behavior priority after applying recent-frequency decay.</summary>
    public sealed class CompanionBehaviorPriority
    {
        public string ToolName { get; set; }
        public string Category { get; set; }
        public int Priority { get; set; }
        public double RecentHeat { get; set; }
    }

    /// <summary>
    /// Dynamically lowers frequently repeated behavior without changing which NPCs are selected.
    /// Every available behavior remains selectable; motive and authoritative preconditions still
    /// take precedence, while equal-quality choices use this relative priority as a tie-breaker.
    /// </summary>
    public static class CompanionBehaviorPriorityPolicy
    {
        public const int LookbackMonths = 6;
        public const int HighestPriority = 100;
        public const int LowestPriority = 20;
        private const double MonthlyDecay = 0.72d;
        private const double ToolPenalty = 0.90d;
        private const double CategoryPenalty = 0.24d;

        public static List<CompanionBehaviorPriority> Rank(
            IList<string> availableToolNames,
            IEnumerable<CompanionBehaviorObservation> observations)
        {
            var available = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (availableToolNames != null)
                for (int i = 0; i < availableToolNames.Count; i++)
                {
                    string tool = Normalize(availableToolNames[i]);
                    if (tool.Length > 0 && seen.Add(tool)) available.Add(tool);
                }

            var toolHeat = new Dictionary<string, double>(StringComparer.Ordinal);
            var categoryHeat = new Dictionary<string, double>(StringComparer.Ordinal);
            if (observations != null)
                foreach (CompanionBehaviorObservation observation in observations)
                {
                    if (observation == null || observation.MonthsAgo < 1
                        || observation.MonthsAgo > LookbackMonths) continue;
                    string tool = Normalize(observation.ToolName);
                    if (tool.Length == 0) continue;
                    double heat = Math.Pow(MonthlyDecay, observation.MonthsAgo - 1);
                    toolHeat[tool] = HeatOf(toolHeat, tool) + heat;
                    string category = CategoryOf(tool);
                    if (!string.IsNullOrWhiteSpace(category))
                        categoryHeat[category] = HeatOf(categoryHeat, category) + heat;
                }

            var raw = new Dictionary<string, double>(StringComparer.Ordinal);
            double strongest = 0d;
            foreach (string tool in available)
            {
                string category = CategoryOf(tool);
                double heat = HeatOf(toolHeat, tool);
                double familyHeat = string.IsNullOrWhiteSpace(category)
                    ? 0d : HeatOf(categoryHeat, category);
                double value = 1d / (1d + ToolPenalty * heat
                    + CategoryPenalty * familyHeat);
                raw[tool] = value;
                if (value > strongest) strongest = value;
            }

            var result = new List<CompanionBehaviorPriority>();
            foreach (string tool in available)
            {
                double relative = strongest > 0d ? raw[tool] / strongest : 1d;
                int priority = (int)Math.Round(HighestPriority * relative,
                    MidpointRounding.AwayFromZero);
                priority = Math.Max(LowestPriority, Math.Min(HighestPriority, priority));
                result.Add(new CompanionBehaviorPriority
                {
                    ToolName = tool,
                    Category = CategoryOf(tool),
                    Priority = priority,
                    RecentHeat = HeatOf(toolHeat, tool),
                });
            }
            result.Sort((left, right) =>
            {
                int byPriority = right.Priority.CompareTo(left.Priority);
                return byPriority != 0 ? byPriority
                    : string.Compare(left.ToolName, right.ToolName, StringComparison.Ordinal);
            });
            return result;
        }

        public static string CategoryOf(string toolName)
        {
            switch (Normalize(toolName))
            {
                case "gift_item":
                case "gift_silver":
                case "barter":
                case "write_book":
                case "teach":
                    return "物资与传承";
                case "relate":
                case "enmity":
                case "dissolve_relation":
                case "adjust_favor":
                case "set_relation":
                case "spend_night":
                case "matchmake":
                    return "关系与情感";
                case "adjust_mood":
                case "adjust_fame":
                    return "心绪与声名";
                case "heal":
                case "detox":
                case "regulate_breath":
                    return "照料与疗愈";
                case "add_feature":
                case "flip_practice":
                case "train_skill":
                case "read_book":
                case "use_item":
                case "change_equipment":
                case "sect_support":
                    return "自身成长";
                case "send_message":
                case "tell_secret":
                    return "沟通与秘闻";
                case "steal":
                case "kill":
                case "poison":
                case "capture":
                    return "冒险与冲突";
                default:
                    return null;
            }
        }

        private static double HeatOf(IDictionary<string, double> heat, string key)
            => heat != null && key != null && heat.TryGetValue(key, out double value)
                ? value : 0d;

        private static string Normalize(string toolName)
            => (toolName ?? string.Empty).Trim().ToLowerInvariant();
    }
}
