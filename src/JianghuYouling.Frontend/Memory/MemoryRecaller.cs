using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Memory;

namespace JianghuYouling
{
    /// <summary>
    /// 分层记忆召回：小而且确实装得下的索引整份内联；中型索引本地相关度+MMR；
    /// 只有极大的旧角色索引才用后台模型做一次语义精排。
    /// </summary>
    public static class MemoryRecaller
    {
        const int InlineCap = 60;
        const int InlineCharacterBudget = 12000;
        const int DeterministicCap = 240;
        const int SelectTrim = 120;
        const int MaxPick = 18;
        const int MaxCoreTopUp = 8;
        const int MaxReturned = MaxPick + MaxCoreTopUp;

        /// <summary>
        /// 一次召回的可提交结果。RecallDetailed 只选中命中项，不立即改 RecallCount；
        /// 调用方应在聊天记录成功提交后调用 TryPersist。这样失败/取消的回话不会巩固记忆。
        /// </summary>
        public sealed class RecallResult
        {
            private readonly List<MemoryEntry> _persistedHits;
            private readonly long _now;
            private bool _applied;

            internal RecallResult(List<string> lines, List<MemoryEntry> persistedHits, long now)
            {
                Lines = lines ?? new List<string>();
                _persistedHits = persistedHits ?? new List<MemoryEntry>();
                _now = now;
            }

            public List<string> Lines { get; }
            public IReadOnlyList<MemoryEntry> PersistedHits => _persistedHits;

            /// <summary>有持久记忆命中；成功聊天后应用这些计数会使 store 变脏。</summary>
            public bool Dirty => _persistedHits.Count > 0;
            public bool Applied => _applied;

            /// <summary>幂等应用命中计数，但不写盘。适合需要自行控制事务边界的调用方。</summary>
            public bool ApplyRecallMarks()
            {
                if (_applied) return Dirty;
                _applied = true;
                foreach (var entry in _persistedHits) NpcMemoryStore.MarkRecalled(entry, _now);
                return Dirty;
            }

            /// <summary>
            /// 在成功聊天后幂等应用并保存。所有存盘异常都折成 false，调用方只需记录告警，
            /// 不能因为召回统计保存失败而撤销或隐藏已经成功的聊天。
            /// </summary>
            public bool TryPersist(NpcMemoryStore store)
            {
                if (!Dirty) return true;
                if (store == null) return false;
                try
                {
                    if (!_applied)
                    {
                        store.MarkRecalledMatches(_persistedHits, _now);
                        _applied = true;
                    }
                    return store.Save();
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// 旧调用保持兼容：仍返回渲染后的字符串，并沿用旧行为在内存中立即标记命中。
        /// 新调用应改用 RecallDetailed，在对话持久化成功后再 result.TryPersist(store)。
        /// </summary>
        public static IEnumerator Recall(IReadOnlyList<MemoryEntry> persisted, NpcSnapshot snap, string topic,
            Action<List<string>> onResult, CancellationToken ct = default)
        {
            RecallResult detailed = null;
            yield return RecallDetailed(persisted, snap, topic, r => detailed = r, ct);
            detailed = detailed ?? EmptyResult(snap != null ? snap.CurrentDate : 0);
            detailed.ApplyRecallMarks();
            onResult?.Invoke(detailed.Lines);
        }

        /// <summary>
        /// 新的延迟提交 API。PersistedHits 只含传入 persisted 中真正展示给模型的有效条目，
        /// 不含从实时快照临时合成的关系/生平信息。
        /// </summary>
        public static IEnumerator RecallDetailed(IReadOnlyList<MemoryEntry> persisted, NpcSnapshot snap, string topic,
            Action<RecallResult> onResult, CancellationToken ct = default)
        {
            long now = snap != null ? snap.CurrentDate : 0;
            if (ct.IsCancellationRequested)
            {
                onResult?.Invoke(EmptyResult(now));
                yield break;
            }

            var persistedSet = new HashSet<MemoryEntry>();
            var pool = new List<MemoryEntry>();
            if (persisted != null)
                foreach (var entry in persisted)
                    if (Usable(entry))
                    {
                        pool.Add(entry);
                        persistedSet.Add(entry);
                    }

            if (snap != null)
                foreach (var entry in MemoryContextBuilder.LiveFromSnapshot(snap))
                    if (Usable(entry)) pool.Add(entry);

            if (pool.Count == 0)
            {
                onResult?.Invoke(EmptyResult(now));
                yield break;
            }

            // 只有数量和字符体量都可控时才整份内联。旧角色即使条数不多，也可能有超长模型记忆；
            // 此时改走本地 TopK，避免一个异常条目把动态 prompt 膨胀到数万 token。
            if (pool.Count <= InlineCap && EstimatedCharacters(pool) <= InlineCharacterBudget)
            {
                pool.Sort(CompareByDateThenId);
                onResult?.Invoke(BuildResult(pool, persistedSet, now));
                yield break;
            }

            if (pool.Count <= DeterministicCap)
            {
                onResult?.Invoke(Finish(pool, MemoryRanker.TopK(pool, topic, now, MaxPick), persistedSet, now));
                yield break;
            }

            // 极大索引：先本地收窄，再让后台模型只挑编号。合法 [] 表示没有相关旧事，
            // Finish 仍会补入核心记忆；仅畸形/失败输出退回本地 TopK。
            var cands = MemoryRanker.TopK(pool, topic, now, SelectTrim);
            var client = LlmService.GetBackgroundClient();
            if (client == null)
            {
                onResult?.Invoke(Finish(pool,
                    cands.GetRange(0, Math.Min(MaxPick, cands.Count)), persistedSet, now));
                yield break;
            }

            string indexText = MemoryIndex.RenderNumberedForPrompt(cands);
            var msgs = MemoryIndex.BuildSelectMessages(indexText, topic, MaxPick);
            // JHYL_INTERACTIVE_RECALL_BYPASSES_BACKGROUND_GATE：这是玩家/群聊正文的必经步骤，
            // 虽使用后台快模型做语义精排，却不能与画像、过月、记忆整理共用非交互排队标签；
            // 否则刚提交的后台压缩会占满两个槽，反过来卡住玩家紧接着发出的下一句。
            // 只解析受限编号列表。给 thinking-only 兼容模型留足推理空间，但不能继承
            // 主对话 32768/服务端无限输出；官方 max_tokens 会同时约束推理与正文。
            var task = client.SendAsync(msgs, 2048, 0.2, ct, 60, false, "对话召回",
                LlmReasoningPolicy.Off);
            yield return new WaitUntil(() => task.IsCompleted || ct.IsCancellationRequested);
            if (ct.IsCancellationRequested)
            {
                onResult?.Invoke(EmptyResult(now));
                yield break;
            }

            List<MemoryEntry> picked = null;
            bool validSelection = false;
            try
            {
                var response = task.Result;
                if (response != null && response.Ok
                    && MemoryIndex.TryParseSelection(response.Content, cands.Count, MaxPick, out var numbers))
                {
                    validSelection = true;
                    picked = new List<MemoryEntry>();
                    foreach (int number in numbers) picked.Add(cands[number - 1]);
                }
            }
            catch { }

            if (!validSelection)
                picked = cands.GetRange(0, Math.Min(MaxPick, cands.Count));
            onResult?.Invoke(Finish(pool, picked, persistedSet, now));
        }

        /// <summary>
        /// Re-ranks memory locally when a group conversation advances beyond the player message
        /// that paid for the semantic selection. This keeps later autonomous rounds topical
        /// without issuing another LLM request for every member and every round.
        /// </summary>
        public static RecallResult RetargetDeterministically(IReadOnlyList<MemoryEntry> persisted,
            NpcSnapshot snap, string topic)
        {
            long now = snap != null ? snap.CurrentDate : 0;
            var persistedSet = new HashSet<MemoryEntry>();
            var pool = new List<MemoryEntry>();
            if (persisted != null)
                foreach (MemoryEntry entry in persisted)
                    if (Usable(entry))
                    {
                        pool.Add(entry);
                        persistedSet.Add(entry);
                    }
            if (snap != null)
                foreach (MemoryEntry entry in MemoryContextBuilder.LiveFromSnapshot(snap))
                    if (Usable(entry)) pool.Add(entry);
            if (pool.Count == 0) return EmptyResult(now);
            if (pool.Count <= InlineCap && EstimatedCharacters(pool) <= InlineCharacterBudget)
            {
                pool.Sort(CompareByDateThenId);
                return BuildResult(pool, persistedSet, now);
            }
            return Finish(pool, MemoryRanker.TopK(pool, topic, now, MaxPick), persistedSet, now);
        }

        private static RecallResult Finish(IList<MemoryEntry> pool, List<MemoryEntry> picked,
            HashSet<MemoryEntry> persistedSet, long now)
        {
            picked = picked ?? new List<MemoryEntry>();
            if (picked.Count > MaxPick) picked.RemoveRange(MaxPick, picked.Count - MaxPick);
            if (pool != null && picked.Count < MaxReturned)
            {
                var coreTopUp = new List<MemoryEntry>();
                foreach (var core in pool)
                    if (Usable(core) && core.IsCore && !picked.Contains(core)) coreTopUp.Add(core);
                coreTopUp.Sort((a, b) =>
                {
                    int pinned = MemoryRanker.CoreRetentionPriority(b)
                        .CompareTo(MemoryRanker.CoreRetentionPriority(a));
                    if (pinned != 0) return pinned;
                    int score = MemoryRanker.PromoteScore(b, now).CompareTo(MemoryRanker.PromoteScore(a, now));
                    if (score != 0) return score;
                    return CompareByDateThenId(b, a);
                });
                int topUp = 0;
                foreach (var core in coreTopUp)
                {
                    if (picked.Count >= MaxReturned || topUp >= MaxCoreTopUp) break;
                    picked.Add(core);
                    topUp++;
                }
            }
            picked.RemoveAll(entry => !Usable(entry));
            picked.Sort(CompareByDateThenId);
            return BuildResult(picked, persistedSet, now);
        }

        private static RecallResult BuildResult(IList<MemoryEntry> picked, HashSet<MemoryEntry> persistedSet, long now)
        {
            var hits = new List<MemoryEntry>();
            var seen = new HashSet<MemoryEntry>();
            if (picked != null && persistedSet != null)
                foreach (var entry in picked)
                    if (entry != null && persistedSet.Contains(entry) && seen.Add(entry)) hits.Add(entry);
            return new RecallResult(MemoryIndex.RenderForPrompt(picked, now), hits, now);
        }

        private static RecallResult EmptyResult(long now)
            => new RecallResult(new List<string>(), new List<MemoryEntry>(), now);

        private static bool Usable(MemoryEntry entry)
            => entry != null && entry.Valid && !string.IsNullOrWhiteSpace(entry.Content);

        private static int EstimatedCharacters(IEnumerable<MemoryEntry> entries)
        {
            int total = 0;
            if (entries == null) return total;
            foreach (var entry in entries)
            {
                if (!Usable(entry)) continue;
                total = unchecked(total + MemoryIndex.ProjectContentForPrompt(entry).Length
                    + (entry.Keywords?.Length ?? 0) + 24);
                if (total > InlineCharacterBudget) return total;
            }
            return total;
        }

        private static int CompareByDateThenId(MemoryEntry left, MemoryEntry right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left == null) return -1;
            if (right == null) return 1;
            int date = left.WorldDate.CompareTo(right.WorldDate);
            return date != 0 ? date : string.CompareOrdinal(left.Id ?? "", right.Id ?? "");
        }
    }
}
