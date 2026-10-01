using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace JianghuYouling.Core.Diagnostics
{
    /// <summary>Result of the user's explicit, local diagnostic-log export.</summary>
    public sealed class DiagnosticLogExportResult
    {
        public bool Ok { get; internal set; }
        public string Path { get; internal set; }
        public IReadOnlyList<string> CopiedFiles { get; internal set; }
        public IReadOnlyList<string> MissingFiles { get; internal set; }
        public IReadOnlyList<string> FailedFiles { get; internal set; }

        public string ToUserMessage()
        {
            var sb = new StringBuilder();
            sb.Append(!Ok ? "【日志导出失败】"
                : FailedFiles != null && FailedFiles.Count > 0
                    ? "【日志已导出（部分文件失败）】"
                    : "【日志已导出】");
            if (!string.IsNullOrWhiteSpace(Path)) sb.Append("\n路径：").Append(Path);
            AppendList(sb, "已复制", CopiedFiles);
            AppendList(sb, "未找到", MissingFiles);
            AppendList(sb, "复制失败", FailedFiles);
            if (Ok)
                sb.Append("\n可把整个文件夹压缩后发给开发者；本次操作不会把日志内容交给模型读取。");
            return sb.ToString();
        }

        private static void AppendList(StringBuilder sb, string label, IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0) return;
            sb.Append('\n').Append(label).Append("：");
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) sb.Append("、");
                sb.Append(values[i]);
            }
        }
    }

    /// <summary>
    /// Copies the two Unity player logs and Jianghu Youling's bounded LLM metric logs to a
    /// timestamped Desktop folder. Source and destination names are code-owned: model arguments
    /// can never choose an arbitrary filesystem path or filename.
    /// </summary>
    public static class DiagnosticLogExportService
    {
        private static readonly object ExportGate = new object();
        private static readonly Encoding Utf8Bom = new UTF8Encoding(true, true);

        public static DiagnosticLogExportResult ExportToDesktop(CancellationToken cancellationToken = default)
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string playerLogDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData", "LocalLow", "Conchship", "The Scroll of Taiwu");
            string metricsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JianghuYouling");
            return Export(desktop, playerLogDirectory, metricsDirectory, DateTime.Now, cancellationToken);
        }

        // Kept internal so the production assistant has only the fixed-path public entry point;
        // the friend test assembly can still exercise real file sharing and missing-file behavior.
        internal static DiagnosticLogExportResult Export(string desktopDirectory,
            string playerLogDirectory, string metricsDirectory, DateTime timestamp,
            CancellationToken cancellationToken = default)
        {
            var copied = new List<string>();
            var missing = new List<string>();
            var failed = new List<string>();
            var result = new DiagnosticLogExportResult
            {
                CopiedFiles = copied,
                MissingFiles = missing,
                FailedFiles = failed,
            };

            lock (ExportGate)
            {
                string destination = null;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string desktop = RequireDirectoryPath(desktopDirectory, "桌面目录不可用");
                    Directory.CreateDirectory(desktop);
                    string folderName = "江湖有灵日志_" + timestamp.ToString("yyyyMMdd_HHmmss_fff")
                        + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    destination = Path.GetFullPath(Path.Combine(desktop, folderName));
                    if (!IsDirectChild(desktop, destination))
                        throw new InvalidDataException("导出目录越出桌面边界");
                    Directory.CreateDirectory(destination);
                    result.Path = destination;

                    CopyKnownFile(playerLogDirectory, "Player.log", destination, copied, missing, failed, cancellationToken);
                    CopyKnownFile(playerLogDirectory, "Player-prev.log", destination, copied, missing, failed, cancellationToken);
                    CopyKnownFile(metricsDirectory, "llm_metrics.jsonl", destination, copied, missing, failed, cancellationToken);
                    CopyKnownFile(metricsDirectory, "llm_metrics.jsonl.1", destination, copied, missing, failed, cancellationToken);

                    cancellationToken.ThrowIfCancellationRequested();
                    WriteManifest(destination, timestamp, copied, missing, failed);
                    result.Ok = true;
                    return result;
                }
                catch (OperationCanceledException)
                {
                    Exception cleanupError = null;
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(destination) && Directory.Exists(destination))
                            Directory.Delete(destination, true);
                    }
                    catch (Exception e) { cleanupError = e; }
                    failed.Add("导出已取消");
                    bool directoryRemains = !string.IsNullOrWhiteSpace(destination)
                        && Directory.Exists(destination);
                    if (directoryRemains)
                        failed.Add("取消后清理失败：" + (cleanupError?.GetType().Name ?? "目录仍存在"));
                    // Preserve the path when cleanup failed so the user can remove or inspect the
                    // partial folder.  Only claim there is no residue after verifying it vanished.
                    result.Path = directoryRemains ? destination : null;
                    result.Ok = false;
                    return result;
                }
                catch (Exception e)
                {
                    failed.Add("导出目录：" + e.GetType().Name);
                    result.Ok = false;
                    return result;
                }
            }
        }

        private static void CopyKnownFile(string sourceDirectory, string safeName,
            string destinationDirectory, List<string> copied, List<string> missing,
            List<string> failed, CancellationToken cancellationToken)
        {
            string destination = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsSafeFileName(safeName)) throw new InvalidDataException("文件名不安全");
                string sourceRoot = RequireDirectoryPath(sourceDirectory, "源目录不可用");
                string source = Path.GetFullPath(Path.Combine(sourceRoot, safeName));
                if (!IsDirectChild(sourceRoot, source)) throw new InvalidDataException("源文件越界");
                if (!File.Exists(source))
                {
                    missing.Add(safeName);
                    return;
                }

                destination = Path.GetFullPath(Path.Combine(destinationDirectory, safeName));
                if (!IsDirectChild(destinationDirectory, destination))
                    throw new InvalidDataException("目标文件越界");
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan))
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                    FileShare.Read, 64 * 1024, FileOptions.WriteThrough))
                {
                    var buffer = new byte[64 * 1024];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                    }
                    output.Flush(true);
                }
                copied.Add(safeName);
            }
            catch (OperationCanceledException)
            {
                try { if (!string.IsNullOrEmpty(destination) && File.Exists(destination)) File.Delete(destination); }
                catch { }
                throw;
            }
            catch (Exception e)
            {
                // A cancelled/failed copy must not leave a truncated file that looks usable.
                try { if (!string.IsNullOrEmpty(destination) && File.Exists(destination)) File.Delete(destination); }
                catch { }
                failed.Add(safeName + "（" + e.GetType().Name + "）");
            }
        }

        private static void WriteManifest(string destination, DateTime timestamp,
            IReadOnlyList<string> copied, IReadOnlyList<string> missing, IReadOnlyList<string> failed)
        {
            var sb = new StringBuilder();
            sb.Append("江湖有灵诊断日志导出\r\n")
              .Append("导出时间：").Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss")).Append("\r\n")
              .Append("说明：Player.log 与 Player-prev.log 是游戏运行日志；llm_metrics.jsonl 与 .1 是江湖有灵模型调用指标。\r\n")
              .Append("请将整个文件夹压缩后发给开发者。\r\n\r\n");
            AppendManifestList(sb, "已复制", copied);
            AppendManifestList(sb, "未找到", missing);
            AppendManifestList(sb, "复制失败", failed);
            File.WriteAllText(Path.Combine(destination, "导出说明.txt"), sb.ToString(), Utf8Bom);
        }

        private static void AppendManifestList(StringBuilder sb, string label, IReadOnlyList<string> values)
        {
            sb.Append(label).Append("：");
            if (values == null || values.Count == 0) sb.Append("无");
            else
                for (int i = 0; i < values.Count; i++)
                {
                    if (i > 0) sb.Append("、");
                    sb.Append(values[i]);
                }
            sb.Append("\r\n");
        }

        private static string RequireDirectoryPath(string value, string error)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new DirectoryNotFoundException(error);
            return Path.GetFullPath(value);
        }

        private static bool IsDirectChild(string parent, string candidate)
        {
            string root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(candidate);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            string relative = full.Substring(root.Length);
            return relative.Length > 0 && relative.IndexOf(Path.DirectorySeparatorChar) < 0
                && relative.IndexOf(Path.AltDirectorySeparatorChar) < 0;
        }

        private static bool IsSafeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value != Path.GetFileName(value)) return false;
            return value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }
    }
}
