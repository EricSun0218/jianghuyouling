using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JianghuYouling.Core.Commission;
using JianghuYouling.Core.Tools;
using Newtonsoft.Json;

namespace JianghuYouling
{
    public sealed class CommissionRecord
    {
        public string Id;
        public uint WorldId;
        public int TaiwuId;
        public int NpcId;
        public string NpcName;
        public string Status;
        public int AcceptedDate;
        public int ClaimedDate = -1;
        public string Kind;
        public int ResourceType = -1;
        public string ResourceName;
        public int Amount;
        public int BaselineValue;
        public int TargetNpcId = -1;
        public string TargetNpcName;
        public int TargetConsummate = -1;
        public int RewardGrade;
        public string ActualRewardKind;
        public string ActualRewardName;
        public int ActualRewardAmount;
        public int ActualRewardGrade = -1;
        public string OfferText;
        public string CompletionText;
        public string ClaimOperationId;

        public bool IsActive => string.Equals(Status, CommissionStore.Active,
            StringComparison.Ordinal);
        public bool IsAssistantCommission => NpcId == CommissionStore.AssistantCommissionerId;
        public string Objective => CommissionProposalPolicy.Objective(Kind, ResourceType, Amount,
            TargetNpcName);
        public string RewardLabel => CommissionProposalPolicy.GradeName(RewardGrade)
            + (string.Equals(Kind, CommissionProposalPolicy.KillNpc, StringComparison.Ordinal)
                ? "丰厚组合奖励（3至4项）" : "组合奖励（2项）");
    }

    /// <summary>按本体世界隔离的委托状态。领奖使用稳定 OperationId，回调丢失后重试不会重复发奖。</summary>
    public static class CommissionStore
    {
        public const string Active = "active";
        public const string Claimed = "claimed";
        public const string Abandoned = "abandoned";
        public const int AssistantCommissionerId = 0;
        private const int CurrentVersion = 2;
        private const int MaxRecords = 128;
        private const int MaxActive = 40;
        private static readonly object Gate = new object();

        private sealed class State
        {
            public int Version = CurrentVersion;
            public uint WorldId;
            public int TaiwuId;
            public List<CommissionRecord> Records = new List<CommissionRecord>();
        }

        private static string PathFor(int taiwuId)
            => Path.Combine(JianghuYoulingPaths.Intents, "commissions_" + taiwuId + ".json");

        public static CommissionRecord ActiveForNpc(int taiwuId, int npcId, int currentDate)
        {
            lock (Gate)
            {
                State state = LoadState(taiwuId, currentDate, true);
                return Clone(state.Records.LastOrDefault(x => x != null && x.NpcId == npcId
                    && x.IsActive));
            }
        }

        public static List<CommissionRecord> AllForTaiwu(int taiwuId, int currentDate)
        {
            lock (Gate)
            {
                State state = LoadState(taiwuId, currentDate, true);
                return state.Records.OrderByDescending(x => x.IsActive)
                    .ThenByDescending(x => x.AcceptedDate).ThenByDescending(x => x.Id)
                    .Select(Clone).ToList();
            }
        }

        public static List<CommissionRecord> ForNpc(int taiwuId, int npcId, int currentDate)
            => AllForTaiwu(taiwuId, currentDate).Where(x => x.NpcId == npcId).Take(12).ToList();

        public static int ActiveCount(int taiwuId, int currentDate)
            => AllForTaiwu(taiwuId, currentDate).Count(x => x.IsActive);

        public static bool TryIssue(int taiwuId, int npcId, string npcName, int currentDate,
            CommissionProposal proposal, int baselineValue, out CommissionRecord issued,
            out string error)
            => TryIssueCore(taiwuId, npcId, npcName, currentDate, proposal, baselineValue,
                false, out issued, out error);

        public static bool TryIssueAssistant(int taiwuId, int currentDate,
            CommissionProposal proposal, int baselineValue, out CommissionRecord issued,
            out string error)
            => TryIssueCore(taiwuId, AssistantCommissionerId, "灵儿", currentDate, proposal,
                baselineValue, true, out issued, out error);

        private static bool TryIssueCore(int taiwuId, int npcId, string npcName, int currentDate,
            CommissionProposal proposal, int baselineValue, bool assistantCommission,
            out CommissionRecord issued, out string error)
        {
            issued = null;
            error = null;
            if (!ValidProposal(taiwuId, npcId, currentDate, proposal, assistantCommission))
            {
                error = "委托参数无效";
                return false;
            }
            lock (Gate)
            {
                State state = LoadState(taiwuId, currentDate, true);
                if (!assistantCommission
                    && state.Records.Any(x => x != null && x.NpcId == npcId && x.IsActive))
                {
                    error = "这名人物已经有一项进行中的委托";
                    return false;
                }
                if (state.Records.Count(x => x != null && x.IsActive) >= MaxActive)
                {
                    error = "进行中的委托已经达到上限";
                    return false;
                }
                string id = OperationId.New();
                var record = new CommissionRecord
                {
                    Id = id,
                    WorldId = JianghuYoulingPaths.CurrentWorldId,
                    TaiwuId = taiwuId,
                    NpcId = npcId,
                    NpcName = Clean(npcName, 80, "江湖人物"),
                    Status = Active,
                    AcceptedDate = currentDate,
                    Kind = proposal.Kind,
                    ResourceType = proposal.ResourceType,
                    ResourceName = CommissionProposalPolicy.ResourceName(proposal.ResourceType),
                    Amount = proposal.Amount,
                    BaselineValue = Math.Max(0, baselineValue),
                    TargetNpcId = proposal.TargetNpcId,
                    TargetNpcName = Clean(proposal.TargetNpcName, 80, string.Empty),
                    TargetConsummate = proposal.TargetConsummate,
                    RewardGrade = proposal.RewardGrade,
                    OfferText = Clean(proposal.OfferText,
                        CommissionProposalPolicy.MaxDialogueChars, "可否替我办一件事？"),
                    CompletionText = Clean(proposal.CompletionText,
                        CommissionProposalPolicy.MaxDialogueChars, "有劳了，这份情我记下了。"),
                    ClaimOperationId = OperationId.FromStableKey("commission-claim|"
                        + JianghuYoulingPaths.CurrentWorldId + "|" + taiwuId + "|" + id),
                };
                state.Records.Add(record);
                Normalize(state, taiwuId);
                if (!SaveState(taiwuId, state))
                {
                    error = "委托未能写入存档外状态";
                    return false;
                }
                issued = Clone(record);
                return true;
            }
        }

        public static bool CanIssueAssistant(int taiwuId, int currentDate)
        {
            if (taiwuId <= 0 || currentDate < 0) return false;
            lock (Gate)
            {
                State state = LoadState(taiwuId, currentDate, true);
                return state.Records.Count(x => x != null && x.IsActive) < MaxActive;
            }
        }

        public static bool MarkClaimed(int taiwuId, string commissionId, int currentDate,
            string rewardKind, string rewardName, int rewardAmount, int rewardGrade,
            out CommissionRecord claimed)
        {
            claimed = null;
            lock (Gate)
            {
                State state = LoadState(taiwuId, currentDate, true);
                CommissionRecord record = state.Records.FirstOrDefault(x => x != null
                    && string.Equals(x.Id, commissionId, StringComparison.Ordinal));
                if (record == null) return false;
                if (!record.IsActive)
                {
                    claimed = Clone(record);
                    return string.Equals(record.Status, Claimed, StringComparison.Ordinal);
                }
                record.Status = Claimed;
                record.ClaimedDate = Math.Max(record.AcceptedDate, currentDate);
                record.ActualRewardKind = Clean(rewardKind, 32, "reward");
                record.ActualRewardName = Clean(rewardName, 120, "奖励");
                record.ActualRewardAmount = Math.Max(1, rewardAmount);
                record.ActualRewardGrade = Math.Max(CommissionProposalPolicy.MinRewardGrade,
                    Math.Min(CommissionProposalPolicy.MaxRewardGrade, rewardGrade));
                Normalize(state, taiwuId);
                if (!SaveState(taiwuId, state)) return false;
                claimed = Clone(record);
                return true;
            }
        }

        public static bool Abandon(int taiwuId, string commissionId, int currentDate)
        {
            lock (Gate)
            {
                State state = LoadState(taiwuId, currentDate, true);
                CommissionRecord record = state.Records.FirstOrDefault(x => x != null
                    && string.Equals(x.Id, commissionId, StringComparison.Ordinal) && x.IsActive);
                if (record == null) return false;
                record.Status = Abandoned;
                record.ClaimedDate = Math.Max(record.AcceptedDate, currentDate);
                Normalize(state, taiwuId);
                return SaveState(taiwuId, state);
            }
        }

        public static bool ReplaceIdentity(int taiwuId, int oldNpcId, int newNpcId)
        {
            if (taiwuId <= 0 || oldNpcId <= 0 || newNpcId <= 0 || oldNpcId == newNpcId)
                return true;
            lock (Gate)
            {
                int currentDate = CurrentDate();
                State state = LoadState(taiwuId, currentDate, true);
                bool changed = false;
                foreach (CommissionRecord record in state.Records)
                    if (record != null && record.NpcId == oldNpcId)
                    {
                        record.NpcId = newNpcId;
                        changed = true;
                    }
                if (!changed) return true;
                Normalize(state, taiwuId);
                return SaveState(taiwuId, state);
            }
        }

        public static bool PruneForRollback(int taiwuId, int currentDate)
        {
            if (taiwuId <= 0 || currentDate < 0) return false;
            lock (Gate)
            {
                State state = LoadState(taiwuId, currentDate, false);
                bool changed = ApplyTimeline(state, currentDate);
                Normalize(state, taiwuId);
                return !changed || SaveState(taiwuId, state);
            }
        }

        public static string PromptContext(int taiwuId, int npcId, int currentDate)
        {
            List<CommissionRecord> records = ForNpc(taiwuId, npcId, currentDate);
            if (records.Count == 0) return null;
            var lines = new List<string>();
            CommissionRecord active = records.FirstOrDefault(x => x.IsActive);
            if (active != null)
                lines.Add("当前仍有一项你亲自交给太吾的委托：" + active.Objective
                    + "；承诺的是" + active.RewardLabel
                    + "。在委托界面收到权威完成回执前绝不能说已经完成或已经发奖，"
                    + "也不能再次发布新委托；可以依人设自然询问进展。");
            foreach (CommissionRecord record in records.Where(x => string.Equals(x.Status,
                Claimed, StringComparison.Ordinal)).Take(2))
                lines.Add("太吾已经完成你此前的委托（" + record.Objective + "），并领取了"
                    + (record.ActualRewardName ?? "奖励") + "；此事已有权威领奖回执，可以自然记得并提起。");
            return lines.Count == 0 ? null : "【人物委托事实】\n" + string.Join("\n", lines.ToArray());
        }

        public static string AssistantPromptContext(int taiwuId, int currentDate)
        {
            if (taiwuId <= 0 || currentDate < 0) return null;
            List<CommissionRecord> records = ForNpc(taiwuId, AssistantCommissionerId,
                currentDate);
            if (records.Count == 0) return null;
            var lines = new List<string>();
            List<CommissionRecord> activeRecords = records.Where(x => x.IsActive).ToList();
            if (activeRecords.Count > 0)
            {
                lines.Add("你当前亲自交给太吾的进行中委托共有 " + activeRecords.Count
                    + " 项；以下均是代码已经写入委托簿的权威事实：");
                foreach (CommissionRecord active in activeRecords)
                {
                    var proposal = new CommissionProposal
                    {
                        Kind = active.Kind, ResourceType = active.ResourceType,
                        ResourceName = active.ResourceName, Amount = active.Amount,
                        TargetNpcId = active.TargetNpcId,
                        TargetNpcName = active.TargetNpcName,
                        TargetConsummate = active.TargetConsummate,
                        RewardGrade = active.RewardGrade,
                    };
                    lines.Add("- " + active.Objective + "；承诺" + active.RewardLabel + "。"
                        + AssistantCommissionPolicy.Guidance(proposal));
                }
                lines.Add("玩家询问时可以结合对应规则耐心指导；领取前不能声称任何一项"
                    + "已经完成或奖励已经发放。");
            }
            foreach (CommissionRecord record in records.Where(x => string.Equals(x.Status,
                Claimed, StringComparison.Ordinal)).Take(2))
                lines.Add("太吾已完成你此前的委托（" + record.Objective + "），并领取了"
                    + (record.ActualRewardName ?? "奖励") + "；可以自然记得并提起。 ");
            return lines.Count == 0 ? null : "【灵儿委托记忆与指导】\n"
                + string.Join("\n", lines.ToArray());
        }

        private static bool ValidProposal(int taiwuId, int npcId, int currentDate,
            CommissionProposal proposal, bool assistantCommission)
        {
            bool validIdentity = assistantCommission
                ? npcId == AssistantCommissionerId
                : npcId > 0 && npcId != taiwuId;
            if (taiwuId <= 0 || !validIdentity || currentDate < 0
                || proposal == null || !CommissionProposalPolicy.IsKind(proposal.Kind)
                || proposal.RewardGrade < CommissionProposalPolicy.MinRewardGrade
                || proposal.RewardGrade > CommissionProposalPolicy.MaxRewardGrade
                || string.IsNullOrWhiteSpace(proposal.OfferText)) return false;
            if (assistantCommission && (string.Equals(proposal.Kind,
                    CommissionProposalPolicy.DeliverResource, StringComparison.Ordinal)
                || string.Equals(proposal.Kind, CommissionProposalPolicy.IncreaseFavor,
                    StringComparison.Ordinal))) return false;
            if (!CommissionProposalPolicy.AmountRange(proposal.Kind, out int min, out int max)
                || proposal.Amount < min || proposal.Amount > max) return false;
            if (string.Equals(proposal.Kind, CommissionProposalPolicy.KillNpc,
                    StringComparison.Ordinal)
                && (proposal.TargetNpcId <= 0 || proposal.TargetNpcId == taiwuId
                    || (!assistantCommission && proposal.TargetNpcId == npcId)
                    || string.IsNullOrWhiteSpace(proposal.TargetNpcName)
                    || proposal.TargetConsummate < 0)) return false;
            return !CommissionProposalPolicy.NeedsResource(proposal.Kind)
                || CommissionProposalPolicy.ResourceName(proposal.ResourceType) != null;
        }

        private static State LoadState(int taiwuId, int currentDate, bool persistTimelineRepair)
        {
            State state = NewState(taiwuId);
            string path = PathFor(taiwuId);
            try
            {
                if (File.Exists(path))
                {
                    string raw = File.ReadAllText(path);
                    if (raw.Length <= DurableSettingsStore.Text)
                        state = JsonConvert.DeserializeObject<State>(raw) ?? state;
                }
            }
            catch { state = NewState(taiwuId); }
            if (state.Version != CurrentVersion || state.WorldId != JianghuYoulingPaths.CurrentWorldId
                || state.TaiwuId != taiwuId) state = NewState(taiwuId);
            bool changed = ApplyTimeline(state, currentDate);
            Normalize(state, taiwuId);
            if (changed && persistTimelineRepair) SaveState(taiwuId, state);
            return state;
        }

        private static bool ApplyTimeline(State state, int currentDate)
        {
            if (state == null || currentDate < 0) return false;
            int before = state.Records?.Count ?? 0;
            if (state.Records == null) state.Records = new List<CommissionRecord>();
            state.Records.RemoveAll(x => x == null || x.AcceptedDate > currentDate);
            bool changed = state.Records.Count != before;
            foreach (CommissionRecord record in state.Records)
            {
                if (record.ClaimedDate <= currentDate) continue;
                record.Status = Active;
                record.ClaimedDate = -1;
                record.ActualRewardKind = null;
                record.ActualRewardName = null;
                record.ActualRewardAmount = 0;
                record.ActualRewardGrade = -1;
                // 本体回档已撤销奖励；旧 operationId 的后端成功回执仍可能存在，
                // 必须按回档后的时间线换一个稳定 ID，避免重做任务时只读到旧回执而不发奖。
                record.ClaimOperationId = OperationId.FromStableKey("commission-reclaim|"
                    + record.WorldId + "|" + record.TaiwuId + "|" + record.Id + "|" + currentDate);
                changed = true;
            }
            return changed;
        }

        private static void Normalize(State state, int taiwuId)
        {
            state.Version = CurrentVersion;
            state.WorldId = JianghuYoulingPaths.CurrentWorldId;
            state.TaiwuId = taiwuId;
            if (state.Records == null) state.Records = new List<CommissionRecord>();
            state.Records.RemoveAll(x => !Valid(x, taiwuId));
            state.Records = state.Records.OrderByDescending(x => x.IsActive)
                .ThenByDescending(x => x.AcceptedDate).ThenByDescending(x => x.Id)
                .Take(MaxRecords).ToList();
        }

        private static bool Valid(CommissionRecord value, int taiwuId)
        {
            if (value == null || !OperationId.IsValid(value.Id)
                || !OperationId.IsValid(value.ClaimOperationId)
                || value.WorldId != JianghuYoulingPaths.CurrentWorldId
                || value.TaiwuId != taiwuId || value.NpcId < AssistantCommissionerId
                || value.NpcId == taiwuId
                || value.AcceptedDate < 0 || value.ClaimedDate < -1
                || !CommissionProposalPolicy.IsKind(value.Kind)
                || value.RewardGrade < CommissionProposalPolicy.MinRewardGrade
                || value.RewardGrade > CommissionProposalPolicy.MaxRewardGrade
                || !CommissionProposalPolicy.AmountRange(value.Kind, out int min, out int max)
                || value.Amount < min || value.Amount > max
                || (string.Equals(value.Kind, CommissionProposalPolicy.KillNpc,
                        StringComparison.Ordinal)
                    && (value.TargetNpcId <= 0 || value.TargetNpcId == taiwuId
                        || (!value.IsAssistantCommission && value.TargetNpcId == value.NpcId)
                        || string.IsNullOrWhiteSpace(value.TargetNpcName)
                        || value.TargetConsummate < 0))
                || (value.IsAssistantCommission
                    && (string.Equals(value.Kind, CommissionProposalPolicy.DeliverResource,
                            StringComparison.Ordinal)
                        || string.Equals(value.Kind, CommissionProposalPolicy.IncreaseFavor,
                            StringComparison.Ordinal)))
                || (CommissionProposalPolicy.NeedsResource(value.Kind)
                    && CommissionProposalPolicy.ResourceName(value.ResourceType) == null)
                || string.IsNullOrWhiteSpace(value.OfferText)
                || string.IsNullOrWhiteSpace(value.CompletionText)) return false;
            return string.Equals(value.Status, Active, StringComparison.Ordinal)
                || string.Equals(value.Status, Claimed, StringComparison.Ordinal)
                || string.Equals(value.Status, Abandoned, StringComparison.Ordinal);
        }

        private static bool SaveState(int taiwuId, State state)
        {
            try
            {
                Normalize(state, taiwuId);
                string raw = JsonConvert.SerializeObject(state, Formatting.Indented);
                return DurableSettingsStore.Save(PathFor(taiwuId), raw,
                    DurableSettingsStore.Text, value => !string.IsNullOrWhiteSpace(value)
                    && value.Length <= DurableSettingsStore.Text && value.IndexOf('\0') < 0);
            }
            catch { return false; }
        }

        private static State NewState(int taiwuId)
            => new State { WorldId = JianghuYoulingPaths.CurrentWorldId, TaiwuId = taiwuId };

        private static CommissionRecord Clone(CommissionRecord value)
        {
            if (value == null) return null;
            return new CommissionRecord
            {
                Id = value.Id, WorldId = value.WorldId, TaiwuId = value.TaiwuId,
                NpcId = value.NpcId, NpcName = value.NpcName, Status = value.Status,
                AcceptedDate = value.AcceptedDate, ClaimedDate = value.ClaimedDate,
                Kind = value.Kind, ResourceType = value.ResourceType,
                ResourceName = value.ResourceName, Amount = value.Amount,
                BaselineValue = value.BaselineValue, RewardGrade = value.RewardGrade,
                TargetNpcId = value.TargetNpcId, TargetNpcName = value.TargetNpcName,
                TargetConsummate = value.TargetConsummate,
                ActualRewardKind = value.ActualRewardKind,
                ActualRewardName = value.ActualRewardName,
                ActualRewardAmount = value.ActualRewardAmount,
                ActualRewardGrade = value.ActualRewardGrade,
                OfferText = value.OfferText, CompletionText = value.CompletionText,
                ClaimOperationId = value.ClaimOperationId,
            };
        }

        private static string Clean(string value, int max, string fallback)
        {
            string text = (value ?? string.Empty).Replace("\0", string.Empty).Trim();
            if (text.Length == 0) text = fallback;
            return text.Length <= max ? text : text.Substring(0, max).TrimEnd();
        }

        private static int CurrentDate()
        {
            try
            {
                BasicGameData data = SingletonObject.getInstance<BasicGameData>();
                return data == null ? -1 : data.CurrDate;
            }
            catch { return -1; }
        }
    }
}
