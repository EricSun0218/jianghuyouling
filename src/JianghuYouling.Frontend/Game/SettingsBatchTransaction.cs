using System;
using System.Collections.Generic;
using System.IO;

namespace JianghuYouling
{
    /// <summary>
    /// In-memory rollback boundary for the settings window's multi-file save.  Individual
    /// stores remain independently durable; this layer prevents a later validation/write
    /// failure from leaving the earlier settings permanently committed without telling the
    /// player.  Secret-bearing snapshots never leave memory and are zeroed on completion.
    /// </summary>
    internal sealed class SettingsBatchTransaction : IDisposable
    {
        private const long MaxSnapshotBytesPerFile = 8L * 1024L * 1024L;
        private readonly Dictionary<string, byte[]> _before =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private bool _finished;

        private SettingsBatchTransaction() { }

        internal static SettingsBatchTransaction Capture(IEnumerable<string> mainPaths)
        {
            if (mainPaths == null) throw new ArgumentNullException(nameof(mainPaths));
            var transaction = new SettingsBatchTransaction();
            try
            {
                foreach (string rawMain in mainPaths)
                {
                    string main = EnsureAllowedPath(rawMain);
                    transaction.CaptureOne(main);
                    transaction.CaptureOne(main + ".tmp");
                    transaction.CaptureOne(main + ".bak");
                }
                return transaction;
            }
            catch
            {
                transaction.ZeroSnapshots();
                throw;
            }
        }

        internal void Commit()
        {
            if (_finished) return;
            _finished = true;
            ZeroSnapshots();
        }

        internal bool Rollback()
        {
            if (_finished) return true;
            bool ok = true;
            foreach (var pair in _before)
            {
                try
                {
                    if (pair.Value == null)
                    {
                        if (File.Exists(pair.Key)) File.Delete(pair.Key);
                        continue;
                    }
                    string directory = Path.GetDirectoryName(pair.Key);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    string staged = pair.Key + ".settings-rollback-" + Guid.NewGuid().ToString("N");
                    try
                    {
                        using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write,
                            FileShare.None, 4096, FileOptions.WriteThrough))
                        {
                            stream.Write(pair.Value, 0, pair.Value.Length);
                            stream.Flush(true);
                        }
                        if (File.Exists(pair.Key)) File.Replace(staged, pair.Key, null, true);
                        else File.Move(staged, pair.Key);
                    }
                    finally
                    {
                        try { if (File.Exists(staged)) File.Delete(staged); } catch { ok = false; }
                    }
                    byte[] restored = File.ReadAllBytes(pair.Key);
                    if (!SameBytes(restored, pair.Value)) ok = false;
                    Array.Clear(restored, 0, restored.Length);
                }
                catch { ok = false; }
            }
            _finished = true;
            ZeroSnapshots();
            return ok;
        }

        public void Dispose()
        {
            if (!_finished) Rollback();
        }

        private void CaptureOne(string path)
        {
            if (_before.ContainsKey(path)) return;
            if (!File.Exists(path))
            {
                _before[path] = null;
                return;
            }
            var info = new FileInfo(path);
            if (info.Length < 0 || info.Length > MaxSnapshotBytesPerFile)
                throw new IOException("设置文件异常过大，拒绝启动批量保存事务");
            _before[path] = File.ReadAllBytes(path);
        }

        private static string EnsureAllowedPath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) throw new InvalidDataException("设置事务路径为空");
            string full = Path.GetFullPath(raw);
            string settings = WithSeparator(Path.GetFullPath(JianghuYoulingPaths.Settings));
            string personas = WithSeparator(Path.GetFullPath(JianghuYoulingPaths.Personas));
            if (!full.StartsWith(settings, StringComparison.OrdinalIgnoreCase)
                && !full.StartsWith(personas, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("设置事务路径越出 Settings/Personas 边界");
            return full;
        }

        private static string WithSeparator(string value)
            => value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

        private static bool SameBytes(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            int diff = 0;
            for (int i = 0; i < left.Length; i++) diff |= left[i] ^ right[i];
            return diff == 0;
        }

        private void ZeroSnapshots()
        {
            foreach (byte[] bytes in _before.Values)
                if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
            _before.Clear();
        }
    }
}
