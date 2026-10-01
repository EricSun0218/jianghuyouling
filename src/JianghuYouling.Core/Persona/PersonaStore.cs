using System.IO;
using System.Text;
using System.Collections.Generic;
using JianghuYouling.Core.Persistence;

namespace JianghuYouling.Core.Persona
{
    /// <summary>
    /// 玩家为某名 NPC 亲手设定的人设。世界目录已经隔离存档，因此人设只绑定 NPC，
    /// 不绑定当代太吾；传剑换代后仍沿用同一人物的人设。
    /// 内容首行可带模式标记:#JYL:replace(替换内置特殊人设)/ #JYL:append(内置特殊人设 + 此人设,都注入)。
    /// 两种模式都保留低优先级、只追加的自动演化层；玩家原文始终不被机器改写。
    /// 旧文件无标记=append(沿用旧行为:自定义与画像并存)。空串/空白=未设定,回退到蒸馏画像。
    /// </summary>
    public static class PersonaStore
    {
        /// <summary>玩家手写 NPC 人设的 UI/持久化统一字符上限。</summary>
        public const int MaxCustomPersonaChars = 250000;
        // 0.29 及更早的人设只按 256 KiB 文件包络校验，没有字符上限。新版读取必须继续
        // 接受当时合法的 ASCII 长文；250000 只约束新保存，不能让旧档在升级后失联。
        const int LegacyMaxBytes = 256 * 1024;
        // 中文 UTF-8 最坏通常为每字 3 bytes；另留标记与恢复余量。
        const int MaxBytes = 2 * 1024 * 1024;
        const string MarkA = "#JYL:append";
        const string MarkR = "#JYL:replace";
        public static string CanonicalPath(string dir, string npcId)
        {
            if (!IsNumericIdentity(npcId))
                throw new InvalidDataException("人设持久化身份必须是非负十进制数");
            return Path.Combine(dir ?? "", "Persona_" + npcId + ".txt");
        }

        /// <summary>读取该同道的自定义人设正文(剥掉模式标记);无文件/读失败/空白则返回 null。</summary>
        public static string Load(string dir, string taiwuId, string npcId)
        {
            try
            {
                if (!TryReadWithSuccessionMigration(dir, taiwuId, npcId, out string raw)) return null;
                var body = StripMarker(raw);
                return string.IsNullOrWhiteSpace(body) ? null : body.Trim();
            }
            catch { return null; }
        }

        /// <summary>读取人设模式:replace=替换内置特殊人设;append=内置特殊人设+自定义人设(默认/旧文件)。自动演化画像始终保留。</summary>
        public static string LoadMode(string dir, string taiwuId, string npcId)
        {
            try
            {
                if (!TryReadWithSuccessionMigration(dir, taiwuId, npcId, out string raw)) return "append";
                var t = NormalizeDocumentStart(raw);
                if (HasExactMarkerLine(t, MarkR)) return "replace";
                return "append";
            }
            catch { return "append"; }
        }

        /// <summary>写入(或清空)该队友的自定义人设 + 模式。text 为空/空白则删除文件(=恢复默认画像)。返回是否成功。</summary>
        public static bool Save(string dir, string taiwuId, string npcId, string text, string mode = "append")
        {
            try
            {
                if (!IsNumericIdentity(taiwuId))
                    throw new InvalidDataException("人设持久化身份必须是非负十进制数");
                var path = CanonicalPath(dir, npcId);
                string marker = (mode == "replace") ? MarkR : MarkA;
                // Empty is a durable tombstone, not deletion: an old bak/tmp must
                // never resurrect a persona the player explicitly cleared.
                string document = marker + "\n" + (text ?? string.Empty).Trim();
                bool saved = DurableFileStore.TryWriteTextAtomic(path, document, MaxBytes, IsValidWritableDocument);
                return saved && (!string.IsNullOrWhiteSpace(text)
                    || DurableFileStore.TryWriteTextAtomic(path, document, MaxBytes, IsValidWritableDocument));
            }
            catch { return false; }
        }

        static bool TryReadWithSuccessionMigration(string dir, string taiwuId, string npcId,
            out string raw)
        {
            raw = null;
            if (!IsNumericIdentity(taiwuId) || !IsNumericIdentity(npcId))
                throw new InvalidDataException("人设持久化身份必须是非负十进制数");
            string canonical = CanonicalPath(dir, npcId);
            if (TryRead(canonical, out raw, out bool canonicalArtifacts)) return true;
            // A canonical tombstone or damaged canonical document must never be bypassed by an
            // older Taiwu-scoped backup, otherwise a cleared persona can resurrect after succession.
            if (canonicalArtifacts) return false;

            string legacy = FindBestLegacyPath(dir, taiwuId, npcId);
            if (legacy == null || !TryRead(legacy, out raw, out _)) return false;
            // Read migration is deliberately copy-on-write. The legacy file remains available for
            // recovery while all future Taiwu identities converge on the NPC-stable canonical path.
            DurableFileStore.TryWriteTextAtomic(canonical, raw, MaxBytes, IsValidWritableOrLegacyDocument);
            return true;
        }

        static bool TryRead(string path, out string raw, out bool anyArtifacts)
        {
            string source;
            return DurableFileStore.TryReadRecoverableText(path, MaxBytes, IsValidReadableDocument,
                out raw, out anyArtifacts, out source);
        }

        static string FindBestLegacyPath(string dir, string taiwuId, string npcId)
        {
            string directory = dir ?? "";
            if (!Directory.Exists(directory)) return null;
            string exact = Path.Combine(directory, "Persona_" + taiwuId + "_" + npcId + ".txt");
            if (HasRecoverableArtifacts(exact))
                return exact;
            string best = null;
            System.DateTime bestTime = System.DateTime.MinValue;
            var candidates = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (string artifact in Directory.GetFiles(directory,
                "Persona_*_" + npcId + ".txt*"))
            {
                string path = artifact.EndsWith(".bak", System.StringComparison.OrdinalIgnoreCase)
                    || artifact.EndsWith(".tmp", System.StringComparison.OrdinalIgnoreCase)
                    ? artifact.Substring(0, artifact.Length - 4) : artifact;
                if (!path.EndsWith(".txt", System.StringComparison.OrdinalIgnoreCase)
                    || !candidates.Add(path)) continue;
                string file = Path.GetFileNameWithoutExtension(path);
                string suffix = "_" + npcId;
                if (string.IsNullOrEmpty(file) || !file.StartsWith("Persona_", System.StringComparison.Ordinal)
                    || !file.EndsWith(suffix, System.StringComparison.Ordinal)) continue;
                string owner = file.Substring("Persona_".Length,
                    file.Length - "Persona_".Length - suffix.Length);
                if (!IsNumericIdentity(owner)) continue;
                System.DateTime time = LatestArtifactWriteTime(path);
                if (best == null || time > bestTime)
                {
                    best = path;
                    bestTime = time;
                }
            }
            return best;
        }

        static bool HasRecoverableArtifacts(string path)
        {
            try
            {
                return File.Exists(path) || File.Exists(path + ".bak")
                    || File.Exists(path + ".tmp");
            }
            catch { return true; }
        }

        static System.DateTime LatestArtifactWriteTime(string path)
        {
            System.DateTime latest = System.DateTime.MinValue;
            foreach (string candidate in new[] { path, path + ".bak", path + ".tmp" })
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        System.DateTime time = File.GetLastWriteTimeUtc(candidate);
                        if (time > latest) latest = time;
                    }
                }
                catch { }
            }
            return latest;
        }

        static bool IsValidWritableOrLegacyDocument(string raw)
            => IsValidWritableDocument(raw) || IsValidReadableDocument(raw);

        static string StripMarker(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            string text = NormalizeDocumentStart(raw);
            if (HasExactMarkerLine(text, MarkA) || HasExactMarkerLine(text, MarkR))
            {
                int nl = text.IndexOf('\n');
                return nl >= 0 ? text.Substring(nl + 1) : "";
            }
            return raw;
        }

        static bool IsValidReadableDocument(string raw)
        {
            if (raw == null || raw.IndexOf('\0') >= 0) return false;
            string trimmed = NormalizeDocumentStart(raw);
            if (!trimmed.StartsWith("#JYL:", System.StringComparison.Ordinal))
                return raw.Length <= MaxCustomPersonaChars
                    || Encoding.UTF8.GetByteCount(raw) <= LegacyMaxBytes; // 旧版无标记 append 文件
            if (!HasExactMarkerLine(trimmed, MarkA) && !HasExactMarkerLine(trimmed, MarkR)) return false;
            return (StripMarker(trimmed) ?? string.Empty).Length <= MaxCustomPersonaChars
                || Encoding.UTF8.GetByteCount(raw) <= LegacyMaxBytes; // 旧版合法的带标记文件也必须可迁移读取
        }

        static bool IsValidWritableDocument(string raw)
        {
            if (raw == null || raw.IndexOf('\0') >= 0) return false;
            string trimmed = NormalizeDocumentStart(raw);
            if (!HasExactMarkerLine(trimmed, MarkA) && !HasExactMarkerLine(trimmed, MarkR)) return false;
            return (StripMarker(trimmed) ?? string.Empty).Length <= MaxCustomPersonaChars;
        }

        static string NormalizeDocumentStart(string raw)
            => (raw ?? string.Empty).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');

        static bool HasExactMarkerLine(string text, string marker)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(marker)
                || !text.StartsWith(marker, System.StringComparison.Ordinal)) return false;
            if (text.Length == marker.Length) return true;
            char next = text[marker.Length];
            return next == '\r' || next == '\n';
        }

        static bool IsNumericIdentity(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 20) return false;
            foreach (char c in value) if (c < '0' || c > '9') return false;
            return true;
        }
    }
}
