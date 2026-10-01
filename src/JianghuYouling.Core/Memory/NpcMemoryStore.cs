using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JianghuYouling.Core.Persistence;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Memory
{
    /// <summary>
    /// 仿 Claude 文件式记忆:离散条目 + 关键词索引 + 按相关性召回 + 重要性/新近度淘汰 + 原子写。
    /// 按 (taiwuId, npcId) 隔离。Unity 无关,可独立单测。
    /// </summary>
    public sealed class NpcMemoryStore
    {
        private const int MaxFileBytes = 16 * 1024 * 1024;
        private const int MaxEntries = 10000;
        private static readonly object PathLocksGate = new object();
        private static readonly Dictionary<string, object> PathLocks = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        // 同进程多页签各持一份 store；删除墓碑必须按路径共享，防另一个旧快照稍后 Save 把已删记忆复活。
        private static readonly Dictionary<string, HashSet<string>> RemovedIdsByPath = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, HashSet<string>> RemovedSourcesByPath = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, long> PathEpochs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Dictionary<string, long>> SourceEpochsByPath = new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);
        private readonly string _path;
        private readonly List<MemoryEntry> _entries;
        private readonly HashSet<string> _removedIds = new HashSet<string>();
        private readonly bool _loadReliable;
        private readonly long _pathEpoch;
        private readonly Dictionary<string, long> _sourceEpochSnapshot;
        // 每个 store 记录自己加载/上次成功保存时看到的召回计数。并发保存时只把本
        // store 此后产生的增量叠加到最新磁盘值，不能用 Max 吞掉其它页签的独立命中。
        private readonly Dictionary<string, RecallSnapshot> _recallBaseline;

        private sealed class RecallSnapshot
        {
            public int Count;
            public int Months;
            public long Last;
        }

        private NpcMemoryStore(string path, List<MemoryEntry> entries, bool loadReliable, long pathEpoch, Dictionary<string, long> sourceEpochSnapshot)
        {
            _path = path;
            _entries = entries;
            _loadReliable = loadReliable;
            _pathEpoch = pathEpoch;
            _sourceEpochSnapshot = sourceEpochSnapshot ?? new Dictionary<string, long>(StringComparer.Ordinal);
            _recallBaseline = CaptureRecallBaseline(entries);
        }

        public static NpcMemoryStore Load(string dir, string taiwuId, string npcId)
        {
            ValidateIdentity(taiwuId, nameof(taiwuId));
            ValidateIdentity(npcId, nameof(npcId), allowAssistant: true);
            var path = Path.Combine(dir, "Memory_" + taiwuId + "_" + npcId + ".json");
            List<MemoryEntry> list;
            bool reliable;
            long epoch;
            Dictionary<string, long> sourceEpochs;
            lock (GetPathLock(path))
            {
                list = ReadRecoverable(path, preserveCorrupt: true, out reliable);
                epoch = CurrentPathEpoch(path);
                sourceEpochs = new Dictionary<string, long>(SourceEpochs(path), StringComparer.Ordinal);
            }
            return new NpcMemoryStore(path, list ?? new List<MemoryEntry>(), reliable, epoch, sourceEpochs);
        }

        /// <summary>列出某太吾名下已建记忆的所有 npcId(从 Memory_{taiwuId}_{npcId}.json 文件名解析)。供过月清理死者用。</summary>
        public static List<int> EnumerateNpcIds(string dir, string taiwuId)
        {
            ValidateIdentity(taiwuId, nameof(taiwuId));
            var ids = new HashSet<int>();
            if (!Directory.Exists(dir)) return ids.ToList();
            string prefix = "Memory_" + taiwuId + "_";
            foreach (var f in Directory.GetFiles(dir, prefix + "*.json*"))
            {
                string name = Path.GetFileName(f);
                int jsonAt = name.IndexOf(".json", StringComparison.OrdinalIgnoreCase);
                if (jsonAt <= prefix.Length || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string tail = name.Substring(prefix.Length, jsonAt - prefix.Length);
                if (int.TryParse(tail, out int npc) && npc > 0) ids.Add(npc);
            }
            return ids.OrderBy(x => x).ToList();
        }

        /// <summary>删除某 npc 的全部记忆恢复文件。返回目标是否已确认不存在（原本就没有也算成功）。</summary>
        public static bool DeleteFile(string dir, string taiwuId, string npcId)
        {
            ValidateIdentity(taiwuId, nameof(taiwuId));
            ValidateIdentity(npcId, nameof(npcId), allowAssistant: true);
            var path = Path.Combine(dir, "Memory_" + taiwuId + "_" + npcId + ".json");
            lock (GetPathLock(path))
            {
                // 先使所有已加载快照永久失效，再做可能部分失败的 I/O。若先删文件、最后才
                // 提升 epoch，删除 bak/index 时的异常会让旧 store 继续 Save 并复活主文件。
                PathEpochs[path] = unchecked(CurrentPathEpoch(path) + 1);
                RemovedIdsByPath.Remove(path);
                RemovedSourcesByPath.Remove(path);
                SourceEpochsByPath.Remove(path);

                // 各 File.Delete 之间没有持久屏障，而恢复读取会把幸存的 bak/tmp 当作最后提交值
                // 提升回 main。因此删除前先把空文档原子提交为新的提交点，并双写使 main 与 bak
                // 都成为空文档（同 PersonaStore/WorldBookStore 的墓碑双写防复活写法）；此后删除
                // 无论中断在哪一步，任何幸存副本都只能读出空记忆。提交失败不中止删除：返回
                // false 本身即“未确认清空”，故障解除后可幂等重试。
                if (File.Exists(path) || File.Exists(path + ".tmp")
                    || File.Exists(path + ".bak") || File.Exists(path + ".bak.repair")
                    || File.Exists(path + ".promote") || File.Exists(path + ".tmp.repair"))
                {
                    if (DurableFileStore.TryWriteTextAtomic(path, "[]", MaxFileBytes, IsValidDocument))
                        DurableFileStore.TryWriteTextAtomic(path, "[]", MaxFileBytes, IsValidDocument);
                }

                bool ok = true;
                void DeleteOne(string candidate)
                {
                    try
                    {
                        if (File.Exists(candidate)) File.Delete(candidate);
                        if (File.Exists(candidate)) ok = false;
                    }
                    catch { ok = false; }
                }

                string indexPath = Path.ChangeExtension(path, ".md");
                try
                {
                    string parent = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                        foreach (var candidate in Directory.GetFiles(parent, Path.GetFileName(path) + ".corrupt-*"))
                            DeleteOne(candidate);
                }
                catch { ok = false; }
                // 派生索引先删；任何失败都保留记忆 main。两套 DurableFileStore 协议文件都由
                // 同一删除原语覆盖 .promote/.tmp.repair/.bak.repair 等全部恢复工件。
                if (ok) ok = DurableFileStore.TryDeleteAllArtifacts(indexPath);
                if (ok) ok = DurableFileStore.TryDeleteAllArtifacts(path);
                return ok;
            }
        }

        public IReadOnlyList<MemoryEntry> All => _entries;
        public int Count => _entries.Count;
        /// <summary>
        /// 本次加载是否来自“有效文件或确实不存在的文件”。若主文件及恢复副本都损坏则为 false，
        /// 画像服务必须暂停固化，避免把读失败误判成“用户删除了全部记忆”。
        /// </summary>
        public bool LoadReliable => _loadReliable;

        /// <summary>写入一条记忆;若与现有记忆高度近似(Jaccard≥0.85)则合并到那条(升重要度/累加频次/刷新日期/并关键词),不新建——防同义记忆堆积。返回最终生效条目。</summary>
        public MemoryEntry Add(MemoryEntry e) { return AddOrMerge(e, out _); }

        public MemoryEntry AddOrMerge(MemoryEntry e, out bool merged)
        {
            merged = false;
            if (e == null) return null;
            if (!string.IsNullOrWhiteSpace(e.SourceId))
            {
                // 删除/重建已提升来源 epoch 时，旧异步整理任务不能把自己伪装成一次新建。
                // 只有从当前 epoch 加载的 store 才有资格创建下一版来源记忆。
                if (!TryAdvanceSourceEpoch(e)) return null;
                foreach (var x in _entries)
                    if (SameSource(x, e))
                    {
                        string keepId = x.Id;
                        CopySourceEntry(e, x);
                        if (string.IsNullOrEmpty(x.Id)) x.Id = keepId;
                        merged = true;
                        return x;
                    }
                // 稳定来源代表一桩可独立撤回的事实。不同 SourceId 即使正文一字不差也必须各存一条，
                // 更不能把相似的普通单聊记忆“认领”为群聊来源。
                if (string.IsNullOrEmpty(e.Id)) e.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
                _entries.Add(e);
                return e;
            }
            ClearTombstoneFor(e);
            var et = MemoryRanker.TokensOf(e);
            if (et.Count > 0)
            {
                MemoryEntry best = null; double bestSim = 0;
                foreach (var x in _entries)
                {
                    if (x == null || !x.Valid || !string.IsNullOrWhiteSpace(x.SourceId)) continue;
                    double sim = MemoryRanker.Jaccard(et, MemoryRanker.TokensOf(x));
                    if (sim > bestSim) { bestSim = sim; best = x; }
                }
                if (best != null && bestSim >= 0.85)
                {
                    best.Importance = Math.Min(10, Math.Max(best.Importance, e.Importance));
                    best.RecallCount += 1;                          // 同一桩事再被提及 = 巩固
                    if (e.WorldDate > best.WorldDate) best.WorldDate = e.WorldDate;
                    best.Keywords = MergeKeywords(best.Keywords, e.Keywords);
                    if (best.Type == MemoryType.Impression && e.Type != MemoryType.Impression) best.Type = e.Type;
                    if (e.IsCore) best.Core = true;
                    merged = true;
                    return best;
                }
            }
            if (string.IsNullOrEmpty(e.Id)) e.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
            _entries.Add(e);
            return e;
        }

        /// <summary>标记一条记忆被召回:累计频次,并在"换了一个月"时累加巩固跨度。</summary>
        public static void MarkRecalled(MemoryEntry e, long now)
        {
            if (e == null) return;
            if (now != e.LastRecalled) e.RecallMonths += 1;   // now=过月序号:跨到不同的一个月才 +1(故为"月"非"日")
            e.RecallCount += 1;
            e.LastRecalled = now;
        }

        /// <summary>
        /// 把延迟提交的召回命中映射回当前 store。工具执行期间 Save 可能已用最新磁盘
        /// 快照替换过内部对象，因此不能只修改召回阶段留下的旧对象引用。
        /// </summary>
        public int MarkRecalledMatches(IEnumerable<MemoryEntry> recalled, long now)
        {
            if (recalled == null) return 0;
            int marked = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var hit in recalled)
            {
                if (hit == null) continue;
                string key = EntryKey(hit);
                if (string.IsNullOrEmpty(key) || !seen.Add(key)) continue;
                MemoryEntry current = null;
                if (!string.IsNullOrWhiteSpace(hit.SourceId)) current = _entries.Find(x => SameSource(x, hit));
                if (current == null && !string.IsNullOrEmpty(hit.Id))
                    current = _entries.Find(x => x != null && string.Equals(x.Id, hit.Id, StringComparison.Ordinal));
                if (current == null) continue;
                MarkRecalled(current, now);
                marked++;
            }
            return marked;
        }

        static string MergeKeywords(string a, string b)
        {
            var set = new List<string>();
            foreach (var src in new[] { a, b })
                if (!string.IsNullOrWhiteSpace(src))
                    foreach (var k in src.Split(new[] { ',', '，', ' ', '、', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var t = k.Trim();
                        if (t.Length > 0 && !set.Contains(t)) set.Add(t);
                    }
            return string.Join(",", set);
        }

        /// <summary>按 id 硬删除若干条记忆(供「对话按轮删除/重试」抹掉该轮沉淀的记忆,不回退任何游戏行为)。返回删除条数。</summary>
        public int RemoveByIds(IEnumerable<string> ids)
        {
            if (ids == null) return 0;
            var set = new HashSet<string>(ids);
            if (set.Count == 0) return 0;
            int before = _entries.Count;
            foreach (var id in set) if (!string.IsNullOrEmpty(id)) _removedIds.Add(id);
            lock (GetPathLock(_path))
            {
                var tombstones = Tombstones(RemovedIdsByPath, _path);
                foreach (var id in set) if (!string.IsNullOrEmpty(id)) tombstones.Add(id);
            }
            _entries.RemoveAll(e => e != null && !string.IsNullOrEmpty(e.Id) && set.Contains(e.Id));
            return before - _entries.Count;
        }

        /// <summary>按稳定来源删除记忆（群聊删除/重试/清空时用）。</summary>
        public int RemoveBySourceIds(IEnumerable<string> sourceIds)
        {
            if (sourceIds == null) return 0;
            var set = new HashSet<string>(sourceIds.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (set.Count == 0) return 0;
            var ids = _entries.Where(e => e != null && set.Contains(e.SourceId)).Select(e => e.Id).Where(x => !string.IsNullOrEmpty(x)).ToList();
            foreach (var id in ids) _removedIds.Add(id);
            lock (GetPathLock(_path))
            {
                var tombstones = Tombstones(RemovedSourcesByPath, _path);
                var epochs = SourceEpochs(_path);
                foreach (var sourceId in set)
                {
                    tombstones.Add(sourceId);
                    long next = unchecked((epochs.TryGetValue(sourceId, out long value) ? value : 0) + 1);
                    epochs[sourceId] = next;
                    _sourceEpochSnapshot[sourceId] = next;
                }
            }
            int before = _entries.Count;
            _entries.RemoveAll(e => e != null && set.Contains(e.SourceId));
            return before - _entries.Count;
        }

        /// <summary>按相关性召回(仅对话沉淀记忆)。底座信息的统一组装见前端 MemoryContextBuilder。</summary>
        public List<MemoryEntry> Recall(string topic, long now, int limit = 5)
        {
            var hit = MemoryRanker.TopK(_entries, topic, now, limit);
            foreach (var e in hit) MarkRecalled(e, now);
            return hit;
        }

        /// <summary>淘汰:超量时保留重要性高 + 近期被召回的,控制体量。上限放宽——记忆是珍贵资产,宁多勿删。</summary>
        public int Prune(long now, int maxKeep = 2000)
        {
            var before = _entries.Count;
            if (before > maxKeep)
            {
                var oldIds = new HashSet<string>(_entries.Where(e => e != null && !string.IsNullOrEmpty(e.Id)).Select(e => e.Id));
                // 核心记忆优先进入有界保留层;其余按晋升分(重要度+频次+巩固+新近)补足容量。
                // Core is immune to time decay, not to storage capacity. A hostile or
                // corrupted provider can otherwise label every entry importance=9 and
                // defeat pruning forever. Keep a bounded deterministic core tier first.
                var core = _entries.Where(e => e != null && e.IsCore)
                    .OrderByDescending(MemoryRanker.CoreRetentionPriority)
                    .ThenByDescending(e => MemoryRanker.PromoteScore(e, now))
                    .ThenByDescending(e => e.WorldDate)
                    .ThenBy(e => e.Id ?? string.Empty, StringComparer.Ordinal)
                    .Take(Math.Max(0, maxKeep))
                    .ToList();
                var rest = _entries.Where(e => e != null && !e.IsCore)
                    .OrderByDescending(e => MemoryRanker.PromoteScore(e, now))
                    .Take(Math.Max(0, maxKeep - core.Count))
                    .ToList();
                _entries.Clear();
                _entries.AddRange(core);
                _entries.AddRange(rest);
                foreach (var e in _entries) if (e != null && !string.IsNullOrEmpty(e.Id)) oldIds.Remove(e.Id);
                foreach (var id in oldIds) _removedIds.Add(id);
                lock (GetPathLock(_path))
                {
                    var tombstones = Tombstones(RemovedIdsByPath, _path);
                    foreach (var id in oldIds) tombstones.Add(id);
                }
            }
            return before - _entries.Count;
        }

        /// <summary>
        /// 原子保存并合并其它页签的新内容。返回 false 表示当前快照已经失效、读取不可靠，
        /// 或磁盘在保存前变成不可恢复状态；调用方不得把这种情况报告成“记忆已保存”。
        /// 显式 I/O 异常仍向上抛出，便于 UI 给出真实错误。
        /// </summary>
        public bool Save()
        {
            // 读失败不能等价成空集后覆盖磁盘。用户显式清空会 DeleteFile，再 Load 得到可靠空库。
            if (!_loadReliable) return false;
            var dir = Path.GetDirectoryName(_path);
            lock (GetPathLock(_path))
            {
                // 清空/死者清理后，旧页签持有的 store 永远不能把已删记忆复活。
                if (_pathEpoch != CurrentPathEpoch(_path)) return false;
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                // 多页签/群聊可同时持有同一 NPC 的独立 store。保存前重新读盘，按 Id/SourceId
                // 合并并发新增与巩固计数，避免最后写入者覆盖先前记忆。
                bool diskReliable;
                var disk = ReadRecoverable(_path, preserveCorrupt: true, out diskReliable) ?? new List<MemoryEntry>();
                if (!diskReliable) return false;
                var removedIds = Tombstones(RemovedIdsByPath, _path);
                var removedSources = Tombstones(RemovedSourcesByPath, _path);
                disk.RemoveAll(e => IsTombstoned(e, removedIds, removedSources));
                foreach (var current in _entries)
                {
                    if (current == null) continue;
                    if (IsTombstoned(current, removedIds, removedSources)) continue;
                    // 同一来源被删除/重建后，旧 store 的快照不能再覆盖磁盘上的新版本。
                    if (!SourceEpochIsCurrent(current)) continue;
                    MemoryEntry target = null;
                    if (!string.IsNullOrWhiteSpace(current.SourceId)) target = disk.Find(x => SameSource(x, current));
                    if (target == null && !string.IsNullOrEmpty(current.Id)) target = disk.Find(x => x != null && x.Id == current.Id);
                    if (target == null) disk.Add(Clone(current));
                    else
                    {
                        _recallBaseline.TryGetValue(EntryKey(current) ?? string.Empty, out RecallSnapshot baseline);
                        MergeConcurrent(target, current, baseline);
                    }
                }
                // 由 source 去重，兼容两个并发 store 首次为同一 exchange 各生成了不同本地 Id。
                var seenSource = new Dictionary<string, MemoryEntry>(StringComparer.Ordinal);
                for (int i = disk.Count - 1; i >= 0; i--)
                {
                    var e = disk[i];
                    if (e == null || string.IsNullOrWhiteSpace(e.SourceId)) continue;
                    string sk = (e.SourceKind ?? "") + "|" + e.SourceId;
                    if (seenSource.TryGetValue(sk, out var keep)) { MergeConcurrent(keep, e); disk.RemoveAt(i); }
                    else seenSource[sk] = e;
                }

                string json = JsonConvert.SerializeObject(disk, Formatting.Indented);
                if (!DurableFileStore.TryWriteTextAtomic(_path, json, MaxFileBytes, IsValidDocument))
                    throw new IOException("NPC 记忆耐久提交或语义读回失败");
                _entries.Clear();
                _entries.AddRange(disk);
                _removedIds.Clear();
                RefreshRecallBaseline(disk);
            }

            // 同时落一份人类可读的记忆索引(仿 Claude MEMORY.md;一条记忆一行,供查看)
            try
            {
                var md = Path.Combine(dir ?? "", Path.GetFileNameWithoutExtension(_path) + ".md");
                string index = "# 记忆索引(" + _entries.Count + " 条)\n\n"
                    + string.Join("\n", MemoryIndex.Render(_entries).ConvertAll(l => "- " + l));
                DurableFileStore.TryWriteTextAtomic(md, index, 8 * 1024 * 1024, _ => true);
            }
            catch { }
            return true;
        }

        private static object GetPathLock(string path)
        {
            lock (PathLocksGate)
            {
                if (!PathLocks.TryGetValue(path, out var gate)) { gate = new object(); PathLocks[path] = gate; }
                return gate;
            }
        }

        private static HashSet<string> Tombstones(Dictionary<string, HashSet<string>> map, string path)
        {
            if (!map.TryGetValue(path, out var set)) { set = new HashSet<string>(StringComparer.Ordinal); map[path] = set; }
            return set;
        }

        private static long CurrentPathEpoch(string path)
            => PathEpochs.TryGetValue(path, out long epoch) ? epoch : 0;

        // 仅在该 path 的锁内读取/创建；Dictionary 本身不承诺并发安全。
        private static Dictionary<string, long> SourceEpochs(string path)
        {
            if (!SourceEpochsByPath.TryGetValue(path, out var epochs))
            {
                epochs = new Dictionary<string, long>(StringComparer.Ordinal);
                SourceEpochsByPath[path] = epochs;
            }
            return epochs;
        }

        private bool TryAdvanceSourceEpoch(MemoryEntry entry)
        {
            string sourceId = entry?.SourceId;
            if (string.IsNullOrWhiteSpace(sourceId)) return false;
            lock (GetPathLock(_path))
            {
                var epochs = SourceEpochs(_path);
                long current = epochs.TryGetValue(sourceId, out long value) ? value : 0;
                long captured = _sourceEpochSnapshot.TryGetValue(sourceId, out value) ? value : 0;
                if (captured != current) return false;
                long next = unchecked(current + 1);
                epochs[sourceId] = next;
                _sourceEpochSnapshot[sourceId] = next;
                // 与 epoch 提升同锁清墓碑；若删除随后发生，它的新墓碑不会被本次旧写越过。
                if (RemovedSourcesByPath.TryGetValue(_path, out var sources)) sources.Remove(sourceId);
                if (!string.IsNullOrEmpty(entry.Id) && RemovedIdsByPath.TryGetValue(_path, out var ids)) ids.Remove(entry.Id);
                return true;
            }
        }

        // Save 的 path 锁内调用。无来源的普通记忆不参与来源 CAS。
        private bool SourceEpochIsCurrent(MemoryEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.SourceId)) return true;
            var epochs = SourceEpochs(_path);
            long current = epochs.TryGetValue(entry.SourceId, out long value) ? value : 0;
            long captured = _sourceEpochSnapshot.TryGetValue(entry.SourceId, out value) ? value : 0;
            return captured == current;
        }

        private static bool IsTombstoned(MemoryEntry entry, HashSet<string> ids, HashSet<string> sources)
            => entry != null
               && ((!string.IsNullOrEmpty(entry.Id) && ids.Contains(entry.Id))
                   || (!string.IsNullOrWhiteSpace(entry.SourceId) && sources.Contains(entry.SourceId)));

        private void ClearTombstoneFor(MemoryEntry entry)
        {
            if (entry == null) return;
            lock (GetPathLock(_path))
            {
                if (!string.IsNullOrEmpty(entry.Id) && RemovedIdsByPath.TryGetValue(_path, out var ids)) ids.Remove(entry.Id);
                if (!string.IsNullOrWhiteSpace(entry.SourceId) && RemovedSourcesByPath.TryGetValue(_path, out var sources)) sources.Remove(entry.SourceId);
            }
        }

        private static List<MemoryEntry> ReadRecoverable(string path, bool preserveCorrupt)
        {
            return ReadRecoverable(path, preserveCorrupt, out _);
        }

        private static List<MemoryEntry> ReadRecoverable(string path, bool preserveCorrupt, out bool reliable)
        {
            try
            {
                bool any;
                string source;
                if (DurableFileStore.TryReadRecoverableText(path, MaxFileBytes, IsValidDocument,
                    out string json, out any, out source, preserveInvalidCandidates: preserveCorrupt))
                {
                    reliable = true;
                    return JArray.Parse(json).ToObject<List<MemoryEntry>>();
                }
                reliable = !any;
                return null;
            }
            catch { reliable = false; return null; }
        }

        private static bool IsValidDocument(string json)
        {
            if (!DurableFileStore.TryParseJsonStrict(json, 24, out JToken root)
                || !(root is JArray array) || array.Count > MaxEntries) return false;
            foreach (JToken token in array)
            {
                if (!(token is JObject item)
                    || !DurableFileStore.HasOnlyProperties(item, "Id", "Content", "Type", "Keywords",
                        "WorldDate", "Importance", "Valid", "LastRecalled", "RecallCount", "RecallMonths",
                        "Core", "SourceKind", "SourceId", "SourceLineIds")) return false;
                if (!DurableFileStore.IsBoundedString(item["Id"], 128)
                    || !DurableFileStore.IsBoundedString(item["Content"], 65536)
                    || !DurableFileStore.IsBoundedString(item["Keywords"], 8192)
                    || !DurableFileStore.IsBoundedString(item["SourceKind"], 128)
                    || !DurableFileStore.IsBoundedString(item["SourceId"], 512)) return false;
                if (!IsOptionalInteger(item["Type"], 0, 5)
                    || !IsOptionalInteger(item["WorldDate"], long.MinValue, long.MaxValue)
                    || !IsOptionalInteger(item["Importance"], 0, 10)
                    || !IsOptionalBoolean(item["Valid"])
                    || !IsOptionalInteger(item["LastRecalled"], long.MinValue, long.MaxValue)
                    || !IsOptionalInteger(item["RecallCount"], 0, int.MaxValue)
                    || !IsOptionalInteger(item["RecallMonths"], 0, int.MaxValue)
                    || !IsOptionalBoolean(item["Core"])) return false;
                JToken lineIds = item["SourceLineIds"];
                if (lineIds != null && lineIds.Type != JTokenType.Null)
                {
                    if (!(lineIds is JArray lines) || lines.Count > 4096) return false;
                    foreach (JToken line in lines)
                        if (!DurableFileStore.IsBoundedString(line, 256, false)) return false;
                }
            }
            return true;
        }

        private static bool IsOptionalBoolean(JToken token)
            => token == null || token.Type == JTokenType.Boolean;

        private static bool IsOptionalInteger(JToken token, long min, long max)
        {
            if (token == null) return true;
            if (token.Type != JTokenType.Integer) return false;
            try { long value = token.Value<long>(); return value >= min && value <= max; }
            catch { return false; }
        }

        private static void ValidateIdentity(string value, string parameter, bool allowAssistant = false)
        {
            if (allowAssistant && string.Equals(value, "assistant", StringComparison.Ordinal)) return;
            if (string.IsNullOrEmpty(value) || value.Length > 20)
                throw new ArgumentException("持久化身份必须是非负十进制数", parameter);
            foreach (char c in value)
                if (c < '0' || c > '9') throw new ArgumentException("持久化身份必须是非负十进制数", parameter);
        }

        private static bool SameSource(MemoryEntry a, MemoryEntry b)
            => a != null && b != null && !string.IsNullOrWhiteSpace(a.SourceId)
               && string.Equals(a.SourceId, b.SourceId, StringComparison.Ordinal)
               && string.Equals(a.SourceKind ?? "", b.SourceKind ?? "", StringComparison.Ordinal);

        private static void CopySourceEntry(MemoryEntry from, MemoryEntry to)
        {
            if (from == null || to == null) return;
            to.Content = from.Content; to.Type = from.Type; to.Keywords = from.Keywords;
            to.WorldDate = from.WorldDate; to.Importance = from.Importance; to.Valid = from.Valid;
            to.Core = from.Core; to.SourceKind = from.SourceKind; to.SourceId = from.SourceId;
            to.SourceLineIds = from.SourceLineIds == null ? null : new List<string>(from.SourceLineIds);
            if (string.IsNullOrEmpty(to.Id)) to.Id = string.IsNullOrEmpty(from.Id) ? Guid.NewGuid().ToString("N").Substring(0, 8) : from.Id;
        }

        private static MemoryEntry Clone(MemoryEntry e)
            => JsonConvert.DeserializeObject<MemoryEntry>(JsonConvert.SerializeObject(e));

        private static string EntryKey(MemoryEntry entry)
        {
            if (entry == null) return null;
            if (!string.IsNullOrWhiteSpace(entry.SourceId))
                return "s:" + (entry.SourceKind ?? string.Empty) + "\n" + entry.SourceId;
            return string.IsNullOrEmpty(entry.Id) ? null : ("i:" + entry.Id);
        }

        private static Dictionary<string, RecallSnapshot> CaptureRecallBaseline(IEnumerable<MemoryEntry> entries)
        {
            var result = new Dictionary<string, RecallSnapshot>(StringComparer.Ordinal);
            if (entries == null) return result;
            foreach (var entry in entries)
            {
                string key = EntryKey(entry);
                if (string.IsNullOrEmpty(key)) continue;
                result[key] = new RecallSnapshot
                {
                    Count = Math.Max(0, entry.RecallCount),
                    Months = Math.Max(0, entry.RecallMonths),
                    Last = entry.LastRecalled,
                };
            }
            return result;
        }

        private void RefreshRecallBaseline(IEnumerable<MemoryEntry> entries)
        {
            _recallBaseline.Clear();
            foreach (var pair in CaptureRecallBaseline(entries)) _recallBaseline[pair.Key] = pair.Value;
        }

        private static int SaturatingAdd(int value, int delta)
        {
            if (delta <= 0) return Math.Max(0, value);
            return value > int.MaxValue - delta ? int.MaxValue : value + delta;
        }

        private static void MergeConcurrent(MemoryEntry target, MemoryEntry current, RecallSnapshot baseline = null)
        {
            if (target == null || current == null) return;
            if (SameSource(target, current))
            {
                string keep = string.IsNullOrEmpty(target.Id) ? current.Id : target.Id;
                CopySourceEntry(current, target);
                target.Id = keep;
            }
            else if (current.WorldDate >= target.WorldDate && !string.IsNullOrWhiteSpace(current.Content))
            {
                target.Content = current.Content;
                target.Type = current.Type;
                target.WorldDate = current.WorldDate;
                target.Valid = current.Valid;
            }
            target.Importance = Math.Max(target.Importance, current.Importance);
            if (baseline == null)
            {
                // 磁盘内部重复来源去重或新条目首次会合：没有独立加载基线，保守取最大值。
                target.RecallCount = Math.Max(target.RecallCount, current.RecallCount);
                target.RecallMonths = Math.Max(target.RecallMonths, current.RecallMonths);
            }
            else
            {
                int countDelta = Math.Max(0, current.RecallCount - baseline.Count);
                target.RecallCount = SaturatingAdd(target.RecallCount, countDelta);

                int monthDelta = Math.Max(0, current.RecallMonths - baseline.Months);
                // 两个页签在同一游戏月各自召回时，次数都应累计，但“跨月数”只计该月一次。
                if (monthDelta > 0 && current.LastRecalled == target.LastRecalled)
                    monthDelta--;
                target.RecallMonths = SaturatingAdd(target.RecallMonths, monthDelta);
            }
            target.LastRecalled = Math.Max(target.LastRecalled, current.LastRecalled);
            target.Keywords = MergeKeywords(target.Keywords, current.Keywords);
            target.Core = target.Core || current.Core;
            if (string.IsNullOrEmpty(target.Id)) target.Id = current.Id;
        }
    }
}
