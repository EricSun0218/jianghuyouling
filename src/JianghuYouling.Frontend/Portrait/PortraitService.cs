using System;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Memory;
using JianghuYouling.Core.Prompt;

namespace JianghuYouling
{
    /// <summary>
    /// 画像 = 长期记忆(仿 Claude 记忆机制)的读写服务(协程):
    ///  · EnsureSeeded:首轮立即给 existing/确定性画像，完整蒸馏在 single-flight 后台完成。
    ///  · Consolidate:一段对话后,把新沉淀的对话记忆"固化"进画像——在既有画像上融入新理解、改写更新。
    /// 带在制去重与世界/清空代次门控；LLM 不可用时后台保存确定性八节画像，Consolidate 直接跳过。
    /// </summary>
    public static class PortraitService
    {
        private sealed class GenerationLease
        {
            public string Key;
            public int Revision;
            public int Epoch;
            public int WorldGeneration;
            public long Token;
            public CancellationTokenSource Cancellation;
            public bool InteractivePriority;
        }

        private sealed class BackgroundUpgradeRequest
        {
            public string Key;
            public int Revision;
            public int WorldGeneration;
            public long Token;
        }

        private sealed class RetrySuppression
        {
            public string Fingerprint;
            public long RetryAfterUtcTicks;
        }

        private static readonly object LeaseGate = new object();
        private static readonly Dictionary<string, int> Revisions = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> Inflight = new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Dictionary<string, CancellationTokenSource> InflightCancellation =
            new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        private static readonly HashSet<string> InflightInteractivePriority =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> BackgroundScheduled = new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Dictionary<string, RetrySuppression> UpgradeRetrySuppression =
            new Dictionary<string, RetrySuppression>(StringComparer.Ordinal);
        private static int _epoch;
        private static long _nextToken;
        private static int _interactiveBurstCount;
        private static int _interactiveBurstEpoch;

        private sealed class InteractiveBurstLease : IDisposable
        {
            private readonly int _burstEpoch;
            private int _disposed;
            internal InteractiveBurstLease(int burstEpoch) { _burstEpoch = burstEpoch; }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                lock (LeaseGate)
                    if (_burstEpoch == _interactiveBurstEpoch)
                        _interactiveBurstCount = Math.Max(0, _interactiveBurstCount - 1);
            }
        }

        /// <summary>
        /// 群聊等玩家正在等待的多路交互优先。期间不新启画像蒸馏，并取消尚未完成的画像请求，
        /// 让派生缓存立即释放全局 LLM 通道；下一次交谈会按最新输入重新调度。
        /// </summary>
        public static IDisposable BeginInteractiveBurst()
        {
            var cancellations = new List<CancellationTokenSource>();
            int burstEpoch;
            lock (LeaseGate)
            {
                burstEpoch = _interactiveBurstEpoch;
                _interactiveBurstCount++;
                if (_interactiveBurstCount == 1)
                {
                    BackgroundScheduled.Clear();
                    foreach (var pair in InflightCancellation)
                        if (pair.Value != null && !InflightInteractivePriority.Contains(pair.Key))
                            cancellations.Add(pair.Value);
                }
            }
            foreach (var cancellation in cancellations)
                try { cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
                catch (Exception ex)
                {
                    // A third-party cancellation callback must not prevent group chat from
                    // acquiring priority over the remaining background requests.
                    Debug.LogWarning("[JHYL_PORTRAIT_BURST_CANCEL_FAILED] " + ex.GetType().Name);
                }
            return new InteractiveBurstLease(burstEpoch);
        }

        private static bool InteractiveBurstActive()
        {
            lock (LeaseGate) return _interactiveBurstCount > 0;
        }

        private static string NpcKey(NpcSnapshot s) => NpcKey(s.TaiwuId, s.NpcId);
        private static string NpcKey(int taiwuId, int npcId) => taiwuId + ":" + npcId;

        /// <summary>清空/删除某 NPC 数据前调用，使所有旧画像请求永久失去写入资格。</summary>
        public static void Invalidate(int taiwuId, int npcId)
        {
            string key = NpcKey(taiwuId, npcId);
            CancellationTokenSource cancellation = null;
            lock (LeaseGate)
            {
                Revisions.TryGetValue(key, out int revision);
                Revisions[key] = unchecked(revision + 1);
                Inflight.Remove(key);
                if (InflightCancellation.TryGetValue(key, out cancellation)) InflightCancellation.Remove(key);
                InflightInteractivePriority.Remove(key);
                BackgroundScheduled.Remove(key);
                RemoveUpgradeSuppressionsLocked(key);
            }
            try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// Cancels only an explicitly awaited group-seat portrait. Ordinary background
        /// generation is never touched. The lease is removed before cancellation so a second
        /// open group can immediately acquire a fresh request even if the provider needs a
        /// moment to observe the canceled token.
        /// </summary>
        public static void CancelInteractiveGeneration(NpcSnapshot snap)
        {
            if (snap == null) return;
            string key = NpcKey(snap);
            CancellationTokenSource cancellation = null;
            lock (LeaseGate)
            {
                if (!InflightInteractivePriority.Contains(key)) return;
                Inflight.Remove(key);
                InflightInteractivePriority.Remove(key);
                if (InflightCancellation.TryGetValue(key, out cancellation))
                    InflightCancellation.Remove(key);
            }
            try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
        }

        /// <summary>切换世界时作废全部后台画像请求；旧协程完成后也不能写进新存档。</summary>
        public static void ResetForWorldExit()
        {
            var cancellations = new List<CancellationTokenSource>();
            lock (LeaseGate)
            {
                _epoch = unchecked(_epoch + 1);
                foreach (var pair in InflightCancellation) if (pair.Value != null) cancellations.Add(pair.Value);
                Inflight.Clear();
                InflightCancellation.Clear();
                InflightInteractivePriority.Clear();
                BackgroundScheduled.Clear();
                UpgradeRetrySuppression.Clear();
                Revisions.Clear();
                _interactiveBurstEpoch = unchecked(_interactiveBurstEpoch + 1);
                _interactiveBurstCount = 0;
            }
            foreach (var cancellation in cancellations)
                try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }

        private static int RevisionOf(string key)
        {
            lock (LeaseGate) return Revisions.TryGetValue(key, out int revision) ? revision : 0;
        }

        private static bool HasInflight(string key)
        {
            lock (LeaseGate) return Inflight.ContainsKey(key);
        }

        private static bool UpgradeSuppressed(string key, string fingerprint)
        {
            lock (LeaseGate)
            {
                string suppressionKey = UpgradeSuppressionKey(key, fingerprint);
                if (!UpgradeRetrySuppression.TryGetValue(suppressionKey, out RetrySuppression suppression)) return false;
                if (DateTime.UtcNow.Ticks >= suppression.RetryAfterUtcTicks)
                {
                    UpgradeRetrySuppression.Remove(suppressionKey);
                    return false;
                }
                return true;
            }
        }

        private static void SuppressUpgrade(string key, string fingerprint, bool evidenceRejected)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(fingerprint)) return;
            lock (LeaseGate)
                UpgradeRetrySuppression[UpgradeSuppressionKey(key, fingerprint)] = new RetrySuppression
                {
                    Fingerprint = fingerprint,
                    // An evidence-grounding rejection is deterministic for identical input;
                    // wait for evidence to change.  Transport/empty failures get a short cooldown.
                    RetryAfterUtcTicks = evidenceRejected
                        ? DateTime.MaxValue.Ticks
                        : DateTime.UtcNow.AddMinutes(10).Ticks,
                };
        }

        private static void ClearUpgradeSuppression(string key)
        {
            lock (LeaseGate) RemoveUpgradeSuppressionsLocked(key);
        }

        private static string UpgradeSuppressionKey(string key, string fingerprint)
            => (key ?? "") + "|" + HashText(fingerprint ?? "");

        private static void RemoveUpgradeSuppressionsLocked(string key)
        {
            string prefix = (key ?? "") + "|";
            var remove = new List<string>();
            foreach (string suppressionKey in UpgradeRetrySuppression.Keys)
                if (suppressionKey.StartsWith(prefix, StringComparison.Ordinal)) remove.Add(suppressionKey);
            foreach (string suppressionKey in remove) UpgradeRetrySuppression.Remove(suppressionKey);
        }

        private static bool RequestStillValid(string key, int revision, int worldGeneration)
        {
            if (!WorldLifecycle.IsSameWorld(worldGeneration)) return false;
            lock (LeaseGate)
                return (Revisions.TryGetValue(key, out int current) ? current : 0) == revision;
        }

        private static GenerationLease TryBeginLease(string key, int revision, int worldGeneration,
            bool interactivePriority = false)
        {
            if (!WorldLifecycle.IsSameWorld(worldGeneration)) return null;
            lock (LeaseGate)
            {
                int current = Revisions.TryGetValue(key, out int value) ? value : 0;
                if (current != revision || Inflight.ContainsKey(key)) return null;
                long token = unchecked(++_nextToken);
                var cancellation = new CancellationTokenSource();
                Inflight[key] = token;
                InflightCancellation[key] = cancellation;
                if (interactivePriority) InflightInteractivePriority.Add(key);
                return new GenerationLease
                {
                    Key = key,
                    Revision = revision,
                    Epoch = _epoch,
                    WorldGeneration = worldGeneration,
                    Token = token,
                    Cancellation = cancellation,
                    InteractivePriority = interactivePriority,
                };
            }
        }

        private static bool LeaseValid(GenerationLease lease)
        {
            if (lease == null || lease.Cancellation == null || lease.Cancellation.IsCancellationRequested
                || !WorldLifecycle.IsSameWorld(lease.WorldGeneration)) return false;
            lock (LeaseGate)
            {
                int revision = Revisions.TryGetValue(lease.Key, out int value) ? value : 0;
                return lease.Epoch == _epoch && revision == lease.Revision
                    && Inflight.TryGetValue(lease.Key, out long token) && token == lease.Token;
            }
        }

        private static void EndLease(GenerationLease lease)
        {
            if (lease == null) return;
            lock (LeaseGate)
            {
                bool owned = false;
                if (Inflight.TryGetValue(lease.Key, out long token) && token == lease.Token)
                {
                    Inflight.Remove(lease.Key);
                    owned = true;
                }
                CancellationTokenSource current;
                if (InflightCancellation.TryGetValue(lease.Key, out current)
                    && ReferenceEquals(current, lease.Cancellation))
                {
                    InflightCancellation.Remove(lease.Key);
                    owned = true;
                }
                if (owned) InflightInteractivePriority.Remove(lease.Key);
            }
            try { lease.Cancellation?.Dispose(); } catch { }
        }

        /// <summary>
        /// 立即返回当前画像；没有时返回确定性的八节 BuildSimple。需要升级时仅登记一个后台任务，
        /// 不等待 LLM，故可直接放在首轮正文的关键路径上。后台结果通过 world/revision/token 三重门控，
        /// 清空、换档或同 NPC 新一代请求都会令旧结果失去写入资格。
        /// </summary>
        public static string GetImmediateAndScheduleUpgrade(NpcSnapshot snap)
        {
            if (snap == null) return "";
            string existing = PortraitStore.GetPortrait(snap);
            string immediate = existing ?? PortraitStore.BuildSimple(snap);
            int worldGeneration = WorldLifecycle.Generation;
            if (!WorldLifecycle.IsSameWorld(worldGeneration)) return immediate;
            if (InteractiveBurstActive()) return immediate;

            string sourceFingerprint = SourceFingerprint(snap);
            string retryFingerprint = "seed|" + sourceFingerprint;
            if ((existing == null || PortraitStore.NeedsUpgrade(snap, sourceFingerprint))
                && !UpgradeSuppressed(NpcKey(snap), retryFingerprint))
                ScheduleBackgroundUpgrade(snap, worldGeneration);
            return immediate;
        }

        /// <summary>
        /// 向后兼容旧协程调用点，但语义改为非阻塞：回调在首个 MoveNext 内立即收到
        /// existing/BuildSimple，完整蒸馏由 single-flight 后台协程完成并供下一轮使用。
        /// </summary>
        public static IEnumerator EnsureSeeded(NpcSnapshot snap, Action<string> onReady)
        {
            onReady?.Invoke(GetImmediateAndScheduleUpgrade(snap));
            yield break;
        }

        /// <summary>
        /// 显式等待一份与当前客观底座一致的完整画像。普通单聊仍走 EnsureSeeded 的非阻塞路径；
        /// 群聊入座阶段会并发调用本方法，在任何成员正式发言前把缺失/过期画像准备完毕。
        /// single-flight、世界代次与画像修订门禁仍由 GenerateSeeded 统一负责，不会重复落盘。
        /// </summary>
        public static IEnumerator EnsureSeededReady(NpcSnapshot snap, Action<string> onReady)
        {
            if (snap == null) { onReady?.Invoke(""); yield break; }
            // 群聊入座只判断“有没有可读画像”。已有任何非空画像就直接复用，
            // 不因 schema、来源指纹、版本或当前状态变化而阻塞群聊重新生成。
            string existing = PortraitStore.GetPortrait(snap);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                onReady?.Invoke(existing);
                yield break;
            }
            yield return GenerateSeeded(snap, onReady, interactivePriority: true);
        }

        /// <summary>
        /// 玩家在人设窗口明确点击“AI 更新”时，基于刚读取的完整人物快照强制蒸馏一次。
        /// 保留旧画像作为失败兜底；只有新画像通过证据校验并耐久落盘后才对外发布。
        /// </summary>
        public static IEnumerator RegenerateReady(NpcSnapshot snap, Action<string> onReady)
        {
            if (snap == null) { onReady?.Invoke(""); yield break; }
            Invalidate(snap.TaiwuId, snap.NpcId);
            yield return GenerateSeeded(snap, onReady, interactivePriority: true,
                forceRegeneration: true);
        }

        private static void ScheduleBackgroundUpgrade(NpcSnapshot snap, int worldGeneration)
        {
            var host = TalkEntryHost.Instance;
            if (host == null || snap == null) return;
            string key = NpcKey(snap);
            BackgroundUpgradeRequest request;
            lock (LeaseGate)
            {
                int revision = Revisions.TryGetValue(key, out int value) ? value : 0;
                if (Inflight.ContainsKey(key) || BackgroundScheduled.ContainsKey(key)) return;
                long token = unchecked(++_nextToken);
                BackgroundScheduled[key] = token;
                request = new BackgroundUpgradeRequest
                {
                    Key = key,
                    Revision = revision,
                    WorldGeneration = worldGeneration,
                    Token = token,
                };
            }

            try { host.StartCoroutine(RunScheduledUpgrade(snap, request)); }
            catch
            {
                lock (LeaseGate)
                    if (BackgroundScheduled.TryGetValue(key, out long current) && current == request.Token)
                        BackgroundScheduled.Remove(key);
            }
        }

        private static bool BackgroundRequestValid(BackgroundUpgradeRequest request)
        {
            if (request == null || !RequestStillValid(request.Key, request.Revision, request.WorldGeneration)) return false;
            lock (LeaseGate)
                return BackgroundScheduled.TryGetValue(request.Key, out long token) && token == request.Token;
        }

        private static IEnumerator RunScheduledUpgrade(NpcSnapshot snap, BackgroundUpgradeRequest request)
        {
            try
            {
                if (!BackgroundRequestValid(request) || InteractiveBurstActive()) yield break;
                yield return GenerateSeeded(snap, _ => { });
            }
            finally
            {
                lock (LeaseGate)
                    if (request != null && BackgroundScheduled.TryGetValue(request.Key, out long token) && token == request.Token)
                        BackgroundScheduled.Remove(request.Key);
            }
        }

        /// <summary>实际蒸馏实现；后台调度与群聊显式等待均复用同一套 single-flight/代次门禁。</summary>
        private static IEnumerator GenerateSeeded(NpcSnapshot snap, Action<string> onReady,
            bool interactivePriority = false, bool forceRegeneration = false)
        {
            if (snap == null) { onReady?.Invoke(""); yield break; }

            int worldGeneration = WorldLifecycle.Generation;
            if (!WorldLifecycle.IsSameWorld(worldGeneration)) yield break;
            string key = NpcKey(snap);
            int revision = RevisionOf(key);
            string existing = PortraitStore.GetPortrait(snap);
            string sourceFingerprint = SourceFingerprint(snap);
            string retryFingerprint = "seed|" + sourceFingerprint;
            if (!forceRegeneration && UpgradeSuppressed(key, retryFingerprint))
            {
                onReady?.Invoke(existing ?? PortraitStore.BuildSimple(snap));
                yield break;
            }
            var memoryInput = BuildMemoryInput(snap);
            if (!forceRegeneration && existing != null
                && !PortraitStore.NeedsUpgrade(snap, sourceFingerprint))
            { onReady?.Invoke(existing); yield break; }
            if (!memoryInput.Reliable)
            {
                Debug.LogWarning("[江湖有灵] 记忆文件损坏且无有效备份,暂停画像改写 npc=" + snap.NpcId);
                onReady?.Invoke(existing ?? PortraitStore.BuildSimple(snap));
                yield break;
            }

            if (HasInflight(key))
            {
                yield return new WaitUntil(() => !HasInflight(key)
                    || !RequestStillValid(key, revision, worldGeneration));
                if (!RequestStillValid(key, revision, worldGeneration)) yield break;
                string completed = PortraitStore.GetPortrait(snap);
                if (!forceRegeneration && completed != null
                    && !PortraitStore.NeedsUpgrade(snap, SourceFingerprint(snap)))
                    onReady?.Invoke(completed);
                else
                    // The previous request may have been a background job canceled when this
                    // interactive group opened. Acquire a fresh priority lease instead of
                    // claiming that a deterministic placeholder is a completed portrait.
                    yield return GenerateSeeded(snap, onReady, interactivePriority,
                        forceRegeneration);
                yield break;
            }

            var lease = TryBeginLease(key, revision, worldGeneration, interactivePriority);
            if (lease == null) yield break;
            try
            {
                var client = LlmService.GetBackgroundClient();   // 画像蒸馏走后台模型(留空=同主模型)
                if (client == null)
                {
                    // 已有旧画像时绝不因暂时无 LLM 覆盖它；下次配置好后台模型再升级。
                    string simple = existing ?? PortraitStore.BuildSimple(snap);
                    if (existing == null && LeaseValid(lease))
                        PortraitStore.Save(snap, simple, "simple", memoryInput.ValidCount, memoryInput.Fingerprint, sourceFingerprint);
                    if (LeaseValid(lease)) onReady?.Invoke(simple);
                    yield break;
                }

                string result = null;
                System.Threading.Tasks.Task<LlmResult> task = null;
                var evidenceProfile = BuildEvidenceProfile(snap);
                var evidenceLife = TextList(snap.LifeRecords, 20);
                var evidenceSecrets = TextList(snap.Secrets, 12);
                try
                {
                    var msgs = PortraitDistiller.BuildMessages(existing, evidenceProfile,
                        evidenceLife, evidenceSecrets, memoryInput.Lines,
                        explicitAppendRequest: forceRegeneration
                            && PortraitDistiller.UsesCustomPersonaAppendMode(evidenceProfile));
                    task = client.SendAsync(msgs, 0, ct: lease.Cancellation.Token, timeoutSec: 180,
                        tag: interactivePriority ? "group-seat-profile" : "画像蒸馏",
                        reasoningPolicy: LlmReasoningPolicy.Auto);
                }
                catch { task = null; }

                if (task != null)
                {
                    yield return new WaitUntil(() => task.IsCompleted || !LeaseValid(lease));
                    if (!LeaseValid(lease)) yield break;
                    try { var r = task.Result; if (r != null && r.Ok) result = PortraitDistiller.Clean(r.Content); } catch { }
                }

                yield return RepairCustomAppendOnce(client, lease, existing, result,
                    evidenceProfile, evidenceLife, evidenceSecrets, memoryInput.Lines,
                    value => result = value, forceRegeneration);
                if (!LeaseValid(lease)) yield break;
                var currentMemory = BuildMemoryInput(snap);
                bool customAppend = PortraitDistiller.UsesCustomPersonaAppendMode(evidenceProfile);
                bool modelReturnedContent = !string.IsNullOrWhiteSpace(result);
                string groundingFailure = null;
                bool complete;
                if (customAppend)
                {
                    complete = PortraitDistiller.TryMergeCustomPersonaAppend(existing, result,
                        evidenceProfile, evidenceLife, evidenceSecrets, memoryInput.Lines,
                        out string merged, out groundingFailure);
                    result = complete ? merged : null;
                }
                else
                {
                    complete = PortraitDistiller.IsEvidenceGrounded(result, evidenceProfile,
                        evidenceLife, evidenceSecrets, memoryInput.Lines, out groundingFailure);
                }
                if (!complete && modelReturnedContent)
                    Debug.LogWarning("[JHYL_PORTRAIT_REJECTED] npc=" + snap.NpcId + " reason=" + groundingFailure);
                bool generatedPortraitPersisted = false;
                if (!complete)
                {
                    SuppressUpgrade(key, retryFingerprint, modelReturnedContent);
                    result = customAppend
                        ? PortraitDistiller.CustomPersonaAppendFallback(existing)
                        : (existing ?? PortraitStore.BuildSimple(snap));
                    // A rejected delta has not consumed the new evidence. Preserve the old
                    // portrait and fingerprints; only a first-time placeholder may be saved.
                    if (currentMemory.Reliable && LeaseValid(lease) && existing == null)
                        PortraitStore.Save(snap, result, customAppend ? "custom-append" : "simple",
                            0, "", "");
                }
                else if (LeaseValid(lease))
                {
                    generatedPortraitPersisted = PortraitStore.Save(snap, result,
                        customAppend ? "llm-custom-append" : "llm", memoryInput.ValidCount,
                        memoryInput.Fingerprint, sourceFingerprint);
                    if (generatedPortraitPersisted) ClearUpgradeSuppression(key);
                    else SuppressUpgrade(key, retryFingerprint, false);
                }
                if (LeaseValid(lease))
                {
                    // 完整 LLM 画像只有在耐久提交成功后才可跨页签发布；否则继续使用上一份权威画像。
                    string ready = complete && !generatedPortraitPersisted
                        ? (PortraitStore.GetPortrait(snap) ?? existing ?? PortraitStore.BuildSimple(snap))
                        : result;
                    onReady?.Invoke(ready);
                }
            }
            finally
            {
                EndLease(lease);
            }
        }

        /// <summary>把自上次固化以来新增的对话记忆融入画像(一段对话结束后调用)。无新记忆/无 LLM 则跳过。</summary>
        public static IEnumerator Consolidate(NpcSnapshot snap)
        {
            if (snap == null) yield break;
            int worldGeneration = WorldLifecycle.Generation;
            if (!WorldLifecycle.IsSameWorld(worldGeneration)) yield break;

            string prior = PortraitStore.GetPortrait(snap);
            if (prior == null) { yield return EnsureSeeded(snap, _ => { }); yield break; }   // 还没初遇画像,先建

            var memoryInput = BuildMemoryInput(snap);
            if (!memoryInput.Reliable)
            {
                Debug.LogWarning("[江湖有灵] 记忆文件损坏且无有效备份,暂停画像固化 npc=" + snap.NpcId);
                yield break;
            }
            string fingerprint = memoryInput.Fingerprint;
            string sourceFingerprint = SourceFingerprint(snap);
            string updateRetryFingerprint = "update|" + sourceFingerprint + "|" + fingerprint
                + "|" + HashText(prior);
            if (string.Equals(fingerprint, PortraitStore.GetMemoryFingerprint(snap), StringComparison.Ordinal)
                && string.Equals(sourceFingerprint, PortraitStore.GetSourceFingerprint(snap), StringComparison.Ordinal)
                && !PortraitStore.NeedsUpgrade(snap, sourceFingerprint)) yield break;

            var recent = new List<string>(memoryInput.Lines);
            if (recent.Count == 0)
                recent.Add("（当前没有仍有效的长期关系记忆；旧画像中仅由已删除记忆支撑的判断应撤回。）");

            string key = NpcKey(snap);
            if (UpgradeSuppressed(key, updateRetryFingerprint)) yield break;
            int revision = RevisionOf(key);
            if (HasInflight(key)) yield break;
            var client = LlmService.GetBackgroundClient();   // 画像蒸馏走后台模型(留空=同主模型)
            if (client == null) yield break;

            var lease = TryBeginLease(key, revision, worldGeneration);
            if (lease == null) yield break;
            try
            {
                string result = null;
                System.Threading.Tasks.Task<LlmResult> task = null;
                var evidenceProfile = BuildEvidenceProfile(snap);
                var evidenceLife = TextList(snap.LifeRecords, 20);
                var evidenceSecrets = TextList(snap.Secrets, 12);
                try
                {
                    // 与 SourceFingerprint 使用完全相同的规范证据范围，禁止“没喂给模型却标记已吸收”。
                    var msgs = PortraitDistiller.BuildMessages(prior, evidenceProfile,
                        evidenceLife, evidenceSecrets, recent);
                    task = client.SendAsync(msgs, 0, ct: lease.Cancellation.Token, timeoutSec: 180,
                        tag: "画像蒸馏", reasoningPolicy: LlmReasoningPolicy.Auto);
                }
                catch { task = null; }

                if (task != null)
                {
                    yield return new WaitUntil(() => task.IsCompleted || !LeaseValid(lease));
                    if (!LeaseValid(lease)) yield break;
                    try { var r = task.Result; if (r != null && r.Ok) result = PortraitDistiller.Clean(r.Content); } catch { }
                }

                yield return RepairCustomAppendOnce(client, lease, prior, result,
                    evidenceProfile, evidenceLife, evidenceSecrets, recent,
                    value => result = value);
                if (!LeaseValid(lease)) yield break;
                bool modelReturnedContent = !string.IsNullOrWhiteSpace(result);
                bool customAppend = PortraitDistiller.UsesCustomPersonaAppendMode(evidenceProfile);
                string groundingFailure = null;
                bool grounded;
                if (customAppend)
                {
                    grounded = PortraitDistiller.TryMergeCustomPersonaAppend(prior, result,
                        evidenceProfile, evidenceLife, evidenceSecrets, recent,
                        out string merged, out groundingFailure);
                    result = grounded ? merged : null;
                }
                else
                {
                    grounded = PortraitDistiller.IsEvidenceGrounded(result, evidenceProfile,
                        evidenceLife, evidenceSecrets, recent, out groundingFailure);
                }
                if (grounded)
                {
                    // 对话期间新增记忆或人物状态变化不应浪费已经完成的蒸馏结果。
                    // 仍以请求实际读取的指纹落盘，下一次调度会自然补入新增证据。
                    // 只有画像正文已被另一条显式写入替换时，才禁止旧任务覆盖新画像。
                    if (!string.Equals(PortraitStore.GetPortrait(snap), prior, StringComparison.Ordinal))
                    {
                        Debug.Log("[江湖有灵] 画像更新期间已有更新版本,保留较新画像 npc=" + snap.NpcId);
                        yield break;
                    }
                    if (!LeaseValid(lease)) yield break;
                    if (PortraitStore.Save(snap, result,
                        customAppend ? "llm-custom-append" : "llm-update",
                        memoryInput.ValidCount, fingerprint, sourceFingerprint))
                    {
                        ClearUpgradeSuppression(key);
                        Debug.Log("[江湖有灵] 画像已固化更新(" + snap.Name + ",融入 " + recent.Count + " 条新记忆)");
                    }
                    else SuppressUpgrade(key, updateRetryFingerprint, false);
                }
                else
                {
                    SuppressUpgrade(key, updateRetryFingerprint, modelReturnedContent);
                    if (modelReturnedContent)
                        Debug.LogWarning("[JHYL_PORTRAIT_REJECTED] npc=" + snap.NpcId + " reason=" + groundingFailure);
                }
            }
            finally
            {
                EndLease(lease);
            }
        }

        private static IEnumerator RepairCustomAppendOnce(OpenAiCompatibleClient client,
            GenerationLease lease, string prior, string raw, NpcProfileForPrompt profile,
            IList<string> life, IList<string> secrets, IList<string> memories,
            Action<string> onResult, bool explicitAppendRequest = false)
        {
            onResult(raw);
            if (!LeaseValid(lease) || !PortraitDistiller.UsesCustomPersonaAppendMode(profile)
                || string.IsNullOrWhiteSpace(raw)) yield break;
            if (PortraitDistiller.TryMergeCustomPersonaAppend(prior, raw, profile,
                life, secrets, memories, out _, out string rejectionReason)) yield break;
            System.Threading.Tasks.Task<LlmResult> repair = null;
            try
            {
                repair = client.SendAsync(PortraitDistiller.BuildCustomAppendRepairMessages(
                    prior, profile, life, secrets, memories, raw, rejectionReason,
                    explicitAppendRequest), 0, ct: lease.Cancellation.Token,
                    timeoutSec: 180, tag: "画像增量纠正", reasoningPolicy: LlmReasoningPolicy.Auto);
            }
            catch { }
            if (repair == null) yield break;
            yield return new WaitUntil(() => repair.IsCompleted || !LeaseValid(lease));
            if (!LeaseValid(lease)) yield break;
            try
            {
                LlmResult result = repair.Result;
                if (result != null && result.Ok && !string.IsNullOrWhiteSpace(result.Content))
                    onResult(PortraitDistiller.Clean(result.Content));
            }
            catch { }
            // The caller validates again before saving. No invalid result is normalized
            // into accepted evidence, and a second rejection never starts another retry.
        }

        private sealed class MemoryInput
        {
            public readonly List<string> Lines = new List<string>();
            public string Fingerprint;
            public int ValidCount;
            public bool Reliable = true;
        }

        private static MemoryInput BuildMemoryInput(NpcSnapshot snap)
        {
            var result = new MemoryInput();
            var candidates = new List<MemoryEntry>();
            if (snap != null)
            {
                var store = NpcMemoryStore.Load(JianghuYoulingPaths.Memories, snap.TaiwuId.ToString(), snap.NpcId.ToString());
                result.Reliable = store.LoadReliable;
                foreach (var e in store.All)
                    if (e != null && e.Valid && !string.IsNullOrWhiteSpace(e.Content)) candidates.Add(e);
            }
            result.ValidCount = candidates.Count;
            candidates.Sort((a, b) =>
            {
                int core = b.IsCore.CompareTo(a.IsCore); if (core != 0) return core;
                int imp = b.Importance.CompareTo(a.Importance); if (imp != 0) return imp;
                int date = b.WorldDate.CompareTo(a.WorldDate); if (date != 0) return date;
                return string.CompareOrdinal(a.Id ?? "", b.Id ?? "");
            });
            if (candidates.Count > 64) candidates.RemoveRange(64, candidates.Count - 64);
            foreach (var e in candidates)
            {
                string c = e.Content.Trim(); if (c.Length > 320) c = c.Substring(0, 320) + "…";
                result.Lines.Add("[" + e.Type + "|" + TalkPromptBuilder.FormatWorldMonth(e.WorldDate) + "] " + c);
            }
            if (result.Lines.Count == 0) result.Lines.Add("（当前无有效长期关系记忆。）");
            result.Fingerprint = MemoryFingerprint(candidates);
            return result;
        }

        private static string MemoryFingerprint(IReadOnlyList<MemoryEntry> entries)
        {
            var sb = new StringBuilder();
            if (entries != null)
                foreach (var e in entries)
                {
                    if (e == null || !e.Valid) continue;
                    sb.Append(e.Id).Append('|').Append(e.SourceKind).Append('|').Append(e.SourceId).Append('|')
                      .Append(e.Type).Append('|').Append(e.Importance).Append('|').Append(e.WorldDate).Append('|')
                      .Append(e.Keywords).Append('|').Append(e.Content).Append('\n');
                }
            return HashText(sb.ToString());
        }

        private static string SourceFingerprint(NpcSnapshot snap)
        {
            if (snap == null) return null;
            var p = BuildEvidenceProfile(snap);
            return PortraitDistiller.SourceEvidenceFingerprint(p, TextList(snap.LifeRecords, 20), TextList(snap.Secrets, 12),
                "");
        }

        private static NpcProfileForPrompt BuildEvidenceProfile(NpcSnapshot snap)
        {
            var p = PortraitStore.BuildProfile(snap);
            if (snap == null) return p;
            p.CustomPersona = JianghuYouling.Core.Persona.PersonaStore.Load(
                JianghuYoulingPaths.Personas, snap.TaiwuId.ToString(), snap.NpcId.ToString());
            p.CustomPersonaMode = JianghuYouling.Core.Persona.PersonaStore.LoadMode(
                JianghuYoulingPaths.Personas, snap.TaiwuId.ToString(), snap.NpcId.ToString());
            return p;
        }

        private static string HashText(string text)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
                var hex = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) hex.Append(b.ToString("x2"));
                return hex.ToString();
            }
        }

        private static List<string> TextList(List<(int date, string type, string text)> src, int max)
        {
            var r = new List<string>();
            if (src == null) return r;
            int from = Math.Max(0, src.Count - max);
            for (int i = from; i < src.Count; i++)
                if (!string.IsNullOrWhiteSpace(src[i].text)) r.Add(src[i].text);
            return r;
        }
    }
}
