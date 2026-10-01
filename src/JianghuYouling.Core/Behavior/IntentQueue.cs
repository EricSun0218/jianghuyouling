using System;
using System.Collections.Generic;
using System.IO;
using JianghuYouling.Core.Persistence;
using JianghuYouling.Core.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Behavior
{
    /// <summary>
    /// 行为意图持久队列。每个实例保存“加载基线”，Save 在 IO 锁内把本实例相对基线的增删改
    /// 三方合并到最新磁盘版本，避免两个聊天/清理实例互相覆盖。
    /// </summary>
    public sealed class IntentQueue
    {
        private const int MaxFileBytes = 2 * 1024 * 1024;
        private const int MaxItems = 4096;
        private static readonly object _io = new object();
        // ACK 墓碑必须带绝对 path；不同世界/太吾文件即使旧 8hex id 相同也不能串删。
        private static readonly HashSet<string> _ackedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly string _path;
        private readonly List<BehaviorIntent> _items;
        private List<BehaviorIntent> _baseline;
        private readonly HashSet<int> _removedNpcIds = new HashSet<int>();
        private bool _clearAllRequested;
        public bool LoadReliable { get; private set; }

        private IntentQueue(string path, List<BehaviorIntent> items, bool reliable)
        {
            _path = path;
            _items = items ?? new List<BehaviorIntent>();
            _baseline = CloneList(_items);
            LoadReliable = reliable;
        }

        public static IntentQueue Load(string dir, string taiwuId)
        {
            if (!IsNumericIdentity(taiwuId))
                throw new ArgumentException("taiwuId 必须是非负十进制身份", nameof(taiwuId));
            string path = Path.Combine(dir, "IntentQueue_" + taiwuId + ".json");
            lock (_io)
            {
                bool anyCandidate;
                string source;
                List<BehaviorIntent> list = ReadCommittedOrRecovery(path, out anyCandidate, out source);
                bool reliable = list != null || !anyCandidate;
                list = list ?? new List<BehaviorIntent>();
                bool repaired = EnsureStableIds(list, path);
                if (reliable && (source != null && source != path || repaired))
                {
                    try { WriteAtomic(path, list); }
                    catch { reliable = false; }
                }
                return new IntentQueue(path, list, reliable);
            }
        }

        public IReadOnlyList<BehaviorIntent> All => _items;
        public int Count => _items.Count;
        public List<BehaviorIntent> Snapshot() => CloneList(_items);

        public static string OperationIdFor(BehaviorIntent intent)
        {
            if (intent == null) return null;
            string key = "intent|" + (intent.Id ?? "") + "|" + intent.TaiwuId + "|" + intent.NpcId
                + "|" + intent.WorldDate + "|" + intent.MoralityDelta + "|" + (intent.Kind ?? "")
                + "|" + (intent.Target ?? "") + "|" + (intent.Note ?? "");
            return OperationId.FromStableKey(key);
        }

        public void Enqueue(BehaviorIntent intent)
        {
            if (intent == null) return;
            if (string.IsNullOrWhiteSpace(intent.Id)) intent.Id = Guid.NewGuid().ToString("N");
            _items.Add(intent);
        }

        public int RemoveByNpc(int npcId)
        {
            if (npcId <= 0) return 0;
            _removedNpcIds.Add(npcId);
            return _items.RemoveAll(x => x != null && x.NpcId == npcId);
        }

        [Obsolete("Use Snapshot + AcknowledgePersisted after a terminal tool receipt.")]
        public List<BehaviorIntent> TakeAll()
        {
            var copy = CloneList(_items);
            _items.Clear();
            _clearAllRequested = true;
            return copy;
        }

        public bool Save()
        {
            if (!LoadReliable) return false;
            lock (_io)
            {
                try
                {
                    bool anyCandidate; string source;
                    List<BehaviorIntent> latest = ReadCommittedOrRecovery(_path, out anyCandidate, out source);
                    if (latest == null && anyCandidate) return false;
                    latest = latest ?? new List<BehaviorIntent>();
                    EnsureStableIds(_items, _path);
                    EnsureStableIds(latest, _path);

                    var baselineById = IndexById(_baseline);
                    var currentById = IndexById(_items);

                    if (_clearAllRequested) latest.Clear();
                    else
                    {
                        if (_removedNpcIds.Count > 0)
                            latest.RemoveAll(x => x != null && _removedNpcIds.Contains(x.NpcId));
                        // 本实例从基线显式删掉的 id，应删最新磁盘同一 id；磁盘后来新增的无关项保留。
                        foreach (var pair in baselineById)
                            if (!currentById.ContainsKey(pair.Key)) RemoveById(latest, pair.Key);

                        // 只把本实例相对基线的新增/修改覆盖到 latest；未改项采用最新磁盘版本。
                        foreach (var pair in currentById)
                        {
                            BehaviorIntent before;
                            bool changed = !baselineById.TryGetValue(pair.Key, out before)
                                || !string.Equals(Fingerprint(before), Fingerprint(pair.Value), StringComparison.Ordinal);
                            if (changed) Upsert(latest, pair.Value);
                        }
                    }
                    latest.RemoveAll(x => x != null && !string.IsNullOrWhiteSpace(x.Id)
                        && _ackedKeys.Contains(AckKey(_path, x.Id)));
                    WriteAtomic(_path, latest);
                    ResetTo(latest);
                    return true;
                }
                catch { return false; }
            }
        }

        /// <summary>终态 ACK；锁内重读最新 main/fallback，仅删一个 id 并提交，重复 ACK 幂等成功。</summary>
        public bool AcknowledgePersisted(string intentId)
        {
            if (string.IsNullOrWhiteSpace(intentId) || !LoadReliable) return false;
            lock (_io)
            {
                try
                {
                    bool anyCandidate; string source;
                    List<BehaviorIntent> latest = ReadCommittedOrRecovery(_path, out anyCandidate, out source);
                    if (latest == null && anyCandidate) return false;
                    latest = latest ?? CloneList(_items);
                    RemoveById(latest, intentId);
                    WriteAtomic(_path, latest);
                    _ackedKeys.Add(AckKey(_path, intentId));
                    ResetTo(latest);
                    return true;
                }
                catch { return false; }
            }
        }

        private void ResetTo(List<BehaviorIntent> latest)
        {
            _items.Clear();
            _items.AddRange(CloneList(latest));
            _baseline = CloneList(latest);
            _removedNpcIds.Clear();
            _clearAllRequested = false;
        }

        private static List<BehaviorIntent> ReadCommittedOrRecovery(string path, out bool anyCandidate, out string source)
        {
            source = null;
            try
            {
                if (!DurableFileStore.TryReadRecoverableText(path, MaxFileBytes, IsValidDocument,
                    out string json, out anyCandidate, out source)) return null;
                source = path;
                return JArray.Parse(json).ToObject<List<BehaviorIntent>>();
            }
            catch
            {
                anyCandidate = File.Exists(path) || File.Exists(path + ".tmp") || File.Exists(path + ".bak");
                source = null;
                return null;
            }
        }

        private static bool EnsureStableIds(List<BehaviorIntent> items, string path)
        {
            bool changed = false;
            if (items == null) return false;
            var used = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null) continue;
                if (string.IsNullOrWhiteSpace(item.Id))
                {
                    item.Id = OperationId.FromStableKey("legacy-intent|" + Path.GetFullPath(path) + "|" + i + "|" + Fingerprint(item));
                    changed = true;
                }
                // 极端旧数据重复 id 也要确定性拆开，否则 ACK 会误删两项。
                if (!used.Add(item.Id))
                {
                    item.Id = OperationId.FromStableKey("duplicate-intent|" + Path.GetFullPath(path) + "|" + i + "|" + Fingerprint(item));
                    used.Add(item.Id);
                    changed = true;
                }
            }
            return changed;
        }

        private static Dictionary<string, BehaviorIntent> IndexById(List<BehaviorIntent> items)
        {
            var result = new Dictionary<string, BehaviorIntent>(StringComparer.Ordinal);
            if (items != null)
                foreach (var item in items)
                    if (item != null && !string.IsNullOrWhiteSpace(item.Id)) result[item.Id] = item;
            return result;
        }

        private static void Upsert(List<BehaviorIntent> items, BehaviorIntent value)
        {
            if (items == null || value == null) return;
            for (int i = 0; i < items.Count; i++)
                if (items[i] != null && string.Equals(items[i].Id, value.Id, StringComparison.Ordinal))
                { items[i] = Clone(value); return; }
            items.Add(Clone(value));
        }

        private static void RemoveById(List<BehaviorIntent> items, string id)
            => items?.RemoveAll(x => x != null && string.Equals(x.Id, id, StringComparison.Ordinal));

        private static string AckKey(string path, string id)
            => Path.GetFullPath(path ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "|" + (id ?? "");

        private static string Fingerprint(BehaviorIntent x)
        {
            if (x == null) return "null";
            return (x.Id ?? "") + "|" + x.NpcId + "|" + x.TaiwuId + "|" + (x.Kind ?? "") + "|"
                + (x.Target ?? "") + "|" + (x.Note ?? "") + "|" + x.MoralityDelta + "|" + x.WorldDate;
        }

        private static BehaviorIntent Clone(BehaviorIntent x)
        {
            if (x == null) return null;
            return new BehaviorIntent
            {
                Id = x.Id, NpcId = x.NpcId, TaiwuId = x.TaiwuId, Kind = x.Kind, Target = x.Target,
                Note = x.Note, MoralityDelta = x.MoralityDelta, WorldDate = x.WorldDate,
            };
        }

        private static List<BehaviorIntent> CloneList(List<BehaviorIntent> items)
        {
            var result = new List<BehaviorIntent>();
            if (items != null) foreach (var item in items) if (item != null) result.Add(Clone(item));
            return result;
        }

        private static void WriteAtomic(string path, List<BehaviorIntent> items)
        {
            string json = JsonConvert.SerializeObject(items ?? new List<BehaviorIntent>(), Formatting.Indented);
            if (!DurableFileStore.TryWriteTextAtomic(path, json, MaxFileBytes, IsValidDocument))
                throw new IOException("意图队列耐久提交或语义读回失败");
        }

        private static bool IsValidDocument(string json)
        {
            if (!DurableFileStore.TryParseJsonStrict(json, 16, out JToken root)
                || !(root is JArray array) || array.Count > MaxItems) return false;
            foreach (JToken token in array)
            {
                if (!(token is JObject item)
                    || !DurableFileStore.HasOnlyProperties(item, "Id", "NpcId", "TaiwuId", "Kind",
                        "Target", "Note", "MoralityDelta", "WorldDate")) return false;
                if (item["NpcId"]?.Type != JTokenType.Integer
                    || item["TaiwuId"]?.Type != JTokenType.Integer
                    || item["MoralityDelta"]?.Type != JTokenType.Integer
                    || item["WorldDate"]?.Type != JTokenType.Integer) return false;
                if (!DurableFileStore.IsBoundedString(item["Id"], 128)
                    || !DurableFileStore.IsBoundedString(item["Kind"], 256)
                    || !DurableFileStore.IsBoundedString(item["Target"], 1024)
                    || !DurableFileStore.IsBoundedString(item["Note"], 4096)) return false;
            }
            return true;
        }

        private static bool IsNumericIdentity(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 20) return false;
            foreach (char c in value) if (c < '0' || c > '9') return false;
            return true;
        }
    }
}
