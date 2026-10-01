using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using JianghuYouling.Core.Tools;
using JianghuYouling.Core.Persistence;
using JianghuYouling.Core.Prompt;

namespace JianghuYouling
{
    public sealed class EventSagaGoal
    {
        public string Id;
        public string Summary;
        public string SuccessCriteria;
        public int MinimumSucceededSteps = 1;
        public int SucceededSteps;
        public int FailedSteps;
        public string Status = "active"; // active | achieved | failed | abandoned
        public int UpdatedDate;
    }

    public sealed class EventSagaOpenLoop
    {
        public string Id;
        public string Kind;
        public string Summary;
        public string Status = "open"; // open | resolved | blocked
        public List<int> ParticipantIds = new List<int>();
        public List<string> EvidenceIds = new List<string>();
        public int NextEligibleDate;
    }

    public sealed class EventSagaConstraint
    {
        public string Id;
        public string Kind;
        public string Value;
        public bool Active = true;
    }

    public sealed class EventSagaStepOutcome
    {
        public string OperationId;
        public string StepId;
        public string ToolName;
        public string Status; // prepared | succeeded | failed | unknown
        public string Code;
        public string Intent;
        public string Summary;
        public string ArgumentsJson;
        // Exact stable key and digest cross-bind the immutable dispatch envelope to OperationId.
        // Legacy records may omit them and remain query-only; they are never redispatched.
        public string OperationBindingKey;
        // 派发时解析完成的实体 id 与动态物品选择。prepared 恢复只能使用这份信封，
        // 绝不能重新按姓名、名册序号或当前背包猜一次。
        public string DispatchEnvelopeJson;
        public string DispatchEnvelopeDigest;
        public string Receipt;
        // True only when Receipt came from a structured backend mutation/query response.
        // Local cancellation/result strings must never enqueue an operation ACK.
        public bool BackendReceiptStored;
        // Once true, cleanup responsibility has been durably transferred to the
        // world-scoped ACK outbox. Network ACK may still be pending, but this saga
        // entry is no longer the only recovery evidence and may eventually be trimmed.
        public bool AckOutboxCommitted;
        public bool Retryable;
        public int RecoveryDispatchCount;
        // 后端终态只是事实；故事/章节才是玩家可见投影。只有投影 durable 提交后才允许 ACK。
        public string ProjectionText;
        public bool ProjectionCommitted;
        public int ActorId;
        public int TargetId;
        // Names are frozen from the game-owned roster beside the numeric identity fields.
        // Durable story projection never recovers them from model/dispatch JSON.
        public string ActorName;
        public string TargetName;
        // Frozen only from the successful executor callback; StoryReceipt must match it exactly.
        // It is deliberately separate from both model/dispatch JSON and the receipt being checked.
        public string ProjectionAsset;
        // Populated only after a succeeded, persisted backend receipt and the matching typed
        // execution callback have both arrived.  Null means projection must use a non-factual fallback.
        public StoryProjectionReceipt StoryReceipt;
        public int WorldDate;
        public long UpdatedUtcTicks;
    }

    /// <summary>
    /// 跨月江湖事件的持久状态。叙事只是投影；Goal/OpenLoops/Constraints/OutcomeJournal
    /// 才是下一月推进时的因果事实，任何副作用都必须先写 prepared checkpoint。
    /// </summary>
    public sealed class EventSaga
    {
        [JsonIgnore]
        public bool LoadReliable = true;
        public int Version = 7;
        public long Revision;
        public uint WorldId;
        public int TaiwuId;
        public bool Active;
        public string Title;
        public string Outline;
        public string AreaName;
        public short AreaId = -1;
        public List<int> ProtagonistIds = new List<int>();
        public List<string> ProtagonistNames = new List<string>();
        public List<string> Chapters = new List<string>();
        public bool TaiwuInvolved;
        // Durable ids of recent single/group turns whose code-owned successful action rows
        // prove Taiwu actually acted (gifted/taught/wrote/told), rather than merely chatted.
        public List<string> TaiwuFameEvidenceIds = new List<string>();
        public int MonthsTotal;
        public int MonthsElapsed;
        public int StartDate;
        public int CompletedDate;

        public EventSagaGoal Goal;
        public List<EventSagaOpenLoop> OpenLoops = new List<EventSagaOpenLoop>();
        public List<EventSagaConstraint> Constraints = new List<EventSagaConstraint>();
        public EventSagaStepOutcome LastOutcome;
        public List<EventSagaStepOutcome> OutcomeJournal = new List<EventSagaStepOutcome>();
        public List<string> PendingOperationIds = new List<string>();
        public List<EventFanoutCheckpoint> FanoutJournal = new List<EventFanoutCheckpoint>();
    }

    internal static class EventDispatchEnvelopeBinding
    {
        internal static string CreateStableKey(uint worldId, int taiwuId,
            int startDate, int worldDate, string stepId, string toolName,
            int actorId, int targetId, string argumentsJson)
            => "monthly-event|" + worldId + "|" + taiwuId
                + "|" + startDate + "|" + worldDate + "|" + (stepId ?? string.Empty)
                + "|" + (toolName ?? string.Empty) + "|" + actorId + "|" + targetId
                + "|" + (argumentsJson ?? string.Empty);

        internal static bool Matches(EventSaga saga, EventSagaStepOutcome outcome)
        {
            if (saga == null || outcome == null
                || string.IsNullOrWhiteSpace(outcome.ToolName)
                || string.IsNullOrWhiteSpace(outcome.DispatchEnvelopeJson))
                return false;
            try
            {
                var envelope = JObject.Parse(outcome.DispatchEnvelopeJson);
                if (!string.Equals((envelope["_tool"]?.ToString() ?? string.Empty).Trim(),
                        outcome.ToolName, StringComparison.Ordinal)
                    || envelope.Value<int?>("_actorId") != outcome.ActorId
                    || envelope.Value<int?>("_targetId") != outcome.TargetId)
                    return false;
                string expectedKey = CreateStableKey(saga.WorldId, saga.TaiwuId,
                    saga.StartDate, outcome.WorldDate, outcome.StepId, outcome.ToolName,
                    outcome.ActorId, outcome.TargetId, outcome.ArgumentsJson);
                return string.Equals(outcome.OperationBindingKey, expectedKey,
                        StringComparison.Ordinal)
                    && DispatchEnvelopeBinding.Matches(expectedKey,
                        outcome.DispatchEnvelopeJson, outcome.DispatchEnvelopeDigest,
                        outcome.OperationId);
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Keeps a durable saga roster aligned after the base game removes temporary/dead
    /// characters. The id/name arrays and every open-loop participant list must be pruned
    /// together; leaving only the old display names makes an invalid character look usable
    /// to the monthly agent.
    /// </summary>
    internal static class EventSagaRosterPolicy
    {
        internal static int RemoveUnavailable(EventSaga saga, ISet<int> unavailable)
        {
            if (saga == null || saga.ProtagonistIds == null || unavailable == null
                || unavailable.Count == 0) return 0;

            if (saga.ProtagonistNames == null)
                saga.ProtagonistNames = new List<string>();

            int removed = 0;
            for (int i = saga.ProtagonistIds.Count - 1; i >= 0; i--)
            {
                if (!unavailable.Contains(saga.ProtagonistIds[i])) continue;
                saga.ProtagonistIds.RemoveAt(i);
                if (i < saga.ProtagonistNames.Count)
                    saga.ProtagonistNames.RemoveAt(i);
                removed++;
            }

            if (saga.OpenLoops != null)
                foreach (EventSagaOpenLoop loop in saga.OpenLoops)
                    if (loop?.ParticipantIds != null)
                        loop.ParticipantIds.RemoveAll(id => unavailable.Contains(id));

            return removed;
        }
    }

    /// <summary>一存档一桩；main/tmp/bak 按 revision 恢复，存盘失败显式返回 false。</summary>
    public static class EventSagaStore
    {
        private static readonly object Io = new object();
        private const int MaxOutcomeJournal = 96;
        private const int MaxSagaBytes = 8 * 1024 * 1024;
        private const int MaxTextField = 256 * 1024;
        private const int MaxSagaMonths = 120;

        static string PathFor(int taiwuId) => Path.Combine(JianghuYoulingPaths.Events, "saga_" + taiwuId + ".json");

        public static EventSaga Load(int taiwuId)
        {
            if (taiwuId <= 0) return Normalize(new EventSaga(), taiwuId);
            lock (Io)
            {
                string path = PathFor(taiwuId);
                EventSaga main = TryRead(path, taiwuId);
                EventSaga bak = TryRead(path + ".bak", taiwuId);
                EventSaga tmp = TryRead(path + ".tmp", taiwuId);
                // main is the publication point. tmp is intentionally retained as an exact
                // current replica after publication, so matching tmp+bak can recover a lost
                // main. If tmp and bak disagree there is no proof whether tmp is a committed
                // degraded write or a pre-publication stage; fail closed rather than rolling
                // back to bak or replaying tmp.
                EventSaga best = main;
                if (best == null && tmp != null && bak != null && SameSaga(tmp, bak))
                    best = tmp;
                else if (best == null && tmp == null)
                    best = bak;
                if (best == null)
                {
                    string tmpPath = path + ".tmp";
                    var empty = Normalize(new EventSaga(), taiwuId);
                    // A lone tmp or disagreeing tmp/bak is ambiguous after the main disappears:
                    // it can be either a fully committed degraded write or an uncommitted stage.
                    // Preserve every byte and require explicit recovery instead of converting a
                    // potentially committed saga into a reliable empty state.
                    bool anyCandidate = File.Exists(path) || File.Exists(tmpPath) || File.Exists(path + ".bak");
                    if (anyCandidate)
                        Debug.LogWarning("[江湖有灵] 连载事件副本无法证明同一提交，维持不可靠加载。"
                            + "请保留 main/tmp/bak 恢复证据并核对 revision 后再处理: " + path);
                    empty.LoadReliable = !anyCandidate;
                    return empty;
                }
                best = Normalize(best, taiwuId);
                best.LoadReliable = true;

                // A successful current-version commit keeps main/tmp/bak at one exact
                // revision.  Repair legacy/stale replicas from the authoritative main (or
                // republish the committed bak when main is unavailable) before declaring
                // the saga reliable; a syntactically valid older bak must not later roll
                // operation evidence backwards.
                if (main == null || !SameSaga(best, tmp) || !SameSaga(best, bak))
                {
                    try
                    {
                        WriteAtomic(path, best, taiwuId);
                    }
                    catch (Exception e)
                    {
                        best.LoadReliable = false;
                        Debug.LogWarning("[江湖有灵] 连载事件恢复 main 失败: " + e.GetType().Name);
                    }
                }
                return best;
            }
        }

        public static bool Save(int taiwuId, EventSaga saga)
        {
            // 有文件但 main/tmp/bak 全损坏时，绝不能拿“空 saga”覆盖未决 operation 证据。
            if (taiwuId <= 0 || saga == null || !saga.LoadReliable)
            {
                Debug.LogWarning("[JHYL_SAGA_SAVE_REJECT] reason=invalid_or_unreliable taiwu="
                    + taiwuId + " saga=" + (saga == null ? "null" : "present")
                    + " reliable=" + (saga != null && saga.LoadReliable ? "1" : "0"));
                return false;
            }
            lock (Io)
            {
                Normalize(saga, taiwuId);
                string path = PathFor(taiwuId);
                EventSaga committedBefore = TryRead(path, taiwuId);
                bool anyCandidate = File.Exists(path) || File.Exists(path + ".tmp") || File.Exists(path + ".bak");
                if (committedBefore == null && anyCandidate)
                {
                    Debug.LogWarning("[JHYL_SAGA_SAVE_REJECT] reason=unreadable_existing_candidate taiwu="
                        + taiwuId + " revision=" + saga.Revision);
                    return false;
                }
                // A stale separately loaded instance must not overwrite a newer
                // prepared/terminal journal. The caller must reload and reconcile.
                if (committedBefore != null && committedBefore.Revision > saga.Revision)
                {
                    Debug.LogWarning("[JHYL_SAGA_SAVE_REJECT] reason=stale_revision taiwu=" + taiwuId
                        + " disk=" + committedBefore.Revision + " candidate=" + saga.Revision);
                    return false;
                }
                long previous = saga.Revision;
                if (previous == long.MaxValue)
                {
                    Debug.LogWarning("[JHYL_SAGA_SAVE_REJECT] reason=revision_overflow taiwu=" + taiwuId);
                    return false;
                }
                saga.Revision = Math.Max(previous + 1, 1);
                try
                {
                    TrimJournal(saga);
                    WriteAtomic(path, saga, taiwuId);
                    return true;
                }
                catch (Exception e)
                {
                    // A read-back failure may occur after main was atomically published.
                    // Reporting false in that state invites callers to roll back or
                    // redispatch around an already committed revision.
                    EventSaga committedAfter = TryRead(path, taiwuId);
                    if (SameSaga(saga, committedAfter)) return true;
                    saga.Revision = previous;
                    Debug.LogWarning("[JHYL_SAGA_SAVE_REJECT] reason=atomic_write_failed taiwu=" + taiwuId
                        + " exception=" + e.GetType().Name);
                    return false;
                }
            }
        }

        public static bool Clear(int taiwuId)
        {
            if (taiwuId <= 0) return false;
            lock (Io)
            {
                try
                {
                    string path = PathFor(taiwuId);
                    EventSaga best = Newer(Newer(TryRead(path, taiwuId), TryRead(path + ".tmp", taiwuId)),
                        TryRead(path + ".bak", taiwuId));
                    if (best == null && (File.Exists(path) || File.Exists(path + ".tmp") || File.Exists(path + ".bak")))
                    {
                        Debug.LogWarning("[江湖有灵] 连载事件候选均损坏，拒绝清除恢复证据");
                        return false;
                    }
                    if (HasUnsettledEvidence(best))
                    {
                        Debug.LogWarning("[江湖有灵] 连载仍有 pending/unknown 或终态未投影证据，拒绝 Clear");
                        return false;
                    }
                    return DurableFileStore.TryDeleteAllArtifacts(path);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[江湖有灵] 连载事件清除失败: " + e.GetType().Name);
                    return false;
                }
            }
        }

        /// <summary>
        /// A loaded game save is the authority for the rollback boundary. When saga replicas
        /// cannot prove one committed revision, none of their current/future operation evidence
        /// may block regeneration on that save. This explicit rollback path discards only this
        /// Taiwu's external saga artifacts; normal Clear remains fail-closed.
        /// </summary>
        internal static bool DiscardUnreliableForSaveRollback(int taiwuId)
        {
            if (taiwuId <= 0) return false;
            lock (Io)
            {
                string path = PathFor(taiwuId);
                bool deleted = DurableFileStore.TryDeleteAllArtifacts(path);
                if (deleted) Debug.LogWarning(
                    "[JHYL_MONTHLY_ROLLBACK_UNRELIABLE_SAGA_DISCARDED] taiwu=" + taiwuId);
                return deleted;
            }
        }

        private static EventSaga Normalize(EventSaga saga, int taiwuId)
        {
            saga = saga ?? new EventSaga();
            saga.Version = 7;
            saga.TaiwuId = taiwuId;
            // v0.29 及更早的连载没有 WorldId 字段，读回恒为 0。本存储的 main/tmp/bak 只可能经由
            // 当前世界的隔离目录(PathFor → JianghuYoulingPaths.Events → Worlds/World_<id>)读写，
            // 因此把目录作用域的世界身份补给无主连载是对文件出处的重述，不是猜测；否则旧连载会被
            // 0.30 的世界绑定校验(派发前身份预登记、fanout checkpoint)永久误判为"世界已切换"。
            // 非零 WorldId 绝不改写——真正属于另一世界的证据仍旧 fail-closed。
            if (saga.WorldId == 0) saga.WorldId = JianghuYoulingPaths.CurrentWorldId;
            if (saga.ProtagonistIds == null) saga.ProtagonistIds = new List<int>();
            if (saga.ProtagonistNames == null) saga.ProtagonistNames = new List<string>();
            if (saga.Chapters == null) saga.Chapters = new List<string>();
            if (saga.OpenLoops == null) saga.OpenLoops = new List<EventSagaOpenLoop>();
            if (saga.Constraints == null) saga.Constraints = new List<EventSagaConstraint>();
            if (saga.OutcomeJournal == null) saga.OutcomeJournal = new List<EventSagaStepOutcome>();
            if (saga.PendingOperationIds == null) saga.PendingOperationIds = new List<string>();
            if (saga.FanoutJournal == null) saga.FanoutJournal = new List<EventFanoutCheckpoint>();
            if (saga.TaiwuFameEvidenceIds == null) saga.TaiwuFameEvidenceIds = new List<string>();
            // OutcomeJournal 是权威事实，PendingOperationIds 只是查询索引。崩溃可能发生在 journal
            // 已写入、索引尚未同步之间；加载时必须保守补回，不能让 prepared/unknown 绕过对账继续推进。
            foreach (var outcome in saga.OutcomeJournal)
            {
                if (outcome == null || !OperationId.IsValid(outcome.OperationId)) continue;
                if (!string.IsNullOrWhiteSpace(outcome.Receipt)) outcome.BackendReceiptStored = true;
                string status = (outcome.Status ?? "").Trim().ToLowerInvariant();
                bool unresolved = status == "prepared" || status == "pending"
                    || status == "unknown" && outcome.Retryable;
                if (unresolved && !saga.PendingOperationIds.Contains(outcome.OperationId))
                    saga.PendingOperationIds.Add(outcome.OperationId);
            }
            if (saga.OutcomeJournal.Count == 0) saga.LastOutcome = null;
            else
            {
                EventSagaStepOutcome canonical = null;
                if (saga.LastOutcome != null && OperationId.IsValid(saga.LastOutcome.OperationId))
                    canonical = saga.OutcomeJournal.Find(x => x != null
                        && string.Equals(x.OperationId, saga.LastOutcome.OperationId, StringComparison.Ordinal));
                saga.LastOutcome = canonical ?? saga.OutcomeJournal[saga.OutcomeJournal.Count - 1];
            }
            foreach (var loop in saga.OpenLoops)
            {
                if (loop == null) continue;
                if (loop.ParticipantIds == null) loop.ParticipantIds = new List<int>();
                if (loop.EvidenceIds == null) loop.EvidenceIds = new List<string>();
                if (string.IsNullOrWhiteSpace(loop.Status)) loop.Status = "open";
            }
            if (saga.Active && saga.Goal == null)
            {
                saga.Goal = new EventSagaGoal
                {
                    Id = "goal-" + Math.Max(0, saga.StartDate),
                    Summary = string.IsNullOrWhiteSpace(saga.Outline) ? "推进并收束这桩江湖风波" : saga.Outline,
                    SuccessCriteria = "依据真实工具回执推进至结局，不虚构未落地结果",
                    Status = "active",
                    UpdatedDate = saga.StartDate,
                };
            }
            if (saga.Goal != null && saga.Goal.MinimumSucceededSteps <= 0)
                saga.Goal.MinimumSucceededSteps = 1;
            return saga;
        }

        private static void TrimJournal(EventSaga saga)
        {
            if (saga.OutcomeJournal != null && saga.OutcomeJournal.Count > MaxOutcomeJournal)
            {
                int remove = saga.OutcomeJournal.Count - MaxOutcomeJournal;
                var pending = new HashSet<string>(saga.PendingOperationIds ?? new List<string>(), StringComparer.Ordinal);
                for (int i = 0; i < saga.OutcomeJournal.Count && remove > 0;)
                {
                    var item = saga.OutcomeJournal[i];
                    if (item != null && !string.IsNullOrWhiteSpace(item.OperationId)
                        && (pending.Contains(item.OperationId) || !item.ProjectionCommitted
                            || item.BackendReceiptStored && !item.AckOutboxCommitted)) { i++; continue; }
                    saga.OutcomeJournal.RemoveAt(i);
                    remove--;
                }
            }
            if (saga.FanoutJournal != null && saga.FanoutJournal.Count > EventFanoutPolicy.MaxCheckpoints)
            {
                int remove = saga.FanoutJournal.Count - EventFanoutPolicy.MaxCheckpoints;
                for (int i = 0; i < saga.FanoutJournal.Count && remove > 0;)
                {
                    EventFanoutCheckpoint checkpoint = saga.FanoutJournal[i];
                    if (checkpoint != null && !checkpoint.ProjectionCommitted) { i++; continue; }
                    saga.FanoutJournal.RemoveAt(i);
                    remove--;
                }
            }
        }

        public static bool HasUnsettledEvidence(EventSaga saga)
        {
            if (saga == null) return false;
            if (saga.FanoutJournal != null)
                foreach (EventFanoutCheckpoint fanout in saga.FanoutJournal)
                    if (fanout != null && (!fanout.ProjectionCommitted
                        || !EventFanoutPolicy.ReadyForProjection(fanout))) return true;
            if (saga.PendingOperationIds != null && saga.PendingOperationIds.Count > 0) return true;
            if (saga.OutcomeJournal == null) return false;
            foreach (var item in saga.OutcomeJournal)
            {
                if (item == null) continue;
                string status = (item.Status ?? "").Trim().ToLowerInvariant();
                if (status == "prepared" || status == "pending" || status == "unknown" && item.Retryable)
                    return true;
                if (IsTerminalStatus(status, item.Retryable) && !item.ProjectionCommitted)
                    return true;
                if (IsTerminalStatus(status, item.Retryable) && item.ProjectionCommitted
                    && item.BackendReceiptStored && !item.AckOutboxCommitted)
                    return true;
            }
            return false;
        }

        private static bool IsTerminalStatus(string status, bool retryable)
            => status == "succeeded" || status == "failed" || status == "rejected" || status == "canceled"
                || status == "unknown" && !retryable;

        private static EventSaga TryRead(string path, int expectedTaiwuId)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string json = ReadStrictUtf8(path);
                JObject root;
                using (var sr = new StringReader(json))
                using (var reader = new JsonTextReader(sr) { DateParseHandling = DateParseHandling.None })
                {
                    root = JObject.Load(reader, new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                        CommentHandling = CommentHandling.Ignore,
                        LineInfoHandling = LineInfoHandling.Ignore,
                    });
                    while (reader.Read())
                        if (reader.TokenType != JsonToken.Comment)
                            throw new InvalidDataException("连载事件 JSON 含尾随内容");
                }
                var serializer = JsonSerializer.Create(new JsonSerializerSettings
                {
                    MissingMemberHandling = MissingMemberHandling.Error,
                });
                var saga = root.ToObject<EventSaga>(serializer);
                if (saga == null || saga.Revision < 0 || saga.Version < 0 || saga.Version > 7
                    || saga.TaiwuId != 0 && saga.TaiwuId != expectedTaiwuId) return null;
                if (saga.Version < 6)
                {
                    // Older receipts did not persist executor-owned asset evidence separately.
                    // Keep the operation outcome, but force non-factual projection instead of
                    // accepting the receipt's Asset as self-authenticating evidence.
                    if (saga.OutcomeJournal != null)
                        foreach (EventSagaStepOutcome outcome in saga.OutcomeJournal)
                            if (outcome != null) { outcome.ProjectionAsset = null; outcome.StoryReceipt = null; }
                    if (saga.LastOutcome != null)
                    { saga.LastOutcome.ProjectionAsset = null; saga.LastOutcome.StoryReceipt = null; }
                }
                if (saga.FanoutJournal == null) saga.FanoutJournal = new List<EventFanoutCheckpoint>();
                if (!ValidSagaShape(saga)) return null;
                if (saga.Active && (saga.AreaId < 0 || saga.MonthsTotal <= 0 || saga.MonthsElapsed < 0
                    || saga.MonthsElapsed >= saga.MonthsTotal || saga.ProtagonistIds == null || saga.ProtagonistIds.Count < 1)) return null;
                if (saga.PendingOperationIds == null || saga.OutcomeJournal == null) return null;
                var pending = new HashSet<string>(StringComparer.Ordinal);
                foreach (string operationId in saga.PendingOperationIds)
                    if (!OperationId.IsValid(operationId) || !pending.Add(operationId)) return null;
                var journalIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in saga.OutcomeJournal)
                {
                    if (item == null || !OperationId.IsValid(item.OperationId) || !journalIds.Add(item.OperationId)
                        || string.IsNullOrWhiteSpace(item.StepId) || string.IsNullOrWhiteSpace(item.ToolName)
                        || !IsValidStatus(item.Status) || item.RecoveryDispatchCount < 0 || item.RecoveryDispatchCount > 1)
                        return null;
                    // v1/v2 pending 没有完整派发信封，只能继续查询与隔离；恢复代码只在信封存在时重放。
                    if (!string.IsNullOrWhiteSpace(item.DispatchEnvelopeJson))
                    {
                        try
                        {
                            var envelope = JObject.Parse(item.DispatchEnvelopeJson);
                            if (saga.Version >= 3 && (envelope.Value<int?>("_actorId") != item.ActorId
                                || envelope.Value<int?>("_targetId") != item.TargetId)) return null;
                        }
                        catch { return null; }
                    }
                    bool hasEnvelopeBinding = !string.IsNullOrWhiteSpace(item.OperationBindingKey)
                        || !string.IsNullOrWhiteSpace(item.DispatchEnvelopeDigest);
                    if (hasEnvelopeBinding)
                    {
                        JObject envelope = JObject.Parse(item.DispatchEnvelopeJson);
                        bool hasToolBinding =
                            !string.IsNullOrWhiteSpace(envelope["_tool"]?.ToString());
                        if (hasToolBinding
                            ? !EventDispatchEnvelopeBinding.Matches(saga, item)
                            : !DispatchEnvelopeBinding.Matches(item.OperationBindingKey,
                                item.DispatchEnvelopeJson, item.DispatchEnvelopeDigest,
                                item.OperationId))
                            return null;
                    }
                }
                foreach (string operationId in pending)
                    if (!journalIds.Contains(operationId)) return null;
                var eventIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (EventFanoutCheckpoint checkpoint in saga.FanoutJournal)
                    if (!EventFanoutPolicy.IsValid(checkpoint) || !eventIds.Add(checkpoint.EventId)) return null;
                return saga;
            }
            catch { return null; }
        }

        private static bool IsValidStatus(string status)
            => status == "prepared" || status == "pending" || status == "succeeded" || status == "failed"
                || status == "rejected" || status == "canceled" || status == "unknown";

        private static bool ValidSagaShape(EventSaga saga)
        {
            if (saga == null || saga.ProtagonistIds == null || saga.ProtagonistIds.Count > 64
                || saga.ProtagonistNames == null || saga.ProtagonistNames.Count > 64
                || saga.Chapters == null || saga.Chapters.Count > 96
                || saga.OpenLoops == null || saga.OpenLoops.Count > 96
                || saga.Constraints == null || saga.Constraints.Count > 96
                || saga.OutcomeJournal == null || saga.OutcomeJournal.Count > MaxOutcomeJournal
                || saga.PendingOperationIds == null || saga.PendingOperationIds.Count > MaxOutcomeJournal
                || saga.FanoutJournal == null || saga.FanoutJournal.Count > EventFanoutPolicy.MaxCheckpoints
                || saga.TaiwuFameEvidenceIds == null || saga.TaiwuFameEvidenceIds.Count > 64)
                return false;
            if (saga.MonthsTotal < 0 || saga.MonthsTotal > MaxSagaMonths || saga.MonthsElapsed < 0
                || saga.MonthsElapsed > MaxSagaMonths || saga.StartDate < 0 || saga.CompletedDate < 0
                || saga.AreaId < -1) return false;
            if (!TextFits(saga.Title) || !TextFits(saga.Outline) || !TextFits(saga.AreaName)) return false;
            var protagonistIds = new HashSet<int>();
            foreach (int id in saga.ProtagonistIds) if (id <= 0 || !protagonistIds.Add(id)) return false;
            foreach (string name in saga.ProtagonistNames) if (!TextFits(name, 1024)) return false;
            foreach (string chapter in saga.Chapters) if (!TextFits(chapter, 64 * 1024)) return false;
            var taiwuEvidence = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in saga.TaiwuFameEvidenceIds)
                if (!TextFits(id, 512) || string.IsNullOrWhiteSpace(id) || !taiwuEvidence.Add(id)) return false;
            if (saga.Goal != null && (!TextFits(saga.Goal.Id, 1024) || !TextFits(saga.Goal.Summary, 32 * 1024)
                || !TextFits(saga.Goal.SuccessCriteria, 32 * 1024) || !ValidGoalStatus(saga.Goal.Status)
                || saga.Goal.MinimumSucceededSteps < 0 || saga.Goal.MinimumSucceededSteps > MaxOutcomeJournal
                || saga.Goal.SucceededSteps < 0 || saga.Goal.SucceededSteps > MaxOutcomeJournal
                || saga.Goal.FailedSteps < 0 || saga.Goal.FailedSteps > MaxOutcomeJournal
                || saga.Goal.UpdatedDate < 0)) return false;
            foreach (var loop in saga.OpenLoops)
            {
                if (loop == null || !TextFits(loop.Id, 1024) || !TextFits(loop.Kind, 128)
                    || !TextFits(loop.Summary, 32 * 1024) || !ValidOpenLoopStatus(loop.Status)
                    || loop.ParticipantIds == null || loop.ParticipantIds.Count > 64
                    || loop.EvidenceIds == null || loop.EvidenceIds.Count > MaxOutcomeJournal
                    || loop.NextEligibleDate < 0) return false;
                var participants = new HashSet<int>();
                foreach (int id in loop.ParticipantIds) if (id <= 0 || !participants.Add(id)) return false;
                var evidence = new HashSet<string>(StringComparer.Ordinal);
                foreach (string id in loop.EvidenceIds)
                    if (!OperationId.IsValid(id) || !evidence.Add(id)) return false;
            }
            foreach (var constraint in saga.Constraints)
                if (constraint == null || !TextFits(constraint.Id, 1024) || !TextFits(constraint.Kind, 128)
                    || !TextFits(constraint.Value, 32 * 1024)) return false;
            foreach (var outcome in saga.OutcomeJournal)
                if (!ValidOutcomeShape(outcome, saga.Version >= 3, saga.Version >= 4, saga.TaiwuId)
                    || HasToolBoundEnvelope(outcome)
                        && !EventDispatchEnvelopeBinding.Matches(saga, outcome))
                    return false;
            if (saga.LastOutcome != null
                && (!ValidOutcomeShape(saga.LastOutcome, saga.Version >= 3,
                        saga.Version >= 4, saga.TaiwuId)
                    || HasToolBoundEnvelope(saga.LastOutcome)
                        && !EventDispatchEnvelopeBinding.Matches(saga, saga.LastOutcome)))
                return false;
            var eventIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (EventFanoutCheckpoint checkpoint in saga.FanoutJournal)
            {
                if (!EventFanoutPolicy.IsValid(checkpoint) || !eventIds.Add(checkpoint.EventId)
                    || checkpoint.RecipientIds.Contains(saga.TaiwuId)
                    || !TextFits(checkpoint.AreaName, 32 * 1024)
                    || !TextFits(checkpoint.StoryText, 64 * 1024)
                    || !TextFits(checkpoint.ProjectionText, 64 * 1024)
                    || !TextFits(checkpoint.Roster, 256 * 1024)) return false;
                foreach (string action in checkpoint.Actions) if (!TextFits(action, 64 * 1024)) return false;
            }
            return true;
        }

        private static bool ValidOutcomeShape(EventSagaStepOutcome outcome, bool requiresBoundIdentity,
            bool requiresReceiptFlags, int expectedTaiwuId)
        {
            bool boundSelfTarget = requiresBoundIdentity && !string.IsNullOrWhiteSpace(outcome?.DispatchEnvelopeJson)
                && outcome.ActorId == outcome.TargetId;
            bool validFameSelfTarget = boundSelfTarget && expectedTaiwuId > 0
                && outcome.ActorId == expectedTaiwuId
                && string.Equals(outcome.ToolName, "event_taiwu_fame", StringComparison.Ordinal)
                && ValidFameEnvelope(outcome);
            if (outcome == null || !OperationId.IsValid(outcome.OperationId)
                || !TextFits(outcome.OperationId, 256) || !TextFits(outcome.StepId, 1024)
                || !TextFits(outcome.ToolName, 128) || !IsValidStatus(outcome.Status)
                || !TextFits(outcome.Code, 1024) || !TextFits(outcome.Intent, 32 * 1024)
                || !TextFits(outcome.Summary, 64 * 1024) || !TextFits(outcome.ArgumentsJson)
                || !TextFits(outcome.OperationBindingKey) || !TextFits(outcome.DispatchEnvelopeJson)
                || !TextFits(outcome.DispatchEnvelopeDigest, 256) || !TextFits(outcome.Receipt)
                || !TextFits(outcome.ProjectionText, 64 * 1024)
                || !TextFits(outcome.ActorName, 1024) || !TextFits(outcome.TargetName, 1024)
                || !TextFits(outcome.ProjectionAsset, 8192)
                || outcome.RecoveryDispatchCount < 0 || outcome.RecoveryDispatchCount > 1
                || requiresBoundIdentity && !string.IsNullOrWhiteSpace(outcome.DispatchEnvelopeJson)
                    && (outcome.ActorId <= 0 || outcome.TargetId <= 0 || boundSelfTarget && !validFameSelfTarget)
                || outcome.WorldDate < 0 || outcome.UpdatedUtcTicks < 0
                || requiresReceiptFlags && outcome.BackendReceiptStored != !string.IsNullOrWhiteSpace(outcome.Receipt)
                || requiresReceiptFlags && outcome.AckOutboxCommitted && (!outcome.BackendReceiptStored || !outcome.ProjectionCommitted
                    || !IsTerminalStatus(outcome.Status, outcome.Retryable))) return false;
            bool hasEnvelopeBinding = !string.IsNullOrWhiteSpace(outcome.OperationBindingKey)
                || !string.IsNullOrWhiteSpace(outcome.DispatchEnvelopeDigest);
            if (hasEnvelopeBinding
                && !DispatchEnvelopeBinding.Matches(outcome.OperationBindingKey,
                    outcome.DispatchEnvelopeJson, outcome.DispatchEnvelopeDigest,
                    outcome.OperationId))
                return false;
            if (!string.IsNullOrWhiteSpace(outcome.ArgumentsJson))
                try { if (!(JToken.Parse(outcome.ArgumentsJson) is JObject)) return false; }
                catch { return false; }
            return ValidStoryReceipt(outcome);
        }

        private static bool HasToolBoundEnvelope(EventSagaStepOutcome outcome)
        {
            if (outcome == null || string.IsNullOrWhiteSpace(outcome.DispatchEnvelopeJson))
                return false;
            try
            {
                return !string.IsNullOrWhiteSpace(
                    JObject.Parse(outcome.DispatchEnvelopeJson)["_tool"]?.ToString());
            }
            catch { return false; }
        }

        private static bool ValidFameEnvelope(EventSagaStepOutcome outcome)
        {
            try
            {
                var args = JObject.Parse(outcome.ArgumentsJson ?? "");
                var envelope = JObject.Parse(outcome.DispatchEnvelopeJson ?? "");
                int delta = args.Value<int?>("delta") ?? 0;
                string reason = (args["reason"]?.ToString() ?? "").Trim();
                return delta >= -12 && delta <= 12 && delta != 0 && delta % 3 == 0
                    && reason.Length >= 2
                    && envelope.Value<int?>("_actorId") == outcome.ActorId
                    && envelope.Value<int?>("_targetId") == outcome.TargetId
                    && envelope.Value<int?>("delta") == delta
                    && string.Equals((envelope["reason"]?.ToString() ?? "").Trim(), reason,
                        StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private static bool ValidStoryReceipt(EventSagaStepOutcome outcome)
        {
            StoryProjectionReceipt receipt = outcome?.StoryReceipt;
            if (receipt == null) return true;
            return !string.IsNullOrWhiteSpace(outcome.Receipt)
                && StoryProjectionValidator.ReceiptMatchesAuthoritativeOutcome(outcome.ToolName,
                    outcome.OperationId, outcome.ActorId, outcome.TargetId, outcome.ActorName,
                    outcome.TargetName, outcome.ProjectionAsset, outcome.Summary,
                    string.Equals(outcome.Status, "succeeded", StringComparison.Ordinal),
                    outcome.BackendReceiptStored, receipt);
        }

        private static bool ValidGoalStatus(string status)
            => status == "active" || status == "achieved" || status == "failed" || status == "abandoned";

        private static bool ValidOpenLoopStatus(string status)
            => status == "open" || status == "resolved" || status == "blocked";

        private static bool TextFits(string value, int max = MaxTextField)
            => value == null || value.Length <= max;

        private static EventSaga Newer(EventSaga a, EventSaga b)
        {
            if (a == null) return b;
            if (b == null) return a;
            return b.Revision > a.Revision ? b : a;
        }

        private static void WriteAtomic(string path, EventSaga saga, int taiwuId)
        {
            if (!ValidSagaShape(saga)) throw new InvalidDataException("连载事件字段或集合超过安全上限");
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            string bak = path + ".bak";
            string promote = path + ".promote";
            string json = JsonConvert.SerializeObject(saga, Formatting.Indented);
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(json);
            if (bytes.Length > MaxSagaBytes) throw new InvalidDataException("连载事件文件超过安全上限");
            WriteSagaBytes(tmp, bytes);
            EventSaga staged = TryRead(tmp, taiwuId);
            if (!SameSaga(saga, staged)) throw new InvalidDataException("连载事件 tmp 严格读回不一致");
            WriteSagaBytes(promote, bytes);
            EventSaga promotion = TryRead(promote, taiwuId);
            if (!SameSaga(saga, promotion))
                throw new InvalidDataException("连载事件 promotion 严格读回不一致");
            // 与项目其余耐久存储统一：忽略 Windows 元数据复制失败，只要求内容替换
            // 完成；随后会把已验证的当前提交同步到 bak。
            if (File.Exists(path)) File.Replace(promote, path, bak, true);
            else File.Move(promote, path);
            EventSaga committed = TryRead(path, taiwuId);
            EventSaga replica = TryRead(tmp, taiwuId);
            if (!SameSaga(saga, committed) || !SameSaga(saga, replica))
                throw new InvalidDataException("连载事件 main/tmp 提交读回不一致");
            WriteSagaBytes(bak, bytes);
            EventSaga backup = TryRead(bak, taiwuId);
            if (!SameSaga(saga, backup))
                throw new InvalidDataException("连载事件 bak 提交读回不一致");
        }

        private static void WriteSagaBytes(string path, byte[] bytes)
        {
            using (var stream = new FileStream(path, FileMode.Create,
                FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private static bool SameSaga(EventSaga expected, EventSaga actual)
        {
            if (expected == null || actual == null || expected.Revision != actual.Revision
                || expected.Version != actual.Version || expected.TaiwuId != actual.TaiwuId) return false;
            return string.Equals(JsonConvert.SerializeObject(expected, Formatting.None),
                JsonConvert.SerializeObject(actual, Formatting.None), StringComparison.Ordinal);
        }

        private static string ReadStrictUtf8(string path)
        {
            long length = new FileInfo(path).Length;
            if (length < 0 || length > MaxSagaBytes) throw new InvalidDataException("连载事件文件超过安全上限");
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length > MaxSagaBytes) throw new InvalidDataException("连载事件文件超过安全上限");
            if (bytes.Length >= 2 && (bytes[0] == 0xff && bytes[1] == 0xfe || bytes[0] == 0xfe && bytes[1] == 0xff))
                throw new InvalidDataException("连载事件不是 UTF-8");
            return new UTF8Encoding(false, true).GetString(bytes);
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
