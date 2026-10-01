using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JianghuYouling.Core.Persistence;
using Newtonsoft.Json;

namespace JianghuYouling
{
    /// <summary>
    /// Derived, world-scoped navigation metadata for permanent direct/group conversations.
    /// Transcripts remain authoritative.  The index is bucketed so an ordinary message rewrites
    /// only a small shard; a durable dirty marker forces a one-time transcript rebuild after any
    /// interrupted transcript/index pair.
    /// </summary>
    internal static class ConversationSessionIndexStore
    {
        private const int CurrentVersion = 2;
        private const int BucketCount = 32;
        private const int MaxShardBytes = 8 * 1024 * 1024;
        private const int MaxManifestBytes = 16 * 1024;
        private const int MaxEntriesPerShard = 10000;
        private const int MaxNameLength = 256;
        private const int MaxGroupIdLength = 128;
        private const int MaxGroupMembers = 32;
        private const string SingleKind = "single";
        private const string GroupKind = "group";
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, MutationState> Mutations =
            new Dictionary<string, MutationState>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, long> MutationEpochs =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, CacheState> Caches =
            new Dictionary<string, CacheState>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, object> PublicationGates =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private static readonly object BackgroundWriteGate = new object();
        private static Task BackgroundWriteTail = Task.FromResult(0);
        private static long _backgroundQueueGeneration;
        private static long _generationSeed;

        internal sealed class SingleEntry
        {
            public int NpcId;
            public string Name;
            public int LastDate;
            public int LastPlayerDate;
            public int Turns;
            public int PlayerConversations;
            public int RecentPlayerConversations;
            public int RecentActiveMonths;
            public long LastActivityUtcTicks;
        }

        internal sealed class MemberEntry
        {
            public int Id;
            public string Name;
        }

        internal sealed class GroupEntry
        {
            public string GroupId;
            public List<MemberEntry> Members = new List<MemberEntry>();
            public bool HasActiveLines;
            public int ArchivedSegmentCount;
            public long LastActivityUtcTicks;
        }

        private sealed class Manifest
        {
            public int Version = CurrentVersion;
            public uint WorldId;
            public int TaiwuId;
            public string Kind;
            public long Generation;
            public int Buckets = BucketCount;
            public int EntryCount;
        }

        private sealed class Shard
        {
            public int Version = CurrentVersion;
            public uint WorldId;
            public int TaiwuId;
            public string Kind;
            public long Generation;
            public int Bucket;
            public List<SingleEntry> Singles = new List<SingleEntry>();
            public List<GroupEntry> Groups = new List<GroupEntry>();
        }

        private sealed class CacheState
        {
            public Manifest Manifest;
            public List<SingleEntry> Singles;
            public List<GroupEntry> Groups;
        }

        internal sealed class MutationState
        {
            public int Active;
            public bool Failed;
            public bool MarkerCreated;
            public string MarkerPath;
        }

        internal sealed class Mutation : IDisposable
        {
            private readonly string _key;
            private readonly MutationState _state;
            private bool _committed;
            private bool _disposed;

            internal Mutation(string key, MutationState state)
            {
                _key = key;
                _state = state;
            }

            internal void Commit() => _committed = true;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                lock (Gate)
                {
                    if (!_committed) _state.Failed = true;
                    _state.Active = Math.Max(0, _state.Active - 1);
                    if (_state.Active > 0) return;
                    if (!_state.Failed && _state.MarkerCreated)
                    {
                        try
                        {
                            if (File.Exists(_state.MarkerPath)) File.Delete(_state.MarkerPath);
                            if (File.Exists(_state.MarkerPath)) _state.Failed = true;
                        }
                        catch { _state.Failed = true; }
                    }
                    if (_state.Failed) Caches.Remove(_key);
                    Mutations.Remove(_key);
                }
            }
        }

        internal static void ResetForWorldExit()
        {
            lock (BackgroundWriteGate)
                Interlocked.Increment(ref _backgroundQueueGeneration);
            lock (Gate)
            {
                Caches.Clear();
                // Active transcript writers own their mutation instances and will dispose them.
                // Do not delete their dirty markers during a world fence.
            }
        }

        internal static Mutation BeginSingleMutation(int taiwuId)
            => BeginMutation(SingleKind, taiwuId);

        internal static Mutation BeginGroupMutation(int taiwuId)
            => BeginMutation(GroupKind, taiwuId);

        private static Mutation BeginMutation(string kind, int taiwuId)
        {
            string key = CacheKey(kind, taiwuId);
            lock (Gate)
            {
                MutationEpochs.TryGetValue(key, out long epoch);
                MutationEpochs[key] = unchecked(epoch + 1);
                if (!Mutations.TryGetValue(key, out MutationState state))
                {
                    state = new MutationState
                    {
                        MarkerPath = DirtyPath(kind, taiwuId),
                    };
                    bool markerExisted = File.Exists(state.MarkerPath);
                    state.MarkerCreated = EnsureDirtyMarker(state.MarkerPath);
                    // A pre-existing marker belongs to an earlier incomplete pair.  A later
                    // successful incremental update cannot prove the rest of that index current.
                    state.Failed = !state.MarkerCreated || File.Exists(state.MarkerPath + ".preexisting");
                    Mutations[key] = state;
                    // A marker from an earlier pair invalidates the process cache.  For the
                    // ordinary clean path keep the already loaded 32-bucket snapshot so each
                    // message rewrites only its one bucket instead of rereading every shard.
                    if (markerExisted) Caches.Remove(key);
                }
                state.Active++;
                return new Mutation(key, state);
            }
        }

        private static bool EnsureDirtyMarker(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                bool existed = File.Exists(path);
                if (existed)
                {
                    // Side marker only lives for this process' mutation state and records that the
                    // dirty file predates the current pair; it is removed when a full rebuild lands.
                    File.WriteAllText(path + ".preexisting", "1", new UTF8Encoding(false));
                    return true;
                }
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                    FileShare.Read, 64, FileOptions.WriteThrough))
                {
                    stream.WriteByte(1);
                    stream.Flush(true);
                }
                return true;
            }
            catch { return false; }
        }

        internal static bool TryLoadSingles(int taiwuId, out List<SingleEntry> entries)
        {
            entries = new List<SingleEntry>();
            if (!TryLoadCache(SingleKind, taiwuId, out CacheState cache)) return false;
            foreach (SingleEntry entry in cache.Singles) entries.Add(Clone(entry));
            return true;
        }

        internal static bool TryLoadGroups(int taiwuId, out List<GroupEntry> entries)
        {
            entries = new List<GroupEntry>();
            if (!TryLoadCache(GroupKind, taiwuId, out CacheState cache)) return false;
            foreach (GroupEntry entry in cache.Groups) entries.Add(Clone(entry));
            return true;
        }

        internal static bool IsSingleCacheReady(int taiwuId)
            => IsCacheReady(SingleKind, taiwuId);

        internal static bool IsGroupCacheReady(int taiwuId)
            => IsCacheReady(GroupKind, taiwuId);

        private static bool IsCacheReady(string kind, int taiwuId)
        {
            if (taiwuId <= 0 || JianghuYoulingPaths.CurrentWorldId == 0) return false;
            string key = CacheKey(kind, taiwuId);
            lock (Gate)
                return Caches.ContainsKey(key) && !Mutations.ContainsKey(key);
        }

        internal static bool ReplaceSingles(int taiwuId, IEnumerable<SingleEntry> source)
            => ReplaceSingles(taiwuId, source, null);

        internal static long CaptureSingleRebuildEpoch(int taiwuId)
            => CaptureRebuildEpoch(SingleKind, taiwuId);

        internal static bool ReplaceSinglesIfRebuildUnchanged(int taiwuId,
            IEnumerable<SingleEntry> source, long expectedEpoch, uint expectedWorldId,
            string expectedDirectory)
            => expectedEpoch >= 0 && expectedWorldId > 0
                && ReplaceSingles(taiwuId, source, expectedEpoch,
                    expectedWorldId, expectedDirectory);

        private static bool ReplaceSingles(int taiwuId, IEnumerable<SingleEntry> source,
            long? expectedEpoch, uint? expectedWorldId = null,
            string expectedDirectory = null)
        {
            var entries = new List<SingleEntry>();
            if (source != null)
                foreach (SingleEntry entry in source)
                    if (Valid(entry)) entries.Add(Clone(entry));
            return ReplaceAll(SingleKind, taiwuId, entries, null, expectedEpoch,
                expectedWorldId, expectedDirectory);
        }

        internal static bool ReplaceGroups(int taiwuId, IEnumerable<GroupEntry> source)
            => ReplaceGroups(taiwuId, source, null);

        internal static long CaptureGroupRebuildEpoch(int taiwuId)
            => CaptureRebuildEpoch(GroupKind, taiwuId);

        internal static bool ReplaceGroupsIfRebuildUnchanged(int taiwuId,
            IEnumerable<GroupEntry> source, long expectedEpoch, uint expectedWorldId,
            string expectedDirectory)
            => expectedEpoch >= 0 && expectedWorldId > 0
                && ReplaceGroups(taiwuId, source, expectedEpoch,
                    expectedWorldId, expectedDirectory);

        private static bool ReplaceGroups(int taiwuId, IEnumerable<GroupEntry> source,
            long? expectedEpoch, uint? expectedWorldId = null,
            string expectedDirectory = null)
        {
            var entries = new List<GroupEntry>();
            if (source != null)
                foreach (GroupEntry entry in source)
                    if (Valid(entry)) entries.Add(Clone(entry));
            return ReplaceAll(GroupKind, taiwuId, null, entries, expectedEpoch,
                expectedWorldId, expectedDirectory);
        }

        private static long CaptureRebuildEpoch(string kind, int taiwuId)
        {
            if (taiwuId <= 0 || JianghuYoulingPaths.CurrentWorldId == 0) return -1;
            string key = CacheKey(kind, taiwuId);
            lock (Gate)
            {
                if (Mutations.TryGetValue(key, out MutationState active)
                    && active.Active > 0) return -1;
                return MutationEpochs.TryGetValue(key, out long epoch) ? epoch : 0;
            }
        }

        internal static bool TryUpsertSingle(int taiwuId, SingleEntry value)
            => TryUpsertSingle(JianghuYoulingPaths.CurrentWorldId,
                JianghuYoulingPaths.ChatLogs, taiwuId, value);

        private static bool TryUpsertSingle(uint worldId, string directory,
            int taiwuId, SingleEntry value, long queueGeneration = -1)
        {
            directory = NormalizeDirectory(directory);
            if (worldId == 0 || string.IsNullOrEmpty(directory) || !Valid(value)
                || !QueueGenerationCurrent(queueGeneration)) return false;
            string key = CacheKey(worldId, SingleKind, taiwuId);
            lock (PublicationGate(key))
            {
                if (!TryLoadCacheIgnoringDirty(SingleKind, taiwuId, directory,
                    worldId, out CacheState cache)) return false;
                long expectedEpoch;
                lock (Gate)
                {
                    MutationEpochs.TryGetValue(key, out expectedEpoch);
                }
                int index = cache.Singles.FindIndex(x => x.NpcId == value.NpcId);
                bool added = index < 0;
                // SaveConv also persists name repairs, memory ACKs and compaction bookkeeping.
                // Those writes must not reorder the sidebar: only an actually appended chat turn
                // advances activity.  A deletion/retry preparation similarly keeps the old order;
                // the later appended replacement turn will grow from the reduced indexed count.
                if (!added && value.Turns <= cache.Singles[index].Turns)
                    value.LastActivityUtcTicks = cache.Singles[index].LastActivityUtcTicks;
                if (added) cache.Singles.Add(Clone(value));
                else cache.Singles[index] = Clone(value);
                if (!QueueGenerationCurrent(queueGeneration)
                    || !SaveBucket(cache.Manifest, BucketFor(value.NpcId),
                        cache.Singles, null, directory)) return false;
                if (added)
                {
                    cache.Manifest.EntryCount++;
                    if (!SaveManifest(cache.Manifest, directory)) return false;
                }
                lock (Gate)
                {
                    MutationEpochs.TryGetValue(key, out long currentEpoch);
                    if (currentEpoch != expectedEpoch
                        || !QueueGenerationCurrent(queueGeneration)) return false;
                    Caches[key] = CloneCache(cache);
                }
                return true;
            }
        }

        internal static bool TryRemoveSingle(int taiwuId, int npcId)
            => TryRemoveSingle(JianghuYoulingPaths.CurrentWorldId,
                JianghuYoulingPaths.ChatLogs, taiwuId, npcId);

        private static bool TryRemoveSingle(uint worldId, string directory,
            int taiwuId, int npcId, long queueGeneration = -1)
        {
            directory = NormalizeDirectory(directory);
            if (worldId == 0 || string.IsNullOrEmpty(directory)
                || taiwuId <= 0 || npcId < 0
                || !QueueGenerationCurrent(queueGeneration)) return false;
            string key = CacheKey(worldId, SingleKind, taiwuId);
            lock (PublicationGate(key))
            {
                if (!TryLoadCacheIgnoringDirty(SingleKind, taiwuId, directory,
                    worldId, out CacheState cache)) return false;
                long expectedEpoch;
                lock (Gate)
                {
                    MutationEpochs.TryGetValue(key, out expectedEpoch);
                }
                int removed = cache.Singles.RemoveAll(x => x.NpcId == npcId);
                if (removed == 0) return QueueGenerationCurrent(queueGeneration);
                if (!SaveBucket(cache.Manifest, BucketFor(npcId),
                    cache.Singles, null, directory)) return false;
                cache.Manifest.EntryCount = Math.Max(0, cache.Manifest.EntryCount - removed);
                if (!SaveManifest(cache.Manifest, directory)) return false;
                lock (Gate)
                {
                    MutationEpochs.TryGetValue(key, out long currentEpoch);
                    if (currentEpoch != expectedEpoch
                        || !QueueGenerationCurrent(queueGeneration)) return false;
                    Caches[key] = CloneCache(cache);
                }
                return true;
            }
        }

        internal static bool TryUpsertGroup(int taiwuId, GroupEntry value)
            => TryUpsertGroup(JianghuYoulingPaths.CurrentWorldId,
                JianghuYoulingPaths.ChatLogs, taiwuId, value);

        private static bool TryUpsertGroup(uint worldId, string directory,
            int taiwuId, GroupEntry value, long queueGeneration = -1)
        {
            directory = NormalizeDirectory(directory);
            if (worldId == 0 || string.IsNullOrEmpty(directory) || !Valid(value)
                || !QueueGenerationCurrent(queueGeneration)) return false;
            string key = CacheKey(worldId, GroupKind, taiwuId);
            lock (PublicationGate(key))
            {
                if (!TryLoadCacheIgnoringDirty(GroupKind, taiwuId, directory,
                    worldId, out CacheState cache)) return false;
                long expectedEpoch;
                lock (Gate)
                {
                    MutationEpochs.TryGetValue(key, out expectedEpoch);
                }
                int index = cache.Groups.FindIndex(x => string.Equals(x.GroupId, value.GroupId,
                    StringComparison.Ordinal));
                bool added = index < 0;
                // Clearing an existing group, repairing metadata, or adding a member is not
                // chat activity. Keep the old sidebar order even though the empty document was
                // just rewritten. A genuinely new empty group still receives its creation time.
                if (!added && (!value.HasActiveLines && value.ArchivedSegmentCount <= 0
                    || value.LastActivityUtcTicks <= 0))
                    value.LastActivityUtcTicks = cache.Groups[index].LastActivityUtcTicks;
                if (added) cache.Groups.Add(Clone(value));
                else cache.Groups[index] = Clone(value);
                if (!SaveBucket(cache.Manifest, BucketFor(value.GroupId),
                    null, cache.Groups, directory)) return false;
                if (added)
                {
                    cache.Manifest.EntryCount++;
                    if (!SaveManifest(cache.Manifest, directory)) return false;
                }
                lock (Gate)
                {
                    MutationEpochs.TryGetValue(key, out long currentEpoch);
                    if (currentEpoch != expectedEpoch
                        || !QueueGenerationCurrent(queueGeneration)) return false;
                    Caches[key] = CloneCache(cache);
                }
                return true;
            }
        }

        internal static bool QueueUpsertSingle(Mutation mutation, uint worldId,
            string directory, int taiwuId, SingleEntry value)
        {
            string capturedDirectory = NormalizeDirectory(directory);
            if (mutation == null || worldId == 0 || string.IsNullOrEmpty(capturedDirectory)
                || taiwuId <= 0 || !Valid(value)) return false;
            SingleEntry captured = Clone(value);
            return QueueBackgroundMutation(mutation,
                generation => TryUpsertSingle(worldId, capturedDirectory, taiwuId,
                    captured, generation));
        }

        internal static bool QueueRemoveSingle(Mutation mutation, uint worldId,
            string directory, int taiwuId, int npcId)
        {
            string capturedDirectory = NormalizeDirectory(directory);
            if (mutation == null || worldId == 0 || string.IsNullOrEmpty(capturedDirectory)
                || taiwuId <= 0 || npcId < 0) return false;
            return QueueBackgroundMutation(mutation,
                generation => TryRemoveSingle(worldId, capturedDirectory, taiwuId,
                    npcId, generation));
        }

        internal static bool QueueUpsertGroup(Mutation mutation, uint worldId,
            string directory, int taiwuId, GroupEntry value)
        {
            string capturedDirectory = NormalizeDirectory(directory);
            if (mutation == null || worldId == 0 || string.IsNullOrEmpty(capturedDirectory)
                || taiwuId <= 0 || !Valid(value)) return false;
            GroupEntry captured = Clone(value);
            return QueueBackgroundMutation(mutation,
                generation => TryUpsertGroup(worldId, capturedDirectory, taiwuId,
                    captured, generation));
        }

        private static bool QueueBackgroundMutation(Mutation mutation, Func<long, bool> apply)
        {
            if (mutation == null || apply == null) return false;
            try
            {
                lock (BackgroundWriteGate)
                {
                    long generation = _backgroundQueueGeneration;
                    BackgroundWriteTail = BackgroundWriteTail.ContinueWith(_ =>
                    {
                        try
                        {
                            if (QueueGenerationCurrent(generation) && apply(generation))
                                mutation.Commit();
                        }
                        catch { }
                        finally { mutation.Dispose(); }
                    }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                }
                return true;
            }
            catch { return false; }
        }

        internal static bool WaitForBackgroundWritesForTests(int timeoutMilliseconds)
        {
            Task snapshot;
            lock (BackgroundWriteGate) snapshot = BackgroundWriteTail;
            try { return snapshot.Wait(Math.Max(1, timeoutMilliseconds)); }
            catch { return false; }
        }

        private static bool ReplaceAll(string kind, int taiwuId, List<SingleEntry> singles,
            List<GroupEntry> groups, long? expectedEpoch, uint? expectedWorldId,
            string expectedDirectory)
        {
            if (taiwuId <= 0 || JianghuYoulingPaths.CurrentWorldId == 0) return false;
            uint worldId = expectedWorldId ?? JianghuYoulingPaths.CurrentWorldId;
            string directory = NormalizeDirectory(
                expectedDirectory ?? JianghuYoulingPaths.ChatLogs);
            if (worldId == 0 || string.IsNullOrEmpty(directory)) return false;
            bool scopedRebuild = expectedWorldId.HasValue;
            string key = CacheKey(worldId, kind, taiwuId);
            long generation;
            long previous = 0;
            if (TryLoadManifest(kind, taiwuId, directory, worldId,
                out Manifest old)) previous = old.Generation;
            lock (Gate)
            {
                if (scopedRebuild && !ScopeMatches(worldId, directory)) return false;
                if (Mutations.TryGetValue(key, out MutationState active)
                    && active.Active > 0) return false;
                if (expectedEpoch.HasValue)
                {
                    MutationEpochs.TryGetValue(key, out long currentEpoch);
                    if (currentEpoch != expectedEpoch.Value) return false;
                }
                _generationSeed = Math.Max(Math.Max(_generationSeed + 1,
                    DateTime.UtcNow.Ticks), previous + 1);
                generation = _generationSeed;
            }

            var manifest = new Manifest
            {
                WorldId = worldId,
                TaiwuId = taiwuId,
                Kind = kind,
                Generation = generation,
                EntryCount = kind == SingleKind ? singles?.Count ?? 0 : groups?.Count ?? 0,
            };
            // Generation-named shards are not visible until the manifest commit.  Stage the
            // expensive durable writes without holding Gate so ordinary chat saves cannot stall
            // behind up to 32 shard flush/readback operations.
            for (int bucket = 0; bucket < BucketCount; bucket++)
                if (!SaveBucket(manifest, bucket, singles, groups,
                    directory, omitEmpty: true)) return false;

            lock (PublicationGate(key))
            {
                lock (Gate)
                {
                    // Revalidate the complete world + mutation fence immediately before
                    // publishing the small manifest. A message written during shard staging
                    // increments the epoch and leaves those generation files unreachable.
                    if (scopedRebuild && !ScopeMatches(worldId, directory)) return false;
                    if (Mutations.TryGetValue(key, out MutationState active)
                        && active.Active > 0) return false;
                    if (expectedEpoch.HasValue)
                    {
                        MutationEpochs.TryGetValue(key, out long currentEpoch);
                        if (currentEpoch != expectedEpoch.Value) return false;
                    }
                }
                // Serialize only per-index publication I/O. Unity state never takes this gate;
                // background incremental writers do, so they cannot load an old cached
                // generation while this manifest is in flight.
                if (!SaveManifest(manifest, directory)) return false;
                lock (Gate)
                {
                    if (scopedRebuild && !ScopeMatches(worldId, directory))
                    {
                        Caches.Remove(key);
                        return false;
                    }
                    if (Mutations.TryGetValue(key, out MutationState active)
                        && active.Active > 0)
                    {
                        Caches.Remove(key);
                        return false;
                    }
                    if (expectedEpoch.HasValue)
                    {
                        MutationEpochs.TryGetValue(key, out long currentEpoch);
                        if (currentEpoch != expectedEpoch.Value)
                        {
                            Caches.Remove(key);
                            return false;
                        }
                    }
                    if (!ClearDirtyFiles(kind, taiwuId, directory, key))
                    {
                        Caches.Remove(key);
                        return false;
                    }
                    var cache = new CacheState
                    {
                        Manifest = manifest,
                        Singles = singles == null ? new List<SingleEntry>() : CloneSingles(singles),
                        Groups = groups == null ? new List<GroupEntry>() : CloneGroups(groups),
                    };
                    Caches[key] = cache;
                    MutationEpochs.TryGetValue(key, out long committedEpoch);
                    MutationEpochs[key] = unchecked(committedEpoch + 1);
                }
            }
            CleanupOldGenerations(manifest, directory);
            return true;
        }

        private static bool TryLoadCache(string kind, int taiwuId, out CacheState cache)
        {
            cache = null;
            string dirty = DirtyPath(kind, taiwuId);
            if (File.Exists(dirty) || File.Exists(dirty + ".preexisting")) return false;
            if (!TryLoadCacheIgnoringDirty(kind, taiwuId, out cache)) return false;
            lock (Gate)
            {
                if (File.Exists(dirty) || File.Exists(dirty + ".preexisting"))
                {
                    cache = null;
                    return false;
                }
            }
            return true;
        }

        private static bool TryLoadCacheIgnoringDirty(string kind, int taiwuId, out CacheState cache)
            => TryLoadCacheIgnoringDirty(kind, taiwuId, JianghuYoulingPaths.ChatLogs,
                JianghuYoulingPaths.CurrentWorldId, out cache);

        private static bool TryLoadCacheIgnoringDirty(string kind, int taiwuId,
            string directory, uint worldId, out CacheState cache)
        {
            cache = null;
            string key = CacheKey(worldId, kind, taiwuId);
            lock (Gate)
                if (Caches.TryGetValue(key, out CacheState cached))
                {
                    cache = CloneCache(cached);
                    return true;
                }
            if (!TryLoadManifest(kind, taiwuId, directory, worldId,
                out Manifest manifest)) return false;
            var singles = new List<SingleEntry>();
            var groups = new List<GroupEntry>();
            for (int bucket = 0; bucket < BucketCount; bucket++)
            {
                string path = ShardPath(manifest, bucket, directory);
                if (!File.Exists(path)) continue;
                if (!DurableFileStore.TryReadRecoverableText(path, MaxShardBytes,
                    IsValidShardDocument, out string json, out _, out _)) return false;
                Shard shard;
                try { shard = JsonConvert.DeserializeObject<Shard>(json); }
                catch { return false; }
                if (!ShardMatches(shard, manifest, bucket)) return false;
                if (kind == SingleKind) singles.AddRange(CloneSingles(shard.Singles));
                else groups.AddRange(CloneGroups(shard.Groups));
            }
            int count = kind == SingleKind ? singles.Count : groups.Count;
            if (count != manifest.EntryCount) return false;
            var loaded = new CacheState { Manifest = manifest, Singles = singles, Groups = groups };
            lock (Gate)
            {
                if (Caches.TryGetValue(key, out CacheState current))
                    cache = CloneCache(current);
                else
                {
                    Caches[key] = CloneCache(loaded);
                    cache = loaded;
                }
            }
            return true;
        }

        private static bool SaveBucket(Manifest manifest, int bucket, List<SingleEntry> singles,
            List<GroupEntry> groups, string directory = null, bool omitEmpty = false)
        {
            var shard = new Shard
            {
                WorldId = manifest.WorldId,
                TaiwuId = manifest.TaiwuId,
                Kind = manifest.Kind,
                Generation = manifest.Generation,
                Bucket = bucket,
            };
            if (manifest.Kind == SingleKind && singles != null)
                foreach (SingleEntry entry in singles)
                    if (BucketFor(entry.NpcId) == bucket) shard.Singles.Add(Clone(entry));
            if (manifest.Kind == GroupKind && groups != null)
                foreach (GroupEntry entry in groups)
                    if (BucketFor(entry.GroupId) == bucket) shard.Groups.Add(Clone(entry));
            if (shard.Singles.Count > MaxEntriesPerShard || shard.Groups.Count > MaxEntriesPerShard)
                return false;
            if (omitEmpty && shard.Singles.Count == 0 && shard.Groups.Count == 0) return true;
            string json = JsonConvert.SerializeObject(shard, Formatting.None);
            return DurableFileStore.TryWriteTextAtomic(ShardPath(manifest, bucket, directory), json,
                MaxShardBytes, IsValidShardDocument);
        }

        private static bool SaveManifest(Manifest manifest, string directory = null)
        {
            string json = JsonConvert.SerializeObject(manifest, Formatting.None);
            return DurableFileStore.TryWriteTextAtomic(
                ManifestPath(manifest.Kind, manifest.TaiwuId, directory),
                json, MaxManifestBytes, IsValidManifestDocument);
        }

        private static bool TryLoadManifest(string kind, int taiwuId, out Manifest manifest)
            => TryLoadManifest(kind, taiwuId, JianghuYoulingPaths.ChatLogs,
                JianghuYoulingPaths.CurrentWorldId, out manifest);

        private static bool TryLoadManifest(string kind, int taiwuId, string directory,
            uint expectedWorldId, out Manifest manifest)
        {
            manifest = null;
            string path = ManifestPath(kind, taiwuId, directory);
            if (!DurableFileStore.TryReadRecoverableText(path, MaxManifestBytes,
                IsValidManifestDocument, out string json, out _, out _)) return false;
            try { manifest = JsonConvert.DeserializeObject<Manifest>(json); }
            catch { return false; }
            return ManifestMatches(manifest, kind, taiwuId, expectedWorldId);
        }

        private static bool IsValidManifestDocument(string json)
        {
            try
            {
                Manifest manifest = JsonConvert.DeserializeObject<Manifest>(json);
                return manifest != null && manifest.Version == CurrentVersion
                    && manifest.WorldId > 0 && manifest.TaiwuId > 0
                    && (manifest.Kind == SingleKind || manifest.Kind == GroupKind)
                    && manifest.Generation > 0 && manifest.Buckets == BucketCount
                    && manifest.EntryCount >= 0
                    && manifest.EntryCount <= BucketCount * MaxEntriesPerShard;
            }
            catch { return false; }
        }

        private static bool IsValidShardDocument(string json)
        {
            try
            {
                Shard shard = JsonConvert.DeserializeObject<Shard>(json);
                if (shard == null || shard.Version != CurrentVersion || shard.WorldId == 0
                    || shard.TaiwuId <= 0 || shard.Generation <= 0
                    || shard.Bucket < 0 || shard.Bucket >= BucketCount
                    || (shard.Kind != SingleKind && shard.Kind != GroupKind)
                    || shard.Singles == null || shard.Groups == null
                    || shard.Singles.Count > MaxEntriesPerShard
                    || shard.Groups.Count > MaxEntriesPerShard) return false;
                if (shard.Kind == SingleKind && shard.Groups.Count != 0
                    || shard.Kind == GroupKind && shard.Singles.Count != 0) return false;
                var ids = new HashSet<int>();
                foreach (SingleEntry entry in shard.Singles)
                    if (!Valid(entry) || !ids.Add(entry.NpcId)
                        || BucketFor(entry.NpcId) != shard.Bucket) return false;
                var groups = new HashSet<string>(StringComparer.Ordinal);
                foreach (GroupEntry entry in shard.Groups)
                    if (!Valid(entry) || !groups.Add(entry.GroupId)
                        || BucketFor(entry.GroupId) != shard.Bucket) return false;
                return true;
            }
            catch { return false; }
        }

        private static bool ManifestMatches(Manifest value, string kind, int taiwuId,
            uint? expectedWorldId = null)
            => value != null && value.WorldId
                == (expectedWorldId ?? JianghuYoulingPaths.CurrentWorldId)
                && value.TaiwuId == taiwuId && value.Kind == kind;

        private static bool ShardMatches(Shard value, Manifest manifest, int bucket)
            => value != null && value.WorldId == manifest.WorldId
                && value.TaiwuId == manifest.TaiwuId && value.Kind == manifest.Kind
                && value.Generation == manifest.Generation && value.Bucket == bucket;

        private static bool Valid(SingleEntry value)
            => value != null && value.NpcId >= 0 && value.Turns > 0
                && value.Turns <= 1000000
                && value.PlayerConversations >= 0 && value.PlayerConversations <= 1000000
                && value.LastPlayerDate >= 0 && value.LastPlayerDate <= 1000000
                && value.RecentPlayerConversations >= 0
                && value.RecentPlayerConversations <= value.PlayerConversations
                && value.RecentActiveMonths >= 0 && value.RecentActiveMonths <= 6
                && ValidName(value.Name);

        private static bool Valid(GroupEntry value)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.GroupId)
                || value.GroupId.Length > MaxGroupIdLength || !SafeGroupId(value.GroupId)
                || value.ArchivedSegmentCount < 0
                || value.Members == null || value.Members.Count == 0
                || value.Members.Count > MaxGroupMembers) return false;
            var ids = new HashSet<int>();
            foreach (MemberEntry member in value.Members)
                if (member == null || member.Id <= 0 || !ids.Add(member.Id)
                    || !ValidName(member.Name)) return false;
            return true;
        }

        private static bool SafeGroupId(string value)
        {
            foreach (char c in value)
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z')
                    && !(c >= '0' && c <= '9') && c != '_' && c != '-')
                    return false;
            return true;
        }

        private static bool ValidName(string value)
        {
            if (value == null) return true;
            if (value.Length > MaxNameLength) return false;
            foreach (char c in value) if (char.IsControl(c)) return false;
            return true;
        }

        private static int BucketFor(int id)
            => (id & int.MaxValue) % BucketCount;

        private static int BucketFor(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in value ?? string.Empty)
                {
                    hash ^= c;
                    hash *= 16777619;
                }
                return (int)(hash % BucketCount);
            }
        }

        private static string CacheKey(string kind, int taiwuId)
            => JianghuYoulingPaths.CurrentWorldId + ":" + kind + ":" + taiwuId;

        private static string CacheKey(uint worldId, string kind, int taiwuId)
            => worldId + ":" + kind + ":" + taiwuId;

        private static string ManifestPath(string kind, int taiwuId, string directory = null)
            => Path.Combine(directory ?? JianghuYoulingPaths.ChatLogs,
                "ConversationIndex_" + kind + "_" + taiwuId + ".manifest.json");

        private static string DirtyPath(string kind, int taiwuId, string directory = null)
            => Path.Combine(directory ?? JianghuYoulingPaths.ChatLogs,
                "ConversationIndex_" + kind + "_" + taiwuId + ".dirty");

        private static string ShardPath(Manifest manifest, int bucket, string directory = null)
            => Path.Combine(directory ?? JianghuYoulingPaths.ChatLogs,
                "ConversationIndex_" + manifest.Kind + "_" + manifest.TaiwuId + "_"
                + manifest.Generation + "_" + bucket.ToString("D2") + ".json");

        private static bool ClearDirtyFiles(string kind, int taiwuId, string directory = null,
            string key = null)
        {
            foreach (string path in new[]
            {
                DirtyPath(kind, taiwuId, directory),
                DirtyPath(kind, taiwuId, directory) + ".preexisting"
            })
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    if (File.Exists(path)) return false;
                }
                catch { return false; }
            Mutations.Remove(key ?? CacheKey(kind, taiwuId));
            return true;
        }

        private static void CleanupOldGenerations(Manifest manifest, string directory = null)
        {
            try
            {
                string prefix = "ConversationIndex_" + manifest.Kind + "_" + manifest.TaiwuId + "_";
                foreach (string path in Directory.GetFiles(
                    directory ?? JianghuYoulingPaths.ChatLogs, prefix + "*.json*"))
                    if (ShouldDeleteGenerationFile(
                        Path.GetFileName(path), prefix, manifest.Generation))
                        try { File.Delete(path); } catch { }
            }
            catch { }
        }

        internal static bool ShouldDeleteGenerationFile(
            string fileName, string prefix, long committedGeneration)
        {
            if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(prefix)
                || committedGeneration <= 0
                || !fileName.StartsWith(prefix, StringComparison.Ordinal))
                return false;
            string suffix = fileName.Substring(prefix.Length);
            int separator = suffix.IndexOf('_');
            if (separator <= 0
                || !long.TryParse(suffix.Substring(0, separator),
                    out long candidateGeneration)
                || candidateGeneration <= 0)
                return false;
            // A concurrent rebuild can already be staging a newer generation while the
            // previously committed rebuild reaches cleanup. Only generations proven older
            // than this manifest are unreachable; current and future staging files must stay.
            return candidateGeneration < committedGeneration;
        }

        private static string NormalizeDirectory(string directory)
        {
            try
            {
                return string.IsNullOrWhiteSpace(directory)
                    ? null : Path.GetFullPath(directory);
            }
            catch { return null; }
        }

        private static bool ScopeMatches(uint worldId, string directory)
            => worldId > 0 && JianghuYoulingPaths.CurrentWorldId == worldId
                && string.Equals(NormalizeDirectory(JianghuYoulingPaths.ChatLogs),
                    directory, StringComparison.OrdinalIgnoreCase);

        private static bool QueueGenerationCurrent(long generation)
            => generation < 0 || Interlocked.Read(ref _backgroundQueueGeneration) == generation;

        private static object PublicationGate(string key)
        {
            lock (Gate)
            {
                if (!PublicationGates.TryGetValue(key, out object value))
                    PublicationGates[key] = value = new object();
                return value;
            }
        }

        private static CacheState CloneCache(CacheState value)
            => new CacheState
            {
                Manifest = new Manifest
                {
                    Version = value.Manifest.Version,
                    WorldId = value.Manifest.WorldId,
                    TaiwuId = value.Manifest.TaiwuId,
                    Kind = value.Manifest.Kind,
                    Generation = value.Manifest.Generation,
                    Buckets = value.Manifest.Buckets,
                    EntryCount = value.Manifest.EntryCount,
                },
                Singles = CloneSingles(value.Singles),
                Groups = CloneGroups(value.Groups),
            };

        private static SingleEntry Clone(SingleEntry value)
            => new SingleEntry
            {
                NpcId = value.NpcId,
                Name = value.Name,
                LastDate = value.LastDate,
                LastPlayerDate = value.LastPlayerDate,
                Turns = value.Turns,
                PlayerConversations = value.PlayerConversations,
                RecentPlayerConversations = value.RecentPlayerConversations,
                RecentActiveMonths = value.RecentActiveMonths,
                LastActivityUtcTicks = value.LastActivityUtcTicks,
            };

        private static GroupEntry Clone(GroupEntry value)
        {
            var clone = new GroupEntry
            {
                GroupId = value.GroupId,
                HasActiveLines = value.HasActiveLines,
                ArchivedSegmentCount = value.ArchivedSegmentCount,
                LastActivityUtcTicks = value.LastActivityUtcTicks,
            };
            foreach (MemberEntry member in value.Members)
                clone.Members.Add(new MemberEntry { Id = member.Id, Name = member.Name });
            return clone;
        }

        private static List<SingleEntry> CloneSingles(IEnumerable<SingleEntry> source)
        {
            var result = new List<SingleEntry>();
            if (source != null) foreach (SingleEntry value in source) result.Add(Clone(value));
            return result;
        }

        private static List<GroupEntry> CloneGroups(IEnumerable<GroupEntry> source)
        {
            var result = new List<GroupEntry>();
            if (source != null) foreach (GroupEntry value in source) result.Add(Clone(value));
            return result;
        }
    }
}
