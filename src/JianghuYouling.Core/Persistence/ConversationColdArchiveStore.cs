using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using JianghuYouling.Core.Prompt;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Persistence
{
    /// <summary>
    /// Immutable cold archive for verbatim single-chat turns removed by rolling
    /// compaction. An archive append is considered committed only when both main
    /// and backup contain the exact validated revision. Callers must not delete
    /// live turns unless Append has returned true.
    /// </summary>
    public sealed class ConversationColdArchiveStore
    {
        private const int CurrentVersion = 1;
        private const int MaxDocumentBytes = 64 * 1024 * 1024;
        private const int MaxSegments = 4096;
        private const int MaxTurns = 100000;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly object GatesLock = new object();
        private static readonly Dictionary<string, object> Gates =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public sealed class Segment
        {
            public string SourceHash { get; set; }
            public string PriorSummary { get; set; }
            public string PriorSummarySourceHash { get; set; }
            public string Summary { get; set; }
            public long ArchivedUtcTicks { get; set; }
            public List<TalkTurn> Turns { get; set; } = new List<TalkTurn>();
        }

        private sealed class Document
        {
            public int Version { get; set; } = CurrentVersion;
            public uint WorldId { get; set; }
            public int TaiwuId { get; set; }
            public int NpcId { get; set; }
            public long Revision { get; set; }
            public List<Segment> Segments { get; set; } = new List<Segment>();
        }

        private readonly string _path;
        private readonly uint _worldId;
        private readonly int _taiwuId;
        private readonly int _npcId;
        private Document _document;

        private ConversationColdArchiveStore(string path, uint worldId, int taiwuId, int npcId,
            Document document, bool reliable)
        {
            _path = path;
            _worldId = worldId;
            _taiwuId = taiwuId;
            _npcId = npcId;
            _document = document ?? NewDocument(worldId, taiwuId, npcId);
            LoadReliable = reliable;
        }

        public bool LoadReliable { get; private set; }
        public string Path => _path;

        public static ConversationColdArchiveStore Load(string rootDirectory, uint worldId,
            int taiwuId, int npcId)
        {
            string path = BuildPath(rootDirectory, worldId, taiwuId, npcId);
            lock (Gate(path))
            {
                if (TryLoadLocked(path, worldId, taiwuId, npcId, out Document document,
                    out bool anyCandidate))
                    return new ConversationColdArchiveStore(path, worldId, taiwuId, npcId,
                        document, true);
                return new ConversationColdArchiveStore(path, worldId, taiwuId, npcId,
                    NewDocument(worldId, taiwuId, npcId), !anyCandidate);
            }
        }

        /// <summary>
        /// Export-only snapshot. Candidate arbitration is strictly read-only: no corrupt
        /// archive, replica repair or promotion is allowed while a background task is bound
        /// to a captured world directory.
        /// </summary>
        public static ConversationColdArchiveStore LoadReadOnly(string rootDirectory, uint worldId,
            int taiwuId, int npcId)
        {
            string path = BuildPath(rootDirectory, worldId, taiwuId, npcId);
            lock (Gate(path))
            {
                Func<string, bool> validator = raw => ValidateDocument(raw, worldId, taiwuId, npcId);
                if (DurableFileStore.TryReadRecoverableTextReadOnly(path, MaxDocumentBytes,
                    validator, out string json, out bool anyCandidate, out _))
                {
                    try
                    {
                        var document = JsonConvert.DeserializeObject<Document>(json);
                        if (document != null)
                            return new ConversationColdArchiveStore(path, worldId, taiwuId,
                                npcId, document, true);
                    }
                    catch { }
                }
                return new ConversationColdArchiveStore(path, worldId, taiwuId, npcId,
                    NewDocument(worldId, taiwuId, npcId), !anyCandidate);
            }
        }

        public bool Append(string sourceHash, string priorSummary, string priorSummarySourceHash,
            string summary, IList<TalkTurn> turns)
        {
            if (!LoadReliable || !IsSha256(sourceHash) || turns == null || turns.Count == 0
                || (!string.IsNullOrEmpty(priorSummarySourceHash) && !IsSha256(priorSummarySourceHash)))
                return false;

            var proposed = new Segment
            {
                SourceHash = sourceHash,
                PriorSummary = priorSummary,
                PriorSummarySourceHash = priorSummarySourceHash,
                Summary = summary,
                ArchivedUtcTicks = DateTime.UtcNow.Ticks,
                Turns = CloneTurns(turns),
            };

            lock (Gate(_path))
            {
                if (!TryLoadLocked(_path, _worldId, _taiwuId, _npcId,
                    out Document latest, out bool anyCandidate))
                {
                    if (anyCandidate) LoadReliable = false;
                    return false;
                }

                foreach (Segment existing in latest.Segments)
                    if (string.Equals(existing?.SourceHash, sourceHash, StringComparison.Ordinal))
                    {
                        // The same source can be summarized differently after a crash/retry.
                        // Idempotency is based on the verbatim source, never model prose.
                        if (!SameSource(existing, proposed)) return false;
                        _document = latest;
                        return true;
                    }

                if (latest.Segments.Count >= MaxSegments) return false;
                latest.Segments.Add(proposed);
                latest.Revision = Math.Max(0, latest.Revision) + 1;
                string json = JsonConvert.SerializeObject(latest, Formatting.Indented);
                Func<string, bool> validator = raw => ValidateDocument(raw, _worldId, _taiwuId, _npcId);
                if (!WriteReplicated(_path, json, validator)) return false;

                if (!TryLoadLocked(_path, _worldId, _taiwuId, _npcId,
                    out Document committed, out _) || committed.Revision != latest.Revision)
                    return false;
                _document = committed;
                return true;
            }
        }

        public IReadOnlyList<Segment> Snapshot()
        {
            lock (Gate(_path))
            {
                var result = new List<Segment>();
                if (_document?.Segments != null)
                    foreach (Segment segment in _document.Segments) result.Add(CloneSegment(segment));
                return result;
            }
        }

        /// <summary>Returns archived verbatim turns in append order for export or manual recovery.</summary>
        public List<TalkTurn> RecoverTurns()
        {
            var result = new List<TalkTurn>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (Segment segment in Snapshot())
            {
                if (segment?.Turns == null) continue;
                foreach (TalkTurn turn in segment.Turns)
                {
                    if (turn == null) continue;
                    if (!string.IsNullOrWhiteSpace(turn.Id) && !seenIds.Add(turn.Id)) continue;
                    result.Add(CloneTurn(turn));
                }
            }
            return result;
        }

        public static string ComputeSourceHash(string priorSummary, string priorSummarySourceHash,
            IList<TalkTurn> turns)
        {
            try
            {
                var source = new JObject
                {
                    ["priorSummary"] = priorSummary == null ? JValue.CreateNull() : new JValue(priorSummary),
                    ["priorSummarySourceHash"] = priorSummarySourceHash == null
                        ? JValue.CreateNull() : new JValue(priorSummarySourceHash),
                    ["turns"] = JArray.FromObject(CloneTurns(turns)),
                };
                byte[] digest;
                using (var sha = SHA256.Create())
                    digest = sha.ComputeHash(StrictUtf8.GetBytes(source.ToString(Formatting.None)));
                var value = new StringBuilder(64);
                foreach (byte b in digest) value.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return value.ToString();
            }
            catch { return null; }
        }

        public static bool Delete(string rootDirectory, uint worldId, int taiwuId, int npcId)
        {
            string path = BuildPath(rootDirectory, worldId, taiwuId, npcId);
            lock (Gate(path))
            {
                bool ok = true;
                // corrupt 归档虽不参与自动恢复，也必须先确认删净；否则返回失败时仍保留 main，
                // 让调用方可安全幂等重试。DurableFileStore 随后统一删净所有协议副本并最后删 main。
                try
                {
                    string parent = System.IO.Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                        foreach (string candidate in Directory.GetFiles(parent,
                            System.IO.Path.GetFileName(path) + ".corrupt-*"))
                        {
                            try { if (File.Exists(candidate)) File.Delete(candidate); }
                            catch { ok = false; }
                            try { if (File.Exists(candidate)) ok = false; }
                            catch { ok = false; }
                        }
                }
                catch { ok = false; }
                return ok && DurableFileStore.TryDeleteAllArtifacts(path);
            }
        }

        public static string BuildPath(string rootDirectory, uint worldId, int taiwuId, int npcId)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
                throw new ArgumentException("归档根目录不能为空", nameof(rootDirectory));
            if (worldId == 0) throw new ArgumentOutOfRangeException(nameof(worldId));
            if (taiwuId <= 0) throw new ArgumentOutOfRangeException(nameof(taiwuId));
            if (npcId < 0) throw new ArgumentOutOfRangeException(nameof(npcId));

            string root = System.IO.Path.GetFullPath(rootDirectory)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            string prefix = root + System.IO.Path.DirectorySeparatorChar;
            string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root,
                "ChatArchive_" + taiwuId.ToString(CultureInfo.InvariantCulture) + "_"
                + npcId.ToString(CultureInfo.InvariantCulture) + ".json"));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("归档路径越出受控目录");
            return path;
        }

        /// <summary>
        /// Preflights every recoverable archive replica before export allocates/parses it.
        /// Missing replicas are normal; access/I/O failures are distinguishable and must
        /// remain fail-closed at the caller.
        /// </summary>
        public static bool TryCheckReadBudget(string rootDirectory, uint worldId, int taiwuId,
            int npcId, long maxBytes, out bool withinBudget)
        {
            withinBudget = false;
            if (maxBytes <= 0) return false;
            try
            {
                string path = BuildPath(rootDirectory, worldId, taiwuId, npcId);
                foreach (string candidate in new[] { path, path + ".tmp", path + ".bak" })
                {
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(candidate);
                        if ((attributes & FileAttributes.Directory) != 0) return false;
                        long length = new FileInfo(candidate).Length;
                        if (length < 0 || length > maxBytes)
                        {
                            withinBudget = false;
                            return true;
                        }
                    }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                }
                withinBudget = true;
                return true;
            }
            catch { return false; }
        }

        private static bool TryLoadLocked(string path, uint worldId, int taiwuId, int npcId,
            out Document document, out bool anyCandidate)
        {
            document = null;
            Func<string, bool> validator = raw => ValidateDocument(raw, worldId, taiwuId, npcId);
            if (!DurableFileStore.TryReadRecoverableText(path, MaxDocumentBytes, validator,
                out string json, out anyCandidate, out _, preserveInvalidCandidates: true))
            {
                if (!anyCandidate)
                {
                    document = NewDocument(worldId, taiwuId, npcId);
                    return true;
                }
                return false;
            }
            if (!WriteReplicated(path, json, validator)) return false;
            try
            {
                document = JsonConvert.DeserializeObject<Document>(json);
                return document != null;
            }
            catch { document = null; return false; }
        }

        private static bool WriteReplicated(string path, string json, Func<string, bool> validator)
        {
            if (!DurableFileStore.TryWriteTextAtomic(path, json, MaxDocumentBytes, validator)) return false;
            if (!ExactReplica(path, json, validator) || !ExactReplica(path + ".bak", json, validator))
            {
                // A second identical atomic replace promotes the just-committed main
                // into bak, so both replicas now contain this immutable append.
                if (!DurableFileStore.TryWriteTextAtomic(path, json, MaxDocumentBytes, validator)) return false;
            }
            return ExactReplica(path, json, validator) && ExactReplica(path + ".bak", json, validator);
        }

        private static bool ExactReplica(string path, string expected, Func<string, bool> validator)
            => DurableFileStore.TryReadStrictUtf8(path, MaxDocumentBytes, out string actual)
               && string.Equals(actual, expected, StringComparison.Ordinal)
               && SafeValidate(validator, actual);

        private static bool ValidateDocument(string json, uint worldId, int taiwuId, int npcId)
        {
            if (!DurableFileStore.TryParseJsonStrict(json, 32, out JToken root)
                || !(root is JObject document)
                || !DurableFileStore.HasOnlyProperties(document, "Version", "WorldId", "TaiwuId",
                    "NpcId", "Revision", "Segments")) return false;
            if (!IntegerEquals(document["Version"], CurrentVersion)
                || !IntegerEquals(document["WorldId"], worldId)
                || !IntegerEquals(document["TaiwuId"], taiwuId)
                || !IntegerEquals(document["NpcId"], npcId)
                || !IntegerInRange(document["Revision"], 1, long.MaxValue)
                || !(document["Segments"] is JArray segments)
                || segments.Count == 0 || segments.Count > MaxSegments) return false;

            int totalTurns = 0;
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            foreach (JToken token in segments)
            {
                if (!(token is JObject segment)
                    || !DurableFileStore.HasOnlyProperties(segment, "SourceHash", "PriorSummary",
                        "PriorSummarySourceHash", "Summary", "ArchivedUtcTicks", "Turns")) return false;
                string sourceHash = segment["SourceHash"]?.Type == JTokenType.String
                    ? segment["SourceHash"].Value<string>() : null;
                string priorHash = segment["PriorSummarySourceHash"]?.Type == JTokenType.String
                    ? segment["PriorSummarySourceHash"].Value<string>() : null;
                if (!IsSha256(sourceHash) || !hashes.Add(sourceHash)
                    || (!string.IsNullOrEmpty(priorHash) && !IsSha256(priorHash))
                    || !DurableFileStore.IsBoundedString(segment["PriorSummary"], 262144)
                    || !DurableFileStore.IsBoundedString(segment["PriorSummarySourceHash"], 64)
                    || !DurableFileStore.IsBoundedString(segment["Summary"], 262144)
                    || !IntegerInRange(segment["ArchivedUtcTicks"], 1, long.MaxValue)
                    || !(segment["Turns"] is JArray turns) || turns.Count == 0) return false;
                totalTurns += turns.Count;
                if (totalTurns > MaxTurns) return false;
                foreach (JToken turnToken in turns)
                    if (!ValidTurn(turnToken)) return false;
            }
            return true;
        }

        private static bool ValidTurn(JToken token)
        {
            if (!(token is JObject turn)) return false;
            if (!DurableFileStore.HasOnlyProperties(turn, "Id", "ExchangeId", "FromPlayer",
                "Text", "Date", "LocationText", "ContactMode", "Kind", "MemoryIds", "Actions",
                "ToolResults", "ImageFileName")) return false;
            if (!DurableFileStore.IsBoundedString(turn["Id"], 128)
                || !DurableFileStore.IsBoundedString(turn["ExchangeId"], 128)
                || turn["FromPlayer"]?.Type != JTokenType.Boolean
                || !DurableFileStore.IsBoundedString(turn["Text"], 262144, false)
                || !DurableFileStore.IsBoundedString(turn["LocationText"], 4096)
                || !DurableFileStore.IsBoundedString(turn["ContactMode"], 128)
                || !DurableFileStore.IsBoundedString(turn["Kind"], 64)
                || !DurableFileStore.IsBoundedString(turn["ImageFileName"], ChatImageReference.MaxFileNameChars)
                || !IntegerInRange(turn["Date"], int.MinValue, int.MaxValue)) return false;
            if (turn["ImageFileName"]?.Type == JTokenType.String
                && !ChatImageReference.IsValid(turn["ImageFileName"].Value<string>())) return false;
            string kind = turn["Kind"]?.Type == JTokenType.String ? turn["Kind"].Value<string>() : null;
            if (!TalkTurnKinds.IsSupportedContextKind(kind)) return false;
            return ValidStringArray(turn["MemoryIds"], 4096, 1024)
                && ValidStringArray(turn["Actions"], 4096, 65536)
                && ValidStringArray(turn["ToolResults"], 4096, 65536);
        }

        private static bool ValidStringArray(JToken token, int maxItems, int maxChars)
        {
            if (token == null || token.Type == JTokenType.Null) return true;
            if (!(token is JArray values) || values.Count > maxItems) return false;
            foreach (JToken value in values)
                if (!DurableFileStore.IsBoundedString(value, maxChars, false)) return false;
            return true;
        }

        private static bool IntegerEquals(JToken token, long expected)
            => IntegerInRange(token, expected, expected);

        private static bool IntegerInRange(JToken token, long min, long max)
        {
            if (token?.Type != JTokenType.Integer) return false;
            try { long value = token.Value<long>(); return value >= min && value <= max; }
            catch { return false; }
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64) return false;
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        private static bool SameSource(Segment left, Segment right)
        {
            if (left == null || right == null
                || !string.Equals(left.PriorSummary, right.PriorSummary, StringComparison.Ordinal)
                || !string.Equals(left.PriorSummarySourceHash, right.PriorSummarySourceHash,
                    StringComparison.Ordinal)) return false;
            return JToken.DeepEquals(JToken.FromObject(left.Turns ?? new List<TalkTurn>()),
                JToken.FromObject(right.Turns ?? new List<TalkTurn>()));
        }

        private static Segment CloneSegment(Segment source)
            => source == null ? null : new Segment
            {
                SourceHash = source.SourceHash,
                PriorSummary = source.PriorSummary,
                PriorSummarySourceHash = source.PriorSummarySourceHash,
                Summary = source.Summary,
                ArchivedUtcTicks = source.ArchivedUtcTicks,
                Turns = CloneTurns(source.Turns),
            };

        private static List<TalkTurn> CloneTurns(IList<TalkTurn> source)
        {
            var result = new List<TalkTurn>();
            if (source != null)
                foreach (TalkTurn turn in source) if (turn != null) result.Add(CloneTurn(turn));
            return result;
        }

        private static TalkTurn CloneTurn(TalkTurn source)
            => source == null ? null : new TalkTurn
            {
                Id = source.Id,
                ExchangeId = source.ExchangeId,
                FromPlayer = source.FromPlayer,
                Text = source.Text,
                Date = source.Date,
                LocationText = source.LocationText,
                ContactMode = source.ContactMode,
                Kind = source.Kind,
                ImageFileName = source.ImageFileName,
                MemoryIds = source.MemoryIds == null ? null : new List<string>(source.MemoryIds),
                Actions = source.Actions == null ? null : new List<string>(source.Actions),
                ToolResults = source.ToolResults == null ? null : new List<string>(source.ToolResults),
            };

        private static Document NewDocument(uint worldId, int taiwuId, int npcId)
            => new Document
            {
                WorldId = worldId,
                TaiwuId = taiwuId,
                NpcId = npcId,
                Revision = 0,
            };

        private static bool SafeValidate(Func<string, bool> validator, string value)
        {
            try { return validator(value); }
            catch { return false; }
        }

        private static object Gate(string path)
        {
            string full = System.IO.Path.GetFullPath(path);
            lock (GatesLock)
            {
                if (!Gates.TryGetValue(full, out object gate))
                {
                    gate = new object();
                    Gates[full] = gate;
                }
                return gate;
            }
        }
    }
}
