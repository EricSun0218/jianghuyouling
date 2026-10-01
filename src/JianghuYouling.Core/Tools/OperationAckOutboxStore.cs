using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Tools
{
    /// <summary>
    /// 副作用 ACK 的纯文件 outbox。调用方只在后端 ACK 成功后移除条目；进程崩溃、ACK
    /// 丢包或世界切换都只会留下可重放的 ACK，不会保存、也绝不会重放原 mutation。
    /// </summary>
    public static class OperationAckOutboxStore
    {
        private const int CurrentVersion = 3;
        private const int MaxDocumentBytes = 4 * 1024 * 1024;
        private const int MaxEntries = 32768;
        private static readonly object Gate = new object();
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly JsonSerializerSettings StrictJson = new JsonSerializerSettings
        {
            MissingMemberHandling = MissingMemberHandling.Error,
        };

        private sealed class Document
        {
            [JsonProperty(Required = Required.Always)]
            public int Version { get; set; } = CurrentVersion;
            [JsonProperty(Required = Required.Always)]
            public uint WorldId { get; set; }
            [JsonProperty(Required = Required.Always)]
            public long Revision { get; set; }
            [JsonProperty(Required = Required.Always)]
            public string IntegritySha256 { get; set; }
            [JsonProperty(Required = Required.Always)]
            public List<Entry> Entries { get; set; } = new List<Entry>();
        }

        private sealed class Entry
        {
            [JsonProperty(Required = Required.Always)]
            public string OperationId { get; set; }
            [JsonProperty(Required = Required.Always)]
            public uint WorldId { get; set; }
            [JsonProperty(Required = Required.Always)]
            public int TaiwuId { get; set; }
            [JsonProperty(Required = Required.Always)]
            public bool LegacyUnbound { get; set; }
            [JsonProperty(Required = Required.Always)]
            public long EnqueuedUtcTicks { get; set; }
        }

        private sealed class LegacyDocument
        {
            [JsonProperty(Required = Required.Always)] public int Version { get; set; }
            [JsonProperty(Required = Required.Always)] public uint WorldId { get; set; }
            [JsonProperty(Required = Required.Always)] public long Revision { get; set; }
            [JsonProperty(Required = Required.Always)] public List<LegacyEntry> Entries { get; set; }
        }

        private sealed class LegacyEntry
        {
            [JsonProperty(Required = Required.Always)] public string OperationId { get; set; }
            [JsonProperty(Required = Required.Always)] public long EnqueuedUtcTicks { get; set; }
        }

        private sealed class LegacyV2Document
        {
            [JsonProperty(Required = Required.Always)] public int Version { get; set; }
            [JsonProperty(Required = Required.Always)] public uint WorldId { get; set; }
            [JsonProperty(Required = Required.Always)] public long Revision { get; set; }
            [JsonProperty(Required = Required.Always)] public List<Entry> Entries { get; set; }
        }

        public sealed class SnapshotEntry
        {
            public string OperationId { get; internal set; }
            public uint WorldId { get; internal set; }
            public int TaiwuId { get; internal set; }
            public bool LegacyUnbound { get; internal set; }
        }

        public sealed class Snapshot
        {
            public bool Reliable { get; internal set; }
            public bool HadCandidates { get; internal set; }
            public uint WorldId { get; internal set; }
            public long Revision { get; internal set; }
            public string SourcePath { get; internal set; }
            public string Error { get; internal set; }
            public List<string> OperationIds { get; internal set; } = new List<string>();
            public List<SnapshotEntry> Entries { get; internal set; } = new List<SnapshotEntry>();
            public List<string> LegacyUnboundOperationIds { get; internal set; } = new List<string>();
        }

        public static Snapshot Load(string mainPath, uint expectedWorldId)
        {
            lock (Gate) return ToSnapshot(LoadLocked(mainPath, expectedWorldId));
        }

        public static string BuildWorldIsolatedPath(string root, uint worldId)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("root is required", nameof(root));
            if (worldId == 0) throw new ArgumentOutOfRangeException(nameof(worldId));
            return Path.Combine(root, "Worlds", "World_" + worldId, "Rpc",
                "operation_ack_outbox_" + worldId + ".json");
        }

        public static bool TryEnqueue(string mainPath, uint worldId, int taiwuId, string operationId, out bool added)
        {
            added = false;
            operationId = NormalizeOperationId(operationId);
            if (!ValidRequest(mainPath, worldId, taiwuId, operationId)) return false;
            lock (Gate)
            {
                LoadResult loaded = LoadLocked(mainPath, worldId);
                if (!loaded.Reliable) return false; // 全部候选损坏时绝不以“空队列”覆盖恢复证据。
                foreach (Entry entry in loaded.Document.Entries)
                {
                    if (!string.Equals(entry.OperationId, operationId, StringComparison.Ordinal)) continue;
                    if (!entry.LegacyUnbound)
                        return entry.WorldId == worldId && entry.TaiwuId == taiwuId;

                    // Startup replay never guesses a v1 entry's Taiwu. Only a durable caller
                    // re-enqueuing this exact operation may bind this one legacy record.
                    Document migrated = Clone(loaded.Document);
                    Entry legacy = migrated.Entries.Find(e => e != null
                        && string.Equals(e.OperationId, operationId, StringComparison.Ordinal));
                    if (legacy == null || !TryAdvanceRevision(migrated)) return false;
                    legacy.WorldId = worldId;
                    legacy.TaiwuId = taiwuId;
                    legacy.LegacyUnbound = false;
                    return CommitLocked(mainPath, migrated);
                }

                Document next = Clone(loaded.Document);
                if (!TryAdvanceRevision(next)) return false;
                next.Entries.Add(new Entry
                {
                    OperationId = operationId,
                    WorldId = worldId,
                    TaiwuId = taiwuId,
                    LegacyUnbound = false,
                    EnqueuedUtcTicks = DateTime.UtcNow.Ticks,
                });
                if (!CommitLocked(mainPath, next)) return false;
                added = true;
                return true;
            }
        }

        /// <summary>只在权威 ACK 成功后调用。条目不存在视为幂等成功。</summary>
        public static bool TryRemoveAcknowledged(string mainPath, uint worldId, int taiwuId,
            string operationId, out bool removed)
        {
            removed = false;
            operationId = NormalizeOperationId(operationId);
            if (!ValidRequest(mainPath, worldId, taiwuId, operationId)) return false;
            lock (Gate)
            {
                LoadResult loaded = LoadLocked(mainPath, worldId);
                if (!loaded.Reliable) return false;
                foreach (Entry existing in loaded.Document.Entries)
                    if (existing != null && string.Equals(existing.OperationId, operationId, StringComparison.Ordinal)
                        && (existing.LegacyUnbound || existing.WorldId != worldId || existing.TaiwuId != taiwuId))
                        return false;
                Document next = Clone(loaded.Document);
                int count = next.Entries.RemoveAll(e => e != null
                    && !e.LegacyUnbound && e.WorldId == worldId && e.TaiwuId == taiwuId
                    && string.Equals(e.OperationId, operationId, StringComparison.Ordinal));
                if (count == 0) return true;
                if (!TryAdvanceRevision(next)) return false;
                if (!CommitLocked(mainPath, next)) return false;
                removed = true;
                return true;
            }
        }

        public static bool IsValidOperationId(string operationId)
        {
            operationId = (operationId ?? "").Trim();
            if (operationId.Length != 32) return false;
            for (int i = 0; i < operationId.Length; i++)
            {
                char c = operationId[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        private sealed class LoadResult
        {
            public bool Reliable;
            public bool HadCandidates;
            public Document Document;
            public string SourcePath;
            public string Error;
        }

        private static LoadResult LoadLocked(string mainPath, uint expectedWorldId)
        {
            Document empty = NewDocument(expectedWorldId);
            if (string.IsNullOrWhiteSpace(mainPath) || expectedWorldId == 0)
                return new LoadResult { Reliable = false, Document = empty, Error = "invalid_scope" };

            string tmpPath = mainPath + ".tmp";
            string bakPath = mainPath + ".bak";
            bool mainExists = File.Exists(mainPath);
            bool tmpExists = File.Exists(tmpPath);
            bool bakExists = File.Exists(bakPath);
            bool hadCandidates = mainExists || tmpExists || bakExists;
            var errors = new List<string>();

            if (!hadCandidates)
                return new LoadResult { Reliable = true, Document = empty, HadCandidates = false };

            // Main is the last-published state. A valid main is authoritative even when
            // an interrupted future write left a higher-revision tmp or bak behind.
            if (mainExists)
            {
                try
                {
                    bool needsMigration;
                    Document main = ReadStrictDocument(mainPath, out needsMigration);
                    Validate(main, expectedWorldId);
                    if (needsMigration)
                    {
                        // A legacy main is integrity-free. If both independent replicas
                        // are already current, identical, and strictly newer, they prove a
                        // completed migration and must win over the rolled-back main.
                        try
                        {
                            bool replicaTmpNeedsMigration, replicaBakNeedsMigration;
                            Document currentTmp = ReadStrictDocument(tmpPath, out replicaTmpNeedsMigration);
                            Document currentBak = ReadStrictDocument(bakPath, out replicaBakNeedsMigration);
                            Validate(currentTmp, expectedWorldId);
                            Validate(currentBak, expectedWorldId);
                            if (!replicaTmpNeedsMigration && !replicaBakNeedsMigration
                                && currentTmp.Revision > main.Revision
                                && SameDocument(currentTmp, currentBak))
                            {
                                Document quorumRecovered = Clone(currentTmp);
                                if (!CommitLocked(mainPath, quorumRecovered))
                                    return new LoadResult
                                    {
                                        Reliable = false, Document = empty, HadCandidates = true,
                                        Error = "current_replica_recovery_failed",
                                    };
                                return new LoadResult
                                {
                                    Reliable = true, HadCandidates = true, Document = quorumRecovered,
                                    SourcePath = mainPath,
                                };
                            }
                        }
                        catch
                        {
                            // A single or invalid secondary cannot overrule a valid main;
                            // continue with the normal legacy migration below.
                        }
                        if (!TryAdvanceRevision(main) || !CommitLocked(mainPath, main))
                            return new LoadResult
                            {
                                Reliable = false, Document = empty, HadCandidates = true,
                                Error = "document_migration_failed",
                            };
                    }
                    else
                    {
                        // A published current main is authoritative, but it is not a
                        // durable outbox barrier until both same-revision replicas are
                        // readable and semantically identical. Repair a partial commit
                        // before duplicate enqueue is allowed to report success.
                        bool replicasMatch = false;
                        try
                        {
                            bool tmpMigrated, bakMigrated;
                            Document currentTmp = ReadStrictDocument(tmpPath, out tmpMigrated);
                            Document currentBak = ReadStrictDocument(bakPath, out bakMigrated);
                            Validate(currentTmp, expectedWorldId);
                            Validate(currentBak, expectedWorldId);
                            replicasMatch = !tmpMigrated && !bakMigrated
                                && SameDocument(main, currentTmp) && SameDocument(main, currentBak);
                        }
                        catch { }
                        if (!replicasMatch && !CommitLocked(mainPath, Clone(main)))
                            return new LoadResult
                            {
                                Reliable = false, Document = empty, HadCandidates = true,
                                Error = "replica_repair_failed",
                            };
                    }
                    return new LoadResult
                    {
                        Reliable = true, HadCandidates = true, Document = main,
                        SourcePath = mainPath,
                    };
                }
                catch (Exception e) { errors.Add(Path.GetFileName(mainPath) + ":" + e.GetType().Name); }
            }

            // No secondary is independently authoritative. If main is absent or corrupt,
            // recovery requires both tmp and bak to attest the exact same revision/content.
            if (!tmpExists || !bakExists)
            {
                if (!tmpExists) errors.Add(Path.GetFileName(tmpPath) + ":missing");
                if (!bakExists) errors.Add(Path.GetFileName(bakPath) + ":missing");
                return new LoadResult
                {
                    Reliable = false, Document = empty, HadCandidates = true,
                    Error = errors.Count == 0 ? "recovery_quorum_missing" : string.Join(";", errors),
                };
            }

            Document tmp = null;
            Document bak = null;
            bool tmpNeedsMigration = false;
            bool bakNeedsMigration = false;
            try
            {
                tmp = ReadStrictDocument(tmpPath, out tmpNeedsMigration);
                Validate(tmp, expectedWorldId);
            }
            catch (Exception e) { tmp = null; errors.Add(Path.GetFileName(tmpPath) + ":" + e.GetType().Name); }
            try
            {
                bak = ReadStrictDocument(bakPath, out bakNeedsMigration);
                Validate(bak, expectedWorldId);
            }
            catch (Exception e) { bak = null; errors.Add(Path.GetFileName(bakPath) + ":" + e.GetType().Name); }

            if (tmp == null || bak == null || tmp.Revision != bak.Revision || !SameDocument(tmp, bak))
                return new LoadResult
                {
                    Reliable = false, Document = empty, HadCandidates = true,
                    Error = errors.Count == 0 ? "recovery_quorum_mismatch" : string.Join(";", errors),
                };

            Document recovered = Clone(tmp);
            if ((tmpNeedsMigration || bakNeedsMigration) && !TryAdvanceRevision(recovered))
                return new LoadResult
                {
                    Reliable = false, Document = empty, HadCandidates = true,
                    Error = "document_migration_failed",
                };
            if (!CommitLocked(mainPath, recovered))
                return new LoadResult
                {
                    Reliable = false, Document = empty, HadCandidates = true,
                    Error = "recovery_commit_failed",
                };
            return new LoadResult
            {
                Reliable = true, HadCandidates = true, Document = recovered,
                SourcePath = mainPath, Error = errors.Count == 0 ? null : string.Join(";", errors),
            };
        }

        private static Document NewDocument(uint worldId)
        {
            var document = new Document { WorldId = worldId, Revision = 0 };
            StampIntegrity(document);
            return document;
        }

        private static void StampIntegrity(Document document)
        {
            if (document == null) return;
            document.Version = CurrentVersion;
            // Json.NET enforces Required.Always during JObject.FromObject. Seed a
            // non-null placeholder before hashing; ComputeIntegrity removes the field,
            // so the placeholder can never influence the digest.
            if (document.IntegritySha256 == null) document.IntegritySha256 = new string('0', 64);
            document.IntegritySha256 = ComputeIntegrity(document);
        }

        private static string ComputeIntegrity(Document document)
        {
            if (document == null) return null;
            try
            {
                JObject value = JObject.FromObject(document);
                value.Remove("IntegritySha256");
                byte[] digest;
                using (var sha = SHA256.Create())
                    digest = sha.ComputeHash(StrictUtf8.GetBytes(value.ToString(Formatting.None)));
                var result = new StringBuilder(64);
                foreach (byte b in digest) result.Append(b.ToString("x2"));
                return result.ToString();
            }
            catch { return null; }
        }

        private static bool IsSha256Hex(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64) return false;
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        private static bool SameDocument(Document left, Document right)
        {
            return left != null && right != null
                && left.Version == right.Version
                && left.WorldId == right.WorldId
                && left.Revision == right.Revision
                && string.Equals(left.IntegritySha256, right.IntegritySha256, StringComparison.Ordinal)
                && SameEntries(left.Entries, right.Entries);
        }

        private static Document ConvertV2(LegacyV2Document legacy)
        {
            if (legacy == null || legacy.Version != 2 || legacy.Entries == null)
                throw new InvalidDataException("invalid_v2_document");
            var migrated = new Document
            {
                WorldId = legacy.WorldId,
                Revision = legacy.Revision,
                Entries = CloneEntries(legacy.Entries),
            };
            StampIntegrity(migrated);
            return migrated;
        }

        private static void Validate(Document document, uint expectedWorldId)
        {
            if (document == null) throw new InvalidDataException("null_document");
            if (document.Version != CurrentVersion) throw new InvalidDataException("unsupported_version");
            if (document.WorldId != expectedWorldId || document.WorldId == 0) throw new InvalidDataException("world_mismatch");
            if (document.Revision < 0 || document.Entries == null || document.Entries.Count > MaxEntries)
                throw new InvalidDataException("invalid_shape");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Entry entry in document.Entries)
            {
                if (entry == null || !IsValidOperationId(entry.OperationId) || !seen.Add(entry.OperationId))
                    throw new InvalidDataException("invalid_or_duplicate_operation_id");
                if (entry.WorldId != expectedWorldId
                    || (entry.LegacyUnbound ? entry.TaiwuId != 0 : entry.TaiwuId <= 0))
                    throw new InvalidDataException("invalid_operation_identity");
                if (entry.EnqueuedUtcTicks <= 0) throw new InvalidDataException("invalid_enqueue_time");
            }
            string computed = ComputeIntegrity(document);
            if (!IsSha256Hex(document.IntegritySha256)
                || !string.Equals(document.IntegritySha256, computed, StringComparison.Ordinal))
                throw new InvalidDataException("integrity_mismatch");
        }

        private static bool CommitLocked(string mainPath, Document next)
        {
            string tmp = mainPath + ".tmp";
            string promote = mainPath + ".promote";
            try
            {
                StampIntegrity(next);
                Validate(next, next.WorldId);
                string directory = Path.GetDirectoryName(mainPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                string json = JsonConvert.SerializeObject(next, Formatting.Indented);
                WriteDurable(tmp, json);
                // 写后先严格解析 tmp；坏写绝不触碰当前 main/bak。
                Document staged = ReadStrictDocument(tmp);
                Validate(staged, next.WorldId);
                if (!SameDocument(staged, next))
                    throw new InvalidDataException("staged_semantic_mismatch");

                // Keep tmp as a same-revision recovery replica. A second independently
                // flushed source is consumed by File.Replace/Move to publish main.
                WriteDurable(promote, json);
                Document promotion = ReadStrictDocument(promote);
                Validate(promotion, next.WorldId);
                if (!SameDocument(promotion, next))
                    throw new InvalidDataException("promotion_semantic_mismatch");

                if (File.Exists(mainPath)) File.Replace(promote, mainPath, mainPath + ".bak", true);
                else File.Move(promote, mainPath);

                // 只有主文件可严格读回、revision/条目完全一致才算可靠提交。
                Document committed = ReadStrictDocument(mainPath);
                Validate(committed, next.WorldId);
                if (!SameDocument(committed, next))
                    throw new InvalidDataException("committed_semantic_mismatch");

                // File.Replace initially places the previous main in bak. Do not report
                // success until all three durable replicas contain the new revision.
                WriteDurable(mainPath + ".bak", json);
                Document replica = ReadStrictDocument(tmp);
                Document backup = ReadStrictDocument(mainPath + ".bak");
                Validate(replica, next.WorldId);
                Validate(backup, next.WorldId);
                return SameDocument(committed, next)
                    && SameDocument(replica, next)
                    && SameDocument(backup, next);
            }
            catch
            {
                // 保留有效 tmp 作为崩溃恢复候选；若 tmp 本身坏了也不覆盖 main/bak。
                return false;
            }
        }

        private static bool SameEntries(List<Entry> left, List<Entry> right)
        {
            if (left == null || right == null || left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
                if (left[i] == null || right[i] == null
                    || !string.Equals(left[i].OperationId, right[i].OperationId, StringComparison.Ordinal)
                    || left[i].WorldId != right[i].WorldId
                    || left[i].TaiwuId != right[i].TaiwuId
                    || left[i].LegacyUnbound != right[i].LegacyUnbound
                    || left[i].EnqueuedUtcTicks != right[i].EnqueuedUtcTicks) return false;
            return true;
        }

        private static void WriteDurable(string path, string content)
        {
            byte[] bytes = StrictUtf8.GetBytes(content ?? "");
            if (bytes.Length <= 0 || bytes.Length > MaxDocumentBytes)
                throw new InvalidDataException("invalid_document_size");
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                // Flush through the OS cache before tmp is promoted. A crash can leave main, tmp or bak,
                // but must not leave a reported-success entry that existed only in managed buffers.
                stream.Flush(true);
            }
        }

        private static Document DeserializeStrict(string json, out bool needsMigration)
        {
            needsMigration = false;
            JObject root = JObject.Parse(json ?? "", new JsonLoadSettings
            {
                CommentHandling = CommentHandling.Ignore,
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
            });
            RejectCaseInsensitiveDuplicateProperties(root);
            JToken versionToken = root["Version"];
            if (versionToken == null || versionToken.Type != JTokenType.Integer)
                throw new InvalidDataException("missing_version");
            int version = versionToken.Value<int>();
            if (version == CurrentVersion)
                return root.ToObject<Document>(JsonSerializer.Create(StrictJson));
            if (version == 2)
            {
                needsMigration = true;
                return ConvertV2(root.ToObject<LegacyV2Document>(JsonSerializer.Create(StrictJson)));
            }
            if (version != 1) throw new InvalidDataException("unsupported_version");

            LegacyDocument legacy = root.ToObject<LegacyDocument>(JsonSerializer.Create(StrictJson));
            if (legacy == null || legacy.Version != 1 || legacy.WorldId == 0 || legacy.Revision < 0
                || legacy.Entries == null || legacy.Entries.Count > MaxEntries)
                throw new InvalidDataException("invalid_legacy_document");
            var migrated = new Document
            {
                Version = CurrentVersion,
                WorldId = legacy.WorldId,
                Revision = legacy.Revision,
                Entries = new List<Entry>(),
            };
            foreach (LegacyEntry entry in legacy.Entries)
            {
                if (entry == null) throw new InvalidDataException("invalid_legacy_entry");
                migrated.Entries.Add(new Entry
                {
                    OperationId = entry.OperationId,
                    WorldId = legacy.WorldId,
                    TaiwuId = 0,
                    LegacyUnbound = true,
                    EnqueuedUtcTicks = entry.EnqueuedUtcTicks,
                });
            }
            StampIntegrity(migrated);
            needsMigration = true;
            return migrated;
        }

        private static void RejectCaseInsensitiveDuplicateProperties(JToken token)
        {
            if (token is JObject obj)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JProperty property in obj.Properties())
                {
                    if (!names.Add(property.Name))
                        throw new InvalidDataException("case_insensitive_duplicate_property");
                    RejectCaseInsensitiveDuplicateProperties(property.Value);
                }
                return;
            }
            if (token is JArray array)
                foreach (JToken child in array) RejectCaseInsensitiveDuplicateProperties(child);
        }

        private static Document ReadStrictDocument(string path)
        {
            bool ignored;
            return ReadStrictDocument(path, out ignored);
        }

        private static Document ReadStrictDocument(string path, out bool needsMigration)
        {
            long expectedLength = new FileInfo(path).Length;
            if (expectedLength <= 0 || expectedLength > MaxDocumentBytes)
                throw new InvalidDataException("invalid_document_size");
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.LongLength != expectedLength) throw new IOException("document_changed_while_reading");
            return DeserializeStrict(StrictUtf8.GetString(bytes), out needsMigration);
        }

        private static bool TryAdvanceRevision(Document document)
        {
            if (document == null || document.Revision < 0 || document.Revision == long.MaxValue) return false;
            document.Revision++;
            return true;
        }

        private static Document Clone(Document source)
        {
            var copy = new Document
            {
                Version = CurrentVersion,
                WorldId = source.WorldId,
                Revision = source.Revision,
                IntegritySha256 = source.IntegritySha256,
                Entries = CloneEntries(source.Entries),
            };
            return copy;
        }

        private static List<Entry> CloneEntries(IEnumerable<Entry> entries)
        {
            var copy = new List<Entry>();
            if (entries == null) return copy;
            foreach (Entry entry in entries)
                copy.Add(new Entry
                {
                    OperationId = entry.OperationId,
                    WorldId = entry.WorldId,
                    TaiwuId = entry.TaiwuId,
                    LegacyUnbound = entry.LegacyUnbound,
                    EnqueuedUtcTicks = entry.EnqueuedUtcTicks,
                });
            return copy;
        }

        private static Snapshot ToSnapshot(LoadResult loaded)
        {
            var snapshot = new Snapshot
            {
                Reliable = loaded.Reliable,
                HadCandidates = loaded.HadCandidates,
                WorldId = loaded.Document?.WorldId ?? 0,
                Revision = loaded.Document?.Revision ?? 0,
                SourcePath = loaded.SourcePath,
                Error = loaded.Error,
            };
            if (loaded.Document?.Entries != null)
                foreach (Entry entry in loaded.Document.Entries)
                    if (entry != null)
                    {
                        if (entry.LegacyUnbound) snapshot.LegacyUnboundOperationIds.Add(entry.OperationId);
                        else
                        {
                            snapshot.OperationIds.Add(entry.OperationId);
                            snapshot.Entries.Add(new SnapshotEntry
                            {
                                OperationId = entry.OperationId,
                                WorldId = entry.WorldId,
                                TaiwuId = entry.TaiwuId,
                                LegacyUnbound = false,
                            });
                        }
                    }
            return snapshot;
        }

        private static bool ValidRequest(string mainPath, uint worldId, int taiwuId, string operationId)
            => !string.IsNullOrWhiteSpace(mainPath) && worldId > 0 && taiwuId > 0
                && IsValidOperationId(operationId);

        private static string NormalizeOperationId(string operationId)
            => (operationId ?? "").Trim().ToLowerInvariant();
    }
}
