using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using JianghuYouling.Core.Persistence;
using JianghuYouling.Core.Prompt;

namespace JianghuYouling
{
    /// <summary>
    /// 玩家自定义世界书（按太吾、按 WorldId 隔离）。文件始终是可手工编辑的 UTF-8 纯文本：
    /// 首行可为 #JYL:replace / #JYL:append；#JYL:default 是“明确还原默认”的持久墓碑。
    /// </summary>
    public static class WorldBookStore
    {
        /// <summary>设置页允许玩家直接编辑的世界书字符上限；持久层仍留有更大恢复余量。</summary>
        public const int MaxCustomWorldBookChars = 500000;
        const string MarkA = "#JYL:append";
        const string MarkR = "#JYL:replace";
        const string MarkDefault = "#JYL:default";
        const int MaxDocumentChars = 2 * 1024 * 1024;
        const int MaxDocumentBytes = MaxDocumentChars * 4;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false, true);
        private static readonly ConcurrentDictionary<string, object> PathLocks =
            new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        private static string PathFor(string personasDirectory, int taiwuId)
            => Path.Combine(personasDirectory ?? string.Empty, "Worldbook_" + taiwuId + ".txt");

        private static object GateFor(string path) => PathLocks.GetOrAdd(path, _ => new object());

        public sealed class Snapshot
        {
            public string CustomText { get; internal set; }
            public string Mode { get; internal set; }
            public string EffectiveText { get; internal set; }
            public string ContentFingerprint { get; internal set; }
            public bool Recovered { get; internal set; }
            public string StorageSource { get; internal set; }
        }

        private sealed class ParsedDocument
        {
            public string Raw;
            public string CustomText;
            public string Mode;
            public bool ExplicitDefault;
            public string Source;
        }

        /// <summary>
        /// 一次一致读取同时得到正文、模式、生效文本和内容指纹。main 是提交点；main 不可靠时才从
        /// 完整 tmp/bak 恢复。旧版“删除 main、只留 bak”代表还原默认，不能擅自复活旧世界书。
        /// </summary>
        public static Snapshot LoadSnapshot(int taiwuId)
            => LoadSnapshotFromDirectory(JianghuYoulingPaths.Personas, taiwuId);

        internal static Snapshot LoadSnapshotFromDirectory(string personasDirectory, int taiwuId)
        {
            string path = PathFor(personasDirectory, taiwuId);
            ParsedDocument document;
            bool recovered = false;

            lock (GateFor(path))
            {
                bool mainWasValid = TryReadDocument(path, "main", out _);
                // 兼容旧版 Clear：它会删除 main、只留下 .bak。此时缺少提交点即代表默认，
                // 不能把备份自动当成现行设定；玩家仍可手工查看/恢复该纯文本备份。
                bool legacyCleared = !File.Exists(path) && File.Exists(path + ".bak")
                    && !File.Exists(path + ".tmp");
                if (legacyCleared)
                {
                    document = DefaultDocument("legacy-default");
                }
                else
                {
                    bool loaded = DurableFileStore.TryReadRecoverableText(path, MaxDocumentBytes,
                        IsValidRawDocument, out string raw, out bool anyCandidate, out _,
                        preserveInvalidCandidates: true);
                    if (loaded && TryParseDocument(raw,
                        mainWasValid ? "main" : "recovered", out document))
                    {
                        recovered = !mainWasValid;
                    }
                    else
                    {
                        // 候选存在但当前无法证明哪一版已提交时必须 fail-closed。后续保存也会
                        // 拒绝覆盖这些恢复证据，待锁/磁盘故障解除后再自动修复。
                        document = DefaultDocument(anyCandidate ? "unreliable" : "default");
                    }
                }
            }

            string effective = WorldBookFilter.Compose(document.CustomText, document.Mode);
            return new Snapshot
            {
                CustomText = document.CustomText,
                Mode = document.Mode,
                EffectiveText = effective,
                ContentFingerprint = WorldBookFilter.ContentFingerprint(effective),
                Recovered = recovered,
                StorageSource = document.Source,
            };
        }

        public static string Load(int taiwuId) => LoadSnapshot(taiwuId).CustomText;

        public static string LoadMode(int taiwuId) => LoadSnapshot(taiwuId).Mode;

        public static bool HasCustom(int taiwuId)
            => !string.IsNullOrWhiteSpace(LoadSnapshot(taiwuId).CustomText);

        /// <summary>
        /// 原子保存世界书。写 tmp 后先做 UTF-8/语义 read-back，再 Replace 到 main，并再次校验 main；
        /// 任一步失败均返回 false，旧 main 仍是提交点。allowClear=true 时写显式 default 墓碑。
        /// </summary>
        public static bool Save(int taiwuId, string text, string mode = "replace", bool allowClear = false)
            => SaveFromDirectory(JianghuYoulingPaths.Personas, taiwuId, text, mode, allowClear);

        internal static bool SaveFromDirectory(string personasDirectory, int taiwuId, string text,
            string mode = "replace", bool allowClear = false)
        {
            string body = text ?? "";
            if (string.IsNullOrWhiteSpace(body))
            {
                if (!allowClear && !string.IsNullOrWhiteSpace(
                    LoadSnapshotFromDirectory(personasDirectory, taiwuId).CustomText)) return false;
                return WriteDefaultTombstone(PathFor(personasDirectory, taiwuId));
            }

            body = body.Trim();
            if (body.Length > MaxCustomWorldBookChars) return false;
            string marker = string.Equals(mode, "append", StringComparison.OrdinalIgnoreCase) ? MarkA : MarkR;
            return WriteCommitted(PathFor(personasDirectory, taiwuId), marker + "\n" + body);
        }

        public static string EffectiveWorldBookText(int taiwuId)
            => LoadSnapshot(taiwuId).EffectiveText;

        internal static string EffectiveWorldBookTextFromDirectory(string personasDirectory, int taiwuId)
            => LoadSnapshotFromDirectory(personasDirectory, taiwuId).EffectiveText;

        public static bool SaveEffective(int taiwuId, string text)
            => SaveEffectiveFromDirectory(JianghuYoulingPaths.Personas, taiwuId, text);

        internal static bool SaveEffectiveFromDirectory(string personasDirectory, int taiwuId, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string body = text.Trim();
            if (string.Equals(body, DefaultWorldBook.Text.Trim(), StringComparison.Ordinal))
                return ClearFromDirectory(personasDirectory, taiwuId);
            return SaveFromDirectory(personasDirectory, taiwuId, body, "replace", allowClear: false);
        }

        /// <summary>
        /// 持久化显式默认墓碑，而不是删除 main。这样旧自定义内容仍可留在 .bak 供人工恢复，
        /// 自动恢复逻辑却不会在重启后把它误当成当前世界书。
        /// </summary>
        public static bool Clear(int taiwuId)
            => ClearFromDirectory(JianghuYoulingPaths.Personas, taiwuId);

        internal static bool ClearFromDirectory(string personasDirectory, int taiwuId)
            => WriteDefaultTombstone(PathFor(personasDirectory, taiwuId));

        private static bool WriteDefaultTombstone(string path)
        {
            string tombstone = MarkDefault + "\n";
            lock (GateFor(path))
            {
                // 双写墓碑会把 main 与 .bak 都变成墓碑;若 .bak 里还留着玩家手写正文
                // (旧版 Clear 只删 main、留 .bak 的存档正是这种状态),第二次发布的
                // File.Replace 会无条件覆盖它 —— 那可能是玩家世界书的最后一份副本。
                // 因此发布墓碑前,先把 main/.bak 中现存的非墓碑自定义正文原子归档为
                // .cleared-* 旁路文件(不在 main/bak/tmp 恢复候选名单内,自动恢复永远
                // 不会误读它,玩家可随时人工找回)。归档失败则整个 Clear 失败,原文件不动。
                if (!TryArchiveCustomBeforeTombstone(path)) return false;
                // Publish twice so both main and bak carry the explicit reset. If the
                // newest main is later damaged, recovery cannot resurrect the old book.
                return WriteCommitted(path, tombstone) && WriteCommitted(path, tombstone);
            }
        }

        private static bool TryArchiveCustomBeforeTombstone(string path)
        {
            try
            {
                string archivedRaw = null;
                foreach (string candidate in new[] { path, path + ".bak" })
                {
                    if (!TryReadDocument(candidate, "archive-scan", out var doc)) continue;
                    if (doc.ExplicitDefault || string.IsNullOrWhiteSpace(doc.CustomText)) continue;
                    // main 与 .bak 内容一致时只归档一份,重复 Clear 不会堆积归档文件。
                    if (string.Equals(doc.Raw, archivedRaw, StringComparison.Ordinal)) continue;
                    string archive = path + ".cleared-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")
                        + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    WriteDurableText(archive, doc.Raw);
                    if (!TryReadDocument(archive, "cleared", out var check)
                        || !string.Equals(check.Raw, doc.Raw, StringComparison.Ordinal))
                    {
                        TryDelete(archive);
                        return false;
                    }
                    archivedRaw = doc.Raw;
                }
                return true;
            }
            catch (Exception e)
            {
                try { UnityEngine.Debug.LogWarning("[江湖有灵] 世界书清空前归档失败,已取消清空: " + e.GetType().Name); } catch { }
                return false;
            }
        }

        private static bool WriteCommitted(string path, string raw)
        {
            lock (GateFor(path))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    ValidateDocument(raw, out _, out _, out _);
                    bool legacyCleared = !File.Exists(path) && File.Exists(path + ".bak")
                        && !File.Exists(path + ".tmp");
                    if (!legacyCleared)
                    {
                        bool readable = DurableFileStore.TryReadRecoverableText(path,
                            MaxDocumentBytes, IsValidRawDocument, out _, out bool anyCandidate,
                            out _, preserveInvalidCandidates: true);
                        if (anyCandidate && !readable) return false;
                    }
                    return DurableFileStore.TryWriteTextAtomic(path, raw, MaxDocumentBytes,
                        IsValidRawDocument);
                }
                catch (Exception e)
                {
                    try { UnityEngine.Debug.LogWarning("[江湖有灵] 世界书存盘失败: " + e.GetType().Name); } catch { }
                    return false;
                }
            }
        }

        private static bool TryReadDocument(string path, string source, out ParsedDocument document)
        {
            document = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            return DurableFileStore.TryReadStrictUtf8(path, MaxDocumentBytes, out string raw)
                && TryParseDocument(raw, source, out document);
        }

        private static bool TryParseDocument(string raw, string source, out ParsedDocument document)
        {
            document = null;
            try
            {
                ValidateDocument(raw, out string body, out string mode, out bool explicitDefault);
                document = new ParsedDocument
                {
                    Raw = raw,
                    CustomText = body,
                    Mode = mode,
                    ExplicitDefault = explicitDefault,
                    Source = source,
                };
                return true;
            }
            catch { return false; }
        }

        private static bool IsValidRawDocument(string raw)
        {
            try
            {
                ValidateDocument(raw, out _, out _, out _);
                return Utf8NoBom.GetByteCount(raw) <= MaxDocumentBytes;
            }
            catch { return false; }
        }

        private static void WriteDurableText(string path, string raw)
        {
            byte[] bytes = Utf8NoBom.GetBytes(raw ?? "");
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private static void ValidateDocument(string raw, out string body, out string mode, out bool explicitDefault)
        {
            if (raw == null) throw new InvalidDataException("世界书内容为空引用");
            if (raw.Length > MaxDocumentChars) throw new InvalidDataException("世界书超过 2 MiB 字符上限");
            if (raw.IndexOf('\0') >= 0) throw new InvalidDataException("世界书包含 NUL 字符");
            ParseDocument(raw, out body, out mode, out explicitDefault);
        }

        private static ParsedDocument DefaultDocument(string source)
            => new ParsedDocument { Raw = MarkDefault + "\n", CustomText = null, Mode = "replace", ExplicitDefault = true, Source = source };

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void ParseDocument(string raw, out string body, out string mode, out bool explicitDefault)
        {
            mode = "replace";
            body = raw;
            explicitDefault = false;
            if (string.IsNullOrEmpty(raw)) return;   // 兼容旧版合法空文件=默认。

            string text = raw.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            if (HasExactMarkerLine(text, MarkDefault))
            {
                explicitDefault = true;
                body = null;
                return;
            }

            // 标记必须独占首行。诸如“#JYL:default世界观……”是玩家正文，不可因前缀
            // 碰巧相同就被静默解释为清空墓碑。
            bool append = HasExactMarkerLine(text, MarkA);
            bool replace = HasExactMarkerLine(text, MarkR);
            if (!append && !replace) return;   // 无标记纯文本：兼容旧文件及玩家手工编辑。

            mode = append ? "append" : "replace";
            int nl = text.IndexOf('\n');
            body = nl >= 0 ? text.Substring(nl + 1) : "";
        }

        private static bool HasExactMarkerLine(string text, string marker)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(marker)
                || !text.StartsWith(marker, StringComparison.Ordinal)) return false;
            if (text.Length == marker.Length) return true;
            char next = text[marker.Length];
            return next == '\r' || next == '\n';
        }
    }
}
