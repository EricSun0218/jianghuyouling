using System;

namespace JianghuYouling.Core.Commission
{
    /// <summary>灵儿委托的纯代码生成策略；不读取提示词，也不调用任何模型。</summary>
    public static class AssistantCommissionPolicy
    {
        private static readonly string[] CollectRequests =
        {
            "我替你记了一项采买：再积攒些{0}吧，往后总有用得上的时候。",
            "江湖路远，手里备足{0}才踏实。我给你留了一项小委托。",
            "别只顾着赶路，顺手把{0}的储备添一添，这事交给你啦。",
        };

        private static readonly string[] MoneyRequests =
        {
            "行走江湖少不了盘缠，我给你记一项赚取银钱的委托。",
            "咱们把钱袋再充实些吧，这回看看你能添多少进账。",
            "后面的路花销不少，我替你定个攒银钱的小目标。",
        };

        private static readonly string[] PrestigeRequests =
        {
            "太吾的名望也该再响亮些，我给你留一项增长威望的委托。",
            "去江湖上再办几件让人记得住的事吧，我等你的好消息。",
            "这回替太吾扬一扬名声，攒够威望再来看看奖励。",
        };

        private static readonly string[] KillRequests =
        {
            "系统检索到一名需要清除的目标：{0}。这项任务危险些，奖励也会更丰厚。",
            "有一项新的清除任务：击杀{0}。我已经按对方的精纯评定了任务品级。",
            "这次是战斗任务，目标为{0}。量力而行，我会一直替你盯着任务进度。",
        };

        private static readonly string[] CompletionLines =
        {
            "做得漂亮，我就知道这点事难不住你。",
            "委托完成啦，这份奖励你安心收下。",
            "辛苦你了，答应你的奖励已经备好。",
        };

        /// <summary>把本体世界进度（也是允许入队品级）映射到任务品级概率中心。</summary>
        public static int ProgressGradeCenter(int worldProgress)
            => Math.Max(CommissionProposalPolicy.MinRewardGrade,
                Math.Min(CommissionProposalPolicy.MaxRewardGrade, worldProgress));

        /// <summary>
        /// 全品级始终有机会出现，世界进度只移动概率中心，不构成硬上限。
        /// 距离概率中心每远一级，权重按 0.55 倍递减。
        /// </summary>
        public static int SelectRewardGrade(int worldProgress, int roll)
        {
            int center = ProgressGradeCenter(worldProgress);
            var weights = new int[CommissionProposalPolicy.MaxRewardGrade + 1];
            int total = 0;
            for (int grade = CommissionProposalPolicy.MinRewardGrade;
                grade <= CommissionProposalPolicy.MaxRewardGrade; grade++)
            {
                int weight = 100000;
                for (int distance = Math.Abs(grade - center); distance > 0; distance--)
                    weight = Math.Max(1, weight * 55 / 100);
                weights[grade] = weight;
                total += weight;
            }
            int cursor = Math.Abs(roll == int.MinValue ? 0 : roll) % total;
            for (int grade = CommissionProposalPolicy.MinRewardGrade;
                grade <= CommissionProposalPolicy.MaxRewardGrade; grade++)
            {
                if (cursor < weights[grade]) return grade;
                cursor -= weights[grade];
            }
            return center;
        }

        public static CommissionProposal Create(int worldProgress, Random random)
            => Create(worldProgress, random, -1, null, -1);

        public static CommissionProposal Create(int worldProgress, Random random,
            int targetNpcId, string targetNpcName, int targetConsummate)
        {
            if (random == null) throw new ArgumentNullException(nameof(random));
            bool hasKillTarget = targetNpcId > 0 && !string.IsNullOrWhiteSpace(targetNpcName)
                && targetConsummate >= 0;
            int kindRoll = random.Next(hasKillTarget ? 4 : 3);
            string kind = kindRoll == 0 ? CommissionProposalPolicy.CollectResource
                : kindRoll == 1 ? CommissionProposalPolicy.EarnMoney
                : kindRoll == 2 ? CommissionProposalPolicy.GainPrestige
                : CommissionProposalPolicy.KillNpc;
            int resourceType = kind == CommissionProposalPolicy.CollectResource
                ? random.Next(6) : -1;
            int grade = kind == CommissionProposalPolicy.KillNpc
                ? CommissionProposalPolicy.RewardGradeForConsummate(targetConsummate)
                : SelectRewardGrade(worldProgress, random.Next());
            CommissionProposalPolicy.AmountRange(kind, out int minimum, out int maximum);
            int amount = ScaledAmount(minimum, maximum, grade, random.Next(1001));
            string resource = CommissionProposalPolicy.ResourceName(resourceType) ?? "资源";
            string[] requests = kind == CommissionProposalPolicy.CollectResource
                ? CollectRequests : kind == CommissionProposalPolicy.EarnMoney
                    ? MoneyRequests : kind == CommissionProposalPolicy.GainPrestige
                        ? PrestigeRequests : KillRequests;
            return new CommissionProposal
            {
                Kind = kind,
                ResourceType = resourceType,
                ResourceName = CommissionProposalPolicy.ResourceName(resourceType),
                Amount = amount,
                TargetNpcId = kind == CommissionProposalPolicy.KillNpc ? targetNpcId : -1,
                TargetNpcName = kind == CommissionProposalPolicy.KillNpc ? targetNpcName : null,
                TargetConsummate = kind == CommissionProposalPolicy.KillNpc
                    ? targetConsummate : -1,
                RewardGrade = grade,
                OfferText = string.Format(requests[random.Next(requests.Length)],
                    kind == CommissionProposalPolicy.KillNpc ? targetNpcName : resource),
                CompletionText = CompletionLines[random.Next(CompletionLines.Length)],
            };
        }

        public static int ScaledAmount(int minimum, int maximum, int grade, int rollPermille)
        {
            minimum = Math.Max(1, minimum);
            maximum = Math.Max(minimum, maximum);
            grade = Math.Max(CommissionProposalPolicy.MinRewardGrade,
                Math.Min(CommissionProposalPolicy.MaxRewardGrade, grade));
            rollPermille = Math.Max(0, Math.Min(1000, rollPermille));
            // 品级决定数量带，随机只在当前带内浮动；世界推进后目标整体单调提高。
            long span = maximum - (long)minimum;
            long bandStart = span * grade / 12;
            long bandWidth = Math.Max(1L, span / 12);
            long value = minimum + bandStart + bandWidth * rollPermille / 1000;
            return (int)Math.Max(minimum, Math.Min(maximum, value));
        }

        public static string Guidance(CommissionProposal proposal)
        {
            if (proposal == null) return string.Empty;
            switch (proposal.Kind)
            {
                case CommissionProposalPolicy.CollectResource:
                    return "可通过采集、购买、交换或其它本体玩法取得" +
                        (proposal.ResourceName ?? "资源") + "；任务计算的是接受后净增加的持有量，达到或超过目标都算完成。";
                case CommissionProposalPolicy.EarnMoney:
                    return "可通过经营、出售物品、完成本体玩法等方式增加银钱；按接受委托后的净增量计算，达到或超过目标即可。";
                case CommissionProposalPolicy.GainPrestige:
                    return "可完成能获得威望的本体事件与行动；按接受委托后的净增量计算，达到或超过目标即可。";
                case CommissionProposalPolicy.KillNpc:
                    return "目标是" + (proposal.TargetNpcName ?? "指定人物")
                        + "；任务会直接核验本体死亡状态，目标死亡即可领取，奖励品级按其精纯评定且比普通委托更丰厚。";
                default:
                    return "打开右侧委托簿即可查看代码核验的实时进度。";
            }
        }
    }
}
