using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using JianghuYouling.Core.Persistence;

namespace JianghuYouling
{
    public static class PlayerTalkMarkStore
    {
        private const int MaxFileBytes = 2 * 1024 * 1024;
        private const int MaxEntries = 100000;
        private static readonly object Sync = new object();

        private static string PathFor(int taiwuId)
        {
            return Path.Combine(JianghuYoulingPaths.ChatLogs, "PlayerTalkDates_" + taiwuId + ".json");
        }

        public static void Mark(int taiwuId, int npcId, int date)
        {
            if (taiwuId <= 0 || npcId <= 0 || npcId == taiwuId || date < 0) return;
            lock (Sync)
            {
                var d = LoadMap(taiwuId, out bool reliable);
                if (!reliable) { UnityEngine.Debug.LogWarning("[JHYL_PLAYER_TALK_MARK_UNRELIABLE] 拒绝覆盖损坏标记库"); return; }
                if (d.TryGetValue(npcId, out int old) && old >= date) return;
                d[npcId] = date;
                SaveMap(taiwuId, d);
            }
        }

        public static int LastDate(int taiwuId, int npcId)
        {
            if (taiwuId <= 0 || npcId <= 0 || npcId == taiwuId) return -1;
            lock (Sync)
            {
                var d = LoadMap(taiwuId, out _);
                return d.TryGetValue(npcId, out int date) ? date : -1;
            }
        }

        public static Dictionary<int, int> AllDates(int taiwuId)
        {
            if (taiwuId <= 0) return new Dictionary<int, int>();
            lock (Sync)
            {
                return new Dictionary<int, int>(LoadMap(taiwuId, out _));
            }
        }

        public static bool Remove(int taiwuId, int npcId)
        {
            if (taiwuId <= 0 || npcId <= 0) return false;
            lock (Sync)
            {
                var d = LoadMap(taiwuId, out bool reliable);
                if (!reliable) return false;
                if (!d.Remove(npcId)) return true;
                return SaveMap(taiwuId, d);
            }
        }

        public static bool ReplaceIdentity(int taiwuId, int oldNpcId, int newNpcId)
        {
            if (taiwuId <= 0 || oldNpcId <= 0 || newNpcId <= 0 || oldNpcId == newNpcId) return true;
            lock (Sync)
            {
                var d = LoadMap(taiwuId, out bool reliable);
                if (!reliable) return false;
                if (!d.TryGetValue(oldNpcId, out int oldDate)) return true;
                int newDate = d.TryGetValue(newNpcId, out int current) ? current : -1;
                d.Remove(oldNpcId);
                d[newNpcId] = Math.Max(oldDate, newDate);
                return SaveMap(taiwuId, d);
            }
        }

        private static Dictionary<int, int> LoadMap(int taiwuId, out bool reliable)
        {
            try
            {
                var p = PathFor(taiwuId);
                if (DurableFileStore.TryReadRecoverableText(p, MaxFileBytes, IsValidDocument,
                    out string json, out bool any, out _))
                {
                    reliable = true;
                    return JObject.Parse(json).ToObject<Dictionary<int, int>>() ?? new Dictionary<int, int>();
                }
                reliable = !any;
                return new Dictionary<int, int>();
            }
            catch
            {
                reliable = false;
                return new Dictionary<int, int>();
            }
        }

        private static bool SaveMap(int taiwuId, Dictionary<int, int> d)
        {
            try
            {
                var p = PathFor(taiwuId);
                string json = JsonConvert.SerializeObject(d, Formatting.Indented);
                return DurableFileStore.TryWriteTextAtomic(p, json, MaxFileBytes, IsValidDocument);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[JHYL_PLAYER_TALK_MARK_SAVE_FAIL] " + e.GetType().Name);
                return false;
            }
        }

        private static bool IsValidDocument(string json)
        {
            if (!DurableFileStore.TryParseJsonStrict(json, 8, out JToken root)
                || !(root is JObject value) || value.Count > MaxEntries) return false;
            foreach (JProperty property in value.Properties())
            {
                if (!int.TryParse(property.Name, out int npcId) || npcId <= 0
                    || property.Value.Type != JTokenType.Integer) return false;
                try { if (property.Value.Value<int>() < 0) return false; }
                catch { return false; }
            }
            return true;
        }
    }
}
