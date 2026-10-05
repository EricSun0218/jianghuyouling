using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using JianghuYouling.Core.Behavior;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Memory;
using JianghuYouling.Core.Persistence;
using JianghuYouling.Core.Prompt;
using JianghuYouling.Core.Tools;
using JianghuYouling.Core.Text;
using JianghuYouling.Effects;
using GameData.Domains.Information;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace JianghuYouling
{
    public sealed class CompanionMonthlyResult
    {
        public int NpcId;
        public string Name;
        public string LocationText;
        public string Summary;
        public string Detail;
        public readonly List<string> Outcomes = new List<string>();
        [JsonIgnore]
        public readonly List<StoryProjectionReceipt> StoryReceipts = new List<StoryProjectionReceipt>();
        // This authority set comes from the game-owned companion scene snapshot, never from
        // StoryReceipts.  Keeping it separate prevents a forged receipt from authorizing its
        // own actor/target simply by being present in the receipt collection.
        [JsonIgnore]
        internal readonly HashSet<int> AuthorizedParticipantIds = new HashSet<int>();
    }

    internal sealed class CompanionPlanStep
    {
        public string Name;
        public string ArgsJson;
    }

    /// <summary>
    /// 同道过月主动行事:同道基于记忆和近况自行决定本月做什么,真实调用工具落地,
    /// 再把结果写入过月窗口与该同道的聊天历史。
    /// </summary>
    public static class CompanionMonthlyActions
    {
        // Per-companion request fuse.  A separate shared request/token budget covers the
        // entire digest so many companions cannot multiply this into thousands of calls.
        private const int MaxAgentRounds = 12;
        private const int MaxTrajectoryReasonChars = 240;
        private const int MinimumConfirmedActions = 3;
        private const int MinimumDistinctActionCategories = 2;
        // Durable journals created by earlier builds may contain step indexes up to 63.
        // Keep the storage/recovery range compatible even though new network loops stop at 8.
        private const int MaxJournalStepsPerBatch = 64;
        // 0 means use the user's/model's configured completion ceiling. DeepSeek reasoning and
        // the final tool call share one completion budget; a low feature-specific cap can erase
        // an otherwise valid action with finish_reason=length.
        private const int ActionDecisionMaxTokens = 0;
        private const int ActionDecisionTimeoutSeconds = 180;
        // Stalled-batch watchdog, renewed by model rounds, queries, tool receipts and child
        // completion. A single high-quality agent may legitimately use all 12 rounds; queued
        // companions must not lose that time.
        internal const int BatchIdleTimeoutSeconds = 300;
        // 外部月度事件与同道人物冲突时允许等待，但等待本身会持续上报活性，
        // 并由独立绝对上限终止；不能让 5 分钟“无心跳”看门狗误杀仍在正常排队的整批。
        private const int ExternalExecutionGateMaxWaitSeconds = 3600;
        // 本地后端 RPC 正常应在数帧内回调；永久丢失时不能把全局月度锁留到进程重启。
        // 超时只表示结果未知，不等同失败，更不能自动重试同一副作用。
        private const int MutationCallbackTimeoutSeconds = 30;
        private const float OperationReconcileWaitSeconds = 8f;
        private const int MonthlyMemoryLimit = 6;
        private const int MonthlyDialogueLimit = 6;
        private const int MonthlyInventoryLimit = 24;
        private const int MonthlySkillLimit = 24;
        private const int MaxMutationJournalBytes = 8 * 1024 * 1024;
        private const int MaxMutationJournalEntries = 240;
        private const int MaxProjectionOutcomes = 96;
        private static bool _running;
        private static int _runEpoch;
        private static readonly object MutationGate = new object();
        private static readonly Dictionary<int, int> MutationInflightByEpoch = new Dictionary<int, int>();
        private static readonly HashSet<string> UnknownMutationQuarantine = new HashSet<string>(StringComparer.Ordinal);
        private static bool _guardReleasePending;
        private static string _guardReleaseReason;
        // One owner token covers every model request in the active monthly batch.  Unity
        // coroutines stop waiting as soon as the world changes, but without cancelling the
        // provider request the HTTP call would keep consuming quota and the global LLM gate.
        private static CancellationTokenSource _runCancellation;
        private static int _runCancellationEpoch;
        private static readonly object JournalIo = new object();

        private sealed class CompanionActionNoveltySnapshot
        {
            internal readonly List<CompanionBehaviorObservation> Observations =
                new List<CompanionBehaviorObservation>();
        }

        private enum ProjectionAuthorityState
        {
            Current,
            StaleAfterSaveRollback,
            Unavailable,
        }

        private sealed class CompanionSceneContext
        {
            internal int Generation;
            internal uint WorldId;
            internal int TaiwuId;
            internal short AreaId;
            internal short BlockId;
            internal string AreaName;
            internal bool ActorScene; // 手动加入的普通 NPC 以自己所在地为现场，而非强制跟随太吾
            internal List<int> MemberIds = new List<int>(); // 当前可被选为主动行动者的人物
            internal HashSet<int> CompanionIds = new HashSet<int>(); // 本体当前同道；默认随太吾同行
            internal HashSet<int> PresentIds = new HashSet<int>(); // 同道同行语义 + 太吾当前真实同块人物
            internal HashSet<int> RemoteIds = new HashSet<int>(); // 本行动者关系网或按唯一全名解析出的异地人物
            internal HashSet<int> AuthorizedTargetIds = new HashSet<int>(); // 仅本计划实际引用目标，控制 journal/故事白名单大小
            internal Dictionary<int, string> Names = new Dictionary<int, string>();
            internal Dictionary<int, string> Genders = new Dictionary<int, string>();
            internal Dictionary<int, string> Locations = new Dictionary<int, string>();
            internal Dictionary<int, string> RelationshipFacts = new Dictionary<int, string>();
            internal Dictionary<int, NpcSnapshot> PersonSkillFacts = new Dictionary<int, NpcSnapshot>();
            internal Dictionary<int, List<SecretRef>> DisclosableSecretsByRecipient =
                new Dictionary<int, List<SecretRef>>();
            internal string RelationshipNetwork;
            internal string RelationshipMotiveFacts;

            internal CompanionSceneContext Clone()
            {
                return new CompanionSceneContext
                {
                    Generation = Generation,
                    WorldId = WorldId,
                    TaiwuId = TaiwuId,
                    AreaId = AreaId,
                    BlockId = BlockId,
                    AreaName = AreaName,
                    ActorScene = ActorScene,
                    MemberIds = new List<int>(MemberIds ?? new List<int>()),
                    CompanionIds = new HashSet<int>(CompanionIds ?? new HashSet<int>()),
                    PresentIds = new HashSet<int>(PresentIds ?? new HashSet<int>()),
                    RemoteIds = new HashSet<int>(RemoteIds ?? new HashSet<int>()),
                    AuthorizedTargetIds = new HashSet<int>(AuthorizedTargetIds ?? new HashSet<int>()),
                    Names = new Dictionary<int, string>(Names ?? new Dictionary<int, string>()),
                    Genders = new Dictionary<int, string>(Genders ?? new Dictionary<int, string>()),
                    Locations = new Dictionary<int, string>(Locations ?? new Dictionary<int, string>()),
                    RelationshipFacts = new Dictionary<int, string>(RelationshipFacts ?? new Dictionary<int, string>()),
                    PersonSkillFacts = new Dictionary<int, NpcSnapshot>(PersonSkillFacts ?? new Dictionary<int, NpcSnapshot>()),
                    DisclosableSecretsByRecipient = new Dictionary<int, List<SecretRef>>(),
                    RelationshipNetwork = RelationshipNetwork,
                    RelationshipMotiveFacts = RelationshipMotiveFacts,
                };
            }

            internal bool IsPresent(int id)
                => id > 0 && (!ActorScene && id == TaiwuId
                    || PresentIds != null && PresentIds.Contains(id));

            internal bool IsKnown(int id)
                => id > 0 && (id == TaiwuId || IsPresent(id)
                    || RemoteIds != null && RemoteIds.Contains(id));

            internal bool IsCurrent()
            {
                if (!WorldLifecycle.IsSameWorld(Generation) || WorldLifecycle.WorldId != WorldId) return false;
                if (ActorScene) return true; // 提交前仍由后端按行动者实时 Location 做 race 复核。
                short area, block;
                return NpcSnapshotReader.TryGetCurrentWorldArea(out area, out block) && area == AreaId && block == BlockId;
            }

            internal string Render(int actorId)
            {
                var present = new List<string>();
                int presentHidden = 0;
                var stablePresentIds = new List<int>(PresentIds ?? new HashSet<int>());
                stablePresentIds.Sort();
                foreach (int id in stablePresentIds)
                {
                    if (id <= 0 || id == actorId || id == TaiwuId) continue;
                    if (present.Count >= 48) { presentHidden++; continue; }
                    present.Add((Names.ContainsKey(id) ? Names[id] : ("#" + id)) + "(#" + id + ","
                        + (Genders.ContainsKey(id) ? Genders[id] : "未知") + ",位置:"
                        + (Locations.ContainsKey(id) ? Locations[id] : CurrentSceneLocation()) + ")");
                }
                present.Sort(StringComparer.Ordinal);
                var remote = new List<string>();
                int remoteHidden = 0;
                var stableRemoteIds = new List<int>(RemoteIds ?? new HashSet<int>());
                stableRemoteIds.Sort();
                foreach (int id in stableRemoteIds)
                {
                    if (id <= 0 || id == actorId || id == TaiwuId || IsPresent(id)) continue;
                    if (remote.Count >= 32) { remoteHidden++; continue; }
                    remote.Add((Names.ContainsKey(id) ? Names[id] : ("#" + id)) + "(#" + id + ","
                        + (Genders.ContainsKey(id) ? Genders[id] : "未知") + ",位置:"
                        + (Locations.ContainsKey(id) ? Locations[id] : "去向不明（已确认异地）") + ")");
                }
                remote.Sort(StringComparer.Ordinal);
                string sceneOwner = ActorScene ? "你自己当前地块上的人物" : "你、太吾、同行同道及太吾当前地块上的人物";
                string taiwuPresence = IsPresent(TaiwuId)
                    ? "太吾本人也在此处，可作为当面行动目标。"
                    : "太吾本人不在此处，只能通过千里传音联系，不能对太吾做当面物理行动。";
                return "【人物与距离】" + sceneOwner + "此刻处在"
                    + (string.IsNullOrWhiteSpace(AreaName) ? ("(area=" + AreaId + ",block=" + BlockId + ")") : ("「" + AreaName + "」"))
                    + "。" + taiwuPresence + "以下人物也在场，可作为本月主动行为目标并进行符合工具前置的当面行为:"
                    + (present.Count == 0 ? "暂无其他已列出人物" : string.Join("、", present.ToArray()))
                    + (presentHidden > 0 ? ("；另有" + presentHidden + "人未展开，可用完整姓名指定") : "")
                    + "。以下是你关系网中当前不在场、但可千里传音的远方联系人:"
                    + (remote.Count == 0 ? "暂无已列出的联系人" : string.Join("、", remote.ToArray()))
                    + (remoteHidden > 0 ? ("；另有" + remoteHidden + "名联系人未展开，可用完整姓名指定") : "")
                    + "。你也可用完整姓名联系未列出的任意人物；代码只接受全局唯一的严格全名。异地人物通常只能传音及进行言语能够成立的交流、关系或态度行为，绝不能隔空赠物、换物、偷窃、疗伤、传授、写书交付或战斗；过月下毒、擒拿、杀人是整月寻至目标后实施的明确例外。";
            }

            private string CurrentSceneLocation()
                => string.IsNullOrWhiteSpace(AreaName)
                    ? ("area=" + AreaId + ",block=" + BlockId) : AreaName;
        }

        // Plans and non-conflicting world mutations may run concurrently. Destructive steps
        // claim their resolved target for the batch so two agents cannot consume the same
        // victim/inventory snapshot, while unrelated NPCs remain genuinely parallel.
        private sealed class CompanionExecutionArbiter
        {
            private readonly object _gate = new object();
            private readonly Dictionary<string, int> _claimedSharedResources = new Dictionary<string, int>(StringComparer.Ordinal);

            internal bool TryClaimStep(string toolName, int actorId, int targetId,
                out string claimKey, out bool claimWasNew, out string conflict)
            {
                claimKey = null;
                claimWasNew = false;
                conflict = null;
                string tool = NormalizePlanToolName(toolName);
                // Only a destructive/control step claims character state, and only after all
                // of that step's authoritative preflight/evidence checks have passed. Planning
                // a later destructive step must not reserve a target that is never dispatched.
                if (tool != "steal" && tool != "poison" && tool != "kill" && tool != "capture") return true;
                if (actorId <= 0 || targetId <= 0)
                { conflict = tool + " 的共享目标无法唯一识别"; return false; }
                claimKey = "character-state|#" + targetId;
                lock (_gate)
                {
                    if (_claimedSharedResources.TryGetValue(claimKey, out int owner))
                    {
                        if (owner != actorId)
                        { conflict = "同批较早同道已占用 " + claimKey; claimKey = null; return false; }
                        // This actor already committed an earlier destructive step against the
                        // same target. A later failed step must not release that committed lock.
                        return true;
                    }
                    _claimedSharedResources[claimKey] = actorId;
                    claimWasNew = true;
                }
                return true;
            }

            internal void ReleaseUncommittedClaim(string claimKey, int actorId)
            {
                if (string.IsNullOrWhiteSpace(claimKey) || actorId <= 0) return;
                lock (_gate)
                    if (_claimedSharedResources.TryGetValue(claimKey, out int owner) && owner == actorId)
                        _claimedSharedResources.Remove(claimKey);
            }

            internal static string CanonicalSceneTarget(JToken token, NpcSnapshot actor, CompanionSceneContext scene)
            {
                string raw = token == null ? "" : token.ToString().Trim();
                if (raw.Length == 0) return null;
                if (string.Equals(raw, "自己", StringComparison.Ordinal) || string.Equals(raw, "我", StringComparison.Ordinal))
                    return actor == null ? null : "#" + actor.NpcId;
                if (string.Equals(raw, "太吾", StringComparison.Ordinal))
                    return actor == null ? null : "#" + actor.TaiwuId;
                int id;
                if (raw[0] == '#' && int.TryParse(raw.Substring(1), out id) && id > 0) return "#" + id;
                if (int.TryParse(raw, out id) && id > 0) return "#" + id;
                if (scene == null || scene.Names == null) return "name:" + raw;
                int hit = 0;
                foreach (var pair in scene.Names)
                    if (string.Equals(pair.Value, raw, StringComparison.Ordinal))
                    {
                        if (hit != 0) return null;
                        hit = pair.Key;
                    }
                return hit > 0 ? "#" + hit : "name:" + raw;
            }
        }

        private sealed class EvidenceFact
        {
            internal string Id;
            internal string Kind;
            internal string Text;
        }

        private sealed class CompanionEvidenceContext
        {
            internal readonly Dictionary<string, EvidenceFact> Facts = new Dictionary<string, EvidenceFact>(StringComparer.Ordinal);

            internal string Render()
            {
                if (Facts.Count == 0) return "【危险行动动机线索】无已结构化记录；仍可依据人物欲望、利益、秘密、保护他人、受命、冲突升级等其他真实动机判断。";
                var sb = new StringBuilder("【危险行动动机线索（只是可能动机，不是硬门槛）】\n");
                foreach (var fact in Facts.Values) sb.Append("- ").Append(fact.Id).Append(" [").Append(fact.Kind).Append("]: ").Append(fact.Text).Append('\n');
                return sb.ToString();
            }

        }

        private sealed class MutationJournalRecord
        {
            public string OperationId;
            public string MutationKey;
            public string ToolName;
            public string Status; // prepared | pending | succeeded | failed | rejected | canceled | unknown
            public string Code;
            public string Receipt;
            public bool BackendReceiptStored;
            public bool AckOutboxCommitted;
            public bool Retryable;
            public string Message;
            public uint WorldId;
            public int TaiwuId;
            public int NpcId;
            public string ActorName;
            public string ActorLocation;
            public int TargetId;
            public string TargetName;
            public string BatchId;
            public int StepIndex;
            public long ConversationClearEpoch;
            public string DispatchEnvelopeJson;
            public string DispatchEnvelopeDigest;
            public int RecoveryDispatchCount;
            public string OutcomeText;
            // Frozen from the successful executor callback, independently of ProjectionReceiptsJson.
            public string ProjectionAsset;
            public string ProjectionOutcomesJson;
            // Batch-level snapshot of code-owned typed receipts. Repeated on every journal
            // entry so a crash before chat projection can still rebuild exact actor/target/asset facts.
            public string ProjectionReceiptsJson;
            // Frozen from CompanionSceneContext when the mutation is prepared.  This is the
            // independent authority set used to revalidate typed receipts after a crash.
            public string ProjectionParticipantIdsJson;
            public string ProjectionText;
            public bool ProjectionCommitted;
            public int WorldDate;
            public long UpdatedUtcTicks;
        }

        private sealed class MutationJournalContext
        {
            internal uint WorldId;
            internal int TaiwuId;
            internal int NpcId;
            internal string ActorName;
            internal string ActorLocation;
            internal int TargetId;
            internal string TargetName;
            internal string BatchId;
            internal int StepIndex;
            internal long ConversationClearEpoch;
            internal string DispatchEnvelopeJson;
            internal string DispatchEnvelopeDigest;
            internal string ProjectionParticipantIdsJson;
        }

        private sealed class MutationAckRequest
        {
            internal uint WorldId;
            internal int TaiwuId;
            internal string OperationId;
        }

        private sealed class MutationJournalDocument
        {
            public int Version = 8;
            public long Revision;
            public List<MutationJournalRecord> Entries = new List<MutationJournalRecord>();
        }

        private sealed class MutationLease
        {
            internal readonly int BatchEpoch;
            internal readonly string ToolName;
            internal readonly string MutationKey;
            internal readonly string OperationId;
            internal readonly string JournalPath;
            private int _completionState;   // 0=pending, 1=known callback/dispatch failure, 2=callback timeout (unknown)
            private string _outcomeStatus;
            private bool _backendReceiptStored;
            private bool _journalPersisted;
            private readonly object _completionGate = new object();
            private readonly Timer _watchdog;

            internal MutationLease(int batchEpoch, string toolName, string mutationKey, string operationId, string journalPath)
            {
                BatchEpoch = batchEpoch;
                ToolName = toolName ?? "unknown";
                MutationKey = mutationKey ?? "";
                OperationId = operationId ?? "";
                JournalPath = journalPath;
                _watchdog = new Timer(_ => ExpireUnknown(), null,
                    MutationCallbackTimeoutSeconds * 1000, Timeout.Infinite);
            }

            internal bool IsCompleted => Volatile.Read(ref _completionState) != 0;
            internal bool IsUnknown => Volatile.Read(ref _completionState) == 2
                || string.Equals(Volatile.Read(ref _outcomeStatus), "unknown", StringComparison.Ordinal);
            internal bool HasAuthoritativeSucceededReceipt
            {
                get
                {
                    lock (_completionGate)
                        return _completionState == 1 && _journalPersisted && _backendReceiptStored
                            && string.Equals(_outcomeStatus, "succeeded", StringComparison.Ordinal);
                }
            }

            internal void CompleteOutcome(ToolOutcome outcome)
            {
                bool receiptUnconfirmed;
                bool persisted;
                lock (_completionGate)
                {
                    if (_completionState != 0) return;
                    try { _watchdog.Dispose(); } catch { }
                    if (outcome == null)
                        outcome = ToolOutcome.Unknown(OperationId, "结构化 operation 回执为空");
                    if (!string.Equals(outcome.OperationId, OperationId, StringComparison.Ordinal)
                        || outcome.IsSucceeded && string.IsNullOrWhiteSpace(outcome.Receipt))
                    {
                        outcome = new ToolOutcome
                        {
                            OperationId = OperationId,
                            Status = "unknown",
                            Code = "AUTHORITATIVE_RECEIPT_MISSING_OR_MISMATCHED",
                            Retryable = true,
                            Message = "成功回调缺少与当前 operationId 精确绑定的 durable backend receipt",
                        };
                    }
                    _outcomeStatus = string.IsNullOrWhiteSpace(outcome.Status) ? "unknown" : outcome.Status.Trim().ToLowerInvariant();
                    receiptUnconfirmed = !outcome.IsTerminal;
                    _backendReceiptStored = !string.IsNullOrWhiteSpace(outcome.Receipt);
                    persisted = UpdateMutationJournal(JournalPath, MutationKey, OperationId, ToolName,
                        _outcomeStatus,
                        0, outcome.Code, outcome.Receipt, outcome.Retryable, outcome.Message);
                    if (!persisted)
                    {
                        // 已收到终态但本地 durable journal 未落盘时，绝不能 ACK 或继续依赖动作。
                        receiptUnconfirmed = true;
                        UpdateMutationJournal(JournalPath, MutationKey, OperationId, ToolName, "unknown", 0,
                            "LOCAL_JOURNAL_PERSIST_FAILED", outcome.Receipt, false,
                            "权威回执已到达，但本地副作用日志无法可靠落盘");
                    }
                    _journalPersisted = persisted;
                    _completionState = receiptUnconfirmed ? 2 : 1;
                }
                EffectHandler.ForgetOperationOutcomeObserver(OperationId);
                if (receiptUnconfirmed)
                {
                    lock (MutationGate)
                        if (!string.IsNullOrWhiteSpace(MutationKey)) UnknownMutationQuarantine.Add(MutationKey);
                }
                if (receiptUnconfirmed && TryInvalidateBatch(BatchEpoch))
                    RequestGuardRelease(BatchEpoch, "operation_receipt_unknown");
                CompleteMutationLease(BatchEpoch);
            }

            internal void CompleteLegacy(bool success, string message)
            {
                bool unconfirmed = IsUnconfirmedMutationMessage(message);
                CompleteOutcome(new ToolOutcome
                {
                    OperationId = OperationId,
                    Status = success ? "succeeded" : (unconfirmed ? "unknown" : "failed"),
                    Code = success ? "OK" : (unconfirmed ? "LEGACY_UNCONFIRMED" : "LEGACY_FAILED"),
                    Retryable = unconfirmed,
                    Message = message,
                });
            }

            internal bool PersistOutcomeText(string outcomeText, string projectionAsset)
            {
                if (string.IsNullOrWhiteSpace(outcomeText)) return false;
                return UpdateMutationProjectionEvidence(JournalPath, MutationKey, outcomeText.Trim(),
                    (projectionAsset ?? string.Empty).Trim());
            }

            internal void CompleteDispatchException(Exception exception)
            {
                CompleteOutcome(new ToolOutcome
                {
                    OperationId = OperationId,
                    Status = "unknown",
                    Code = "RPC_DISPATCH_EXCEPTION",
                    Retryable = true,
                    Message = "RPC 派发异常:" + (exception == null ? "unknown" : exception.Message),
                });
            }

            internal void CancelBeforeDispatch()
            {
                lock (_completionGate)
                {
                    if (_completionState != 0) return;
                    try { _watchdog.Dispose(); } catch { }
                    UpdateMutationJournal(JournalPath, MutationKey, OperationId, ToolName, "canceled", 0,
                        "CANCELED_BEFORE_DISPATCH", null, false, "派发前批次已失效");
                    _outcomeStatus = "canceled";
                    _completionState = 1;
                }
                EffectHandler.ForgetOperationOutcomeObserver(OperationId);
                CompleteMutationLease(BatchEpoch);
            }

            private void ExpireUnknown()
            {
                lock (_completionGate)
                {
                    if (_completionState != 0) return;
                    try { _watchdog.Dispose(); } catch { }
                    UpdateMutationJournal(JournalPath, MutationKey, OperationId, ToolName, "unknown", 0,
                        "CALLBACK_TIMEOUT", null, true, "等待后端回执超时；不会自动重试副作用");
                    _outcomeStatus = "unknown";
                    _completionState = 2;
                }
                EffectHandler.ForgetOperationOutcomeObserver(OperationId);
                lock (MutationGate)
                    if (!string.IsNullOrWhiteSpace(MutationKey)) UnknownMutationQuarantine.Add(MutationKey);
                Debug.LogWarning("[JHYL_COMPANION_MUTATION_UNKNOWN] epoch=" + BatchEpoch
                    + " tool=" + ToolName + " callback_timeout_seconds=" + MutationCallbackTimeoutSeconds
                    + " outcome=unknown no_retry=true quarantine=true");
                // 独立 watchdog 必须自行终止整批并请求解锁，不能假设承载 RunMonthly/RunOne
                // 的 Unity 协程仍有机会运行 finally。其余已派发租约各自回调或超时后，末个租约释放全局锁。
                RequestGuardRelease(BatchEpoch, "mutation_unknown");
                CompleteMutationLease(BatchEpoch);
            }
        }

        public static void CancelForWorldExit()
        {
            int invalidatedEpoch;
            lock (MutationGate)
            {
                invalidatedEpoch = _runEpoch;
                unchecked { _runEpoch++; }
            }
            CancelBatchRequests(invalidatedEpoch);
            RequestGuardRelease(invalidatedEpoch, "world_exit");
        }

        public static void CancelForTravel()
        {
            int batchEpoch;
            lock (MutationGate)
            {
                if (!_running) return;
                batchEpoch = _runEpoch;
            }
            if (!TryInvalidateBatch(batchEpoch)) return;
            RequestGuardRelease(batchEpoch, "travel");
            Debug.Log("[JHYL_COMPANION_MONTHLY_CANCEL] reason=travel epoch=" + batchEpoch);
        }

        public static void CancelForMonthAdvance()
        {
            int batchEpoch;
            lock (MutationGate)
            {
                if (!_running) return;
                batchEpoch = _runEpoch;
            }
            if (!TryInvalidateBatch(batchEpoch)) return;
            RequestGuardRelease(batchEpoch, "native_month_advance");
            Debug.Log("[JHYL_COMPANION_MONTHLY_CANCEL] reason=native_month_advance epoch="
                + batchEpoch);
        }

        public static IEnumerator RunMonthly(int taiwuId, int date, Action<List<CompanionMonthlyResult>> onDone,
            Func<int, IReadOnlyCollection<int>, bool> mutationExecutionGate = null,
            Action<List<CompanionMonthlyResult>, int, int> onProgress = null)
        {
            if (taiwuId <= 0) { Debug.Log("[JHYL_COMPANION_MONTHLY_SKIP] invalid_taiwu"); onDone?.Invoke(null); yield break; }
            if (MonthlySettlement.IsAdvancingMonthForMonthlyWork())
            {
                Debug.Log("[JHYL_COMPANION_MONTHLY_SKIP] native_month_advancing=true");
                onProgress?.Invoke(new List<CompanionMonthlyResult>(), 0, 0);
                onDone?.Invoke(new List<CompanionMonthlyResult>());
                yield break;
            }
            if (MonthlySettlement.IsTravelingForMonthlyWork())
            {
                Debug.Log("[JHYL_COMPANION_MONTHLY_SKIP] traveling=true");
                onProgress?.Invoke(new List<CompanionMonthlyResult>(), 0, 0);
                onDone?.Invoke(new List<CompanionMonthlyResult>());
                yield break;
            }
            int configuredCount = CompanionMonthlyStore.LoadCount();
            if (configuredCount == 0)
            {
                // JHYL_COMPANION_MONTHLY_ZERO_SHORT_CIRCUIT: the economy preset must not
                // initialize an LLM client, candidate scene, host or request budget at all.
                Debug.Log("[JHYL_COMPANION_MONTHLY_SKIP] configured_count=0");
                onProgress?.Invoke(new List<CompanionMonthlyResult>(), 0, 0);
                onDone?.Invoke(new List<CompanionMonthlyResult>());
                yield break;
            }
            MonthlyAgentRequestBudget.Begin(WorldLifecycle.WorldId, taiwuId, date);
            // JHYL_COMPANION_MONTHLY_ALWAYS_MAIN_MODEL: 同道月度行为是会改变游戏状态的多轮
            // Agent，必须与单聊使用同一主模型能力；后台快模型只承担无副作用的辅助任务。
            OpenAiCompatibleClient plannerClient = LlmService.GetClient();
            if (plannerClient == null) { Debug.Log("[JHYL_COMPANION_MONTHLY_SKIP] no_llm_client"); onDone?.Invoke(null); yield break; }
            int plannerConcurrency = Math.Max(1,
                plannerClient.RecommendedMonthlyPlannerConcurrency);
            int runEpoch;
            int blockedInflight;
            if (!TryStartBatch(out runEpoch, out blockedInflight))
            {
                Debug.Log("[JHYL_COMPANION_MONTHLY_SKIP] running_or_stale_mutation inflight=" + blockedInflight);
                onDone?.Invoke(null);
                yield break;
            }
            float batchStarted = Time.unscaledTime;
            int generation = WorldLifecycle.Generation;
            CancellationToken batchCancellation = GetBatchCancellationToken(runEpoch);
            var results = new List<CompanionMonthlyResult>();
            int completionState = 0;
            Action<List<CompanionMonthlyResult>> completeOnce = value =>
            {
                if (Interlocked.Exchange(ref completionState, 1) == 0) onDone?.Invoke(value);
            };
            bool timeoutInvalidated = false;
            List<CompanionMonthlyResult> timeoutResults = null;
            try
            {
                bool journalReconciled = false;
                yield return ReconcileCompanionMutationJournal(generation, taiwuId, ok => journalReconciled = ok);
                if (!journalReconciled || !BatchIsCurrent(runEpoch, generation))
                {
                    Debug.LogWarning("[江湖有灵] 同道副作用 journal 损坏或世界已切换，本批 fail-closed 跳过");
                    yield break;
                }
                string diag = null;
                List<int> pool = null;
                List<int> team = null;
                float poolStarted = Time.unscaledTime;
                yield return BuildCandidatePool(taiwuId, date, (ids, d) => { pool = ids; diag = d; },
                    () => BatchIsCurrent(runEpoch, generation), ids => team = ids);
                Debug.Log("[JHYL_COMPANION_MONTHLY_TIMING] candidate_pool_ms=" + ElapsedMs(poolStarted));
                if (!BatchIsCurrent(runEpoch, generation)) yield break;
                pool = pool ?? new List<int>();
                if (pool.Count == 0)
                {
                    Debug.Log("[JHYL_COMPANION_MONTHLY_EMPTY_POOL_DIAG] " + diag);
                    yield break;
                }
                Debug.Log("[JHYL_COMPANION_MONTHLY_POOL_DIAG] " + diag);

                CompanionSceneContext scene = null;
                yield return BuildCompanionSceneContext(taiwuId, generation, team, pool, x => scene = x,
                    () => BatchIsCurrent(runEpoch, generation));
                if (!BatchIsCurrent(runEpoch, generation) || scene == null || !scene.IsCurrent())
                {
                    Debug.LogWarning("[江湖有灵] 同道主动行事无法建立权威同地场景，本批安全跳过");
                    yield break;
                }

                var selected = CompanionMonthlySelectionPolicy.Select(pool, configuredCount,
                    WorldLifecycle.WorldId, taiwuId, date);
                Debug.Log("[JHYL_COMPANION_MONTHLY_SELECTION] requested=" + configuredCount
                    + " eligible=" + pool.Count + " selected=" + selected.Count
                    + " ids=[" + string.Join(",", selected) + "]");
                CompanionActionNoveltySnapshot noveltySnapshot =
                    BuildCompanionActionNoveltySnapshot(date);
                var childDispatcher = new BoundedWorkDispatcher(
                    selected.Count, plannerConcurrency);
                int[] progressHeartbeat = { 0 };
                Action heartbeat = () => Interlocked.Increment(ref progressHeartbeat[0]);
                var executionArbiter = new CompanionExecutionArbiter();
                if (WorldLifecycle.IsSameWorld(generation))
                    onProgress?.Invoke(new List<CompanionMonthlyResult>(), 0, selected.Count);
                var host = TalkEntryHost.Instance;
                // 没有独立宿主时不能串行 yield RunOne：内部 mutation 等回调是无本地超时的，
                // 父协程将永远到不了 600 秒 watchdog。宿主缺失属于生命周期异常，安全跳过本批。
                if (host == null)
                {
                    Debug.LogWarning("[江湖有灵] 同道主动行事缺少 TalkEntryHost，本批已安全跳过");
                    yield break;
                }
                Action<int> startChild = null;
                startChild = executionIndex =>
                {
                    int npc = selected[executionIndex];
                    int childCompletion = 0;
                    Action<CompanionMonthlyResult> childDone = r =>
                    {
                        heartbeat();
                        if (Interlocked.Exchange(ref childCompletion, 1) != 0) return;
                        if (!childDispatcher.CompleteOne()) return;
                        if (r != null && WorldLifecycle.IsSameWorld(generation)) results.Add(r);
                        int remaining = childDispatcher.Pending;
                        // JHYL_COMPANION_MONTHLY_INCREMENTAL_PROGRESS: 月度总览无需等待最后一位同道；
                        // 每个子 Agent 收口后都交付不可变快照，UI 可保留其余席位的“生成中”占位。
                        if (WorldLifecycle.IsSameWorld(generation))
                            onProgress?.Invoke(new List<CompanionMonthlyResult>(results),
                                selected.Count - Math.Max(0, remaining), selected.Count);
                    };
                    IEnumerator co = RunOne(npc, taiwuId, date, generation, runEpoch, batchCancellation, scene,
                        executionArbiter, mutationExecutionGate, noveltySnapshot,
                        childDone, heartbeat);
                    host.StartCoroutine(RunOneSafely(co, runEpoch, npc, () => childDone(null)));
                };
                Action pumpChildren = () =>
                {
                    // 每帧只启动一个子 Agent。并发上限不变，但把人物快照、背包、功法等
                    // 本地 RPC 的首轮扇出摊到数帧，避免过月结算时几十个请求挤在同一帧。
                    childDispatcher.Pump(BatchIsCurrent(runEpoch, generation), startChild,
                        (index, error) =>
                    {
                        heartbeat();
                        Debug.LogWarning("[江湖有灵] 同道过月子 Agent 启动失败:npc="
                            + selected[index] + " " + error.GetType().Name);
                    }, 1);
                };
                Action retireQueuedChildren = () =>
                {
                    int retired = childDispatcher.RetireQueued();
                    if (retired <= 0) return;
                    heartbeat();
                    Debug.Log("[JHYL_COMPANION_QUEUED_PLANNERS_RETIRED] count=" + retired
                        + " epoch=" + runEpoch);
                };
                // Start a bounded group immediately, then admit the next companion whenever
                // any active agent closes. Planning remains genuinely parallel without letting
                // a large user-selected roster multiply main-thread snapshots and allocations.
                pumpChildren();
                Debug.Log("[JHYL_COMPANION_BOUNDED_PARALLEL_SCHEDULER] selected=" + selected.Count
                    + " active_limit=" + plannerConcurrency);
                float hard = Time.unscaledTime + BatchIdleTimeoutSeconds;
                int lastProgressHeartbeat = Volatile.Read(ref progressHeartbeat[0]);
                // batch epoch 可能因某一步 UNKNOWN 被安全失效；仍给各 RunOne 一帧收束并回传 UNKNOWN，
                // 不能立刻弹空摘要。每有一位同道完成就续期；世界切换才立即退出。
                while (childDispatcher.Pending > 0 && Time.unscaledTime < hard
                    && WorldLifecycle.IsSameWorld(generation))
                {
                    if (BatchIsCurrent(runEpoch, generation)) pumpChildren();
                    else retireQueuedChildren();
                    int currentHeartbeat = Volatile.Read(ref progressHeartbeat[0]);
                    if (currentHeartbeat != lastProgressHeartbeat)
                    {
                        lastProgressHeartbeat = currentHeartbeat;
                        hard = Time.unscaledTime + BatchIdleTimeoutSeconds;
                    }
                    yield return null;
                }
                if (childDispatcher.Pending > 0)
                {
                    Debug.LogWarning("[江湖有灵] 同道过月主动行事等待超时:剩余 "
                        + childDispatcher.Pending + "/" + selected.Count);
                    // JHYL_COMPANION_MONTHLY_BATCH_EPOCH: timeout invalidates the whole batch
                    // before finally requests release. Every old wait/RPC/persist gate then
                    // fails closed; unresolved mutation leases retain _running and block all
                    // following batches until their callbacks prove the backend work finished.
                    if (Time.unscaledTime >= hard && TryInvalidateBatch(runEpoch))
                    {
                        timeoutInvalidated = true;
                        timeoutResults = new List<CompanionMonthlyResult>(results);
                        yield break;
                    }
                }
                Debug.Log("[江湖有灵] 同道过月主动行事:候选 " + pool.Count + ",入选 " + selected.Count + ",行动 " + results.Count + ",总耗时 " + ElapsedMs(batchStarted) + "ms");
            }
            finally
            {
                if (timeoutInvalidated)
                {
                    // TryInvalidateBatch above deliberately happens before requesting release.
                    // The mutation fence keeps _running held while any dispatched backend
                    // mutation still has no callback, even across world changes.
                    RequestGuardRelease(runEpoch, "timeout");
                    completeOnce(WorldLifecycle.IsSameWorld(generation) ? (timeoutResults ?? new List<CompanionMonthlyResult>()) : null);
                }
                else
                {
                    bool ownsRun = BatchEpochIsCurrent(runEpoch);
                    if (ownsRun) RequestGuardRelease(runEpoch, "completed");
                    // mutation unknown 会先失效 batch epoch；完成回调不能再依赖 ownsRun，否则外层会等到 600 秒。
                    completeOnce(WorldLifecycle.IsSameWorld(generation) ? results : null);
                }
            }
        }

        public static bool ChattedThisSettlementMonth(int taiwuId, int npcId, int settlementDate)
        {
            // 保留给诊断与旧数据迁移：聊天日期只是一种动因，不再决定同道月度候选资格。
            int last = TalkOrchestrator.LastChatDate(taiwuId, npcId);
            return last >= 0 && (last == settlementDate || (settlementDate > 0 && last == settlementDate - 1));
        }

        public static IEnumerator BuildCandidatePool(int taiwuId, int date, Action<List<int>, string> onDone,
            Func<bool> stillCurrent = null, Action<List<int>> onTeam = null)
        {
            bool done = false, ageAuthorityReliable = false; List<int> team = null;
            if (stillCurrent != null && !stillCurrent()) { onDone?.Invoke(null, "cancelled"); yield break; }
            // JHYL_COMPANION_MONTHLY_NO_BABIES:本体将 Baby 定义为 AgeGroup 0（当前年龄未满 AgeBaby）。
            // 必须让后端直接按 Character.GetAgeGroup 过滤；读取失败不能退回未经年龄校验的队伍。
            EffectHandler.QueryNonBabyCompanionGroup(taiwuId, (ok, ids) =>
            { ageAuthorityReliable = ok; team = ids; done = true; });
            float dl = Time.unscaledTime + EffectHandler.ReadOnlyQueryWaitSeconds;
            while (!done && Time.unscaledTime < dl && (stillCurrent == null || stillCurrent())) yield return null;
            if (stillCurrent != null && !stillCurrent()) { onDone?.Invoke(null, "cancelled"); yield break; }
            team = team ?? new List<int>();
            if (!done || !ageAuthorityReliable)
            {
                onTeam?.Invoke(new List<int>());
                onDone?.Invoke(new List<int>(), "date=" + date
                    + " teamDone=" + done + " babyFilterReliable=false policy=fail_closed_no_baby_actions");
                yield break;
            }

            // 当前同道默认入选；玩家在单聊中手动加入的普通 NPC 先经过同一套活体、年龄、
            // 动物权威校验。删除同道会写入排除表，直到玩家重新加入为止。
            CompanionMonthlyCandidateStore.Snapshot stored = CompanionMonthlyCandidateStore.Load(taiwuId);
            List<int> explicitEligible = null; bool explicitDone = false; bool explicitReliable = false;
            EffectHandler.QueryMonthlyAgentEligibility(stored.Included,
                (ok, ids, rejected) => { explicitReliable = ok; explicitEligible = ids; explicitDone = true; });
            float explicitDeadline = Time.unscaledTime + EffectHandler.ReadOnlyQueryWaitSeconds;
            while (!explicitDone && Time.unscaledTime < explicitDeadline
                && (stillCurrent == null || stillCurrent())) yield return null;
            if (!explicitDone || !explicitReliable)
            {
                onTeam?.Invoke(new List<int>(team));
                var companionsOnly = CompanionMonthlyCandidateStore.BuildEffective(taiwuId, team,
                    new List<int>());
                companionsOnly = CompanionMonthlySelectionPolicy.NormalizeCandidates(companionsOnly, taiwuId);
                onDone?.Invoke(companionsOnly, BuildCandidateDiag(taiwuId, date, team,
                    companionsOnly, true)
                    + " manualEligibilityReliable=false policy=companions_continue_manual_deferred");
                yield break;
            }
            var result = CompanionMonthlyCandidateStore.BuildEffective(taiwuId, team, explicitEligible);
            result = CompanionMonthlySelectionPolicy.NormalizeCandidates(result, taiwuId);
            onTeam?.Invoke(new List<int>(team));
            onDone?.Invoke(result, BuildCandidateDiag(taiwuId, date, team, result, done)
                + " babyAnimalFilterReliable=true manual=" + explicitEligible.Count);
        }

        // Unity does not route exceptions from a yielded child IEnumerator through the
        // parent's try/finally. Drive the whole RunOne stack here so one unexpected child
        // failure cannot strand the stable-order arbiter or the global monthly guard.
        private static IEnumerator RunOneSafely(IEnumerator root, int batchEpoch, int npcId, Action onFault)
        {
            var stack = new Stack<IEnumerator>();
            if (root != null) stack.Push(root);
            while (stack.Count > 0)
            {
                IEnumerator current = stack.Peek();
                object yielded = null;
                bool moved;
                try
                {
                    moved = current.MoveNext();
                    if (moved) yielded = current.Current;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[JHYL_COMPANION_CHILD_EXCEPTION] npc=" + npcId + " epoch="
                        + batchEpoch + " " + e.GetType().Name);
                    if (TryInvalidateBatch(batchEpoch)) RequestGuardRelease(batchEpoch, "child_exception");
                    onFault?.Invoke();
                    yield break;
                }
                if (!moved) { stack.Pop(); continue; }
                if (yielded is IEnumerator nested && !(yielded is YieldInstruction)
                    && !(yielded is CustomYieldInstruction))
                {
                    stack.Push(nested);
                    continue;
                }
                yield return yielded;
            }
            // RunOne is expected to report exactly once. The caller callback is idempotent,
            // so this final nudge also covers an accidental normal path that forgot to report.
            onFault?.Invoke();
        }

        private static IEnumerator BuildCompanionSceneContext(int taiwuId, int generation, List<int> team,
            List<int> candidates,
            Action<CompanionSceneContext> onDone, Func<bool> stillCurrent)
        {
            if (!PredicateIsCurrent(stillCurrent) || !WorldLifecycle.IsSameWorld(generation)) { onDone?.Invoke(null); yield break; }
            short area, block, template; string areaName;
            if (!NpcSnapshotReader.TryGetCurrentWorldArea(out area, out block, out template, out areaName))
            { onDone?.Invoke(null); yield break; }
            var scene = new CompanionSceneContext
            {
                Generation = generation,
                WorldId = WorldLifecycle.WorldId,
                TaiwuId = taiwuId,
                AreaId = area,
                BlockId = block,
                AreaName = areaName,
            };
            var unique = new HashSet<int>();
            scene.PresentIds.Add(taiwuId);
            if (candidates != null)
                foreach (int id in candidates)
                    if (id > 0 && id != taiwuId && unique.Add(id)) scene.MemberIds.Add(id);
            unique.Clear();
            if (team != null)
                foreach (int id in team)
                    if (id > 0 && id != taiwuId && unique.Add(id))
                    {
                        scene.CompanionIds.Add(id);
                        scene.PresentIds.Add(id); // 本体同行语义：同道视为与太吾同行。
                    }

            List<int> sameBlock = null; bool blockDone = false;
            EffectHandler.QuerySameBlockChars(taiwuId, ids => { sameBlock = ids; blockDone = true; }, true);
            float blockDeadline = Time.unscaledTime + EffectHandler.ReadOnlyQueryWaitSeconds;
            while (!blockDone && Time.unscaledTime < blockDeadline && PredicateIsCurrent(stillCurrent)) yield return null;
            if (!blockDone) { onDone?.Invoke(null); yield break; }
            if (sameBlock != null)
                foreach (int id in sameBlock)
                    if (id > 0 && id != taiwuId) scene.PresentIds.Add(id);
            foreach (int id in scene.PresentIds)
                if (id > 0) scene.Locations[id] = string.IsNullOrWhiteSpace(areaName)
                    ? ("area=" + area + ",block=" + block) : areaName;

            var nameIds = new List<int>(scene.PresentIds);
            nameIds.Remove(taiwuId);
            nameIds.Sort();
            List<string> names = null, genders = null; bool namesDone = nameIds.Count == 0;
            if (!namesDone) EffectHandler.QueryCharNamesAndGenders(nameIds,
                (x, g) => { names = x; genders = g; namesDone = true; });
            float ndl = Time.unscaledTime + EffectHandler.ReadOnlyQueryWaitSeconds;
            while (!namesDone && Time.unscaledTime < ndl && PredicateIsCurrent(stillCurrent)) yield return null;
            for (int i = 0; i < nameIds.Count; i++)
            {
                string name = names != null && i < names.Count ? names[i] : null;
                scene.Names[nameIds[i]] = string.IsNullOrWhiteSpace(name) ? ("#" + nameIds[i]) : name;
                string gender = genders != null && i < genders.Count ? genders[i] : null;
                scene.Genders[nameIds[i]] = string.IsNullOrWhiteSpace(gender) ? "未知" : gender;
            }

            if (!PredicateIsCurrent(stillCurrent) || !scene.IsCurrent()) { onDone?.Invoke(null); yield break; }
            onDone?.Invoke(scene);
        }

        private static IEnumerator PopulateRemoteContacts(CompanionSceneContext scene, int actorId,
            Func<bool> stillCurrent)
        {
            if (scene == null || actorId <= 0 || !PredicateIsCurrent(stillCurrent)) yield break;
            List<int> related = null; bool done = false; bool contextOk = false;
            EffectHandler.QueryNpcRelationIdsWithBehaviorContext(actorId, (ok, ids, relations, motiveFacts) =>
            {
                contextOk = ok;
                related = ids;
                scene.RelationshipNetwork = relations;
                scene.RelationshipMotiveFacts = motiveFacts;
                done = true;
            });
            float deadline = Time.unscaledTime + EffectHandler.ReadOnlyQueryWaitSeconds;
            while (!done && Time.unscaledTime < deadline && PredicateIsCurrent(stillCurrent)) yield return null;
            if (!done)
            {
                scene.RelationshipNetwork = "（权威关系网读取超时；不得猜测，涉及第三方前必须调用关系查询。）";
                scene.RelationshipMotiveFacts = "";
                yield break;
            }
            if (!PredicateIsCurrent(stillCurrent) || !scene.IsCurrent()) yield break;
            if (!contextOk)
            {
                scene.RelationshipNetwork = "（权威关系网暂时读取失败；不得猜测，涉及第三方前必须调用关系查询。）";
                scene.RelationshipMotiveFacts = "";
            }
            var idsToName = new List<int>();
            if (related != null)
                foreach (int id in related)
                {
                    if (id <= 0 || id == actorId) continue;
                    // QueryNpcRelationIdsWithBehaviorContext 已在同一次权威读取中返回完整显著
                    // 关系网。把其中的人物标记为本轮已知关系，后续动作不必再花一个 LLM
                    // 回合重复 query_relationship；若关系动作成功，失效器会清除此快照。
                    if (id != scene.TaiwuId)
                        scene.RelationshipFacts[id] = "本轮权威显著关系网已确认；具体关系见默认上下文";
                    if (id == scene.TaiwuId || scene.IsPresent(id)) continue;
                    if (scene.RemoteIds.Add(id)) idsToName.Add(id);
                }
            idsToName.Sort();
            if (idsToName.Count == 0) yield break;
            List<string> names = null, genders = null; bool namesDone = false;
            EffectHandler.QueryCharNamesAndGenders(idsToName,
                (value, genderValues) => { names = value; genders = genderValues; namesDone = true; });
            int pendingLocations = idsToName.Count;
            foreach (int remoteIdValue in idsToName)
            {
                int remoteId = remoteIdValue;
                EffectHandler.QueryCharLocation(remoteId, (area, block, ok) =>
                {
                    if (ok && area >= 0 && block >= 0)
                    {
                        string readable = NpcSnapshotReader.ResolveLocationText(
                            new GameData.Domains.Map.Location(area, block));
                        scene.Locations[remoteId] = string.IsNullOrWhiteSpace(readable)
                            ? ("area=" + area + ",block=" + block) : readable;
                    }
                    pendingLocations--;
                });
            }
            float namesDeadline = Time.unscaledTime + EffectHandler.ReadOnlyQueryWaitSeconds;
            while ((!namesDone || pendingLocations > 0) && Time.unscaledTime < namesDeadline
                && PredicateIsCurrent(stillCurrent)) yield return null;
            if (!namesDone || !PredicateIsCurrent(stillCurrent) || !scene.IsCurrent()) yield break;
            for (int i = 0; i < idsToName.Count; i++)
            {
                string name = names != null && i < names.Count ? names[i] : null;
                if (!string.IsNullOrWhiteSpace(name)) scene.Names[idsToName[i]] = name.Trim();
                string gender = genders != null && i < genders.Count ? genders[i] : null;
                if (!string.IsNullOrWhiteSpace(gender)) scene.Genders[idsToName[i]] = gender.Trim();
            }
        }

        private static IEnumerator PrepareActorLocalScene(CompanionSceneContext scene, int actorId,
            Func<bool> stillCurrent, Action<bool> onDone)
        {
            if (scene == null || actorId <= 0 || !PredicateIsCurrent(stillCurrent))
            { onDone?.Invoke(false); yield break; }
            bool done = false, ok = false;
            short area = -1, block = -1;
            List<int> ids = null;
            EffectHandler.QueryActorBlockChars(actorId, (success, a, b, values) =>
            { ok = success; area = a; block = b; ids = values; done = true; });
            float deadline = Time.unscaledTime + EffectHandler.ReadOnlyQueryWaitSeconds;
            while (!done && Time.unscaledTime < deadline && PredicateIsCurrent(stillCurrent)) yield return null;
            if (!done || !ok || area < 0 || block < 0 || !PredicateIsCurrent(stillCurrent))
            { onDone?.Invoke(false); yield break; }

            scene.ActorScene = true;
            scene.AreaId = area;
            scene.BlockId = block;
            scene.AreaName = null;
            scene.PresentIds.Clear();
            scene.RemoteIds.Clear();
            scene.AuthorizedTargetIds.Clear();
            scene.Names.Clear();
            scene.Genders.Clear();
            scene.Locations.Clear();
            scene.PresentIds.Add(actorId);
            if (ids != null)
                foreach (int id in ids) if (id > 0) scene.PresentIds.Add(id);
            foreach (int id in scene.PresentIds)
                if (id > 0) scene.Locations[id] = "area=" + area + ",block=" + block;

            var nameIds = new List<int>(scene.PresentIds);
            nameIds.Remove(actorId);
            nameIds.Sort();
            List<string> names = null, genders = null; bool namesDone = nameIds.Count == 0;
            if (!namesDone) EffectHandler.QueryCharNamesAndGenders(nameIds,
                (value, genderValues) => { names = value; genders = genderValues; namesDone = true; });
            float namesDeadline = Time.unscaledTime + EffectHandler.ReadOnlyQueryWaitSeconds;
            while (!namesDone && Time.unscaledTime < namesDeadline && PredicateIsCurrent(stillCurrent)) yield return null;
            if (!namesDone || !PredicateIsCurrent(stillCurrent))
            { onDone?.Invoke(false); yield break; }
            for (int i = 0; i < nameIds.Count; i++)
            {
                string name = names != null && i < names.Count ? names[i] : null;
                scene.Names[nameIds[i]] = string.IsNullOrWhiteSpace(name) ? ("#" + nameIds[i]) : name;
                string gender = genders != null && i < genders.Count ? genders[i] : null;
                scene.Genders[nameIds[i]] = string.IsNullOrWhiteSpace(gender) ? "未知" : gender;
            }
            onDone?.Invoke(true);
        }

        private static string BuildCandidateDiag(int taiwuId, int date, List<int> team, List<int> pool, bool teamDone)
        {
            string F(List<int> ids)
            {
                if (ids == null || ids.Count == 0) return "[]";
                var parts = new List<string>();
                foreach (var id in ids)
                    if (id > 0)
                        parts.Add(id + ":last=" + TalkOrchestrator.LastChatDate(taiwuId, id));
                return "[" + string.Join(",", parts.ToArray()) + "]";
            }
            return "date=" + date
                + " teamDone=" + teamDone
                + " team=" + F(team)
                + " pool=" + F(pool)
                + " policy=current_companions_plus_manual_candidates";
        }

        private static IEnumerator RunOne(int npcId, int taiwuId, int date, int generation, int batchEpoch,
            CancellationToken batchCancellation,
            CompanionSceneContext scene, CompanionExecutionArbiter executionArbiter,
            Func<int, IReadOnlyCollection<int>, bool> mutationExecutionGate,
            CompanionActionNoveltySnapshot noveltySnapshot,
            Action<CompanionMonthlyResult> onDone, Action heartbeat)
        {
            heartbeat?.Invoke();
            if (batchCancellation.IsCancellationRequested || !BatchIsCurrent(batchEpoch, generation)
                || scene == null || !scene.MemberIds.Contains(npcId) || !scene.IsCurrent())
            { onDone(null); yield break; }
            scene = scene.Clone();
            long conversationClearEpoch = TalkOrchestrator.CaptureConversationClearEpoch(taiwuId, npcId);
            string batchId = OperationId.FromStableKey("companion-batch|" + WorldLifecycle.WorldId + "|"
                + taiwuId + "|" + npcId + "|" + date);
            Func<bool> current = () => !batchCancellation.IsCancellationRequested
                && RunIsCurrent(batchEpoch, generation, taiwuId, npcId, conversationClearEpoch)
                && scene.IsCurrent();
            if (!current()) { onDone(null); yield break; }
            var traceRoot = new LlmTraceContext(batchId,
                "companion:" + WorldLifecycle.WorldId + ":" + taiwuId + ":" + npcId, batchId);
            float oneStarted = Time.unscaledTime;
            var result = new CompanionMonthlyResult { NpcId = npcId, Name = "#" + npcId };
            string mutationJournalPath = MutationJournalPath(WorldLifecycle.WorldId);
            if (TryLoadProjectedCompanionBatch(mutationJournalPath, batchId,
                out CompanionMonthlyResult alreadyProjected))
            {
                ProjectionAuthorityState projectionAuthority = ProjectionAuthorityState.Current;
                yield return ValidateProjectedCompanionBatchAuthority(mutationJournalPath, batchId,
                    generation, current, value => projectionAuthority = value);
                if (projectionAuthority == ProjectionAuthorityState.Current)
                {
                    onDone(alreadyProjected);
                    yield break;
                }
                if (projectionAuthority == ProjectionAuthorityState.Unavailable)
                {
                    Debug.LogWarning("[JHYL_COMPANION_PROJECTION_RESTORE_PAUSED] batch=" + batchId
                        + " reason=current_save_receipt_unavailable");
                    onDone(null);
                    yield break;
                }
                if (!InvalidateStaleProjectedCompanionBatch(mutationJournalPath, batchId, date))
                {
                    Debug.LogWarning("[JHYL_COMPANION_STALE_PROJECTION_RESET_FAILED] batch=" + batchId);
                    onDone(null);
                    yield break;
                }
                Debug.LogWarning("[JHYL_COMPANION_STALE_PROJECTION_RESET] batch=" + batchId
                    + " reason=backend_operation_missing_in_current_save regenerate=true");
            }

            NpcSnapshot snap = null;
            float preloadStarted = Time.unscaledTime;
            yield return NpcSnapshotReader.Fetch(npcId, value => snap = value, false, current);
            heartbeat?.Invoke();
            if (!current() || snap == null || snap.IsDead || snap.NpcId != npcId || snap.TaiwuId != taiwuId)
            { onDone(null); yield break; }
            if (snap.GameAgeGroup == GameData.Domains.Character.AgeGroup.Baby)
            {
                Debug.Log("[JHYL_COMPANION_MONTHLY_SKIP] npc=" + npcId
                    + " reason=game_age_group_baby physiologicalAge=" + snap.PhysiologicalAge
                    + " actualAge=" + snap.ActualAge);
                onDone(null);
                yield break;
            }
            bool actorSceneReady = true;
            if (!scene.CompanionIds.Contains(npcId))
            {
                actorSceneReady = false;
                yield return PrepareActorLocalScene(scene, npcId, current, ok => actorSceneReady = ok);
                heartbeat?.Invoke();
            }
            if (!actorSceneReady || !current())
            {
                if (current()) Debug.LogWarning("[JHYL_COMPANION_MONTHLY_SKIP] npc=" + npcId
                    + " reason=actor_scene_unavailable");
                onDone(null); yield break;
            }
            result.Name = string.IsNullOrWhiteSpace(snap.Name) ? ("#" + npcId) : snap.Name;
            // The backend scene already resolved followers/carried characters. Do not
            // reintroduce a stale DisplayData location into the model or durable receipt.
            snap.LocationText = FormatLiveLocation(scene.AreaId, scene.BlockId);
            result.LocationText = string.IsNullOrWhiteSpace(snap.LocationText)
                ? "去向不明（游戏当前未提供有效地点）" : snap.LocationText.Trim();
            PopulateAuthorizedParticipants(result.AuthorizedParticipantIds, snap, scene);
            yield return PopulateRemoteContacts(scene, npcId, current);
            heartbeat?.Invoke();
            if (!current()) { onDone(null); yield break; }
            PopulateAuthorizedParticipants(result.AuthorizedParticipantIds, snap, scene);
            yield return NpcSnapshotReader.FetchGiftables(npcId, snap, current);
            heartbeat?.Invoke();
            if (!current()) { onDone(null); yield break; }
            yield return NpcSnapshotReader.FetchSkills(npcId, snap, current);
            heartbeat?.Invoke();
            if (!current()) { onDone(null); yield break; }
            yield return NpcSnapshotReader.FetchStudyProgress(npcId, snap, current);
            heartbeat?.Invoke();
            if (!current()) { onDone(null); yield break; }
            yield return NpcSnapshotReader.FetchLifeSkills(npcId, snap, current);
            heartbeat?.Invoke();
            if (!current()) { onDone(null); yield break; }
            Debug.Log("[JHYL_COMPANION_MONTHLY_TIMING] npc=" + result.Name
                + " preload_ms=" + ElapsedMs(preloadStarted));

            OpenAiCompatibleClient client = LlmService.GetClient();
            if (client == null) { onDone(null); yield break; }
            CompanionEvidenceContext evidence = BuildEvidenceContext(snap, date, scene);
            List<ToolDef> actionTools = BuildCompanionTools(snap);
            List<CompanionBehaviorPriority> behaviorPriorities =
                BuildCompanionBehaviorPriorities(actionTools, noveltySnapshot);
            actionTools = OrderCompanionToolsByBehaviorPriority(actionTools,
                behaviorPriorities);
            var availableTools = new HashSet<string>(StringComparer.Ordinal);
            var monthlyToolNames = new List<string>();
            foreach (ToolDef tool in actionTools)
                if (tool != null && !string.IsNullOrWhiteSpace(tool.Name))
                { availableTools.Add(tool.Name); monthlyToolNames.Add(tool.Name); }
            string monthlySkills = ConversationSkillCatalog.BuildCompanionMonthlySkillBundle(monthlyToolNames);
            string actionNoveltyHint = BuildRecentCompanionActionNoveltyHint(
                behaviorPriorities);
            List<LlmMessage> messages = BuildMessages(snap, date, scene, evidence,
                scene.RelationshipNetwork);
            messages.Add(LlmMessage.System(
                "JHYL_COMPANION_MONTHLY_AGENT_LOOP：你是这名主动人物本人。不要一次性提交整月计划；"
                + "要像正常对话 Agent 一样多轮调用当前真实工具，每轮阅读权威回执后再决定下一步；主动行动才是本任务的核心。"
                + "思量按语义分成简短自然段；下一轮只处理新收到的回执、未决问题和下一步，不复述上一轮已经完成的分析、人物资料、计划或工具说明。"
                + "同一轮可并列完成互不依赖的只读查询；任何会改变状态的动作每轮只执行一项，必须先看到真实结果，下一轮才能决定后续。"
                + "首轮或新支线需要多项只读事实时应在同一轮并列查询，不要一次只查一人。若你已经决定并直接调用动作，"
                + "代码会在同一工具回执内自动补齐缺少的关系、受教者技能或秘闻候选；全部可靠且参数仍有效就同轮执行，"
                + "否则不产生副作用并一次返回完整原因。不要为了协议机械地先查一轮，也不要重复相同查询。"
                + (scene.ActorScene
                    ? "你不是当前同道，行动现场以你自己的真实所在地为准；太吾若不在你身边，只能千里传音联系，不能隔空做当面行为。"
                    : "你本月始终与太吾同行，不得离队、赴约、迁居、自行远行或追逐远方人物；异地熟人只能用千里传音类言语、关系或态度行为联系。")
                + "第一轮必须调用至少一个工具；明确失败后按原因换可行做法并继续思考，不得重复完全相同的失败动作；UNKNOWN 后系统会停止。"
                + "send_message 只在本月因果确实需要告知、请求、解释、谈判或表态时自然选择，不是完成本月行动的硬门槛，也不计入下述三项、两类实质行为。说话只写实际内容，当面还是千里传音由代码在派发前按双方实时位置决定。"
                + "本月至少完成三项得到成功回执的实质行为，并且分属至少两种行为类型。赠物、交换、写书、亲授都属于同一“物资与传承”类型：因果需要时这些动作仍可连续执行，但该类型无论做几次都只计一种，不能单靠它们凑足两类。"
                + "本月不设工具动作次数额度；三项、两类只是禁止提前收尾的最低线，达到它绝不代表最初目标已经完成。未达标时要继续补足；达标后只要承诺、冲突、追寻或其它因果仍悬着，仍须继续紧密相关的行动，直到自然落定或被真实条件明确阻断。只有第一项都无法落地、且所有硬前置确实挡住任何行动时才可调用 no_action。"
                + "所有真实工具结果都会由代码自动写入长期记忆，不需要调用 remember。"
                + "当原始动因已经自然落定或被权威回执明确阻断时，直接停止调用工具，只回复‘本轮结束’，不能返回空消息。无需生成叙事正文；"
                + "玩家只看代码汇总的真实工具回执，长期记忆也只记录这些回执。\n\n"
                + monthlySkills + "\n\n" + actionNoveltyHint
                + "\n\n工具名称、参数与用途以本轮随请求提供的真实工具定义为准，不在提示正文中重复抄写。"));

            int executionOrdinal = 0;
            int confirmedActionCount = 0;
            var confirmedActionCategories = new HashSet<string>(StringComparer.Ordinal);
            var completionState = new MonthlyAgentCompletionState(
                MinimumConfirmedActions, MinimumDistinctActionCategories);
            bool anyAttempt = false;
            bool noActionChosen = false;
            bool remembered = false;
            string finalNarrative = null;
            var attemptedKeys = new HashSet<string>(StringComparer.Ordinal);
            var queryResultCache = new Dictionary<string, string>(StringComparer.Ordinal);
            var preMinimumProgressFuse = new MonthlyPreMinimumProgressFuse(3);
            var postMinimumProgressFuse = new MonthlyPostMinimumProgressFuse(2);
            double previousCacheRatio = 0;
            for (int round = 0; round < MaxAgentRounds && current(); round++)
            {
                // Keep one stable provider-visible schema across the full loop. The execution
                // layer below rejects query drift after the bounded planning phase; removing
                // query definitions here would turn a recoverable model choice into a protocol
                // level “unauthorized tool” failure before our correction can run.
                IList<ToolDef> roundTools = actionTools;
                string toolChoice = "auto";
                AgentContextPruneReport pruneReport =
                    AgentContextPruner.ApplyMonthly(messages, previousCacheRatio);
                if (pruneReport.ReclaimedCharacters > 0)
                    Debug.Log("[JHYL_COMPANION_CONTEXT_PRUNE] npc=" + npcId
                        + " round=" + (round + 1)
                        + " reclaimed_chars=" + pruneReport.ReclaimedCharacters
                        + " compacted=" + pruneReport.CompactedResults
                        + " deduplicated=" + pruneReport.DeduplicatedResults);
                var promptBudget = PromptBudgeter.Apply(messages, roundTools,
                    LlmService.InputBudgetFor(client));
                if (promptBudget.ExceedsBudget)
                {
                    Debug.LogWarning("[JHYL_COMPANION_AGENT_INPUT_BUDGET] npc=" + npcId
                        + " before=" + promptBudget.BeforeTokens + " after=" + promptBudget.AfterTokens
                        + " budget=" + promptBudget.BudgetTokens);
                    break;
                }
                if (!MonthlyAgentRequestBudget.TryAcquire(WorldLifecycle.WorldId, taiwuId, date,
                    promptBudget.AfterTokens,
                    ActionDecisionMaxTokens > 0 ? ActionDecisionMaxTokens
                        : (client.DefaultMaxTokens > 0 ? client.DefaultMaxTokens : 32768),
                    out string budgetDiagnostic))
                {
                    Debug.LogWarning("[JHYL_MONTHLY_AGENT_COST_CIRCUIT] companion=" + npcId + " "
                        + budgetDiagnostic);
                    finalNarrative = DeterministicStory(result, null);
                    break;
                }
                float llmStarted = Time.unscaledTime;
                LlmReasoningPolicy roundReasoning =
                    MonthlyAgentReasoningPolicy.Select(messages, round);
                var task = client.SendToolRoundAsyncWithTrace(messages, roundTools, toolChoice,
                    ActionDecisionMaxTokens, 0.35, batchCancellation, ActionDecisionTimeoutSeconds,
                    "同道过月 Agent", false, roundReasoning, traceRoot.WithRound(round));
                yield return new WaitUntil(() => task.IsCompleted || !current());
                heartbeat?.Invoke();
                if (!current()) { onDone(null); yield break; }
                LlmToolResult turn = null;
                try { turn = task.Result; }
                catch (Exception e) { Debug.LogWarning("[江湖有灵] 同道过月 Agent 异常:" + e.GetType().Name); }
                Debug.Log("[JHYL_COMPANION_MONTHLY_TIMING] npc=" + result.Name + " agent_round="
                    + (round + 1) + " llm_ms=" + ElapsedMs(llmStarted) + " tools="
                    + (turn?.ToolCalls?.Count ?? 0) + " reasoning="
                    + roundReasoning.ToString().ToLowerInvariant());
                string retryBudgetDiagnostic = null;
                float retryStarted = Time.unscaledTime;
                var recoveryTask = MonthlyAgentRoundRecovery.RecoverAsync(roundReasoning, turn,
                    current,
                    () => MonthlyAgentRequestBudget.TryAcquire(WorldLifecycle.WorldId, taiwuId, date,
                        promptBudget.AfterTokens,
                        ActionDecisionMaxTokens > 0 ? ActionDecisionMaxTokens
                            : (client.DefaultMaxTokens > 0 ? client.DefaultMaxTokens : 32768),
                        out retryBudgetDiagnostic),
                    retryPolicy => client.SendToolRoundAsyncWithTrace(messages, roundTools,
                        toolChoice, ActionDecisionMaxTokens, 0.35, batchCancellation,
                        ActionDecisionTimeoutSeconds, "同道过月 Agent 轻思考失败恢复", false,
                        retryPolicy, traceRoot.WithRound(round)));
                if (!recoveryTask.IsCompleted)
                    yield return new WaitUntil(() => recoveryTask.IsCompleted || !current());
                heartbeat?.Invoke();
                if (!current()) { onDone(null); yield break; }
                MonthlyAgentRoundRecovery.Outcome recovery = null;
                try { recovery = recoveryTask.Result; }
                catch (Exception e)
                {
                    Debug.LogWarning("[江湖有灵] 同道过月恢复协调异常:" + e.GetType().Name);
                }
                if (recovery != null)
                {
                    turn = recovery.Result;
                    if (recovery.RetryAttempted)
                    {
                        Debug.LogWarning("[JHYL_COMPANION_LOW_REASONING_RECOVERY] npc="
                            + result.Name + " round=" + (round + 1));
                        Debug.Log("[JHYL_COMPANION_MONTHLY_TIMING] npc=" + result.Name
                            + " agent_round=" + (round + 1) + " recovery_llm_ms="
                            + ElapsedMs(retryStarted) + " tools=" + (turn?.ToolCalls?.Count ?? 0)
                            + " reasoning=auto");
                    }
                    if (recovery.RetryBudgetDenied)
                        Debug.LogWarning("[JHYL_MONTHLY_AGENT_COST_CIRCUIT] companion recovery "
                            + retryBudgetDiagnostic);
                    if (!string.IsNullOrWhiteSpace(recovery.RetryExceptionType))
                        Debug.LogWarning("[江湖有灵] 同道过月完整思考恢复异常:"
                            + recovery.RetryExceptionType);
                }
                if (turn == null || !turn.Ok)
                {
                    if (anyAttempt)
                    {
                        result.Outcomes.Add("失败:同道主动行事 Agent 中途请求失败，原因:"
                            + Trim(turn?.Error ?? "主模型无回执", 220));
                        break;
                    }
                    result.Outcomes.Add("失败:同道主动行事 Agent 未能开始，原因:"
                        + (turn?.Error ?? "主模型无回执"));
                    result.Summary = "主动行事 Agent 未能开始";
                    result.Detail = DeterministicStory(result, null);
                    onDone(result);
                    yield break;
                }
                previousCacheRatio = turn.PromptTokens > 0
                    ? (double)turn.CachedTokens / turn.PromptTokens : previousCacheRatio;

                if (!turn.HasToolCalls)
                {
                    // JHYL_COMPANION_CAUSAL_REVIEW_ONLY_AFTER_NO_TOOL_DRAFT:
                    // the three-action/two-category floor is inspected only when the agent tries
                    // to stop. Successful actions never create a completion quota.
                    if (!anyAttempt && !noActionChosen)
                    {
                        if (preMinimumProgressFuse.ObserveRound(false, false))
                        {
                            Debug.LogWarning("[JHYL_COMPANION_PRE_MINIMUM_FUSE] npc=" + npcId
                                + " round=" + (round + 1)
                                + " reason=three_rounds_without_action_or_new_facts");
                            finalNarrative = DeterministicStory(result, null);
                            break;
                        }
                        messages.Add(LlmMessage.Assistant(turn.Content ?? ""));
                        messages.Add(LlmMessage.System(
                            "你尚未调用任何真实工具。本月与太吾实际相处过，必须先行动；只有硬前置全部阻断时才调用 no_action。"
                            + (preMinimumProgressFuse.ConsecutiveNonProgressRounds >= 2
                                ? " 已连续两轮没有新增真实行动或权威事实；下一轮应选择可落地行动，或在确实无事可做时调用 no_action，不要重复查询或空写。"
                                : "")));
                        // 统一保持 auto 和完整思考；是否真正行动由下一轮本地回执门继续检查。
                        continue;
                    }
                    MonthlyNoToolDecision completionDecision =
                        completionState.OnNoToolDraft(noActionChosen);
                    if (completionDecision == MonthlyNoToolDecision.ContinueForMinimum)
                    {
                        if (preMinimumProgressFuse.ObserveRound(false, false))
                        {
                            Debug.LogWarning("[JHYL_COMPANION_PRE_MINIMUM_FUSE] npc=" + npcId
                                + " round=" + (round + 1) + " confirmed="
                                + confirmedActionCount
                                + " reason=three_rounds_without_action_or_new_facts");
                            finalNarrative = DeterministicStory(result, null);
                            break;
                        }
                        messages.Add(LlmMessage.Assistant(turn.Content ?? ""));
                        messages.Add(LlmMessage.System(BuildCompanionDiversityCorrection(
                            confirmedActionCount, confirmedActionCategories)
                            + (preMinimumProgressFuse.ConsecutiveNonProgressRounds >= 2
                                ? " 已连续两轮没有新增真实行动或权威事实；下一轮应直接执行可落地行为，若真实条件已阻断则停止，不要重复查询或空写。"
                                : "")));
                        continue;
                    }
                    if (completionDecision == MonthlyNoToolDecision.RequestCausalReview)
                    {
                        messages.Add(LlmMessage.Assistant(turn.Content ?? ""));
                        messages.Add(LlmMessage.System(
                            "三项、两类只是最低线，不是完成条件。现在重新对照最初动因、未决承诺、冲突与追寻："
                            + "只有存在一项具体未决因果时，才继续调用紧密相关的查询或行动；没有动作次数上限。"
                            + "若最初动因已经自然落定，或被权威回执明确阻断，就停止调用工具，只回复‘本轮结束’，不能返回空消息；无需再写叙事正文。"
                            + "禁止仅为丰富度、凑热闹或继续扩写而追加无关动作。"
                            + "不要汇报数量、门槛或这次检查。"));
                        continue;
                    }
                    // 同道过月不再生成或采纳模型正文。玩家只看到权威工具回执；
                    // 最后一轮无工具响应只承担“因果链已经可以停止”的控制信号。
                    finalNarrative = DeterministicStory(result, null);
                    LlmLog.RecordTrajectory("同道过月 Agent", traceRoot.WithRound(round),
                        "stop_signal", "authoritative_outcomes_only");
                    break;
                }

                messages.Add(LlmMessage.WithToolCalls(turn.ToolCalls, turn.Content,
                    turn.ReplayReasoningContent));
                bool roundHasSuccessfulActionReceipt = false;
                bool roundHasNewAuthoritativeFacts = false;
                // 同轮可处理多个互不依赖的只读查询；状态变更只能落地一个，且不能紧跟
                // 在同轮查询之后，因为模型尚未读到查询回执。
                bool mutationDecisionConsumedThisRound = false;
                bool queryDecisionSeenThisRound = false;
                foreach (LlmToolCall call in turn.ToolCalls)
                {
                    if (call == null) continue;
                    bool requestedReadOnlyQuery = IsCompanionQueryTool(call.Name);
                    if (mutationDecisionConsumedThisRound
                        || queryDecisionSeenThisRound && !requestedReadOnlyQuery)
                    {
                        messages.Add(LlmMessage.Tool(call.Id,
                            "未执行：同轮可并列只读查询，但状态变更必须在读取本轮查询或前一动作回执后的下一轮再决定。"));
                        continue;
                    }
                    if (requestedReadOnlyQuery) queryDecisionSeenThisRound = true;
                    else mutationDecisionConsumedThisRound = true;
                    if (!availableTools.Contains(call.Name))
                    {
                        messages.Add(LlmMessage.Tool(call?.Id,
                            "未执行：当前场景没有这个工具，请只使用已提供工具。"));
                        continue;
                    }
                    string canonicalKey = NormalizePlanToolName(call.Name) + "|"
                        + CanonicalizePlanArgs(call.Name, call.ArgumentsJson, snap);
                    if (requestedReadOnlyQuery
                        && queryResultCache.TryGetValue(canonicalKey, out string cachedQueryResult))
                    {
                        messages.Add(LlmMessage.Tool(call.Id,
                            "复用本轮最近一次相同权威查询（其后没有成功状态变更）：\n"
                            + cachedQueryResult));
                        LlmLog.RecordTrajectory("同道过月 Agent", traceRoot.WithRound(round),
                            call.Name, "query_cache_hit");
                        continue;
                    }
                    if (attemptedKeys.Contains(canonicalKey))
                    {
                        messages.Add(LlmMessage.Tool(call.Id,
                            "未执行：这一步的工具、对象和参数与本月已尝试动作完全相同。请根据回执换方案或结束。"));
                        continue;
                    }
                    if (remembered && call.Name != "no_action")
                    {
                        messages.Add(LlmMessage.Tool(call.Id,
                            "未执行：你已经用 remember 收束本月经历，不能再追加动作；请停止调用工具。"));
                        continue;
                    }
                    if (call.Name == "remember" && confirmedActionCount <= 0)
                    {
                        messages.Add(LlmMessage.Tool(call.Id,
                            "未执行：尚无已确认成功的真实动作，不能先写‘已经做成’的记忆。"));
                        continue;
                    }
                    if (call.Name == "no_action" && confirmedActionCount > 0)
                    {
                        messages.Add(LlmMessage.Tool(call.Id,
                            "未执行：本月已经有真实行为成功，no_action 不能抹掉或提前终止已开始的行动链。"
                            + BuildCompanionDiversityCorrection(confirmedActionCount,
                                confirmedActionCategories)));
                        continue;
                    }
                    if (call.Name == "remember" && !MinimumCompanionActionDiversityMet(
                        confirmedActionCount, confirmedActionCategories))
                    {
                        messages.Add(LlmMessage.Tool(call.Id,
                            "未执行：本月尚未达到至少三项、两类成功行为，不能先用 remember 收束。"
                            + BuildCompanionDiversityCorrection(confirmedActionCount,
                                confirmedActionCategories)));
                        continue;
                    }
                    var step = new CompanionPlanStep
                    {
                        Name = call.Name,
                        ArgsJson = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson,
                    };
                    bool readOnlyQuery = requestedReadOnlyQuery;
                    bool worldMutation = call.Name != "no_action" && call.Name != "remember" && !readOnlyQuery;
                    // The event/companion conflict gate must describe this action only. Using
                    // scene.AuthorizedTargetIds here would include people merely queried by an
                    // earlier round and unnecessarily serialize an unrelated later mutation.
                    // Freeze the exact resolved target into this step before consulting the
                    // gate. Execution must never get a second chance to resolve a target which
                    // was unknown when the gate made its isolation decision.
                    var currentActionTargetIds = new HashSet<int>();
                    string targetResolutionFailure = null;
                    yield return PrimeCompanionPlanTargets(new[] { step }, snap, scene, current,
                        currentActionTargetIds, reason => targetResolutionFailure = reason);
                    heartbeat?.Invoke();
                    if (!current()) { onDone(null); yield break; }
                    if (worldMutation && !string.IsNullOrWhiteSpace(targetResolutionFailure))
                    {
                        messages.Add(LlmMessage.Tool(call.Id,
                            "未执行：动作目标无法在冲突检查前可靠冻结，已拒绝派发真实副作用："
                            + targetResolutionFailure));
                        continue;
                    }
                    PopulateAuthorizedParticipants(result.AuthorizedParticipantIds, snap, scene);
                    if (!ExpandBatchProjectionAuthority(MutationJournalPath(WorldLifecycle.WorldId),
                        batchId, snap, scene))
                    {
                        messages.Add(LlmMessage.Tool(call.Id,
                            "未执行：本批人物授权无法可靠扩展，后续动作已停止。"));
                        finalNarrative = DeterministicStory(result, null);
                        break;
                    }

                    if (worldMutation)
                    {
                        bool externalGateValid = true;
                        bool externalGateOpen = mutationExecutionGate == null;
                        bool externalGateTimedOut = false;
                        float externalGateDeadline = Time.unscaledTime
                            + ExternalExecutionGateMaxWaitSeconds;
                        while (current() && !externalGateOpen)
                        {
                            externalGateValid = TryReadExternalExecutionGate(mutationExecutionGate,
                                snap.NpcId, currentActionTargetIds, out externalGateOpen);
                            if (!externalGateValid || externalGateOpen) break;
                            heartbeat?.Invoke();
                            if (Time.unscaledTime >= externalGateDeadline)
                            {
                                externalGateTimedOut = true;
                                break;
                            }
                            yield return null;
                        }
                        if (externalGateTimedOut)
                        {
                            messages.Add(LlmMessage.Tool(call.Id,
                                "未执行：等待同场江湖事件释放人物冲突门超过绝对上限，"
                                + "本动作没有派发；请改做不冲突的事情或收束本月行动。"));
                            continue;
                        }
                        if (!externalGateValid)
                        {
                            messages.Add(LlmMessage.Tool(call.Id,
                                "未执行：外部执行顺序门读取失败，拒绝派发真实副作用。"));
                            continue;
                        }
                    }

                    yield return RefreshActorPreflightState(call.Name, snap, current);
                    heartbeat?.Invoke();
                    if (!current()) { onDone(null); yield break; }
                    string toolResult = null;
                    string operationId = null;
                    yield return ExecuteCompanionTool(call.Name, step.ArgsJson, snap, scene, evidence,
                        executionArbiter, result.Outcomes, result.StoryReceipts, batchEpoch, generation,
                        conversationClearEpoch, batchId, executionOrdinal, value => toolResult = value,
                        value => operationId = value);
                    heartbeat?.Invoke();
                    bool notExecuted = ToolResultNotExecuted(toolResult);
                    if (readOnlyQuery && ToolResultSucceeded(toolResult))
                    {
                        queryResultCache[canonicalKey] = toolResult ?? "查询没有返回可靠结果";
                        roundHasNewAuthoritativeFacts = true;
                    }
                    if (!notExecuted && !readOnlyQuery) attemptedKeys.Add(canonicalKey);
                    anyAttempt = anyAttempt || call.Name != "no_action" && !readOnlyQuery && !notExecuted;
                    noActionChosen = noActionChosen || call.Name == "no_action";
                    remembered = remembered || call.Name == "remember" && ToolResultSucceeded(toolResult);
                    if (!notExecuted && !readOnlyQuery) executionOrdinal++;
                    LlmLog.RecordTrajectory("同道过月 Agent", traceRoot.WithRound(round), call.Name,
                        notExecuted ? "not_executed" : ToolResultUnknown(toolResult) ? "unknown"
                            : ToolResultSucceeded(toolResult) ? "succeeded" : "rejected", operationId,
                        notExecuted ? SanitizeTrajectoryReason(toolResult)
                            : ToolResultSucceeded(toolResult) ? null
                            : ReadMutationFailureCode(mutationJournalPath, operationId));
                    if (notExecuted)
                    {
                        messages.Add(LlmMessage.Tool(call.Id, (toolResult ?? "未执行：前置条件不满足")
                            + "\n这不是已经发生的失败，也不会写入本月故事。请先查询所需事实，改用可行对象或参数后继续。"));
                        continue;
                    }
                    if (ToolResultUnknown(toolResult))
                    {
                        result.Summary = "主动行事回执未知，已停止后续行动";
                        result.Detail = DeterministicStory(result, null);
                        FinalizeCompanionBatchProjection(taiwuId, npcId, date, result, batchId,
                            generation, conversationClearEpoch);
                        onDone(result);
                        yield break;
                    }
                    if (!current()) { onDone(null); yield break; }
                    if (!CheckpointCompanionBatchOutcomes(MutationJournalPath(WorldLifecycle.WorldId),
                        batchId, result.Outcomes, result.StoryReceipts))
                    {
                        result.Outcomes.Add("未知:完整批次结果 checkpoint 失败；已停止后续行动");
                        result.Summary = "主动行事投影 checkpoint 失败";
                        result.Detail = DeterministicStory(result, null);
                        FinalizeCompanionBatchProjection(taiwuId, npcId, date, result, batchId,
                            generation, conversationClearEpoch);
                        onDone(result);
                        yield break;
                    }
                    if (ToolResultSucceeded(toolResult) && call.Name != "no_action"
                        && call.Name != "remember" && !readOnlyQuery)
                    {
                        roundHasSuccessfulActionReceipt = true;
                        // 只使真正受本次动作影响的查询域失效。赠物、换装、传授等不会让
                        // 已查询的人际关系凭空过期；保留它们可避免后续每一步重新查人。
                        InvalidateCompanionQueryState(call.Name, scene, queryResultCache,
                            currentActionTargetIds, snap.NpcId);
                        if (call.Name != "send_message")
                        {
                            string category = CompanionActionCategory(call.Name);
                            completionState.RecordSucceededAction(category);
                            confirmedActionCount = completionState.ActionCount;
                            confirmedActionCategories = new HashSet<string>(
                                completionState.Categories, StringComparer.Ordinal);
                        }
                    }
                    if (MinimumCompanionActionDiversityMet(confirmedActionCount,
                        confirmedActionCategories))
                    {
                        // The concise authoritative outcome below asks the model to compare the
                        // original motive with all results. Its next no-tool turn is therefore an
                        // informed stop decision; another empty review request adds no evidence.
                        completionState.MarkCausalReviewInstructionDelivered();
                    }
                    // Typed receipts stay in the durable journal and UI projection. Repeating
                    // their verbose manifest in every later prompt only grows token cost.
                    string modelReceipt = toolResult ?? "未成:工具没有返回结果";
                    messages.Add(LlmMessage.Tool(call.Id, modelReceipt
                        + (ToolResultSucceeded(toolResult)
                            ? (MinimumCompanionActionDiversityMet(confirmedActionCount,
                                    confirmedActionCategories)
                                ? "\n本月只达到了三项、两类最低线，这不代表最初目标已经完成，也不形成动作上限。请对照最初动因：若已自然落定或被权威条件阻断，就停止调用工具，只回复‘本轮结束’，不能返回空消息；只有存在具体未决因果才继续紧密相关的下一步，禁止仅为丰富度追加无关动作。无需生成叙事正文。"
                                : "\n" + BuildCompanionDiversityCorrection(confirmedActionCount,
                                    confirmedActionCategories))
                            : "\n这是明确失败原因，请换可行做法；不得假称成功。")));
                    if (noActionChosen) remembered = true;
                }
                if (preMinimumProgressFuse.ObserveRound(completionState.MinimumSatisfied,
                    roundHasSuccessfulActionReceipt, roundHasNewAuthoritativeFacts))
                {
                    Debug.LogWarning("[JHYL_COMPANION_PRE_MINIMUM_FUSE] npc=" + npcId
                        + " round=" + (round + 1) + " confirmed=" + confirmedActionCount
                        + " reason=three_rounds_without_action_or_new_facts");
                    finalNarrative = DeterministicStory(result, null);
                    break;
                }
                if (postMinimumProgressFuse.ObserveToolRound(
                    completionState.MinimumSatisfied, roundHasSuccessfulActionReceipt,
                    roundHasNewAuthoritativeFacts))
                {
                    // This is a progress fuse, not an action quota.  Any additional successful
                    // action or newly hydrated prerequisite resets it. Repeated cached queries,
                    // rejected calls and no-ops do not count as progress.
                    Debug.Log("[JHYL_COMPANION_PROGRESS_FUSE] npc=" + npcId
                        + " reason=two_post_minimum_rounds_without_action_or_new_facts");
                    finalNarrative = DeterministicStory(result, null);
                    break;
                }
                if (!string.IsNullOrWhiteSpace(finalNarrative)) break;
            }

            if (!current()) { onDone(null); yield break; }
            if (anyAttempt && !MinimumCompanionActionDiversityMet(confirmedActionCount,
                confirmedActionCategories))
                Debug.LogWarning("[JHYL_COMPANION_MONTHLY_DIVERSITY_FUSE] npc=" + result.Name
                    + " confirmed=" + confirmedActionCount + " categories="
                    + string.Join(",", new List<string>(confirmedActionCategories).ToArray())
                    + " reason=request_or_round_fuse");
            if (!anyAttempt)
            {
                result.Summary = MakeSummary(result);
                result.Detail = string.IsNullOrWhiteSpace(finalNarrative)
                    ? FallbackStory(result) : finalNarrative;
                if (string.IsNullOrWhiteSpace(result.Summary)) result.Summary = "本月没有形成落地行动";
                onDone(result);
                yield break;
            }
            if (string.IsNullOrWhiteSpace(finalNarrative)) finalNarrative = DeterministicStory(result, null);
            result.Detail = finalNarrative;
            result.Summary = MakeSummary(result);
            if (!FinalizeCompanionBatchProjection(taiwuId, npcId, date, result, batchId,
                generation, conversationClearEpoch))
            { onDone(null); yield break; }
            Debug.Log("[JHYL_COMPANION_MONTHLY_TIMING] npc=" + result.Name
                + " agent_loop=true total_ms=" + ElapsedMs(oneStarted)
                + " outcomes=" + result.Outcomes.Count + " rounds_max=" + MaxAgentRounds);
            onDone(result);
        }

        private static bool IsCompanionQueryTool(string name)
        {
            string normalized = NormalizePlanToolName(name);
            return normalized.StartsWith("query_", StringComparison.Ordinal);
        }

        private static bool MinimumCompanionActionDiversityMet(int confirmedActionCount,
            ISet<string> categories)
            => confirmedActionCount >= MinimumConfirmedActions
                && categories != null && categories.Count >= MinimumDistinctActionCategories;

        private static string CompanionActionCategory(string toolName)
            => CompanionBehaviorPriorityPolicy.CategoryOf(
                NormalizePlanToolName(toolName));

        private static CompanionActionNoveltySnapshot BuildCompanionActionNoveltySnapshot(int date)
        {
            var snapshot = new CompanionActionNoveltySnapshot();
            try
            {
                MutationJournalDocument journal;
                lock (JournalIo)
                    journal = LoadMutationJournal(MutationJournalPath(WorldLifecycle.WorldId));
                if (journal?.Entries == null) return snapshot;
                foreach (MutationJournalRecord entry in journal.Entries)
                {
                    if (entry == null || entry.WorldDate >= date
                        || entry.WorldDate < Math.Max(0,
                            date - CompanionBehaviorPriorityPolicy.LookbackMonths)
                        || !string.Equals(entry.Status, "succeeded", StringComparison.Ordinal))
                        continue;
                    string tool = NormalizePlanToolName(entry.ToolName);
                    if (string.IsNullOrWhiteSpace(tool) || IsCompanionQueryTool(tool)
                        || tool == "no_action" || tool == "remember")
                        continue;
                    snapshot.Observations.Add(new CompanionBehaviorObservation
                    {
                        ToolName = tool,
                        MonthsAgo = Math.Max(1, date - entry.WorldDate),
                    });
                }
            }
            catch { }
            return snapshot;
        }

        private static List<CompanionBehaviorPriority> BuildCompanionBehaviorPriorities(
            IList<ToolDef> tools, CompanionActionNoveltySnapshot snapshot)
        {
            var available = new List<string>();
            if (tools != null)
                foreach (ToolDef definition in tools)
                {
                    string tool = NormalizePlanToolName(definition?.Name);
                    if (!string.IsNullOrWhiteSpace(tool) && !IsCompanionQueryTool(tool)
                        && tool != "no_action" && tool != "remember")
                        available.Add(tool);
                }
            return CompanionBehaviorPriorityPolicy.Rank(available,
                snapshot?.Observations);
        }

        private static List<ToolDef> OrderCompanionToolsByBehaviorPriority(
            IList<ToolDef> tools, IList<CompanionBehaviorPriority> priorities)
        {
            var fixedTools = new List<ToolDef>();
            var actions = new List<ToolDef>();
            var priorityByTool = new Dictionary<string, int>(StringComparer.Ordinal);
            if (priorities != null)
                foreach (CompanionBehaviorPriority priority in priorities)
                    if (priority != null && !string.IsNullOrWhiteSpace(priority.ToolName))
                        priorityByTool[priority.ToolName] = priority.Priority;
            if (tools != null)
                foreach (ToolDef definition in tools)
                {
                    string tool = NormalizePlanToolName(definition?.Name);
                    if (tool == "no_action" || tool == "remember" || IsCompanionQueryTool(tool))
                        fixedTools.Add(definition);
                    else
                        actions.Add(definition);
                }
            actions.Sort((left, right) =>
            {
                string leftName = NormalizePlanToolName(left?.Name);
                string rightName = NormalizePlanToolName(right?.Name);
                int leftPriority = priorityByTool.TryGetValue(leftName, out int lp) ? lp : 0;
                int rightPriority = priorityByTool.TryGetValue(rightName, out int rp) ? rp : 0;
                int byPriority = rightPriority.CompareTo(leftPriority);
                return byPriority != 0 ? byPriority
                    : string.Compare(leftName, rightName, StringComparison.Ordinal);
            });
            fixedTools.AddRange(actions);
            return fixedTools;
        }

        private static string BuildRecentCompanionActionNoveltyHint(
            IList<CompanionBehaviorPriority> priorities)
        {
            var favored = new List<string>();
            var reduced = new List<string>();
            if (priorities != null)
            {
                int count = priorities.Count;
                for (int i = 0; i < count && favored.Count < 8; i++)
                {
                    CompanionBehaviorPriority priority = priorities[i];
                    if (priority == null) continue;
                    favored.Add(CompanionNoveltyCandidateText(priority.ToolName)
                        + "(" + priority.Priority + ")");
                }
                for (int i = count - 1; i >= 0 && reduced.Count < 6; i--)
                {
                    CompanionBehaviorPriority priority = priorities[i];
                    if (priority == null || priority.RecentHeat <= 0d) continue;
                    reduced.Add(CompanionNoveltyCandidateText(priority.ToolName)
                        + "(" + priority.Priority + ")");
                }
            }
            return "【动态行为优先级】只根据最近"
                + CompanionBehaviorPriorityPolicy.LookbackMonths
                + "个月全体过月主动行事的成功行为频率计算，不改变本月人物抽选。"
                + "当前较高优先级候选："
                + (favored.Count == 0 ? "无" : string.Join("、", favored.ToArray()))
                + "；近期较常出现、已动态降权的候选："
                + (reduced.Count == 0 ? "无" : string.Join("、", reduced.ToArray()))
                + "。括号内为相对优先级（100最高、20最低）。高频行为只是降低选择概率，不会被禁用；"
                + "先服从人物动机、关系、位置和真实处境，只有多种行动同样合理且前置都成立时，"
                + "按此优先级加权取舍，避免连续数月反复送礼、传授、传话或刷关系。"
                + "杀人、下毒、擒拿的动机可以是仇怨，也可以是利益、灭口、保护、受命、野心或冲突升级等；过月执行只以行动者精纯不低于目标为玩法门槛。";
        }

        private static string CompanionNoveltyCandidateText(string tool)
        {
            switch (tool)
            {
                case "change_equipment": return "change_equipment（备战、身份变化、审美或处境改变时换装）";
                case "adjust_mood": return "adjust_mood（真实得失、交谈或关系变化造成心情波动）";
                case "adjust_fame": return "adjust_fame（公开善恶、胜负或事迹真正传入江湖）";
                case "steal": return "steal（贪图、急需、夺证、报复、嫉妒或不愿交换）";
                case "barter": return "barter（双方各有所求、缺钱、议价或以银钱参与换物）";
                case "capture": return "capture（控制、审问、救人、保护、利益、受命或立威）";
                case "poison": return "poison（暗算、削弱、报复、灭口、保护或利益冲突）";
                case "kill": return "kill（除恶、自保、夺物、嫉妒、灭口、受命、野心或冲突升级）";
                case "sect_support": return "sect_support（门派立场、承诺、报恩或公开为太吾说项）";
                case "read_book": return "read_book（闲暇阅读、随手翻书、消磨时间、求知、备战或完成旧愿）";
                case "train_skill": return "train_skill（闲暇日常练功、维持手感、备战、争胜、自强或复仇）";
                case "use_item": return "use_item（用膳、饮茶饮酒、疗伤服药、服毒或使用内力类特殊消耗品）";
                case "dissolve_relation": return "dissolve_relation（背叛、失望、决裂、避祸或告别旧关系）";
                default: return tool;
            }
        }

        private static string BuildCompanionDiversityCorrection(int confirmedActionCount,
            ISet<string> categories)
        {
            var completed = categories == null
                ? new List<string>() : new List<string>(categories);
            completed.Sort(StringComparer.Ordinal);
            string completedText = completed.Count == 0
                ? "尚无" : string.Join("、", completed.ToArray());
            return "请对照本月最初动因判断目标是否已经达成，同时检查丰富度。当前进度：成功行为 "
                + confirmedActionCount + "/" + MinimumConfirmedActions
                + "，不同类型 " + completed.Count + "/" + MinimumDistinctActionCategories
                + "（已完成：" + completedText + "）。请继续选择与最初动因、人物性格和真实前置"
                + "相符的行动并读取回执；因果需要时可以赠物后再传功或继续其他同类动作，"
                + "但同一类型只增加一次类型数，最终仍须覆盖尚未完成的类型，不能只靠赠物、交换、写书或传功凑数，也不得只传话后结束。";
        }

        private static void InvalidateCompanionQueryState(string toolName,
            CompanionSceneContext scene, Dictionary<string, string> queryResultCache,
            IReadOnlyCollection<int> affectedTargetIds, int actorId)
        {
            string tool = NormalizePlanToolName(toolName);
            bool relationshipChanged = tool == "relate" || tool == "enmity"
                || tool == "dissolve_relation" || tool == "adjust_favor"
                || tool == "set_relation" || tool == "matchmake"
                || tool == "spend_night" || tool == "kill" || tool == "capture";
            bool secretChanged = tool == "tell_secret";
            bool skillChanged = tool == "teach" || tool == "train_skill"
                || tool == "read_book";
            bool actorInventoryChanged = tool == "gift_item" || tool == "gift_silver"
                || tool == "steal" || tool == "barter" || tool == "write_book"
                || tool == "change_equipment" || tool == "use_item" || tool == "kill";
            if (!relationshipChanged && !secretChanged && !skillChanged
                && !actorInventoryChanged) return;

            if (affectedTargetIds != null)
            {
                foreach (int targetId in affectedTargetIds)
                {
                    if (targetId <= 0) continue;
                    if (relationshipChanged && scene?.RelationshipFacts != null)
                        scene.RelationshipFacts.Remove(targetId);
                    if (skillChanged && scene?.PersonSkillFacts != null)
                        scene.PersonSkillFacts.Remove(targetId);
                    if (secretChanged && scene?.DisclosableSecretsByRecipient != null)
                        scene.DisclosableSecretsByRecipient.Remove(targetId);
                }
            }
            if (queryResultCache == null || queryResultCache.Count == 0) return;
            var stale = new List<string>();
            foreach (string key in queryResultCache.Keys)
            {
                bool affectedTarget = CachedCompanionQueryTargetsAny(
                    key, scene, affectedTargetIds, actorId);
                if (relationshipChanged && affectedTarget
                    && (key.StartsWith("query_relationship|", StringComparison.Ordinal)
                        || key.StartsWith("query_person|", StringComparison.Ordinal)))
                    stale.Add(key);
                else if (skillChanged && affectedTarget
                    && key.StartsWith("query_person|", StringComparison.Ordinal))
                    stale.Add(key);
                else if (secretChanged && affectedTarget
                    && key.StartsWith("query_secret_recipient|", StringComparison.Ordinal))
                    stale.Add(key);
                else if (actorInventoryChanged
                    && CachedCompanionQueryTargetsEntity(key, scene, actorId, actorId)
                    && key.StartsWith("query_person|", StringComparison.Ordinal))
                    stale.Add(key);
            }
            foreach (string key in stale) queryResultCache.Remove(key);
        }

        private static bool CachedCompanionQueryTargetsAny(string key,
            CompanionSceneContext scene, IReadOnlyCollection<int> targetIds, int actorId)
        {
            if (targetIds == null || targetIds.Count == 0) return false;
            foreach (int targetId in targetIds)
                if (CachedCompanionQueryTargetsEntity(key, scene, targetId, actorId))
                    return true;
            return false;
        }

        private static bool CachedCompanionQueryTargetsEntity(string key,
            CompanionSceneContext scene, int targetId, int actorId)
        {
            if (string.IsNullOrWhiteSpace(key) || targetId <= 0) return false;
            int separator = key.IndexOf('|');
            if (separator < 0 || separator >= key.Length - 1) return false;
            JObject args;
            try { args = JObject.Parse(key.Substring(separator + 1)); }
            catch { return false; }
            string value = CanonicalString(args["target"] ?? args["name"]);
            if (value.Length == 0) return false;
            if (string.Equals(value, "#" + targetId, StringComparison.Ordinal)
                || string.Equals(value, targetId.ToString(), StringComparison.Ordinal))
                return true;
            if (targetId == actorId && string.Equals(value, "@self", StringComparison.Ordinal))
                return true;
            if (scene != null && targetId == scene.TaiwuId
                && string.Equals(value, "@taiwu", StringComparison.Ordinal))
                return true;
            return scene?.Names != null
                && scene.Names.TryGetValue(targetId, out string targetName)
                && !string.IsNullOrWhiteSpace(targetName)
                && string.Equals(value, targetName.Trim(), StringComparison.Ordinal);
        }

        private static bool TryReadExternalExecutionGate(
            Func<int, IReadOnlyCollection<int>, bool> gate, int actorId,
            IReadOnlyCollection<int> targetIds, out bool open)
        {
            open = gate == null;
            if (gate == null) return true;
            try { open = gate(actorId, targetIds); return true; }
            catch (Exception e)
            {
                Debug.LogWarning("[JHYL_COMPANION_EXTERNAL_GATE_ERROR] " + e.GetType().Name);
                return false;
            }
        }

        private static int ElapsedMs(float started)
            => Math.Max(0, (int)((Time.unscaledTime - started) * 1000f));

        public static bool HasOutstandingDialogueActionIntent(IReadOnlyList<JianghuYouling.Core.Prompt.TalkTurn> turns)
            => CompanionMonthlyActivationPolicy.HasOutstandingDialogueActionIntent(turns, MonthlyDialogueLimit);

        private static bool TryStartBatch(out int batchEpoch, out int blockedInflight)
        {
            lock (MutationGate)
            {
                blockedInflight = TotalInflightUnsafe();
                if (_running || blockedInflight > 0)
                {
                    batchEpoch = _runEpoch;
                    return false;
                }
                _running = true;
                _guardReleasePending = false;
                _guardReleaseReason = null;
                batchEpoch = unchecked(++_runEpoch);
                try { _runCancellation?.Dispose(); } catch { }
                _runCancellation = new CancellationTokenSource();
                _runCancellationEpoch = batchEpoch;
                return true;
            }
        }

        private static CancellationToken GetBatchCancellationToken(int batchEpoch)
        {
            lock (MutationGate)
                return _runCancellation != null && _runCancellationEpoch == batchEpoch
                    ? _runCancellation.Token : new CancellationToken(true);
        }

        private static void CancelBatchRequests(int batchEpoch)
        {
            CancellationTokenSource cancellation = null;
            lock (MutationGate)
                if (_runCancellation != null && _runCancellationEpoch == batchEpoch)
                    cancellation = _runCancellation;
            try { cancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private static void DisposeBatchRequests(int batchEpoch)
        {
            CancellationTokenSource cancellation = null;
            lock (MutationGate)
            {
                if (_runCancellation == null || _runCancellationEpoch != batchEpoch) return;
                cancellation = _runCancellation;
                _runCancellation = null;
                _runCancellationEpoch = 0;
            }
            try { cancellation.Dispose(); } catch { }
        }

        private static bool BatchEpochIsCurrent(int batchEpoch)
        {
            lock (MutationGate) return _running && batchEpoch == _runEpoch;
        }

        private static bool BatchIsCurrent(int batchEpoch, int generation)
            => BatchEpochIsCurrent(batchEpoch) && WorldLifecycle.IsSameWorld(generation)
                && !MonthlySettlement.IsAdvancingMonthForMonthlyWork();

        private static bool TryInvalidateBatch(int batchEpoch)
        {
            bool invalidated;
            lock (MutationGate)
            {
                invalidated = batchEpoch == _runEpoch;
                if (invalidated) unchecked { _runEpoch++; }
            }
            if (invalidated) CancelBatchRequests(batchEpoch);
            return invalidated;
        }

        private static MutationLease TryBeginMutationLease(int batchEpoch, Func<bool> stillCurrent,
            string toolName, string mutationKey, string operationId, string journalPath, int worldDate,
            out bool quarantined, out string refusal, MutationJournalContext context)
        {
            quarantined = false;
            refusal = null;
            if (!PredicateIsCurrent(stillCurrent)) return null;
            if (MutationJournalBlocks(journalPath, mutationKey))
            {
                quarantined = true;
                return null;
            }
            if (!UpdateMutationJournal(journalPath, mutationKey, operationId, toolName, "prepared", worldDate,
                context: context))
            {
                refusal = "本地副作用日志 checkpoint 失败";
                return null;
            }
            MutationLease lease;
            lock (MutationGate)
            {
                if (!_running || batchEpoch != _runEpoch)
                {
                    refusal = "批次已失效";
                    lease = null;
                }
                else
                {
                if (!string.IsNullOrWhiteSpace(mutationKey) && UnknownMutationQuarantine.Contains(mutationKey))
                {
                    quarantined = true;
                    lease = null;
                }
                    else
                    {
                        int count;
                        MutationInflightByEpoch.TryGetValue(batchEpoch, out count);
                        MutationInflightByEpoch[batchEpoch] = count + 1;
                        lease = new MutationLease(batchEpoch, toolName, mutationKey, operationId, journalPath);
                    }
                }
            }
            if (lease == null)
            {
                if (!quarantined) UpdateMutationJournal(journalPath, mutationKey, operationId, toolName,
                    "canceled", worldDate, "CANCELED_BEFORE_DISPATCH", null, false,
                    "批次在 RPC 派发前失效");
                return null;
            }
            if (PredicateIsCurrent(stillCurrent)) return lease;
            lease.CancelBeforeDispatch();
            return null;
        }

        private static MutationLease TryBeginToolMutationLease(int batchEpoch, Func<bool> stillCurrent,
            NpcSnapshot snap, CompanionSceneContext scene, string toolName, JObject args,
            string resolvedEntityField, int resolvedEntityId,
            List<string> outcomes, Action<string> onResult, string batchId, int stepIndex,
            long conversationClearEpoch, Action<string> onOperationPrepared)
        {
            string normalizedTool = NormalizePlanToolName(toolName);
            if (!IsCompanionKnownEndpoint(scene, snap, resolvedEntityId))
            {
                const string reason = "动作对象不是本轮已可靠识别的人物";
                onResult?.Invoke("未执行：" + reason);
                return null;
            }
            if (RequiresCompanionPhysicalEnvelope(normalizedTool, snap.NpcId, resolvedEntityId)
                && !IsCompanionPresentEndpoint(scene, snap, resolvedEntityId))
            {
                const string reason = "目标不在同一地块；千里传音不能执行当面、动手或过物行为";
                onResult?.Invoke("未执行：" + reason);
                return null;
            }
            bool selfOnly = normalizedTool == "add_feature" || normalizedTool == "flip_practice"
                || normalizedTool == "train_skill" || normalizedTool == "read_book"
                || normalizedTool == "use_item" || normalizedTool == "change_equipment";
            bool selfAllowed = selfOnly || normalizedTool == "heal"
                || normalizedTool == "adjust_mood" || normalizedTool == "adjust_fame";
            if (selfOnly && resolvedEntityId != snap.NpcId)
            {
                string reason = normalizedTool + " 只允许同道作用于自己";
                onResult?.Invoke("未执行：" + reason);
                return null;
            }
            if (!selfAllowed && resolvedEntityId == snap.NpcId)
            {
                const string reason = "双人动作的目标不能是行动者自己";
                onResult?.Invoke("未执行：" + reason);
                return null;
            }
            var keyArgs = args == null ? new JObject() : new JObject(args);
            // 名称/“太吾”/“自己”等别名在解析后统一换成实体 id，未知操作隔离不能被换个叫法绕过。
            if (!string.IsNullOrWhiteSpace(resolvedEntityField) && resolvedEntityId > 0)
                keyArgs[resolvedEntityField] = resolvedEntityId;
            string key = (WorldLifecycle.WorldId > 0 ? WorldLifecycle.WorldId.ToString() : "pending")
                + "|" + (snap == null ? 0 : snap.TaiwuId)
                + "|" + (snap == null ? 0 : snap.NpcId)
                + "|date=" + (snap == null ? 0 : snap.CurrentDate)
                + "|" + NormalizePlanToolName(toolName)
                + "|" + CanonicalizePlanArgs(toolName,
                    keyArgs.ToString(Newtonsoft.Json.Formatting.None), snap);
            uint worldId = WorldLifecycle.WorldId;
            string journalPath = MutationJournalPath(worldId);
            string envelope = BuildCompanionDispatchEnvelope(toolName, snap, scene, keyArgs,
                resolvedEntityField, resolvedEntityId);
            if (string.IsNullOrWhiteSpace(envelope))
            {
                const string reason = "无法建立确定性派发信封";
                onResult?.Invoke("未执行：" + reason);
                return null;
            }
            string envelopeDigest = DispatchEnvelopeBinding.CreateDigest(envelope);
            string operationId = DispatchEnvelopeBinding.CreateOperationId(
                "companion|" + key, envelopeDigest);
            if (!OperationId.IsValid(operationId))
            {
                const string reason = "无法绑定派发信封与幂等操作编号";
                onResult?.Invoke("未执行：" + reason);
                return null;
            }
            var context = new MutationJournalContext
            {
                WorldId = worldId,
                TaiwuId = snap == null ? 0 : snap.TaiwuId,
                NpcId = snap == null ? 0 : snap.NpcId,
                ActorName = snap == null ? null : (string.IsNullOrWhiteSpace(snap.Name) ? ("#" + snap.NpcId) : snap.Name),
                ActorLocation = snap == null ? null : snap.LocationText,
                TargetId = resolvedEntityId,
                TargetName = CompanionSceneEndpointName(scene, snap, resolvedEntityId),
                BatchId = batchId,
                StepIndex = stepIndex,
                ConversationClearEpoch = conversationClearEpoch,
                DispatchEnvelopeJson = envelope,
                DispatchEnvelopeDigest = envelopeDigest,
                ProjectionParticipantIdsJson = SerializeAuthorizedParticipantIds(snap, scene),
            };
            bool quarantined; string refusal;
            var lease = TryBeginMutationLease(batchEpoch, stillCurrent, toolName, key, operationId, journalPath,
                snap == null ? 0 : snap.CurrentDate, out quarantined, out refusal, context);
            if (lease != null)
            {
                if (!EffectHandler.PrepareOperationIdentity(context.WorldId, context.TaiwuId, operationId))
                {
                    lease.CancelBeforeDispatch();
                    const string identityFailure = "动作准备后存档或太吾身份已变化，已取消派发";
                    onResult?.Invoke("未执行：" + identityFailure);
                    return null;
                }
                var endpoints = CompanionPhysicalEndpoints(snap, resolvedEntityId, scene.ActorScene);
                // 好感变化是关系数值结算，不是需要双方保持同块的物理动作。此前所有月度
                // mutation 都无差别挂群聊物理信封，导致 gm:favor 被后端以
                // group_physical_operation_unmapped 拒绝。仅真正物理动作注册现场信封；
                // ResolvePerson 允许任意唯一具名人物；真正物理动作仍必须通过上方实时在场门禁。
                if (RequiresCompanionPhysicalEnvelope(normalizedTool, snap.NpcId, resolvedEntityId)
                    && !JianghuYouling.Rpc.OperationRpcClient.RegisterGroupPhysicalEnvelope(operationId,
                        snap.NpcId, snap.TaiwuId, endpoints, scene.ActorScene))
                {
                    EffectHandler.DiscardPreparedOperation(operationId);
                    lease.CancelBeforeDispatch();
                    const string envelopeFailure = "无法注册同道现场复核信封，已取消派发";
                    onResult?.Invoke("未执行：" + envelopeFailure);
                    return null;
                }
                if (EffectHandler.ObserveOperationOutcome(operationId, lease.CompleteOutcome))
                {
                    onOperationPrepared?.Invoke(operationId);
                    return lease;
                }
                EffectHandler.DiscardPreparedOperation(operationId);
                lease.CancelBeforeDispatch();
                const string observerFailure = "无法注册结构化回执观察器，已取消派发";
                onResult?.Invoke("未执行：" + observerFailure);
                return null;
            }
            if (quarantined)
                ReportMutationUnknown(outcomes, toolName,
                    "此前同一操作的后端结果未知，为避免重复副作用，跨重启均不再自动派发(operationId=" + operationId + ")", onResult);
            else
            {
                string reason = refusal ?? "批次已失效";
                onResult?.Invoke("未执行：" + reason);
            }
            return null;
        }

        private static string BuildCompanionDispatchEnvelope(string toolName, NpcSnapshot snap,
            CompanionSceneContext scene, JObject resolvedArgs, string resolvedEntityField, int resolvedEntityId)
        {
            if (snap == null || scene == null || snap.NpcId <= 0 || snap.TaiwuId <= 0 || resolvedEntityId <= 0
                || string.IsNullOrWhiteSpace(toolName)) return null;
            try
            {
                var envelope = resolvedArgs == null ? new JObject() : new JObject(resolvedArgs);
                envelope["_tool"] = NormalizePlanToolName(toolName);
                envelope["_actorId"] = snap.NpcId;
                envelope["_taiwuId"] = snap.TaiwuId;
                envelope["_targetId"] = resolvedEntityId;
                envelope["_targetField"] = resolvedEntityField ?? "target";
                envelope["_sceneWorldId"] = scene.WorldId.ToString();
                envelope["_sceneAreaId"] = scene.AreaId;
                envelope["_sceneBlockId"] = scene.BlockId;
                envelope["_actorScene"] = scene.ActorScene;
                envelope["_contactMode"] = scene.IsPresent(resolvedEntityId) ? "face_to_face" : "remote_voice";
                bool requiresPhysical = RequiresCompanionPhysicalEnvelope(
                    toolName, snap.NpcId, resolvedEntityId);
                envelope["_physicalGuard"] = requiresPhysical;
                envelope["_physicalEndpoints"] = requiresPhysical
                    ? string.Join(",", CompanionPhysicalEndpoints(snap, resolvedEntityId, scene.ActorScene)) : "";
                return envelope.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch { return null; }
        }

        private static List<int> CompanionPhysicalEndpoints(NpcSnapshot snap, int resolvedEntityId,
            bool actorScene = false)
        {
            var endpoints = new List<int>();
            if (snap == null) return endpoints;
            if (snap.NpcId > 0) endpoints.Add(snap.NpcId);
            if (resolvedEntityId > 0 && resolvedEntityId != snap.NpcId) endpoints.Add(resolvedEntityId);
            // A physical action should normally have two different endpoints. Keep the Taiwu
            // anchor only as a fail-closed fallback for a malformed legacy envelope.
            if (!actorScene && endpoints.Count == 1 && snap.TaiwuId > 0 && snap.TaiwuId != snap.NpcId)
                endpoints.Add(snap.TaiwuId);
            endpoints.Sort();
            return endpoints;
        }

        private static bool RequiresCompanionPhysicalEnvelope(string toolName,
            int actorId = 0, int targetId = 0)
        {
            string normalized = NormalizePlanToolName(toolName);
            switch (normalized)
            {
                // These mutations only change the acting character. They neither touch Taiwu nor
                // exchange anything, so manufacturing an actor+Taiwu envelope makes valid remote
                // monthly actors fail the backend's exact endpoint-set check.
                case "add_feature":
                case "flip_practice":
                case "train_skill":
                case "read_book":
                case "use_item":
                case "change_equipment":
                    return false;
            }
            if (normalized == "heal" && actorId > 0 && actorId == targetId) return false;

            // 言语能够成立的关系、态度、秘闻与传音行为可面向远方人物，不把它们
            // 伪装成群聊物理动作；进入游戏本体战斗仍只属于当面单聊，不在本规划器中开放。
            // 其余动作均会改变人物、物品、装备或身体状态，继续在后端做同块 race 复核。
            switch (normalized)
            {
                case "adjust_favor":
                case "adjust_mood":
                case "adjust_fame":
                case "sect_support":
                case "tell_secret":
                case "relate":
                case "enmity":
                case "dissolve_relation":
                case "send_message":
                case "set_relation":
                case "poison":
                case "capture":
                case "kill":
                    return false;
                default:
                    return true;
            }
        }

        private static void PopulateAuthorizedParticipants(ISet<int> destination, NpcSnapshot snap,
            CompanionSceneContext scene)
        {
            if (destination == null) return;
            destination.Clear();
            if (snap != null)
            {
                if (snap.NpcId > 0) destination.Add(snap.NpcId);
                if (snap.TaiwuId > 0) destination.Add(snap.TaiwuId);
            }
            if (scene?.AuthorizedTargetIds != null)
                foreach (int id in scene.AuthorizedTargetIds)
                    if (id > 0) destination.Add(id);
        }

        private static string SerializeAuthorizedParticipantIds(NpcSnapshot snap, CompanionSceneContext scene)
        {
            var ids = new SortedSet<int>();
            PopulateAuthorizedParticipants(ids, snap, scene);
            return JsonConvert.SerializeObject(ids, Formatting.None);
        }

        private static string MutationJournalPath(uint worldId)
            => Path.Combine(JianghuYoulingPaths.Events, "companion_mutation_journal_" + (worldId > 0 ? worldId.ToString() : "pending") + ".json");

        internal static bool PruneTimelineAtOrAfter(uint worldId, int taiwuId, int currentDate)
        {
            if (worldId == 0 || taiwuId <= 0 || currentDate < 0) return false;
            string path = MutationJournalPath(worldId);
            lock (JournalIo)
            {
                MutationJournalDocument doc = LoadMutationJournal(path);
                if (doc == null || doc.Entries == null) return false;
                int removed = doc.Entries.RemoveAll(entry => entry != null
                    && entry.WorldId == worldId && entry.TaiwuId == taiwuId
                    && entry.WorldDate >= currentDate);
                if (removed == 0) return true;
                doc.Revision++;
                bool saved = SaveMutationJournalDocument(path, doc);
                if (saved) Debug.Log("[JHYL_MONTHLY_TIMELINE_PRUNED] companion_journal removed="
                    + removed + " boundary=" + currentDate);
                return saved;
            }
        }

        private static bool MutationJournalBlocks(string path, string mutationKey)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(mutationKey)) return true;
            lock (JournalIo)
            {
                var doc = LoadMutationJournal(path);
                if (doc == null) return File.Exists(path) || File.Exists(path + ".tmp") || File.Exists(path + ".bak"); // 存在但无法恢复时 fail closed
                foreach (var entry in doc.Entries)
                    if (entry != null && entry.MutationKey == mutationKey
                        && (entry.Status == "prepared" || entry.Status == "pending"
                            || entry.Status == "unknown" && entry.Retryable))
                        return true;
                return false;
            }
        }

        private static bool UpdateMutationJournal(string path, string mutationKey, string operationId,
            string toolName, string status, int worldDate, string code = null, string receipt = null,
            bool? retryable = null, string message = null, MutationJournalContext context = null)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(mutationKey)) return false;
            lock (JournalIo)
            {
                try
                {
                    var doc = LoadMutationJournal(path);
                    // 任何候选文件存在却都无法解析时都必须 fail-closed。尤其 watchdog/迟到回调可能在
                    // Unity 主流程之外到达，不能让它用当前这一条记录覆盖并遗失别的未决 operation。
                    if (doc == null)
                    {
                        if (File.Exists(path) || File.Exists(path + ".tmp") || File.Exists(path + ".bak"))
                        {
                            Debug.LogWarning("[江湖有灵] 同道副作用日志已损坏，拒绝以局部回调覆盖:" + path);
                            return false;
                        }
                        doc = new MutationJournalDocument();
                    }
                    MutationJournalRecord found = null;
                    foreach (var entry in doc.Entries)
                        if (entry != null && entry.MutationKey == mutationKey) { found = entry; break; }
                    if (found == null)
                    {
                        found = new MutationJournalRecord { MutationKey = mutationKey };
                        doc.Entries.Add(found);
                    }
                    // unknown 是不可逆隔离态；迟到 callback 只证明有回执，不能在没有 receipt 查询时自动放开重发。
                    if (found.Status == "unknown" && status == "callback_received") status = "unknown";
                    string previousStatus = found.Status;
                    bool previousRetryable = found.Retryable;
                    found.OperationId = operationId;
                    found.ToolName = toolName;
                    found.Status = status;
                    bool nextRetryable = retryable ?? found.Retryable;
                    if ((!string.Equals(previousStatus, status, StringComparison.Ordinal)
                            || previousRetryable != nextRetryable)
                        && IsTerminalMutationStatus(status, nextRetryable))
                    {
                        // 先前“结果未知”投影后来拿到终态时，要生成一条终态追记；旧未知故事不能吞掉新事实。
                        // Projection 是 batch 级快照，曾被重复保存在每个 step。只清当前 entry
                        // 会让同批其他 entry 的旧 committed 文本在恢复时冒充最新事实。
                        foreach (MutationJournalRecord batchEntry in doc.Entries)
                            if (batchEntry != null && !string.IsNullOrWhiteSpace(found.BatchId)
                                && string.Equals(batchEntry.BatchId, found.BatchId, StringComparison.Ordinal))
                            {
                                batchEntry.ProjectionCommitted = false;
                                batchEntry.ProjectionText = null;
                                batchEntry.ProjectionOutcomesJson = null;
                                batchEntry.ProjectionReceiptsJson = null;
                            }
                        found.OutcomeText = null;
                        found.ProjectionAsset = null;
                    }
                    if (context != null)
                    {
                        found.WorldId = context.WorldId;
                        found.TaiwuId = context.TaiwuId;
                        found.NpcId = context.NpcId;
                        found.ActorName = context.ActorName;
                        found.ActorLocation = context.ActorLocation;
                        found.TargetId = context.TargetId;
                        found.TargetName = context.TargetName;
                        found.BatchId = context.BatchId;
                        found.StepIndex = context.StepIndex;
                        found.ConversationClearEpoch = context.ConversationClearEpoch;
                        found.DispatchEnvelopeJson = context.DispatchEnvelopeJson;
                        found.DispatchEnvelopeDigest = context.DispatchEnvelopeDigest;
                        found.ProjectionParticipantIdsJson = context.ProjectionParticipantIdsJson;
                    }
                    if (status == "prepared" || status == "pending")
                    {
                        found.Code = "DISPATCH_PENDING";
                        found.Receipt = null;
                        found.BackendReceiptStored = false;
                        found.AckOutboxCommitted = false;
                        found.Retryable = true;
                        found.Message = null;
                        found.ProjectionAsset = null;
                    }
                    if (code != null) found.Code = code;
                    if (receipt != null)
                    {
                        found.Receipt = receipt;
                        found.BackendReceiptStored = !string.IsNullOrWhiteSpace(receipt);
                        found.AckOutboxCommitted = false;
                    }
                    if (retryable.HasValue) found.Retryable = retryable.Value;
                    if (message != null) found.Message = message;
                    if (worldDate > 0) found.WorldDate = worldDate;
                    found.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                    doc.Revision++;
                    if (doc.Entries.Count > MaxMutationJournalEntries)
                    {
                        doc.Entries.Sort((a, b) => (a == null ? 0 : a.UpdatedUtcTicks).CompareTo(b == null ? 0 : b.UpdatedUtcTicks));
                        int removable = doc.Entries.Count - MaxMutationJournalEntries;
                        for (int i = 0; i < doc.Entries.Count && removable > 0;)
                        {
                            var e = doc.Entries[i];
                            if (e != null && (e.Status == "prepared" || e.Status == "pending"
                                || e.Status == "unknown" && e.Retryable || !e.ProjectionCommitted
                                || e.BackendReceiptStored && !e.AckOutboxCommitted)) { i++; continue; }
                            doc.Entries.RemoveAt(i);
                            removable--;
                        }
                    }
                    return SaveMutationJournalDocument(path, doc);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[江湖有灵] 同道副作用日志存盘失败:" + e.GetType().Name);
                    return false;
                }
            }
        }

        private static bool SaveMutationJournalDocument(string path, MutationJournalDocument doc)
        {
            try
            {
                int compacted = PruneSettledMutationRecords(doc);
                if (!MutationJournalDocumentFits(doc))
                    throw new InvalidDataException("同道副作用日志字段或集合超过安全上限");
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = path + ".tmp", bak = path + ".bak";
                string promote = path + ".promote";
                string serialized = JsonConvert.SerializeObject(doc, Formatting.Indented);
                byte[] bytes = new UTF8Encoding(false, true).GetBytes(serialized);
                if (bytes.Length > MaxMutationJournalBytes)
                    throw new InvalidDataException("同道副作用日志超过安全上限");
                WriteMutationJournalBytes(tmp, bytes);
                var staged = ReadMutationJournalCandidate(tmp);
                if (!SameMutationJournal(doc, staged))
                    throw new InvalidDataException("副作用日志 tmp 严格读回不一致");
                // Keep tmp as a same-revision recovery replica; publish main from a
                // separately flushed source, then mirror the current commit into bak.
                WriteMutationJournalBytes(promote, bytes);
                var promotion = ReadMutationJournalCandidate(promote);
                if (!SameMutationJournal(doc, promotion))
                    throw new InvalidDataException("副作用日志 promotion 严格读回不一致");
                if (File.Exists(path)) File.Replace(promote, path, bak); else File.Move(promote, path);
                var committed = ReadMutationJournalCandidate(path);
                var replica = ReadMutationJournalCandidate(tmp);
                if (!SameMutationJournal(doc, committed)
                    || !SameMutationJournal(doc, replica))
                    throw new InvalidDataException("副作用日志 main 提交读回不一致");
                // main 是提交点，tmp 是同 revision、独立 flush 的恢复副本。走到这里时
                // prepared 信封已经具备两份可读当前提交，调用方必须继续原派发；若仅因
                // 第三份 bak 刷新失败而返回 false，就会留下“本次明确未派发、重启却按
                // prepared 恢复派发”的延迟副作用。bak 仍尽力刷新；失败时下次 Load
                // 会先从权威 main 补齐 tmp+bak，补齐前保持 fail-closed。
                try
                {
                    WriteMutationJournalBytes(bak, bytes);
                    var backup = ReadMutationJournalCandidate(bak);
                    if (!SameMutationJournal(doc, backup))
                        Debug.LogWarning("[江湖有灵] 同道副作用日志 bak 读回不一致；main+tmp 已提交，本次继续原派发");
                }
                catch (Exception backupError)
                {
                    Debug.LogWarning("[江湖有灵] 同道副作用日志 bak 刷新失败；main+tmp 已提交，本次继续原派发:"
                        + backupError.GetType().Name);
                }
                if (compacted > 0)
                    Debug.Log("[JHYL_COMPANION_JOURNAL_COMPACTED] removed=" + compacted
                        + " remaining=" + doc.Entries.Count);
                return true; // JHYL_COMPANION_JOURNAL_MAIN_TMP_COMMIT_POINT
            }
            catch (Exception e)
            {
                Debug.LogWarning("[江湖有灵] 同道副作用日志原子提交失败:" + e.GetType().Name);
                return false;
            }
        }

        private static void WriteMutationJournalBytes(string path, byte[] bytes)
        {
            using (var stream = new FileStream(path, FileMode.Create,
                FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private static bool UpdateMutationProjectionEvidence(string path, string mutationKey,
            string outcomeText, string projectionAsset)
        {
            lock (JournalIo)
            {
                var doc = LoadMutationJournal(path);
                if (doc == null || doc.Entries == null) return false;
                MutationJournalRecord found = null;
                foreach (var entry in doc.Entries)
                    if (entry != null && entry.MutationKey == mutationKey) { found = entry; break; }
                if (found == null) return false;
                found.OutcomeText = outcomeText;
                found.ProjectionAsset = projectionAsset;
                found.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                doc.Revision++;
                return SaveMutationJournalDocument(path, doc);
            }
        }

        private static MutationJournalDocument LoadMutationJournal(string path)
        {
            // main 是最后一次已发布的提交，只要可读就是唯一权威。孤本高 revision tmp 只是
            // 被中断的未来写入：调用方已收到 checkpoint 失败并放弃派发，采信它会把从未
            // 跨过 prepared 屏障的信封当成已承诺事实，交给 operation_not_found 恢复派发重放。
            var main = ReadMutationJournalCandidate(path);
            if (main != null)
            {
                var currentTmp = ReadMutationJournalCandidate(path + ".tmp");
                var currentBak = ReadMutationJournalCandidate(path + ".bak");
                if (SameMutationJournal(main, currentTmp)
                    && SameMutationJournal(main, currentBak)) return main;
                // Legacy writers left tmp missing and bak one revision behind.  A valid main is
                // still authoritative, but do not begin/continue monthly mutations until the
                // exact current commit has two independently flushed recovery replicas.
                try
                {
                    string serialized = JsonConvert.SerializeObject(main, Formatting.Indented);
                    byte[] bytes = new UTF8Encoding(false, true).GetBytes(serialized);
                    if (bytes.Length > MaxMutationJournalBytes) return null;
                    if (!CurrentCommitReplicaPair.TryPublish(path + ".tmp",
                        path + ".bak", bytes, MaxMutationJournalBytes)) return null;
                    currentTmp = ReadMutationJournalCandidate(path + ".tmp");
                    currentBak = ReadMutationJournalCandidate(path + ".bak");
                    if (SameMutationJournal(main, currentTmp)
                        && SameMutationJournal(main, currentBak)) return main;
                }
                catch { }
                Debug.LogWarning("[江湖有灵] 同道副作用 journal 当前 main 有效，但无法建立同 revision 双副本；本轮 fail-closed");
                return null;
            }
            if (!File.Exists(path) && !File.Exists(path + ".tmp") && !File.Exists(path + ".bak"))
                return new MutationJournalDocument();
            // main 缺失/损坏时任何单副本都不具备权威性：必须 tmp 与 bak 两份独立副本
            // revision 与内容完全一致才可恢复，否则 fail-closed 返回 null 交由调用方拒绝。
            var tmp = ReadMutationJournalCandidate(path + ".tmp");
            var bak = ReadMutationJournalCandidate(path + ".bak");
            if (tmp == null || bak == null || tmp.Revision != bak.Revision
                || !SameMutationJournal(tmp, bak))
            {
                // fail-closed 是安全姿态，但必须指名文件给出人工恢复路径：单副本可能含
                // 从未派发的 prepared 信封，程序不得自动采信；确认游戏内该月同道动作
                // 均未发生后，移走这些残档即可恢复同道过月行事。
                Debug.LogWarning("[江湖有灵] 同道副作用 journal 无法可靠恢复(main 缺失/损坏且 tmp/bak 不构成一致双副本)。"
                    + "同道过月行事将保持停用以防重放未派发的副作用。人工恢复:先核对游戏内该月同道动作确未发生,"
                    + "再把以下残档移出目录(保留备份):" + path + "、" + path + ".tmp、" + path + ".bak");
                return null;
            }
            var best = tmp;
            // JHYL_COMPANION_JOURNAL_REPAIR_MAIN: 双副本一致的恢复结果先提交回 main，
            // 后续读写才能回到 main 权威路径；提交失败时同样 fail-closed，
            // 绝不把仅存在于副本中的状态直接交给对账或恢复派发。
            if (!SaveMutationJournalDocument(path, best))
            {
                Debug.LogWarning("[江湖有灵] 同道副作用 journal 双副本一致，但恢复提交 main 失败");
                return null;
            }
            return best;
        }

        private static MutationJournalDocument ReadMutationJournalCandidate(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                long length = new FileInfo(path).Length;
                if (length < 0 || length > MaxMutationJournalBytes) return null;
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length > MaxMutationJournalBytes) return null;
                if (bytes.Length >= 2 && (bytes[0] == 0xff && bytes[1] == 0xfe
                    || bytes[0] == 0xfe && bytes[1] == 0xff)) return null;
                string json = new UTF8Encoding(false, true).GetString(bytes);
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
                    while (reader.Read()) if (reader.TokenType != JsonToken.Comment) return null;
                }
                var serializer = JsonSerializer.Create(new JsonSerializerSettings
                {
                    MissingMemberHandling = MissingMemberHandling.Error,
                });
                var value = root.ToObject<MutationJournalDocument>(serializer);
                if (value == null || value.Version < 1 || value.Version > 8 || value.Revision < 0
                    || value.Entries == null || value.Entries.Count > MaxMutationJournalEntries) return null;
                int sourceVersion = value.Version;
                if (value.Version < 2)
                {
                    // v1 没有 Retryable；其 unknown 全部来自超时/未确认，升级时必须继续 fail-closed 对账。
                    foreach (var entry in value.Entries)
                        if (entry != null && entry.Status == "unknown") entry.Retryable = true;
                    value.Version = 2;
                }
                var keys = new HashSet<string>(StringComparer.Ordinal);
                var operationIds = new HashSet<string>(StringComparer.Ordinal);
                var batchSteps = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in value.Entries)
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.MutationKey)
                        || !OperationId.IsValid(entry.OperationId) || !operationIds.Add(entry.OperationId)
                        || !keys.Add(entry.MutationKey)
                        || !IsValidMutationJournalStatus(entry.Status)
                        || entry.RecoveryDispatchCount < 0 || entry.RecoveryDispatchCount > 1
                        || entry.ProjectionCommitted && string.IsNullOrWhiteSpace(entry.ProjectionText)
                        || !MutationJournalTextFits(entry)) return null;
                    if (!string.IsNullOrWhiteSpace(entry.DispatchEnvelopeJson))
                    {
                        try
                        {
                            var envelope = JObject.Parse(entry.DispatchEnvelopeJson);
                            if (sourceVersion >= 3 && (envelope.Value<int?>("_actorId") != entry.NpcId
                                || envelope.Value<int?>("_taiwuId") != entry.TaiwuId
                                || (envelope.Value<int?>("_targetId") ?? 0) <= 0)) return null;
                            if (sourceVersion < 6)
                            {
                                entry.TargetId = envelope.Value<int?>("_targetId") ?? 0;
                                entry.TargetName = envelope.Value<string>("_targetName");
                                if (string.IsNullOrWhiteSpace(entry.TargetName) && entry.TargetId > 0)
                                    entry.TargetName = "#" + entry.TargetId;
                            }
                            else if (envelope.Value<int?>("_targetId") != entry.TargetId) return null;
                            if (sourceVersion < 6 && string.IsNullOrWhiteSpace(entry.ProjectionParticipantIdsJson))
                            {
                                var migrated = new SortedSet<int>();
                                int actor = envelope.Value<int?>("_actorId") ?? 0;
                                int taiwu = envelope.Value<int?>("_taiwuId") ?? 0;
                                int target = envelope.Value<int?>("_targetId") ?? 0;
                                if (actor > 0) migrated.Add(actor);
                                if (taiwu > 0) migrated.Add(taiwu);
                                if (target > 0) migrated.Add(target);
                                entry.ProjectionParticipantIdsJson = JsonConvert.SerializeObject(migrated, Formatting.None);
                            }
                        }
                        catch { return null; }
                    }
                    if (!string.IsNullOrWhiteSpace(entry.DispatchEnvelopeDigest)
                        && !DispatchEnvelopeBinding.Matches("companion|" + entry.MutationKey,
                            entry.DispatchEnvelopeJson, entry.DispatchEnvelopeDigest,
                            entry.OperationId))
                        return null;
                    if (sourceVersion >= 3 && (!TryParseProjectionParticipantIds(entry.ProjectionParticipantIdsJson,
                            out HashSet<int> participantIds)
                        || !participantIds.Contains(entry.NpcId) || !participantIds.Contains(entry.TaiwuId)
                        || !TryReadCompanionEnvelopeTarget(entry.DispatchEnvelopeJson, out int envelopeTarget)
                        || envelopeTarget != entry.TargetId || !participantIds.Contains(entry.TargetId)
                        || string.IsNullOrWhiteSpace(entry.ActorName)
                        || string.IsNullOrWhiteSpace(entry.TargetName))) return null;
                    if (!ProjectionOutcomesShapeValid(entry.ProjectionOutcomesJson)) return null;
                    if (!ProjectionReceiptsShapeValid(entry.ProjectionReceiptsJson)) return null;
                    // v3 新 checkpoint 必须具备可恢复实体身份与派发信封；v1/v2 只读兼容并继续隔离，绝不猜测补发。
                    if (sourceVersion >= 3 && (entry.WorldId == 0 || entry.TaiwuId <= 0 || entry.NpcId <= 0
                        || entry.TargetId <= 0
                        || string.IsNullOrWhiteSpace(entry.BatchId) || string.IsNullOrWhiteSpace(entry.DispatchEnvelopeJson)
                        || entry.StepIndex < 0 || entry.StepIndex >= MaxJournalStepsPerBatch
                        || entry.WorldDate < 0 || entry.ConversationClearEpoch < 0 || entry.UpdatedUtcTicks < 0
                        || !batchSteps.Add(entry.BatchId + "|" + entry.StepIndex)))
                        return null;
                    if (sourceVersion >= 4 && (entry.BackendReceiptStored != !string.IsNullOrWhiteSpace(entry.Receipt)
                        || entry.AckOutboxCommitted && (!entry.BackendReceiptStored || !entry.ProjectionCommitted
                            || !IsTerminalMutationStatus(entry.Status, entry.Retryable)))) return null;
                    if (!string.IsNullOrWhiteSpace(entry.Receipt)) entry.BackendReceiptStored = true;
                }
                value.Version = 8;
                return value;
            }
            catch { return null; }
        }

        private static bool IsValidMutationJournalStatus(string status)
            => status == "prepared" || status == "pending" || status == "succeeded" || status == "failed"
                || status == "rejected" || status == "canceled" || status == "unknown"
                || status == "callback_received"; // 只读兼容早期 v1；新写入不再生成。

        private static bool MutationJournalTextFits(MutationJournalRecord entry)
        {
            if (entry == null) return false;
            bool Fits(string value, int max) => value == null || value.Length <= max;
            return Fits(entry.OperationId, 256) && Fits(entry.MutationKey, 32 * 1024)
                && Fits(entry.ToolName, 128) && Fits(entry.Status, 32) && Fits(entry.Code, 1024)
                && Fits(entry.Receipt, 256 * 1024) && Fits(entry.Message, 64 * 1024)
                && Fits(entry.ActorName, 1024) && Fits(entry.ActorLocation, 2048)
                && Fits(entry.TargetName, 1024) && Fits(entry.BatchId, 1024)
                && Fits(entry.DispatchEnvelopeJson, 256 * 1024)
                && Fits(entry.DispatchEnvelopeDigest, 256)
                && Fits(entry.OutcomeText, 64 * 1024)
                && Fits(entry.ProjectionAsset, 8192)
                && Fits(entry.ProjectionOutcomesJson, 512 * 1024)
                && Fits(entry.ProjectionReceiptsJson, 512 * 1024)
                && Fits(entry.ProjectionParticipantIdsJson, 8192)
                && Fits(entry.ProjectionText, 64 * 1024);
        }

        private static bool MutationJournalDocumentFits(MutationJournalDocument doc)
        {
            if (doc == null || doc.Version < 1 || doc.Version > 8 || doc.Revision < 0
                || doc.Entries == null || doc.Entries.Count > MaxMutationJournalEntries) return false;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var operations = new HashSet<string>(StringComparer.Ordinal);
            var batchSteps = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in doc.Entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.MutationKey) || !keys.Add(entry.MutationKey)
                    || !OperationId.IsValid(entry.OperationId) || !operations.Add(entry.OperationId)
                    || !IsValidMutationJournalStatus(entry.Status) || !MutationJournalTextFits(entry)
                    || entry.RecoveryDispatchCount < 0 || entry.RecoveryDispatchCount > 1
                    || entry.WorldId == 0 || entry.TaiwuId <= 0 || entry.NpcId <= 0
                    || entry.TargetId <= 0 || string.IsNullOrWhiteSpace(entry.ActorName)
                    || string.IsNullOrWhiteSpace(entry.TargetName)
                    || string.IsNullOrWhiteSpace(entry.BatchId) || entry.StepIndex < 0 || entry.StepIndex >= MaxJournalStepsPerBatch
                    || entry.WorldDate < 0 || entry.ConversationClearEpoch < 0 || entry.UpdatedUtcTicks < 0
                    || !batchSteps.Add(entry.BatchId + "|" + entry.StepIndex)
                    || entry.ProjectionCommitted && string.IsNullOrWhiteSpace(entry.ProjectionText)
                    || !ProjectionOutcomesShapeValid(entry.ProjectionOutcomesJson)
                    || !ProjectionReceiptsShapeValid(entry.ProjectionReceiptsJson)
                    || !TryParseProjectionParticipantIds(entry.ProjectionParticipantIdsJson,
                        out HashSet<int> participantIds)
                    || !participantIds.Contains(entry.NpcId) || !participantIds.Contains(entry.TaiwuId)
                    || !TryReadCompanionEnvelopeTarget(entry.DispatchEnvelopeJson, out int envelopeTarget)
                    || envelopeTarget != entry.TargetId || !participantIds.Contains(entry.TargetId)
                    || !string.IsNullOrWhiteSpace(entry.DispatchEnvelopeDigest)
                        && !DispatchEnvelopeBinding.Matches("companion|" + entry.MutationKey,
                            entry.DispatchEnvelopeJson, entry.DispatchEnvelopeDigest,
                            entry.OperationId)
                    || entry.BackendReceiptStored != !string.IsNullOrWhiteSpace(entry.Receipt)
                    || entry.AckOutboxCommitted && (!entry.BackendReceiptStored || !entry.ProjectionCommitted
                        || !IsTerminalMutationStatus(entry.Status, entry.Retryable))) return false;
            }
            return true;
        }

        private static bool ProjectionOutcomesShapeValid(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return true;
            try
            {
                var array = JArray.Parse(json);
                if (array.Count > MaxProjectionOutcomes) return false;
                foreach (var token in array)
                    if (token == null || token.Type != JTokenType.String || token.ToString().Length > 64 * 1024)
                        return false;
                return true;
            }
            catch { return false; }
        }

        private static bool TryParseProjectionParticipantIds(string json, out HashSet<int> ids)
        {
            ids = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                JArray array = JArray.Parse(json);
                if (array.Count < 2 || array.Count > 64) return false;
                int previous = 0;
                foreach (JToken token in array)
                {
                    if (token == null || token.Type != JTokenType.Integer) return false;
                    int id = token.Value<int>();
                    if (id <= previous || !ids.Add(id)) return false;
                    previous = id;
                }
                return true;
            }
            catch { ids.Clear(); return false; }
        }

        private static bool TryReadCompanionEnvelopeTarget(string json, out int targetId)
        {
            targetId = 0;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                JObject envelope = JObject.Parse(json);
                targetId = envelope.Value<int?>("_targetId") ?? 0;
                return targetId > 0;
            }
            catch { targetId = 0; return false; }
        }

        private static bool ProjectionReceiptsShapeValid(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return true;
            try
            {
                JArray array;
                using (var sr = new StringReader(json))
                using (var reader = new JsonTextReader(sr) { DateParseHandling = DateParseHandling.None })
                {
                    array = JArray.Load(reader, new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                        CommentHandling = CommentHandling.Ignore,
                        LineInfoHandling = LineInfoHandling.Ignore,
                    });
                    while (reader.Read()) if (reader.TokenType != JsonToken.Comment) return false;
                }
                if (array.Count > MaxProjectionOutcomes) return false;
                var operations = new HashSet<string>(StringComparer.Ordinal);
                foreach (JToken token in array)
                {
                    if (!(token is JObject item) || item.Count != 8
                        || !HasExactProperties(item, "Kind", "OperationId", "ActorId", "TargetId",
                            "ActorName", "TargetName", "Asset", "Summary")
                        || item["Kind"]?.Type != JTokenType.String
                        || item["OperationId"]?.Type != JTokenType.String
                        || item["ActorId"]?.Type != JTokenType.Integer
                        || item["TargetId"]?.Type != JTokenType.Integer
                        || item["ActorName"]?.Type != JTokenType.String
                        || item["TargetName"]?.Type != JTokenType.String
                        || item["Asset"]?.Type != JTokenType.String
                        || item["Summary"]?.Type != JTokenType.String) return false;
                    string operationId = item.Value<string>("OperationId");
                    if (!OperationId.IsValid(operationId) || !operations.Add(operationId)
                        || (item.Value<string>("Kind") ?? "").Length > 128
                        || (item.Value<string>("ActorName") ?? "").Length > 1024
                        || (item.Value<string>("TargetName") ?? "").Length > 1024
                        || (item.Value<string>("Asset") ?? "").Length > 8192
                        || string.IsNullOrWhiteSpace(item.Value<string>("Summary"))
                        || item.Value<string>("Summary").Length > 64 * 1024
                        || item.Value<int>("ActorId") <= 0 || item.Value<int>("TargetId") < 0)
                        return false;
                }
                return true;
            }
            catch { return false; }
        }

        private static bool TryCollectBatchProjectionAuthority(IList<MutationJournalRecord> entries,
            out HashSet<int> authority)
        {
            authority = new HashSet<int>();
            if (entries == null || entries.Count == 0) return false;
            uint worldId = 0;
            int taiwuId = 0, npcId = 0;
            string batchId = null;
            foreach (MutationJournalRecord entry in entries)
            {
                if (entry == null || entry.WorldId == 0 || entry.TaiwuId <= 0 || entry.NpcId <= 0
                    || entry.TargetId <= 0 || string.IsNullOrWhiteSpace(entry.ActorName)
                    || string.IsNullOrWhiteSpace(entry.TargetName)
                    || string.IsNullOrWhiteSpace(entry.BatchId)) return false;
                if (worldId == 0)
                {
                    worldId = entry.WorldId;
                    taiwuId = entry.TaiwuId;
                    npcId = entry.NpcId;
                    batchId = entry.BatchId;
                }
                else if (entry.WorldId != worldId || entry.TaiwuId != taiwuId
                    || entry.NpcId != npcId || !string.Equals(entry.BatchId, batchId, StringComparison.Ordinal))
                    return false;
                if (!TryParseProjectionParticipantIds(entry.ProjectionParticipantIdsJson,
                    out HashSet<int> entryAuthority)) return false;
                if (authority.Count == 0)
                    foreach (int id in entryAuthority) authority.Add(id);
                else if (!authority.SetEquals(entryAuthority))
                    return false;
                if (!TryReadCompanionEnvelopeTarget(entry.DispatchEnvelopeJson, out int targetId)
                    || !entryAuthority.Contains(entry.NpcId) || !entryAuthority.Contains(entry.TaiwuId)
                    || targetId != entry.TargetId || !entryAuthority.Contains(entry.TargetId)) return false;
            }
            return authority.Contains(npcId) && authority.Contains(taiwuId);
        }

        private static bool CompanionProjectionReceiptsMatchJournal(IList<MutationJournalRecord> entries,
            IList<string> outcomes, IList<StoryProjectionReceipt> receipts, ISet<int> authority)
        {
            if (entries == null || entries.Count == 0 || outcomes == null || authority == null) return false;
            var byOperation = new Dictionary<string, MutationJournalRecord>(StringComparer.Ordinal);
            foreach (MutationJournalRecord entry in entries)
                if (entry == null || !OperationId.IsValid(entry.OperationId)
                    || byOperation.ContainsKey(entry.OperationId)) return false;
                else byOperation.Add(entry.OperationId, entry);

            MutationJournalRecord first = entries[0];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (StoryProjectionReceipt receipt in receipts ?? new List<StoryProjectionReceipt>())
            {
                if (receipt == null || !OperationId.IsValid(receipt.OperationId)
                    || !seen.Add(receipt.OperationId)) return false;
                string kind = StoryProjectionValidator.KindForTool(receipt.Kind);
                if (string.IsNullOrWhiteSpace(kind)) kind = (receipt.Kind ?? string.Empty).Trim().ToLowerInvariant();
                if (string.Equals(kind, "remember", StringComparison.Ordinal)
                    && !byOperation.ContainsKey(receipt.OperationId))
                {
                    bool stableLocalId = false;
                    for (int step = 0; step < MaxJournalStepsPerBatch; step++)
                    {
                        string expected = OperationId.FromStableKey("companion-local-story|" + first.WorldId
                            + "|" + first.BatchId + "|" + step + "|remember");
                        if (string.Equals(expected, receipt.OperationId, StringComparison.Ordinal))
                        { stableLocalId = true; break; }
                    }
                    if (!stableLocalId || receipt.ActorId != first.NpcId || receipt.TargetId != first.NpcId
                        || !authority.Contains(receipt.ActorId)
                        || !string.Equals((receipt.ActorName ?? string.Empty).Trim(),
                            (first.ActorName ?? string.Empty).Trim(), StringComparison.Ordinal)
                        || !string.Equals((receipt.TargetName ?? string.Empty).Trim(),
                            (first.ActorName ?? string.Empty).Trim(), StringComparison.Ordinal)
                        || !string.IsNullOrWhiteSpace(receipt.Asset)) return false;
                    continue;
                }
                if (string.Equals(kind, "message", StringComparison.Ordinal)
                    && !byOperation.ContainsKey(receipt.OperationId))
                {
                    bool stableLocalId = false;
                    for (int step = 0; step < MaxJournalStepsPerBatch; step++)
                    {
                        string expected = OperationId.FromStableKey("companion-local-story|" + first.WorldId
                            + "|" + first.BatchId + "|" + step + "|send_message|" + receipt.TargetId);
                        if (string.Equals(expected, receipt.OperationId, StringComparison.Ordinal))
                        { stableLocalId = true; break; }
                    }
                    if (!stableLocalId || receipt.ActorId != first.NpcId || receipt.TargetId <= 0
                        || receipt.TargetId == receipt.ActorId || !authority.Contains(receipt.ActorId)
                        || !authority.Contains(receipt.TargetId)
                        || !string.Equals((receipt.ActorName ?? string.Empty).Trim(),
                            (first.ActorName ?? string.Empty).Trim(), StringComparison.Ordinal)
                        || string.IsNullOrWhiteSpace(receipt.TargetName)
                        || string.IsNullOrWhiteSpace(receipt.Asset)
                        || string.IsNullOrWhiteSpace(receipt.Summary)) return false;
                    continue;
                }

                if (!byOperation.TryGetValue(receipt.OperationId, out MutationJournalRecord entry)
                    || !string.Equals(entry.Status, "succeeded", StringComparison.Ordinal)
                    || !entry.BackendReceiptStored || string.IsNullOrWhiteSpace(entry.Receipt)
                    || !string.Equals(StoryProjectionValidator.KindForTool(entry.ToolName), kind, StringComparison.Ordinal)
                    || receipt.ActorId != entry.NpcId
                    || receipt.TargetId != entry.TargetId
                    || !string.Equals((receipt.ActorName ?? string.Empty).Trim(),
                        (entry.ActorName ?? string.Empty).Trim(), StringComparison.Ordinal)
                    || !string.Equals((receipt.TargetName ?? string.Empty).Trim(),
                        (entry.TargetName ?? string.Empty).Trim(), StringComparison.Ordinal)
                    || !string.Equals((receipt.Asset ?? string.Empty).Trim(),
                        (entry.ProjectionAsset ?? string.Empty).Trim(), StringComparison.Ordinal)
                    || !string.Equals(entry.OutcomeText, "成功:" + (receipt.Summary ?? string.Empty).Trim(), StringComparison.Ordinal))
                    return false;
            }
            return StoryProjectionValidator.TryBuildDurableProjection(outcomes, receipts, authority,
                out _, out _);
        }

        private static bool HasExactProperties(JObject value, params string[] names)
        {
            if (value == null || value.Count != names.Length) return false;
            var expected = new HashSet<string>(names, StringComparer.Ordinal);
            foreach (JProperty property in value.Properties())
                if (!expected.Remove(property.Name)) return false;
            return expected.Count == 0;
        }

        private static bool SameMutationJournal(MutationJournalDocument expected, MutationJournalDocument actual)
        {
            if (expected == null || actual == null || expected.Version != actual.Version
                || expected.Revision != actual.Revision || expected.Entries == null || actual.Entries == null
                || expected.Entries.Count != actual.Entries.Count) return false;
            // 属性顺序由同一 DTO 固定；规范化为单行 JSON 可同时核对全部状态、回执和消息字段。
            return string.Equals(JsonConvert.SerializeObject(expected, Formatting.None),
                JsonConvert.SerializeObject(actual, Formatting.None), StringComparison.Ordinal);
        }

        // JHYL_COMPANION_OPERATION_RECONCILE_BOUNDED:启动批次前先查 receipt；只有权威
        // operation_not_found 才按 durable 信封与同一 operationId 做一次恢复派发。
        private static IEnumerator ReconcileCompanionMutationJournal(int generation, int taiwuId, Action<bool> onDone)
        {
            if (!WorldLifecycle.IsSameWorld(generation)) { onDone?.Invoke(false); yield break; }
            string path = MutationJournalPath(WorldLifecycle.WorldId);
            MutationJournalDocument document;
            lock (JournalIo) document = LoadMutationJournal(path);
            if (document == null || document.Entries == null)
            {
                Debug.LogWarning("[江湖有灵] 同道副作用 journal 存在但无法恢复，拒绝启动批次");
                onDone?.Invoke(false);
                yield break;
            }
            foreach (var entry in document.Entries)
                if (entry != null && entry.WorldId != 0 && entry.WorldId != WorldLifecycle.WorldId)
                {
                    Debug.LogWarning("[江湖有灵] 同道副作用 journal 世界身份不匹配，拒绝查询或派发 op=" + entry.OperationId);
                    onDone?.Invoke(false);
                    yield break;
                }

            var unresolved = new List<MutationJournalRecord>();
            foreach (var entry in document.Entries)
                if (entry != null && entry.WorldId == WorldLifecycle.WorldId && entry.TaiwuId == taiwuId
                    && (entry.Status == "prepared" || entry.Status == "pending"
                    || entry.Status == "unknown" && entry.Retryable)) unresolved.Add(entry);
            foreach (var entry in unresolved)
            {
                if (!WorldLifecycle.IsSameWorld(generation)) { onDone?.Invoke(false); yield break; }
                if (string.IsNullOrWhiteSpace(entry.MutationKey) || !OperationId.IsValid(entry.OperationId))
                {
                    Debug.LogWarning("[江湖有灵] 同道副作用 journal 条目损坏，保持 fail-closed op=" + (entry.OperationId ?? "(空)"));
                    onDone?.Invoke(false);
                    yield break;
                }
                ToolOutcome outcome = null; bool done = false;
                EffectHandler.QueryOperation(entry.WorldId, entry.TaiwuId, entry.OperationId,
                    value => { outcome = value; done = true; });
                float deadline = Time.unscaledTime + OperationReconcileWaitSeconds;
                while (!done && Time.unscaledTime < deadline && WorldLifecycle.IsSameWorld(generation)) yield return null;
                if (!WorldLifecycle.IsSameWorld(generation)) { onDone?.Invoke(false); yield break; }
                if (!done || outcome == null)
                {
                    Debug.LogWarning("[江湖有灵] 同道副作用 receipt 查询超时，继续隔离 op=" + entry.OperationId);
                    continue;
                }
                if (IsOperationNotFound(outcome) && entry.RecoveryDispatchCount == 0
                    && entry.WorldId == WorldLifecycle.WorldId && entry.TaiwuId == taiwuId && entry.NpcId > 0
                    && CompanionEnvelopeMatchesRecord(entry))
                {
                    bool rosterReliable = false, rosterAllowed = false;
                    string rosterMessage = null;
                    yield return RevalidateCompanionRecoveryRoster(entry, generation,
                        (reliable, allowed, message) =>
                        {
                            rosterReliable = reliable;
                            rosterAllowed = allowed;
                            rosterMessage = message;
                        });
                    if (!WorldLifecycle.IsSameWorld(generation)) { onDone?.Invoke(false); yield break; }
                    if (!rosterReliable)
                    {
                        Debug.LogWarning("[江湖有灵] 同道恢复派发无法可靠取得当前队伍，继续隔离 op="
                            + entry.OperationId + " " + (rosterMessage ?? ""));
                        continue;
                    }
                    if (!rosterAllowed)
                    {
                        // operation_not_found 已证明后端没有这笔操作；当前同道资格又已失效，
                        // 因而可以安全地在本地终结，绝不能沿用旧队伍授权重放副作用。
                        if (!CancelCompanionRecoveryForRosterChange(path, entry.MutationKey,
                            rosterMessage ?? "行动者或动作端点已不再是当前同道"))
                        { onDone?.Invoke(false); yield break; }
                        lock (MutationGate) UnknownMutationQuarantine.Remove(entry.MutationKey);
                        Debug.LogWarning("[江湖有灵] 同道恢复派发因当前队伍已变化而取消 op="
                            + entry.OperationId + " " + (rosterMessage ?? ""));
                        continue;
                    }
                    if (!EffectHandler.PrepareOperationIdentity(entry.WorldId, entry.TaiwuId, entry.OperationId))
                    { onDone?.Invoke(false); yield break; }
                    if (!CheckpointCompanionRecoveryDispatch(path, entry.MutationKey))
                    { EffectHandler.DiscardPreparedOperation(entry.OperationId); onDone?.Invoke(false); yield break; }
                    ToolOutcome recoveryDispatchOutcome = null;
                    if (!EffectHandler.ObserveOperationOutcome(entry.OperationId,
                            value => recoveryDispatchOutcome = value))
                    {
                        EffectHandler.DiscardPreparedOperation(entry.OperationId);
                        onDone?.Invoke(false);
                        yield break;
                    }
                    string dispatchResult = null; bool dispatchDone = false;
                    yield return RedispatchCompanionEnvelope(entry.ToolName, entry.DispatchEnvelopeJson,
                        entry.OperationId, value => { dispatchResult = value; dispatchDone = true; });
                    bool recoveryWasDispatched = EffectHandler.WasOperationDispatched(entry.OperationId);
                    EffectHandler.ForgetOperationOutcomeObserver(entry.OperationId);
                    if (!dispatchDone)
                        Debug.LogWarning("[江湖有灵] 同道 operation_not_found 恢复派发未得到业务 callback op=" + entry.OperationId);
                    else
                        Debug.Log("[江湖有灵] 同道 operation_not_found 已执行唯一一次恢复派发 op=" + entry.OperationId
                            + " callback=" + (dispatchResult ?? "(空)"));
                    if (!recoveryWasDispatched)
                    {
                        EffectHandler.DiscardPreparedOperation(entry.OperationId);
                        if (recoveryDispatchOutcome != null && recoveryDispatchOutcome.Retryable)
                        {
                            Debug.LogWarning("[江湖有灵] 同道恢复派发在触及后端前遇到可重试本地故障，"
                                + "保留零次派发资格 op=" + entry.OperationId + " code="
                                + (recoveryDispatchOutcome.Code ?? "unknown"));
                            continue;
                        }
                        if (!CancelCompanionRecoveryBeforeDispatch(path, entry.MutationKey,
                                recoveryDispatchOutcome?.Code ?? "RECOVERY_LOCAL_PRECONDITION_REJECTED",
                                dispatchResult ?? recoveryDispatchOutcome?.Message ?? "恢复动作未通过本地前置校验"))
                        { onDone?.Invoke(false); yield break; }
                        lock (MutationGate) UnknownMutationQuarantine.Remove(entry.MutationKey);
                        Debug.LogWarning("[江湖有灵] 同道恢复派发在触及后端前被确定性拒绝，已安全取消 op="
                            + entry.OperationId + " " + (dispatchResult ?? ""));
                        continue;
                    }
                    if (!WorldLifecycle.IsSameWorld(generation)) { onDone?.Invoke(false); yield break; }
                    if (!CommitCompanionRecoveryDispatchAttempt(path, entry.MutationKey))
                    { onDone?.Invoke(false); yield break; }
                    outcome = null; done = false;
                    EffectHandler.QueryOperation(entry.WorldId, entry.TaiwuId, entry.OperationId,
                        value => { outcome = value; done = true; });
                    deadline = Time.unscaledTime + OperationReconcileWaitSeconds;
                    while (!done && Time.unscaledTime < deadline && WorldLifecycle.IsSameWorld(generation)) yield return null;
                    if (!WorldLifecycle.IsSameWorld(generation)) { onDone?.Invoke(false); yield break; }
                    if (!done || outcome == null)
                    {
                        Debug.LogWarning("[江湖有灵] 同道恢复派发后 receipt 查询超时，继续隔离 op=" + entry.OperationId);
                        continue;
                    }
                }
                if (outcome.IsSucceeded && (!string.Equals(outcome.OperationId, entry.OperationId, StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(outcome.Receipt)))
                {
                    outcome = new ToolOutcome
                    {
                        OperationId = entry.OperationId,
                        Status = "unknown",
                        Code = "AUTHORITATIVE_RECEIPT_MISSING_OR_MISMATCHED",
                        Retryable = true,
                        Message = "查询返回成功但缺少精确 operationId 的 durable backend receipt",
                    };
                }
                string status = (outcome.Status ?? "unknown").Trim().ToLowerInvariant();
                if (!UpdateMutationJournal(path, entry.MutationKey, entry.OperationId, entry.ToolName, status,
                    entry.WorldDate, outcome.Code, outcome.Receipt, outcome.Retryable, outcome.Message))
                { onDone?.Invoke(false); yield break; }

                lock (MutationGate)
                {
                    if (outcome.IsTerminal) UnknownMutationQuarantine.Remove(entry.MutationKey);
                    else UnknownMutationQuarantine.Add(entry.MutationKey);
                }
                if (outcome.IsTerminal)
                {
                    Debug.Log("[江湖有灵] 同道副作用 receipt 已恢复终态 op=" + entry.OperationId + " status=" + status + " code=" + (outcome.Code ?? ""));
                }
                else
                    Debug.LogWarning("[江湖有灵] 同道副作用 receipt 仍非终态，继续隔离 op=" + entry.OperationId + " status=" + status + " code=" + (outcome.Code ?? ""));
            }
            if (!RecoverCompanionBatchProjections(path, generation, taiwuId))
            {
                onDone?.Invoke(false);
                yield break;
            }
            AcknowledgeAllProjectedCompanionOutcomes(path);
            onDone?.Invoke(true);
        }

        private static bool IsOperationNotFound(ToolOutcome outcome)
            => outcome != null && string.Equals(outcome.Code, "operation_not_found", StringComparison.OrdinalIgnoreCase);

        private static bool CompanionEnvelopeMatchesRecord(MutationJournalRecord entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.DispatchEnvelopeJson)) return false;
            try
            {
                if (!DispatchEnvelopeBinding.Matches("companion|" + entry.MutationKey,
                        entry.DispatchEnvelopeJson, entry.DispatchEnvelopeDigest,
                        entry.OperationId))
                    return false;
                var envelope = JObject.Parse(entry.DispatchEnvelopeJson);
                int actorId = envelope.Value<int?>("_actorId") ?? 0;
                int taiwuId = envelope.Value<int?>("_taiwuId") ?? 0;
                int targetId = envelope.Value<int?>("_targetId") ?? 0;
                bool physicalGuard = envelope.Value<bool?>("_physicalGuard") ?? false;
                uint sceneWorldId;
                string endpointCsv = envelope.Value<string>("_physicalEndpoints");
                var expectedEndpoints = ParsePhysicalEndpoints(endpointCsv);
                var computedEndpoints = new SortedSet<int>();
                if (physicalGuard)
                {
                    if (actorId > 0) computedEndpoints.Add(actorId);
                    if (targetId > 0) computedEndpoints.Add(targetId);
                    if (computedEndpoints.Count == 1 && taiwuId > 0) computedEndpoints.Add(taiwuId);
                }
                return actorId == entry.NpcId
                    && taiwuId == entry.TaiwuId
                    && targetId > 0
                    && uint.TryParse(envelope.Value<string>("_sceneWorldId"), out sceneWorldId)
                    && sceneWorldId == entry.WorldId
                    && envelope.Value<short?>("_sceneAreaId").HasValue
                    && envelope.Value<short?>("_sceneBlockId").HasValue
                    && physicalGuard == RequiresCompanionPhysicalEnvelope(
                        entry.ToolName, actorId, targetId)
                    && expectedEndpoints != null && expectedEndpoints.SetEquals(computedEndpoints)
                    && string.Equals(NormalizePlanToolName(envelope.Value<string>("_tool")),
                        NormalizePlanToolName(entry.ToolName), StringComparison.Ordinal);
            }
            catch { return false; }
        }

        /// <summary>
        /// operation_not_found 只证明原 RPC 未到达后端，并不延续旧月的同道授权。
        /// 恢复派发前重新读取游戏 GetGroupSet：行动者必须仍是当前同道。物理端点还须
        /// 位于当前同行同道或太吾同块人物集合；远程言语动作只校验目标实体仍存在。
        /// 读取超时或返回不含太吾的异常空集时保持 UNKNOWN 隔离。
        /// </summary>
        private static IEnumerator RevalidateCompanionRecoveryRoster(MutationJournalRecord entry, int generation,
            Action<bool, bool, string> onDone)
        {
            if (entry == null || entry.WorldId == 0 || entry.TaiwuId <= 0 || entry.NpcId <= 0
                || entry.WorldId != WorldLifecycle.WorldId || !WorldLifecycle.IsSameWorld(generation))
            {
                onDone?.Invoke(false, false, "恢复记录与当前世界身份不一致");
                yield break;
            }

            SortedSet<int> endpoints = null; bool requiresPhysical = false, actorScene = false;
            int actorId = 0, targetId = 0;
            int sceneArea = int.MinValue, sceneBlock = int.MinValue;
            try
            {
                var envelope = JObject.Parse(entry.DispatchEnvelopeJson ?? "");
                endpoints = ParsePhysicalEndpoints(envelope.Value<string>("_physicalEndpoints"));
                requiresPhysical = envelope.Value<bool?>("_physicalGuard") ?? false;
                actorScene = envelope.Value<bool?>("_actorScene") ?? false;
                actorId = envelope.Value<int?>("_actorId") ?? entry.NpcId;
                targetId = envelope.Value<int?>("_targetId") ?? 0;
                sceneArea = envelope.Value<int?>("_sceneAreaId") ?? int.MinValue;
                sceneBlock = envelope.Value<int?>("_sceneBlockId") ?? int.MinValue;
            }
            catch { }
            if (endpoints == null || requiresPhysical != RequiresCompanionPhysicalEnvelope(
                    entry.ToolName, actorId, targetId)
                || requiresPhysical && (endpoints.Count < (actorScene ? 1 : 2)
                    || !endpoints.Contains(entry.NpcId))
                || !requiresPhysical && endpoints.Count != 0)
            {
                onDone?.Invoke(false, false, "恢复记录的动作距离门禁无效");
                yield break;
            }

            // JHYL_MANUAL_MONTHLY_RECOVERY: manually selected ordinary NPCs act from their
            // own authoritative block. They are not companions and must never be rejected
            // merely because GetGroupSet does not contain them.
            if (actorScene)
            {
                bool eligibilityDone = false, eligibilityReliable = false;
                List<int> eligibleActors = null;
                EffectHandler.QueryMonthlyAgentEligibility(new List<int> { entry.NpcId },
                    (ok, ids, rejected) =>
                    {
                        if (!WorldLifecycle.IsSameWorld(generation)
                            || WorldLifecycle.WorldId != entry.WorldId) return;
                        eligibilityReliable = ok;
                        eligibleActors = ids;
                        eligibilityDone = true;
                    });
                float eligibilityDeadline = Time.unscaledTime + OperationReconcileWaitSeconds;
                while (!eligibilityDone && Time.unscaledTime < eligibilityDeadline
                    && WorldLifecycle.IsSameWorld(generation)) yield return null;
                if (!eligibilityDone || !eligibilityReliable
                    || !WorldLifecycle.IsSameWorld(generation)
                    || WorldLifecycle.WorldId != entry.WorldId)
                {
                    onDone?.Invoke(false, false, "手动过月行动者资格查询失败、超时或世界已变化");
                    yield break;
                }
                if (eligibleActors == null || !eligibleActors.Contains(entry.NpcId))
                {
                    onDone?.Invoke(true, false, "手动过月行动者已失效、变为婴儿或不再是可互动人物");
                    yield break;
                }
                if (!requiresPhysical)
                {
                    yield return RevalidateMonthlyRecoveryTarget(entry.TargetId, generation, onDone);
                    yield break;
                }

                bool actorBlockDone = false, actorBlockOk = false;
                short actorArea = -1, actorBlock = -1;
                List<int> actorBlockIds = null;
                EffectHandler.QueryActorBlockChars(entry.NpcId, (ok, area, block, ids) =>
                {
                    actorBlockOk = ok;
                    actorArea = area;
                    actorBlock = block;
                    actorBlockIds = ids;
                    actorBlockDone = true;
                });
                float actorBlockDeadline = Time.unscaledTime + OperationReconcileWaitSeconds;
                while (!actorBlockDone && Time.unscaledTime < actorBlockDeadline
                    && WorldLifecycle.IsSameWorld(generation)) yield return null;
                if (!actorBlockDone || !actorBlockOk)
                {
                    onDone?.Invoke(false, false, "手动过月行动者当前地块查询失败或超时");
                    yield break;
                }
                if (actorArea != sceneArea || actorBlock != sceneBlock)
                {
                    onDone?.Invoke(true, false, "手动过月行动者已离开动作原现场");
                    yield break;
                }
                var actorPresent = new HashSet<int>();
                if (actorBlockIds != null)
                    foreach (int id in actorBlockIds) if (id > 0) actorPresent.Add(id);
                foreach (int endpointId in endpoints)
                {
                    if (actorPresent.Contains(endpointId)) continue;
                    onDone?.Invoke(true, false,
                        "物理动作端点#" + endpointId + "已不在行动者当前同块现场");
                    yield break;
                }
                onDone?.Invoke(true, true, null);
                yield break;
            }

            bool fetched = false, ageAuthorityReliable = false;
            List<int> currentGroup = null;
            EffectHandler.QueryNonBabyCompanionGroup(entry.TaiwuId, (ok, ids) =>
            {
                if (!WorldLifecycle.IsSameWorld(generation) || WorldLifecycle.WorldId != entry.WorldId) return;
                ageAuthorityReliable = ok;
                currentGroup = ids;
                fetched = true;
            });
            float deadline = Time.unscaledTime + OperationReconcileWaitSeconds;
            while (!fetched && Time.unscaledTime < deadline && WorldLifecycle.IsSameWorld(generation))
                yield return null;
            if (!fetched || !ageAuthorityReliable || !WorldLifecycle.IsSameWorld(generation)
                || WorldLifecycle.WorldId != entry.WorldId)
            {
                onDone?.Invoke(false, false, "当前非婴儿同道名册查询失败、超时或世界已变化");
                yield break;
            }

            var group = new HashSet<int>();
            if (currentGroup != null)
                foreach (int id in currentGroup) if (id > 0) group.Add(id);
            // GetGroupSet 的正常结果包含太吾本人；缺失表示反序列化/领域查询失败，
            // 不能把异常空集误判为所有同道都已离队。
            if (!group.Contains(entry.TaiwuId))
            {
                onDone?.Invoke(false, false, "当前同道名册不含太吾，结果不可作为恢复授权");
                yield break;
            }
            if (!group.Contains(entry.NpcId))
            {
                onDone?.Invoke(true, false, "行动者已不在当前同道队伍");
                yield break;
            }
            if (!requiresPhysical)
            {
                yield return RevalidateMonthlyRecoveryTarget(entry.TargetId, generation, onDone);
                yield break;
            }

            List<int> presentIds = null;
            bool presenceDone = false, presenceReliable = false;
            EffectHandler.QueryTaiwuScenePresence(entry.TaiwuId, endpoints,
                (ok, ids) =>
                {
                    presenceReliable = ok;
                    presentIds = ids;
                    presenceDone = true;
                });
            float presenceDeadline = Time.unscaledTime + OperationReconcileWaitSeconds;
            while (!presenceDone && Time.unscaledTime < presenceDeadline
                && WorldLifecycle.IsSameWorld(generation)) yield return null;
            if (!presenceDone || !presenceReliable)
            {
                onDone?.Invoke(false, false, "当前同行、同块或在押现场查询失败或超时");
                yield break;
            }
            var present = new HashSet<int>();
            if (presentIds != null)
                foreach (int id in presentIds) if (id > 0) present.Add(id);
            foreach (int endpointId in endpoints)
            {
                if (!present.Contains(endpointId))
                {
                    onDone?.Invoke(true, false,
                        "物理动作端点#" + endpointId + "已不在当前同行、同块或在押现场");
                    yield break;
                }
            }
            onDone?.Invoke(true, true, null);
        }

        private static IEnumerator RevalidateMonthlyRecoveryTarget(int targetId, int generation,
            Action<bool, bool, string> onDone)
        {
            bool eligibilityDone = false, eligibilityReliable = false;
            List<int> eligibleTargets = null;
            EffectHandler.QueryMonthlyAgentEligibility(new List<int> { targetId },
                (ok, ids, rejected) =>
                {
                    if (!WorldLifecycle.IsSameWorld(generation)) return;
                    eligibilityReliable = ok;
                    eligibleTargets = ids;
                    eligibilityDone = true;
                });
            float deadline = Time.unscaledTime + OperationReconcileWaitSeconds;
            while (!eligibilityDone && Time.unscaledTime < deadline
                && WorldLifecycle.IsSameWorld(generation)) yield return null;
            if (!eligibilityDone || !eligibilityReliable || !WorldLifecycle.IsSameWorld(generation))
            {
                onDone?.Invoke(false, false, "远程动作目标资格查询失败、超时或世界已变化");
                yield break;
            }
            if (eligibleTargets == null || !eligibleTargets.Contains(targetId))
            {
                onDone?.Invoke(true, false, "远程动作目标已失效、变为婴儿或不再是可互动人物");
                yield break;
            }
            onDone?.Invoke(true, true, null);
        }

        private static bool CancelCompanionRecoveryForRosterChange(string path, string mutationKey, string message)
        {
            lock (JournalIo)
            {
                var doc = LoadMutationJournal(path);
                if (doc == null || doc.Entries == null) return false;
                MutationJournalRecord found = null;
                foreach (var entry in doc.Entries)
                    if (entry != null && entry.MutationKey == mutationKey) { found = entry; break; }
                if (found == null || found.RecoveryDispatchCount != 0
                    || !(found.Status == "prepared" || found.Status == "pending"
                        || found.Status == "unknown" && found.Retryable)) return false;

                found.Status = "canceled";
                found.Code = "RECOVERY_GROUP_MEMBERSHIP_CHANGED";
                found.Retryable = false;
                found.Message = message;
                // QueryOperation 已权威返回 operation_not_found，因此任何旧 UNKNOWN
                // receipt 都不再是可 ACK 的后端终态，必须与本地取消一起原子清除。
                found.Receipt = null;
                found.BackendReceiptStored = false;
                found.AckOutboxCommitted = false;
                found.OutcomeText = null;
                found.ProjectionOutcomesJson = null;
                found.ProjectionText = null;
                found.ProjectionCommitted = false;
                found.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                doc.Revision++;
                return SaveMutationJournalDocument(path, doc);
            }
        }

        private static bool CancelCompanionRecoveryBeforeDispatch(string path, string mutationKey,
            string code, string message)
        {
            lock (JournalIo)
            {
                var doc = LoadMutationJournal(path);
                if (doc == null || doc.Entries == null) return false;
                MutationJournalRecord found = null;
                foreach (var entry in doc.Entries)
                    if (entry != null && entry.MutationKey == mutationKey) { found = entry; break; }
                if (found == null || found.RecoveryDispatchCount != 0) return false;
                found.Status = "canceled";
                found.Code = string.IsNullOrWhiteSpace(code)
                    ? "RECOVERY_LOCAL_PRECONDITION_REJECTED" : code;
                found.Retryable = false;
                found.Message = message;
                found.Receipt = null;
                found.BackendReceiptStored = false;
                found.AckOutboxCommitted = false;
                found.OutcomeText = null;
                found.ProjectionOutcomesJson = null;
                found.ProjectionText = null;
                found.ProjectionCommitted = false;
                found.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                doc.Revision++;
                return SaveMutationJournalDocument(path, doc);
            }
        }

        private static bool CheckpointCompanionRecoveryDispatch(string path, string mutationKey)
        {
            lock (JournalIo)
            {
                var doc = LoadMutationJournal(path);
                if (doc == null || doc.Entries == null) return false;
                MutationJournalRecord found = null;
                foreach (var entry in doc.Entries)
                    if (entry != null && entry.MutationKey == mutationKey) { found = entry; break; }
                if (found == null || found.RecoveryDispatchCount != 0
                    || string.IsNullOrWhiteSpace(found.DispatchEnvelopeJson)) return false;
                found.Status = "prepared";
                found.Code = "RECOVERY_DISPATCH_CLAIMED";
                found.Retryable = true;
                found.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                doc.Revision++;
                return SaveMutationJournalDocument(path, doc);
            }
        }

        private static bool CommitCompanionRecoveryDispatchAttempt(string path, string mutationKey)
        {
            lock (JournalIo)
            {
                var doc = LoadMutationJournal(path);
                if (doc == null || doc.Entries == null) return false;
                MutationJournalRecord found = null;
                foreach (var entry in doc.Entries)
                    if (entry != null && entry.MutationKey == mutationKey) { found = entry; break; }
                if (found == null || found.RecoveryDispatchCount != 0) return false;
                found.RecoveryDispatchCount = 1;
                found.Code = "RECOVERY_DISPATCH_ATTEMPTED";
                found.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                doc.Revision++;
                return SaveMutationJournalDocument(path, doc);
            }
        }

        private static IEnumerator RedispatchCompanionEnvelope(string toolName, string envelopeJson,
            string operationId, Action<string> onDone)
        {
            JObject a;
            try { a = JObject.Parse(envelopeJson ?? ""); }
            catch { onDone?.Invoke("非法派发信封"); yield break; }
            int Int(string key, int fallback = 0)
            {
                JToken token = a[key];
                if (token == null) return fallback;
                try { return token.Value<int>(); }
                catch { int value; return int.TryParse(token.ToString(), out value) ? value : fallback; }
            }
            string Str(string key) => a[key] == null ? null : a[key].ToString();
            bool Bool(string key, bool fallback)
            {
                JToken token = a[key];
                if (token == null) return fallback;
                try { return token.Value<bool>(); }
                catch { bool value; return bool.TryParse(token.ToString(), out value) ? value : fallback; }
            }
            int actorId = Int("_actorId"), taiwuId = Int("_taiwuId"), targetId = Int("_targetId");
            if (actorId <= 0 || taiwuId <= 0 || targetId <= 0 || !OperationId.IsValid(operationId))
            { onDone?.Invoke("非法派发实体身份"); yield break; }
            uint sceneWorldId;
            short currentArea, currentBlock;
            string sceneWorldText = Str("_sceneWorldId");
            int sceneArea = Int("_sceneAreaId", int.MinValue), sceneBlock = Int("_sceneBlockId", int.MinValue);
            bool requiresPhysicalEnvelope = RequiresCompanionPhysicalEnvelope(
                toolName, actorId, targetId);
            bool actorScene = Bool("_actorScene", false);
            var physicalEndpoints = requiresPhysicalEnvelope
                ? ParsePhysicalEndpoints(Str("_physicalEndpoints")) : null;
            if (!uint.TryParse(sceneWorldText, out sceneWorldId) || sceneWorldId == 0
                || sceneWorldId != WorldLifecycle.WorldId
                || sceneArea < short.MinValue || sceneArea > short.MaxValue
                || sceneBlock < short.MinValue || sceneBlock > short.MaxValue
                || requiresPhysicalEnvelope && (physicalEndpoints == null || physicalEndpoints.Count < 1))
            { onDone?.Invoke("恢复派发现场信封无效"); yield break; }
            if (requiresPhysicalEnvelope)
            {
                bool sceneCurrent = false;
                if (actorScene)
                {
                    bool actorLocationDone = false, actorLocationOk = false; short actorArea = -1, actorBlock = -1;
                    EffectHandler.QueryActorBlockChars(actorId, (ok, area, block, ids) =>
                    { actorLocationOk = ok; actorArea = area; actorBlock = block; actorLocationDone = true; });
                    float actorDeadline = Time.unscaledTime + EffectHandler.ReadOnlyQueryWaitSeconds;
                    while (!actorLocationDone && Time.unscaledTime < actorDeadline) yield return null;
                    sceneCurrent = actorLocationDone && actorLocationOk
                        && actorArea == (short)sceneArea && actorBlock == (short)sceneBlock;
                }
                else sceneCurrent = NpcSnapshotReader.TryGetCurrentWorldArea(out currentArea, out currentBlock)
                    && currentArea == (short)sceneArea && currentBlock == (short)sceneBlock;
                if (!sceneCurrent)
                { onDone?.Invoke("恢复派发已拒绝:行动者已离开动作原现场"); yield break; }
            }
            if (requiresPhysicalEnvelope
                && !JianghuYouling.Rpc.OperationRpcClient.RegisterGroupPhysicalEnvelope(operationId,
                    actorId, taiwuId, physicalEndpoints, actorScene))
            { onDone?.Invoke("恢复派发无法注册同道现场信封"); yield break; }
            bool finished = false; string result = null;
            Action<bool, string> finish = (ok, message) =>
            {
                result = (ok ? "OK:" : "未成:") + (message ?? (ok ? "已确认" : "引擎拒绝"));
                finished = true;
            };
            try
            {
                switch (NormalizePlanToolName(toolName))
                {
                    case "gift_item":
                        EffectHandler.ApplyGiveItemByName(actorId, taiwuId, Str("item"), Math.Max(1, Int("amount", 1)),
                            (n, m) => finish(n > 0, m), targetId == taiwuId ? 0 : targetId, operationId);
                        break;
                    case "gift_silver":
                        EffectHandler.ApplyGiveSilver(actorId, taiwuId, Math.Max(1, Int("amount", 1)),
                            (n, m) => finish(n > 0, m), targetId == taiwuId ? 0 : targetId, operationId);
                        break;
                    case "steal":
                        EffectHandler.ApplySteal(actorId, targetId, Str("item"), Math.Max(1, Int("amount", 1)),
                            (ok, n, real, m, detected, chance) => finish(ok, m), operationId);
                        break;
                    case "poison":
                        EffectHandler.ApplyMonthlyPoison(actorId, targetId, Str("poison_type"), finish, operationId);
                        break;
                    case "relate":
                    {
                        string kind = Str("kind").Trim().ToLowerInvariant();
                        if (!IsRelationKind(kind))
                        { onDone?.Invoke("恢复派发关系类型无效"); yield break; }
                        EffectHandler.ApplyRelateNpc(actorId, targetId, kind, finish, operationId);
                        break;
                    }
                    case "enmity":
                        EffectHandler.ApplyEnmity(actorId, targetId, Bool("make", true), finish, operationId);
                        break;
                    case "kill":
                        EffectHandler.ApplyMonthlyKill(actorId, targetId, finish, operationId);
                        break;
                    case "capture":
                        EffectHandler.ApplyMonthlyCapture(actorId, targetId, finish, operationId);
                        break;
                    case "add_feature":
                        EffectHandler.ApplyAddFeature(targetId, Str("feature"), finish, operationId);
                        break;
                    case "flip_practice":
                        EffectHandler.ApplyFlipPractice(targetId, Str("skill"),
                            (ok, name, before, after, count, message) => finish(ok, message), operationId);
                        break;
                    case "heal":
                        EffectHandler.ApplyHeal(actorId, targetId, finish, operationId);
                        break;
                    case "detox":
                        EffectHandler.ApplyDetox(actorId, targetId, finish, operationId);
                        break;
                    case "regulate_breath":
                        EffectHandler.ApplyRegulateBreath(actorId, targetId, finish, operationId);
                        break;
                    case "dissolve_relation":
                    {
                        string relation = Str("relation").Trim().ToLowerInvariant();
                        if (!IsDissolvableRelation(relation))
                        { onDone?.Invoke("恢复派发解除关系类型无效"); yield break; }
                        EffectHandler.ApplyDissolveRelation(actorId, taiwuId, relation, targetId,
                            finish, operationId);
                        break;
                    }
                    case "barter":
                        EffectHandler.ApplyBarter(actorId, targetId, Str("give_item"), Math.Max(1, Int("give_amount", 1)),
                            Str("receive_item"), Math.Max(1, Int("receive_amount", 1)),
                            (ok, ga, ra, gn, rn, message) => finish(ok, message), operationId);
                        break;
                    case "write_book":
                        if (!IsSkillKind(Str("type")))
                        { onDone?.Invoke("恢复派发写书类型无效"); yield break; }
                        EffectHandler.ApplyWriteBook(actorId, taiwuId, Str("type"), (short)Int("template_id", -1),
                            (ok, book, lost) => finish(ok, book), targetId == taiwuId ? 0 : targetId, operationId);
                        break;
                    case "teach":
                        if (!IsSkillKind(Str("type")))
                        { onDone?.Invoke("恢复派发亲授类型无效"); yield break; }
                        if (Str("type") == "life")
                            EffectHandler.ApplyTeachLifeSkill(actorId, taiwuId, (short)Int("template_id", -1),
                                finish, targetId, operationId);
                        else
                            EffectHandler.ApplyTeachSkillId(actorId, taiwuId, (short)Int("template_id", -1),
                                finish, targetId, operationId);
                        break;
                    case "tell_secret":
                        EffectHandler.ApplyDiscloseSecret((SecretInformationId)Int("secret_id", -1),
                            actorId, targetId, finish, operationId);
                        break;
                    case "use_item":
                    {
                        string item = Str("item").Trim();
                        if (item.Length == 0)
                        { onDone?.Invoke("恢复派发使用物品参数无效"); yield break; }
                        EffectHandler.ApplyNpcUseItem(actorId, item,
                            (ok, name, kind, amount, before, after, message) => finish(ok, message),
                            operationId);
                        break;
                    }
                    case "change_equipment":
                    {
                        string action = Str("action").Trim().ToLowerInvariant();
                        string part = Str("part").Trim().ToLowerInvariant();
                        string item = Str("item").Trim();
                        if (action != "on" && action != "off"
                            || action == "off" && !IsEquipmentPart(part)
                            || action == "on" && item.Length == 0)
                        {
                            onDone?.Invoke("恢复派发换装参数无效");
                            yield break;
                        }
                        if (action == "off")
                            EffectHandler.ApplyEquipTakeOff(actorId, part, finish, operationId);
                        else EffectHandler.ApplyEquipByName(actorId, item, finish, operationId);
                        break;
                    }
                    case "sect_support":
                        EffectHandler.ApplySectSupport(actorId, finish, operationId);
                        break;
                    case "adjust_favor":
                        if (targetId == taiwuId)
                            EffectHandler.ApplyFavor(actorId, taiwuId, Int("delta"), finish, operationId);
                        else EffectHandler.ApplyThirdPartyFavor(actorId, targetId, Int("delta"), finish, operationId);
                        break;
                    case "adjust_mood":
                        EffectHandler.ApplyCharacterHappiness(targetId, Int("delta"), finish, operationId);
                        break;
                    case "adjust_fame":
                        EffectHandler.ApplyCharacterFame(targetId, Int("delta"), finish, operationId);
                        break;
                    case "spend_night":
                        EffectHandler.ApplySpendNightBetween(actorId, targetId, taiwuId, finish, operationId);
                        break;
                    case "matchmake":
                        EffectHandler.ApplyMatchmake(actorId, targetId, finish, operationId);
                        break;
                    case "set_relation":
                    {
                        string action = Str("action").Trim().ToLowerInvariant();
                        switch (action)
                        {
                            case "befriend": EffectHandler.ApplyRelation(actorId, taiwuId, "best_friend", finish, operationId); break;
                            case "swear_sibling": EffectHandler.ApplyRelation(actorId, taiwuId, "sworn_sibling", finish, operationId); break;
                            case "apprentice": EffectHandler.ApplyRelation(actorId, taiwuId, "apprentice", finish, operationId); break;
                            case "take_disciple": EffectHandler.ApplyRelation(actorId, taiwuId, "take_disciple", finish, operationId); break;
                            case "lover": case "spouse": EffectHandler.ApplyRelation(actorId, taiwuId, action, finish, operationId); break;
                            case "recognize": EffectHandler.ApplyRecognizeTaiwu(actorId, finish, operationId); break;
                            case "follow": EffectHandler.ApplyFollow(actorId, true, finish, operationId); break;
                            default: onDone?.Invoke("恢复派发关系动作无效"); yield break;
                        }
                        break;
                    }
                    default:
                        onDone?.Invoke("不支持的恢复工具:" + (toolName ?? "unknown"));
                        yield break;
                }
            }
            catch (Exception e)
            {
                onDone?.Invoke("恢复派发异常:" + e.GetType().Name);
                yield break;
            }
            float deadline = Time.unscaledTime + MutationCallbackTimeoutSeconds;
            while (!finished && Time.unscaledTime < deadline) yield return null;
            onDone?.Invoke(finished ? result : "UNKNOWN:恢复派发 callback 超时");
        }

        private static SortedSet<int> ParsePhysicalEndpoints(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return null;
            var endpoints = new SortedSet<int>();
            foreach (string raw in csv.Split(','))
            {
                int id;
                if (!int.TryParse((raw ?? string.Empty).Trim(), out id) || id <= 0 || !endpoints.Add(id))
                    return null;
            }
            return endpoints;
        }

        private static bool RecoverCompanionBatchProjections(string path, int generation, int taiwuId)
        {
            MutationJournalDocument snapshot;
            lock (JournalIo) snapshot = LoadMutationJournal(path);
            if (snapshot == null || snapshot.Entries == null) return false;
            var batches = new Dictionary<string, List<MutationJournalRecord>>(StringComparer.Ordinal);
            var dirtyBatchIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in snapshot.Entries)
                if (entry != null && entry.WorldId == WorldLifecycle.WorldId && entry.TaiwuId == taiwuId
                    && !string.IsNullOrWhiteSpace(entry.BatchId) && !entry.ProjectionCommitted)
                    dirtyBatchIds.Add(entry.BatchId);
            foreach (var entry in snapshot.Entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.BatchId) || !dirtyBatchIds.Contains(entry.BatchId)) continue;
                List<MutationJournalRecord> list;
                if (!batches.TryGetValue(entry.BatchId, out list)) batches[entry.BatchId] = list = new List<MutationJournalRecord>();
                list.Add(entry);
            }
            foreach (var pair in batches)
            {
                var entries = pair.Value;
                entries.Sort((x, y) => x.StepIndex.CompareTo(y.StepIndex));
                var first = entries[0];
                if (first.WorldId != WorldLifecycle.WorldId || first.TaiwuId <= 0 || first.NpcId <= 0)
                    return false;
                foreach (var entry in entries)
                    if (entry.WorldId != first.WorldId || entry.TaiwuId != first.TaiwuId
                        || entry.NpcId != first.NpcId || entry.WorldDate != first.WorldDate
                        || entry.ConversationClearEpoch != first.ConversationClearEpoch)
                        return false;
                string projectionText = null;
                foreach (var entry in entries)
                    if (!entry.ProjectionCommitted && !string.IsNullOrWhiteSpace(entry.ProjectionText))
                    { projectionText = entry.ProjectionText; break; }
                var outcomes = new List<string>();
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    if (string.IsNullOrWhiteSpace(entries[i].ProjectionOutcomesJson)) continue;
                    try
                    {
                        var saved = JArray.Parse(entries[i].ProjectionOutcomesJson);
                        foreach (var token in saved)
                        {
                            string line = token == null ? null : token.ToString();
                            if (!string.IsNullOrWhiteSpace(line) && !outcomes.Contains(line)) outcomes.Add(line);
                        }
                    }
                    catch { return false; }
                    break;
                }
                foreach (var entry in entries)
                {
                    string line = entry.OutcomeText;
                    if (string.IsNullOrWhiteSpace(line)) line = RecoveredCompanionOutcomeLine(entry);
                    if (!string.IsNullOrWhiteSpace(line) && !outcomes.Contains(line)) outcomes.Add(line);
                }
                var receipts = new List<StoryProjectionReceipt>();
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    string receiptsJson = entries[i].ProjectionReceiptsJson;
                    if (string.IsNullOrWhiteSpace(receiptsJson)) continue;
                    if (!ProjectionReceiptsShapeValid(receiptsJson)) return false;
                    try
                    {
                        var saved = JsonConvert.DeserializeObject<List<StoryProjectionReceipt>>(receiptsJson);
                        if (saved != null) receipts.AddRange(saved);
                    }
                    catch { return false; }
                    break;
                }
                if (!TryCollectBatchProjectionAuthority(entries, out HashSet<int> authorizedParticipants))
                    return false;
                if (!CompanionProjectionReceiptsMatchJournal(entries, outcomes, receipts,
                    authorizedParticipants))
                {
                    Debug.LogWarning("[JHYL_COMPANION_RECOVERY_RECEIPT_REJECTED] batch=" + pair.Key
                        + " durable_projection=non_factual_fallback");
                    receipts.Clear();
                }
                if (string.IsNullOrWhiteSpace(projectionText))
                {
                    var result = new CompanionMonthlyResult
                    {
                        NpcId = first.NpcId,
                        Name = string.IsNullOrWhiteSpace(first.ActorName) ? ("#" + first.NpcId) : first.ActorName,
                        LocationText = first.ActorLocation,
                    };
                    result.Outcomes.AddRange(outcomes);
                    result.StoryReceipts.AddRange(receipts);
                    foreach (int id in authorizedParticipants) result.AuthorizedParticipantIds.Add(id);
                    result.Summary = outcomes.Count == 0 ? "主动行事回执仍未知" : Trim(outcomes[0], 42);
                    result.Detail = DeterministicStory(result, null);
                    projectionText = "【主动行事】" + result.Detail;
                }
                string outcomesJson = JsonConvert.SerializeObject(outcomes, Formatting.None);
                if (!CommitCompanionActorOutcomeMemory(first.TaiwuId, first.NpcId,
                    first.WorldDate, pair.Key, outcomes)
                    || !CommitCompanionTargetAwareness(first.TaiwuId, first.WorldDate, receipts))
                    return false;
                if (!PrepareCompanionBatchProjection(path, pair.Key, projectionText, outcomesJson)) return false;

                long currentClearEpoch = TalkOrchestrator.CaptureConversationClearEpoch(first.TaiwuId, first.NpcId);
                bool userCleared = currentClearEpoch != first.ConversationClearEpoch;
                if (!userCleared)
                {
                    if (!WorldLifecycle.IsSameWorld(generation)) return false;
                    bool already = ConversationContainsExactNpcProjection(first.TaiwuId, first.NpcId,
                        first.WorldDate, projectionText);
                    if (!already && !TalkOrchestrator.AppendNpcLineIfCurrent(first.TaiwuId, first.NpcId,
                        projectionText, first.WorldDate, generation, first.ConversationClearEpoch,
                        first.ActorName)) return false;
                    if (already && !TalkOrchestrator.TryUpdateExistingConversationNameIfCurrent(
                        first.TaiwuId, first.NpcId, first.ActorName, generation)) return false;
                }
                else
                    Debug.Log("[江湖有灵] 同道终态投影恢复遇到用户已清空会话，保留 journal 审计但不复活旧聊天 batch=" + pair.Key);
                if (!CommitCompanionBatchProjection(path, pair.Key)) return false;
                AcknowledgeProjectedCompanionBatch(path, pair.Key);
            }
            return true;
        }

        private static string RecoveredCompanionOutcomeLine(MutationJournalRecord entry)
        {
            if (entry == null) return null;
            string tool = string.IsNullOrWhiteSpace(entry.ToolName) ? "unknown" : entry.ToolName;
            if (entry.Status == "succeeded")
                return "成功:工具 " + tool + " 已由后端终态回执确认";
            if (entry.Status == "unknown")
                return "未知:工具 " + tool + " 的最终结果无法判定"
                    + (string.IsNullOrWhiteSpace(entry.Message) ? "" : (":" + entry.Message));
            if (entry.Status == "prepared" || entry.Status == "pending")
                return "未知:工具 " + tool + " 的后端回执尚未确认；不会无界重试";
            return "失败:工具 " + tool + " 未成"
                + (string.IsNullOrWhiteSpace(entry.Message) ? "" : (":" + entry.Message));
        }

        private static void ReportMutationUnknown(List<string> outcomes, string toolName, string reason,
            Action<string> onResult)
        {
            string text = "工具 " + (toolName ?? "unknown") + " " + (reason ?? "后端结果未知");
            if (outcomes != null) outcomes.Add("未知:" + text);
            onResult?.Invoke("UNKNOWN:" + text);
        }

        private static bool CompleteMutationStepProjection(MutationLease lease, List<string> outcomes,
            bool succeeded, string text, Action<string> onResult, string projectionAsset = null)
        {
            if (succeeded && (lease == null || !lease.HasAuthoritativeSucceededReceipt))
            {
                CompleteUnknownStepProjection(lease, outcomes, lease == null ? "unknown" : lease.ToolName,
                    "成功回调没有与当前 operationId 精确绑定的 durable backend receipt；拒绝生成成功事实",
                    onResult);
                return false;
            }
            AddOutcome(outcomes, succeeded, text);
            string line = outcomes != null && outcomes.Count > 0 ? outcomes[outcomes.Count - 1] : null;
            if (lease == null || !lease.PersistOutcomeText(line, succeeded ? projectionAsset : null))
            {
                if (lease != null && TryInvalidateBatch(lease.BatchEpoch))
                    RequestGuardRelease(lease.BatchEpoch, "step_projection_checkpoint_failed");
                ReportMutationUnknown(outcomes, lease == null ? "unknown" : lease.ToolName,
                    "后端终态已确认，但完整批次投影 checkpoint 失败；停止后续行动并等待崩溃恢复", onResult);
                return false;
            }
            onResult?.Invoke((succeeded ? "OK:" : "未成:") + text);
            return true;
        }

        private static void CompleteUnknownStepProjection(MutationLease lease, List<string> outcomes,
            string toolName, string reason, Action<string> onResult)
        {
            ReportMutationUnknown(outcomes, toolName, reason, onResult);
            string line = outcomes != null && outcomes.Count > 0 ? outcomes[outcomes.Count - 1] : null;
            if (lease != null) lease.PersistOutcomeText(line, null);
        }

        private static void ReportReceivedMutationUnknown(MutationLease lease, List<string> outcomes,
            string toolName, string message, Action<string> onResult)
        {
            if (lease != null)
            {
                lock (MutationGate)
                    if (!string.IsNullOrWhiteSpace(lease.MutationKey)) UnknownMutationQuarantine.Add(lease.MutationKey);
                UpdateMutationJournal(lease.JournalPath, lease.MutationKey, lease.OperationId, lease.ToolName, "unknown", 0);
                if (TryInvalidateBatch(lease.BatchEpoch)) RequestGuardRelease(lease.BatchEpoch, "operation_receipt_unknown");
            }
            ReportMutationUnknown(outcomes, toolName, string.IsNullOrWhiteSpace(message)
                ? "后端 operation receipt 未确认，已停止且不会自动重试"
                : message, onResult);
        }

        private static bool IsUnconfirmedMutationMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return false;
            return message.StartsWith("UNKNOWN:", StringComparison.OrdinalIgnoreCase)
                || message.Contains("回执暂未") || message.Contains("回执未确认") || message.Contains("结果未知")
                || message.Contains("暂未可得") || message.Contains("请稍后再试")
                || message.Contains("RPC 派发失败") || message.Contains("回包解析失败") || message.Contains("回包为空");
        }

        private static bool PredicateIsCurrent(Func<bool> stillCurrent)
        {
            if (stillCurrent == null) return true;
            try { return stillCurrent(); }
            catch { return false; }
        }

        private static void CompleteMutationLease(int batchEpoch)
        {
            bool released = false;
            int remaining;
            string reason = null;
            lock (MutationGate)
            {
                int count;
                if (MutationInflightByEpoch.TryGetValue(batchEpoch, out count))
                {
                    if (count <= 1) MutationInflightByEpoch.Remove(batchEpoch);
                    else MutationInflightByEpoch[batchEpoch] = count - 1;
                }
                remaining = TotalInflightUnsafe();
                if (_guardReleasePending && remaining == 0)
                {
                    reason = _guardReleaseReason;
                    _guardReleasePending = false;
                    _guardReleaseReason = null;
                    _running = false;
                    released = true;
                }
            }
            if (released) DisposeBatchRequests(batchEpoch);
            Debug.Log("[JHYL_COMPANION_MUTATION_LEASE] complete epoch=" + batchEpoch
                + " remaining=" + remaining + " guardReleased=" + released + " reason=" + (reason ?? "-"));
        }

        private static void RequestGuardRelease(int batchEpoch, string reason)
        {
            int remaining;
            bool released;
            bool invalidated = false;
            lock (MutationGate)
            {
                _guardReleasePending = true;
                _guardReleaseReason = reason;
                remaining = TotalInflightUnsafe();
                // Defensive fail-closed path: a nominal completion should have no mutation
                // outstanding. If it does (for example an unexpected coroutine exception),
                // invalidate before retaining the guard so no sibling continuation can dispatch.
                if (remaining > 0 && batchEpoch == _runEpoch)
                {
                    unchecked { _runEpoch++; }
                    invalidated = true;
                }
                released = remaining == 0;
                if (released)
                {
                    _guardReleasePending = false;
                    _guardReleaseReason = null;
                    _running = false;
                }
            }
            if (invalidated) CancelBatchRequests(batchEpoch);
            if (released) DisposeBatchRequests(batchEpoch);
            if (released)
                Debug.Log("[JHYL_COMPANION_MUTATION_FENCE] released epoch=" + batchEpoch + " reason=" + reason);
            else
                Debug.LogWarning("[JHYL_COMPANION_MUTATION_FENCE] retained epoch=" + batchEpoch
                    + " inflight=" + remaining + " reason=" + reason
                    + "; later monthly batches remain disabled until callbacks arrive or bounded unknown watchdogs expire");
        }

        private static int TotalInflightUnsafe()
        {
            int total = 0;
            foreach (var pair in MutationInflightByEpoch) total += Math.Max(0, pair.Value);
            return total;
        }

        private static bool RunIsCurrent(int batchEpoch, int generation, int taiwuId, int npcId,
            long conversationClearEpoch)
        {
            return BatchIsCurrent(batchEpoch, generation)
                && TalkOrchestrator.CaptureConversationClearEpoch(taiwuId, npcId) == conversationClearEpoch;
        }

        private static CompanionEvidenceContext BuildEvidenceContext(NpcSnapshot snap, int date, CompanionSceneContext scene)
        {
            var context = new CompanionEvidenceContext();
            if (snap == null || scene == null) return context;
            string recent = TalkOrchestrator.RecentDialogueWith(snap.TaiwuId, snap.NpcId, MonthlyDialogueLimit) ?? "";
            int lastChat = TalkOrchestrator.LastChatDate(snap.TaiwuId, snap.NpcId);
            bool currentMonth = lastChat == date || (date > 0 && lastChat == date - 1);
            if (currentMonth)
            {
                foreach (string playerLine in ExtractPlayerLines(recent, snap.Name))
                {
                    if (!ContainsExplicitHighRiskRequest(playerLine) || ContainsRiskNegation(playerLine)) continue;
                    // 太吾自己作为受害目标绝不由对话文字授权；只有下方权威 RelationFlag=仇敌才可授权。
                    var targets = MentionedTeammateTargetIds(playerLine, scene);
                    if (targets.Count > 0)
                        AddEvidence(context, "dialogue", "本月太吾明确提出的高风险请求:" + Trim(playerLine, 180), targets);
                }
            }
            if ((snap.RelationFlag & 32768) != 0)
                AddEvidence(context, "relation", snap.Name + "与太吾当前真实关系为仇敌", new List<int> { snap.TaiwuId });
            AddRelationshipMotiveEvidence(context, snap, scene);
            // QueryRosterRelations 返回的是给叙事看的姓名文本，不具备实体身份。它绝不参与高风险授权；
            // NPC-NPC 敌对只认下方 saga open-loop 中持久化的 ParticipantIds。

            try
            {
                var saga = EventSagaStore.Load(snap.TaiwuId);
                if (saga != null && saga.LoadReliable && saga.Active && saga.WorldId == WorldLifecycle.WorldId && saga.OpenLoops != null)
                    foreach (var loop in saga.OpenLoops)
                    {
                        if (loop == null || loop.Status != "open" || loop.Kind != "hostility" || loop.ParticipantIds == null
                            || !loop.ParticipantIds.Contains(snap.NpcId) || loop.EvidenceIds == null
                            || !loop.EvidenceIds.Exists(OperationId.IsValid)) continue;
                        foreach (int participant in loop.ParticipantIds)
                        {
                            if (participant == snap.NpcId || participant == snap.TaiwuId) continue;
                            if (!SagaLoopHasVerifiedHostility(saga, loop, snap.NpcId, participant)) continue;
                            string name = scene.Names.ContainsKey(participant) ? scene.Names[participant] : null;
                            if (!string.IsNullOrWhiteSpace(name))
                                AddEvidence(context, "saga_open_loop", "连载未解决敌对线索 " + loop.Id + ":" + loop.Summary,
                                    new List<int> { participant });
                        }
                    }
            }
            catch { }
            return context;
        }

        // 后端只返回结构化角色 id，不靠姓名文本猜目标。过月上下文不预载心系之人；
        // 高风险关系证据只保留当前真实敌对边。
        private static void AddRelationshipMotiveEvidence(CompanionEvidenceContext context, NpcSnapshot snap,
            CompanionSceneContext scene)
        {
            if (context == null || snap == null || scene == null
                || string.IsNullOrWhiteSpace(scene.RelationshipMotiveFacts)) return;
            foreach (string raw in scene.RelationshipMotiveFacts.Split(';'))
            {
                string[] parts = (raw ?? "").Split(':');
                if (parts.Length < 2) continue;
                if ((parts[0] == "enemy" || parts[0] == "enemy_of")
                    && int.TryParse(parts[1], out int enemyId) && enemyId > 0 && enemyId != snap.NpcId)
                {
                    string enemyName = scene.Names.ContainsKey(enemyId) ? scene.Names[enemyId] : ("#" + enemyId);
                    AddEvidence(context, "live_hostility", snap.Name + "与" + enemyName + "当前存在真实敌对关系",
                        new List<int> { enemyId });
                }
            }
        }

        private static bool SagaLoopHasVerifiedHostility(EventSaga saga, EventSagaOpenLoop loop,
            int actorId, int targetId)
        {
            if (saga == null || loop == null || loop.EvidenceIds == null || saga.OutcomeJournal == null) return false;
            foreach (string evidenceId in loop.EvidenceIds)
            {
                if (!OperationId.IsValid(evidenceId)) continue;
                foreach (var outcome in saga.OutcomeJournal)
                    if (outcome != null && outcome.OperationId == evidenceId && outcome.ToolName == "event_enmity"
                        && outcome.Status == "succeeded" && SagaOutcomeCreatesHostility(outcome)
                        && (outcome.ActorId == actorId && outcome.TargetId == targetId
                            || outcome.ActorId == targetId && outcome.TargetId == actorId))
                        return true;
            }
            return false;
        }

        private static bool SagaOutcomeCreatesHostility(EventSagaStepOutcome outcome)
        {
            StoryProjectionReceipt receipt = outcome?.StoryReceipt;
            return outcome != null && outcome.ToolName == "event_enmity"
                && !string.IsNullOrWhiteSpace(outcome.Receipt) && receipt != null
                && StoryProjectionValidator.ReceiptMatchesAuthoritativeOutcome(outcome.ToolName,
                    outcome.OperationId, outcome.ActorId, outcome.TargetId, outcome.ActorName,
                    outcome.TargetName, outcome.ProjectionAsset, outcome.Summary, outcome.Status == "succeeded",
                    outcome.BackendReceiptStored, receipt)
                && string.Equals((receipt.Asset ?? "").Trim(), "结仇", StringComparison.Ordinal);
        }

        private static void AddEvidence(CompanionEvidenceContext context, string kind, string text, List<int> targets)
        {
            if (context == null || targets == null || targets.Count == 0) return;
            string id = "E-" + OperationId.FromStableKey(kind + "|" + text + "|" + string.Join("|", targets.ConvertAll(x => x.ToString()).ToArray())).Substring(0, 12);
            if (context.Facts.ContainsKey(id)) return;
            context.Facts[id] = new EvidenceFact { Id = id, Kind = kind, Text = text };
        }

        private static List<int> MentionedTeammateTargetIds(string text, CompanionSceneContext scene)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            foreach (var pair in scene.Names)
            {
                if (string.IsNullOrWhiteSpace(pair.Value) || pair.Value.Trim().Length < 2
                    || text.IndexOf(pair.Value, StringComparison.Ordinal) < 0) continue;
                int sameNameCount = 0;
                foreach (var candidate in scene.Names)
                    if (string.Equals(candidate.Value, pair.Value, StringComparison.Ordinal)) sameNameCount++;
                // 同名或别名无法唯一落到一个角色 id 时 fail-closed；玩家可改用 #角色ID 明示。
                if (sameNameCount == 1 && !result.Contains(pair.Key)) result.Add(pair.Key);
            }
            foreach (var pair in scene.Names)
                if (MentionsExactCharacterId(text, pair.Key) && !result.Contains(pair.Key))
                    result.Add(pair.Key);
            return result;
        }

        private static bool MentionsExactCharacterId(string text, int characterId)
        {
            if (string.IsNullOrWhiteSpace(text) || characterId <= 0) return false;
            string marker = "#" + characterId;
            int start = 0;
            while (start < text.Length)
            {
                int at = text.IndexOf(marker, start, StringComparison.Ordinal);
                if (at < 0) return false;
                int end = at + marker.Length;
                if (end >= text.Length || !char.IsDigit(text[end])) return true;
                start = end;
            }
            return false;
        }

        private static List<string> ExtractPlayerLines(string recent, string npcName)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(recent)) return result;
            const string playerMarker = "太吾:";
            string npcMarker = (string.IsNullOrWhiteSpace(npcName) ? "某人" : npcName) + ":";
            int cursor = 0;
            while (cursor < recent.Length)
            {
                int player = recent.IndexOf(playerMarker, cursor, StringComparison.Ordinal);
                if (player < 0) break;
                int start = player + playerMarker.Length;
                int nextPlayer = recent.IndexOf(playerMarker, start, StringComparison.Ordinal);
                int nextNpc = recent.IndexOf(npcMarker, start, StringComparison.Ordinal);
                int end = recent.Length;
                if (nextPlayer >= 0 && nextPlayer < end) end = nextPlayer;
                if (nextNpc >= 0 && nextNpc < end) end = nextNpc;
                string line = recent.Substring(start, Math.Max(0, end - start)).Trim();
                if (line.Length > 0) result.Add(line);
                cursor = Math.Max(start, end);
            }
            return result;
        }

        private static bool ContainsExplicitHighRiskRequest(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            bool command = text.Contains("帮我") || text.Contains("替我") || text.Contains("请你") || text.Contains("我要你")
                || text.Contains("给我") || text.Contains("务必") || text.Contains("必须") || text.Contains("去把") || text.StartsWith("把", StringComparison.Ordinal);
            bool act = text.Contains("杀了") || text.Contains("杀掉") || text.Contains("取其性命") || text.Contains("取他性命")
                || text.Contains("下毒") || text.Contains("毒死") || text.Contains("毒杀")
                || text.Contains("绑了") || text.Contains("绑走") || text.Contains("擒下") || text.Contains("抓住") || text.Contains("掳走");
            return command && act;
        }

        private static bool ContainsRiskNegation(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            string[] denied = { "不", "别", "莫", "不可", "不能", "不许", "放过", "饶了", "饶过", "住手", "算了" };
            foreach (string token in denied) if (text.IndexOf(token, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static List<LlmMessage> BuildMessages(NpcSnapshot snap, int date, CompanionSceneContext scene,
            CompanionEvidenceContext evidence, string relationshipNetwork)
        {
            string portrait = PortraitStore.GetPortrait(snap) ?? PortraitStore.BuildSimple(snap);
            var mem = NpcMemoryStore.Load(JianghuYoulingPaths.Memories, snap.TaiwuId.ToString(), snap.NpcId.ToString());
            var top = MemoryRanker.TopByImportanceRecency(mem.All, MonthlyMemoryLimit, date);
            var lines = MemoryIndex.RenderForPrompt(top, date);
            string recent = TalkOrchestrator.RecentDialogueWith(snap.TaiwuId, snap.NpcId, MonthlyDialogueLimit);
            var sb = new StringBuilder();
            sb.Append(scene != null && scene.ActorScene
                ? "你是《太吾绘卷》里的江湖人物「"
                : "你是《太吾绘卷》里的同道「")
                .Append(snap.Name).Append("」。现在是过月结算,你要基于自己的性格、记忆、处境,自行决定本月主动做什么。");
            sb.Append("【本轮身份与性别账本·权威】你自己=").Append(snap.Name).Append("(#")
                .Append(snap.NpcId).Append(",").Append(string.IsNullOrWhiteSpace(snap.Gender) ? "未知" : snap.Gender)
                .Append(")；第一人称永远指你自己。太吾/玩家=")
                .Append(string.IsNullOrWhiteSpace(snap.TaiwuName) ? "太吾" : snap.TaiwuName)
                .Append("(#").Append(snap.TaiwuId).Append(",")
                .Append(string.IsNullOrWhiteSpace(snap.TaiwuGender) ? "未知" : snap.TaiwuGender)
                .Append(")。姓名、性别、代词、行动者和承受者必须与账本及工具参数逐项对应，不得被画像、旧记忆或称谓颠倒。");
            sb.Append("【本轮权威年月】").Append(FormatWorldMonth(date)).Append("。所有判断与行动都发生在这个月。");
            sb.Append(scene != null && scene.ActorScene
                ? "你是玩家手动加入主动人物名单的江湖人物；是否与太吾聊过不影响过月行动资格。近来对话若存在只是动因之一，请同时从长期记忆、关系网、人设经历与当下处境里找动因。"
                : "你是从太吾当前全部同道中被本月稳定抽中的；是否与太吾聊过不影响资格。近来对话若存在只是动因之一，请同时从长期记忆、关系网、人设经历与当下处境里找动因。");
            sb.Append("这不是普通聊天回复,不要只说想法;你必须在多轮 Agent 循环中真正调用工具做事,每轮读取真实回执后再决定下一步,不得预言或假称成功。");
            sb.Append(scene != null && scene.ActorScene
                ? "你是玩家手动加入候选的普通江湖人物，本月从你自己的真实所在地行动；太吾若不在场，只能千里传音联系。"
                : "你本月始终与太吾同行,不能离队、赴约、迁居、自行远行或追逐远方人物;异地熟人只能千里传音联系。");
            sb.Append(BehaviorDispositionPolicy.BuildActorDirective(snap.Behavior, true));
            sb.Append(BehaviorDispositionPolicy.BuildMoodAndFameDirective(
                snap.Happiness, snap.FameText));
            sb.Append("【核心原则】JHYL_COMPANION_MONTHLY_MUST_ACT: 本月你被选中后必须完成至少三项取得成功回执的实质行为，并覆盖至少两种行为类型；不能无所事事或只传话。赠物、交换、写书、亲授统一属于“物资与传承”：这些动作本身都允许，因果需要时赠物后仍可传功，但无论执行几项都只增加一种类型，不能单靠它们凑足两类。讲话只在告知、请求、解释、谈判、表态或承接因果确有需要时自然发生，不是完成本月行动的硬门槛，也不计入三项、两类实质行为；发送方式必须服从代码刚返回的实时位置，显示当面就不能说成传音，显示异地就不能写成当面。为本月行动挑动因时按此优先级逐层下探:①长期记忆里尚未了结的心愿、心结或承诺;②当前显著关系网里的爱憎纠葛;③你的人设、生平经历与见闻;④近来与太吾对话中的请求、冲突或线索;⑤你与太吾的情分及当前处境。上一层没有指向就顺着下一层找。只有第一项行动都无法落地、且位置、精纯或目标状态等硬前置把所有可用实事都挡死时,才可以只写一条 no_action 并说清是哪条硬前置拦住了你。");
            sb.Append("若没有更紧迫的承诺、冲突、危机或追寻，闲暇本身就是有效日常动因：只要确有已会武学就可以 train_skill 日常练功，只要背包确有可读秘籍或技艺书就可以 read_book 闲暇阅读。它们不要求先发生特殊剧情，也不因人物懒散而被禁止。");
            sb.Append("JHYL_COMPANION_MONTHLY_COMBO_ACTIONS: 围绕同一个核心动因连续做至少三步因果链，并覆盖至少两种类型;每一步都要先看上一工具的真实结果。动作要有关联；同类后续可以真实发生，只是不增加类型数，最终还要形成至少两类。");
            sb.Append("每次读完真实结果，都回到最初动因判断并检查三项两类进度：未达标就继续挑选仍与动因紧密相关的下一步，并最终补足另一类；达到只代表越过最低线，不代表完成，也不形成动作上限。达到后若最初动因已经自然落定或被权威结果明确阻断，就停止调用工具；只有存在具体未决因果才继续紧密相关的下一步，禁止仅为丰富度追加无关动作。");
            sb.Append("本月每项真实工具结果都由代码自动写入长期记忆；不要额外概括或调用记忆工具。");
            sb.Append("不要为了热闹做不合人设的事，也不要连续数月只传一句话、写一本书、送一件小物或传一门功法来敷衍。优先承接未了的爱憎、承诺、欲望与危机，让三项跨类型行为形成前因、行动与余波，真正改变自己或另一人的处境。工具做完后由你自己停止调用工具，只回复‘本轮结束’，不能返回空消息，无需生成叙事正文。");
            sb.Append("你必须把【人物与距离】当作当前感知边界：在场人物可做符合前置的当面行为；异地人物通常只能千里传音及进行言语能够成立的交流、关系或态度行为。过月 kill/capture/poison 是明确例外，代表行动者在整月中自行寻至目标后实施，代码只校验精纯，不要因异地而禁用。可用完整姓名联系未列出的任意人物，代码只接受全局唯一严格全名。绝不能把群聊成员、旧识或记忆里的人仅因被提及就误认作在场。进入游戏本体战斗只属于当面单聊，本过月规划器不提供发起战斗。");
            sb.Append("kill/capture/poison 必须有符合人物与本月因果的具体动机；仇怨只是可能之一，也可源于利益、灭口、保护、受命、野心或冲突升级。不要随机伤人，但也不要把没有仇敌关系误当成禁止。过月这三项的玩法硬门槛只有行动者精纯不低于目标；代码仍会拒绝无效、已死亡、自身等不可能目标。");
            sb.Append("不得因为偷窃、下毒、擒拿、杀人属于负面或危险行为就统一降低选择概率、设置冷却或限制每月次数；具体动机成立时，不寻常的行动应当和安全行动一样直接进入选择。");
            var msgs = new List<LlmMessage>();
            msgs.Add(LlmMessage.System(sb.ToString()));
            msgs.Add(LlmMessage.User("【你的画像】\n" + portrait
                + "\n\n" + (scene == null ? "【人物与距离】无法确认" : scene.Render(snap.NpcId))
                + "\n\n" + (evidence == null ? "【危险行动动机线索】无已结构化记录" : evidence.Render())
                + "\n\n【当轮代码权威身份】\n姓名:" + (snap.Name ?? ("#" + snap.NpcId))
                + "；性别:" + (string.IsNullOrWhiteSpace(snap.Gender) ? "未知" : snap.Gender)
                + "；性取向:" + (string.IsNullOrWhiteSpace(snap.SexualOrientation) ? "未可靠读取" : snap.SexualOrientation)
                + "；身龄:" + snap.PhysiologicalAge + "岁（当前生理、外观与社交呈现）"
                + "；命龄:" + snap.ActualAge + "岁（实际生存总年数）"
                + "；当前魅力值:" + CharacterCharmText.Format(snap.Charm)
                + "\n当前立场:" + (string.IsNullOrWhiteSpace(snap.Behavior) ? "未知" : snap.Behavior)
                + "；当前身份:" + (string.IsNullOrWhiteSpace(snap.OrgFullTitle) ? "无明确门派身份" : snap.OrgFullTitle)
                + (string.IsNullOrWhiteSpace(snap.GradeName) ? "" : "（" + snap.GradeName + "）")
                + "；与太吾当前关系:" + (string.IsNullOrWhiteSpace(snap.Relation) ? "无特殊" : snap.Relation)
                + "；当前好感:" + (string.IsNullOrWhiteSpace(snap.FavorLevel) ? "未载" : snap.FavorLevel)
                + "\n\n【当下状态】\n" + PortraitStore.StatusText(snap)
                + "\n当前心情值:" + (int)snap.Happiness + "（越高越舒畅，越低越低落）"
                + "\n江湖名誉/侠名:" + (string.IsNullOrWhiteSpace(snap.FameText) ? "尚无明确侠名" : snap.FameText)
                + "\n\n【你随身可用之物（首批预览；需要更多候选时 query_person 自己）】\n" + ListGiftables(snap, 8)
                + "\n\n【你背包里现在确实可换上的装备（首批预览）】\n" + ListEquipableInventory(snap, 8)
                + "\n\n【你当前确实穿戴着】\n" + ListEquipped(snap)
                + "\n\n【你当前尚未练满、可用于日常修炼的武学（权威确切候选）】\n" + ListTrainableSkills(snap)
                + "\n\n【你背包中当前可供闲暇阅读的秘籍或技艺书（确切候选）】\n" + ListReadableBooks(snap)
                + "\n\n【当前确实可以颠倒正逆练的武学】\n" + ListFlippableSkills(snap)
                + "\n\n【你会的技艺（首批预览；需要更多候选时 query_person 自己）】\n" + ListSkills(snap.LearnableLifeSkills, 8)
                + "\n\n【秘闻能力】\n" + (snap.ShareableSecrets != null && snap.ShareableSecrets.Count > 0
                    ? "你确有可分享的秘闻；选定接收者后必须调用 query_secret_recipient 获取对方尚未知晓的准确候选。"
                    : "当前没有已确认可分享的秘闻。")
                + "\n\n【你记得的事】\n" + (lines.Count == 0 ? "暂无" : string.Join("\n", lines.ToArray()))
                + "\n\n【你的生平经历与见闻】\n" + RenderLifeAndLore(snap)
                + "\n\n【你的当前显著关系网（均从你本人视角）】\n"
                + (string.IsNullOrWhiteSpace(relationshipNetwork) ? "暂无已确认显著关系" : relationshipNetwork)
                + "\n\n【你与太吾的情分】\n" + RenderAffinityToTaiwu(snap)
                + "\n\n【近来与太吾的对话】\n" + (string.IsNullOrWhiteSpace(recent) ? "暂无" : recent)
                + "\n\n请依核心原则的动因优先级(长期记忆>关系网>人设生平见闻>近来对话>对太吾好感与处境)决定本月至少一件非对话主动行为；不能只传话。"));
            return msgs;
        }

        private static string FormatWorldMonth(int date)
        {
            int safe = date < 0 ? 0 : date;
            return "第" + (safe / 12 + 1) + "年" + (safe % 12 + 1) + "月";
        }

        // JHYL_COMPANION_MONTHLY_MUST_ACT: 把 NPC 生平经历见闻作为决策依据补进上下文,供动因阶梯第③层"人设/生平/见闻"取用。
        private static string RenderLifeAndLore(NpcSnapshot snap)
        {
            if (snap == null || snap.LifeRecords == null || snap.LifeRecords.Count == 0) return "暂无";
            var records = snap.LifeRecords;
            int start = Math.Max(0, records.Count - MonthlyMemoryLimit);
            var sb = new StringBuilder();
            for (int i = start; i < records.Count; i++)
            {
                var r = records[i];
                if (string.IsNullOrWhiteSpace(r.text)) continue;
                string type = string.IsNullOrWhiteSpace(r.type) ? "经历" : r.type.Trim();
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("[").Append(type).Append("] ").Append(Trim(r.text.Trim(), 90));
            }
            return sb.Length == 0 ? "暂无" : sb.ToString();
        }

        // JHYL_COMPANION_MONTHLY_MUST_ACT: 把 NPC 对太吾的当前关系与好感作为决策依据补进上下文,供动因阶梯第⑤层"对太吾好感"兜底取用。
        private static string RenderAffinityToTaiwu(NpcSnapshot snap)
        {
            if (snap == null) return "暂无";
            string rel = string.IsNullOrWhiteSpace(snap.Relation) ? "无已确认的特殊关系" : snap.Relation.Trim();
            string fav = string.IsNullOrWhiteSpace(snap.FavorLevel) ? "未载" : snap.FavorLevel.Trim();
            return "与太吾关系:" + rel + ";对太吾好感:" + fav;
        }

        private static List<ToolDef> BuildCompanionTools(NpcSnapshot snap)
        {
            var tools = new List<ToolDef>
            {
                ToolDef.Of("no_action", "仅当本月第一项行动都无法成功，且位置/精纯/目标状态等硬前置把所有可用动作都挡死时才调用；一旦已有真实行为成功就不能再调用。reason 必须说清是哪条硬前置导致完全无法行动；不得因为懒得按动因优先级(长期记忆>关系网>人设生平见闻>近来对话>对太吾好感)找事就用它。", ToolDef.Obj(("reason", ToolDef.Str("说明是哪条硬前置导致完全无法行动"), true))),
                ToolDef.Of("query_person", "只读查询任意具名人物的真实近况、你与其当前关系，以及对方已通晓的武学和技艺；填“自己”时刷新并列出自己的完整持有、装备、武学和技艺候选。初始上下文只给首批预览，需要更多候选时查自己。多名人物要在同一工具轮并列查询；查询不会消耗真实动作步数。直接调用行动时，代码也会自动补齐缺少的精确前置并在可靠时同轮执行。", ToolDef.Obj(("name", ToolDef.Str("人物完整姓名/#角色ID/太吾/自己"), true))),
                ToolDef.Of("query_relationship", "查询你与某个具名人物之间当前真实的关系、好感和敌友状态。默认上下文已列出的权威关系可直接复用；未列出、关系刚变化或拿不准时再查。太吾关系已在默认上下文，无需重复查询。", ToolDef.Obj(("target", ToolDef.Str("第三方完整姓名/#角色ID"), true))),
                ToolDef.Of("query_health_status", "只读察看你自己此刻的伤势、气息（游戏本体的内息紊乱）和六类中毒实况。担心身体、受伤中毒、想判断是否需要求医或照料别人前可用；查询不计入真实行动。", ToolDef.Obj()),
                ToolDef.Of("send_message", "在本月因果需要告知行动结果、提出请求、警告、道歉、解释、谈判、吐露态度、兑现承诺或铺垫下一行动时，向任意具名人物当面说话或发起千里传音；没有交流需要时不必调用。代码会在派发前实时读取双方位置；目标在场时只能当面告知，异地时只能远隔传声。content 只写实际要说的话，不要自行写“传音”或“当面”。默认上下文未给出双方关系时先 query_relationship，称谓和态度必须符合真实关系。讲话不计入本月三项、两类实质行为。", ToolDef.Obj(("target", ToolDef.Str("对方完整姓名/太吾/#角色ID"), true), ("content", ToolDef.Str("要亲口传达的消息"), true))),
                ToolDef.Of("steal", "偷取任意在场人物的真实持有物。后端按本体物品价值/数量警觉度与三阶段能力检定，贵重或大量物品更难；失败会败露并降低被偷者对偷者好感。异地不可用。", ToolDef.Obj(("victim", ToolDef.Str("在场被偷者完整姓名/太吾/#角色ID"), true), ("item", ToolDef.Str("要偷的物品名"), true), ("amount", ToolDef.Int("数量;空=1"), false))),
                ToolDef.Of("relate", "与任意具名人物缔结关系；对方异地时通过千里传音表达并落地。mentor 表示你为师、对方为徒；adoptive_parent 表示你认对方为义父/义母，adoptive_child 表示你收对方为义子/义女。明确自愿建立义亲时不硬卡年龄、双向好感或已有在世父母子女；执行层仍拒绝自己认自己、重复义亲和本体判定冲突的关系。lover 只写入你对目标的单向爱慕，不能替目标爱慕你；仅当目标原本已爱慕你时，结果才会成为两情相悦。NPC 之间可自主发展这些关系；涉及太吾时只能表达单向爱慕，其他需要太吾同意的关系不会执行。", ToolDef.Obj(("target", ToolDef.Str("对方完整姓名/太吾/#角色ID"), true), ("kind", ToolDef.Sel("关系；义亲按你看对方的辈分", "befriend", "sworn", "mentor", "adoptive_parent", "adoptive_child", "lover", "spouse"), true))),
                ToolDef.Of("enmity", "与任意具名人物结仇或化解仇怨；异地时可通过千里传音表达。", ToolDef.Obj(("target", ToolDef.Str("对方完整姓名/太吾/#角色ID"), true), ("make", ToolDef.Bool("true结仇,false化解"), true))),
                ToolDef.Of("add_feature", "让自己因本月经历长进一种良性品性；本动作只作用于自己。", ToolDef.Obj(("person", ToolDef.Str("人物只能填自己或留空"), false), ("feature", ToolDef.Str("品性词,如 仗义/勤勉/豁达/重信"), true))),
                ToolDef.Of("heal", "为自己、太吾或任意在场人物疗伤。目标必须真实在场，异地不可用。", ToolDef.Obj(("target", ToolDef.Str("在场疗伤对象;空=太吾"), false))),
                ToolDef.Of("detox", "为太吾或另一名在场人物驱毒。代码会先读取目标六类中毒实况；无毒、异地、自己给自己驱毒或本体医术判定不允许时不会执行。", ToolDef.Obj(("target", ToolDef.Str("另一名在场中毒者;空=太吾"), false))),
                ToolDef.Of("regulate_breath", "为太吾或另一名在场人物调息，降低其内息紊乱。代码会先读取目标真实气息；内息顺畅、异地、自己给自己调息或本体医术判定不允许时不会执行。", ToolDef.Obj(("target", ToolDef.Str("另一名在场需要调息者;空=太吾"), false))),
                ToolDef.Of("dissolve_relation", "解除自己与任意具名人物的既有关系；adoptive_parent/adoptive_child 按你看对方的辈分填写，异地时可通过千里传音表达。", ToolDef.Obj(("target", ToolDef.Str("对象完整姓名/太吾/#角色ID;空=太吾"), false), ("relation", ToolDef.Sel("关系", "friend", "sworn", "mentor", "adoptive_parent", "adoptive_child", "lover", "spouse"), true))),
                ToolDef.Of("adjust_favor", "因本月经历改变自己对任意具名人物的好感；不要求对方在场。", ToolDef.Obj(("target", ToolDef.Str("对象完整姓名/太吾/#角色ID;空=太吾"), false), ("delta", ToolDef.Int("好感变化 -3000..3000"), true))),
                ToolDef.Of("adjust_mood", "因本月已经发生的交谈、得失、关系变化或遭遇，改变任意人物（包括自己、第三方 NPC 或太吾）的当前心情。target 是心情实际变化的人；reason 写清直接原因。不要把同一变化重复算作好感。", ToolDef.Obj(("target", ToolDef.Str("心情实际变化的人：自己/太吾/完整姓名/#角色ID"), true), ("delta", ToolDef.Int("心情变化 -30..30，正=更愉快，负=更低落；不能为0"), true), ("reason", ToolDef.Str("本月已经发生并造成心情变化的具体原因"), true))),
                ToolDef.Of("adjust_fame", "因本月已经发生且会被江湖知晓的公开善举、恶行、胜负或事迹，改变任意人物（包括自己、第三方 NPC 或太吾）的名望。私下想法、普通闲聊和无人知晓的小事不能改名望。", ToolDef.Obj(("target", ToolDef.Str("名望实际变化的人：自己/太吾/完整姓名/#角色ID"), true), ("delta", ToolDef.Sel("名望变化", "-12", "-9", "-6", "-3", "3", "6", "9", "12"), true), ("reason", ToolDef.Str("已公开发生并足以影响名望的具体事迹"), true))),
                ToolDef.Of("set_relation", "只允许主动表达自己对太吾的单向爱慕，或单方面归心认可太吾。不能替太吾同意结交、结义、拜师、收徒、成婚或加入队伍；这些需要太吾本人决定。lover 不能替太吾爱慕你，只有太吾此前也已爱慕你时才会自然成为两情相悦。", ToolDef.Obj(("action", ToolDef.Sel("允许的单方面关系动作", "lover", "recognize"), true))),
                ToolDef.Of("spend_night", "与同地块的成年恋人或配偶共度春宵；对象不能是太吾，NPC 之间须符合亲密关系与游戏本体条件。", ToolDef.Obj(("target", ToolDef.Str("同地块的非太吾对象"), true))),
                ToolDef.Of("matchmake", "与同地块一名自己情愿结合的 NPC 结为夫妻。对象不能是自己或太吾；真实关系与婚配条件由游戏本体校验。", ToolDef.Obj(("target", ToolDef.Str("同地块 NPC 完整姓名/#角色ID"), true))),
            };

            // JHYL_COMPANION_MONTHLY_STABLE_ACTION_SURFACE: 初始快照是当前事实，不是整条
            // Agent 链的权限表。偷窃、交换等前一步可能让后一步新获得物品或银钱；技能、
            // 装备和秘闻状态也必须在实际调用前按最新现场复核。因此动态动作始终提供，
            // 由 RefreshActorPreflightState + 执行层实时校验，不能因初始空清单永久删工具。
            tools.Add(ToolDef.Of("flip_practice", "把自己当前确实可颠倒的一门功法正逆练颠倒；调用前会刷新可颠倒清单，本动作只作用于自己。", ToolDef.Obj(("person", ToolDef.Str("人物只能填自己或留空"), false), ("skill", ToolDef.Str("当前可颠倒的确切功法名"), true))));
            tools.Add(ToolDef.Of("train_skill", "把自己已经学会且尚未练满的一门武学直接修炼至完整状态：研读全部正逆与总纲页，并按原练法完成突破激活。闲暇无事或日常练功即可选择，不要求复仇、备战、承诺或危机。本动作只作用于自己，不能作用于太吾或第三方；代码内置查询实时进度，必须从上下文尚未练满的确切候选选名，调用前再次刷新。", ToolDef.Obj(("skill", ToolDef.Str("尚未练满候选中的确切武学名"), true))));
            tools.Add(ToolDef.Of("read_book", "把自己背包中真实持有且尚未读完的一本武学秘籍或技艺书直接读完全部书页。闲暇阅读、随手翻书、消磨时间或日常求知即可选择，不要求额外剧情。本动作只作用于自己，不能作用于太吾或第三方；代码内置查询实时进度，必须从上下文尚未读完的确切候选选名，调用前再次刷新。", ToolDef.Obj(("book", ToolDef.Str("尚未读完候选中的确切书名"), true))));
            tools.Add(ToolDef.Of("use_item", "使用自己背包中的真实消耗品。可因日常用膳、饮茶饮酒、疗伤服药、人物动机服毒或使用内力类特殊物品而触发；闲暇饮食不要求额外剧情。代码会内置刷新本体允许 NPC 使用的确切候选，并在执行时再次校验数量、主属性、服食栏位与库存后验。装备改用 change_equipment，秘籍改用 read_book；材料、工具、促织与普通杂物不可直接使用。", ToolDef.Obj(("item", ToolDef.Str("当前可自行使用候选中的确切物品名"), true))));
            tools.Add(ToolDef.Of("barter", "与任意在场人物当面以物换物。买卖、交易、议价、缺钱或双方各有所求时都可主动采用，不必等别人逐字提出“以物换物”。任意一边都可填“银钱”，可物品换物品、物品换银钱或银钱换物品。双方物品名和金额必须来自真实持有，调用前会刷新自己的当前持有物；异地不可用。", ToolDef.Obj(("target", ToolDef.Str("在场交换对象;空=太吾"), false), ("give_item", ToolDef.Str("自己给出的物品/资源/银钱"), true), ("give_amount", ToolDef.Int("自己给出数量或银钱金额;空=1"), false), ("receive_item", ToolDef.Str("希望对方给出的物品/资源/银钱"), true), ("receive_amount", ToolDef.Int("对方给出数量或银钱金额;空=1"), false))));
            tools.Add(ToolDef.Of("change_equipment", "自主换装。调用前会刷新背包和穿戴；action=on 时 item 只能选当前真实可装备物品，action=off 时 part 只能选当前非空部位。不得根据画像服饰或常识臆造物品。", ToolDef.Obj(("action", ToolDef.Sel("动作", "on", "off"), true), ("item", ToolDef.Str("当前可换上的真实物品名"), false), ("part", ToolDef.Sel("当前非空的卸下部位", "weapon", "armor", "accessory", "carrier"), false))));
            tools.Add(ToolDef.Of("write_book", "把自己真正会的一门武学或技艺回忆成秘籍，赠给任意在场人物。调用前会刷新当前所学；需要当面交书，异地不可用。", ToolDef.Obj(("target", ToolDef.Str("在场收书人;空=太吾"), false), ("type", ToolDef.Sel("类型", "combat", "life"), true), ("skill", ToolDef.Str("自己当前确实会的武学/技艺名"), true))));
            tools.Add(ToolDef.Of("teach", "把自己真正会、且对方尚未通晓的一门武学或技艺当面亲授给另一名在场人物。调用时会刷新当前所学并自动读取受教者已会内容；前置可靠且对方未学过就同轮亲授，否则不产生副作用并返回完整原因。不能亲授太吾，异地不可用。", ToolDef.Obj(("target", ToolDef.Str("在场受学者完整姓名/#角色ID"), true), ("type", ToolDef.Sel("类型", "combat", "life"), true), ("skill", ToolDef.Str("自己会且受教者尚未通晓的确切名称"), true))));
            tools.Add(ToolDef.Of("query_secret_recipient", "按指定接收者查询自己当前确知且对方尚未知、仍未公开的秘闻，返回的 index 保留完整清单原始序号。用于决定传播内容；直接调用 tell_secret 时代码也会自动做同样的接收者资格预查。", ToolDef.Obj(("target", ToolDef.Str("听闻者完整姓名/太吾/#角色ID;空=太吾"), false))));
            tools.Add(ToolDef.Of("tell_secret", "把自己当前确实知道的一条秘闻告诉任意具名人物；异地时通过千里传音。index 必须是初始清单或同一接收者查询中的原始序号；调用时代码会自动按具体接收者刷新资格，仍可传播就同轮执行，否则不产生副作用并返回当前候选。", ToolDef.Obj(("target", ToolDef.Str("听闻者完整姓名/太吾/#角色ID;空=太吾"), false), ("index", ToolDef.Int("初始清单或接收者资格查询中的原始秘闻序号"), true))));
            if (snap == null || snap.IsSect)
                tools.Add(ToolDef.Of("sect_support", "本月在自己所属门派内公开为太吾说项，提高本门对太吾的支持。", ToolDef.Obj()));
            tools.Add(ToolDef.Of("gift_item", "把自己当前真实持有且未穿戴的一件物品当面赠给太吾或任意在场第三方。自主过月绝不能卸下当前装备赠送；调用前会刷新当前持有物，异地不可用。", ToolDef.Obj(("target", ToolDef.Str("在场受赠者;空=太吾"), false), ("item", ToolDef.Str("当前真实未穿戴物品名"), true), ("amount", ToolDef.Int("数量;空=1"), false))));
            tools.Add(ToolDef.Of("gift_silver", "把自己当前真实持有的银钱当面赠给太吾或任意在场第三方。调用前会刷新银钱，异地不可用。", ToolDef.Obj(("target", ToolDef.Str("在场受赠者;空=太吾"), false), ("amount", ToolDef.Int("银钱数"), true))));
            tools.Add(ToolDef.Of("kill", "取某人性命；过月执行只校验行动者精纯不低于目标。有可夺财物时会取得目标背包中价值最高的一件并在结果中显示。仇怨只是可能动机之一，也可因利益、灭口、保护、受命、野心或冲突升级而选择，但必须符合人物与本月因果。", ToolDef.Obj(("target", ToolDef.Str("目标完整姓名/#角色ID"), true))));
            tools.Add(ToolDef.Of("poison", "向一人下毒；过月执行不消耗毒药，只校验行动者精纯不低于目标。按动机选择烈毒、郁毒、寒毒、赤毒、腐毒或幻毒，结果会显示实际毒型。仇怨只是可能动机之一，仍须有符合人物与本月因果的具体缘由。",
                ToolDef.Obj(("target", ToolDef.Str("目标完整姓名/太吾/#角色ID"), true),
                    ("poison_type", ToolDef.Sel("所下毒型", "烈毒", "郁毒", "寒毒", "赤毒", "腐毒", "幻毒"), true))));
            tools.Add(ToolDef.Of("capture", "把某人掳走为俘；过月执行不要求绳索，只校验行动者精纯不低于目标。仇怨只是可能动机之一，仍须有符合人物与本月因果的具体缘由。", ToolDef.Obj(("target", ToolDef.Str("目标完整姓名/#角色ID"), true))));
            tools.RemoveAll(tool => tool == null
                || !JianghuYouling.Core.Tools.ToolRegistry.IsAutonomousToolEnabled(tool.Name));
            return tools;
        }
        private static string DescribeCompanionTools(IList<ToolDef> tools)
        {
            if (tools == null || tools.Count == 0) return "无";
            var sb = new StringBuilder();
            foreach (var t in tools)
            {
                if (t == null || string.IsNullOrWhiteSpace(t.Name)) continue;
                sb.Append("- ").Append(t.Name).Append(": ").Append(Trim(t.Description, 90));
                string p = DescribeToolParams(t.Parameters);
                if (!string.IsNullOrWhiteSpace(p)) sb.Append(" 参数:").Append(p);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static string DescribeToolParams(JObject schema)
        {
            if (schema == null) return null;
            var props = schema["properties"] as JObject;
            if (props == null) return null;
            var req = new HashSet<string>();
            var arr = schema["required"] as JArray;
            if (arr != null)
                foreach (var x in arr)
                    if (x != null) req.Add(x.ToString());
            var parts = new List<string>();
            foreach (var p in props.Properties())
            {
                string s = p.Name + (req.Contains(p.Name) ? "*" : "");
                var o = p.Value as JObject;
                var e = o == null ? null : o["enum"] as JArray;
                if (e != null && e.Count > 0)
                {
                    var vals = new List<string>();
                    foreach (var v in e) vals.Add(v.ToString());
                    s += "=" + string.Join("/", vals.ToArray());
                }
                parts.Add(s);
            }
            return string.Join(",", parts.ToArray());
        }
        private static string NormalizePlanToolName(string name)
        {
            string n = (name ?? "").Trim().ToLowerInvariant();
            switch (n)
            {
                case "none":
                case "skip":
                case "noaction": return "no_action";
                case "gift":
                case "give":
                case "give_item": return "gift_item";
                case "silver":
                case "give_silver": return "gift_silver";
                case "relationship":
                case "relate_npc": return "relate";
                case "query_relation":
                case "query_relationships": return "query_relationship";
                case "query_character_info": return "query_person";
                case "set_enmity": return "enmity";
                case "feature": return "add_feature";
                case "kidnap": return "capture";
                case "reverse_practice":
                case "flip_skill": return "flip_practice";
                case "dissolve": return "dissolve_relation";
                case "relation_with_taiwu": return "set_relation";
                case "equip": return "change_equipment";
                case "eat":
                case "consume": return "use_item";
                case "favor": return "adjust_favor";
                case "secret": return "tell_secret";
                default: return n;
            }
        }
        private static string CanonicalizePlanArgs(string name, string argsJson, NpcSnapshot actor = null)
        {
            JObject src;
            try { src = string.IsNullOrWhiteSpace(argsJson) ? new JObject() : JObject.Parse(argsJson); }
            catch { src = new JObject(); }
            var dst = new JObject();
            switch (NormalizePlanToolName(name))
            {
                case "no_action":
                    dst["reason"] = CanonicalString(src["reason"]);
                    break;
                case "remember":
                    dst["content"] = CanonicalString(src["content"]);
                    dst["importance"] = CanonicalInt(src["importance"], 4, 1, 10);
                    break;
                case "query_relationship":
                    dst["target"] = CanonicalPerson(src["target"], "", actor);
                    break;
                case "query_person":
                    dst["name"] = CanonicalPerson(src["name"] ?? src["target"], "", actor);
                    break;
                case "query_health_status":
                    break;
                case "query_secret_recipient":
                    dst["target"] = CanonicalPerson(src["target"], "@taiwu", actor);
                    break;
                case "gift_item":
                    dst["target"] = CanonicalPerson(src["target"], "@taiwu", actor);
                    dst["item"] = CanonicalString(src["item"]);
                    dst["amount"] = CanonicalInt(src["amount"], 1, 1, int.MaxValue);
                    break;
                case "gift_silver":
                    dst["target"] = CanonicalPerson(src["target"], "@taiwu", actor);
                    dst["amount"] = CanonicalInt(src["amount"], 1, 1, int.MaxValue);
                    break;
                case "steal":
                    dst["victim"] = CanonicalPerson(src["victim"], "", actor);
                    dst["item"] = CanonicalString(src["item"]);
                    dst["amount"] = CanonicalInt(src["amount"], 1, 1, int.MaxValue);
                    break;
                case "relate":
                    dst["target"] = CanonicalPerson(src["target"], "", actor);
                    dst["kind"] = CanonicalString(src["kind"]).ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(dst["kind"].ToString())) dst["kind"] = "befriend";
                    break;
                case "enmity":
                    dst["target"] = CanonicalPerson(src["target"], "", actor);
                    dst["make"] = CanonicalBool(src["make"], true);
                    break;
                case "kill":
                case "poison":
                case "capture":
                    dst["target"] = CanonicalPerson(src["target"], "", actor);
                    break;
                case "add_feature":
                    dst["person"] = CanonicalPerson(src["person"], "@self", actor);
                    dst["feature"] = CanonicalString(src["feature"]);
                    break;
                case "flip_practice":
                    dst["person"] = CanonicalPerson(src["person"], "@self", actor);
                    dst["skill"] = CanonicalString(src["skill"]);
                    break;
                case "train_skill":
                    dst["skill"] = CanonicalString(src["skill"]);
                    break;
                case "read_book":
                    dst["book"] = CanonicalString(src["book"]);
                    break;
                case "use_item":
                    dst["item"] = CanonicalString(src["item"] ?? src["name"]);
                    break;
                case "heal":
                case "detox":
                case "regulate_breath":
                    dst["target"] = CanonicalPerson(src["target"], "@taiwu", actor);
                    break;
                case "dissolve_relation":
                    dst["target"] = CanonicalPerson(src["target"], "@taiwu", actor);
                    dst["relation"] = CanonicalString(src["relation"]).ToLowerInvariant();
                    break;
                case "barter":
                    dst["target"] = CanonicalPerson(src["target"], "@taiwu", actor);
                    dst["give_item"] = CanonicalString(src["give_item"]);
                    dst["give_amount"] = CanonicalInt(src["give_amount"], 1, 1, int.MaxValue);
                    dst["receive_item"] = CanonicalString(src["receive_item"]);
                    dst["receive_amount"] = CanonicalInt(src["receive_amount"], 1, 1, int.MaxValue);
                    break;
                case "write_book":
                case "teach":
                    dst["target"] = CanonicalPerson(src["target"],
                        NormalizePlanToolName(name) == "write_book" ? "@taiwu" : "", actor);
                    dst["type"] = CanonicalString(src["type"]).ToLowerInvariant();
                    dst["skill"] = CanonicalString(src["skill"]);
                    if (src["template_id"] != null) dst["template_id"] = CanonicalInt(src["template_id"], -1, -1, short.MaxValue);
                    break;
                case "tell_secret":
                    dst["target"] = CanonicalPerson(src["target"], "@taiwu", actor);
                    dst["index"] = CanonicalInt(src["index"], 0, 0, int.MaxValue);
                    if (src["secret_id"] != null) dst["secret_id"] = CanonicalInt(src["secret_id"], -1, -1, int.MaxValue);
                    if (src["secret_source"] != null) dst["secret_source"] = CanonicalInt(src["secret_source"], 0, 0, int.MaxValue);
                    break;
                case "change_equipment":
                    dst["action"] = CanonicalString(src["action"]).ToLowerInvariant();
                    dst["item"] = CanonicalString(src["item"]);
                    dst["part"] = CanonicalString(src["part"]).ToLowerInvariant();
                    break;
                case "sect_support":
                    break;
                case "adjust_favor":
                    dst["target"] = CanonicalPerson(src["target"], "@taiwu", actor);
                    dst["delta"] = CanonicalInt(src["delta"], 0, -3000, 3000);
                    break;
                case "adjust_mood":
                    dst["target"] = CanonicalPerson(src["target"], "", actor);
                    dst["delta"] = CanonicalInt(src["delta"], 0, -30, 30);
                    dst["reason"] = CanonicalString(src["reason"]);
                    break;
                case "adjust_fame":
                    dst["target"] = CanonicalPerson(src["target"], "", actor);
                    dst["delta"] = CanonicalInt(src["delta"], 0, -12, 12);
                    dst["reason"] = CanonicalString(src["reason"]);
                    break;
                case "spend_night":
                case "matchmake":
                    dst["target"] = CanonicalPerson(src["target"],
                        NormalizePlanToolName(name) == "spend_night" ? "@taiwu" : "", actor);
                    break;
                case "set_relation":
                    dst["action"] = CanonicalString(src["action"]).ToLowerInvariant();
                    break;
                default:
                    dst = new JObject(src);
                    break;
            }
            return CanonicalizeJsonToken(dst).ToString(Newtonsoft.Json.Formatting.None);
        }

        private static string CanonicalString(JToken token)
            => token == null || token.Type == JTokenType.Null ? "" : token.ToString().Trim();

        private static string CanonicalPerson(JToken token, string defaultValue, NpcSnapshot actor)
        {
            string value = CanonicalString(token);
            if (value.Length == 0) return defaultValue ?? "";
            if (LooksLikeTaiwu(value)) return "@taiwu";
            if (LooksLikeSelf(value, actor == null ? null : actor.Name)) return "@self";
            return value;
        }

        private static int CanonicalInt(JToken token, int defaultValue, int min, int max)
        {
            int value = defaultValue;
            if (token != null)
            {
                try { value = token.Value<int>(); }
                catch { int parsed; if (int.TryParse(token.ToString(), out parsed)) value = parsed; }
            }
            return Math.Max(min, Math.Min(max, value));
        }

        private static bool CanonicalBool(JToken token, bool defaultValue)
        {
            if (token == null) return defaultValue;
            try { return token.Value<bool>(); }
            catch { return !string.Equals(token.ToString(), "false", StringComparison.OrdinalIgnoreCase); }
        }

        private static JToken CanonicalizeJsonToken(JToken token)
        {
            var obj = token as JObject;
            if (obj != null)
            {
                var names = new List<string>();
                foreach (var property in obj.Properties()) names.Add(property.Name);
                names.Sort(StringComparer.Ordinal);
                var sorted = new JObject();
                foreach (var propertyName in names)
                    sorted[propertyName] = CanonicalizeJsonToken(obj[propertyName]);
                return sorted;
            }
            var arr = token as JArray;
            if (arr != null)
            {
                var normalized = new JArray();
                foreach (var item in arr) normalized.Add(CanonicalizeJsonToken(item));
                return normalized;
            }
            return token == null ? JValue.CreateNull() : token.DeepClone();
        }

        private static bool ToolResultSucceeded(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            return s.TrimStart().StartsWith("OK:", StringComparison.Ordinal);
        }

        private static bool ToolResultUnknown(string s)
            => !string.IsNullOrWhiteSpace(s) && s.TrimStart().StartsWith("UNKNOWN:", StringComparison.Ordinal);

        private static bool ToolResultNotExecuted(string s)
            => !string.IsNullOrWhiteSpace(s) && s.TrimStart().StartsWith("未执行：", StringComparison.Ordinal);

        private static string ReadMutationFailureCode(string journalPath, string operationId)
        {
            // Export the typed error, not a result paragraph that can contain private dialogue.
            if (string.IsNullOrWhiteSpace(operationId)) return "local_action_rejected";
            try
            {
                lock (JournalIo)
                {
                    var journal = LoadMutationJournal(journalPath);
                    if (journal?.Entries != null)
                        foreach (var entry in journal.Entries)
                            if (entry != null && entry.OperationId == operationId)
                                return string.IsNullOrWhiteSpace(entry.Code) ? entry.Status : entry.Code;
                }
            }
            catch { }
            return "operation_receipt_unavailable";
        }

        private static string SanitizeTrajectoryReason(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string redacted = SecretRedactor.Redact(value).Trim();
            var safe = new StringBuilder(Math.Min(redacted.Length, MaxTrajectoryReasonChars));
            bool previousWasSpace = false;
            bool truncated = false;
            foreach (char c in redacted)
            {
                char normalized = char.IsControl(c) || char.IsWhiteSpace(c) ? ' ' : c;
                if (normalized == ' ' && previousWasSpace) continue;
                if (safe.Length >= MaxTrajectoryReasonChars)
                {
                    truncated = true;
                    break;
                }
                safe.Append(normalized);
                previousWasSpace = normalized == ' ';
            }
            if (truncated && safe.Length > 0) safe[safe.Length - 1] = '…';
            string result = safe.ToString().Trim();
            return result.Length == 0 ? null : result;
        }

        private static string BuildConfirmedMemoryArgs(string originalArgsJson, IList<string> outcomes)
        {
            JObject args;
            try { args = string.IsNullOrWhiteSpace(originalArgsJson) ? new JObject() : JObject.Parse(originalArgsJson); }
            catch { args = new JObject(); }
            var confirmed = new List<string>();
            if (outcomes != null)
                foreach (var raw in outcomes)
                {
                    if (string.IsNullOrWhiteSpace(raw) || !raw.StartsWith("成功:", StringComparison.Ordinal)) continue;
                    confirmed.Add(raw.Substring("成功:".Length).Trim());
                }
            args["content"] = Trim("本月我已确认完成：" + string.Join("；", confirmed.ToArray()), 260);
            return args.ToString(Newtonsoft.Json.Formatting.None);
        }
        private static bool TryClaimResolvedDestructiveStep(CompanionExecutionArbiter executionArbiter,
            string toolName, NpcSnapshot snap, int targetId, List<string> outcomes, Action<string> onResult,
            out string claimKey, out bool claimWasNew)
        {
            claimKey = null;
            claimWasNew = false;
            if (executionArbiter == null) return true;
            if (executionArbiter.TryClaimStep(toolName, snap?.NpcId ?? 0, targetId,
                out claimKey, out claimWasNew, out string conflict)) return true;
            string reason = "共享资源仲裁拒绝:" + (string.IsNullOrWhiteSpace(conflict) ? "目标状态不可用" : conflict);
            onResult?.Invoke("未执行：" + reason);
            return false;
        }

        private static string CompanionRelationshipTargetField(string tool)
            => CompanionMutationTargetPolicy.TargetField(NormalizePlanToolName(tool));

        private static bool CompanionRelationshipDefaultsToTaiwu(string tool)
            => CompanionMutationTargetPolicy.DefaultsToTaiwu(NormalizePlanToolName(tool));

        private static string BuildCompanionRelationshipFact(string actorName, string targetName,
            MonthlyActionPreflight state)
        {
            if (state == null) return "关系读取失败";
            var flags = new List<string>();
            if (state.Spouse) flags.Add("夫妻");
            if (state.Adored) flags.Add("恋人（两情相悦）");
            if (state.Sworn) flags.Add("结义");
            if (state.Friend) flags.Add("好友");
            if (state.Mentor) flags.Add("师徒");
            if (state.Enemy) flags.Add("仇敌");
            string relation = !string.IsNullOrWhiteSpace(state.RelationText)
                ? state.RelationText.Trim()
                : (flags.Count == 0 ? "无已确认特殊关系" : string.Join("、", flags.ToArray()));
            return (string.IsNullOrWhiteSpace(actorName) ? "行动者" : actorName) + "→"
                + (string.IsNullOrWhiteSpace(targetName) ? "目标" : targetName)
                + "；关系=" + relation + "；好感=" + state.ActorFavor
                + "；敌对=" + (state.Enemy ? "是" : "否")
                + "；双方存活=" + (state.ActorAlive && state.TargetAlive ? "是" : "否");
        }

        private static IEnumerator HydrateCompanionRelationshipFact(NpcSnapshot snap,
            CompanionSceneContext scene, int targetId, string targetName, Func<bool> stillCurrent,
            Action<bool, string> done)
        {
            if (snap == null || scene == null || targetId <= 0 || targetId == snap.NpcId)
            {
                done?.Invoke(false, "关系查询对象无效");
                yield break;
            }
            if (targetId == snap.TaiwuId)
            {
                string taiwuFact = RenderAffinityToTaiwu(snap);
                scene.RelationshipFacts[targetId] = taiwuFact;
                done?.Invoke(true, snap.Name + "→太吾；" + taiwuFact);
                yield break;
            }

            MonthlyActionPreflight relation = null;
            bool relationDone = false;
            EffectHandler.QueryMonthlyActionPreflight(snap.NpcId, targetId,
                value =>
                {
                    relation = value;
                    relationDone = true;
                });
            float deadline = Time.unscaledTime + 8f;
            while (!relationDone && Time.unscaledTime < deadline
                && (stillCurrent == null || stillCurrent())) yield return null;
            if (stillCurrent != null && !stillCurrent())
            {
                done?.Invoke(false, "存档或聊天记录已切换");
                yield break;
            }
            string label = string.IsNullOrWhiteSpace(targetName) ? ("#" + targetId) : targetName;
            if (!relationDone || relation == null)
            {
                done?.Invoke(false, "未能可靠读取你与" + label + "的当前关系");
                yield break;
            }
            if (!relation.ActorAlive || !relation.TargetAlive)
            {
                scene.PresentIds.Remove(targetId);
                scene.RemoteIds.Remove(targetId);
                scene.AuthorizedTargetIds.Remove(targetId);
                scene.RelationshipFacts.Remove(targetId);
                scene.PersonSkillFacts.Remove(targetId);
                scene.DisclosableSecretsByRecipient.Remove(targetId);
                done?.Invoke(false, label + "已不在当前角色名册中");
                yield break;
            }
            string fact = BuildCompanionRelationshipFact(snap.Name, label, relation);
            scene.RelationshipFacts[targetId] = fact;
            done?.Invoke(true, fact);
        }

        private static IEnumerator HydrateCompanionTargetSkillFact(NpcSnapshot snap,
            CompanionSceneContext scene, int targetId, string targetName, Func<bool> stillCurrent,
            Action<bool, string> done)
        {
            if (snap == null || scene == null || targetId <= 0 || targetId == snap.NpcId)
            {
                done?.Invoke(false, "受教者无效");
                yield break;
            }
            var targetSkills = new NpcSnapshot { NpcId = targetId, TaiwuId = snap.TaiwuId };
            yield return NpcSnapshotReader.FetchSkills(targetId, targetSkills, stillCurrent,
                includeFlipEligibility: false);
            if (stillCurrent != null && !stillCurrent())
            {
                done?.Invoke(false, "存档或聊天记录已切换");
                yield break;
            }
            yield return NpcSnapshotReader.FetchLifeSkills(targetId, targetSkills, stillCurrent);
            if (stillCurrent != null && !stillCurrent())
            {
                done?.Invoke(false, "存档或聊天记录已切换");
                yield break;
            }
            string label = string.IsNullOrWhiteSpace(targetName) ? ("#" + targetId) : targetName;
            if (!targetSkills.CombatSkillsLoaded || !targetSkills.LifeSkillsLoaded)
            {
                done?.Invoke(false, "未能可靠读取" + label + "已会的武学与技艺");
                yield break;
            }
            scene.PersonSkillFacts[targetId] = targetSkills;
            done?.Invoke(true, label + "已会：武学="
                + ListKnownRelevantSkills(snap.LearnableSkills, targetSkills.LearnableSkills)
                + "；技艺="
                + ListKnownRelevantSkills(snap.LearnableLifeSkills,
                    targetSkills.LearnableLifeSkills));
        }

        private static IEnumerator HydrateCompanionSecretCandidates(NpcSnapshot snap,
            CompanionSceneContext scene, int targetId, string targetName, Func<bool> stillCurrent,
            Action<bool, string> done)
        {
            if (snap == null || scene == null || targetId <= 0 || targetId == snap.NpcId)
            {
                done?.Invoke(false, "秘闻接收者无效");
                yield break;
            }
            List<SecretRef> eligible = null;
            string reason = null;
            yield return NpcSnapshotReader.FetchDisclosableSecrets(snap.NpcId, targetId,
                snap.CurrentDate, (items, why) =>
                {
                    eligible = items;
                    reason = why;
                }, stillCurrent);
            if (stillCurrent != null && !stillCurrent())
            {
                done?.Invoke(false, "存档或聊天记录已切换");
                yield break;
            }
            string label = string.IsNullOrWhiteSpace(targetName) ? ("#" + targetId) : targetName;
            if (eligible == null || eligible.Count == 0)
            {
                scene.DisclosableSecretsByRecipient.Remove(targetId);
                done?.Invoke(true, "当前没有可向" + label + "传播的秘闻；"
                    + (reason ?? "对方已经知情或秘闻已经公开"));
                yield break;
            }
            scene.DisclosableSecretsByRecipient[targetId] = new List<SecretRef>(eligible);
            var lines = new List<string>();
            foreach (SecretRef item in eligible)
                if (item != null)
                    lines.Add(item.DisplayIndex + ":" + Trim(item.Text, 120));
            done?.Invoke(true, "可向" + label + "传播的权威秘闻候选="
                + string.Join("；", lines.ToArray()));
        }

        private static IEnumerator ExecuteCompanionTool(string name, string argsJson, NpcSnapshot snap,
            CompanionSceneContext scene, CompanionEvidenceContext evidence,
            CompanionExecutionArbiter executionArbiter, List<string> outcomes,
            List<StoryProjectionReceipt> storyReceipts,
            int batchEpoch, int generation, long conversationClearEpoch, string batchId, int stepIndex,
            Action<string> onResult, Action<string> onOperationPrepared)
        {
            JObject a; try { a = string.IsNullOrWhiteSpace(argsJson) ? new JObject() : JObject.Parse(argsJson); } catch { a = new JObject(); }
            string S(string k) { var t = a[k]; return t == null ? null : t.ToString(); }
            int I(string k, int def = 0) { var t = a[k]; if (t == null) return def; try { return t.Value<int>(); } catch { int v; return int.TryParse(t.ToString(), out v) ? v : def; } }
            bool B(string k, bool def = false) { var t = a[k]; if (t == null) return def; try { return t.Value<bool>(); } catch { return (t.ToString() ?? "").ToLowerInvariant() != "false"; } }
            bool Current() { return RunIsCurrent(batchEpoch, generation, snap.TaiwuId, snap.NpcId, conversationClearEpoch) && scene != null && scene.IsCurrent(); }

            if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
            string preflightFailure;
            if (!TryPreflightCompanionStep(name, a, snap, out preflightFailure))
            {
                string reason = "前置预查拒绝:" + preflightFailure;
                onResult("未执行：" + reason);
                yield break;
            }

            // The initial monthly context knows the companion's relation to Taiwu, but the
            // candidate roster only carries third-party names and distance.  Do not let the
            // model address or act on a third party until this agent has read that exact pair's
            // authoritative relationship. An explicit action call is already the model's
            // decision, so hydrate the exact pair and continue in this same tool turn. Any
            // unreliable prerequisite still fails closed before a mutation is dispatched.
            string relationshipField = CompanionRelationshipTargetField(name);
            int resolvedActionTarget = 0;
            MonthlyActionPreflight livePairState = null;
            if (relationshipField != null)
            {
                int relationTarget = 0; string relationLabel = null;
                bool defaultTaiwu = CompanionRelationshipDefaultsToTaiwu(name);
                yield return ResolvePerson(snap, scene, S(relationshipField),
                    defaultTaiwu ? snap.TaiwuId : 0, defaultTaiwu ? "太吾" : null, Current,
                    (id, nm) => { relationTarget = id; relationLabel = nm; });
                if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                resolvedActionTarget = relationTarget;
                if (name != "adjust_mood" && name != "adjust_fame"
                    && relationTarget > 0 && relationTarget != snap.NpcId && relationTarget != snap.TaiwuId
                    && (scene.RelationshipFacts == null || !scene.RelationshipFacts.ContainsKey(relationTarget)))
                {
                    bool relationshipReady = false;
                    string relationshipReceipt = null;
                    yield return HydrateCompanionRelationshipFact(snap, scene, relationTarget,
                        relationLabel, Current,
                        (ok, receipt) =>
                        {
                            relationshipReady = ok;
                            relationshipReceipt = receipt;
                        });
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    if (!relationshipReady)
                    {
                        onResult("未执行：代码已自动读取本动作所需的关系前置，但无法可靠确认，"
                            + "所以没有产生任何副作用：" + (relationshipReceipt
                                ?? "当前没有可依赖的关系事实"));
                        yield break;
                    }
                    if (relationshipReady && name == "teach")
                    {
                        bool skillsReady = false;
                        string skillsReceipt = null;
                        yield return HydrateCompanionTargetSkillFact(snap, scene, relationTarget,
                            relationLabel, Current, (ok, receipt) =>
                            {
                                skillsReady = ok;
                                skillsReceipt = receipt;
                            });
                        if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                        if (!skillsReady)
                        {
                            onResult("未执行：代码已自动读取本动作所需的受教者技能前置，但无法可靠确认，"
                                + "所以没有产生任何副作用：" + (skillsReceipt ?? "技能读取失败"));
                            yield break;
                        }
                    }
                    if (relationshipReady && name == "tell_secret")
                    {
                        bool secretsReady = false;
                        string secretReceipt = null;
                        yield return HydrateCompanionSecretCandidates(snap, scene, relationTarget,
                            relationLabel, Current, (ok, receipt) =>
                            {
                                secretsReady = ok;
                                secretReceipt = receipt;
                            });
                        if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                        if (!secretsReady)
                        {
                            onResult("未执行：代码已自动读取本动作所需的秘闻前置，但无法可靠确认，"
                                + "所以没有产生任何副作用：" + (secretReceipt ?? "秘闻读取失败"));
                            yield break;
                        }
                    }
                }

                if (RequiresLivePairState(name) && relationTarget > 0
                    && relationTarget != snap.NpcId)
                {
                    bool livePairDone = false;
                    EffectHandler.QueryMonthlyActionPreflight(snap.NpcId, relationTarget,
                        value => { livePairState = value; livePairDone = true; });
                    float livePairDeadline = Time.unscaledTime + EffectHandler.ReadOnlyQueryWaitSeconds;
                    while (!livePairDone && Time.unscaledTime < livePairDeadline && Current())
                        yield return null;
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    if (!livePairDone || livePairState == null)
                    {
                        onResult("未执行：动作派发前无法可靠读取双方实时位置与状态，"
                            + "因此没有产生任何副作用；请稍后重试或改选其他对象。");
                        yield break;
                    }
                    UpdateLivePairPresence(scene, snap, relationTarget, livePairState);
                    if (RequiresSameLocation(name) && !livePairState.SameValidLocation)
                    {
                        onResult("未执行：动作派发前实时复核发现"
                            + (relationLabel ?? ("#" + relationTarget))
                            + "已不与行动者处于同一有效地块（行动者 "
                            + FormatLiveLocation(livePairState.ActorArea, livePairState.ActorBlock)
                            + "，目标 "
                            + FormatLiveLocation(livePairState.TargetArea, livePairState.TargetBlock)
                            + "）。旧在场名单已失效；请重新选择当前同地块人物，"
                            + "或改用可异地成立的传话/关系/态度行为。");
                        yield break;
                    }
                }
            }

            if (RequiresTaiwuConsent(name, a, resolvedActionTarget, snap.TaiwuId))
            {
                onResult("未执行：这项行为需要太吾本人同意，NPC 过月不能替太吾作出同意；"
                    + "赠物、偷窃、传话等不要求太吾同意的行为仍可正常执行。");
                yield break;
            }

            switch (name)
            {
                case "query_health_status":
                {
                    NpcHealthStatus health = null; bool healthDone = false;
                    EffectHandler.QueryNpcHealthStatus(snap.NpcId,
                        value => { health = value; healthDone = true; });
                    float deadline = Time.unscaledTime + EffectHandler.ReadOnlyQueryWaitSeconds;
                    while (!healthDone && Time.unscaledTime < deadline && Current()) yield return null;
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    if (!healthDone || health == null)
                    { onResult("未执行：未能可靠读取自己的伤势、气息与中毒状态"); yield break; }
                    onResult("OK:自己的身体实况：气血" + health.Health + "/"
                        + health.LeftMaxHealth + "；伤势标记" + health.InjuryMarks
                        + "；气息（内息紊乱）" + health.QiDisorderShow + "，阶段「"
                        + (health.QiDisorderLevel ?? "未知") + "」；中毒："
                        + (health.PoisonSummary ?? "无中毒") + "。");
                    yield break;
                }
                case "query_person":
                case "query_relationship":
                {
                    int target = 0; string label = null;
                    yield return ResolvePerson(snap, scene,
                        S(name == "query_person" ? "name" : "target"), 0, null, Current,
                        (id, nm) => { target = id; label = nm; });
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    if (name == "query_person" && target == snap.NpcId)
                    {
                        yield return NpcSnapshotReader.FetchGiftables(snap.NpcId, snap, Current);
                        if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                        yield return NpcSnapshotReader.FetchSkills(snap.NpcId, snap, Current);
                        if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                        yield return NpcSnapshotReader.FetchStudyProgress(snap.NpcId, snap, Current);
                        if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                        yield return NpcSnapshotReader.FetchLifeSkills(snap.NpcId, snap, Current);
                        if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                        onResult("OK:自己的当前权威候选：持有="
                            + ListGiftables(snap) + "；可换装备=" + ListEquipableInventory(snap)
                            + "；已穿戴=" + ListEquipped(snap)
                            + "；武学=" + ListSkills(snap.LearnableSkills)
                            + "；尚未练满=" + ListTrainableSkills(snap)
                            + "；尚未读完书籍=" + ListReadableBooks(snap)
                            + "；技艺=" + ListSkills(snap.LearnableLifeSkills)
                            + "；可颠倒=" + ListFlippableSkills(snap));
                        yield break;
                    }
                    if (target <= 0)
                    {
                        onResult("未执行：" + (label ?? "无法可靠识别查询对象")
                            + "。请使用在场名单或关系网中的完整姓名或 #角色ID，不要用称呼、姓名加注释或多人合写。");
                        yield break;
                    }
                    if (target == snap.NpcId)
                    { onResult("未执行：不能查询自己与自己的关系；查询本人资料请调用 query_person(name=自己)"); yield break; }
                    if (target == snap.TaiwuId)
                    {
                        string taiwuFact = RenderAffinityToTaiwu(snap);
                        scene.RelationshipFacts[target] = taiwuFact;
                        onResult("OK:权威人物查询：" + snap.Name + "→太吾；" + taiwuFact);
                        yield break;
                    }
                    bool relationshipReady = false;
                    string fact = null;
                    yield return HydrateCompanionRelationshipFact(snap, scene, target, label,
                        Current, (ok, receipt) =>
                        {
                            relationshipReady = ok;
                            fact = receipt;
                        });
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    if (!relationshipReady)
                    { onResult("未执行：" + (fact ?? ("未能可靠读取你与" + (label ?? ("#" + target)) + "的关系，请稍后重查"))); yield break; }
                    string brief = null;
                    string skillsReceipt = null;
                    if (name == "query_person")
                    {
                        yield return NpcSnapshotReader.FetchPersonBrief(target, value => brief = value);
                        if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                        bool skillsReady = false;
                        yield return HydrateCompanionTargetSkillFact(snap, scene, target, label,
                            Current, (ok, receipt) =>
                            {
                                skillsReady = ok;
                                skillsReceipt = receipt;
                            });
                        if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                        // 人物身份、关系、位置与近况已经可靠时，附带技能切片读取失败
                        // 不应把整个 query_person 判成“未执行”，否则模型会反复查同一人。
                        // 仅把亲授资格标为未知；真正 teach 仍会在派发前独立失败关闭。
                        if (!skillsReady) skillsReceipt = skillsReceipt
                            ?? "未能可靠读取对方已会的武学与技艺";
                    }
                    onResult("OK:权威人物查询：" + fact
                        + (string.IsNullOrWhiteSpace(brief) ? "" : ("；近况=" + brief.Trim()))
                        + (name == "query_person" && scene.PersonSkillFacts.TryGetValue(target, out NpcSnapshot known)
                            ? ("；在你可传的武学中，对方已会="
                                + ListKnownRelevantSkills(snap.LearnableSkills, known.LearnableSkills)
                                + "；在你可传的技艺中，对方已会="
                                + ListKnownRelevantSkills(snap.LearnableLifeSkills, known.LearnableLifeSkills))
                            : (name == "query_person" && !string.IsNullOrWhiteSpace(skillsReceipt)
                                ? ("；技能切片暂不可用=" + skillsReceipt
                                    + "（本回执仍可确认身份、关系、位置与近况；不能据此证明可亲授）") : ""))
                        + "。后续对该人的称谓、态度和关系行为必须以此为准，不得把目标回退为太吾。");
                    yield break;
                }
                case "query_secret_recipient":
                {
                    int target = 0; string label = null;
                    yield return ResolvePerson(snap, scene, S("target"), snap.TaiwuId, "太吾", Current,
                        (id, nm) => { target = id; label = nm; });
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    if (target <= 0 || target == snap.NpcId)
                    { onResult("未执行：秘闻接收者必须是可唯一识别的另一名人物"); yield break; }
                    List<SecretRef> eligible = null; string reason = null;
                    yield return NpcSnapshotReader.FetchDisclosableSecrets(snap.NpcId, target,
                        snap.CurrentDate, (items, why) => { eligible = items; reason = why; }, Current);
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    if (eligible == null || eligible.Count == 0)
                    {
                        scene.DisclosableSecretsByRecipient.Remove(target);
                        onResult("OK:当前没有可向" + (label ?? ("#" + target)) + "传播的秘闻；"
                            + (reason ?? "对方已经知情或秘闻已经公开")
                            + "。请换接收者或换行为，不要调用 tell_secret。" );
                        yield break;
                    }
                    scene.DisclosableSecretsByRecipient[target] = new List<SecretRef>(eligible);
                    var text = new StringBuilder("OK:可向" + (label ?? ("#" + target))
                        + "传播的秘闻（tell_secret.target 必须相同，index 照抄原始序号）：\n");
                    foreach (SecretRef secret in eligible)
                        text.Append(secret.DisplayIndex).Append(". ")
                            .Append(Trim(secret.Text, 120)).Append('\n');
                    onResult(text.ToString());
                    yield break;
                }
                case "no_action":
                {
                    string reason = (S("reason") ?? "").Trim();
                    if (reason.Length == 0) reason = "当前权威硬前置不足，无法安全落地任何可用行动";
                    if (outcomes != null) outcomes.Add("未行动:" + reason);
                    onResult("OK:本月无落地行动:" + reason);
                    yield break;
                }
                case "remember":
                {
                    string content = (S("content") ?? "").Trim();
                    if (content.Length == 0) { onResult("未成:记忆为空"); yield break; }
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    try
                    {
                        var mem = NpcMemoryStore.Load(JianghuYoulingPaths.Memories, snap.TaiwuId.ToString(), snap.NpcId.ToString());
                        if (!Current()) throw new OperationCanceledException("存档或聊天记录已切换");
                        int importance = MemoryTrustPolicy.ClampModelImportance(I("importance", 4), 4);
                        mem.Add(MemoryTrustPolicy.SanitizeModelMemory(new MemoryEntry
                        {
                            Content = content, Type = MemoryType.Event, Keywords = "同道主动行事",
                            Importance = importance, WorldDate = snap.CurrentDate,
                            SourceKind = "companion_monthly_note",
                            SourceId = OperationId.FromStableKey("companion-local-story|"
                                + WorldLifecycle.WorldId + "|" + batchId + "|" + stepIndex + "|remember"),
                        }, 4));
                        mem.Prune(snap.CurrentDate);
                        if (!Current()) throw new OperationCanceledException("存档或聊天记录已切换");
                        if (!mem.Save()) throw new InvalidOperationException("记忆文件已被清空或不可可靠读取");
                        if (!Current()) throw new OperationCanceledException("存档或聊天记录已切换");
                        AddOutcome(outcomes, true, snap.Name + "记住了此事");
                        AddStoryReceipt(storyReceipts, "remember",
                            OperationId.FromStableKey("companion-local-story|" + WorldLifecycle.WorldId + "|" + batchId + "|" + stepIndex + "|remember"),
                            snap.NpcId, snap.NpcId, snap.Name, snap.Name, "", snap.Name + "记住了此事");
                        // remember 是本地副作用，不会生成自己的后端 operation journal。必须在返回成功前
                        // 把它并入本批已有的 durable 投影 checkpoint，避免协程下一帧前退出时只留下前序动作。
                        if (!CheckpointCompanionBatchOutcomes(MutationJournalPath(WorldLifecycle.WorldId), batchId,
                            outcomes, storyReceipts))
                        {
                            AddOutcome(outcomes, false, "记忆已写入，但完整批次投影 checkpoint 失败");
                            onResult("UNKNOWN:记忆已写入，但完整批次投影尚未可靠提交");
                            yield break;
                        }
                        onResult("OK:已写入记忆");
                    }
                    catch (OperationCanceledException) { onResult("未成:存档或聊天记录已切换"); }
                    catch (Exception e) { AddOutcome(outcomes, false, "记忆写入失败:" + e.GetType().Name); onResult("未成:记忆写入失败"); }
                    yield break;
                }
                case "send_message":
                {
                    int target = 0; string label = null;
                    yield return ResolvePerson(snap, scene, S("target"), 0, null, Current,
                        (id, nm) => { target = id; label = nm; });
                    string content = Trim((S("content") ?? "").Trim(), 260);
                    if (target <= 0 || target == snap.NpcId || content.Length == 0)
                    { onResult("未执行：传话对象或内容无效"); yield break; }
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    string operationId = OperationId.FromStableKey("companion-local-story|" + WorldLifecycle.WorldId
                        + "|" + batchId + "|" + stepIndex + "|send_message|" + target);
                    string mode = livePairState != null && livePairState.SameValidLocation
                        ? "当面告知" : "千里传音";
                    if (CommunicationContentContradictsMode(content, mode, out string contradiction))
                    {
                        onResult("未执行：说话内容与代码按实时位置判定的交流方式冲突（"
                            + contradiction + "）。请保留实际话意并重新措辞，"
                            + "不要在 content 中自行声称当面或传音。");
                        yield break;
                    }
                    string summary = snap.Name + "向" + (label ?? ("#" + target)) + mode + "：“" + content + "”";
                    try
                    {
                        if (target != snap.TaiwuId)
                        {
                            var targetMemory = NpcMemoryStore.Load(JianghuYoulingPaths.Memories,
                                snap.TaiwuId.ToString(), target.ToString());
                            if (!targetMemory.LoadReliable) throw new InvalidOperationException("目标记忆不可可靠读取");
                            MemoryEntry stored = targetMemory.Add(MemoryTrustPolicy.SanitizeModelMemory(new MemoryEntry
                            {
                                Content = snap.Name + "曾" + mode + "对我说：“" + content + "”",
                                Type = MemoryType.Event,
                                Keywords = "同道传话," + snap.Name,
                                Importance = 5,
                                WorldDate = snap.CurrentDate,
                                SourceKind = "companion_message",
                                SourceId = operationId,
                            }, 5));
                            if (stored == null || !targetMemory.Save()) throw new InvalidOperationException("目标记忆保存失败");
                        }
                        if (!Current()) throw new OperationCanceledException("存档或聊天记录已切换");
                        AddOutcome(outcomes, true, summary);
                        AddStoryReceipt(storyReceipts, "send_message", operationId, snap.NpcId, target,
                            snap.Name, label, content, summary);
                        onOperationPrepared?.Invoke(operationId);
                        onResult("OK:" + summary);
                    }
                    catch (OperationCanceledException) { onResult("未成:存档或聊天记录已切换"); }
                    catch (Exception e)
                    {
                        AddOutcome(outcomes, false, "传话写入失败:" + e.GetType().Name);
                        onResult("未成:传话未能可靠送达");
                    }
                    yield break;
                }
                case "spend_night":
                {
                    int target = 0; string label = null;
                    yield return ResolvePerson(snap, scene, S("target"), snap.TaiwuId, "太吾", Current,
                        (id, nm) => { target = id; label = nm; });
                    if (target <= 0 || target == snap.NpcId)
                    { onResult("未执行：春宵对象不能是自己且必须可唯一识别"); yield break; }
                    yield return ExecuteBooleanCompanionMutation(name, a, "target", target, label, "春宵",
                        snap.Name + "与" + (label ?? "对方") + "两情相悦，共度春宵", "春宵未成", snap, scene,
                        outcomes, storyReceipts, batchEpoch, generation, conversationClearEpoch, batchId,
                        stepIndex, onResult, onOperationPrepared,
                        (done, operationId) => EffectHandler.ApplySpendNightBetween(
                            snap.NpcId, target, snap.TaiwuId, done, operationId));
                    yield break;
                }
                case "matchmake":
                {
                    int target = 0; string label = null;
                    yield return ResolvePerson(snap, scene, S("target"), 0, null, Current,
                        (id, nm) => { target = id; label = nm; });
                    if (target <= 0 || target == snap.NpcId)
                    { onResult("未执行：婚配对象不能是自己且必须可唯一识别"); yield break; }
                    yield return ExecuteBooleanCompanionMutation(name, a, "target", target, label, "spouse",
                        snap.Name + "与" + (label ?? "对方") + "结为夫妻", "婚配未成", snap, scene,
                        outcomes, storyReceipts, batchEpoch, generation, conversationClearEpoch, batchId,
                        stepIndex, onResult, onOperationPrepared,
                        (done, operationId) => EffectHandler.ApplyMatchmake(snap.NpcId, target, done, operationId));
                    yield break;
                }
                case "set_relation":
                {
                    string action = (S("action") ?? "").Trim().ToLowerInvariant();
                    string asset = action;
                    string verb;
                    Action<Action<bool, string>, string> dispatch;
                    switch (action)
                    {
                        case "befriend": verb = "与太吾结为挚友"; dispatch = (done, op) => EffectHandler.ApplyRelation(snap.NpcId, snap.TaiwuId, "best_friend", done, op); break;
                        case "swear_sibling": verb = "与太吾义结金兰"; dispatch = (done, op) => EffectHandler.ApplyRelation(snap.NpcId, snap.TaiwuId, "sworn_sibling", done, op); break;
                        case "apprentice": verb = "拜太吾为师"; dispatch = (done, op) => EffectHandler.ApplyRelation(snap.NpcId, snap.TaiwuId, "apprentice", done, op); break;
                        case "take_disciple": verb = "收太吾为徒"; dispatch = (done, op) => EffectHandler.ApplyRelation(snap.NpcId, snap.TaiwuId, "take_disciple", done, op); break;
                        case "lover": verb = "向太吾表达爱慕"; dispatch = (done, op) => EffectHandler.ApplyRelation(snap.NpcId, snap.TaiwuId, "lover", done, op); break;
                        case "spouse": verb = "与太吾结为夫妻"; dispatch = (done, op) => EffectHandler.ApplyRelation(snap.NpcId, snap.TaiwuId, "spouse", done, op); break;
                        case "recognize": verb = "归心太吾"; dispatch = (done, op) => EffectHandler.ApplyRecognizeTaiwu(snap.NpcId, done, op); break;
                        case "follow": verb = "加入太吾队伍"; dispatch = (done, op) => EffectHandler.ApplyFollow(snap.NpcId, true, done, op); break;
                        default: onResult("未执行：关系动作无效"); yield break;
                    }
                    yield return ExecuteBooleanCompanionMutation(name, a, "target", snap.TaiwuId, "太吾", asset,
                        snap.Name + verb, "关系变化失败", snap, scene, outcomes, storyReceipts, batchEpoch,
                        generation, conversationClearEpoch, batchId, stepIndex, onResult, onOperationPrepared, dispatch);
                    yield break;
                }
                case "gift_item":
                {
                    if (snap.HoldingsLoaded && !HasGiftableItem(snap)) { onResult("未执行：身上没有可赠之物"); yield break; }
                    int target = 0; string label = null;
                    yield return ResolvePerson(snap, scene, S("target"), snap.TaiwuId, "太吾", Current, (id, nm) => { target = id; label = nm; });
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    string item = (S("item") ?? "").Trim(); int amount = Math.Max(1, I("amount", 1));
                    if (target <= 0 || target == snap.NpcId || item.Length == 0) { onResult("未执行：不能把物品赠给自己，且物品必须有效"); yield break; }
                    int[] actual = { -1 }; string[] msg = { null };
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    var lease = TryBeginToolMutationLease(batchEpoch, Current, snap, scene, name, a, "target", target,
                        outcomes, onResult, batchId, stepIndex, conversationClearEpoch, onOperationPrepared);
                    if (lease == null) yield break;
                    try
                    {
                        EffectHandler.ApplyGiveItemByName(snap.NpcId, snap.TaiwuId, item, amount, (n, m) =>
                        {
                            try { actual[0] = n; msg[0] = m; }
                            finally { lease.CompleteLegacy(n > 0, m); }
                        }, target == snap.TaiwuId ? 0 : target, lease.OperationId);
                    }
                    catch (Exception e) { actual[0] = 0; msg[0] = "RPC派发异常:" + e.GetType().Name; lease.CompleteDispatchException(e); }
                    yield return WaitRpc(() => actual[0] >= 0, lease);
                    if (lease.IsUnknown) { CompleteUnknownStepProjection(lease, outcomes, name, "后端结果未知；本批停止且不会自动重试", onResult); yield break; }
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    bool ok = actual[0] > 0;
                    string text = ok ? (snap.Name + "赠给" + (label ?? "对方") + "「" + item + "」x" + actual[0]) : ("赠物失败:" + (msg[0] ?? "对方没有此物或转移失败"));
                    if (CompleteMutationStepProjection(lease, outcomes, ok, text, onResult, item) && ok)
                        AddStoryReceipt(storyReceipts, name, lease.OperationId, snap.NpcId, target,
                            snap.Name, label, item, text);
                    yield break;
                }
                case "gift_silver":
                {
                    if (snap.InventoryLoaded && !HasSilver(snap)) { onResult("未执行：身上没有银钱"); yield break; }
                    int target = 0; string label = null;
                    yield return ResolvePerson(snap, scene, S("target"), snap.TaiwuId, "太吾", Current, (id, nm) => { target = id; label = nm; });
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    int amount = Math.Max(1, I("amount", 0));
                    if (target <= 0 || target == snap.NpcId || amount <= 0) { onResult("未执行：不能把银钱赠给自己，且数额必须有效"); yield break; }
                    int[] actual = { -1 }; string[] msg = { null };
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    var lease = TryBeginToolMutationLease(batchEpoch, Current, snap, scene, name, a, "target", target,
                        outcomes, onResult, batchId, stepIndex, conversationClearEpoch, onOperationPrepared);
                    if (lease == null) yield break;
                    try
                    {
                        EffectHandler.ApplyGiveSilver(snap.NpcId, snap.TaiwuId, amount, (n, m) =>
                        {
                            try { actual[0] = n; msg[0] = m; }
                            finally { lease.CompleteLegacy(n > 0, m); }
                        }, target == snap.TaiwuId ? 0 : target, lease.OperationId);
                    }
                    catch (Exception e) { actual[0] = 0; msg[0] = "RPC派发异常:" + e.GetType().Name; lease.CompleteDispatchException(e); }
                    yield return WaitRpc(() => actual[0] >= 0, lease);
                    if (lease.IsUnknown) { CompleteUnknownStepProjection(lease, outcomes, name, "后端结果未知；本批停止且不会自动重试", onResult); yield break; }
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    bool ok = actual[0] > 0;
                    string text = ok ? (snap.Name + "赠给" + (label ?? "对方") + "银钱" + actual[0]) : ("赠银失败:" + (msg[0] ?? "银钱不足"));
                    if (CompleteMutationStepProjection(lease, outcomes, ok, text, onResult,
                        "银钱" + actual[0]) && ok)
                        AddStoryReceipt(storyReceipts, name, lease.OperationId, snap.NpcId, target,
                            snap.Name, label, "银钱" + actual[0], text);
                    yield break;
                }
                case "steal":
                {
                    int victim = 0; string label = null;
                    yield return ResolvePerson(snap, scene, S("victim"), 0, null, Current, (id, nm) => { victim = id; label = nm; });
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    string item = (S("item") ?? "").Trim(); int amount = Math.Max(1, I("amount", 1));
                    if (victim <= 0 || victim == snap.NpcId || item.Length == 0) { onResult("未执行：不能偷窃自己，且物品必须有效"); yield break; }
                    NpcSnapshot victimInventory = null;
                    yield return FetchInventoryForPreflight(victim, Current, value => victimInventory = value);
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    GiftableItem victimItem;
                    if (victimInventory != null && victimInventory.HoldingsLoaded
                        && !TryFindHolding(victimInventory.GiftableItems, item, amount, out victimItem))
                    {
                        string why = "偷窃前置预查拒绝:" + (label ?? ("#" + victim))
                            + "当前权威持有清单里没有「" + item + "」x" + amount
                            + "；可见持有物:" + ListGiftables(victimInventory);
                        onResult("未执行：" + why); yield break;
                    }
                    int[] got = { -1 }, chance = { 0 }; bool[] detected = { false }; string[] real = { null }, msg = { null };
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    if (!TryClaimResolvedDestructiveStep(executionArbiter, name, snap, victim,
                        outcomes, onResult, out string claimKey, out bool claimWasNew)) yield break;
                    var lease = TryBeginToolMutationLease(batchEpoch, Current, snap, scene, name, a, "victim", victim,
                        outcomes, onResult, batchId, stepIndex, conversationClearEpoch, onOperationPrepared);
                    if (lease == null)
                    {
                        if (claimWasNew) executionArbiter?.ReleaseUncommittedClaim(claimKey, snap.NpcId);
                        yield break;
                    }
                    try
                    {
                        EffectHandler.ApplySteal(snap.NpcId, victim, item, amount, (ok, n, rn, m, det, ch) =>
                        {
                            try { got[0] = ok ? n : 0; real[0] = rn; msg[0] = m; detected[0] = det; chance[0] = ch; }
                            finally { lease.CompleteLegacy(ok, m); }
                        }, lease.OperationId);
                    }
                    catch (Exception e) { got[0] = 0; msg[0] = "RPC派发异常:" + e.GetType().Name; lease.CompleteDispatchException(e); }
                    yield return WaitRpc(() => got[0] >= 0, lease);
                    if (lease.IsUnknown) { CompleteUnknownStepProjection(lease, outcomes, name, "后端结果未知；本批停止且不会自动重试", onResult); yield break; }
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    bool ok2 = got[0] > 0;
                    if (!ok2 && claimWasNew) executionArbiter?.ReleaseUncommittedClaim(claimKey, snap.NpcId);
                    string text = ok2 ? (snap.Name + "偷得" + (label ?? "对方") + "的「"
                        + (real[0] ?? item) + "」x" + got[0]
                        + (detected[0] ? "，但当场败露" : ""))
                        : ("偷窃失败:" + (detected[0] ? "败露" : (msg[0] ?? "未能得手"))
                            + ",成功率约" + chance[0] + "%");
                    if (CompleteMutationStepProjection(lease, outcomes, ok2, text, onResult,
                        real[0] ?? item) && ok2)
                        AddStoryReceipt(storyReceipts, name, lease.OperationId, snap.NpcId, victim,
                            snap.Name, label, real[0] ?? item, text);
                    yield break;
                }
                case "poison":
                {
                    int target = 0; string label = null;
                    yield return ResolvePerson(snap, scene, S("target"), 0, null, Current, (id, nm) => { target = id; label = nm; });
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    if (target <= 0 || target == snap.NpcId) { onResult("未执行：不能向自己下毒，且目标必须可唯一识别"); yield break; }
                    string pairRefusal = null;
                    yield return PreflightCompanionPair(name, a, snap, target, Current,
                        reason => pairRefusal = reason);
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    if (!string.IsNullOrWhiteSpace(pairRefusal))
                    { onResult("未执行：权威前置预查拒绝:" + pairRefusal); yield break; }
                    int[] state = { -1 }; string[] msg = { null };
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    if (!TryClaimResolvedDestructiveStep(executionArbiter, name, snap, target,
                        outcomes, onResult, out string claimKey, out bool claimWasNew)) yield break;
                    var lease = TryBeginToolMutationLease(batchEpoch, Current, snap, scene, name, a, "target", target,
                        outcomes, onResult, batchId, stepIndex, conversationClearEpoch, onOperationPrepared);
                    if (lease == null)
                    {
                        if (claimWasNew) executionArbiter?.ReleaseUncommittedClaim(claimKey, snap.NpcId);
                        yield break;
                    }
                    try
                    {
                        EffectHandler.ApplyMonthlyPoison(snap.NpcId, target, S("poison_type"), (ok, m) =>
                        {
                            try { state[0] = ok ? 1 : 0; msg[0] = m; }
                            finally { lease.CompleteLegacy(ok, m); }
                        }, lease.OperationId);
                    }
                    catch (Exception e) { state[0] = 0; msg[0] = "RPC派发异常:" + e.GetType().Name; lease.CompleteDispatchException(e); }
                    yield return WaitRpc(() => state[0] >= 0, lease);
                    if (lease.IsUnknown) { CompleteUnknownStepProjection(lease, outcomes, name, "后端结果未知；本批停止且不会自动重试", onResult); yield break; }
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    if (state[0] != 1 && claimWasNew) executionArbiter?.ReleaseUncommittedClaim(claimKey, snap.NpcId);
                    string poisonName = msg[0] ?? S("poison_type") ?? "毒";
                    string text = state[0] == 1 ? (snap.Name + "向" + (label ?? "目标") + "下" + poisonName + "得手") : ("下毒失败:" + (msg[0] ?? "精纯不及或目标无效"));
                    if (CompleteMutationStepProjection(lease, outcomes, state[0] == 1, text, onResult, poisonName) && state[0] == 1)
                        AddStoryReceipt(storyReceipts, name, lease.OperationId, snap.NpcId, target,
                            snap.Name, label, poisonName, text);
                    yield break;
                }
                case "relate":
                {
                    int target = 0; string label = null;
                    yield return ResolvePerson(snap, scene, S("target"), 0, null, Current, (id, nm) => { target = id; label = nm; });
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    string kind = (S("kind") ?? "").Trim().ToLowerInvariant();
                    if (target <= 0 || target == snap.NpcId || !IsRelationKind(kind)) { onResult("未执行：结缘对象或关系类型无效"); yield break; }
                    yield return ExecuteBooleanCompanionMutation(name, a, "target", target, label, kind,
                        snap.Name + "与" + (label ?? "对方") + "结成"
                            + StoryProjectionValidator.RelationshipDisplayName(kind, false), "结缘失败",
                        snap, scene, outcomes, storyReceipts, batchEpoch, generation,
                        conversationClearEpoch, batchId, stepIndex, onResult, onOperationPrepared,
                        (done, operationId) => EffectHandler.ApplyRelateNpc(snap.NpcId, target, kind,
                            done, operationId));
                    yield break;
                }
                case "enmity":
                {
                    int target = 0; string label = null;
                    yield return ResolvePerson(snap, scene, S("target"), 0, null, Current, (id, nm) => { target = id; label = nm; });
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    bool make = B("make", true);
                    if (target <= 0 || target == snap.NpcId) { onResult("未执行：不能与自己结仇或和解，且对象必须可唯一识别"); yield break; }
                    string relationChange = make ? "结仇" : "化解";
                    yield return ExecuteBooleanCompanionMutation(name, a, "target", target, label,
                        relationChange,
                        snap.Name + (make ? "与" : "同") + (label ?? "对方")
                            + (make ? "结下仇怨" : "化解旧怨"),
                        "恩怨失败", snap, scene, outcomes, storyReceipts, batchEpoch, generation,
                        conversationClearEpoch, batchId, stepIndex, onResult, onOperationPrepared,
                        (done, operationId) => EffectHandler.ApplyEnmity(snap.NpcId, target, make,
                            done, operationId));
                    yield break;
                }
                case "kill":
                case "capture":
                {
                    int target = 0; string label = null;
                    yield return ResolvePerson(snap, scene, S("target"), 0, null, Current, (id, nm) => { target = id; label = nm; });
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    if (target <= 0 || target == snap.NpcId) { onResult("未执行：不能把自己作为取命或擒拿目标，且目标必须可唯一识别"); yield break; }
                    string pairRefusal = null;
                    yield return PreflightCompanionPair(name, a, snap, target, Current,
                        reason => pairRefusal = reason);
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    if (!string.IsNullOrWhiteSpace(pairRefusal))
                    { onResult("未执行：权威前置预查拒绝:" + pairRefusal); yield break; }
                    int[] state = { -1 }; string[] msg = { null }; string[] lootName = { null };
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    if (!TryClaimResolvedDestructiveStep(executionArbiter, name, snap, target,
                        outcomes, onResult, out string claimKey, out bool claimWasNew)) yield break;
                    var lease = TryBeginToolMutationLease(batchEpoch, Current, snap, scene, name, a, "target", target,
                        outcomes, onResult, batchId, stepIndex, conversationClearEpoch, onOperationPrepared);
                    if (lease == null)
                    {
                        if (claimWasNew) executionArbiter?.ReleaseUncommittedClaim(claimKey, snap.NpcId);
                        yield break;
                    }
                    try
                    {
                        Action<bool, string> callback = (ok, m) =>
                        {
                            try { state[0] = ok ? 1 : 0; msg[0] = m; }
                            finally { lease.CompleteLegacy(ok, m); }
                        };
                        if (name == "kill")
                            EffectHandler.ApplyMonthlyKillDetailed(snap.NpcId, target, (ok, m, loot) =>
                            {
                                lootName[0] = loot;
                                callback(ok, m);
                            }, lease.OperationId);
                        else EffectHandler.ApplyMonthlyCapture(snap.NpcId, target, callback, lease.OperationId);
                    }
                    catch (Exception e) { state[0] = 0; msg[0] = "RPC派发异常:" + e.GetType().Name; lease.CompleteDispatchException(e); }
                    yield return WaitRpc(() => state[0] >= 0, lease);
                    if (lease.IsUnknown) { CompleteUnknownStepProjection(lease, outcomes, name, "后端结果未知；本批停止且不会自动重试", onResult); yield break; }
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    if (state[0] != 1 && claimWasNew) executionArbiter?.ReleaseUncommittedClaim(claimKey, snap.NpcId);
                    string verb = name == "kill" ? "取命" : "擒拿";
                    string loot = name == "kill" ? lootName[0] : null;
                    string text = state[0] == 1
                        ? (snap.Name + "对" + (label ?? "目标") + verb + "得手"
                            + (string.IsNullOrWhiteSpace(loot) ? "" : ("，并夺得「" + loot + "」")))
                        : (verb + "失败:" + (msg[0] ?? "精纯不及或目标无效"));
                    if (CompleteMutationStepProjection(lease, outcomes, state[0] == 1, text, onResult,
                        loot ?? "") && state[0] == 1)
                        AddStoryReceipt(storyReceipts, name, lease.OperationId, snap.NpcId, target,
                            snap.Name, label, loot ?? "", text);
                    yield break;
                }
                case "add_feature":
                {
                    int person = 0; string label = null;
                    yield return ResolvePerson(snap, scene, S("person"), snap.NpcId, "自己", Current, (id, nm) => { person = id; label = nm; });
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    string feature = (S("feature") ?? "").Trim();
                    if (person != snap.NpcId || feature.Length == 0) { onResult("未执行：只能让自己长进品性，且品性必须有效"); yield break; }
                    int[] state = { -1 }; string[] msg = { null };
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    var lease = TryBeginToolMutationLease(batchEpoch, Current, snap, scene, name, a, "person", person,
                        outcomes, onResult, batchId, stepIndex, conversationClearEpoch, onOperationPrepared);
                    if (lease == null) yield break;
                    try
                    {
                        EffectHandler.ApplyAddFeature(person, feature, (ok, m) =>
                        {
                            try { state[0] = ok ? 1 : 0; msg[0] = m; }
                            finally { lease.CompleteLegacy(ok, m); }
                        }, lease.OperationId);
                    }
                    catch (Exception e) { state[0] = 0; msg[0] = "RPC派发异常:" + e.GetType().Name; lease.CompleteDispatchException(e); }
                    yield return WaitRpc(() => state[0] >= 0, lease);
                    if (lease.IsUnknown) { CompleteUnknownStepProjection(lease, outcomes, name, "后端结果未知；本批停止且不会自动重试", onResult); yield break; }
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    string text = state[0] == 1 ? ((label ?? "此人") + "性情长进:「" + (msg[0] ?? feature) + "」") : ("性情长进失败:" + (msg[0] ?? "不合适或已有"));
                    if (CompleteMutationStepProjection(lease, outcomes, state[0] == 1, text, onResult,
                        msg[0] ?? feature) && state[0] == 1)
                        AddStoryReceipt(storyReceipts, name, lease.OperationId, snap.NpcId, person,
                            snap.Name, snap.Name, msg[0] ?? feature, text);
                    yield break;
                }
                case "flip_practice":
                {
                    int person = 0; string label = null;
                    yield return ResolvePerson(snap, scene, S("person"), snap.NpcId, "自己", Current, (id, nm) => { person = id; label = nm; });
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    string skill = (S("skill") ?? "").Trim();
                    if (person != snap.NpcId || skill.Length == 0) { onResult("未执行：只能颠倒自己的已学功法"); yield break; }
                    int[] state = { -1 }, count = { 0 }; string[] real = { null }, before = { null }, after = { null }, msg = { null };
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    var lease = TryBeginToolMutationLease(batchEpoch, Current, snap, scene, name, a, "person", person,
                        outcomes, onResult, batchId, stepIndex, conversationClearEpoch, onOperationPrepared);
                    if (lease == null) yield break;
                    try
                    {
                        EffectHandler.ApplyFlipPractice(person, skill, (ok, n, b, aft, c, m) =>
                        {
                            try { state[0] = ok ? 1 : 0; real[0] = n; before[0] = b; after[0] = aft; count[0] = c; msg[0] = m; }
                            finally { lease.CompleteLegacy(ok, m); }
                        }, lease.OperationId);
                    }
                    catch (Exception e) { state[0] = 0; msg[0] = "RPC派发异常:" + e.GetType().Name; lease.CompleteDispatchException(e); }
                    yield return WaitRpc(() => state[0] >= 0, lease);
                    if (lease.IsUnknown) { CompleteUnknownStepProjection(lease, outcomes, name, "后端结果未知；本批停止且不会自动重试", onResult); yield break; }
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    string text = state[0] == 1 ? ((label ?? "此人") + "将「" + (real[0] ?? skill) + "」由" + (before[0] ?? "?") + "改作" + (after[0] ?? "?") + ",颠倒" + count[0] + "页") : ("正逆练颠倒失败:" + (msg[0] ?? "未学会或无可颠倒页"));
                    if (CompleteMutationStepProjection(lease, outcomes, state[0] == 1, text, onResult,
                        real[0] ?? skill) && state[0] == 1)
                        AddStoryReceipt(storyReceipts, name, lease.OperationId, snap.NpcId, person,
                            snap.Name, snap.Name, real[0] ?? skill, text);
                    yield break;
                }
                case "train_skill":
                {
                    string skill = (S("skill") ?? "").Trim();
                    if (skill.Length == 0)
                    { onResult("未执行：必须指定自己已经学会的确切武学"); yield break; }
                    int[] state = { -1 }, beforePages = { 0 }, afterPages = { 0 };
                    string[] real = { null }, direction = { null }, msg = { null };
                    var lease = TryBeginToolMutationLease(batchEpoch, Current, snap, scene,
                        name, a, null, snap.NpcId, outcomes, onResult, batchId,
                        stepIndex, conversationClearEpoch, onOperationPrepared);
                    if (lease == null) yield break;
                    try
                    {
                        EffectHandler.ApplyNpcTrainSkill(snap.NpcId, skill,
                            (ok, n, before, after, dir, m) =>
                            {
                                try
                                {
                                    state[0] = ok ? 1 : 0; real[0] = n;
                                    beforePages[0] = before; afterPages[0] = after;
                                    direction[0] = dir; msg[0] = m;
                                }
                                finally { lease.CompleteLegacy(ok, m); }
                            }, lease.OperationId);
                    }
                    catch (Exception e)
                    {
                        state[0] = 0; msg[0] = "RPC派发异常:" + e.GetType().Name;
                        lease.CompleteDispatchException(e);
                    }
                    yield return WaitRpc(() => state[0] >= 0, lease);
                    if (lease.IsUnknown)
                    {
                        CompleteUnknownStepProjection(lease, outcomes, name,
                            "后端结果未知；本批停止且不会自动重试", onResult);
                        yield break;
                    }
                    string text = state[0] == 1
                        ? snap.Name + "将「" + (real[0] ?? skill) + "」修炼完整，研读 "
                            + beforePages[0] + "→" + afterPages[0] + "页，完成"
                            + (direction[0] ?? "原练法") + "突破"
                        : "修炼失败:" + (msg[0] ?? "未学会这门武学");
                    if (CompleteMutationStepProjection(lease, outcomes, state[0] == 1,
                        text, onResult, real[0] ?? skill) && state[0] == 1)
                        AddStoryReceipt(storyReceipts, name, lease.OperationId,
                            snap.NpcId, snap.NpcId, snap.Name, snap.Name,
                            real[0] ?? skill, text);
                    yield break;
                }
                case "read_book":
                {
                    string book = (S("book") ?? "").Trim();
                    if (book.Length == 0)
                    { onResult("未执行：必须指定自己背包中真实持有的确切书名"); yield break; }
                    int[] state = { -1 }, beforePages = { 0 }, afterPages = { 0 };
                    string[] real = { null }, kind = { null }, msg = { null };
                    var lease = TryBeginToolMutationLease(batchEpoch, Current, snap, scene,
                        name, a, null, snap.NpcId, outcomes, onResult, batchId,
                        stepIndex, conversationClearEpoch, onOperationPrepared);
                    if (lease == null) yield break;
                    try
                    {
                        EffectHandler.ApplyNpcReadBook(snap.NpcId, book,
                            (ok, n, type, before, after, m) =>
                            {
                                try
                                {
                                    state[0] = ok ? 1 : 0; real[0] = n; kind[0] = type;
                                    beforePages[0] = before; afterPages[0] = after; msg[0] = m;
                                }
                                finally { lease.CompleteLegacy(ok, m); }
                            }, lease.OperationId);
                    }
                    catch (Exception e)
                    {
                        state[0] = 0; msg[0] = "RPC派发异常:" + e.GetType().Name;
                        lease.CompleteDispatchException(e);
                    }
                    yield return WaitRpc(() => state[0] >= 0, lease);
                    if (lease.IsUnknown)
                    {
                        CompleteUnknownStepProjection(lease, outcomes, name,
                            "后端结果未知；本批停止且不会自动重试", onResult);
                        yield break;
                    }
                    string text = state[0] == 1
                        ? snap.Name + "读完" + (kind[0] ?? "") + "书「"
                            + (real[0] ?? book) + "」，进度 "
                            + beforePages[0] + "→" + afterPages[0] + "页"
                        : "读书失败:" + (msg[0] ?? "背包中没有此书");
                    if (CompleteMutationStepProjection(lease, outcomes, state[0] == 1,
                        text, onResult, real[0] ?? book) && state[0] == 1)
                        AddStoryReceipt(storyReceipts, name, lease.OperationId,
                            snap.NpcId, snap.NpcId, snap.Name, snap.Name,
                            real[0] ?? book, text);
                    yield break;
                }
                case "use_item":
                {
                    string item = (S("item") ?? S("name") ?? string.Empty).Trim();
                    if (item.Length == 0)
                    { onResult("未执行：必须指定当前可自行使用候选中的确切物品名"); yield break; }
                    int[] state = { -1 }, amount = { 0 }, before = { 0 }, after = { 0 };
                    string[] real = { null }, kind = { null }, msg = { null };
                    var lease = TryBeginToolMutationLease(batchEpoch, Current, snap, scene,
                        name, a, null, snap.NpcId, outcomes, onResult, batchId,
                        stepIndex, conversationClearEpoch, onOperationPrepared);
                    if (lease == null) yield break;
                    try
                    {
                        EffectHandler.ApplyNpcUseItem(snap.NpcId, item,
                            (ok, n, type, used, beforeCount, afterCount, m) =>
                            {
                                try
                                {
                                    state[0] = ok ? 1 : 0; real[0] = n; kind[0] = type;
                                    amount[0] = used; before[0] = beforeCount;
                                    after[0] = afterCount; msg[0] = m;
                                }
                                finally { lease.CompleteLegacy(ok, m); }
                            }, lease.OperationId);
                    }
                    catch (Exception e)
                    {
                        state[0] = 0; msg[0] = "RPC派发异常:" + e.GetType().Name;
                        lease.CompleteDispatchException(e);
                    }
                    yield return WaitRpc(() => state[0] >= 0, lease);
                    if (lease.IsUnknown)
                    {
                        CompleteUnknownStepProjection(lease, outcomes, name,
                            "后端结果未知；本批停止且不会自动重试", onResult);
                        yield break;
                    }
                    string text = state[0] == 1
                        ? snap.Name + "使用" + (kind[0] ?? "物品") + "「"
                            + (real[0] ?? item) + "」" + (amount[0] > 1 ? ("×" + amount[0]) : "")
                            + "，背包数量 " + before[0] + "→" + after[0]
                        : "使用物品失败:" + (msg[0] ?? "不在实时候选或当前条件不允许");
                    if (CompleteMutationStepProjection(lease, outcomes, state[0] == 1,
                        text, onResult, real[0] ?? item) && state[0] == 1)
                        AddStoryReceipt(storyReceipts, name, lease.OperationId,
                            snap.NpcId, snap.NpcId, snap.Name, snap.Name,
                            real[0] ?? item, text);
                    yield break;
                }
                case "heal":
                {
                    int target = 0; string label = null;
                    yield return ResolvePerson(snap, scene, S("target"), snap.TaiwuId, "太吾", Current,
                        (id, nm) => { target = id; label = nm; });
                    if (target <= 0) { onResult("未执行：认不出疗伤目标"); yield break; }
                    yield return ExecuteBooleanCompanionMutation(name, a, "target", target, label, "",
                        snap.Name + "为" + (label ?? "对方") + "完成疗伤", "疗伤失败", snap, scene,
                        outcomes, storyReceipts, batchEpoch, generation, conversationClearEpoch, batchId,
                        stepIndex, onResult, onOperationPrepared,
                        (done, operationId) => EffectHandler.ApplyHeal(snap.NpcId, target, done, operationId));
                    yield break;
                }
                case "dissolve_relation":
                {
                    int target = 0; string label = null;
                    yield return ResolvePerson(snap, scene, S("target"), snap.TaiwuId, "太吾", Current,
                        (id, nm) => { target = id; label = nm; });
                    string relation = (S("relation") ?? "").Trim().ToLowerInvariant();
                    if (target <= 0 || target == snap.NpcId || !IsDissolvableRelation(relation)) { onResult("未执行：不能解除与自己的关系，且关系必须有效"); yield break; }
                    yield return ExecuteBooleanCompanionMutation(name, a, "target", target, label, relation,
                        snap.Name + "与" + (label ?? "对方") + "解除了"
                            + StoryProjectionValidator.RelationshipDisplayName(relation, true),
                        "解除关系失败", snap, scene,
                        outcomes, storyReceipts, batchEpoch, generation, conversationClearEpoch, batchId,
                        stepIndex, onResult, onOperationPrepared,
                        (done, operationId) => EffectHandler.ApplyDissolveRelation(snap.NpcId, snap.TaiwuId,
                            relation, target, done, operationId));
                    yield break;
                }
                case "barter":
                {
                    int target = 0; string label = null;
                    yield return ResolvePerson(snap, scene, S("target"), snap.TaiwuId, "太吾", Current,
                        (id, nm) => { target = id; label = nm; });
                    string give = (S("give_item") ?? "").Trim(), receive = (S("receive_item") ?? "").Trim();
                    int giveAmount = Math.Max(1, I("give_amount", 1)), receiveAmount = Math.Max(1, I("receive_amount", 1));
                    if (target <= 0 || target == snap.NpcId || give.Length == 0 || receive.Length == 0)
                    { onResult("未执行：不能与自己换物，且双方交换物必须有效"); yield break; }
                    NpcSnapshot targetInventory = null;
                    yield return FetchInventoryForPreflight(target, Current, value => targetInventory = value);
                    if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                    GiftableItem receiveHolding;
                    if (targetInventory != null && targetInventory.HoldingsLoaded
                        && !TryFindHoldingOrSilver(targetInventory, receive, receiveAmount, out receiveHolding))
                    {
                        string why = "以物换物前置预查拒绝:" + (label ?? ("#" + target))
                            + "当前权威持有清单里没有「" + receive + "」x" + receiveAmount
                            + "；可见持有物:" + ListGiftables(targetInventory);
                        onResult("未执行：" + why); yield break;
                    }
                    string asset = give + "x" + giveAmount + " ↔ " + receive + "x" + receiveAmount;
                    int[] actualGive = { giveAmount }, actualReceive = { receiveAmount };
                    string[] actualGiveName = { give }, actualReceiveName = { receive };
                    yield return ExecuteBooleanCompanionMutation(name, a, "target", target, label, asset,
                        snap.Name + "与" + (label ?? "对方") + "完成以物换物:" + asset, "以物换物失败", snap, scene,
                        outcomes, storyReceipts, batchEpoch, generation, conversationClearEpoch, batchId,
                        stepIndex, onResult, onOperationPrepared,
                        (done, operationId) => EffectHandler.ApplyBarter(snap.NpcId, target, give, giveAmount,
                            receive, receiveAmount, (ok, ga, ra, gn, rn, message) =>
                            {
                                if (ok) { actualGive[0] = ga; actualReceive[0] = ra; actualGiveName[0] = gn ?? give; actualReceiveName[0] = rn ?? receive; }
                                done(ok, message);
                            }, operationId),
                        () => snap.Name + "与" + (label ?? "对方") + "完成以物换物:"
                            + actualGiveName[0] + "x" + actualGive[0] + " ↔ " + actualReceiveName[0] + "x" + actualReceive[0],
                        () => actualGiveName[0] + "x" + actualGive[0] + " ↔ " + actualReceiveName[0] + "x" + actualReceive[0]);
                    yield break;
                }
                case "write_book":
                case "teach":
                {
                    int target = 0; string label = null;
                    int defaultTarget = name == "write_book" ? snap.TaiwuId : 0;
                    yield return ResolvePerson(snap, scene, S("target"), defaultTarget,
                        defaultTarget > 0 ? "太吾" : null, Current, (id, nm) => { target = id; label = nm; });
                    string kind = (S("type") ?? "").Trim().ToLowerInvariant();
                    LearnableSkill skill = FindKnownSkill(snap, kind, S("skill"));
                    if (target <= 0 || target == snap.NpcId || skill == null || (kind != "combat" && kind != "life"))
                    { onResult("未执行：对象、类型或所习内容无效"); yield break; }
                    if (name == "teach" && target == snap.TaiwuId)
                    { onResult("未执行：同道过月的亲授只面向同地块其他人物，不能亲授太吾"); yield break; }
                    if (name == "teach")
                    {
                        NpcSnapshot recipientSkills = null;
                        if (scene.PersonSkillFacts == null
                            || !scene.PersonSkillFacts.TryGetValue(target, out recipientSkills))
                        {
                            bool skillsReady = false;
                            string skillsReceipt = null;
                            yield return HydrateCompanionTargetSkillFact(snap, scene, target, label,
                                Current, (ok, receipt) =>
                                {
                                    skillsReady = ok;
                                    skillsReceipt = receipt;
                            });
                            if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                            if (!skillsReady || scene.PersonSkillFacts == null
                                || !scene.PersonSkillFacts.TryGetValue(target, out recipientSkills))
                            {
                                onResult("未执行：代码已自动读取受教者武学与技艺前置，但无法可靠确认，"
                                    + "所以没有发生亲授：" + (skillsReceipt ?? "读取失败"));
                                yield break;
                            }
                        }
                        bool recipientSkillsLoaded = kind == "life"
                            ? recipientSkills.LifeSkillsLoaded : recipientSkills.CombatSkillsLoaded;
                        if (!recipientSkillsLoaded)
                        {
                            onResult("未执行：受教者的" + (kind == "life" ? "技艺" : "武学")
                                + "清单没有可靠读出，不能冒险亲授；请重新 query_person 或换行为。");
                            yield break;
                        }
                        IList<LearnableSkill> learned = kind == "life"
                            ? recipientSkills.LearnableLifeSkills : recipientSkills.LearnableSkills;
                        bool alreadyKnown = false;
                        if (learned != null)
                            foreach (LearnableSkill knownSkill in learned)
                                if (knownSkill != null && knownSkill.TemplateId == skill.TemplateId)
                                { alreadyKnown = true; break; }
                        if (alreadyKnown)
                        {
                            onResult("未执行：" + (label ?? ("#" + target)) + "已经通晓「" + skill.Name
                                + "」。本步没有发生。当前可亲授给此人的武学="
                                + ListUnlearnedRelevantSkills(snap.LearnableSkills, recipientSkills.LearnableSkills)
                                + "；技艺=" + ListUnlearnedRelevantSkills(snap.LearnableLifeSkills,
                                    recipientSkills.LearnableLifeSkills)
                                + "。只能选择上述未学候选；均为无时必须换对象或改做别的事，不要轮流试授已会内容。");
                            yield break;
                        }
                    }
                    a["template_id"] = skill.TemplateId;
                    string verb = name == "teach" ? "亲授" : "写成秘籍并赠予";
                    string[] actualBook = { skill.Name }; int[] lostPages = { 0 };
                    yield return ExecuteBooleanCompanionMutation(name, a, "target", target, label, skill.Name,
                        snap.Name + "将「" + skill.Name + "」" + verb + (label ?? "对方"), verb + "失败", snap, scene,
                        outcomes, storyReceipts, batchEpoch, generation, conversationClearEpoch, batchId,
                        stepIndex, onResult, onOperationPrepared,
                        (done, operationId) =>
                        {
                            if (name == "write_book")
                                EffectHandler.ApplyWriteBook(snap.NpcId, snap.TaiwuId, kind, skill.TemplateId,
                                    (ok, book, lost) =>
                                    {
                                        if (ok) { actualBook[0] = book ?? skill.Name; lostPages[0] = lost; }
                                        done(ok, book);
                                    },
                                    target == snap.TaiwuId ? 0 : target, operationId);
                            else if (kind == "life")
                                EffectHandler.ApplyTeachLifeSkill(snap.NpcId, snap.TaiwuId, skill.TemplateId,
                                    done, target, operationId);
                            else EffectHandler.ApplyTeachSkillId(snap.NpcId, snap.TaiwuId, skill.TemplateId,
                                    done, target, operationId);
                        },
                        name == "write_book" ? (Func<string>)(() => snap.Name + "写成「" + actualBook[0]
                            + "」并赠予" + (label ?? "对方") + (lostPages[0] > 0 ? ("，残缺" + lostPages[0] + "页") : "")) : null,
                        name == "write_book" ? (Func<string>)(() => actualBook[0]) : null);
                    yield break;
                }
                case "tell_secret":
                {
                    int target = 0; string label = null;
                    yield return ResolvePerson(snap, scene, S("target"), snap.TaiwuId, "太吾", Current,
                        (id, nm) => { target = id; label = nm; });
                    int index = I("index", 0);
                    SecretRef secret = null;
                    if (target > 0 && index > 0 && scene.DisclosableSecretsByRecipient != null
                        && scene.DisclosableSecretsByRecipient.TryGetValue(target,
                            out List<SecretRef> recipientSecrets))
                        secret = recipientSecrets.Find(item => item != null
                            && item.DisplayIndex == index);
                    if (target <= 0 || target == snap.NpcId || secret == null)
                    {
                        if (target <= 0 || target == snap.NpcId)
                        {
                            onResult("未执行：秘闻接收者必须是可唯一识别的另一名人物");
                            yield break;
                        }
                        string secretReceipt = null;
                        yield return HydrateCompanionSecretCandidates(snap, scene, target, label,
                            Current, (_, receipt) => secretReceipt = receipt);
                        if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
                        if (scene.DisclosableSecretsByRecipient != null
                            && scene.DisclosableSecretsByRecipient.TryGetValue(target,
                                out List<SecretRef> refreshedSecrets))
                            secret = refreshedSecrets.Find(item => item != null
                                && item.DisplayIndex == index);
                        if (secret == null)
                        {
                            onResult("未执行：代码已自动刷新该接收者的可传播秘闻，但 index "
                                + index + " 不在当前权威候选中，所以没有传播。"
                                + (secretReceipt ?? "当前没有可传播候选"));
                            yield break;
                        }
                    }
                    bool canDisclose = false, discloseDone = false; string discloseReason = null;
                    EffectHandler.QueryCanDiscloseSecret(secret.Id, snap.NpcId, target,
                        (ok, reason) => { canDisclose = ok; discloseReason = reason; discloseDone = true; });
                    float discloseDeadline = Time.unscaledTime + 8f;
                    while (!discloseDone && Time.unscaledTime < discloseDeadline && Current()) yield return null;
                    if (!discloseDone)
                    { onResult("未执行：秘闻传播状态预查超时"); yield break; }
                    if (!canDisclose)
                    { onResult("未执行：" + (discloseReason ?? "这桩秘闻当前不可向目标传播")); yield break; }
                    a["secret_id"] = (int)secret.Id; a["secret_source"] = snap.NpcId;
                    string asset = Trim(secret.Text, 120);
                    yield return ExecuteBooleanCompanionMutation(name, a, "target", target, label, asset,
                        snap.Name + "向" + (label ?? "对方") + "吐露秘闻:" + asset, "吐露秘闻失败", snap, scene,
                        outcomes, storyReceipts, batchEpoch, generation, conversationClearEpoch, batchId,
                        stepIndex, onResult, onOperationPrepared,
                        (done, operationId) => EffectHandler.ApplyDiscloseSecret(secret.Id,
                            snap.NpcId, target, done, operationId));
                    yield break;
                }
                case "change_equipment":
                {
                    string action = (S("action") ?? "").Trim().ToLowerInvariant();
                    string item = (S("item") ?? "").Trim(), part = (S("part") ?? "").Trim().ToLowerInvariant();
                    if (action != "on" && action != "off" || action == "on" && item.Length == 0
                        || action == "off" && !IsEquipmentPart(part))
                    { onResult("未执行：换装动作、物品或部位无效"); yield break; }
                    string asset = action == "on" ? ("换上" + item) : ("卸下" + part);
                    yield return ExecuteBooleanCompanionMutation(name, a, "person", snap.NpcId, snap.Name, asset,
                        snap.Name + asset, "换装失败", snap, scene, outcomes, storyReceipts, batchEpoch, generation,
                        conversationClearEpoch, batchId, stepIndex, onResult, onOperationPrepared,
                        (done, operationId) => { if (action == "off") EffectHandler.ApplyEquipTakeOff(snap.NpcId, part, done, operationId); else EffectHandler.ApplyEquipByName(snap.NpcId, item, done, operationId); });
                    yield break;
                }
                case "sect_support":
                {
                    if (!snap.IsSect) { onResult("未执行：并非门派中人"); yield break; }
                    yield return ExecuteBooleanCompanionMutation(name, a, "target", snap.TaiwuId, "太吾", "本门支持",
                        snap.Name + "在所属门派内公开支持太吾", "门派支持失败", snap, scene, outcomes,
                        storyReceipts, batchEpoch, generation, conversationClearEpoch, batchId, stepIndex, onResult,
                        onOperationPrepared, (done, operationId) => EffectHandler.ApplySectSupport(snap.NpcId, done, operationId));
                    yield break;
                }
                case "adjust_favor":
                {
                    int target = 0; string label = null;
                    yield return ResolvePerson(snap, scene, S("target"), snap.TaiwuId, "太吾", Current,
                        (id, nm) => { target = id; label = nm; });
                    int delta = Math.Max(-3000, Math.Min(3000, I("delta", 0)));
                    if (target <= 0 || target == snap.NpcId || delta == 0) { onResult("未执行：不能调整对自己的好感，且变化值必须有效"); yield break; }
                    string asset = (delta > 0 ? "+" : "") + delta;
                    yield return ExecuteBooleanCompanionMutation(name, a, "target", target, label, asset,
                        snap.Name + "对" + (label ?? "对方") + "的好感变化" + asset, "好感变化失败", snap, scene,
                        outcomes, storyReceipts, batchEpoch, generation, conversationClearEpoch, batchId,
                        stepIndex, onResult, onOperationPrepared,
                        (done, operationId) => { if (target == snap.TaiwuId) EffectHandler.ApplyFavor(snap.NpcId, snap.TaiwuId, delta, done, operationId); else EffectHandler.ApplyThirdPartyFavor(snap.NpcId, target, delta, done, operationId); });
                    yield break;
                }
                case "detox":
                case "regulate_breath":
                {
                    int target = 0; string label = null;
                    yield return ResolvePerson(snap, scene, S("target"), snap.TaiwuId, "太吾", Current,
                        (id, nm) => { target = id; label = nm; });
                    if (target <= 0 || target == snap.NpcId)
                    { onResult("未执行：只能由当前 NPC 为另一名在场人物驱毒或调息"); yield break; }
                    string verb = name == "detox" ? "驱毒" : "调息";
                    yield return ExecuteBooleanCompanionMutation(name, a, "target", target, label, "",
                        snap.Name + "为" + (label ?? "对方") + "完成" + verb, verb + "失败", snap, scene,
                        outcomes, storyReceipts, batchEpoch, generation, conversationClearEpoch, batchId,
                        stepIndex, onResult, onOperationPrepared,
                        (done, operationId) =>
                        {
                            if (name == "detox") EffectHandler.ApplyDetox(snap.NpcId, target, done, operationId);
                            else EffectHandler.ApplyRegulateBreath(snap.NpcId, target, done, operationId);
                        });
                    yield break;
                }
                case "adjust_mood":
                case "adjust_fame":
                {
                    bool mood = name == "adjust_mood";
                    int target = 0; string label = null;
                    yield return ResolvePerson(snap, scene, S("target"), 0, null, Current,
                        (id, nm) => { target = id; label = nm; });
                    int delta = I("delta", 0);
                    string reason = (S("reason") ?? "").Trim();
                    if (target <= 0 || reason.Length < 2)
                    {
                        onResult("未执行：状态变化对象或具体原因无效");
                        yield break;
                    }
                    if (mood
                        ? delta == 0 || delta < -30 || delta > 30
                        : delta == 0 || delta < -12 || delta > 12 || delta % 3 != 0)
                    {
                        onResult(mood
                            ? "未执行：心情变化必须是 -30..30 的非零整数"
                            : "未执行：名望变化只能是 ±3、±6、±9 或 ±12");
                        yield break;
                    }
                    string asset = (delta > 0 ? "+" : "") + delta;
                    string stateName = mood ? "心情" : "名望";
                    yield return ExecuteBooleanCompanionMutation(name, a, "target", target,
                        label, asset,
                        (label ?? ("#" + target)) + "的" + stateName + "变化" + asset
                            + "，缘由：" + reason,
                        stateName + "变化失败", snap, scene, outcomes, storyReceipts,
                        batchEpoch, generation, conversationClearEpoch, batchId, stepIndex,
                        onResult, onOperationPrepared,
                        (done, operationId) =>
                        {
                            if (mood)
                                EffectHandler.ApplyCharacterHappiness(target, delta, done,
                                    operationId);
                            else EffectHandler.ApplyCharacterFame(target, delta, done,
                                operationId);
                        });
                    yield break;
                }
                default:
                    onResult("未成:未知工具 " + name);
                    yield break;
            }
        }

        private static IEnumerator ExecuteBooleanCompanionMutation(string tool, JObject args,
            string entityField, int targetId, string targetName, string asset, string successText,
            string failurePrefix, NpcSnapshot snap, CompanionSceneContext scene, List<string> outcomes,
            List<StoryProjectionReceipt> storyReceipts, int batchEpoch, int generation,
            long conversationClearEpoch, string batchId, int stepIndex, Action<string> onResult,
            Action<string> onOperationPrepared, Action<Action<bool, string>, string> dispatch,
            Func<string> successTextOverride = null, Func<string> assetOverride = null)
        {
            bool Current() => RunIsCurrent(batchEpoch, generation, snap.TaiwuId, snap.NpcId,
                conversationClearEpoch) && scene != null && scene.IsCurrent();
            if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
            string preflightRefusal = null;
            yield return PreflightCompanionPair(tool, args, snap, targetId, Current,
                reason => preflightRefusal = reason);
            if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
            if (!string.IsNullOrWhiteSpace(preflightRefusal))
            {
                onResult("未执行：权威前置预查拒绝:" + preflightRefusal);
                yield break;
            }
            int[] state = { -1 }; string[] message = { null };
            var lease = TryBeginToolMutationLease(batchEpoch, Current, snap, scene, tool, args,
                entityField, targetId, outcomes, onResult, batchId, stepIndex, conversationClearEpoch,
                onOperationPrepared);
            if (lease == null) yield break;
            try
            {
                dispatch((ok, msg) =>
                {
                    try { state[0] = ok ? 1 : 0; message[0] = msg; }
                    finally { lease.CompleteLegacy(ok, msg); }
                }, lease.OperationId);
            }
            catch (Exception e)
            {
                state[0] = 0; message[0] = "RPC派发异常:" + e.GetType().Name;
                lease.CompleteDispatchException(e);
            }
            yield return WaitRpc(() => state[0] >= 0, lease);
            if (lease.IsUnknown)
            {
                CompleteUnknownStepProjection(lease, outcomes, tool,
                    "后端结果未知；为避免重复副作用，本步不自动重试", onResult);
                yield break;
            }
            if (!Current()) { onResult("未成:存档或聊天记录已切换"); yield break; }
            bool ok = state[0] == 1;
            string normalizedTool = NormalizePlanToolName(tool);
            string relationKind = ((normalizedTool == "set_relation"
                ? args?["action"] : args?["kind"])?.ToString() ?? "").Trim().ToLowerInvariant();
            bool adorationMutation = (normalizedTool == "relate" || normalizedTool == "set_relation")
                && relationKind == "lover";
            string finalAsset = ok && assetOverride != null ? assetOverride() : asset;
            if (ok && adorationMutation && !string.IsNullOrWhiteSpace(message[0])
                && message[0].IndexOf("单方面爱慕", StringComparison.Ordinal) >= 0)
                finalAsset = "adored";
            string text = ok ? (adorationMutation && !string.IsNullOrWhiteSpace(message[0])
                    ? message[0]
                    : successTextOverride == null ? successText : successTextOverride())
                : failurePrefix + ":" + (message[0] ?? "引擎拒绝");
            if (CompleteMutationStepProjection(lease, outcomes, ok, text, onResult, finalAsset) && ok)
                AddStoryReceipt(storyReceipts, tool, lease.OperationId, snap.NpcId, targetId,
                    snap.Name, targetName, finalAsset, text);
        }

        private static IEnumerator PreflightCompanionPair(string tool, JObject args, NpcSnapshot snap,
            int targetId, Func<bool> stillCurrent, Action<string> done)
        {
            string normalized = NormalizePlanToolName(tool);
            if (normalized == "heal")
            {
                HealPreflight heal = null; bool healDone = false;
                EffectHandler.QueryHealPreflight(targetId,
                    value => { heal = value; healDone = true; });
                float healDeadline = Time.unscaledTime + 8f;
                while (!healDone && Time.unscaledTime < healDeadline
                    && (stillCurrent == null || stillCurrent())) yield return null;
                if (!healDone || heal == null)
                { done?.Invoke("健康与伤势状态读取超时"); yield break; }
                if (!heal.TargetAlive) { done?.Invoke("目标已不在江湖"); yield break; }
                if (!heal.TargetNeedsHealing)
                {
                    done?.Invoke("目标当前气血" + heal.TargetHealth + "/" + heal.TargetLeftMaxHealth
                        + "、伤势标记" + heal.TargetInjuryMarks + "，没有可治疗的变化");
                    yield break;
                }
                done?.Invoke(null);
                yield break;
            }
            bool needsPair = normalized == "matchmake" || normalized == "spend_night"
                || normalized == "relate" || normalized == "dissolve_relation" || normalized == "enmity"
                || normalized == "set_relation" || normalized == "poison"
                || normalized == "capture" || normalized == "kill";
            if (!needsPair || snap == null || targetId <= 0 || targetId == snap.NpcId)
            { done?.Invoke(null); yield break; }
            MonthlyActionPreflight state = null; bool completed = false;
            EffectHandler.QueryMonthlyActionPreflight(snap.NpcId, targetId,
                value => { state = value; completed = true; });
            float deadline = Time.unscaledTime + 8f;
            while (!completed && Time.unscaledTime < deadline
                && (stillCurrent == null || stillCurrent())) yield return null;
            if (!completed || state == null) { done?.Invoke("人物、关系与能力状态读取超时"); yield break; }
            if (!state.ActorAlive) { done?.Invoke("行动者已不在江湖"); yield break; }
            if (!state.TargetAlive) { done?.Invoke("目标已不在江湖"); yield break; }
            if ((normalized == "poison" || normalized == "capture" || normalized == "kill")
                && state.ActorRestrained)
            { done?.Invoke("actor_restrained：行动者正被囚禁或绑架，不能下毒、擒拿或行凶；请改用可行行动"); yield break; }
            if ((normalized == "matchmake" || normalized == "spend_night")
                && (!state.ActorAdult || !state.TargetAdult))
            { done?.Invoke("双方并非都已成年"); yield break; }
            if ((normalized == "capture" || normalized == "kill") && state.TargetIsTaiwu)
            { done?.Invoke("不能以太吾为杀害或擒拿目标"); yield break; }
            if ((normalized == "poison" || normalized == "capture" || normalized == "kill")
                && !state.StrongEnough)
            { done?.Invoke("行动者精纯不及目标(" + state.ActorConsummate + "<" + state.TargetConsummate + ")"); yield break; }
            if (normalized == "capture" && state.TargetKidnapped)
            { done?.Invoke("目标已经被他人掳走"); yield break; }
            if (normalized == "matchmake" && state.Spouse)
            { done?.Invoke("双方已经是夫妻"); yield break; }
            if (normalized == "matchmake" && !state.CanMarry)
            { done?.Invoke(state.ActorInfected || state.TargetInfected ? "有人已完全入魔，不可成婚"
                : "游戏权威关系规则判定双方不可成婚"); yield break; }
            if (normalized == "spend_night" && !state.Spouse && !state.Adored && state.ActorFavor < 18000)
            { done?.Invoke("发起者对目标的情意尚不足以共度春宵"); yield break; }
            if (normalized == "enmity")
            {
                bool make = args?.Value<bool?>("make") ?? true;
                if (make && state.Enemy) { done?.Invoke("双方已经是仇敌"); yield break; }
                if (!make && !state.Enemy) { done?.Invoke("双方当前并无仇怨可解"); yield break; }
            }
            string kind = ((normalized == "dissolve_relation"
                ? args?["relation"] : args?["kind"])?.ToString() ?? "").Trim().ToLowerInvariant();
            if (normalized == "set_relation")
            {
                string action = (args?["action"]?.ToString() ?? "").Trim().ToLowerInvariant();
                kind = action == "befriend" ? "befriend" : action == "swear_sibling" ? "sworn"
                    : action == "lover" ? "lover" : action == "spouse" ? "spouse"
                    : action == "adoptive_parent" ? "adoptive_parent"
                    : action == "adoptive_child" ? "adoptive_child"
                    : action == "apprentice" || action == "take_disciple" ? "mentor" : "";
            }
            bool relationExists = kind == "befriend" || kind == "friend" ? state.Friend
                : kind == "sworn" ? state.Sworn : kind == "mentor" ? state.Mentor
                : kind == "adoptive_parent" ? state.AdoptiveParent
                : kind == "adoptive_child" ? state.AdoptiveChild
                : kind == "lover" ? state.Adored : kind == "spouse" ? state.Spouse : false;
            if ((normalized == "relate" || normalized == "set_relation") && relationExists)
            { done?.Invoke("双方已经存在所请求关系；当前：" + state.RelationText); yield break; }
            if ((normalized == "relate" || normalized == "set_relation")
                && kind == "adoptive_parent" && !state.CanAdoptiveParent)
            {
                done?.Invoke(string.IsNullOrWhiteSpace(state.AdoptiveParentReason)
                    ? "不满足认义父母的本体条件" : state.AdoptiveParentReason);
                yield break;
            }
            if (normalized == "detox" || normalized == "regulate_breath")
            {
                if (snap == null || targetId <= 0 || targetId == snap.NpcId)
                { done?.Invoke("驱毒或调息只能由当前 NPC 为另一名人物施行"); yield break; }
                NpcHealthStatus health = null; bool healthDone = false;
                EffectHandler.QueryNpcHealthStatus(targetId,
                    value => { health = value; healthDone = true; });
                float healthDeadline = Time.unscaledTime + EffectHandler.ReadOnlyQueryWaitSeconds;
                while (!healthDone && Time.unscaledTime < healthDeadline
                    && (stillCurrent == null || stillCurrent())) yield return null;
                if (!healthDone || health == null)
                { done?.Invoke("伤势、气息与中毒状态读取超时"); yield break; }
                if (normalized == "detox")
                {
                    bool poisoned = false;
                    if (health.Poisons != null)
                        foreach (int value in health.Poisons) if (value > 0) { poisoned = true; break; }
                    if (!poisoned) { done?.Invoke("目标当前并未中毒"); yield break; }
                }
                else if (health.QiDisorderShow <= 0)
                { done?.Invoke("目标当前内息顺畅"); yield break; }
                done?.Invoke(null);
                yield break;
            }
            if ((normalized == "relate" || normalized == "set_relation")
                && kind == "adoptive_child" && !state.CanAdoptiveChild)
            {
                done?.Invoke(string.IsNullOrWhiteSpace(state.AdoptiveChildReason)
                    ? "不满足收义子女的本体条件" : state.AdoptiveChildReason);
                yield break;
            }
            if ((normalized == "relate" || normalized == "set_relation") && kind == "spouse")
            {
                if (!state.ActorAdult || !state.TargetAdult)
                { done?.Invoke("双方并非都已成年，不能成婚"); yield break; }
                if (!state.CanMarry)
                {
                    done?.Invoke(state.ActorInfected || state.TargetInfected
                        ? "有人已完全入魔，不能成婚"
                        : "双方已有配偶、属于禁婚亲缘，或现有关系不允许成婚");
                    yield break;
                }
            }
            bool relationCanDissolve = kind == "lover"
                ? state.ActorAdoresTarget || state.TargetAdoresActor : relationExists;
            if (normalized == "dissolve_relation" && !relationCanDissolve)
            { done?.Invoke("双方并不存在要解除的关系；当前：" + state.RelationText); yield break; }
            done?.Invoke(null);
        }

        private static IEnumerator PrimeCompanionPlanTargets(IList<CompanionPlanStep> steps, NpcSnapshot snap,
            CompanionSceneContext scene, Func<bool> stillCurrent, ISet<int> resolvedTargetIds = null,
            Action<string> onResolutionFailure = null)
        {
            if (steps == null || snap == null || scene == null) yield break;
            foreach (CompanionPlanStep step in steps)
            {
                if (stillCurrent != null && !stillCurrent()) yield break;
                if (step == null) continue;
                string tool = NormalizePlanToolName(step.Name);
                string field = CompanionMutationTargetPolicy.TargetField(tool);
                JObject args = null;
                try { args = JObject.Parse(step.ArgsJson ?? "{}"); }
                catch
                {
                    args = new JObject();
                    if (field != null && !CompanionMutationTargetPolicy.DefaultsToTaiwu(tool))
                    {
                        onResolutionFailure?.Invoke(tool + " 的目标参数不是有效 JSON");
                        continue;
                    }
                }
                string raw = field == null ? null : args?[field]?.ToString();
                int implicitTarget = CompanionMutationTargetPolicy.ImplicitTargetId(
                    tool, raw, snap.TaiwuId);
                if (implicitTarget > 0)
                {
                    scene.AuthorizedTargetIds.Add(implicitTarget);
                    resolvedTargetIds?.Add(implicitTarget);
                    if (CompanionMutationTargetPolicy.HasFixedTaiwuTarget(tool)
                        || string.IsNullOrWhiteSpace(raw))
                    {
                        if (field != null)
                        {
                            args[field] = "#" + implicitTarget;
                            step.ArgsJson = args.ToString(Formatting.None);
                        }
                        continue;
                    }
                }
                if (field == null) continue;
                if (string.IsNullOrWhiteSpace(raw))
                {
                    onResolutionFailure?.Invoke(tool + " 缺少目标字段 " + field);
                    continue;
                }
                int frozenTargetId = 0;
                string resolutionDetail = null;
                yield return ResolvePerson(snap, scene, raw, 0, null, stillCurrent,
                    (id, name) =>
                    {
                        frozenTargetId = id;
                        resolutionDetail = name;
                    });
                if (frozenTargetId <= 0)
                {
                    onResolutionFailure?.Invoke(tool + " 的目标“" + raw + "”无法可靠冻结："
                        + (resolutionDetail ?? "人物解析无权威结果"));
                    continue;
                }
                scene.AuthorizedTargetIds.Add(frozenTargetId);
                resolvedTargetIds?.Add(frozenTargetId);
                args[field] = "#" + frozenTargetId;
                step.ArgsJson = args.ToString(Formatting.None);
            }
        }

        private static bool ExpandBatchProjectionAuthority(string path, string batchId, NpcSnapshot snap,
            CompanionSceneContext scene)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(batchId)
                || snap == null || scene == null) return false;
            var expanded = new SortedSet<int>();
            PopulateAuthorizedParticipants(expanded, snap, scene);
            string expandedJson = JsonConvert.SerializeObject(expanded, Formatting.None);
            lock (JournalIo)
            {
                var doc = LoadMutationJournal(path);
                if (doc == null || doc.Entries == null) return false;
                bool changed = false;
                foreach (MutationJournalRecord entry in doc.Entries)
                {
                    if (entry == null || !string.Equals(entry.BatchId, batchId, StringComparison.Ordinal)) continue;
                    if (!TryParseProjectionParticipantIds(entry.ProjectionParticipantIdsJson,
                        out HashSet<int> existing) || !existing.IsSubsetOf(expanded)
                        || !expanded.Contains(entry.NpcId) || !expanded.Contains(entry.TaiwuId)
                        || !expanded.Contains(entry.TargetId)) return false;
                    if (existing.SetEquals(expanded)) continue;
                    entry.ProjectionParticipantIdsJson = expandedJson;
                    entry.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                    changed = true;
                }
                if (!changed) return true;
                doc.Revision++;
                return SaveMutationJournalDocument(path, doc);
            }
        }

        private static IEnumerator ResolvePerson(NpcSnapshot snap, CompanionSceneContext scene, string raw,
            int defaultId, string defaultName, Func<bool> stillCurrent, Action<int, string> done)
        {
            if (stillCurrent != null && !stillCurrent()) { done(0, "存档或聊天记录已切换"); yield break; }
            if (snap == null || scene == null || !scene.IsCurrent())
            { done(0, "人物与距离场景不可可靠读取"); yield break; }
            string t = (raw ?? "").Trim();
            if (t.Length == 0)
            {
                done(IsCompanionKnownEndpoint(scene, snap, defaultId) ? defaultId : 0,
                    IsCompanionKnownEndpoint(scene, snap, defaultId) ? defaultName : "默认对象无法可靠识别");
                yield break;
            }
            if (LooksLikeSelf(t, snap.Name)) { done(snap.NpcId, "自己"); yield break; }
            if (LooksLikeTaiwu(t)) { done(snap.TaiwuId, "太吾"); yield break; }

            int explicitId = ParseCompanionSceneId(t);
            if (explicitId > 0)
            {
                if (!IsCompanionKnownEndpoint(scene, snap, explicitId))
                {
                    int resolvedId = int.MinValue; string explicitResolveReason = null;
                    EffectHandler.ResolveChar(snap.NpcId, "#" + explicitId, true, true,
                        (id, reason) => { resolvedId = id; explicitResolveReason = reason; });
                    float explicitDeadline = Time.unscaledTime + EffectHandler.ReadOnlyQueryWaitSeconds;
                    while (resolvedId == int.MinValue && Time.unscaledTime < explicitDeadline
                        && (stillCurrent == null || stillCurrent())) yield return null;
                    if (resolvedId != explicitId
                        || stillCurrent != null && !stillCurrent())
                    {
                        done(0, string.Equals(explicitResolveReason, "ambiguous", StringComparison.Ordinal)
                            ? ("角色编号存在歧义:#" + explicitId)
                            : ("无法确认可交互角色实体:#" + explicitId));
                        yield break;
                    }
                    string explicitName = "#" + explicitId;
                    List<string> explicitNames = null; bool explicitNameDone = false;
                    EffectHandler.QueryCharNames(new List<int> { explicitId },
                        value => { explicitNames = value; explicitNameDone = true; });
                    float explicitNameDeadline = Time.unscaledTime + EffectHandler.ReadOnlyQueryWaitSeconds;
                    while (!explicitNameDone && Time.unscaledTime < explicitNameDeadline
                        && (stillCurrent == null || stillCurrent())) yield return null;
                    if (explicitNames != null && explicitNames.Count > 0
                        && !string.IsNullOrWhiteSpace(explicitNames[0]))
                        explicitName = explicitNames[0];
                    RegisterResolvedScenePerson(scene, snap, explicitId, explicitName);
                }
                done(explicitId, CompanionSceneEndpointName(scene, snap, explicitId));
                yield break;
            }

            int matchedId = 0;
            if (scene.Names != null)
                foreach (var pair in scene.Names)
                {
                    if (!string.Equals((pair.Value ?? string.Empty).Trim(), t, StringComparison.Ordinal)) continue;
                    if (matchedId != 0)
                    { done(0, "可识别人物中存在重名，请使用 #角色ID"); yield break; }
                    matchedId = pair.Key;
                }
            if (matchedId > 0 && IsCompanionKnownEndpoint(scene, snap, matchedId))
            { done(matchedId, CompanionSceneEndpointName(scene, snap, matchedId)); yield break; }

            int resolved = int.MinValue; string resolveReason = null;
            EffectHandler.ResolveChar(snap.NpcId, t, true, true,
                (id, reason) => { resolved = id; resolveReason = reason; });
            float deadline = Time.unscaledTime + EffectHandler.ReadOnlyQueryWaitSeconds;
            while (resolved == int.MinValue && Time.unscaledTime < deadline
                && (stillCurrent == null || stillCurrent())) yield return null;
            if (resolved <= 0 || stillCurrent != null && !stillCurrent())
            {
                done(0, string.Equals(resolveReason, "ambiguous", StringComparison.Ordinal)
                    ? ("全局存在同名人物，请使用 #角色ID:" + t)
                    : ("无法从关系网、现场或全局唯一全名识别人物:" + t));
                yield break;
            }
            List<string> resolvedNames = null; bool nameDone = false;
            EffectHandler.QueryCharNames(new List<int> { resolved }, value => { resolvedNames = value; nameDone = true; });
            float nameDeadline = Time.unscaledTime + EffectHandler.ReadOnlyQueryWaitSeconds;
            while (!nameDone && Time.unscaledTime < nameDeadline
                && (stillCurrent == null || stillCurrent())) yield return null;
            if (!nameDone || stillCurrent != null && !stillCurrent())
            { done(0, "已识别人但未能可靠读取姓名:#" + resolved); yield break; }
            string resolvedName = resolvedNames != null && resolvedNames.Count > 0 ? resolvedNames[0] : null;
            RegisterResolvedScenePerson(scene, snap, resolved, string.IsNullOrWhiteSpace(resolvedName) ? t : resolvedName);
            done(resolved, CompanionSceneEndpointName(scene, snap, resolved));
        }

        private static void RegisterResolvedScenePerson(CompanionSceneContext scene, NpcSnapshot snap,
            int id, string name)
        {
            if (scene == null || snap == null || id <= 0) return;
            scene.AuthorizedTargetIds.Add(id);
            if (id != snap.NpcId && id != snap.TaiwuId && !scene.IsPresent(id)) scene.RemoteIds.Add(id);
            if (id != snap.TaiwuId && !string.IsNullOrWhiteSpace(name)) scene.Names[id] = name.Trim();
        }

        private static bool IsCompanionKnownEndpoint(CompanionSceneContext scene, NpcSnapshot snap, int id)
            => id > 0 && scene != null && snap != null
                && (id == snap.TaiwuId || id == snap.NpcId || scene.IsKnown(id));

        private static bool IsCompanionPresentEndpoint(CompanionSceneContext scene, NpcSnapshot snap, int id)
            => id > 0 && scene != null && snap != null
                && (id == snap.NpcId || scene.IsPresent(id));

        private static bool RequiresLivePairState(string toolName)
        {
            switch (NormalizePlanToolName(toolName))
            {
                case "send_message":
                case "gift_item":
                case "gift_silver":
                case "steal":
                case "heal":
                case "detox":
                case "regulate_breath":
                case "barter":
                case "write_book":
                case "teach":
                case "spend_night":
                case "matchmake":
                case "relate":
                    return true;
                default:
                    return false;
            }
        }

        // 已经完成正文投影，且后端回执已由 ACK outbox 接管（或本就没有后端回执）的
        // 终态记录不再承担恢复职责。及时移除可避免历史月份把 journal 撑到 MB 级，导致
        // 每一个过月行为都在 Unity 主线程同步序列化、校验并 fsync 三份巨大文件。
        private static int PruneSettledMutationRecords(MutationJournalDocument doc)
        {
            if (doc?.Entries == null || doc.Entries.Count == 0) return 0;
            return doc.Entries.RemoveAll(entry => entry != null
                && IsTerminalMutationStatus(entry.Status, entry.Retryable)
                && entry.ProjectionCommitted
                && (!entry.BackendReceiptStored || entry.AckOutboxCommitted));
        }

        private static bool RequiresSameLocation(string toolName)
        {
            switch (NormalizePlanToolName(toolName))
            {
                case "gift_item":
                case "gift_silver":
                case "steal":
                case "heal":
                case "detox":
                case "regulate_breath":
                case "barter":
                case "write_book":
                case "teach":
                case "spend_night":
                case "matchmake":
                    return true;
                default:
                    return false;
            }
        }

        private static void UpdateLivePairPresence(CompanionSceneContext scene,
            NpcSnapshot snap, int targetId, MonthlyActionPreflight state)
        {
            if (scene == null || snap == null || targetId <= 0 || state == null) return;
            if (state.SameValidLocation)
            {
                scene.PresentIds.Add(targetId);
                scene.RemoteIds.Remove(targetId);
                if (state.TargetArea >= 0 && state.TargetBlock >= 0)
                    scene.Locations[targetId] = FormatLiveLocation(
                        state.TargetArea, state.TargetBlock);
                return;
            }
            if (targetId != snap.NpcId) scene.PresentIds.Remove(targetId);
            scene.RemoteIds.Add(targetId);
            if (state.TargetArea >= 0 && state.TargetBlock >= 0)
                scene.Locations[targetId] = FormatLiveLocation(
                    state.TargetArea, state.TargetBlock);
        }

        private static string FormatLiveLocation(int area, int block)
        {
            if (area < 0 || block < 0) return "位置无效";
            try
            {
                string readable = NpcSnapshotReader.ResolveLocationText(
                    new GameData.Domains.Map.Location((short)area, (short)block));
                if (!string.IsNullOrWhiteSpace(readable)) return readable.Trim();
            }
            catch { }
            return "area=" + area + ",block=" + block;
        }

        private static bool RequiresTaiwuConsent(string toolName, JObject args,
            int resolvedTargetId, int taiwuId)
        {
            string tool = NormalizePlanToolName(toolName);
            int targetId = CompanionMutationTargetPolicy.HasFixedTaiwuTarget(tool)
                ? taiwuId : resolvedTargetId;
            if (taiwuId <= 0 || targetId != taiwuId) return false;
            string kind;
            switch (tool)
            {
                case "spend_night":
                case "matchmake":
                    return true;
                case "relate":
                    kind = (args?["kind"]?.ToString() ?? "").Trim().ToLowerInvariant();
                    // 单方面爱慕只改变行动者自己的心意；其余关系会替太吾作出同意。
                    return kind != "lover";
                case "set_relation":
                    kind = (args?["action"]?.ToString() ?? "").Trim().ToLowerInvariant();
                    return kind == "befriend" || kind == "swear_sibling"
                        || kind == "apprentice" || kind == "take_disciple"
                        || kind == "adoptive_parent" || kind == "adoptive_child"
                        || kind == "spouse" || kind == "follow";
                default:
                    return false;
            }
        }

        private static bool CommunicationContentContradictsMode(string content,
            string mode, out string reason)
        {
            reason = null;
            string text = (content ?? "").Trim();
            if (text.Length == 0) return false;
            if (mode == "当面告知"
                && (text.Contains("传音") || text.Contains("隔空传声")
                    || text.Contains("远隔传声") || text.Contains("千里传声")))
            {
                reason = "双方同地，应当面说话，但内容声称正在传音";
                return true;
            }
            if (mode == "千里传音"
                && (text.Contains("当面") || text.Contains("面对面")
                    || text.Contains("站在你面前") || text.Contains("走到你身边")))
            {
                reason = "双方异地，只能千里传音，但内容声称正在当面交谈";
                return true;
            }
            return false;
        }

        private static string CompanionSceneEndpointName(CompanionSceneContext scene, NpcSnapshot snap, int id)
        {
            if (snap != null && id == snap.NpcId) return string.IsNullOrWhiteSpace(snap.Name) ? ("#" + id) : snap.Name;
            if (snap != null && id == snap.TaiwuId) return "太吾";
            string name;
            return scene != null && scene.Names != null && scene.Names.TryGetValue(id, out name)
                && !string.IsNullOrWhiteSpace(name) ? name : ("#" + id);
        }

        private static int ParseCompanionSceneId(string raw)
            => JianghuYouling.Core.Text.CharacterReferenceParser.ParseId(raw);

        private static IEnumerator WaitRpc(Func<bool> done, MutationLease lease)
        {
            // JHYL_COMPANION_MUTATION_FAIL_CLOSED_RECOVERABLE: invalidating a world/batch must
            // not make an already-dispatched mutation look failed. Keep waiting for its callback
            // or for the lease-owned watchdog to mark it unknown. The watchdog is independent of
            // this coroutine, so a destroyed host cannot strand _running forever.
            while (!SafeRpcDone(done) && lease != null && !lease.IsCompleted) yield return null;
        }

        private static bool SafeRpcDone(Func<bool> done)
        {
            if (done == null) return false;
            try { return done(); }
            catch { return false; }
        }

        private static bool LooksLikeSelf(string s, string name)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            return s == "我" || s == "自己" || s == "本人" || (!string.IsNullOrWhiteSpace(name) && s == name.Trim());
        }

        private static bool LooksLikeTaiwu(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            return s == "太吾" || s == "太吾传人" || s == "玩家" || s == "少侠" || s == "你" || s == "你自己" || s.Contains("太吾");
        }

        private static void AddStoryReceipt(List<StoryProjectionReceipt> receipts, string tool,
            string operationId, int actorId, int targetId, string actorName, string targetName,
            string asset, string summary)
        {
            if (receipts == null || !OperationId.IsValid(operationId) || actorId <= 0 || targetId < 0) return;
            string kind = StoryProjectionValidator.KindForTool(tool);
            if (string.IsNullOrWhiteSpace(kind)) return;
            foreach (StoryProjectionReceipt existing in receipts)
                if (existing != null && string.Equals(existing.OperationId, operationId, StringComparison.Ordinal)) return;
            receipts.Add(new StoryProjectionReceipt
            {
                Kind = kind,
                OperationId = operationId,
                ActorId = actorId,
                TargetId = targetId,
                ActorName = string.IsNullOrWhiteSpace(actorName) ? ("#" + actorId) : actorName.Trim(),
                TargetName = targetId <= 0 ? "" : (string.IsNullOrWhiteSpace(targetName) ? ("#" + targetId) : targetName.Trim()),
                Asset = (asset ?? "").Trim(),
                Summary = summary,
            });
        }

        private static void AddOutcome(List<string> outcomes, bool ok, string text)
        {
            if (outcomes == null || string.IsNullOrWhiteSpace(text)) return;
            outcomes.Add((ok ? "成功:" : "失败:") + text.Trim());
        }

        private static string FallbackStory(CompanionMonthlyResult r)
            => DeterministicStory(r, null);

        private static string DeterministicStory(CompanionMonthlyResult r, IList<CompanionPlanStep> steps)
        {
            string factual = null;
            if (r != null && r.Outcomes != null && r.Outcomes.Count > 0)
            {
                if (StoryProjectionValidator.TryBuildDurableProjection(r.Outcomes, r.StoryReceipts,
                    r.AuthorizedParticipantIds, out string durable, out _)) factual = durable;
                else factual = StoryProjectionValidator.BuildNonFactualFallback(r.Outcomes);
            }
            if (string.IsNullOrWhiteSpace(factual))
            {
                string name = string.IsNullOrWhiteSpace(r?.Name) ? "这名同道" : r.Name.Trim();
                return name + "本月的打算没有形成任何可确认的结果。未曾发生、尚未证实的事，"
                    + "不会被写成既定经历。";
            }
            // 兜底只投影已经确认的具体行动，不再给每个人套同一段天气开场、
            // “尘埃稍定”收尾或下月预告。玩家应先看到人物做了什么。
            return factual.Trim();
        }

        private static string MakeSummary(CompanionMonthlyResult r)
        {
            if (r.Outcomes.Count > 0)
            {
                string s = r.Outcomes[0];
                int p = s.IndexOf(':');
                if (p >= 0 && p + 1 < s.Length) s = s.Substring(p + 1);
                return Trim(s, 42);
            }
            return Trim(r.Detail, 42);
        }

        private static bool FinalizeCompanionBatchProjection(int taiwuId, int npcId, int date,
            CompanionMonthlyResult r, string batchId, int generation, long conversationClearEpoch)
        {
            if (!WorldLifecycle.IsSameWorld(generation)
                || TalkOrchestrator.CaptureConversationClearEpoch(taiwuId, npcId) != conversationClearEpoch
                || r == null || string.IsNullOrWhiteSpace(batchId)) return false;
            // 同道过月只投影权威工具回执，不再保存模型正文；聊天历史仍保留
            // 完整结果，供之后对话承接。
            string authoritativeProjection = DeterministicStory(r, null);
            r.Detail = null;
            string text = "【主动行事】" + authoritativeProjection;
            string path = MutationJournalPath(WorldLifecycle.WorldId);
            string outcomesJson = JsonConvert.SerializeObject(r.Outcomes ?? new List<string>(), Formatting.None);
            bool hasJournalEntries;
            if (!TryGetCompanionBatchJournalState(path, batchId, out hasJournalEntries)) return false;
            if (!CommitCompanionActorOutcomeMemory(taiwuId, npcId, date, batchId, r.Outcomes)
                || !CommitCompanionTargetAwareness(taiwuId, date, r.StoryReceipts)) return false;
            if (hasJournalEntries && !PrepareCompanionBatchProjection(path, batchId, text, outcomesJson)) return false;
            try
            {
                bool alreadyProjected = ConversationContainsExactNpcProjection(taiwuId, npcId, date, text);
                List<string> successfulActions = SuccessfulOutcomeActions(r.Outcomes);
                bool saved = alreadyProjected
                    ? TalkOrchestrator.TryUpdateExistingConversationNameIfCurrent(
                        taiwuId, npcId, r.Name, generation)
                    : TalkOrchestrator.AppendNpcLineIfCurrent(taiwuId, npcId, text,
                        date, generation, conversationClearEpoch, successfulActions, r.Outcomes, r.Name);
                if (!saved || !WorldLifecycle.IsSameWorld(generation)
                    || TalkOrchestrator.CaptureConversationClearEpoch(taiwuId, npcId) != conversationClearEpoch)
                    return false;
                if (hasJournalEntries)
                {
                    if (!CommitCompanionBatchProjection(path, batchId)) return false;
                    AcknowledgeProjectedCompanionBatch(path, batchId);
                }
                return true;
            }
            catch { return false; }
        }

        private static List<string> SuccessfulOutcomeActions(IList<string> outcomes)
        {
            var result = new List<string>();
            if (outcomes == null) return result;
            foreach (string raw in outcomes)
            {
                string value = (raw ?? string.Empty).Trim();
                if (!value.StartsWith("成功:", StringComparison.Ordinal)) continue;
                value = value.Substring(3).Trim();
                if (value.Length > 0 && !result.Contains(value)) result.Add(value);
            }
            return result;
        }

        private static bool TryGetCompanionBatchJournalState(string path, string batchId, out bool hasEntries)
        {
            hasEntries = false;
            lock (JournalIo)
            {
                var doc = LoadMutationJournal(path);
                if (doc == null || doc.Entries == null) return false;
                foreach (var entry in doc.Entries)
                    if (entry != null && entry.BatchId == batchId) { hasEntries = true; break; }
                return true;
            }
        }

        private static bool TryLoadProjectedCompanionBatch(string path, string batchId,
            out CompanionMonthlyResult result)
        {
            result = null;
            lock (JournalIo)
            {
                var doc = LoadMutationJournal(path);
                if (doc == null || doc.Entries == null) return false;
                MutationJournalRecord projected = null;
                foreach (var entry in doc.Entries)
                    if (entry != null && entry.BatchId == batchId && entry.ProjectionCommitted
                        && !string.IsNullOrWhiteSpace(entry.ProjectionText))
                    { projected = entry; break; }
                if (projected == null) return false;
                if (TalkOrchestrator.CaptureConversationClearEpoch(projected.TaiwuId, projected.NpcId)
                    != projected.ConversationClearEpoch)
                {
                    // 清空聊天不允许重放副作用，但也不应把旧故事重新送回本月弹窗。
                    result = null;
                    return true;
                }
                result = new CompanionMonthlyResult
                {
                    NpcId = projected.NpcId,
                    Name = string.IsNullOrWhiteSpace(projected.ActorName) ? ("#" + projected.NpcId) : projected.ActorName,
                    LocationText = projected.ActorLocation,
                    Detail = null,
                };
                if (!string.IsNullOrWhiteSpace(projected.ProjectionOutcomesJson))
                    try
                    {
                        foreach (var token in JArray.Parse(projected.ProjectionOutcomesJson))
                            if (token != null && !string.IsNullOrWhiteSpace(token.ToString())) result.Outcomes.Add(token.ToString());
                    }
                    catch { result = null; return false; }
                result.Summary = result.Outcomes.Count > 0 ? Trim(result.Outcomes[0], 42) : Trim(result.Detail, 42);
                return true;
            }
        }

        private static IEnumerator ValidateProjectedCompanionBatchAuthority(string path, string batchId,
            int generation, Func<bool> stillCurrent, Action<ProjectionAuthorityState> onDone)
        {
            var operationIds = new HashSet<string>(StringComparer.Ordinal);
            uint worldId = 0;
            int taiwuId = 0;
            bool journalInvalid = false;
            lock (JournalIo)
            {
                MutationJournalDocument doc = LoadMutationJournal(path);
                if (doc == null || doc.Entries == null)
                    journalInvalid = true;
                else
                    foreach (MutationJournalRecord entry in doc.Entries)
                    {
                        if (entry == null || entry.BatchId != batchId || !entry.ProjectionCommitted
                            || entry.Status != "succeeded") continue;
                        if (!OperationId.IsValid(entry.OperationId) || entry.WorldId == 0 || entry.TaiwuId <= 0)
                        { journalInvalid = true; break; }
                        if (worldId == 0) { worldId = entry.WorldId; taiwuId = entry.TaiwuId; }
                        if (entry.WorldId != worldId || entry.TaiwuId != taiwuId)
                        { journalInvalid = true; break; }
                        operationIds.Add(entry.OperationId);
                    }
            }
            if (journalInvalid)
            {
                onDone?.Invoke(ProjectionAuthorityState.Unavailable);
                yield break;
            }

            foreach (string operationId in operationIds)
            {
                if (!WorldLifecycle.IsSameWorld(generation) || !PredicateIsCurrent(stillCurrent))
                {
                    onDone?.Invoke(ProjectionAuthorityState.Unavailable);
                    yield break;
                }
                ToolOutcome outcome = null;
                bool completed = false;
                EffectHandler.QueryOperation(worldId, taiwuId, operationId,
                    value => { outcome = value; completed = true; });
                float deadline = Time.unscaledTime + OperationReconcileWaitSeconds;
                while (!completed && Time.unscaledTime < deadline
                    && WorldLifecycle.IsSameWorld(generation) && PredicateIsCurrent(stillCurrent))
                    yield return null;
                if (!completed || outcome == null || !WorldLifecycle.IsSameWorld(generation)
                    || !PredicateIsCurrent(stillCurrent))
                {
                    onDone?.Invoke(ProjectionAuthorityState.Unavailable);
                    yield break;
                }
                if (IsOperationNotFound(outcome) || outcome.IsTerminal && !outcome.IsSucceeded)
                {
                    onDone?.Invoke(ProjectionAuthorityState.StaleAfterSaveRollback);
                    yield break;
                }
                if (!outcome.IsSucceeded
                    || !string.Equals(outcome.OperationId, operationId, StringComparison.Ordinal))
                {
                    onDone?.Invoke(ProjectionAuthorityState.Unavailable);
                    yield break;
                }
            }
            onDone?.Invoke(ProjectionAuthorityState.Current);
        }

        private static bool InvalidateStaleProjectedCompanionBatch(string path, string batchId, int date)
        {
            string projectionText = null;
            var mutationKeys = new List<string>();
            int taiwuId = 0;
            int npcId = 0;
            lock (JournalIo)
            {
                MutationJournalDocument doc = LoadMutationJournal(path);
                if (doc == null || doc.Entries == null) return false;
                bool found = false;
                for (int i = doc.Entries.Count - 1; i >= 0; i--)
                {
                    MutationJournalRecord entry = doc.Entries[i];
                    if (entry == null || entry.BatchId != batchId) continue;
                    found = true;
                    if (string.IsNullOrWhiteSpace(projectionText)
                        && !string.IsNullOrWhiteSpace(entry.ProjectionText))
                        projectionText = entry.ProjectionText;
                    if (taiwuId <= 0) taiwuId = entry.TaiwuId;
                    if (npcId <= 0) npcId = entry.NpcId;
                    if (!string.IsNullOrWhiteSpace(entry.MutationKey)) mutationKeys.Add(entry.MutationKey);
                    doc.Entries.RemoveAt(i);
                }
                if (!found) return true;
                doc.Revision++;
                if (!SaveMutationJournalDocument(path, doc)) return false;
            }
            lock (MutationGate)
                foreach (string key in mutationKeys) UnknownMutationQuarantine.Remove(key);
            if (!string.IsNullOrWhiteSpace(projectionText) && taiwuId > 0 && npcId > 0
                && !TalkOrchestrator.RemoveExactNpcLineIfCurrent(taiwuId, npcId, projectionText, date))
                return false;
            return true;
        }

        private static bool CheckpointCompanionBatchOutcomes(string path, string batchId,
            IList<string> outcomes, IList<StoryProjectionReceipt> receipts)
        {
            lock (JournalIo)
            {
                var doc = LoadMutationJournal(path);
                if (doc == null || doc.Entries == null) return false;
                bool found = false;
                var batchEntries = new List<MutationJournalRecord>();
                string json = JsonConvert.SerializeObject(outcomes ?? new List<string>(), Formatting.None);
                string receiptsJson = JsonConvert.SerializeObject(receipts
                    ?? new List<StoryProjectionReceipt>(), Formatting.None);
                foreach (var entry in doc.Entries)
                {
                    if (entry == null || entry.BatchId != batchId) continue;
                    found = true;
                    batchEntries.Add(entry);
                }
                if (found)
                {
                    if (!TryCollectBatchProjectionAuthority(batchEntries, out HashSet<int> authority)
                        || !CompanionProjectionReceiptsMatchJournal(batchEntries, outcomes, receipts, authority))
                        return false;
                }
                foreach (var entry in batchEntries)
                {
                    entry.ProjectionOutcomesJson = json;
                    entry.ProjectionReceiptsJson = receiptsJson;
                    entry.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                }
                if (!found) return true; // 本轮只有本地前置失败，没有后端 mutation journal。
                doc.Revision++;
                return SaveMutationJournalDocument(path, doc);
            }
        }

        private static bool ConversationContainsExactNpcProjection(int taiwuId, int npcId, int date, string text)
        {
            try
            {
                var turns = TalkOrchestrator.History(taiwuId, npcId);
                if (turns == null) return false;
                foreach (var turn in turns)
                    if (turn != null && !turn.FromPlayer && !TalkTurnKinds.IsNative(turn) && turn.Date == date
                        && string.Equals((turn.Text ?? "").Trim(), (text ?? "").Trim(), StringComparison.Ordinal))
                        return true;
            }
            catch { }
            return false;
        }

        private static bool PrepareCompanionBatchProjection(string path, string batchId, string projectionText,
            string outcomesJson)
        {
            lock (JournalIo)
            {
                var doc = LoadMutationJournal(path);
                if (doc == null || doc.Entries == null) return false;
                bool found = false;
                foreach (var entry in doc.Entries)
                {
                    if (entry == null || entry.BatchId != batchId) continue;
                    found = true;
                    entry.ProjectionText = projectionText;
                    entry.ProjectionOutcomesJson = outcomesJson;
                    entry.ProjectionCommitted = false;
                    entry.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                }
                if (!found) return false;
                doc.Revision++;
                return SaveMutationJournalDocument(path, doc);
            }
        }

        private static bool CommitCompanionBatchProjection(string path, string batchId)
        {
            lock (JournalIo)
            {
                var doc = LoadMutationJournal(path);
                if (doc == null || doc.Entries == null) return false;
                bool found = false;
                foreach (var entry in doc.Entries)
                {
                    if (entry == null || entry.BatchId != batchId) continue;
                    if (string.IsNullOrWhiteSpace(entry.ProjectionText)) return false;
                    found = true;
                    entry.ProjectionCommitted = true;
                    entry.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                }
                if (!found) return false;
                doc.Revision++;
                return SaveMutationJournalDocument(path, doc);
            }
        }

        private static void AcknowledgeProjectedCompanionBatch(string path, string batchId)
        {
            var requests = new List<MutationAckRequest>();
            var operationIds = new HashSet<string>(StringComparer.Ordinal);
            lock (JournalIo)
            {
                var doc = LoadMutationJournal(path);
                if (doc == null || doc.Entries == null) return;
                foreach (var entry in doc.Entries)
                    if (entry != null && entry.BatchId == batchId && entry.ProjectionCommitted
                        && IsAcknowledgeableMutation(entry)
                        && entry.WorldId > 0 && entry.TaiwuId > 0 && OperationId.IsValid(entry.OperationId)
                        && operationIds.Add(entry.OperationId))
                        requests.Add(new MutationAckRequest
                        {
                            WorldId = entry.WorldId,
                            TaiwuId = entry.TaiwuId,
                            OperationId = entry.OperationId,
                        });
            }
            foreach (var request in requests)
            {
                bool outboxCommitted = EffectHandler.AcknowledgeOperation(
                    request.WorldId, request.TaiwuId, request.OperationId, ok =>
                {
                    if (!ok) Debug.LogWarning("[江湖有灵] 同道已投影终态 ACK 暂未完成 op=" + request.OperationId);
                });
                if (outboxCommitted && !MarkMutationAckOutboxCommitted(path, request.OperationId))
                    Debug.LogWarning("[江湖有灵] 同道 ACK outbox 已接管，但 journal 接管标记暂未写回 op=" + request.OperationId);
            }
        }

        private static void AcknowledgeAllProjectedCompanionOutcomes(string path)
        {
            var requests = new List<MutationAckRequest>();
            var operationIds = new HashSet<string>(StringComparer.Ordinal);
            lock (JournalIo)
            {
                var doc = LoadMutationJournal(path);
                if (doc == null || doc.Entries == null) return;
                foreach (var entry in doc.Entries)
                    if (entry != null && entry.WorldId == WorldLifecycle.WorldId && entry.TaiwuId > 0
                        && entry.ProjectionCommitted
                        && IsAcknowledgeableMutation(entry)
                        && OperationId.IsValid(entry.OperationId) && operationIds.Add(entry.OperationId))
                        requests.Add(new MutationAckRequest
                        {
                            WorldId = entry.WorldId,
                            TaiwuId = entry.TaiwuId,
                            OperationId = entry.OperationId,
                        });
            }
            foreach (var request in requests)
            {
                bool outboxCommitted = EffectHandler.AcknowledgeOperation(
                    request.WorldId, request.TaiwuId, request.OperationId, ok =>
                {
                    if (!ok) Debug.LogWarning("[江湖有灵] 同道启动恢复 ACK 暂未完成 op=" + request.OperationId);
                });
                if (outboxCommitted && !MarkMutationAckOutboxCommitted(path, request.OperationId))
                    Debug.LogWarning("[江湖有灵] 同道恢复 ACK outbox 已接管，但 journal 标记暂未写回 op=" + request.OperationId);
            }
        }

        private static bool MarkMutationAckOutboxCommitted(string path, string operationId)
        {
            if (string.IsNullOrWhiteSpace(path) || !OperationId.IsValid(operationId)) return false;
            lock (JournalIo)
            {
                var doc = LoadMutationJournal(path);
                if (doc == null || doc.Entries == null) return false;
                MutationJournalRecord found = null;
                foreach (var entry in doc.Entries)
                    if (entry != null && string.Equals(entry.OperationId, operationId, StringComparison.Ordinal))
                    { found = entry; break; }
                if (found == null) return false;
                if (found.AckOutboxCommitted) return true;
                found.AckOutboxCommitted = true;
                found.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                doc.Revision++;
                return SaveMutationJournalDocument(path, doc);
            }
        }

        private static bool IsTerminalMutationStatus(string status, bool retryable)
            => status == "succeeded" || status == "failed" || status == "rejected" || status == "canceled"
                || status == "unknown" && !retryable;

        private static bool IsAcknowledgeableMutation(MutationJournalRecord entry)
            => entry != null && IsTerminalMutationStatus(entry.Status, entry.Retryable)
                && entry.BackendReceiptStored && !string.IsNullOrWhiteSpace(entry.Receipt)
                && !entry.AckOutboxCommitted
                && !string.Equals(entry.Code, "CANCELED_BEFORE_DISPATCH", StringComparison.Ordinal);

        private static IEnumerator RefreshActorPreflightState(string toolName, NpcSnapshot snap,
            Func<bool> stillCurrent)
        {
            if (snap == null || stillCurrent != null && !stillCurrent()) yield break;
            switch (NormalizePlanToolName(toolName))
            {
                case "gift_item":
                case "gift_silver":
                case "barter":
                case "change_equipment":
                case "read_book":
                    yield return NpcSnapshotReader.FetchGiftables(snap.NpcId, snap, stillCurrent);
                    if (stillCurrent == null || stillCurrent())
                        yield return NpcSnapshotReader.FetchStudyProgress(snap.NpcId, snap, stillCurrent);
                    break;
                case "use_item":
                    yield return NpcSnapshotReader.FetchUsableItems(snap.NpcId, snap, stillCurrent);
                    break;
                case "flip_practice":
                case "train_skill":
                    yield return NpcSnapshotReader.FetchSkills(snap.NpcId, snap, stillCurrent);
                    if (NormalizePlanToolName(toolName) == "train_skill"
                        && (stillCurrent == null || stillCurrent()))
                        yield return NpcSnapshotReader.FetchStudyProgress(snap.NpcId, snap, stillCurrent);
                    break;
                case "write_book":
                case "teach":
                    yield return NpcSnapshotReader.FetchSkills(snap.NpcId, snap, stillCurrent);
                    if (stillCurrent == null || stillCurrent())
                        yield return NpcSnapshotReader.FetchLifeSkills(snap.NpcId, snap, stillCurrent);
                    break;
            }
        }

        /// <summary>
        /// 对已经随快照权威预载的硬前置做零 RPC 判定。模型即使无视清单臆造物品，
        /// 也会在 mutation lease/operation id 创建前被挡回，并把具体可选项交给失败重规划。
        /// </summary>
        private static bool TryPreflightCompanionStep(string name, JObject args, NpcSnapshot snap,
            out string failure)
        {
            failure = null;
            if (snap == null) { failure = "行动者快照不可用"; return false; }
            args = args ?? new JObject();
            string Str(string key) => args[key] == null ? "" : args[key].ToString().Trim();
            int Int(string key, int fallback)
            {
                if (args[key] == null) return fallback;
                try { return args[key].Value<int>(); }
                catch { int value; return int.TryParse(args[key].ToString(), out value) ? value : fallback; }
            }
            GiftableItem holding = null;
            switch (NormalizePlanToolName(name))
            {
                case "gift_item":
                {
                    string item = Str("item"); int amount = Math.Max(1, Int("amount", 1));
                    if (snap.HoldingsLoaded
                        && !TryFindHolding(snap.GiftableItems, item, amount, out holding))
                    {
                        failure = "自己当前权威持有清单里没有「" + item + "」x" + amount
                            + "；可见持有物:" + ListGiftables(snap);
                        return false;
                    }
                    if (snap.HoldingsLoaded && holding != null
                        && holding.NonEquippedCount < amount)
                    {
                        failure = "「" + item + "」只有当前穿戴的数量，不能在自主过月中卸下赠送；请换背包、资源或熟食";
                        return false;
                    }
                    break;
                }
                case "gift_silver":
                {
                    int amount = Math.Max(1, Int("amount", 1));
                    if (snap.InventoryLoaded && snap.Silver < amount)
                    { failure = "自己只有银钱" + snap.Silver + "，不足以赠出" + amount; return false; }
                    break;
                }
                case "barter":
                {
                    string item = Str("give_item"); int amount = Math.Max(1, Int("give_amount", 1));
                    if (snap.HoldingsLoaded
                        && !TryFindHoldingOrSilver(snap, item, amount, out holding))
                    {
                        failure = "自己当前权威持有清单里没有可交换的「" + item + "」x" + amount
                            + "；可见持有物:" + ListGiftables(snap);
                        return false;
                    }
                    break;
                }
                case "change_equipment":
                {
                    string action = Str("action").ToLowerInvariant();
                    string part = Str("part").ToLowerInvariant();
                    if (action != "on" && action != "off"
                        || action == "off" && !IsEquipmentPart(part))
                    {
                        failure = "换装动作或卸下部位无效；只能换上物品，或卸下兵器/护具/佩饰/代步";
                        return false;
                    }
                    if (action == "on" && snap.InventoryLoaded)
                    {
                        string item = Str("item");
                        if (!TryFindHolding(snap.EquipableInventoryItems, item, 1, out holding))
                        {
                            failure = "背包里没有可换上的装备「" + item + "」；真实可选:" + ListEquipableInventory(snap);
                            return false;
                        }
                    }
                    else if (action == "off" && snap.EquipmentLoaded)
                    {
                        if (string.IsNullOrWhiteSpace(part) || !snap.EquippedParts.Contains(part))
                        {
                            failure = "当前「" + (part.Length == 0 ? "未指定部位" : part)
                                + "」没有装备可卸；真实穿戴:" + ListEquipped(snap);
                            return false;
                        }
                    }
                    break;
                }
                case "write_book":
                case "teach":
                    if (!IsSkillKind(Str("type")) || FindKnownSkill(snap, Str("type"), Str("skill")) == null)
                    { failure = "自己并未学会指定武学/技艺；只能从已会清单选择"; return false; }
                    if (NormalizePlanToolName(name) == "teach" && LooksLikeTaiwu(Str("target")))
                    { failure = "亲授不能用于太吾；应改用 write_book 回忆成秘籍交给太吾"; return false; }
                    break;
                case "matchmake":
                    if (Str("target").Length == 0) { failure = "婚配对象不能为空"; return false; }
                    break;
                case "set_relation":
                {
                    string action = Str("action").ToLowerInvariant();
                    if (action != "befriend" && action != "swear_sibling" && action != "apprentice"
                        && action != "take_disciple" && action != "lover" && action != "spouse"
                        && action != "adoptive_parent" && action != "adoptive_child"
                        && action != "recognize" && action != "follow")
                    { failure = "与太吾的关系动作无效"; return false; }
                    break;
                }
                case "flip_practice":
                {
                    LearnableSkill selected = FindKnownSkill(snap, "combat", Str("skill"));
                    if (selected == null)
                    { failure = "自己并未学会指定功法，不能颠倒正逆练"; return false; }
                    if (snap.FlipPracticeEligibilityLoaded
                        && !snap.FlippableCombatSkillIds.Contains(selected.TemplateId))
                    {
                        failure = "指定功法尚未突破、没有激活常页或未读对应反页，不能颠倒；当前可选："
                            + ListFlippableSkills(snap);
                        return false;
                    }
                    break;
                }
                case "train_skill":
                {
                    LearnableSkill selected = FindKnownSkill(snap, "combat", Str("skill"));
                    if (selected == null)
                    {
                        failure = "自己并未学会指定武学，不能修炼；当前可选："
                            + ListTrainableSkills(snap);
                        return false;
                    }
                    if (snap.StudyProgressLoaded
                        && !snap.IncompleteTrainingSkillIds.Contains(selected.TemplateId))
                    {
                        failure = "指定武学已经修炼完整；当前尚未练满可选："
                            + ListTrainableSkills(snap);
                        return false;
                    }
                    break;
                }
                case "read_book":
                {
                    string book = Str("book");
                    if (snap.StudyProgressLoaded && !UnreadBookNameHit(snap, book))
                    {
                        failure = "指定书籍已经读完或不在当前未读完候选中；当前可选："
                            + ListReadableBooks(snap);
                        return false;
                    }
                    if (snap.HoldingsLoaded
                        && (!TryFindHolding(snap.GiftableItems, book, 1, out holding)
                            || holding == null || holding.Key.ItemType != 10))
                    {
                        failure = "自己当前背包中没有可读的书籍「" + book
                            + "」；当前可选：" + ListReadableBooks(snap);
                        return false;
                    }
                    break;
                }
                case "use_item":
                {
                    string item = Str("item");
                    if (!snap.UsableItemsLoaded)
                    {
                        failure = "当前可用物品读取失败，不能把空清单当成没有候选";
                        return false;
                    }
                    if (!UsableItemNameHit(snap, item))
                    {
                        failure = "指定物品不在当前可自行使用候选中；当前可选："
                            + ListUsableItems(snap);
                        return false;
                    }
                    break;
                }
                case "relate":
                {
                    string target = Str("target");
                    int mask = RelationMask(Str("kind"));
                    if (mask == 0)
                    { failure = "要缔结的关系类型无效"; return false; }
                    if ((target.Length == 0 || LooksLikeTaiwu(target)) && mask != 0
                        && (snap.RelationFlag & mask) != 0)
                    {
                        failure = "自己与太吾已经存在该类关系；当前关系:" + (snap.Relation ?? "未知");
                        return false;
                    }
                    break;
                }
                case "dissolve_relation":
                {
                    string target = Str("target");
                    int mask = RelationMask(Str("relation"));
                    if (mask == 0)
                    { failure = "要解除的关系类型无效"; return false; }
                    if ((target.Length == 0 || LooksLikeTaiwu(target)) && mask != 0
                        && (snap.RelationFlag & mask) == 0)
                    {
                        failure = "自己与太吾并不存在要解除的该类关系；当前关系:" + (snap.Relation ?? "无特殊关系");
                        return false;
                    }
                    break;
                }
                case "sect_support":
                    if (!snap.IsSect) { failure = "自己当前并非门派中人"; return false; }
                    break;
                case "add_feature":
                {
                    string feature = Str("feature");
                    if (snap.Features != null && snap.Features.Exists(x =>
                        string.Equals((x ?? "").Trim(), feature, StringComparison.OrdinalIgnoreCase)))
                    { failure = "自己已经具有品性「" + feature + "」，无需重复增加"; return false; }
                    break;
                }
                case "adjust_favor":
                    if (Int("delta", 0) == 0)
                    { failure = "好感变化为零，不构成真实行动"; return false; }
                    break;
                case "adjust_mood":
                {
                    int delta = Int("delta", 0);
                    if (Str("target").Length == 0 || Str("reason").Length < 2
                        || delta == 0 || delta < -30 || delta > 30)
                    {
                        failure = "心情变化需要明确对象、具体原因和 -30..30 的非零变化值";
                        return false;
                    }
                    break;
                }
                case "adjust_fame":
                {
                    int delta = Int("delta", 0);
                    if (Str("target").Length == 0 || Str("reason").Length < 2
                        || delta == 0 || delta < -12 || delta > 12 || delta % 3 != 0)
                    {
                        failure = "名望变化需要明确对象、公开事迹原因，以及 ±3/±6/±9/±12 的变化值";
                        return false;
                    }
                    break;
                }
            }
            return true;
        }

        private static int RelationMask(string relation)
        {
            switch ((relation ?? "").Trim().ToLowerInvariant())
            {
                case "befriend":
                case "friend": return 8192;
                case "sworn": return 512;
                case "mentor": return 2048 | 4096;
                case "adoptive_parent": return 64;
                case "adoptive_child": return 128;
                case "lover": return 16384;
                case "spouse": return 1024;
                default: return 0;
            }
        }

        private static bool IsRelationKind(string kind)
        {
            kind = (kind ?? "").Trim().ToLowerInvariant();
            return kind == "befriend" || kind == "sworn" || kind == "mentor"
                || kind == "adoptive_parent" || kind == "adoptive_child"
                || kind == "lover" || kind == "spouse";
        }

        private static bool IsDissolvableRelation(string relation)
        {
            relation = (relation ?? "").Trim().ToLowerInvariant();
            return relation == "friend" || relation == "sworn" || relation == "mentor"
                || relation == "adoptive_parent" || relation == "adoptive_child"
                || relation == "lover" || relation == "spouse";
        }

        private static bool IsSkillKind(string kind)
        {
            kind = (kind ?? "").Trim().ToLowerInvariant();
            return kind == "combat" || kind == "life";
        }

        private static IEnumerator FetchInventoryForPreflight(int characterId, Func<bool> stillCurrent,
            Action<NpcSnapshot> done)
        {
            if (characterId <= 0 || stillCurrent != null && !stillCurrent())
            { done?.Invoke(null); yield break; }
            var fresh = new NpcSnapshot { NpcId = characterId };
            yield return NpcSnapshotReader.FetchGiftables(characterId, fresh, stillCurrent);
            if (stillCurrent != null && !stillCurrent()) { done?.Invoke(null); yield break; }
            done?.Invoke(fresh);
        }

        private static bool TryFindHoldingOrSilver(NpcSnapshot snap, string requested, int amount,
            out GiftableItem holding)
        {
            holding = null;
            if (snap == null) return false;
            if (LooksLikeSilver(requested)) return snap.Silver >= Math.Max(1, amount);
            return TryFindHolding(snap.GiftableItems, requested, amount, out holding);
        }

        private static bool LooksLikeSilver(string name)
        {
            string text = (name ?? "").Trim();
            return text == "银钱" || text == "钱" || text == "银两" || text == "银子";
        }

        private static bool TryFindHolding(IList<GiftableItem> holdings, string requested, int amount,
            out GiftableItem found)
        {
            found = null;
            string query = JianghuYouling.Shared.ItemNameMatcher.StripCountSuffix((requested ?? "").Trim());
            if (query.Length == 0 || holdings == null) return false;
            GiftableItem best = null;
            foreach (GiftableItem item in holdings)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Name)) continue;
                string name = item.Name.Trim();
                if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase))
                {
                    found = item;
                    return Math.Max(1, item.Count) >= Math.Max(1, amount);
                }
                // 与后端 ResolveNpcItemByName 使用同一规则：模糊命中取最长、最具体的
                // 真实物名。不能依赖快照枚举顺序选“第一个包含项”，否则前置预查会
                // 放行甲物，后端却按乙物落地，造成无谓失败或数量判断不一致。
                if (JianghuYouling.Shared.ItemNameMatcher.IsHit(name, query)
                    && (best == null || name.Length > (best.Name ?? string.Empty).Trim().Length))
                    best = item;
            }
            if (best == null || Math.Max(1, best.Count) < Math.Max(1, amount)) return false;
            found = best;
            return true;
        }

        private static bool HasPoison(NpcSnapshot snap)
        {
            if (snap == null) return false;
            if (snap.HasPoison) return true;
            if (snap.GiftableItems != null)
                foreach (var g in snap.GiftableItems)
                    if (g != null && !string.IsNullOrWhiteSpace(g.Name) && g.Name.Contains("毒")) return true;
            return false;
        }

        private static bool HasGiftableItem(NpcSnapshot snap)
        {
            if (snap?.GiftableItems == null) return false;
            foreach (GiftableItem item in snap.GiftableItems)
                if (item != null && item.NonEquippedCount > 0) return true;
            return false;
        }

        private static bool CommitCompanionActorOutcomeMemory(int taiwuId, int npcId, int date,
            string batchId, IList<string> outcomes)
        {
            if (taiwuId <= 0 || npcId <= 0 || string.IsNullOrWhiteSpace(batchId)) return false;
            if (outcomes == null || outcomes.Count == 0) return true;
            string sourceId = batchId + ":actor:" + npcId;
            var content = new StringBuilder("本月真实工具结果：");
            foreach (string raw in outcomes)
            {
                string outcome = GlyphSanitizer.Clean(raw ?? string.Empty).Trim();
                if (outcome.Length == 0) continue;
                content.Append("\n- ").Append(Trim(outcome, 600));
                if (content.Length >= 12000) break;
            }
            if (content.Length <= "本月真实工具结果：".Length) return true;
            try
            {
                NpcMemoryStore memory = NpcMemoryStore.Load(JianghuYoulingPaths.Memories,
                    taiwuId.ToString(), npcId.ToString());
                if (!memory.LoadReliable) return false;
                MemoryEntry stored = memory.AddOrMerge(new MemoryEntry
                {
                    Content = content.ToString(),
                    Type = MemoryType.Event,
                    Keywords = "过月主动行事,工具结果",
                    Importance = 5,
                    WorldDate = date,
                    Valid = true,
                    SourceKind = "companion_monthly_outcomes",
                    SourceId = sourceId,
                }, out _);
                memory.Prune(date);
                return stored != null && memory.Save()
                    && HasCompanionOutcomeMemorySource(memory, sourceId);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[JHYL_COMPANION_ACTOR_MEMORY_FAILED] npc=" + npcId
                    + " batch=" + batchId + " exception=" + e.GetType().Name);
                return false;
            }
        }

        private static bool HasCompanionOutcomeMemorySource(NpcMemoryStore memory, string sourceId)
        {
            if (memory?.All == null || string.IsNullOrWhiteSpace(sourceId)) return false;
            foreach (MemoryEntry entry in memory.All)
                if (entry != null && entry.Valid
                    && string.Equals(entry.SourceKind, "companion_monthly_outcomes",
                        StringComparison.Ordinal)
                    && string.Equals(entry.SourceId, sourceId, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static bool CommitCompanionTargetAwareness(int taiwuId, int date,
            IList<StoryProjectionReceipt> receipts)
        {
            if (taiwuId <= 0 || receipts == null) return taiwuId > 0;
            foreach (StoryProjectionReceipt receipt in receipts)
            {
                if (receipt == null || !OperationId.IsValid(receipt.OperationId)
                    || receipt.TargetId <= 0 || receipt.TargetId == taiwuId
                    || receipt.TargetId == receipt.ActorId
                    || !CompanionTargetAwarenessPolicy.ShouldRemember(receipt.Kind, receipt.Summary))
                    continue;
                string sourceId = CompanionTargetAwarenessSourceId(receipt);
                try
                {
                    NpcMemoryStore memory = NpcMemoryStore.Load(JianghuYoulingPaths.Memories,
                        taiwuId.ToString(), receipt.TargetId.ToString());
                    if (!memory.LoadReliable) return false;
                    MemoryEntry stored = memory.AddOrMerge(new MemoryEntry
                    {
                        Content = "我亲历：" + (receipt.Summary ?? string.Empty).Trim(),
                        Type = MemoryType.Event,
                        Keywords = "过月互动," + (receipt.ActorName ?? string.Empty).Trim(),
                        Importance = 5,
                        WorldDate = date,
                        Valid = true,
                        SourceKind = "companion_monthly_target",
                        SourceId = sourceId,
                    }, out _);
                    if (stored == null || !memory.Save()
                        || !HasCompanionTargetAwarenessSource(memory, sourceId)) return false;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[JHYL_COMPANION_TARGET_MEMORY_FAILED] target="
                        + receipt.TargetId + " op=" + receipt.OperationId
                        + " exception=" + e.GetType().Name);
                    return false;
                }
            }
            return true;
        }

        private static string CompanionTargetAwarenessSourceId(StoryProjectionReceipt receipt)
            => receipt.OperationId + ":target:" + receipt.TargetId;

        private static bool HasCompanionTargetAwarenessSource(NpcMemoryStore memory, string sourceId)
        {
            if (memory?.All == null || string.IsNullOrWhiteSpace(sourceId)) return false;
            foreach (MemoryEntry entry in memory.All)
                if (entry != null && entry.Valid
                    && string.Equals(entry.SourceKind, "companion_monthly_target",
                        StringComparison.Ordinal)
                    && string.Equals(entry.SourceId, sourceId, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static bool HasSilver(NpcSnapshot snap)
        {
            return snap != null && snap.Silver > 0;
        }

        private static bool HasRope(NpcSnapshot snap)
        {
            if (snap == null) return false;
            if (snap.HasRope) return true;
            if (snap.GiftableItems != null)
                foreach (var g in snap.GiftableItems)
                    if (g != null && !string.IsNullOrWhiteSpace(g.Name) && g.Name.Contains("绳")) return true;
            return false;
        }

        private static string ListGiftables(NpcSnapshot snap, int limit = MonthlyInventoryLimit)
        {
            if (snap == null || snap.GiftableItems == null || snap.GiftableItems.Count == 0) return "未见可用物";
            var names = new List<string>();
            foreach (var g in snap.GiftableItems)
            {
                if (g == null || g.NonEquippedCount <= 0 || string.IsNullOrWhiteSpace(g.Name))
                    continue;
                names.Add(g.NonEquippedCount > 1
                    ? (g.Name + "x" + g.NonEquippedCount) : g.Name);
                if (names.Count >= Math.Max(1, limit)) break;
            }
            return names.Count == 0 ? "未见可用物" : string.Join("、", names.ToArray());
        }

        private static string ListReadableBooks(NpcSnapshot snap, int limit = MonthlyInventoryLimit)
        {
            if (snap == null || !snap.StudyProgressLoaded)
                return "权威阅读进度读取未完成，本轮不得臆造书名或假定尚未读完";
            var names = new List<string>();
            if (snap.UnreadBookNames != null)
                foreach (string name in snap.UnreadBookNames)
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    names.Add(name.Trim());
                    if (names.Count >= Math.Max(1, limit)) break;
                }
            return names.Count == 0 ? "无" : string.Join("、", names.ToArray());
        }

        private static bool UnreadBookNameHit(NpcSnapshot snap, string requested)
        {
            if (snap?.UnreadBookNames == null) return false;
            string query = JianghuYouling.Shared.ItemNameMatcher.StripCountSuffix(
                (requested ?? string.Empty).Trim());
            foreach (string candidate in snap.UnreadBookNames)
                if (!string.IsNullOrWhiteSpace(candidate)
                    && JianghuYouling.Shared.ItemNameMatcher.IsHit(candidate, query)) return true;
            return false;
        }

        private static string ListUsableItems(NpcSnapshot snap, int limit = MonthlyInventoryLimit)
        {
            if (snap == null || !snap.UsableItemsLoaded)
                return "权威可用物品读取未完成，本轮不得臆造或假定可以使用";
            if (snap.UsableItemNames == null || snap.UsableItemNames.Count == 0) return "无";
            var names = new List<string>();
            foreach (string candidate in snap.UsableItemNames)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                names.Add(candidate.Trim());
                if (names.Count >= Math.Max(1, limit)) break;
            }
            return names.Count == 0 ? "无" : string.Join("、", names.ToArray());
        }

        private static bool UsableItemNameHit(NpcSnapshot snap, string requested)
        {
            if (snap == null || !snap.UsableItemsLoaded || snap.UsableItemNames == null) return false;
            string query = JianghuYouling.Shared.ItemNameMatcher.StripCountSuffix(
                (requested ?? string.Empty).Trim());
            if (query.Length == 0) return false;
            foreach (string candidate in snap.UsableItemNames)
                if (!string.IsNullOrWhiteSpace(candidate)
                    && JianghuYouling.Shared.ItemNameMatcher.IsHit(candidate, query)) return true;
            return false;
        }

        private static string ListEquipableInventory(NpcSnapshot snap, int limit = MonthlyInventoryLimit)
        {
            if (snap == null || !snap.InventoryLoaded) return "权威背包读取未完成，本轮不据此臆断";
            if (snap.EquipableInventoryItems == null || snap.EquipableInventoryItems.Count == 0) return "无";
            var names = new List<string>();
            foreach (GiftableItem item in snap.EquipableInventoryItems)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Name)) continue;
                names.Add(item.Name + (item.Count > 1 ? ("x" + item.Count) : ""));
                if (names.Count >= Math.Max(1, limit)) break;
            }
            return names.Count == 0 ? "无" : string.Join("、", names.ToArray());
        }

        private static string ListEquipped(NpcSnapshot snap)
        {
            if (snap == null || !snap.EquipmentLoaded) return "权威穿戴读取未完成，本轮不据此臆断";
            if (snap.EquippedItemNames == null || snap.EquippedItemNames.Count == 0) return "无";
            var parts = new List<string>();
            if (snap.EquippedParts.Contains("weapon")) parts.Add("weapon");
            if (snap.EquippedParts.Contains("armor")) parts.Add("armor");
            if (snap.EquippedParts.Contains("accessory")) parts.Add("accessory");
            if (snap.EquippedParts.Contains("carrier")) parts.Add("carrier");
            if (snap.EquippedParts.Contains("clothing")) parts.Add("clothing(不可卸贴身衣着)");
            return string.Join("、", snap.EquippedItemNames.ToArray()) + "；非空部位:"
                + (parts.Count == 0 ? "无" : string.Join("/", parts.ToArray()));
        }

        private static bool IsEquipmentPart(string part)
        {
            part = (part ?? "").Trim().ToLowerInvariant();
            return part == "weapon" || part == "armor" || part == "accessory" || part == "carrier";
        }

        private static string ListSkills(List<LearnableSkill> skills, int limit = MonthlySkillLimit)
        {
            if (skills == null || skills.Count == 0) return "未见所习武学";
            var names = new List<string>();
            foreach (var s in skills)
            {
                if (s == null || string.IsNullOrWhiteSpace(s.Name)) continue;
                names.Add(s.Name);
                if (names.Count >= Math.Max(1, limit)) break;
            }
            return names.Count == 0 ? "未见所习武学" : string.Join("、", names.ToArray());
        }

        private static string ListTrainableSkills(NpcSnapshot snap, int limit = MonthlySkillLimit)
        {
            if (snap == null || !snap.StudyProgressLoaded)
                return "权威修炼进度读取未完成，本轮不得臆造武学名或假定尚未练满";
            if (snap.LearnableSkills == null || snap.LearnableSkills.Count == 0
                || snap.IncompleteTrainingSkillIds == null
                || snap.IncompleteTrainingSkillIds.Count == 0) return "无";
            var names = new List<string>();
            foreach (LearnableSkill skill in snap.LearnableSkills)
            {
                if (skill == null || string.IsNullOrWhiteSpace(skill.Name)
                    || !snap.IncompleteTrainingSkillIds.Contains(skill.TemplateId)) continue;
                names.Add(skill.Name);
                if (names.Count >= Math.Max(1, limit)) break;
            }
            return names.Count == 0 ? "无" : string.Join("、", names.ToArray());
        }

        // 传授决策只需要知道「我能教的候选里，对方已经会哪些」。输出受教者的整套所学
        // 既浪费上下文，也可能因通用显示上限漏掉真正冲突的候选；按模板 ID 求交集才精确。
        private static string ListKnownRelevantSkills(List<LearnableSkill> actorSkills,
            List<LearnableSkill> recipientSkills)
        {
            if (actorSkills == null || actorSkills.Count == 0
                || recipientSkills == null || recipientSkills.Count == 0) return "无";
            var recipientIds = new HashSet<short>();
            foreach (LearnableSkill skill in recipientSkills)
                if (skill != null) recipientIds.Add(skill.TemplateId);
            var names = new List<string>();
            foreach (LearnableSkill skill in actorSkills)
            {
                if (skill == null || string.IsNullOrWhiteSpace(skill.Name)
                    || !recipientIds.Contains(skill.TemplateId)) continue;
                names.Add(skill.Name);
                if (names.Count >= MonthlySkillLimit) break;
            }
            return names.Count == 0 ? "无" : string.Join("、", names.ToArray());
        }

        private static string ListUnlearnedRelevantSkills(List<LearnableSkill> actorSkills,
            List<LearnableSkill> recipientSkills)
        {
            if (recipientSkills == null) return "未知，必须重新查询";
            var learned = new HashSet<short>();
            foreach (LearnableSkill skill in recipientSkills)
                if (skill != null) learned.Add(skill.TemplateId);
            var names = new List<string>();
            if (actorSkills != null)
                foreach (LearnableSkill skill in actorSkills)
                {
                    if (skill == null || string.IsNullOrWhiteSpace(skill.Name)
                        || learned.Contains(skill.TemplateId)) continue;
                    names.Add(skill.Name);
                    if (names.Count >= MonthlySkillLimit) break;
                }
            return names.Count == 0 ? "无" : string.Join("、", names.ToArray());
        }

        private static string ListFlippableSkills(NpcSnapshot snap)
        {
            if (snap == null || !snap.FlipPracticeEligibilityLoaded)
                return "权威资格读取未完成，本轮不得臆断";
            if (snap.FlippableCombatSkillIds == null || snap.FlippableCombatSkillIds.Count == 0)
                return "无";
            var names = new List<string>();
            if (snap.LearnableSkills != null)
                foreach (LearnableSkill skill in snap.LearnableSkills)
                {
                    if (skill == null || !snap.FlippableCombatSkillIds.Contains(skill.TemplateId)
                        || string.IsNullOrWhiteSpace(skill.Name)) continue;
                    names.Add(skill.Name);
                    if (names.Count >= MonthlySkillLimit) break;
                }
            return names.Count == 0 ? "无" : string.Join("、", names.ToArray());
        }

        private static LearnableSkill FindKnownSkill(NpcSnapshot snap, string kind, string requested)
        {
            List<LearnableSkill> skills = string.Equals(kind, "life", StringComparison.OrdinalIgnoreCase)
                ? snap?.LearnableLifeSkills : snap?.LearnableSkills;
            string query = (requested ?? "").Trim();
            if (skills == null || query.Length == 0) return null;
            LearnableSkill contains = null;
            foreach (LearnableSkill skill in skills)
            {
                if (skill == null || string.IsNullOrWhiteSpace(skill.Name)) continue;
                if (string.Equals(skill.Name.Trim(), query, StringComparison.OrdinalIgnoreCase)) return skill;
                if (contains == null && (skill.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                    || query.IndexOf(skill.Name, StringComparison.OrdinalIgnoreCase) >= 0)) contains = skill;
            }
            return contains;
        }

        private static string ListSecrets(List<SecretRef> secrets)
        {
            if (secrets == null || secrets.Count == 0) return "暂无可吐露秘闻";
            var lines = new List<string>();
            for (int i = 0; i < secrets.Count && lines.Count < MonthlySkillLimit; i++)
            {
                SecretRef secret = secrets[i];
                if (secret == null || string.IsNullOrWhiteSpace(secret.Text)) continue;
                lines.Add((i + 1) + ". " + Trim(secret.Text, 100));
            }
            return lines.Count == 0 ? "暂无可吐露秘闻" : string.Join("\n", lines.ToArray());
        }

        private static string Trim(string s, int max)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.Replace("\r", "").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
