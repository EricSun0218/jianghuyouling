using System;
using System.IO;
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
                    || !CommissionProposalPolicy.ValidOfferedRewardGrade(generated.Kind, generated.RewardGrade)
                    || generated.Kind == CommissionProposalPolicy.KillNpc
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
            TestOutlawNestPolicy();
            TestCommissionPersistence();
            TestIntegrationContracts();
            Console.WriteLine("[PASS] 委托策略、清巢事实、存档/回退与接线回归");
        }

        private static CommissionProposal OutlawProposal()
        {
            for (int seed = 0; seed < 100; seed++)
            {
                var proposal = AssistantCommissionPolicy.Create(8, new Random(seed));
                if (proposal.Kind == CommissionProposalPolicy.ClearOutlawNest) return proposal;
            }
            throw new InvalidOperationException("无仇人时也应能生成外道巢穴委托");
        }

        private static void TestOutlawNestPolicy()
        {
            var proposal = OutlawProposal();
            Check(proposal.Amount == 1 && proposal.RewardGrade == -1
                && proposal.TargetNpcId == -1 && proposal.TargetConsummate == -1,
                "清巢每单固定一处，接单时不能预定奖励品级或人物目标");
            Check(CommissionProposalPolicy.Objective(proposal.Kind, -1, 1).Contains("外道巢穴")
                && AssistantCommissionPolicy.Guidance(proposal).Contains("首次成功"), "清巢目标与指导");
            Check(CommissionProposalPolicy.HasCombatRewards(proposal.Kind), "清巢采用战斗奖励");
            Check(!CommissionProposalPolicy.TryParseArguments(JObject.Parse(
                "{\"kind\":\"clear_outlaw_nest\",\"amount\":1,\"reward_grade\":\"一品\",\"request\":\"清巢。\"}"),
                out _, out _), "NPC / 模型不能发布灵儿专属清巢委托");
            Check(!CommissionProposalPolicy.ValidOfferedRewardGrade(proposal.Kind, 8)
                && !CommissionProposalPolicy.ValidOfferedRewardGrade(CommissionProposalPolicy.EarnMoney, -1),
                "未定品级只适用于清巢");
            for (int grade = 0; grade <= 8; grade++)
                Check(OutlawNestProgressPolicy.RewardGradeForDifficulty(grade) == grade,
                    "巢穴难度应映射到同级奖励");
            Check(OutlawNestProgressPolicy.RewardGradeForDifficulty(-1) == -1
                && OutlawNestProgressPolicy.RewardGradeForDifficulty(9) == -1, "非法难度不可发奖");
            Check(OutlawNestProgressPolicy.IsOutlawNest(123, 123, 0)
                && !OutlawNestProgressPolicy.IsOutlawNest(123, 123, 1)
                && !OutlawNestProgressPolicy.IsOutlawNest(123, 124, 0)
                && !OutlawNestProgressPolicy.IsOutlawNest(0, 0, 0), "外道、义士和其他奇遇分类");

            // 前两次发生在接单前；之后先清低难度、再清高难度，也只取第一次。
            Check(!OutlawNestProgressPolicy.TryFirstCompletionGrade(2, 2, _ => "68", out _),
                "不能用接单前的清巢完成任务");
            Check(OutlawNestProgressPolicy.TryFirstCompletionGrade(3, 2, _ => "680", out int first)
                && first == 0, "接单后首次成功结算");
            Check(OutlawNestProgressPolicy.TryFirstCompletionGrade(4, 2, _ => "6808", out int locked)
                && locked == first, "后续更难的清巢不改动原单品级");
            Check(!OutlawNestProgressPolicy.TryFirstCompletionGrade(1, 2, _ => "6", out _)
                && !OutlawNestProgressPolicy.TryFirstCompletionGrade(2, 2, _ => "68", out _),
                "存档回退不能保留未来完成事实");
            Check(!OutlawNestProgressPolicy.TryFirstCompletionGrade(3, -1, _ => "680", out _)
                && !OutlawNestProgressPolicy.TryFirstCompletionGrade(3, 2, _ => "68x", out _)
                && !OutlawNestProgressPolicy.TryFirstCompletionGrade(3, 2, _ => null, out _),
                "缺失或损坏的完成事实必须拒绝发奖");
            int boundary = OutlawNestProgressPolicy.PageSize;
            Func<int, string> pages = page => page == 0 ? new string('0', boundary) : "64";
            Check(OutlawNestProgressPolicy.TryFirstCompletionGrade(boundary + 2, boundary - 1,
                    pages, out int previousPage) && previousPage == 0
                && OutlawNestProgressPolicy.TryFirstCompletionGrade(boundary + 2, boundary,
                    pages, out int nextPage) && nextPage == 6, "清巢历史跨页边界");
        }

        private static void TestCommissionPersistence()
        {
            string oldRoot = JianghuYoulingPaths.Root;
            uint oldWorld = JianghuYoulingPaths.CurrentWorldId;
            string root = Path.Combine(Path.GetTempPath(), "JHYL_Commission_" + Guid.NewGuid().ToString("N"));
            try
            {
                JianghuYoulingPaths.Root = root;
                JianghuYoulingPaths.CurrentWorldId = 982371;
                Directory.CreateDirectory(JianghuYoulingPaths.Intents);
                const int taiwu = 100;
                var ordinary = new CommissionProposal
                {
                    Kind = CommissionProposalPolicy.EarnMoney, Amount = 500, RewardGrade = 0,
                    OfferText = "备些盘缠。", CompletionText = "收下吧。",
                };
                Check(CommissionStore.TryIssueAssistant(taiwu, 10, ordinary, 1000,
                    out CommissionRecord existing, out _), "原普通委托仍可发布");
                CommissionProposal nest = OutlawProposal();
                Check(!CommissionStore.TryIssue(taiwu, 101, "人物", 10, nest, 2, out _, out _),
                    "清巢任务不能由 NPC 写入");
                Check(CommissionStore.TryIssueAssistant(taiwu, 10, nest, 2,
                    out CommissionRecord issued, out _), "保存灵儿清巢委托");
                var reloaded = CommissionStore.AllForTaiwu(taiwu, 10);
                Check(reloaded.Count == 2 && reloaded.Exists(x => x.Id == existing.Id)
                    && reloaded.Exists(x => x.Id == issued.Id && x.RewardGrade == -1
                        && x.BaselineValue == 2 && x.RewardLabel.Contains("难度")),
                    "未定品级重新加载不丢单，也不删除原有委托");
                Check(issued.RewardLabelFor(6).StartsWith("三品")
                    && issued.RewardGrade == -1, "完成后显示真实品级，不篡改接单参数");
                Check(CommissionStore.MarkClaimed(taiwu, issued.Id, 12, "bundle", "组合奖励", 1, 6,
                    out CommissionRecord claimed) && claimed.ActualRewardGrade == 6
                    && claimed.RewardLabel.StartsWith("三品"), "真实领奖品级持久化");
                Check(CommissionStore.PruneForRollback(taiwu, 11), "清巢领奖后回退");
                var restored = CommissionStore.AllForTaiwu(taiwu, 11).Find(x => x.Id == issued.Id);
                Check(restored != null && restored.IsActive && restored.ActualRewardGrade == -1
                    && restored.RewardGrade == -1 && restored.ClaimOperationId != issued.ClaimOperationId,
                    "回档重置已领奖状态与回执身份，但保留原始接单基线");
                JianghuYoulingPaths.CurrentWorldId++;
                Check(CommissionStore.AllForTaiwu(taiwu, 11).Count == 0, "不同世界不混用清巢委托");
            }
            finally
            {
                JianghuYoulingPaths.Root = oldRoot;
                JianghuYoulingPaths.CurrentWorldId = oldWorld;
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private static void TestIntegrationContracts()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
            Check(dir != null, "测试仓库根目录");
            string backend = File.ReadAllText(Path.Combine(dir.FullName, "src", "JianghuYouling.Backend", "BackendPluginMain.cs"));
            string capture = File.ReadAllText(Path.Combine(dir.FullName, "src", "JianghuYouling.Backend", "OutlawNestCommissionProgress.cs"));
            string widget = File.ReadAllText(Path.Combine(dir.FullName, "src", "JianghuYouling.Frontend", "UI", "AssistantWidget.cs"));
            int selectStart = backend.IndexOf("private static int SelectAssistantCommissionKillTarget(", StringComparison.Ordinal);
            int selectEnd = backend.IndexOf("private sealed class CommissionRewardTemplate", selectStart, StringComparison.Ordinal);
            string select = backend.Substring(selectStart, selectEnd - selectStart);
            Check(select.Contains("?.Enemies.GetCollection()")
                && select.Contains("!DomainManager.Character.IsCharacterAlive(targetId)")
                && select.Contains("DomainManager.Character.TryGetDeadCharacter(targetId) != null")
                && !select.Contains("?.RelatedEnemies"), "杀人候选只取太吾活着的仇人");
            int issueStart = widget.IndexOf("void TickAssistantCommission(", StringComparison.Ordinal);
            int issueEnd = widget.IndexOf("private sealed class InteractivePriorityLease", issueStart, StringComparison.Ordinal);
            string issue = widget.Substring(issueStart, issueEnd - issueStart);
            Check(!issue.Contains("ConversationSessionIndexStore") && issue.Contains("selectKillTarget: true"),
                "旧的已聊天人物候选池必须直接移除");
            Check(capture.Contains("nameof(AdventureDomain.FinishAdventure)")
                && capture.Contains("if (!__result || __state == null) return;")
                && capture.Contains("DomainManager.Mod.SetSerializableModData(context, ModId, DataName(taiwuId), true, next)"),
                "只有玩家真实通关成功才记录，且记录进入本体存档域");
            Check(backend.Contains("OutlawNestCommissionProgress.Read(taiwuId, baseline, out current, out rewardGrade)")
                && backend.Contains("killCommission || outlawCommission ? 3 + context.Random.Next(2) : 2"),
                "后端独立核验清巢完成事实与3至4项奖励");
            int claimStart = backend.IndexOf("private static SerializableModData ClaimCommissionReward(", StringComparison.Ordinal);
            int claimEnd = backend.IndexOf("private static bool CommissionAmountRange(", claimStart, StringComparison.Ordinal);
            Check(!backend.Substring(claimStart, claimEnd - claimStart).Contains(".Enemies"),
                "领奖不能因后续和解取消杀人委托");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
