using System;
using System.Collections.Generic;
using System.IO;
using JianghuYouling.Core.Persistence;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JianghuYouling
{
    /// <summary>
    /// 只记录本功能上线后收到的 NPC 主动消息。文件不存在即全部已读，绝不扫描旧聊天
    /// 反推未读，因此升级用户的历史记录天然保持已读。
    /// </summary>
    public static class NpcChatUnreadStore
    {
        const int SchemaVersion = 1;
        const int MaxFileBytes = 512 * 1024;
        const int MaxPeople = 65536;
        const int MaxPerPerson = 9999;
        static readonly object Gate = new object();
        static string _scope;
        static int _taiwuId;
        static bool _loaded;
        static Dictionary<int, int> _counts = new Dictionary<int, int>();

        static string ScopeFor(int taiwuId) => JianghuYoulingPaths.CurrentWorldId + "|" + taiwuId;
        static string PathFor(int taiwuId) => Path.Combine(JianghuYoulingPaths.ChatLogs,
            "chat_unread_" + taiwuId + ".json");

        public static int Total(int taiwuId)
        {
            if (taiwuId <= 0) return 0;
            lock (Gate)
            {
                EnsureLoaded(taiwuId);
                long total = 0;
                foreach (int count in _counts.Values)
                    total = Math.Min(int.MaxValue, total + Math.Max(0, count));
                return (int)total;
            }
        }

        public static int Count(int taiwuId, int npcId)
        {
            if (taiwuId <= 0 || npcId < 0) return 0;
            lock (Gate)
            {
                EnsureLoaded(taiwuId);
                return _counts.TryGetValue(npcId, out int count) ? Math.Max(0, count) : 0;
            }
        }

        public static bool Add(int taiwuId, int npcId)
        {
            if (taiwuId <= 0 || npcId < 0) return false;
            lock (Gate)
            {
                EnsureLoaded(taiwuId);
                int before = _counts.TryGetValue(npcId, out int count) ? count : 0;
                _counts[npcId] = Math.Min(MaxPerPerson, Math.Max(0, before) + 1);
                if (Save(taiwuId)) return true;
                if (before > 0) _counts[npcId] = before;
                else _counts.Remove(npcId);
                return false;
            }
        }

        public static bool MarkRead(int taiwuId, int npcId)
        {
            if (taiwuId <= 0 || npcId < 0) return true;
            lock (Gate)
            {
                EnsureLoaded(taiwuId);
                if (!_counts.TryGetValue(npcId, out int before) || before <= 0) return true;
                _counts.Remove(npcId);
                if (Save(taiwuId)) return true;
                _counts[npcId] = before;
                return false;
            }
        }

        public static bool ReplaceIdentity(int taiwuId, int oldNpcId, int newNpcId)
        {
            if (taiwuId <= 0 || oldNpcId < 0 || newNpcId < 0 || oldNpcId == newNpcId) return true;
            lock (Gate)
            {
                EnsureLoaded(taiwuId);
                if (!_counts.TryGetValue(oldNpcId, out int oldCount) || oldCount <= 0) return true;
                int newCount = _counts.TryGetValue(newNpcId, out int current) ? current : 0;
                _counts.Remove(oldNpcId);
                _counts[newNpcId] = Math.Min(MaxPerPerson, Math.Max(0, newCount) + oldCount);
                if (Save(taiwuId)) return true;
                _counts[oldNpcId] = oldCount;
                if (newCount > 0) _counts[newNpcId] = newCount; else _counts.Remove(newNpcId);
                return false;
            }
        }

        public static void ResetForWorldExit()
        {
            lock (Gate)
            {
                _scope = null;
                _taiwuId = 0;
                _loaded = false;
                _counts.Clear();
            }
        }

        static void EnsureLoaded(int taiwuId)
        {
            string scope = ScopeFor(taiwuId);
            if (_loaded && _taiwuId == taiwuId && string.Equals(_scope, scope,
                StringComparison.Ordinal)) return;
            _scope = scope;
            _taiwuId = taiwuId;
            _loaded = true;
            _counts = new Dictionary<int, int>();
            if (!DurableFileStore.TryReadRecoverableText(PathFor(taiwuId), MaxFileBytes,
                IsValidDocument, out string json, out _, out _) || string.IsNullOrWhiteSpace(json)) return;
            try
            {
                JObject map = JObject.Parse(json)["counts"] as JObject;
                if (map == null) return;
                foreach (JProperty property in map.Properties())
                    if (int.TryParse(property.Name, out int npcId) && npcId >= 0)
                    {
                        int count = property.Value.Value<int>();
                        if (count > 0) _counts[npcId] = Math.Min(MaxPerPerson, count);
                    }
            }
            catch { _counts.Clear(); }
        }

        static bool Save(int taiwuId)
        {
            try
            {
                var map = new JObject();
                int written = 0;
                foreach (KeyValuePair<int, int> item in _counts)
                {
                    if (written++ >= MaxPeople) break;
                    if (item.Key >= 0 && item.Value > 0)
                        map[item.Key.ToString()] = Math.Min(MaxPerPerson, item.Value);
                }
                var root = new JObject
                {
                    ["schemaVersion"] = SchemaVersion,
                    ["counts"] = map,
                };
                return DurableFileStore.TryWriteTextAtomic(PathFor(taiwuId),
                    root.ToString(Formatting.None), MaxFileBytes, IsValidDocument);
            }
            catch { return false; }
        }

        static bool IsValidDocument(string json)
        {
            if (!DurableFileStore.TryParseJsonStrict(json, 4, out JToken token)
                || !(token is JObject root)
                || !DurableFileStore.HasOnlyProperties(root, "schemaVersion", "counts")
                || root["schemaVersion"]?.Type != JTokenType.Integer
                || root["schemaVersion"].Value<int>() != SchemaVersion
                || !(root["counts"] is JObject map) || map.Count > MaxPeople) return false;
            foreach (JProperty property in map.Properties())
                if (!int.TryParse(property.Name, out int npcId) || npcId < 0
                    || property.Value.Type != JTokenType.Integer
                    || property.Value.Value<int>() <= 0
                    || property.Value.Value<int>() > MaxPerPerson) return false;
            return true;
        }
    }
}
