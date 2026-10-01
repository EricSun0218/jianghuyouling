using System;
using JianghuYouling.Core.Commission;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.DevTest
{
    internal static class CommissionPolicyTests
    {
        internal static void Run()
        {
            var valid = JObject.Parse("{\"kind\":\"collect_resource\",\"resource\":\"药材\","
                + "\"amount\":999999,\"reward_grade\":\"一品\",\"request\":\"替我多备些药材。\"}");
            if (!CommissionProposalPolicy.TryParseArguments(valid, out CommissionProposal proposal,
                    out string error) || error != null || proposal == null
                || proposal.Kind != CommissionProposalPolicy.CollectResource
                || proposal.ResourceType != 5 || proposal.Amount != 10000
                || proposal.RewardGrade != 8
                || proposal.CompletionText.Length == 0)
                throw new InvalidOperationException("人物委托白名单、数量钳制或可见品级映射错误");

            if (!CommissionProposalPolicy.TryGrade("九品", out int ninth) || ninth != 0
                || !CommissionProposalPolicy.TryGrade("1", out int first) || first != 8
                || CommissionProposalPolicy.TryGrade("神品", out _))
                throw new InvalidOperationException("委托奖励品级没有严格限制在本体九到一品");

            var invalidKind = JObject.Parse("{\"kind\":\"kill_someone\",\"amount\":1,"
                + "\"reward_grade\":\"一品\",\"request\":\"去办。\"}");
            if (CommissionProposalPolicy.TryParseArguments(invalidKind, out _, out _))
                throw new InvalidOperationException("人物委托放行了白名单外任务");

            var kill = JObject.Parse("{\"kind\":\"kill_npc\",\"target\":\"顾长风\","
                + "\"amount\":999,\"reward_grade\":\"九品\",\"request\":\"替我除掉顾长风。\"}");
            if (!CommissionProposalPolicy.TryParseArguments(kill, out CommissionProposal killProposal,
                    out string killError) || killError != null || killProposal.Amount != 1
                || killProposal.TargetNpcName != "顾长风"
                || CommissionProposalPolicy.Objective(killProposal.Kind, -1, 1,
                    killProposal.TargetNpcName) != "杀死 顾长风")
                throw new InvalidOperationException("击杀委托目标解析或固定数量错误");
            if (CommissionProposalPolicy.RewardGradeForConsummate(0) != 0
                || CommissionProposalPolicy.RewardGradeForConsummate(3) != 0
                || CommissionProposalPolicy.RewardGradeForConsummate(4) != 1
                || CommissionProposalPolicy.RewardGradeForConsummate(17) != 7
                || CommissionProposalPolicy.RewardGradeForConsummate(18) != 8)
                throw new InvalidOperationException("击杀委托没有按本体精纯品阶映射奖励品级");

            var missingResource = JObject.Parse("{\"kind\":\"deliver_resource\",\"amount\":50,"
                + "\"reward_grade\":\"九品\",\"request\":\"交来。\"}");
            if (CommissionProposalPolicy.TryParseArguments(missingResource, out _, out _))
                throw new InvalidOperationException("资源委托没有强制要求资源类型");

            string objective = CommissionProposalPolicy.Objective(
                CommissionProposalPolicy.EarnMoney, -1, 3000);
            if (!objective.Contains("银钱") || !objective.Contains("3000"))
                throw new InvalidOperationException("委托目标显示与参数不一致");

            for (int grade = 1; grade <= CommissionProposalPolicy.MaxRewardGrade; grade++)
                for (int resourceType = 0; resourceType < 8; resourceType++)
                    if (CommissionRewardPolicy.BaseResourceAmount(grade, resourceType)
                        <= CommissionRewardPolicy.BaseResourceAmount(grade - 1, resourceType))
                        throw new InvalidOperationException("委托资源奖励没有随品级严格提高");
            if (CommissionRewardPolicy.BaseResourceAmount(8, 6)
                    != CommissionRewardPolicy.BaseResourceAmount(8, 0) * 4
                || CommissionRewardPolicy.BaseResourceAmount(8, 7)
                    != CommissionRewardPolicy.BaseResourceAmount(8, 0) * 2)
                throw new InvalidOperationException("银钱与威望没有使用各自的品级数量倍率");

            if (!CommissionProgressPolicy.IsCompleted(CommissionProposalPolicy.CollectResource,
                    1450, 1000, 400)
                || !CommissionProgressPolicy.IsCompleted(CommissionProposalPolicy.CollectResource,
                    int.MaxValue, int.MaxValue - 1000, 900)
                || CommissionProgressPolicy.IsCompleted(CommissionProposalPolicy.CollectResource,
                    1399, 1000, 400)
                || !CommissionProgressPolicy.IsCompleted(CommissionProposalPolicy.DeliverResource,
                    501, 0, 500))
                throw new InvalidOperationException("委托超额完成或整数上限附近的进度比较错误");

            int previousCenter = -1;
            for (int worldProgress = 0; worldProgress <= 9; worldProgress++)
            {
                int center = AssistantCommissionPolicy.ProgressGradeCenter(worldProgress);
                if (center < previousCenter || center < 0
                    || center > CommissionProposalPolicy.MaxRewardGrade)
                    throw new InvalidOperationException("灵儿委托品级没有跟随本体世界进度");
                previousCenter = center;
            }
            if (AssistantCommissionPolicy.ProgressGradeCenter(0) != 0
                || AssistantCommissionPolicy.ProgressGradeCenter(8) != 8
                || AssistantCommissionPolicy.ProgressGradeCenter(9) != 8)
                throw new InvalidOperationException("世界进度与本体可入队品级映射错误");
            var seenAtStart = new bool[9];
            var seenAtEnd = new bool[9];
            long startSum = 0, endSum = 0;
            // 连续覆盖完整权重环（最大总权重小于 250000），证明初期和后期
            // 的九个品级都存在非零区间，而不是靠随机冒烟碰运气。
            for (int roll = 0; roll < 250000; roll++)
            {
                int startGrade = AssistantCommissionPolicy.SelectRewardGrade(0, roll);
                int endGrade = AssistantCommissionPolicy.SelectRewardGrade(8, roll);
                seenAtStart[startGrade] = true;
                seenAtEnd[endGrade] = true;
                startSum += startGrade;
                endSum += endGrade;
            }
            if (Array.Exists(seenAtStart, value => !value)
                || Array.Exists(seenAtEnd, value => !value) || endSum <= startSum)
                throw new InvalidOperationException("世界进度没有只调整全品级抽取概率");
            int previousAmount = 0;
            CommissionProposalPolicy.AmountRange(CommissionProposalPolicy.CollectResource,
                out int assistantMin, out int assistantMax);
            for (int grade = 0; grade <= CommissionProposalPolicy.MaxRewardGrade; grade++)
            {
                int scaled = AssistantCommissionPolicy.ScaledAmount(assistantMin, assistantMax,
                    grade, 500);
                if (scaled < previousAmount || scaled < assistantMin || scaled > assistantMax)
                    throw new InvalidOperationException("灵儿委托目标数量没有随品级提高");
                previousAmount = scaled;
            }
            for (int seed = 0; seed < 100; seed++)
            {
                CommissionProposal generated = AssistantCommissionPolicy.Create(6,
                    new Random(seed));
                if (generated == null || generated.Kind == CommissionProposalPolicy.DeliverResource
                    || generated.Kind == CommissionProposalPolicy.IncreaseFavor
                    || generated.RewardGrade < CommissionProposalPolicy.MinRewardGrade
                    || generated.RewardGrade > CommissionProposalPolicy.MaxRewardGrade
                    || string.IsNullOrWhiteSpace(generated.OfferText)
                    || string.IsNullOrWhiteSpace(AssistantCommissionPolicy.Guidance(generated)))
                    throw new InvalidOperationException("灵儿纯代码委托生成越过允许任务或品级边界");
            }
            bool sawKill = false;
            for (int seed = 0; seed < 100; seed++)
            {
                CommissionProposal generated = AssistantCommissionPolicy.Create(6,
                    new Random(seed), 9527, "顾长风", 18);
                if (generated.Kind != CommissionProposalPolicy.KillNpc) continue;
                sawKill = true;
                if (generated.TargetNpcId != 9527 || generated.TargetNpcName != "顾长风"
                    || generated.TargetConsummate != 18 || generated.RewardGrade != 8
                    || !AssistantCommissionPolicy.Guidance(generated).Contains("死亡状态"))
                    throw new InvalidOperationException("灵儿击杀委托没有绑定目标、精纯品级或指导");
            }
            if (!sawKill) throw new InvalidOperationException("灵儿代码任务池没有生成击杀委托");
        }
    }
}
