using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Memory
{
    /// <summary>
    /// 群聊成员副作用与群 transcript 之间的 durable 提交屏障。
    /// prepared 必须早于个人记忆落盘；父群聊提交对应成员行并清掉单聊动作日志后，
    /// 才能 complete。崩溃恢复时由调用方根据 transcript 决定 finalize 或 cleanup。
    /// </summary>
    public sealed class GroupExchangeJournal
    {
        private const int CurrentVersion = 4;
        private const int IdentityBoundVersion = 2;
        private const int IntegrityBoundVersion = 3;
        private const int MaxDocumentBytes = 4 * 1024 * 1024;
        // One journal is replayed on Unity's main thread. A normal group has only a
        // handful of in-flight member attempts; cap pathological/corrupt backlog so a
        // single file cannot monopolize a frame during crash recovery.
        private const int MaxEntries = 512;
        public const string Prepared = "prepared";
        // Two-phase clear fence. cleanup_intent is written before the empty transcript;
        // cleanup_committed is written only after that transcript has durable replicas.
        // cleanup_pending is accepted solely as the ambiguous legacy v2 state.
        public const string CleanupIntent = "cleanup_intent";
        public const string CleanupCommitted = "cleanup_committed";
        public const string CleanupPending = "cleanup_pending";

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly JsonSerializerSettings StrictJson = new JsonSerializerSettings
        {
            MissingMemberHandling = MissingMemberHandling.Error,
        };

        public sealed class Entry
        {
            public string ExchangeId { get; set; }
            // v2: one NPC may speak more than once inside the same player exchange.
            // v1 entries are assigned a deterministic, durable attempt id during migration;
            // a v2 document is never allowed to use the old wildcard identity.
            public string AttemptId { get; set; }
            public int NpcId { get; set; }
            public string PlayerInput { get; set; }
            public int WorldDate { get; set; }
            public string State { get; set; }
            // Keep persisted collections null by default. Json.NET otherwise runs the
            // property initializer when a field is absent and turns a truncated v2
            // document into a seemingly valid empty recovery record.
            public List<string> MemoryIds { get; set; }
            public List<string> Actions { get; set; }
            public List<string> ToolResults { get; set; }
            public List<string> OperationIds { get; set; }
            public long UpdatedUtcTicks { get; set; }
        }

        private sealed class Document
        {
            [JsonProperty(Required = Required.Always)]
            public int Version { get; set; } = CurrentVersion;
            [JsonProperty(Required = Required.Always)]
            public uint WorldId { get; set; }
            [JsonProperty(Required = Required.Always)]
            public int TaiwuId { get; set; }
            [JsonProperty(Required = Required.Always)]
            public string GroupId { get; set; }
            [JsonProperty(Required = Required.Always)]
            public long Revision { get; set; }
            public string IntegritySha256 { get; set; }
            [JsonProperty(Required = Required.Always)]
            public List<Entry> Entries { get; set; } = new List<Entry>();
        }

        private sealed class Candidate
        {
            public Document Document;
            public int Rank;
            public string Path;
        }

        private static readonly object GatesLock = new object();
        private static readonly Dictionary<string, object> Gates =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        private readonly string _path;
        private readonly uint _worldId;
        private readonly int _taiwuId;
        private readonly string _groupId;

        public GroupExchangeJournal(string path, uint worldId, int taiwuId, string groupId)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path");
            if (worldId == 0) throw new ArgumentOutOfRangeException(nameof(worldId));
            if (taiwuId <= 0) throw new ArgumentOutOfRangeException(nameof(taiwuId));
            if (string.IsNullOrWhiteSpace(groupId)) throw new ArgumentException("groupId");
            _path = path;
            _worldId = worldId;
            _taiwuId = taiwuId;
            _groupId = groupId;
        }

        public IReadOnlyList<Entry> Snapshot()
        {
            lock (Gate(_path))
            {
                if (!TryReadRecoverable(out var document)) return null;
                return CloneEntries(document.Entries);
            }
        }

        /// <summary>
        /// Discover an existing journal's persisted scope without assuming the current
        /// Taiwu. Each candidate scope is then reopened through the normal quorum,
        /// identity, integrity and migration path before it is returned.
        /// </summary>
        public static bool TryOpenExisting(string path, uint expectedWorldId,
            out GroupExchangeJournal journal, out int taiwuId, out string groupId,
            out IReadOnlyList<Entry> entries)
        {
            journal = null;
            taiwuId = 0;
            groupId = null;
            entries = null;
            if (string.IsNullOrWhiteSpace(path) || expectedWorldId == 0) return false;
            var scopes = new List<string>();
            foreach (string candidate in new[] { path, path + ".tmp", path + ".bak" })
            {
                if (!File.Exists(candidate)) continue;
                try
                {
                    Document raw = ReadStrictDocument(candidate);
                    if (raw == null || raw.WorldId != expectedWorldId || raw.TaiwuId <= 0
                        || string.IsNullOrWhiteSpace(raw.GroupId)) continue;
                    string key = raw.TaiwuId + "\n" + raw.GroupId;
                    if (!scopes.Contains(key)) scopes.Add(key);
                }
                catch { }
            }
            foreach (string scope in scopes)
            {
                int split = scope.IndexOf('\n');
                int candidateTaiwu;
                if (split <= 0 || !int.TryParse(scope.Substring(0, split), out candidateTaiwu)) continue;
                string candidateGroup = scope.Substring(split + 1);
                try
                {
                    var opened = new GroupExchangeJournal(path, expectedWorldId, candidateTaiwu, candidateGroup);
                    IReadOnlyList<Entry> snapshot = opened.Snapshot();
                    if (snapshot == null) continue;
                    journal = opened;
                    taiwuId = candidateTaiwu;
                    groupId = candidateGroup;
                    entries = snapshot;
                    return true;
                }
                catch { }
            }
            return false;
        }

        public bool Prepare(string exchangeId, string attemptId, int npcId, string playerInput, int worldDate,
            IEnumerable<string> memoryIds, IEnumerable<string> actions)
            => Prepare(exchangeId, attemptId, npcId, playerInput, worldDate, memoryIds, actions, null);

        public bool Prepare(string exchangeId, string attemptId, int npcId, string playerInput, int worldDate,
            IEnumerable<string> memoryIds, IEnumerable<string> actions, IEnumerable<string> operationIds)
            => Prepare(exchangeId, attemptId, npcId, playerInput, worldDate, memoryIds, actions,
                operationIds, null);

        public bool Prepare(string exchangeId, string attemptId, int npcId, string playerInput, int worldDate,
            IEnumerable<string> memoryIds, IEnumerable<string> actions, IEnumerable<string> operationIds,
            IEnumerable<string> toolResults)
        {
            if (string.IsNullOrWhiteSpace(exchangeId) || string.IsNullOrWhiteSpace(attemptId) || npcId < 0) return false;
            return Mutate(document =>
            {
                var entry = Find(document.Entries, exchangeId, attemptId, npcId);
                if (entry == null)
                {
                    entry = new Entry
                    {
                        ExchangeId = exchangeId,
                        AttemptId = attemptId,
                        NpcId = npcId,
                        PlayerInput = playerInput ?? string.Empty,
                        WorldDate = worldDate,
                        State = Prepared,
                    };
                    document.Entries.Add(entry);
                }
                if (string.IsNullOrEmpty(entry.PlayerInput)) entry.PlayerInput = playerInput ?? string.Empty;
                if (entry.WorldDate == 0) entry.WorldDate = worldDate;
                if (entry.State != CleanupPending && entry.State != CleanupIntent
                    && entry.State != CleanupCommitted) entry.State = Prepared;
                entry.MemoryIds = Distinct(memoryIds);
                entry.Actions = Distinct(actions);
                entry.ToolResults = Distinct(toolResults);
                entry.OperationIds = Distinct(operationIds);
                entry.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                return true;
            });
        }

        /// <summary>
        /// Persist the final player-visible execution receipts before the parent transcript
        /// is committed. Tool results are finalized only after the member agent loop exits,
        /// while the mutation barrier itself may have been prepared by an earlier tool call.
        /// </summary>
        public bool UpdateToolResults(string exchangeId, string attemptId, int npcId,
            IEnumerable<string> toolResults)
            => Mutate(document =>
            {
                var entry = Find(document.Entries, exchangeId, attemptId, npcId);
                if (entry == null || entry.State != Prepared) return false;
                entry.ToolResults = Distinct(toolResults);
                entry.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                return true;
            });

        public bool MarkCleanupPending(string exchangeId, string attemptId, int npcId)
            => MarkCleanupIntent(exchangeId, attemptId, npcId);

        public bool MarkCleanupIntent(string exchangeId, string attemptId, int npcId)
            => Mutate(document =>
            {
                var entry = Find(document.Entries, exchangeId, attemptId, npcId);
                if (entry == null) return false;
                entry.State = CleanupIntent;
                entry.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                return true;
            });

        public bool MarkCleanupCommitted(string exchangeId, string attemptId, int npcId)
            => Mutate(document =>
            {
                var entry = Find(document.Entries, exchangeId, attemptId, npcId);
                if (entry == null) return false;
                if (entry.State != CleanupIntent && entry.State != CleanupPending
                    && entry.State != CleanupCommitted) return false;
                entry.State = CleanupCommitted;
                entry.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                return true;
            });

        public bool RestorePrepared(string exchangeId, string attemptId, int npcId)
            => Mutate(document =>
            {
                var entry = Find(document.Entries, exchangeId, attemptId, npcId);
                if (entry == null) return false;
                if (entry.State != CleanupIntent && entry.State != CleanupPending
                    && entry.State != Prepared) return false;
                entry.State = Prepared;
                entry.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                return true;
            });

        public bool Complete(string exchangeId, string attemptId, int npcId)
            => Mutate(document => document.Entries.RemoveAll(entry => entry != null
                && entry.NpcId == npcId
                && string.Equals(entry.AttemptId ?? string.Empty, attemptId ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(entry.ExchangeId, exchangeId, StringComparison.Ordinal)) > 0,
                missingIsSuccess: true);

        /// <summary>
        /// 固定模板人物首次对话建立永久副本后，群聊事务的成员身份也必须同步迁移；否则
        /// transcript 已改成副本而崩溃恢复日志仍指向原人物，会被身份校验拒绝。
        /// </summary>
        public bool ReplaceNpcIdentity(int oldNpcId, int newNpcId)
        {
            if (oldNpcId < 0 || newNpcId < 0) return false;
            if (oldNpcId == newNpcId) return true;
            return Mutate(document =>
            {
                bool changed = false;
                foreach (Entry entry in document.Entries)
                {
                    if (entry == null || entry.NpcId != oldNpcId) continue;
                    if (Find(document.Entries, entry.ExchangeId, entry.AttemptId, newNpcId) != null)
                        return false;
                    entry.NpcId = newNpcId;
                    changed = true;
                }
                return changed;
            }, missingIsSuccess: true);
        }

        private bool Mutate(Func<Document, bool> mutation, bool missingIsSuccess = false)
        {
            lock (Gate(_path))
            {
                try
                {
                    if (!TryReadRecoverable(out var document)) return false;
                    bool changed = mutation(document);
                    if (!changed) return missingIsSuccess;
                    document.Version = CurrentVersion;
                    document.WorldId = _worldId;
                    document.TaiwuId = _taiwuId;
                    document.GroupId = _groupId;
                    document.Revision = checked(document.Revision + 1);
                    return WriteAtomic(document);
                }
                catch
                {
                    return false;
                }
            }
        }

        private bool TryReadRecoverable(out Document document)
        {
            document = null;
            string[] paths = { _path, _path + ".tmp", _path + ".bak" };
            Candidate best = null;
            Candidate main = null;
            var valid = new List<Candidate>();
            bool any = false;
            for (int i = 0; i < paths.Length; i++)
            {
                if (!File.Exists(paths[i])) continue;
                any = true;
                var candidate = TryRead(paths[i]);
                if (candidate == null) continue;
                int rank = paths.Length - i;
                var wrapped = new Candidate { Document = candidate, Rank = rank, Path = paths[i] };
                valid.Add(wrapped);
                if (i == 0) main = wrapped;
            }
            // A valid main is the last published commit. A unique higher tmp is merely
            // an interrupted future stage and must not be auto-promoted. If main is gone
            // or corrupt, require two semantically identical same-revision replicas.
            if (main != null)
            {
                best = main;
                // A legacy main has no integrity digest. If two independent current-
                // version replicas attest the same strictly newer revision, prefer that
                // quorum and publish it instead of downgrading a completed migration.
                if (main.Document.Version < CurrentVersion)
                    foreach (Candidate candidate in valid)
                    {
                        if (candidate.Document.Version != CurrentVersion
                            || candidate.Document.Revision <= main.Document.Revision) continue;
                        int copies = 0;
                        foreach (Candidate other in valid)
                            if (SameDocument(candidate.Document, other.Document)) copies++;
                        if (copies >= 2 && (best == main
                            || candidate.Document.Revision > best.Document.Revision)) best = candidate;
                    }
            }
            else
                foreach (Candidate candidate in valid)
                {
                    int copies = 0;
                    foreach (Candidate other in valid)
                        if (SameDocument(candidate.Document, other.Document)) copies++;
                    if (copies < 2) continue;
                    if (best == null || candidate.Document.Revision > best.Document.Revision
                        || candidate.Document.Revision == best.Document.Revision && candidate.Rank > best.Rank)
                        best = candidate;
                }
            if (best == null)
            {
                if (any) return false;
                document = NewDocument();
                return true;
            }
            document = best.Document;
            if (document.Version == 1)
            {
                if (!MigrateV1(document) || !WriteAtomic(document)) return false;
            }
            else if (document.Version == IdentityBoundVersion)
            {
                if (!MigrateV2(document) || !WriteAtomic(document)) return false;
            }
            else if (document.Version == IntegrityBoundVersion)
            {
                if (!MigrateV3(document) || !WriteAtomic(document)) return false;
            }
            else
            {
                int copies = 0;
                foreach (Candidate candidate in valid)
                    if (SameDocument(document, candidate.Document)) copies++;
                if (main == null || !SameDocument(main.Document, document) || copies < 2)
                {
                    if (!WriteAtomic(document)) return false;
                }
            }
            return true;
        }

        private Document TryRead(string path)
        {
            try
            {
                var document = ReadStrictDocument(path);
                Validate(document);
                return document;
            }
            catch { return null; }
        }

        private Document NewDocument() => new Document
        {
            WorldId = _worldId,
            TaiwuId = _taiwuId,
            GroupId = _groupId,
        };

        private bool WriteAtomic(Document document)
        {
            string directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string tmp = _path + ".tmp";
            string promote = _path + ".swap";
            document.Version = CurrentVersion;
            document.IntegritySha256 = ComputeIntegrity(document);
            if (!ValidSha256(document.IntegritySha256)) return false;
            string json = JsonConvert.SerializeObject(document, Formatting.Indented);
            try
            {
                // tmp 本身是崩溃恢复候选。只有写穿 OS 缓存并能严格读回完整身份、
                // revision 与条目后，才允许触碰当前 main/bak。
                WriteDurable(tmp, json);
                var staged = ReadStrictDocument(tmp);
                Validate(staged);
                if (!SameDocument(staged, document)) return false;

                // File.Replace 会消耗 source。另写一份 promotion source，确保主文件替换后
                // 的读回若失败，已核验的 tmp 仍在，Prepare 也不会虚报 durable barrier 成功。
                WriteDurable(promote, json);
                var promotion = ReadStrictDocument(promote);
                Validate(promotion);
                if (!SameDocument(promotion, document)) return false;

                if (File.Exists(_path)) File.Replace(promote, _path, _path + ".bak", true);
                else File.Move(promote, _path);

                var committed = ReadStrictDocument(_path);
                Validate(committed);
                if (!SameDocument(committed, document)) return false;
                // A successful parent barrier always retains tmp at the same revision as
                // main. bak is the previous commit by File.Replace and cannot alone prove
                // the latest Prepare if main is later damaged.
                var replica = ReadStrictDocument(tmp);
                Validate(replica);
                if (!SameDocument(replica, document)) return false;

                // main+tmp is the dispatch-safe commit point. A failed optional bak
                // refresh must not report failure after the durable Prepare already
                // exists, otherwise recovery could dispatch it later after the caller
                // has treated this turn as aborted.
                try
                {
                    WriteDurable(_path + ".bak", json);
                    var backup = ReadStrictDocument(_path + ".bak");
                    Validate(backup);
                    if (!SameDocument(backup, document))
                        throw new InvalidDataException("backup_revision_mismatch");
                }
                catch
                {
                    return true; // JHYL_GROUP_JOURNAL_MAIN_TMP_COMMIT_POINT
                }
                return true;
            }
            catch
            {
                // 不删除 tmp：若 promotion/main 的最后一步失败，它仍是严格核验过的恢复证据。
                return false;
            }
        }

        private void Validate(Document document)
        {
            if (document == null || (document.Version != 1 && document.Version != IdentityBoundVersion
                    && document.Version != IntegrityBoundVersion && document.Version != CurrentVersion)
                || document.Revision < 0 || document.WorldId != _worldId || document.TaiwuId != _taiwuId
                || !string.Equals(document.GroupId, _groupId, StringComparison.Ordinal)
                || document.Entries == null || document.Entries.Count > MaxEntries)
                throw new InvalidDataException("invalid_document");

            var keys = new HashSet<string>(StringComparer.Ordinal);
            var allOperationIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in document.Entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.ExchangeId) || entry.NpcId < 0
                    || entry.PlayerInput == null || entry.UpdatedUtcTicks <= 0
                    || (document.Version >= IdentityBoundVersion && string.IsNullOrWhiteSpace(entry.AttemptId))
                    || (document.Version == 1 && entry.AttemptId != null && string.IsNullOrWhiteSpace(entry.AttemptId))
                    || (entry.State != Prepared && entry.State != CleanupPending
                        && entry.State != CleanupIntent && entry.State != CleanupCommitted)
                    || entry.MemoryIds == null || entry.Actions == null
                    || (document.Version >= CurrentVersion && entry.ToolResults == null)
                    || (document.Version >= IdentityBoundVersion && entry.OperationIds == null)
                    || !keys.Add(EntryKey(entry)))
                    throw new InvalidDataException("invalid_entry");
                var operationIds = new HashSet<string>(StringComparer.Ordinal);
                if (entry.OperationIds != null)
                    foreach (string operationId in entry.OperationIds)
                        if (!ValidOperationId(operationId) || !operationIds.Add(operationId)
                            || !allOperationIds.Add(operationId))
                            throw new InvalidDataException("invalid_operation_id");
                ValidateDistinctStrings(entry.MemoryIds, "invalid_memory_id");
                ValidateDistinctStrings(entry.Actions, "invalid_action");
                if (entry.ToolResults != null)
                    ValidateDistinctStrings(entry.ToolResults, "invalid_tool_result");
            }
            if (document.Version >= IntegrityBoundVersion)
            {
                string computed = ComputeIntegrity(document);
                if (!ValidSha256(document.IntegritySha256)
                    || !string.Equals(document.IntegritySha256, computed, StringComparison.Ordinal))
                    throw new InvalidDataException("integrity_mismatch");
            }
        }

        private static void ValidateDistinctStrings(IEnumerable<string> values, string error)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (values == null) throw new InvalidDataException(error);
            foreach (string value in values)
                if (string.IsNullOrWhiteSpace(value) || !seen.Add(value))
                    throw new InvalidDataException(error);
        }

        private static bool ValidOperationId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 32) return false;
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        private bool MigrateV1(Document document)
        {
            if (document == null || document.Version != 1 || document.Revision == long.MaxValue) return false;
            for (int i = 0; i < document.Entries.Count; i++)
            {
                Entry entry = document.Entries[i];
                if (entry == null) return false;
                if (string.IsNullOrWhiteSpace(entry.AttemptId))
                    entry.AttemptId = LegacyAttemptId(entry, i);
                if (entry.OperationIds == null) entry.OperationIds = new List<string>();
                if (entry.ToolResults == null) entry.ToolResults = new List<string>();
            }
            document.Version = CurrentVersion;
            document.IntegritySha256 = null;
            document.Revision++;
            document.IntegritySha256 = ComputeIntegrity(document);
            Validate(document);
            return true;
        }

        private bool MigrateV2(Document document)
        {
            if (document == null || document.Version != IdentityBoundVersion
                || document.Revision == long.MaxValue) return false;
            foreach (Entry entry in document.Entries)
            {
                if (entry == null) return false;
                if (entry.ToolResults == null) entry.ToolResults = new List<string>();
            }
            document.Version = CurrentVersion;
            document.Revision++;
            document.IntegritySha256 = ComputeIntegrity(document);
            Validate(document);
            return true;
        }

        private bool MigrateV3(Document document)
        {
            if (document == null || document.Version != IntegrityBoundVersion
                || document.Revision == long.MaxValue) return false;
            foreach (Entry entry in document.Entries)
            {
                if (entry == null) return false;
                if (entry.ToolResults == null) entry.ToolResults = new List<string>();
            }
            document.Version = CurrentVersion;
            document.Revision++;
            document.IntegritySha256 = ComputeIntegrity(document);
            Validate(document);
            return true;
        }

        private string LegacyAttemptId(Entry entry, int index)
        {
            string source = _worldId + "\n" + _taiwuId + "\n" + _groupId + "\n"
                + (entry?.ExchangeId ?? string.Empty) + "\n" + (entry?.NpcId ?? 0) + "\n"
                + (entry?.UpdatedUtcTicks ?? 0) + "\n" + index;
            byte[] digest;
            using (var sha = SHA256.Create()) digest = sha.ComputeHash(StrictUtf8.GetBytes(source));
            var text = new StringBuilder("legacy-v1-");
            for (int i = 0; i < 16; i++) text.Append(digest[i].ToString("x2"));
            return text.ToString();
        }

        private static string EntryKey(Entry entry)
            => entry.NpcId + "\n" + entry.ExchangeId + "\n" + (entry.AttemptId ?? "\0");

        private static bool SameDocument(Document left, Document right)
        {
            if (left == null || right == null || left.Version != right.Version
                || left.WorldId != right.WorldId || left.TaiwuId != right.TaiwuId
                || left.Revision != right.Revision
                || !string.Equals(left.IntegritySha256, right.IntegritySha256, StringComparison.Ordinal)
                || !string.Equals(left.GroupId, right.GroupId, StringComparison.Ordinal)
                || left.Entries == null || right.Entries == null
                || left.Entries.Count != right.Entries.Count) return false;
            for (int i = 0; i < left.Entries.Count; i++)
                if (!SameEntry(left.Entries[i], right.Entries[i])) return false;
            return true;
        }

        private static bool SameEntry(Entry left, Entry right)
            => left != null && right != null
                && left.NpcId == right.NpcId && left.WorldDate == right.WorldDate
                && left.UpdatedUtcTicks == right.UpdatedUtcTicks
                && string.Equals(left.ExchangeId, right.ExchangeId, StringComparison.Ordinal)
                && string.Equals(left.AttemptId, right.AttemptId, StringComparison.Ordinal)
                && string.Equals(left.PlayerInput, right.PlayerInput, StringComparison.Ordinal)
                && string.Equals(left.State, right.State, StringComparison.Ordinal)
                && SameStrings(left.MemoryIds, right.MemoryIds)
                && SameStrings(left.Actions, right.Actions)
                && SameOptionalStrings(left.ToolResults, right.ToolResults)
                && SameStrings(left.OperationIds, right.OperationIds);

        private static bool SameOptionalStrings(List<string> left, List<string> right)
        {
            if (left == null || right == null) return left == null && right == null;
            return SameStrings(left, right);
        }

        private static bool SameStrings(List<string> left, List<string> right)
        {
            if (left == null || right == null || left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private static Document ReadStrictDocument(string path)
        {
            long expectedLength = new FileInfo(path).Length;
            if (expectedLength <= 0 || expectedLength > MaxDocumentBytes)
                throw new InvalidDataException("invalid_document_size");
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.LongLength != expectedLength) throw new IOException("document_changed_while_reading");
            string json = StrictUtf8.GetString(bytes);
            var root = JObject.Parse(json, new JsonLoadSettings
            {
                CommentHandling = CommentHandling.Ignore,
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
            });
            ValidateSerializedShape(root);
            return root.ToObject<Document>(JsonSerializer.Create(StrictJson));
        }

        private static void ValidateSerializedShape(JObject root)
        {
            if (root == null) throw new InvalidDataException("null_document");
            RejectCaseInsensitiveDuplicates(root);
            JToken versionToken = root["Version"];
            JToken entriesToken = root["Entries"];
            if (versionToken?.Type != JTokenType.Integer || entriesToken?.Type != JTokenType.Array)
                throw new InvalidDataException("missing_document_shape");
            int version = versionToken.Value<int>();
            if (version != 1 && version != IdentityBoundVersion
                && version != IntegrityBoundVersion && version != CurrentVersion)
                throw new InvalidDataException("unsupported_version");
            foreach (JToken token in (JArray)entriesToken)
            {
                var entry = token as JObject;
                if (entry == null) throw new InvalidDataException("entry_not_object");
                RejectCaseInsensitiveDuplicates(entry);
                RequireType(entry, "ExchangeId", JTokenType.String);
                if (version >= IdentityBoundVersion) RequireType(entry, "AttemptId", JTokenType.String);
                RequireType(entry, "NpcId", JTokenType.Integer);
                RequireType(entry, "PlayerInput", JTokenType.String);
                RequireType(entry, "WorldDate", JTokenType.Integer);
                RequireType(entry, "State", JTokenType.String);
                RequireType(entry, "MemoryIds", JTokenType.Array);
                RequireType(entry, "Actions", JTokenType.Array);
                if (version >= CurrentVersion) RequireType(entry, "ToolResults", JTokenType.Array);
                if (version >= IdentityBoundVersion) RequireType(entry, "OperationIds", JTokenType.Array);
                RequireType(entry, "UpdatedUtcTicks", JTokenType.Integer);
            }
            if (version >= IntegrityBoundVersion) RequireType(root, "IntegritySha256", JTokenType.String);
        }

        private static void RequireType(JObject value, string name, JTokenType type)
        {
            JProperty property = value.Property(name, StringComparison.Ordinal);
            if (property == null || property.Value == null || property.Value.Type != type)
                throw new InvalidDataException("missing_or_invalid_" + name);
        }

        private static void RejectCaseInsensitiveDuplicates(JObject value)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JProperty property in value.Properties())
                if (!names.Add(property.Name))
                    throw new InvalidDataException("case_insensitive_duplicate_property");
        }

        private static void WriteDurable(string path, string content)
        {
            byte[] bytes = StrictUtf8.GetBytes(content ?? string.Empty);
            if (bytes.Length <= 0 || bytes.Length > MaxDocumentBytes)
                throw new InvalidDataException("invalid_document_size");
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private static string ComputeIntegrity(Document document)
        {
            if (document == null) return null;
            try
            {
                JObject value = JObject.FromObject(document);
                value.Remove("IntegritySha256");
                // v3 digests were produced before Entry.ToolResults existed. Json.NET
                // materializes that missing property as null on the new CLR type; remove it
                // again while validating the old schema so the historical canonical bytes
                // remain unchanged. The v4 migration then adds an explicit empty array and
                // signs the complete current schema.
                if (document.Version < CurrentVersion && value["Entries"] is JArray legacyEntries)
                    foreach (JToken token in legacyEntries)
                        (token as JObject)?.Remove("ToolResults");
                byte[] digest;
                using (var sha = SHA256.Create())
                    digest = sha.ComputeHash(StrictUtf8.GetBytes(value.ToString(Formatting.None)));
                var result = new StringBuilder(64);
                foreach (byte b in digest) result.Append(b.ToString("x2"));
                return result.ToString();
            }
            catch { return null; }
        }

        private static bool ValidSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64) return false;
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        private static Entry Find(List<Entry> entries, string exchangeId, string attemptId, int npcId)
            => entries?.Find(entry => entry != null && entry.NpcId == npcId
                && string.Equals(entry.AttemptId ?? string.Empty, attemptId ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(entry.ExchangeId, exchangeId, StringComparison.Ordinal));

        private static List<string> Distinct(IEnumerable<string> values)
        {
            var result = new List<string>();
            if (values == null) return result;
            foreach (string value in values)
                if (!string.IsNullOrWhiteSpace(value) && !result.Contains(value)) result.Add(value);
            return result;
        }

        private static List<Entry> CloneEntries(IEnumerable<Entry> entries)
        {
            var result = new List<Entry>();
            if (entries == null) return result;
            foreach (var entry in entries)
            {
                if (entry == null) continue;
                result.Add(new Entry
                {
                    ExchangeId = entry.ExchangeId,
                    AttemptId = entry.AttemptId,
                    NpcId = entry.NpcId,
                    PlayerInput = entry.PlayerInput,
                    WorldDate = entry.WorldDate,
                    State = entry.State,
                    MemoryIds = entry.MemoryIds == null ? new List<string>() : new List<string>(entry.MemoryIds),
                    Actions = entry.Actions == null ? new List<string>() : new List<string>(entry.Actions),
                    ToolResults = entry.ToolResults == null ? new List<string>() : new List<string>(entry.ToolResults),
                    OperationIds = entry.OperationIds == null ? new List<string>() : new List<string>(entry.OperationIds),
                    UpdatedUtcTicks = entry.UpdatedUtcTicks,
                });
            }
            return result;
        }

        private static object Gate(string path)
        {
            lock (GatesLock)
            {
                if (!Gates.TryGetValue(path, out var gate))
                {
                    gate = new object();
                    Gates[path] = gate;
                }
                return gate;
            }
        }
    }
}
