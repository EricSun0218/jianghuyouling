using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using JianghuYouling.Core.Persistence;
using JianghuYouling.Core.Prompt;
using Newtonsoft.Json;

namespace JianghuYouling
{
    /// <summary>
    /// 结构化世界书只保存编辑器布局；真正生效的权威正文仍是 Worldbook_*.txt。
    /// 布局携带正文指纹，玩家改用高级纯文本编辑后会自动判旧并重新导入，
    /// 不会让过期条目反向覆盖玩家的新正文。
    /// </summary>
    internal static class StructuredWorldBookStore
    {
        private const int MaxLayoutBytes = 8 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false, true);
        private static readonly ConcurrentDictionary<string, object> PathLocks =
            new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        internal sealed class Snapshot
        {
            public StructuredWorldBook.Document Document;
            public bool ImportedFromText;
            public bool Recovered;
            public bool DefaultTemplateApplied;
        }

        internal static string LayoutPath(string personasDirectory, int taiwuId)
            => Path.Combine(personasDirectory ?? string.Empty, "Worldbook_" + taiwuId + ".entries.json");

        internal static Snapshot Load(int taiwuId)
            => LoadFromDirectory(JianghuYoulingPaths.Personas, taiwuId);

        internal static Snapshot LoadFromDirectory(string personasDirectory, int taiwuId)
        {
            WorldBookStore.Snapshot world = WorldBookStore.LoadSnapshotFromDirectory(personasDirectory, taiwuId);
            bool isBuiltInDefault = string.Equals(
                (world.EffectiveText ?? string.Empty).Trim(),
                (DefaultWorldBook.Text ?? string.Empty).Trim(),
                StringComparison.Ordinal);
            string path = LayoutPath(personasDirectory, taiwuId);
            lock (PathLocks.GetOrAdd(path, _ => new object()))
            {
                bool mainWasValid = TryReadLayout(path, out _);
                bool loaded = DurableFileStore.TryReadRecoverableText(path, MaxLayoutBytes,
                    IsValidLayout, out string raw, out _, out _, preserveInvalidCandidates: true);
                if (loaded && TryParse(raw, out StructuredWorldBook.Document document)
                    && string.Equals(document.SourceFingerprint, world.ContentFingerprint,
                        StringComparison.Ordinal))
                {
                    if (document.DefaultTemplateRevision > 0
                        && document.DefaultTemplateRevision
                            < StructuredWorldBook.CurrentDefaultTemplateRevision)
                    {
                        if (StructuredWorldBook.TryUpgradeDefaultTemplateLayout(document))
                        {
                            return new Snapshot
                            {
                                Document = document,
                                ImportedFromText = false,
                                Recovered = !mainWasValid,
                                DefaultTemplateApplied = true,
                            };
                        }
                        return new Snapshot
                        {
                            Document = StructuredWorldBook.ImportDefault(world.EffectiveText),
                            ImportedFromText = true,
                            Recovered = !mainWasValid,
                            DefaultTemplateApplied = true,
                        };
                    }
                    StructuredWorldBook.Normalize(document);
                    return new Snapshot
                    {
                        Document = document,
                        ImportedFromText = false,
                        Recovered = !mainWasValid,
                    };
                }
            }

            return new Snapshot
            {
                Document = isBuiltInDefault
                    ? StructuredWorldBook.ImportDefault(world.EffectiveText)
                    : StructuredWorldBook.Import(world.EffectiveText),
                ImportedFromText = true,
                Recovered = false,
                DefaultTemplateApplied = isBuiltInDefault,
            };
        }

        internal static bool Save(int taiwuId, StructuredWorldBook.Document document, out string error)
            => SaveFromDirectory(JianghuYoulingPaths.Personas, taiwuId, document, out error);

        internal static bool SaveFromDirectory(string personasDirectory, int taiwuId,
            StructuredWorldBook.Document document, out string error)
        {
            error = null;
            if (!StructuredWorldBook.Validate(document, out error)) return false;
            string effective = StructuredWorldBook.Compile(document);
            if (string.IsNullOrWhiteSpace(effective))
            {
                error = "至少需要一个已启用且有正文的条目";
                return false;
            }
            if (effective.Length > WorldBookStore.MaxCustomWorldBookChars)
            {
                error = "编译后的世界书超过 " + WorldBookStore.MaxCustomWorldBookChars + " 字上限";
                return false;
            }

            if (!WorldBookStore.SaveEffectiveFromDirectory(personasDirectory, taiwuId, effective))
            {
                error = "世界书正文写入或语义读回失败";
                return false;
            }

            // SaveEffective 可能把与内置默认逐字相同的文本折叠成默认墓碑；无论哪种情况，
            // 当前生效指纹都应与编译正文一致。
            document.SourceFingerprint = WorldBookFilter.ContentFingerprint(effective);
            StructuredWorldBook.Normalize(document);
            string raw = JsonConvert.SerializeObject(document, Formatting.Indented) + "\n";
            string path = LayoutPath(personasDirectory, taiwuId);
            lock (PathLocks.GetOrAdd(path, _ => new object()))
            {
                if (!DurableFileStore.TryWriteTextAtomic(path, raw, MaxLayoutBytes, IsValidLayout))
                {
                    error = "世界书条目索引写入失败";
                    return false;
                }
            }

            Snapshot verify = LoadFromDirectory(personasDirectory, taiwuId);
            if (verify.ImportedFromText
                || !string.Equals(verify.Document.SourceFingerprint, document.SourceFingerprint,
                    StringComparison.Ordinal))
            {
                error = "世界书条目索引读回校验失败";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 高级纯文本保存或还原默认后，使旧结构化布局失效。写显式空索引，不删除文件，
        /// 避免 .bak 在恢复时把旧分类和条目重新变成现行数据。
        /// </summary>
        internal static bool InvalidateFromDirectory(string personasDirectory, int taiwuId)
        {
            var invalid = new StructuredWorldBook.Document { SourceFingerprint = null };
            string raw = JsonConvert.SerializeObject(invalid, Formatting.Indented) + "\n";
            string path = LayoutPath(personasDirectory, taiwuId);
            lock (PathLocks.GetOrAdd(path, _ => new object()))
            {
                return DurableFileStore.TryWriteTextAtomic(path, raw, MaxLayoutBytes, IsValidLayout)
                    && DurableFileStore.TryWriteTextAtomic(path, raw, MaxLayoutBytes, IsValidLayout);
            }
        }

        private static bool TryReadLayout(string path, out StructuredWorldBook.Document document)
        {
            document = null;
            return File.Exists(path)
                && DurableFileStore.TryReadStrictUtf8(path, MaxLayoutBytes, out string raw)
                && TryParse(raw, out document);
        }

        private static bool IsValidLayout(string raw)
            => Utf8NoBom.GetByteCount(raw ?? "") <= MaxLayoutBytes && TryParse(raw, out _);

        private static bool TryParse(string raw, out StructuredWorldBook.Document document)
        {
            document = null;
            try
            {
                if (string.IsNullOrWhiteSpace(raw) || raw.IndexOf('\0') >= 0) return false;
                document = JsonConvert.DeserializeObject<StructuredWorldBook.Document>(raw);
                if (document == null || document.Version > StructuredWorldBook.CurrentVersion) return false;
                StructuredWorldBook.Normalize(document);
                return StructuredWorldBook.Validate(document, out _);
            }
            catch
            {
                document = null;
                return false;
            }
        }
    }
}
