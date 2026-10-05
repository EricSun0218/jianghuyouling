using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using GameData.Common;
using GameData.Domains.Mod;
using GameData.Serializer;
using GameData.Utilities;
using JianghuYouling.Core.Tools;
using JianghuYouling.Shared;
using UnityEngine;

namespace JianghuYouling.Rpc
{
    /// <summary>
    /// 副作用 RPC 通道：每次请求携带稳定 operation_id，回包丢失后有界查询后端存档回执。
    /// 调用方回调严格 exactly-once；跨存档 generation 的迟到回包绝不会落到新世界。
    /// </summary>
    internal static class OperationRpcClient
    {
        private const float DefaultResponseTimeoutSeconds = 4f;
        private const float QueryTimeoutSeconds = 1.75f;
        private const float ReadOnlyReplayDelaySeconds = 0.35f;
        private const int MaxQueryAttempts = 3;
        private const int MaxReadOnlyAttempts = 2;
        // The outer coroutine needs a little scheduling headroom beyond the exact
        // 4.0 + 0.35 + 1.75 second replay path. Keep the published caller budget
        // beside the transport constants so the two cannot silently drift apart.
        internal const float ReadOnlyCallerWaitSeconds =
            DefaultResponseTimeoutSeconds + ReadOnlyReplayDelaySeconds + QueryTimeoutSeconds + 1f;
        // JHYL_ACK_REPLAY_MAX_CONCURRENCY: startup replay is ACK-only and never fans out without a bound.
        private const int AcknowledgementReplayMaxConcurrency = 1;
        private static readonly object AcknowledgementGate = new object();
        private static readonly Queue<AcknowledgementWork> AcknowledgementQueue = new Queue<AcknowledgementWork>();
        private static readonly Dictionary<string, AcknowledgementWork> PendingAcknowledgements =
            new Dictionary<string, AcknowledgementWork>(StringComparer.Ordinal);
        private static int _acknowledgementEpoch;
        private static int _activeAcknowledgementWorkers;
        private static bool _acknowledgementPumpRunning;
        private static bool _acknowledgementPumpRequested;
        private static readonly object GroupEnvelopeGate = new object();
        private static readonly Dictionary<string, GroupPhysicalEnvelope> GroupPhysicalEnvelopes =
            new Dictionary<string, GroupPhysicalEnvelope>(StringComparer.Ordinal);
        private static readonly Dictionary<string, OperationIdentityExpectation> OperationIdentityExpectations =
            new Dictionary<string, OperationIdentityExpectation>(StringComparer.Ordinal);

        private sealed class GroupPhysicalEnvelope
        {
            public int ActorId;
            public int TaiwuId;
            public string EndpointsCsv;
            public bool ActorScene;
        }

        private sealed class OperationIdentityExpectation
        {
            public uint WorldId;
            public int TaiwuId;
        }

        private sealed class AcknowledgementWork
        {
            public string Key;
            public string OperationId;
            public string OutboxPath;
            public uint WorldId;
            public int TaiwuId;
            public int WorldGeneration;
            public int Epoch;
            public readonly List<Action<bool>> Callbacks = new List<Action<bool>>();
        }

        private sealed class CallState
        {
            public readonly string Method;
            public readonly string OperationId;
            public readonly SerializableModData Parameter;
            public readonly int WorldGeneration;
            public readonly uint WorldId;
            public readonly int TaiwuId;
            public readonly Action<SerializableModData> Callback;
            public readonly bool AutoAcknowledge;
            public readonly bool RequireStructuredMutationReceipt;
            public readonly bool AllowEarlierTaiwuInSameWorld;
            public readonly string ExpectedOperationKind;
            public readonly bool AllowUnpersistedStructuredResponse;
            public int Completed;
            public SerializableModData LastObserved;

            public CallState(string method, string operationId, SerializableModData parameter, int generation,
                uint worldId, int taiwuId,
                Action<SerializableModData> callback, bool autoAcknowledge = false,
                bool requireStructuredMutationReceipt = false,
                bool allowEarlierTaiwuInSameWorld = false,
                string expectedOperationKind = null,
                bool allowUnpersistedStructuredResponse = false)
            {
                Method = method;
                OperationId = operationId;
                Parameter = parameter;
                WorldGeneration = generation;
                WorldId = worldId;
                TaiwuId = taiwuId;
                Callback = callback;
                AutoAcknowledge = autoAcknowledge;
                RequireStructuredMutationReceipt = requireStructuredMutationReceipt;
                AllowEarlierTaiwuInSameWorld = allowEarlierTaiwuInSameWorld;
                ExpectedOperationKind = expectedOperationKind;
                AllowUnpersistedStructuredResponse = allowUnpersistedStructuredResponse;
            }

            public bool IsCompleted => Volatile.Read(ref Completed) != 0;

            public void Observe(SerializableModData response)
            {
                if (response == null || IsCompleted) return;
                if (RequireStructuredMutationReceipt
                    && !IsCompleteStructuredOperationResponse(response, OperationId, WorldId, TaiwuId,
                        ExpectedOperationKind, AllowUnpersistedStructuredResponse))
                {
                    LastObserved = Outcome(false, "unknown", "incomplete_structured_operation_response", true,
                        OperationId, "副作用 RPC 回包缺少完整 v2 回执字段，已拒绝按成功/失败解释并继续查询");
                    return;
                }
                if (!ResponseMatchesOperationIdentity(response, OperationId, WorldId, TaiwuId))
                {
                    Debug.LogWarning("[江湖有灵] operation 回包 operation_id 不匹配，已忽略 expected="
                        + OperationId + ", method=" + Method);
                    LastObserved = Outcome(false, "unknown", "response_operation_id_mismatch", true, OperationId,
                        "RPC 回包属于另一操作，已拒绝采信并继续按原 operation_id 对账");
                    return;
                }
                LastObserved = response;
                if (IsTerminal(response)) Complete(response);
            }

            public void Complete(SerializableModData response)
            {
                if (Interlocked.CompareExchange(ref Completed, 1, 0) != 0) return;
                if (!CallStateWorldStillSame(this))
                    response = Outcome(false, "unknown", "world_changed", true, OperationId,
                        "已切换或离开存档，副作用结果未知；回到原存档后仅查询回执");
                try
                {
                    bool succeeded = false;
                    response?.Get("success", out succeeded);
                    if (!succeeded)
                    {
                        ReadOutcome(response, out string status, out string code, out bool retryable);
                        string characterState = null;
                        response?.Get("character_state", out characterState);
                        // Deliberately omit arguments, dialogue and receipt bodies. The code and
                        // operation id are enough to correlate a refusal across all RPC methods.
                        Debug.Log("[JHYL_RPC_OUTCOME] method=" + Method + " op=" + OperationId
                            + " status=" + (status ?? "unknown") + " code=" + (code ?? "missing_code")
                            + " retryable=" + retryable
                            + (string.IsNullOrEmpty(characterState) ? "" : " " + characterState));
                    }
                }
                catch { } // A diagnostic failure must never suppress the exactly-once callback.
                try { Callback?.Invoke(response); }
                catch (Exception e) { Debug.LogWarning("[江湖有灵] operation callback 异常:" + e.GetType().Name); }
                // 由本客户端临时生成 id 的调用没有 durable 恢复者；回调已同步消费终态后即可 ACK，
                // 让后端在容量需要时把完整回执压缩为防重墓碑。显式 stable id 必须由调用方
                // 在自己的 journal 可靠落盘后再 ACK，绝不能在这里抢先确认。
                if (AutoAcknowledge && IsTerminal(response) && IsJournaledMutationReceipt(response))
                    AcknowledgeExistingReceipt(WorldId, TaiwuId, OperationId, null);
            }
        }

        public static string NewOperationId() => Guid.NewGuid().ToString("N");

        public static bool RegisterGroupPhysicalEnvelope(string operationId, int actorId, int taiwuId,
            IEnumerable<int> endpoints, bool actorScene = false)
        {
            if (!IsValidOperationId(operationId) || actorId <= 0 || taiwuId <= 0 || endpoints == null)
            {
                DiscardPreparedOperation(operationId);
                return false;
            }
            var ids = new SortedSet<int>();
            foreach (int id in endpoints) if (id > 0) ids.Add(id);
            lock (GroupEnvelopeGate)
            {
                // Re-preparing the same durable id must never inherit an envelope from an
                // earlier aborted attempt. An empty footprint explicitly means no envelope.
                GroupPhysicalEnvelopes.Remove(operationId);
                if (ids.Count == 0) return true;
                GroupPhysicalEnvelopes[operationId] = new GroupPhysicalEnvelope
                {
                    ActorId = actorId,
                    TaiwuId = taiwuId,
                    EndpointsCsv = string.Join(",", ids),
                    ActorScene = actorScene,
                };
            }
            return true;
        }

        public static bool RegisterOperationIdentityExpectation(string operationId, uint worldId, int taiwuId)
        {
            uint currentWorldId;
            int currentTaiwuId;
            if (!IsValidOperationId(operationId) || worldId == 0 || taiwuId <= 0
                || !TryGetCurrentOperationIdentity(out currentWorldId, out currentTaiwuId)
                || currentWorldId != worldId || currentTaiwuId != taiwuId)
            {
                DiscardPreparedOperation(operationId);
                return false;
            }
            lock (GroupEnvelopeGate)
            {
                // Identity preparation begins a fresh dispatch attempt for this id.
                GroupPhysicalEnvelopes.Remove(operationId);
                OperationIdentityExpectations[operationId] = new OperationIdentityExpectation
                { WorldId = worldId, TaiwuId = taiwuId };
            }
            return true;
        }

        public static void DiscardPreparedOperation(string operationId)
        {
            if (!IsValidOperationId(operationId)) return;
            lock (GroupEnvelopeGate)
            {
                OperationIdentityExpectations.Remove(operationId);
                GroupPhysicalEnvelopes.Remove(operationId);
            }
        }

        public static bool IsValidOperationId(string operationId)
        {
            if (string.IsNullOrEmpty(operationId) || operationId.Length != 32) return false;
            for (int i = 0; i < operationId.Length; i++)
            {
                char c = operationId[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        public static bool Call(string method, SerializableModData parameter, Action<SerializableModData> completed,
            string stableOperationId = null, float responseTimeoutSeconds = DefaultResponseTimeoutSeconds,
            bool requireStructuredMutationReceipt = false, bool readOnlyRequest = false)
        {
            bool autoAcknowledge = string.IsNullOrWhiteSpace(stableOperationId);
            string operationId = autoAcknowledge ? NewOperationId() : stableOperationId.Trim();
            if (string.IsNullOrWhiteSpace(method) || !IsValidOperationId(operationId))
            {
                DiscardPreparedOperation(operationId);
                completed?.Invoke(Outcome(false, "failed", "invalid_operation_request", false, operationId,
                    "副作用 RPC 方法或 operation_id 无效"));
                return false;
            }
            if (!readOnlyRequest && MonthlySettlement.IsAdvancingMonthForMonthlyWork())
            {
                DiscardPreparedOperation(operationId);
                completed?.Invoke(Outcome(false, "rejected", "native_month_advancing", false,
                    operationId, "游戏本体正在过月结算，动作未派发；待结算完成后可重新尝试"));
                Debug.LogWarning("[JHYL_MUTATION_BLOCKED_DURING_NATIVE_MONTH] method="
                    + method + " op=" + operationId);
                return false;
            }
            if (!WorldLifecycle.HasWorldIdentity)
            {
                DiscardPreparedOperation(operationId);
                completed?.Invoke(Outcome(false, "unknown", "world_identity_pending", true, operationId,
                    "权威 WorldId 尚未就绪，未派发副作用"));
                return false;
            }

            uint worldId;
            int taiwuId;
            if (!TryGetCurrentOperationIdentity(out worldId, out taiwuId))
            {
                DiscardPreparedOperation(operationId);
                completed?.Invoke(Outcome(false, "unknown", "operation_identity_pending", true, operationId,
                    "WorldId/TaiwuId 尚未同时就绪，未派发副作用"));
                return false;
            }

            OperationIdentityExpectation expectedIdentity = null;
            lock (GroupEnvelopeGate)
            {
                if (OperationIdentityExpectations.TryGetValue(operationId, out expectedIdentity))
                    OperationIdentityExpectations.Remove(operationId);
            }
            if (expectedIdentity != null
                && (expectedIdentity.WorldId != worldId || expectedIdentity.TaiwuId != taiwuId))
            {
                DiscardPreparedOperation(operationId);
                completed?.Invoke(Outcome(false, "rejected", "operation_identity_changed_before_dispatch", false,
                    operationId, "动作准备后 WorldId/TaiwuId 已变化，动作没有派发"));
                return false;
            }

            parameter = parameter ?? new SerializableModData();
            parameter.Set(RpcConst.OperationIdField, operationId);
            parameter.Set("protocol_version", RpcConst.OperationProtocolVersion);
            BindOperationIdentity(parameter, worldId, taiwuId);
            if (!ApplyGroupPhysicalEnvelope(parameter, operationId, taiwuId))
            {
                DiscardPreparedOperation(operationId);
                completed?.Invoke(Outcome(false, "rejected", "group_physical_envelope_identity_changed", false,
                    operationId, "群聊动作准备后太吾身份已变化，动作没有派发"));
                return false;
            }
            int generation = WorldLifecycle.Generation;
            var state = new CallState(method, operationId, parameter, generation, worldId, taiwuId,
                completed, autoAcknowledge, requireStructuredMutationReceipt, false,
                ExpectedMutationOperationKind(method, parameter), false);
            MonoBehaviour host = TalkEntryHost.Instance;
            if (host == null) host = ConfigHost.Instance;
            if (host == null)
            {
                state.Complete(Outcome(false, "unknown", "coroutine_host_missing", true, operationId,
                    "副作用 RPC 宿主未就绪，请稍后再试"));
                return false;
            }
            float timeout = responseTimeoutSeconds <= 0 ? DefaultResponseTimeoutSeconds : responseTimeoutSeconds;
            host.StartCoroutine(readOnlyRequest ? RunReadOnly(state, timeout) : Run(state, timeout));
            return true;
        }

        /// <summary>
        /// 调用方的 durable journal 已可靠写入终态后确认回执。ACK 丢失只会延迟后端压缩，
        /// 不会重放原 mutation；因此这里有界重试 ACK 本身，且绝不走 Call/原方法补发分支。
        /// </summary>
        public static bool AcknowledgeExistingReceipt(string operationId, Action<bool> completed)
        {
            operationId = (operationId ?? "").Trim();
            uint worldId;
            int taiwuId;
            if (!IsValidOperationId(operationId) || !TryGetCurrentOperationIdentity(out worldId, out taiwuId))
            {
                completed?.Invoke(false);
                return false;
            }
            return AcknowledgeExistingReceipt(worldId, taiwuId, operationId, completed);
        }

        /// <summary>
        /// Cleanup-only ACK for a durable journal that preserved the mutation's original
        /// identity. It may acknowledge an earlier Taiwu after succession, but never a
        /// different WorldId; backend receipt/tombstone binding still verifies the exact triple.
        /// </summary>
        public static bool AcknowledgeExistingReceipt(uint worldId, int taiwuId,
            string operationId, Action<bool> completed)
        {
            operationId = (operationId ?? "").Trim();
            uint currentWorldId;
            int currentTaiwuId;
            if (!IsValidOperationId(operationId) || worldId == 0 || taiwuId <= 0
                || !TryGetCurrentOperationIdentity(out currentWorldId, out currentTaiwuId)
                || currentWorldId != worldId || currentTaiwuId <= 0)
            {
                completed?.Invoke(false);
                return false;
            }

            int generation = WorldLifecycle.Generation;
            string outboxPath = JianghuYoulingPaths.OperationAckOutboxFor(worldId);
            if (!AcknowledgementWorldStillSame(generation, worldId, taiwuId))
            {
                completed?.Invoke(false);
                return false;
            }

            // JHYL_ACK_OUTBOX_PERSIST_BEFORE_NETWORK: every public ACK request is durable before any RPC can start.
            bool added;
            if (!OperationAckOutboxStore.TryEnqueue(outboxPath, worldId, taiwuId, operationId, out added))
            {
                Debug.LogWarning("[江湖有灵] ACK outbox 写入失败，拒绝发送 ACK op=" + operationId);
                completed?.Invoke(false);
                return false;
            }

            if (!QueuePersistedAcknowledgement(outboxPath, worldId, taiwuId, generation, operationId, completed))
            {
                // The durable entry intentionally remains for the next activation of this world.
                completed?.Invoke(false);
                return true;
            }
            PumpAcknowledgements();
            return true;
        }

        /// <summary>WorldId 就绪后只重放 outbox 中的 ACK，不查询或重发原 mutation。</summary>
        public static void ReplayAcknowledgementOutbox()
        {
            uint worldId;
            int taiwuId;
            if (!TryGetCurrentOperationIdentity(out worldId, out taiwuId)) return;
            int generation = WorldLifecycle.Generation;
            string outboxPath = JianghuYoulingPaths.OperationAckOutboxFor(worldId);
            if (!AcknowledgementWorldStillSame(generation, worldId, taiwuId)) return;

            OperationAckOutboxStore.Snapshot snapshot = OperationAckOutboxStore.Load(outboxPath, worldId);
            if (!snapshot.Reliable)
            {
                Debug.LogWarning("[江湖有灵] ACK outbox 全部候选不可用，已封闭失败且不会覆盖: " + (snapshot.Error ?? "unknown"));
                return;
            }

            int queued = 0;
            foreach (OperationAckOutboxStore.SnapshotEntry entry in snapshot.Entries)
            {
                // JHYL_ACK_REPLAY_ACK_ONLY: this path reaches only RunAcknowledgeRpc/SendDirect(AckOperationMethod).
                if (entry != null && entry.WorldId == worldId && entry.TaiwuId > 0
                    && QueuePersistedAcknowledgement(outboxPath, worldId, entry.TaiwuId,
                        generation, entry.OperationId, null)) queued++;
            }
            if (queued > 0)
            {
                Debug.Log("[江湖有灵] ACK outbox 开始有界重放 worldId=" + worldId + ", count=" + queued);
                PumpAcknowledgements();
            }
        }

        /// <summary>Invalidate in-memory work; durable entries remain isolated under their original WorldId.</summary>
        public static void ResetAcknowledgementPumpForWorldChange()
        {
            List<Action<bool>> callbacks = new List<Action<bool>>();
            lock (AcknowledgementGate)
            {
                _acknowledgementEpoch++;
                foreach (AcknowledgementWork work in PendingAcknowledgements.Values)
                {
                    callbacks.AddRange(work.Callbacks);
                    work.Callbacks.Clear();
                }
                PendingAcknowledgements.Clear();
                AcknowledgementQueue.Clear();
                _activeAcknowledgementWorkers = 0;
            }
            InvokeAcknowledgementCallbacks(callbacks, false);
            lock (GroupEnvelopeGate)
            {
                GroupPhysicalEnvelopes.Clear();
                OperationIdentityExpectations.Clear();
            }
        }

        private static bool QueuePersistedAcknowledgement(string outboxPath, uint worldId, int taiwuId, int generation,
            string operationId, Action<bool> completed)
        {
            if (!IsValidOperationId(operationId) || string.IsNullOrWhiteSpace(outboxPath)) return false;
            string key = worldId + ":" + taiwuId + ":" + operationId;
            lock (AcknowledgementGate)
            {
                if (!AcknowledgementWorldStillSame(generation, worldId, taiwuId)) return false;
                AcknowledgementWork existing;
                if (PendingAcknowledgements.TryGetValue(key, out existing))
                {
                    if (completed != null) existing.Callbacks.Add(completed);
                    return true;
                }
                var work = new AcknowledgementWork
                {
                    Key = key,
                    OperationId = operationId,
                    OutboxPath = outboxPath,
                    WorldId = worldId,
                    TaiwuId = taiwuId,
                    WorldGeneration = generation,
                    Epoch = _acknowledgementEpoch,
                };
                if (completed != null) work.Callbacks.Add(completed);
                PendingAcknowledgements.Add(key, work);
                AcknowledgementQueue.Enqueue(work);
                return true;
            }
        }

        private static void PumpAcknowledgements()
        {
            while (true)
            {
                lock (AcknowledgementGate)
                {
                    if (_acknowledgementPumpRunning)
                    {
                        _acknowledgementPumpRequested = true;
                        return;
                    }
                    _acknowledgementPumpRunning = true;
                    _acknowledgementPumpRequested = false;
                }

                bool rerun = false;
                try
                {
                    while (true)
                    {
                        AcknowledgementWork work = null;
                        MonoBehaviour host = null;
                        List<Action<bool>> hostFailureCallbacks = null;
                        lock (AcknowledgementGate)
                        {
                            if (_activeAcknowledgementWorkers >= AcknowledgementReplayMaxConcurrency
                                || AcknowledgementQueue.Count == 0) break;
                            host = TalkEntryHost.Instance;
                            if (host == null) host = ConfigHost.Instance;
                            if (host == null)
                            {
                                hostFailureCallbacks = new List<Action<bool>>();
                                while (AcknowledgementQueue.Count > 0)
                                {
                                    AcknowledgementWork stranded = AcknowledgementQueue.Dequeue();
                                    PendingAcknowledgements.Remove(stranded.Key);
                                    hostFailureCallbacks.AddRange(stranded.Callbacks);
                                    stranded.Callbacks.Clear();
                                }
                            }
                            else
                            {
                                work = AcknowledgementQueue.Dequeue();
                                _activeAcknowledgementWorkers++;
                            }
                        }

                        if (hostFailureCallbacks != null)
                        {
                            InvokeAcknowledgementCallbacks(hostFailureCallbacks, false);
                            break;
                        }
                        try
                        {
                            host.StartCoroutine(RunAcknowledgeRpc(work.OperationId, work.WorldGeneration,
                                work.WorldId, work.TaiwuId,
                                succeeded => CompleteAcknowledgement(work, succeeded)));
                        }
                        catch
                        {
                            CompleteAcknowledgement(work, false);
                        }
                    }
                }
                finally
                {
                    lock (AcknowledgementGate)
                    {
                        _acknowledgementPumpRunning = false;
                        rerun = _acknowledgementPumpRequested
                            && _activeAcknowledgementWorkers < AcknowledgementReplayMaxConcurrency
                            && AcknowledgementQueue.Count > 0;
                        _acknowledgementPumpRequested = false;
                    }
                }
                if (!rerun) return;
            }
        }

        private static void CompleteAcknowledgement(AcknowledgementWork work, bool backendAcknowledged)
        {
            bool current;
            lock (AcknowledgementGate)
            {
                AcknowledgementWork registered;
                current = work != null && work.Epoch == _acknowledgementEpoch
                    && PendingAcknowledgements.TryGetValue(work.Key, out registered)
                    && ReferenceEquals(registered, work);
            }
            if (!current) return;

            bool reliablyRemoved = false;
            if (backendAcknowledged && AcknowledgementWorldStillSame(work.WorldGeneration, work.WorldId, work.TaiwuId))
            {
                // JHYL_ACK_OUTBOX_REMOVE_AFTER_ACK: callback true requires this durable removal commit too.
                bool removed;
                reliablyRemoved = OperationAckOutboxStore.TryRemoveAcknowledged(
                    work.OutboxPath, work.WorldId, work.TaiwuId, work.OperationId, out removed);
            }

            List<Action<bool>> callbacks;
            lock (AcknowledgementGate)
            {
                AcknowledgementWork registered;
                if (work.Epoch != _acknowledgementEpoch
                    || !PendingAcknowledgements.TryGetValue(work.Key, out registered)
                    || !ReferenceEquals(registered, work)) return;
                PendingAcknowledgements.Remove(work.Key);
                if (_activeAcknowledgementWorkers > 0) _activeAcknowledgementWorkers--;
                callbacks = new List<Action<bool>>(work.Callbacks);
                work.Callbacks.Clear();
            }
            InvokeAcknowledgementCallbacks(callbacks, backendAcknowledged && reliablyRemoved);
            PumpAcknowledgements();
        }

        private static void InvokeAcknowledgementCallbacks(List<Action<bool>> callbacks, bool succeeded)
        {
            if (callbacks == null) return;
            foreach (Action<bool> callback in callbacks)
            {
                try { callback?.Invoke(succeeded); } catch { }
            }
        }

        /// <summary>
        /// 只读对账已有 operationId：不执行原 mutation，也不走 Run 中的 not_found 同-id补发分支。
        /// 仅有界重查回执，用于下月/重启后 reconcile durable pending journal。
        /// </summary>
        public static void QueryExistingReceipt(string operationId, Action<SerializableModData> completed)
        {
            operationId = (operationId ?? "").Trim();
            if (!IsValidOperationId(operationId))
            {
                completed?.Invoke(Outcome(false, "failed", "invalid_operation_id", false, operationId,
                    "查询缺少 32 位 operation_id"));
                return;
            }
            uint worldId;
            int taiwuId;
            if (!TryGetCurrentOperationIdentity(out worldId, out taiwuId))
            {
                completed?.Invoke(Outcome(false, "unknown", "operation_identity_pending", true, operationId,
                    "权威 WorldId/TaiwuId 尚未同时就绪，未查询回执"));
                return;
            }
            QueryExistingReceipt(worldId, taiwuId, operationId, completed);
        }

        /// <summary>
        /// Read-only reconciliation using the durable mutation identity. Unlike a new
        /// mutation this may query an earlier Taiwu after succession, but only while the
        /// same authoritative WorldId remains active.
        /// </summary>
        public static void QueryExistingReceipt(uint worldId, int taiwuId, string operationId,
            Action<SerializableModData> completed)
        {
            operationId = (operationId ?? "").Trim();
            uint currentWorldId;
            int currentTaiwuId;
            if (!IsValidOperationId(operationId) || worldId == 0 || taiwuId <= 0
                || !TryGetCurrentOperationIdentity(out currentWorldId, out currentTaiwuId)
                || currentWorldId != worldId)
            {
                completed?.Invoke(Outcome(false, "unknown", "operation_identity_pending", true, operationId,
                    "原始 WorldId/TaiwuId 暂不可用于只读回执查询"));
                return;
            }
            var query = new SerializableModData();
            query.Set(RpcConst.OperationIdField, operationId);
            query.Set("protocol_version", RpcConst.OperationProtocolVersion);
            BindOperationIdentity(query, worldId, taiwuId);
            var state = new CallState(RpcConst.QueryOperationMethod, operationId, query, WorldLifecycle.Generation,
                worldId, taiwuId, completed, false, true, true, null, true);
            MonoBehaviour host = TalkEntryHost.Instance;
            if (host == null) host = ConfigHost.Instance;
            if (host == null)
            {
                state.Complete(Outcome(false, "unknown", "coroutine_host_missing", true, operationId,
                    "副作用 RPC 宿主未就绪，请稍后再查"));
                return;
            }
            host.StartCoroutine(RunQueryOnly(state));
        }

        private static IEnumerator Run(CallState state, float responseTimeoutSeconds)
        {
            if (MonthlySettlement.IsAdvancingMonthForMonthlyWork())
            {
                state.Complete(Outcome(false, "rejected", "native_month_advancing", false,
                    state.OperationId, "游戏本体正在过月结算，动作未派发；待结算完成后可重新尝试"));
                yield break;
            }
            Debug.Log("[江湖有灵] operation dispatch method=" + state.Method + " op=" + state.OperationId);
            Send(state.Method, state.Parameter, state, state.Observe);

            float deadline = Time.unscaledTime + responseTimeoutSeconds;
            while (!state.IsCompleted && WorldStillSame(state.WorldGeneration, state.WorldId, state.TaiwuId)
                && Time.unscaledTime < deadline)
                yield return null;
            if (state.IsCompleted) yield break;
            if (!WorldStillSame(state.WorldGeneration, state.WorldId, state.TaiwuId))
            {
                state.Complete(Outcome(false, "canceled", "world_changed", false, state.OperationId, "已切换或离开存档"));
                yield break;
            }

            Debug.LogWarning("[江湖有灵] operation 原回包超时，查询后端回执 method=" + state.Method + " op=" + state.OperationId);
            bool resent = false;
            for (int attempt = 0; attempt < MaxQueryAttempts && !state.IsCompleted; attempt++)
            {
                if (!CallStateWorldStillSame(state)) break;
                bool queryReturned = false;
                SerializableModData queryResponse = null;
                var query = new SerializableModData();
                query.Set(RpcConst.OperationIdField, state.OperationId);
                query.Set("protocol_version", RpcConst.OperationProtocolVersion);
                BindOperationIdentity(query, state.WorldId, state.TaiwuId);
                Send(RpcConst.QueryOperationMethod, query, state, response =>
                {
                    queryReturned = true;
                    string rawStatus, rawCode;
                    bool rawRetryable;
                    ReadOutcome(response, out rawStatus, out rawCode, out rawRetryable);
                    if (rawStatus == "unknown" && rawCode == "operation_not_found")
                        queryResponse = response;
                    else
                    {
                        state.Observe(response);
                        queryResponse = state.LastObserved;
                    }
                });

                float queryDeadline = Time.unscaledTime + QueryTimeoutSeconds;
                while (!state.IsCompleted && CallStateWorldStillSame(state)
                    && !queryReturned && Time.unscaledTime < queryDeadline)
                    yield return null;
                if (state.IsCompleted) yield break;

                string status, code; bool retryable;
                ReadOutcome(queryResponse, out status, out code, out retryable);
                // 未找到说明原请求可能未抵达后端；用同一 opId 最多补发一次，后端 ledger 保证不重复落地。
                if (!resent && queryReturned && status == "unknown" && code == "operation_not_found")
                {
                    if (MonthlySettlement.IsAdvancingMonthForMonthlyWork())
                    {
                        state.Complete(Outcome(false, "unknown", "native_month_advance_replay_suppressed",
                            true, state.OperationId,
                            "原动作回执尚未找到，但游戏本体已经开始过月；已禁止补发，稍后只核对原回执"));
                        yield break;
                    }
                    resent = true;
                    Debug.LogWarning("[江湖有灵] operation 回执未找到，同 opId 有界补发 method=" + state.Method + " op=" + state.OperationId);
                    Send(state.Method, state.Parameter, state, state.Observe);
                    float resendDeadline = Time.unscaledTime + QueryTimeoutSeconds;
                    while (!state.IsCompleted && WorldStillSame(state.WorldGeneration, state.WorldId, state.TaiwuId)
                        && Time.unscaledTime < resendDeadline)
                        yield return null;
                    if (state.IsCompleted) yield break;
                }

                if (attempt + 1 < MaxQueryAttempts)
                    yield return new WaitForSecondsRealtime(0.35f * (attempt + 1));
            }

            if (!CallStateWorldStillSame(state))
                state.Complete(Outcome(false, "canceled", "world_changed", false, state.OperationId, "已切换或离开存档"));
            else
                state.Complete(state.LastObserved ?? Outcome(false, "unknown", "receipt_unavailable", true, state.OperationId,
                    "副作用回执暂未可得；请以游戏真实状态为准"));
        }

        /// <summary>
        /// Non-journaled calls have no durable mutation receipt to query. A lost response can be
        /// recovered only by replaying the same side-effect-free request with a strict bound.
        /// Keeping this path separate also prevents a backend outage from being misreported as a
        /// missing side-effect receipt.
        /// </summary>
        private static IEnumerator RunReadOnly(CallState state, float responseTimeoutSeconds)
        {
            for (int attempt = 0; attempt < MaxReadOnlyAttempts && !state.IsCompleted; attempt++)
            {
                if (!CallStateWorldStillSame(state)) break;
                if (MonthlySettlement.IsAdvancingMonthForMonthlyWork())
                {
                    state.Complete(Outcome(false, "canceled",
                        "native_month_advance_read_suppressed", true, state.OperationId,
                        "游戏本体正在保存并结算月份；本次只读查询已取消，未产生副作用"));
                    yield break;
                }
                bool returned = false;
                Debug.Log((attempt == 0
                    ? "[江湖有灵] readonly dispatch method="
                    : "[江湖有灵] readonly response timeout, bounded replay method=")
                    + state.Method + " op=" + state.OperationId + " attempt=" + (attempt + 1));
                Send(state.Method, state.Parameter, state, response =>
                {
                    returned = true;
                    state.Observe(response);
                });

                float waitSeconds = attempt == 0 ? responseTimeoutSeconds : QueryTimeoutSeconds;
                float deadline = Time.unscaledTime + waitSeconds;
                while (!state.IsCompleted && CallStateWorldStillSame(state)
                    && !returned && Time.unscaledTime < deadline
                    && !MonthlySettlement.IsAdvancingMonthForMonthlyWork())
                    yield return null;
                if (state.IsCompleted) yield break;
                if (MonthlySettlement.IsAdvancingMonthForMonthlyWork())
                {
                    state.Complete(Outcome(false, "canceled",
                        "native_month_advance_read_suppressed", true, state.OperationId,
                        "游戏本体正在保存并结算月份；本次只读查询已取消，未产生副作用"));
                    yield break;
                }
                if (attempt + 1 < MaxReadOnlyAttempts)
                    yield return new WaitForSecondsRealtime(ReadOnlyReplayDelaySeconds * (attempt + 1));
            }

            if (!CallStateWorldStillSame(state))
                state.Complete(Outcome(false, "canceled", "world_changed", false, state.OperationId, "已切换或离开存档"));
            else
                state.Complete(state.LastObserved ?? Outcome(false, "unknown", "read_only_response_unavailable",
                    true, state.OperationId, "只读后端查询暂无回包；未产生需要核对的副作用"));
        }

        private static IEnumerator RunQueryOnly(CallState state)
        {
            for (int attempt = 0; attempt < MaxQueryAttempts && !state.IsCompleted; attempt++)
            {
                if (!CallStateWorldStillSame(state)) break;
                bool returned = false;
                Send(RpcConst.QueryOperationMethod, state.Parameter, state, response =>
                {
                    returned = true;
                    state.Observe(response);
                });
                float deadline = Time.unscaledTime + QueryTimeoutSeconds;
                while (!state.IsCompleted && CallStateWorldStillSame(state)
                    && !returned && Time.unscaledTime < deadline)
                    yield return null;
                if (state.IsCompleted) yield break;
                if (attempt + 1 < MaxQueryAttempts)
                    yield return new WaitForSecondsRealtime(0.35f * (attempt + 1));
            }
            if (!CallStateWorldStillSame(state))
                state.Complete(Outcome(false, "canceled", "world_changed", false, state.OperationId, "已切换或离开存档"));
            else
                state.Complete(state.LastObserved ?? Outcome(false, "unknown", "receipt_unavailable", true, state.OperationId,
                    "后端回执暂未可得，继续保持隔离"));
        }

        private static IEnumerator RunAcknowledgeRpc(string operationId, int generation, uint worldId, int taiwuId,
            Action<bool> completed)
        {
            bool succeeded = false;
            for (int attempt = 0; attempt < MaxQueryAttempts && !succeeded; attempt++)
            {
                if (!AcknowledgementWorldStillSame(generation, worldId, taiwuId)) break;
                bool returned = false;
                var p = new SerializableModData();
                p.Set(RpcConst.OperationIdField, operationId);
                p.Set("protocol_version", RpcConst.OperationProtocolVersion);
                BindOperationIdentity(p, worldId, taiwuId);
                SendDirect(RpcConst.AckOperationMethod, p, generation, worldId, taiwuId, response =>
                {
                    returned = true;
                    bool ok = false; string status = null, code = null, responseOperationId = null;
                    response?.Get("success", out ok);
                    response?.Get(RpcConst.OperationStatusField, out status);
                    response?.Get(RpcConst.OperationCodeField, out code);
                    response?.Get(RpcConst.OperationIdField, out responseOperationId);
                    succeeded = IsCompleteStructuredOperationResponse(response, operationId, worldId, taiwuId,
                            "ack", true)
                        && string.Equals(responseOperationId, operationId, StringComparison.Ordinal)
                        && ok && status == "succeeded"
                        && (code == "acknowledged" || code == "already_acknowledged");
                });
                float deadline = Time.unscaledTime + QueryTimeoutSeconds;
                while (!returned && AcknowledgementWorldStillSame(generation, worldId, taiwuId) && Time.unscaledTime < deadline)
                    yield return null;
                if (!succeeded && attempt + 1 < MaxQueryAttempts)
                    yield return new WaitForSecondsRealtime(0.2f * (attempt + 1));
            }
            try { completed?.Invoke(succeeded); } catch { }
        }

        private static void SendDirect(string method, SerializableModData parameter, int generation, uint worldId, int taiwuId,
            Action<SerializableModData> decoded)
        {
            if (!AcknowledgementWorldStillSame(generation, worldId, taiwuId)) { decoded?.Invoke(null); return; }
            string modId = Plugin.Instance?.ModIdStr;
            if (string.IsNullOrWhiteSpace(modId)) modId = RpcConst.FallbackModId;
            try
            {
                ModDomainMethod.AsyncCall.CallModMethodWithParamAndRet(
                    null, modId, method, parameter,
                    delegate (int offset, RawDataPool pool)
                    {
                        if (!AcknowledgementWorldStillSame(generation, worldId, taiwuId)) { decoded?.Invoke(null); return; }
                        SerializableModData response = null;
                        try { Serializer.Deserialize(pool, offset, ref response); } catch { }
                        try { decoded?.Invoke(response); } catch { }
                    });
            }
            catch { try { decoded?.Invoke(null); } catch { } }
        }

        private static void Send(string method, SerializableModData parameter, CallState state, Action<SerializableModData> decoded)
        {
            if (state == null || state.IsCompleted
                || !CallStateWorldStillSame(state)) return;
            string modId = Plugin.Instance?.ModIdStr;
            if (string.IsNullOrWhiteSpace(modId)) modId = RpcConst.FallbackModId;
            try
            {
                ModDomainMethod.AsyncCall.CallModMethodWithParamAndRet(
                    null, modId, method, parameter,
                    delegate (int offset, RawDataPool pool)
                    {
                        if (state.IsCompleted) return;
                        if (!CallStateWorldStillSame(state))
                        {
                            state.Complete(Outcome(false, "canceled", "world_changed", false, state.OperationId, "已切换或离开存档"));
                            return;
                        }
                        SerializableModData response = null;
                        try { Serializer.Deserialize(pool, offset, ref response); }
                        catch (Exception e)
                        {
                            response = Outcome(false, "unknown", "response_deserialize_failed", true, state.OperationId,
                                "RPC 回包解析失败:" + e.GetType().Name);
                        }
                        if (response == null)
                            response = Outcome(false, "unknown", "empty_rpc_response", true, state.OperationId, "RPC 回包为空");
                        try { decoded?.Invoke(response); } catch { }
                    });
            }
            catch (Exception e)
            {
                try
                {
                    decoded?.Invoke(Outcome(false, "unknown", "rpc_dispatch_failed", true, state.OperationId,
                        "RPC 派发失败:" + e.GetType().Name));
                }
                catch { }
            }
        }

        private static bool IsTerminal(SerializableModData response)
        {
            string status, code; bool retryable;
            ReadOutcome(response, out status, out code, out retryable);
            if (status == "succeeded" || status == "failed" || status == "rejected" || status == "canceled") return true;
            if (status == "unknown") return !retryable;
            // Gm 中的只读 op 为兼容旧调用方仍保留 success/业务字段，不进 ledger，
            // 因此没有 v2 status 也应当作一次完整的只读回包，不去查不存在的副作用回执。
            bool legacySuccess;
            return response != null && response.Get("success", out legacySuccess);
        }

        private static bool IsJournaledMutationReceipt(SerializableModData response)
        {
            string kind = null, operationId = null;
            bool persisted = false;
            response?.Get("operation_kind", out kind);
            response?.Get(RpcConst.OperationIdField, out operationId);
            response?.Get(RpcConst.OperationReceiptPersistedField, out persisted);
            return !string.IsNullOrWhiteSpace(kind) && kind != "query" && kind != "ack"
                && IsValidOperationId(operationId) && persisted;
        }

        private static void ReadOutcome(SerializableModData response, out string status, out string code, out bool retryable)
        {
            status = null; code = null; retryable = false;
            response?.Get(RpcConst.OperationStatusField, out status);
            response?.Get(RpcConst.OperationCodeField, out code);
            response?.Get(RpcConst.OperationRetryableField, out retryable);
        }

        private static bool TryGetCurrentOperationIdentity(out uint worldId, out int taiwuId)
        {
            worldId = WorldLifecycle.WorldId;
            taiwuId = -1;
            if (!WorldLifecycle.HasWorldIdentity || worldId == 0) return false;
            try
            {
                BasicGameData data = SingletonObject.getInstance<BasicGameData>();
                taiwuId = data != null ? data.TaiwuCharId : -1;
            }
            catch { taiwuId = -1; }
            return taiwuId > 0;
        }

        private static void BindOperationIdentity(SerializableModData parameter, uint worldId, int taiwuId)
        {
            if (parameter == null) return;
            parameter.Set(RpcConst.OperationWorldIdField, worldId.ToString());
            parameter.Set(RpcConst.OperationTaiwuIdField, taiwuId);
        }

        private static bool ApplyGroupPhysicalEnvelope(SerializableModData parameter, string operationId, int taiwuId)
        {
            GroupPhysicalEnvelope envelope = null;
            lock (GroupEnvelopeGate)
            {
                if (GroupPhysicalEnvelopes.TryGetValue(operationId, out envelope))
                    GroupPhysicalEnvelopes.Remove(operationId);
            }
            if (envelope == null) return true;
            if (envelope.TaiwuId != taiwuId) return false;
            parameter.Set(RpcConst.OperationGroupPhysicalGuardField, 1);
            parameter.Set(RpcConst.OperationGroupActorIdField, envelope.ActorId);
            parameter.Set(RpcConst.OperationGroupPhysicalEndpointsField, envelope.EndpointsCsv ?? "");
            if (envelope.ActorScene)
                parameter.Set(RpcConst.OperationGroupPhysicalActorSceneField, 1);
            return true;
        }

        private static bool WorldStillSame(int generation, uint worldId, int taiwuId)
        {
            uint currentWorldId;
            int currentTaiwuId;
            return worldId > 0 && taiwuId > 0 && WorldLifecycle.IsSameWorld(generation)
                && TryGetCurrentOperationIdentity(out currentWorldId, out currentTaiwuId)
                && currentWorldId == worldId && currentTaiwuId == taiwuId;
        }

        private static bool CallStateWorldStillSame(CallState state)
            => state != null && (state.AllowEarlierTaiwuInSameWorld
                ? AcknowledgementWorldStillSame(state.WorldGeneration, state.WorldId, state.TaiwuId)
                : WorldStillSame(state.WorldGeneration, state.WorldId, state.TaiwuId));

        private static bool AcknowledgementWorldStillSame(int generation, uint worldId, int taiwuId)
        {
            uint currentWorldId;
            int currentTaiwuId;
            return worldId > 0 && taiwuId > 0 && WorldLifecycle.IsSameWorld(generation)
                && TryGetCurrentOperationIdentity(out currentWorldId, out currentTaiwuId)
                && currentWorldId == worldId;
        }

        private static bool ResponseMatchesOperationIdentity(SerializableModData response,
            string expectedOperationId, uint expectedWorldId, int expectedTaiwuId)
        {
            if (response == null) return false;
            // 账本判据以"回包带非空 operation_id"为准，而非"带 status/operation_kind"。
            // 真正的副作用账本回包一定盖了非空 operation_id：后端 ExecuteJournaled 的
            // NormalizeOperationResult / BuildOperationOutcome 与本地 Outcome() 助手都无条件
            // Set(operation_id) 且同时 StampOperationBinding(world/taiwu)，故下面仍按精确三元组
            // (operation_id,world_id,taiwu_id) 严格匹配——属于另一操作的回执 id 不同仍被拒绝、
            // 未知态仍被隔离，信任边界不削弱。只读 GM 回包(成功业务字段，以及后端 Fail() 只写
            // status/无 operation_id 的失败回包)不带 operation_id，因此走普通交付，不再被误判为
            // id 不匹配而丢弃、耗满响应窗口后误报 unknown。
            string responseOperationId = null;
            bool structured = response.Get(RpcConst.OperationIdField, out responseOperationId)
                && !string.IsNullOrEmpty(responseOperationId);
            if (!structured) return true;
            string responseWorldText = null;
            int responseTaiwuId = -1;
            uint responseWorldId;
            return response.Get(RpcConst.OperationWorldIdField, out responseWorldText)
                && response.Get(RpcConst.OperationTaiwuIdField, out responseTaiwuId)
                && uint.TryParse(responseWorldText, out responseWorldId)
                && string.Equals(responseOperationId, expectedOperationId, StringComparison.Ordinal)
                && responseWorldId == expectedWorldId && responseTaiwuId == expectedTaiwuId;
        }

        private static bool IsCompleteStructuredOperationResponse(SerializableModData response,
            string expectedOperationId, uint expectedWorldId, int expectedTaiwuId,
            string expectedOperationKind = null, bool allowUnpersisted = false)
        {
            if (!ResponseMatchesOperationIdentity(response, expectedOperationId, expectedWorldId, expectedTaiwuId))
                return false;
            int protocolVersion;
            string status = null, code = null, receipt = null, operationKind = null, integrity = null;
            bool retryable = false, success = false, persisted = false;
            bool common = response.Get("protocol_version", out protocolVersion)
                && protocolVersion == RpcConst.OperationProtocolVersion
                && response.Get("success", out success)
                && response.Get(RpcConst.OperationStatusField, out status)
                && !string.IsNullOrWhiteSpace(status)
                && response.Get(RpcConst.OperationCodeField, out code)
                && !string.IsNullOrWhiteSpace(code)
                && response.Get(RpcConst.OperationRetryableField, out retryable)
                && response.Get(RpcConst.OperationReceiptField, out receipt)
                && response.Get("operation_kind", out operationKind)
                && !string.IsNullOrWhiteSpace(operationKind)
                && (string.IsNullOrWhiteSpace(expectedOperationKind)
                    || string.Equals(operationKind, expectedOperationKind, StringComparison.Ordinal));
            if (!common) return false;
            bool hasPersisted = response.Get(RpcConst.OperationReceiptPersistedField, out persisted);
            if (hasPersisted && persisted)
                return !string.IsNullOrWhiteSpace(receipt)
                    && response.Get(RpcConst.OperationReceiptIntegrityField, out integrity)
                    && IsSha256Hex(integrity);
            if (allowUnpersisted) return true;
            return IsTerminal(response);
        }

        private static string ExpectedMutationOperationKind(string method, SerializableModData parameter)
        {
            if (string.IsNullOrWhiteSpace(method)) return null;
            if (string.Equals(method, RpcConst.GmMethod, StringComparison.Ordinal))
            {
                string op = null;
                if (parameter != null && parameter.Get("op", out op) && !string.IsNullOrWhiteSpace(op))
                    return "gm:" + op;
            }
            if (string.Equals(method, RpcConst.StartCombatMethod, StringComparison.Ordinal))
                return RpcConst.StartCombatMethod;
            return method;
        }

        private static bool IsSha256Hex(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64) return false;
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        private static SerializableModData Outcome(bool success, string status, string code, bool retryable,
            string operationId, string message)
        {
            var response = new SerializableModData();
            response.Set("success", success);
            response.Set("message", message ?? "");
            response.Set(RpcConst.OperationStatusField, status ?? (success ? "succeeded" : "unknown"));
            response.Set(RpcConst.OperationCodeField, code ?? (success ? "ok" : "unknown"));
            response.Set(RpcConst.OperationRetryableField, retryable);
            response.Set(RpcConst.OperationIdField, operationId ?? "");
            response.Set("protocol_version", RpcConst.OperationProtocolVersion);
            response.Set(RpcConst.OperationReceiptPersistedField, false);
            // Locally synthesized outcomes are not backend journal receipts.  Keeping this
            // field empty is what prevents callers from enqueueing an ACK for a mutation
            // that never reached the authoritative ledger (or whose dispatch is ambiguous).
            response.Set(RpcConst.OperationReceiptField, "");
            return response;
        }
    }
}
