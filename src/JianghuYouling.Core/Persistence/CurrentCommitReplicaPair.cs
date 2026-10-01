using System;
using System.IO;

namespace JianghuYouling.Core.Persistence
{
    /// <summary>
    /// Publishes the exact current payload to two independently flushed recovery replicas.
    /// Each replica is staged and atomically promoted, so a failed refresh preserves either the
    /// previous valid replica or the fully written current one.
    /// </summary>
    public static class CurrentCommitReplicaPair
    {
        public static bool TryPublish(string firstPath, string secondPath,
            byte[] payload, int maxBytes)
        {
            if (string.IsNullOrWhiteSpace(firstPath)
                || string.IsNullOrWhiteSpace(secondPath)
                || string.Equals(firstPath, secondPath, StringComparison.OrdinalIgnoreCase)
                || payload == null || payload.Length > maxBytes || maxBytes <= 0)
                return false;
            try
            {
                PublishOne(firstPath, payload);
                PublishOne(secondPath, payload);
                return BytesEqual(File.ReadAllBytes(firstPath), payload)
                    && BytesEqual(File.ReadAllBytes(secondPath), payload);
            }
            catch { return false; }
        }

        private static void PublishOne(string path, byte[] payload)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string stage = path + ".refresh-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(stage, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(payload, 0, payload.Length);
                    stream.Flush(true);
                }
                if (!BytesEqual(File.ReadAllBytes(stage), payload))
                    throw new IOException("replica stage readback mismatch");
                if (File.Exists(path)) File.Replace(stage, path, null, true);
                else File.Move(stage, path);
                if (!BytesEqual(File.ReadAllBytes(path), payload))
                    throw new IOException("replica commit readback mismatch");
            }
            finally
            {
                try { if (File.Exists(stage)) File.Delete(stage); } catch { }
            }
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i]) return false;
            return true;
        }
    }
}
