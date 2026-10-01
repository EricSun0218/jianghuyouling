using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using JianghuYouling.Core.Persistence;
using Newtonsoft.Json;

namespace JianghuYouling
{
    /// <summary>
    /// Per-save visibility state for the conversation sidebar. Hiding only records a stable
    /// navigation identity here; chat transcripts, group membership and memories are untouched.
    /// </summary>
    public static class ConversationNavigationStore
    {
        private const int CurrentVersion = 1;
        private const int MaxHiddenConversationsPerShard = 2048;
        private const int MaxShardCount = 4096;
        private const int MaxIdentityLength = 256;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Snapshot> ReliableSnapshots =
            new Dictionary<string, Snapshot>(StringComparer.Ordinal);

        private sealed class State
        {
            public int Version = CurrentVersion;
            public uint WorldId;
            public int TaiwuId;
            public List<string> Hidden = new List<string>();
        }

        public sealed class Snapshot
        {
            public List<string> Hidden = new List<string>();
            public bool LoadReliable = true;
        }

        private static string PathFor(int taiwuId, int shardIndex)
            => Path.Combine(JianghuYoulingPaths.Intents, shardIndex <= 0
                ? "conversation_navigation_" + taiwuId + ".json"
                : "conversation_navigation_" + taiwuId + "_" + shardIndex.ToString("D4") + ".json");

        public static Snapshot Load(int taiwuId)
        {
            lock (Gate)
            {
                string scope = ScopeKey(taiwuId);
                if (ReliableSnapshots.TryGetValue(scope, out Snapshot cached))
                    return CloneSnapshot(cached);

                var all = new SortedSet<string>(StringComparer.Ordinal);
                bool reliable = true;
                foreach (int shardIndex in DiscoverShardIndexes(taiwuId))
                {
                    if (!TryLoadState(taiwuId, shardIndex, out State state))
                    {
                        reliable = false;
                        continue;
                    }
                    foreach (string identity in state.Hidden)
                        all.Add(identity);
                }
                var snapshot = new Snapshot
                {
                    Hidden = reliable ? new List<string>(all) : new List<string>(),
                    LoadReliable = reliable,
                };
                // UI refreshes are frequent and run on Unity's main thread. Keep the last complete,
                // world-scoped snapshot in memory; a later partial/corrupt disk scan must never
                // replace it or reveal only the conversations from readable shards.
                if (reliable) ReliableSnapshots[scope] = CloneSnapshot(snapshot);
                return snapshot;
            }
        }

        internal static void ResetCacheForWorldExit()
        {
            lock (Gate) ReliableSnapshots.Clear();
        }

        public static bool Hide(int taiwuId, string identity)
        {
            if (taiwuId <= 0 || !ValidIdentity(identity)) return false;
            lock (Gate)
            {
                List<int> shards = DiscoverShardIndexes(taiwuId);
                var loaded = new List<KeyValuePair<int, State>>();
                foreach (int shardIndex in shards)
                {
                    if (!TryLoadState(taiwuId, shardIndex, out State state)) return false;
                    loaded.Add(new KeyValuePair<int, State>(shardIndex, state));
                }
                foreach (var pair in loaded)
                    if (pair.Value.Hidden.Contains(identity))
                    {
                        CacheLoaded(taiwuId, loaded);
                        return true;
                    }

                // 每片同时受条目数和 durable 文件字节上限约束；满片后只追加新片，
                // 不再对全局集合排序截断，因此第 2049 个及之后的隐藏标记也不会静默丢失。
                foreach (var pair in loaded)
                {
                    State state = pair.Value;
                    if (state.Hidden.Count >= MaxHiddenConversationsPerShard) continue;
                    state.Hidden.Add(identity);
                    Normalize(state, taiwuId);
                    if (!state.Hidden.Contains(identity) || !DocumentFits(state))
                    {
                        state.Hidden.Remove(identity);
                        continue;
                    }
                    if (!SaveState(taiwuId, pair.Key, state)) return false;
                    CacheLoaded(taiwuId, loaded);
                    return true;
                }

                int next = shards.Count == 0 ? 0 : shards[shards.Count - 1] + 1;
                if (next < 0 || next >= MaxShardCount) return false;
                State fresh = NewState(taiwuId);
                fresh.Hidden.Add(identity);
                Normalize(fresh, taiwuId);
                if (!fresh.Hidden.Contains(identity) || !DocumentFits(fresh)
                    || !SaveState(taiwuId, next, fresh)) return false;
                loaded.Add(new KeyValuePair<int, State>(next, fresh));
                CacheLoaded(taiwuId, loaded);
                return true;
            }
        }

        public static bool Restore(int taiwuId, string identity)
        {
            if (taiwuId <= 0 || !ValidIdentity(identity)) return false;
            lock (Gate)
            {
                var loaded = new List<KeyValuePair<int, State>>();
                foreach (int shardIndex in DiscoverShardIndexes(taiwuId))
                {
                    if (!TryLoadState(taiwuId, shardIndex, out State state)) return false;
                    loaded.Add(new KeyValuePair<int, State>(shardIndex, state));
                }
                var matches = new List<KeyValuePair<int, State>>();
                foreach (var pair in loaded)
                    if (pair.Value.Hidden.Contains(identity)) matches.Add(pair);
                if (matches.Count == 0) return true;
                // 正常写路径保证一个 identity 只存在于一片。若人工编辑造成重复，先拒绝，
                // 避免多文件提交只成功一半而留下更难恢复的部分状态。
                if (matches.Count != 1) return false;
                State target = matches[0].Value;
                target.Hidden.Remove(identity);
                Normalize(target, taiwuId);
                if (!SaveState(taiwuId, matches[0].Key, target)) return false;
                CacheLoaded(taiwuId, loaded);
                return true;
            }
        }

        public static bool ReplaceSingleIdentity(int taiwuId, int oldNpcId, int newNpcId)
        {
            if (taiwuId <= 0 || oldNpcId < 0 || newNpcId < 0 || oldNpcId == newNpcId) return true;
            string oldIdentity = "single:" + taiwuId + ":" + oldNpcId;
            string newIdentity = "single:" + taiwuId + ":" + newNpcId;
            Snapshot snapshot = Load(taiwuId);
            if (!snapshot.LoadReliable || !snapshot.Hidden.Contains(oldIdentity)) return snapshot.LoadReliable;
            // 先写新身份，再移除旧身份；中断只会暂时同时隐藏，不会意外暴露会话。
            return Hide(taiwuId, newIdentity) && Restore(taiwuId, oldIdentity);
        }

        private static string ScopeKey(int taiwuId)
            => JianghuYoulingPaths.CurrentWorldId + ":" + taiwuId;

        private static Snapshot CloneSnapshot(Snapshot source)
            => new Snapshot
            {
                Hidden = source?.Hidden == null
                    ? new List<string>() : new List<string>(source.Hidden),
                LoadReliable = source != null && source.LoadReliable,
            };

        private static void CacheLoaded(int taiwuId,
            IEnumerable<KeyValuePair<int, State>> loaded)
        {
            var all = new SortedSet<string>(StringComparer.Ordinal);
            if (loaded != null)
                foreach (var pair in loaded)
                    if (pair.Value?.Hidden != null)
                        foreach (string identity in pair.Value.Hidden)
                            if (ValidIdentity(identity)) all.Add(identity);
            ReliableSnapshots[ScopeKey(taiwuId)] = new Snapshot
            {
                Hidden = new List<string>(all),
                LoadReliable = true,
            };
        }

        private static bool TryLoadState(int taiwuId, int shardIndex, out State state)
        {
            state = NewState(taiwuId);
            if (taiwuId <= 0) return false;
            try
            {
                string path = PathFor(taiwuId, shardIndex);
                uint currentWorldId = JianghuYoulingPaths.CurrentWorldId;
                bool loaded = DurableFileStore.TryReadRecoverableText(path,
                    DurableSettingsStore.Text,
                    value => IsValidDocumentForScope(value, taiwuId, currentWorldId),
                    out string json, out bool anyCandidate, out _,
                    preserveInvalidCandidates: false);
                if (!loaded) return !anyCandidate;
                state = JsonConvert.DeserializeObject<State>(json);
                if (state == null) return false;
                Normalize(state, taiwuId);
                return true;
            }
            catch { state = NewState(taiwuId); return false; }
        }

        private static State NewState(int taiwuId)
            => new State
            {
                WorldId = JianghuYoulingPaths.CurrentWorldId,
                TaiwuId = taiwuId,
            };

        private static void Normalize(State state, int taiwuId)
        {
            state.Version = CurrentVersion;
            state.WorldId = JianghuYoulingPaths.CurrentWorldId;
            state.TaiwuId = taiwuId;
            var unique = new SortedSet<string>(StringComparer.Ordinal);
            if (state.Hidden != null)
                foreach (string identity in state.Hidden)
                {
                    if (!ValidIdentity(identity)) continue;
                    unique.Add(identity);
                    if (unique.Count >= MaxHiddenConversationsPerShard) break;
                }
            state.Hidden = new List<string>(unique);
        }

        private static bool SaveState(int taiwuId, int shardIndex, State state)
        {
            try
            {
                Normalize(state, taiwuId);
                string json = JsonConvert.SerializeObject(state, Formatting.Indented);
                uint currentWorldId = JianghuYoulingPaths.CurrentWorldId;
                return DurableFileStore.TryWriteTextAtomic(PathFor(taiwuId, shardIndex), json,
                    DurableSettingsStore.Text,
                    value => IsValidDocumentForScope(value, taiwuId, currentWorldId));
            }
            catch { return false; }
        }

        private static bool DocumentFits(State state)
        {
            try
            {
                string json = JsonConvert.SerializeObject(state, Formatting.Indented);
                return Encoding.UTF8.GetByteCount(json) <= DurableSettingsStore.Text
                    && IsValidDocumentForScope(json, state.TaiwuId, state.WorldId);
            }
            catch { return false; }
        }

        private static bool IsValidDocumentForScope(string value, int expectedTaiwuId,
            uint expectedWorldId)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > DurableSettingsStore.Text)
                return false;
            try
            {
                State state = JsonConvert.DeserializeObject<State>(value);
                if (state == null || state.Version != CurrentVersion
                    || state.TaiwuId != expectedTaiwuId
                    || (state.WorldId != 0 && state.WorldId != expectedWorldId)
                    || state.Hidden == null
                    || state.Hidden.Count > MaxHiddenConversationsPerShard)
                    return false;
                foreach (string identity in state.Hidden)
                    if (!ValidIdentity(identity)) return false;
                return true;
            }
            catch { return false; }
        }

        private static bool ValidIdentity(string identity)
        {
            if (string.IsNullOrWhiteSpace(identity) || identity.Length > MaxIdentityLength)
                return false;
            for (int i = 0; i < identity.Length; i++)
                if (char.IsControl(identity[i])) return false;
            return identity.StartsWith("single:", StringComparison.Ordinal)
                || identity.StartsWith("group:", StringComparison.Ordinal)
                || identity.StartsWith("assistant:", StringComparison.Ordinal);
        }

        private static List<int> DiscoverShardIndexes(int taiwuId)
        {
            var indexes = new SortedSet<int> { 0 };
            if (taiwuId <= 0) return new List<int>(indexes);
            try
            {
                string dir = JianghuYoulingPaths.Intents;
                if (!Directory.Exists(dir)) return new List<int>(indexes);
                string prefix = "conversation_navigation_" + taiwuId + "_";
                foreach (string path in Directory.GetFiles(dir, prefix + "*.json*"))
                {
                    string fileName = Path.GetFileName(path);
                    if (fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
                        || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                        fileName = fileName.Substring(0, fileName.Length - 4);
                    if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                    string name = Path.GetFileNameWithoutExtension(fileName);
                    if (string.IsNullOrEmpty(name) || !name.StartsWith(prefix, StringComparison.Ordinal))
                        continue;
                    string suffix = name.Substring(prefix.Length);
                    if (int.TryParse(suffix, out int shardIndex)
                        && shardIndex > 0 && shardIndex < MaxShardCount)
                        indexes.Add(shardIndex);
                }
            }
            catch { }
            return new List<int>(indexes);
        }
    }
}
