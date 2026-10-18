using System;
using System.IO;
using JianghuYouling.Core.Persistence;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Telemetry
{
    /// <summary>The only telemetry data persisted locally is the random installation identity.</summary>
    internal static class TelemetryIdentity
    {
        private static readonly object Gate = new object();
        internal static string LoadOrCreate(string directory)
        {
            lock (Gate) return LoadOrCreateLocked(directory);
        }

        private static string LoadOrCreateLocked(string directory)
        {
            string path = Path.Combine(directory, "client_id.json");
            if (DurableFileStore.TryReadRecoverableText(path, 1024, Valid,
                out string text, out bool exists, out _)) return JObject.Parse(text).Value<string>("client_id");
            if (exists) throw new IOException("Telemetry identity is unavailable");
            var identity = new JObject { ["client_id"] = Guid.NewGuid().ToString("D") };
            if (!DurableFileStore.TryWriteTextAtomic(path, identity.ToString(Formatting.None), 1024, Valid))
                throw new IOException("Telemetry identity could not be persisted");
            return identity.Value<string>("client_id");
        }

        private static bool Valid(string text)
        {
            try
            {
                JObject obj = JObject.Parse(text);
                string id = obj.Value<string>("client_id");
                return obj.Count == 1 && Guid.TryParseExact(id, "D", out Guid guid) && guid != Guid.Empty
                    && id[14] == '4' && "89abAB".IndexOf(id[19]) >= 0;
            }
            catch { return false; }
        }
    }
}
