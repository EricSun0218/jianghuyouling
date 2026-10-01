using System;
using System.Text;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Commission
{
    public sealed class CommissionProposal
    {
        public string Kind { get; set; }
        public int ResourceType { get; set; } = -1;
        public string ResourceName { get; set; }
        public int Amount { get; set; }
        public int TargetNpcId { get; set; } = -1;
        public string TargetNpcName { get; set; }
        public int TargetConsummate { get; set; } = -1;
        /// <summary>本体内部品级：0=九品，8=一品。</summary>
        public int RewardGrade { get; set; }
        public string OfferText { get; set; }
        public string CompletionText { get; set; }
    }

    /// <summary>
    /// NPC 只能从有限任务模板中填写参数，并且只决定奖励品级。实际奖励由后端在领奖时
    /// 从该品级的本体物品/资源池随机生成；自然语言不能增加任务、证明完成或指定奖励。
    /// </summary>
    public static class CommissionProposalPolicy
    {
        public const string DeliverResource = "deliver_resource";
        public const string CollectResource = "collect_resource";
        public const string EarnMoney = "earn_money";
        public const string GainPrestige = "gain_prestige";
        public const string IncreaseFavor = "increase_favor";
        public const string KillNpc = "kill_npc";
        public const int MinRewardGrade = 0;
        public const int MaxRewardGrade = 8;
        public const int MaxDialogueChars = 300;

        private static readonly string[] ResourceNames =
        {
            "食材", "木材", "金石", "玉石", "织物", "药材",
        };

        private static readonly string[] GradeNames =
        {
            "九品", "八品", "七品", "六品", "五品", "四品", "三品", "二品", "一品",
        };

        public static string ResourceName(int resourceType)
            => resourceType >= 0 && resourceType < ResourceNames.Length
                ? ResourceNames[resourceType] : null;

        public static string GradeName(int grade)
            => grade >= MinRewardGrade && grade <= MaxRewardGrade
                ? GradeNames[grade] : null;

        public static bool IsKind(string kind)
            => string.Equals(kind, DeliverResource, StringComparison.Ordinal)
                || string.Equals(kind, CollectResource, StringComparison.Ordinal)
                || string.Equals(kind, EarnMoney, StringComparison.Ordinal)
                || string.Equals(kind, GainPrestige, StringComparison.Ordinal)
                || string.Equals(kind, IncreaseFavor, StringComparison.Ordinal)
                || string.Equals(kind, KillNpc, StringComparison.Ordinal);

        public static bool NeedsResource(string kind)
            => string.Equals(kind, DeliverResource, StringComparison.Ordinal)
                || string.Equals(kind, CollectResource, StringComparison.Ordinal);

        public static bool TryResourceType(string value, out int resourceType)
        {
            resourceType = -1;
            string text = (value ?? string.Empty).Trim();
            for (int i = 0; i < ResourceNames.Length; i++)
                if (string.Equals(text, ResourceNames[i], StringComparison.OrdinalIgnoreCase))
                {
                    resourceType = i;
                    return true;
                }
            return int.TryParse(text, out resourceType)
                && resourceType >= 0 && resourceType < ResourceNames.Length;
        }

        public static bool TryGrade(string value, out int grade)
        {
            grade = -1;
            string text = (value ?? string.Empty).Trim();
            for (int i = 0; i < GradeNames.Length; i++)
                if (string.Equals(text, GradeNames[i], StringComparison.OrdinalIgnoreCase))
                {
                    grade = i;
                    return true;
                }
            // 数字沿用玩家可见品阶：1=一品，9=九品。
            if (int.TryParse(text.Replace("品", string.Empty), out int visible)
                && visible >= 1 && visible <= 9)
            {
                grade = 9 - visible;
                return true;
            }
            return false;
        }

        public static bool TryParseArguments(JObject root, out CommissionProposal proposal,
            out string error)
        {
            proposal = null;
            error = null;
            if (root == null)
            {
                error = "没有填写委托参数";
                return false;
            }
            string kind = (root["kind"]?.ToString() ?? string.Empty).Trim();
            if (!IsKind(kind))
            {
                error = "选择了不受支持的委托类型";
                return false;
            }
            int amount;
            if (string.Equals(kind, KillNpc, StringComparison.Ordinal))
                amount = 1;
            else if (!TryInteger(root["amount"], out amount))
            {
                error = "没有填写有效的任务数量";
                return false;
            }
            if (!AmountRange(kind, out int min, out int max))
            {
                error = "委托类型无效";
                return false;
            }
            amount = Clamp(amount, min, max);

            int resourceType = -1;
            if (NeedsResource(kind)
                && !TryResourceType(root["resource"]?.ToString(), out resourceType))
            {
                error = "资源委托没有选择有效资源";
                return false;
            }
            int rewardGrade = 0;
            if (!string.Equals(kind, KillNpc, StringComparison.Ordinal)
                && !TryGrade(root["reward_grade"]?.ToString(), out rewardGrade))
            {
                error = "没有选择有效的奖励品级";
                return false;
            }

            string offer = CleanDialogue(root["request"]?.ToString());
            string completion = CleanDialogue(root["completion"]?.ToString());
            string targetName = CleanTargetName(root["target"]?.ToString());
            if (string.Equals(kind, KillNpc, StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(targetName))
            {
                error = "击杀委托没有选择目标人物";
                return false;
            }
            if (string.IsNullOrWhiteSpace(offer))
            {
                error = "没有写出委托内容";
                return false;
            }
            if (string.IsNullOrWhiteSpace(completion))
                completion = "有劳了，这份情我记下了。";

            proposal = new CommissionProposal
            {
                Kind = kind,
                ResourceType = resourceType,
                ResourceName = ResourceName(resourceType),
                Amount = amount,
                TargetNpcName = targetName,
                RewardGrade = rewardGrade,
                OfferText = offer,
                CompletionText = completion,
            };
            return true;
        }

        public static bool AmountRange(string kind, out int min, out int max)
        {
            switch (kind)
            {
                case DeliverResource: min = 50; max = 5000; return true;
                case CollectResource: min = 100; max = 10000; return true;
                case EarnMoney: min = 500; max = 30000; return true;
                case GainPrestige: min = 100; max = 8000; return true;
                case IncreaseFavor: min = 300; max = 5000; return true;
                case KillNpc: min = max = 1; return true;
                default: min = max = 0; return false;
            }
        }

        public static string Objective(string kind, int resourceType, int amount,
            string targetNpcName = null)
        {
            string resource = ResourceName(resourceType) ?? "资源";
            switch (kind)
            {
                case DeliverResource: return "向委托人交付 " + resource + " " + amount;
                case CollectResource: return "新增积攒 " + resource + " " + amount;
                case EarnMoney: return "新增赚取银钱 " + amount;
                case GainPrestige: return "新增获得威望 " + amount;
                case IncreaseFavor: return "令委托人对太吾的好感增加 " + amount;
                case KillNpc: return "杀死 " + (string.IsNullOrWhiteSpace(targetNpcName)
                    ? "指定人物" : targetNpcName.Trim());
                default: return "未知委托";
            }
        }

        public static string CleanDialogue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var builder = new StringBuilder(Math.Min(value.Length, MaxDialogueChars));
            bool pendingSpace = false;
            foreach (char c in value.Trim())
            {
                if (c == '\0' || char.IsSurrogate(c)) continue;
                if (char.IsWhiteSpace(c)) { pendingSpace = builder.Length > 0; continue; }
                if (pendingSpace && builder.Length < MaxDialogueChars) builder.Append(' ');
                pendingSpace = false;
                if (builder.Length >= MaxDialogueChars) break;
                builder.Append(c);
            }
            return builder.ToString().Trim();
        }

        public static int RewardGradeForConsummate(int consummateLevel)
        {
            if (consummateLevel <= 3) return MinRewardGrade;
            return Math.Min(MaxRewardGrade, Math.Max(MinRewardGrade,
                (consummateLevel - 2) / 2));
        }

        private static string CleanTargetName(string value)
        {
            string text = CleanDialogue(value);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return text.Length <= 80 ? text : text.Substring(0, 80).TrimEnd();
        }

        private static bool TryInteger(JToken token, out int value)
        {
            value = 0;
            return token != null && int.TryParse(token.ToString(), out value);
        }

        private static int Clamp(int value, int min, int max)
            => value < min ? min : value > max ? max : value;
    }
}
