using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using JianghuYouling.Core.Llm;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Persistence
{
    /// <summary>
    /// Small, crash-safe file primitive shared by Core and Frontend stores.
    /// The main file is the commit point. A valid main always wins; tmp/bak are
    /// considered only when main is absent or invalid. A write is committed once
    /// main has been atomically published and exactly read back; refreshing the
    /// current backup is best-effort after that commit point. Recovery is reported
    /// as successful only after main and a backup replica have both been durably
    /// rebuilt and semantically read back.
    /// </summary>
    public static class DurableFileStore
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly object GatesLock = new object();
        private static readonly Dictionary<string, object> Gates =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public static bool TryReadRecoverableText(string path, int maxBytes,
            Func<string, bool> semanticValidator, out string value, out bool anyCandidate,
            out string sourcePath, bool preserveInvalidCandidates = false)
        {
            value = null;
            sourcePath = null;
            anyCandidate = false;
            if (string.IsNullOrWhiteSpace(path) || maxBytes <= 0 || semanticValidator == null) return false;

            lock (Gate(path))
            {
                string main = Path.GetFullPath(path);
                // The main file is the commit point.  If it is damaged, the backup is
                // the last committed value and must win over a complete-but-uncommitted
                // tmp left by a write that never reached Replace/Move.  A valid tmp is
                // recoverable only when no committed replica survives (for example, a
                // first-ever write that crashed immediately before the final Move).
                string[] candidates = { main, main + ".bak", main + ".tmp" };
                string backupCandidate = null, stagedCandidate = null, mainCandidate = null;
                bool backupValid = File.Exists(main + ".bak")
                    && TryReadStrictUtf8(main + ".bak", maxBytes, out backupCandidate)
                    && SafeValidate(semanticValidator, backupCandidate);
                bool stagedValid = File.Exists(main + ".tmp")
                    && TryReadStrictUtf8(main + ".tmp", maxBytes, out stagedCandidate)
                    && SafeValidate(semanticValidator, stagedCandidate);
                bool mainValid = File.Exists(main)
                    && TryReadStrictUtf8(main, maxBytes, out mainCandidate)
                    && SafeValidate(semanticValidator, mainCandidate);
                // A post-commit backup-refresh failure deliberately preserves the exact current
                // value in tmp. If main is later damaged while the previous backup still exists,
                // neither surviving value alone proves which revision committed. Fail closed
                // instead of silently rolling back to bak or promoting an uncommitted tmp.
                if (!mainValid && backupValid && stagedValid
                    && !string.Equals(backupCandidate, stagedCandidate, StringComparison.Ordinal))
                {
                    anyCandidate = true;
                    return false;
                }
                foreach (string candidate in candidates)
                {
                    if (!File.Exists(candidate)) continue;
                    anyCandidate = true;
                    if (!TryReadStrictUtf8(candidate, maxBytes, out string parsed)
                        || !SafeValidate(semanticValidator, parsed)) continue;

                    if (preserveInvalidCandidates
                        && !TryArchiveInvalidCandidatesLocked(candidates, maxBytes, semanticValidator))
                        return false;

                    if (!string.Equals(candidate, main, StringComparison.OrdinalIgnoreCase))
                    {
                        // Do not claim a recovered value until there are two durable,
                        // independently readable committed copies again. The staged tmp
                        // must survive a failed re-commit here: when the candidate being
                        // recovered IS the tmp, deleting it on failure would destroy the
                        // only surviving replica.
                        if (!TryWriteTextAtomicLocked(main, parsed, maxBytes, semanticValidator,
                                preserveStagedTmpOnFailure: true)
                            || !TryWriteReplicaLocked(main + ".bak", parsed, maxBytes, semanticValidator))
                            return false;
                    }
                    else
                    {
                        // A replica from an older commit is valid JSON but is not valid recovery
                        // evidence for the current commit.  Keep main/bak byte-identical after
                        // every successful load so later main corruption cannot silently roll
                        // navigation, membership, memories or operation journals backwards.
                        bool backupCurrent = TryReadStrictUtf8(main + ".bak", maxBytes,
                                out string backup)
                            && string.Equals(backup, parsed, StringComparison.Ordinal)
                            && SafeValidate(semanticValidator, backup);
                        if (!backupCurrent && !TryWriteReplicaLocked(main + ".bak", parsed,
                            maxBytes, semanticValidator))
                        {
                            // Preserve exact current evidence until backup repair succeeds. This
                            // also makes a later stale-bak/current-tmp disagreement fail closed.
                            TryWriteReplicaLocked(main + ".tmp", parsed, maxBytes, semanticValidator);
                            return false;
                        }
                        // Only after the exact current backup is durable can an old crash-staged
                        // tmp be discarded without weakening recovery.
                        try { if (File.Exists(main + ".tmp")) File.Delete(main + ".tmp"); } catch { }
                    }

                    value = parsed;
                    sourcePath = main;
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Selects the same committed/recovery candidate order as
        /// <see cref="TryReadRecoverableText"/>, but never archives, deletes, promotes,
        /// repairs or rewrites any source file. Background export and diagnostics must use
        /// this path so a world switch cannot turn a read into an old-world mutation.
        /// </summary>
        public static bool TryReadRecoverableTextReadOnly(string path, int maxBytes,
            Func<string, bool> semanticValidator, out string value, out bool anyCandidate,
            out string sourcePath)
        {
            value = null;
            sourcePath = null;
            anyCandidate = false;
            if (string.IsNullOrWhiteSpace(path) || maxBytes <= 0 || semanticValidator == null) return false;

            lock (Gate(path))
            {
                string main = Path.GetFullPath(path);
                string backupPath = main + ".bak";
                string stagedPath = main + ".tmp";
                string backupCandidate = null, stagedCandidate = null, mainCandidate = null;
                bool backupValid = File.Exists(backupPath)
                    && TryReadStrictUtf8(backupPath, maxBytes, out backupCandidate)
                    && SafeValidate(semanticValidator, backupCandidate);
                bool stagedValid = File.Exists(stagedPath)
                    && TryReadStrictUtf8(stagedPath, maxBytes, out stagedCandidate)
                    && SafeValidate(semanticValidator, stagedCandidate);
                bool mainValid = File.Exists(main)
                    && TryReadStrictUtf8(main, maxBytes, out mainCandidate)
                    && SafeValidate(semanticValidator, mainCandidate);
                if (!mainValid && backupValid && stagedValid
                    && !string.Equals(backupCandidate, stagedCandidate, StringComparison.Ordinal))
                {
                    anyCandidate = true;
                    return false;
                }
                foreach (string candidate in new[] { main, main + ".bak", main + ".tmp" })
                {
                    if (!File.Exists(candidate)) continue;
                    anyCandidate = true;
                    if (!TryReadStrictUtf8(candidate, maxBytes, out string parsed)
                        || !SafeValidate(semanticValidator, parsed)) continue;
                    value = parsed;
                    sourcePath = candidate;
                    return true;
                }
                return false;
            }
        }

        public static bool TryWriteTextAtomic(string path, string value, int maxBytes,
            Func<string, bool> semanticValidator)
        {
            if (string.IsNullOrWhiteSpace(path) || maxBytes <= 0 || semanticValidator == null) return false;
            lock (Gate(path))
                return TryWriteTextAtomicLocked(Path.GetFullPath(path), value ?? string.Empty,
                    maxBytes, semanticValidator);
        }

        /// <summary>
        /// Deletes every file that can participate in this store's commit/recovery protocol.
        /// Recovery artifacts are removed and verified first; the authoritative main is the
        /// final deletion. If any artifact is locked or has become a directory, main is kept
        /// intact so a caller never reports a half-cleared durable record as successfully gone.
        /// </summary>
        public static bool TryDeleteAllArtifacts(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            lock (Gate(path))
            {
                string main;
                try { main = Path.GetFullPath(path); }
                catch { return false; }

                string[] artifacts =
                {
                    main + ".promote",
                    main + ".tmp.repair",
                    main + ".bak.repair",
                    main + ".recover",
                    main + ".tmp",
                    main + ".bak",
                };
                foreach (string artifact in artifacts)
                    if (!TryDeleteFileExactly(artifact)) return false;
                return TryDeleteFileExactly(main);
            }
        }

        public static bool TryReadStrictUtf8(string path, int maxBytes, out string value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(path) || maxBytes <= 0) return false;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < 0 || info.Length > maxBytes) return false;
                byte[] bytes = new byte[(int)info.Length];
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 4096, FileOptions.SequentialScan))
                {
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int read = stream.Read(bytes, offset, bytes.Length - offset);
                        if (read <= 0) return false;
                        offset += read;
                    }
                    if (stream.ReadByte() != -1) return false;
                }
                // Reject UTF-8 BOM as well as UTF-16/UTF-32. All persisted state has
                // one canonical encoding, which also makes exact readback meaningful.
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                    return false;
                value = StrictUtf8.GetString(bytes);
                return true;
            }
            catch { return false; }
        }

        public static bool TryParseJsonStrict(string json, int maxDepth, out JToken token)
        {
            token = null;
            if (json == null || maxDepth <= 0) return false;
            try
            {
                // Json.NET's JToken loader may discard comments inside objects even
                // with CommentHandling.Load. Scan the lexical stream first so strict
                // JSON never silently accepts JavaScript-style comments.
                using (var commentText = new StringReader(json))
                using (var commentReader = new JsonTextReader(commentText) { MaxDepth = maxDepth })
                    while (commentReader.Read())
                        if (commentReader.TokenType == JsonToken.Comment) return false;

                using (var sr = new StringReader(json))
                using (var reader = new StrictJsonTextReader(sr)
                {
                    DateParseHandling = DateParseHandling.None,
                    FloatParseHandling = FloatParseHandling.Decimal,
                    MaxDepth = maxDepth,
                    SupportMultipleContent = true,
                    CloseInput = true,
                })
                {
                    if (!reader.Read()) return false;
                    if (reader.TokenType == JsonToken.Comment) return false;
                    token = JToken.Load(reader, new JsonLoadSettings
                    {
                        CommentHandling = CommentHandling.Ignore,
                        LineInfoHandling = LineInfoHandling.Ignore,
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                    });
                    if (reader.Read()) { token = null; return false; }
                    return true;
                }
            }
            catch { token = null; return false; }
        }

        public static bool HasOnlyProperties(JObject value, params string[] allowed)
        {
            if (value == null) return false;
            var set = new HashSet<string>(allowed ?? Array.Empty<string>(), StringComparer.Ordinal);
            foreach (JProperty property in value.Properties())
                if (!set.Contains(property.Name)) return false;
            return true;
        }

        public static bool IsBoundedString(JToken value, int maxChars, bool allowNull = true)
        {
            if (value == null || value.Type == JTokenType.Null) return allowNull;
            return value.Type == JTokenType.String && value.Value<string>().Length <= maxChars;
        }

        private static bool TryWriteTextAtomicLocked(string path, string value, int maxBytes,
            Func<string, bool> semanticValidator, bool preserveStagedTmpOnFailure = false)
        {
            string tmp = path + ".tmp";
            string promote = path + ".promote";
            bool stagedThisAttempt = false;
            bool committed = false;
            try
            {
                if (!SafeValidate(semanticValidator, value)) return false;
                byte[] bytes = StrictUtf8.GetBytes(value);
                if (bytes.Length > maxBytes) return false;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                stagedThisAttempt = true;
                WriteDurable(tmp, bytes);
                if (!TryReadStrictUtf8(tmp, maxBytes, out string staged)
                    || !string.Equals(staged, value, StringComparison.Ordinal)
                    || !SafeValidate(semanticValidator, staged)) return false;

                // Publish a separately flushed promotion file and keep tmp as exact-current
                // evidence until the backup is refreshed. File.Replace consumes its source;
                // publishing tmp directly would leave only new main + stale bak after a backup
                // failure and permit silent rollback if main were later damaged.
                WriteDurable(promote, bytes);
                if (!TryReadStrictUtf8(promote, maxBytes, out string promotion)
                    || !string.Equals(promotion, value, StringComparison.Ordinal)
                    || !SafeValidate(semanticValidator, promotion)) return false;
                if (File.Exists(path)) File.Replace(promote, path, path + ".bak", true);
                else File.Move(promote, path);
                committed = true;

                bool committedOk = TryReadStrictUtf8(path, maxBytes, out string committedValue)
                    && string.Equals(committedValue, value, StringComparison.Ordinal)
                    && SafeValidate(semanticValidator, committedValue);
                if (!committedOk) return false;

                // File.Replace leaves .bak at the previous commit. Once the new main has
                // passed exact semantic readback, the caller must observe success: returning
                // false after this commit point can make it retry or roll back in-memory state
                // even though the new value will reappear after restart. Refresh the exact
                // current backup best-effort; a later recoverable read will fail closed and
                // retry that repair before exposing the value if the refresh did not land.
                bool backupCurrent = TryReadStrictUtf8(path + ".bak", maxBytes, out string backup)
                    && string.Equals(backup, value, StringComparison.Ordinal)
                    && SafeValidate(semanticValidator, backup);
                if (!backupCurrent)
                {
                    backupCurrent = TryWriteReplicaLocked(path + ".bak", value,
                        maxBytes, semanticValidator);
                }
                if (backupCurrent)
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return true; // main exact-readback is the authoritative commit point
            }
            catch { return false; }
            finally
            {
                // A write that failed while the process is alive must not leave its own
                // uncommitted tmp behind: main/bak still hold the previous committed value,
                // and stale tmp bytes can later be misread as evidence (e.g. the protected
                // key store fails closed on a half-written protected tmp forever). Crash
                // recovery is unaffected: a hard crash never reaches this cleanup, so a
                // fully staged first-ever tmp still survives for TryReadRecoverableText.
                if (!committed)
                    try { if (File.Exists(promote)) File.Delete(promote); } catch { }
                if (stagedThisAttempt && !committed && !preserveStagedTmpOnFailure)
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        private static bool TryWriteReplicaLocked(string path, string value, int maxBytes,
            Func<string, bool> semanticValidator)
        {
            string tmp = path + ".repair";
            try
            {
                if (!SafeValidate(semanticValidator, value)) return false;
                byte[] bytes = StrictUtf8.GetBytes(value);
                if (bytes.Length > maxBytes) return false;
                WriteDurable(tmp, bytes);
                if (!TryReadStrictUtf8(tmp, maxBytes, out string staged)
                    || !string.Equals(staged, value, StringComparison.Ordinal)
                    || !SafeValidate(semanticValidator, staged)) return false;
                if (File.Exists(path)) File.Replace(tmp, path, null, true);
                else File.Move(tmp, path);
                return TryReadStrictUtf8(path, maxBytes, out string committed)
                    && string.Equals(committed, value, StringComparison.Ordinal)
                    && SafeValidate(semanticValidator, committed);
            }
            catch { return false; }
        }

        private static void WriteDurable(string path, byte[] bytes)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private static bool SafeValidate(Func<string, bool> validator, string value)
        {
            try { return validator(value); }
            catch { return false; }
        }

        private static bool TryDeleteFileExactly(string path)
        {
            try
            {
                if (Directory.Exists(path)) return false;
                if (File.Exists(path)) File.Delete(path);
                return !File.Exists(path) && !Directory.Exists(path);
            }
            catch { return false; }
        }

        private static bool TryArchiveInvalidCandidatesLocked(IEnumerable<string> candidates,
            int maxBytes, Func<string, bool> semanticValidator)
        {
            foreach (string candidate in candidates)
            {
                if (!File.Exists(candidate)) continue;
                if (TryReadStrictUtf8(candidate, maxBytes, out string parsed)
                    && SafeValidate(semanticValidator, parsed)) continue;
                try
                {
                    string archive = candidate + ".corrupt-"
                        + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-"
                        + Guid.NewGuid().ToString("N").Substring(0, 8);
                    File.Move(candidate, archive);
                    if (!File.Exists(archive) || File.Exists(candidate)) return false;
                }
                catch { return false; }
            }
            return true;
        }

        private static object Gate(string path)
        {
            string full;
            try { full = Path.GetFullPath(path ?? string.Empty); }
            catch { full = path ?? string.Empty; }
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
