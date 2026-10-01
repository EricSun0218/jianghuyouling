using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using JianghuYouling.Core.Persistence;

namespace JianghuYouling
{
    public struct MonthlyChronicleStatus
    {
        public int UnreadCount;
        public int GeneratingCount;
    }

    /// <summary>
    /// 过月纪事按钮的轻量状态源。首次启用只把已有纪事建立为已读基线；以后按“事件/同道人物”
    /// 分项比较内容指纹，因此同月增量完成、按需补写正文都能准确增加未读，而每帧 UI 刷新不会读盘。
    /// </summary>
    public static class MonthlyChronicleNoticeStore
    {
        // v3 首次载入会把升级当下的全部旧纪事重新建立为已读基线；同时旧版没有 EventId
        // 的事件按月份取得稳定身份，之后新月份插到列表头部也不会把整批历史误判为未读。
        const int SchemaVersion = 3;
        const int MaxFileBytes = 8 * 1024 * 1024;
        const int MaxSeenItems = 65536;
        static readonly object Gate = new object();
        static readonly HashSet<string> Generating = new HashSet<string>(StringComparer.Ordinal);
        static string _scope;
        static int _taiwuId;
        static bool _initialized;
        static bool _dirty;
        static Dictionary<string, string> _current = new Dictionary<string, string>(StringComparer.Ordinal);
        static Dictionary<string, string> _seen = new Dictionary<string, string>(StringComparer.Ordinal);

        // UI 每帧只比较内存中的世界身份，绝不为了状态刷新去触发目录检查。
        static string ScopeFor(int taiwuId) => JianghuYoulingPaths.CurrentWorldId + "|" + taiwuId;
        static string StatePath(int taiwuId) => Path.Combine(JianghuYoulingPaths.Events,
            "chronicle_seen_" + taiwuId + ".json");
        static string GenerationKey(int taiwuId, int date) => ScopeFor(taiwuId) + "|" + date;
        public static string CompanionItemId(int date, int npcId)
            => "companion:" + date + ":" + npcId;

        public static void BeginGeneration(int taiwuId, int date)
        {
            if (taiwuId <= 0) return;
            lock (Gate)
            {
                EnsureCache(taiwuId);
                Generating.Add(GenerationKey(taiwuId, date));
            }
        }

        public static void EndGeneration(int taiwuId, int date)
        {
            if (taiwuId <= 0) return;
            lock (Gate)
            {
                Generating.Remove(GenerationKey(taiwuId, date));
                if (_initialized && _taiwuId == taiwuId && _scope == ScopeFor(taiwuId)) _dirty = true;
            }
        }

        public static void NotifyArchiveChanged(int taiwuId)
        {
            if (taiwuId <= 0) return;
            lock (Gate)
                if (_initialized && _taiwuId == taiwuId && _scope == ScopeFor(taiwuId)) _dirty = true;
        }

        public static MonthlyChronicleStatus Snapshot(int taiwuId)
        {
            if (taiwuId <= 0) return default;
            lock (Gate)
            {
                EnsureCache(taiwuId);
                int unread = 0;
                foreach (KeyValuePair<string, string> item in _current)
                    if (!_seen.TryGetValue(item.Key, out string fingerprint)
                        || !string.Equals(fingerprint, item.Value, StringComparison.Ordinal)) unread++;
                int generating = 0;
                string prefix = ScopeFor(taiwuId) + "|";
                foreach (string key in Generating)
                    if (key.StartsWith(prefix, StringComparison.Ordinal)) generating++;
                return new MonthlyChronicleStatus { UnreadCount = unread, GeneratingCount = generating };
            }
        }

        public static int UnreadCountForDate(int taiwuId, int date)
        {
            if (taiwuId <= 0 || date < 0) return 0;
            lock (Gate)
            {
                EnsureCache(taiwuId);
                int unread = 0;
                string eventPrefix = "event:" + date + ":";
                string companionPrefix = "companion:" + date + ":";
                foreach (KeyValuePair<string, string> item in _current)
                {
                    if (!item.Key.StartsWith(eventPrefix, StringComparison.Ordinal)
                        && !item.Key.StartsWith(companionPrefix, StringComparison.Ordinal)) continue;
                    if (!_seen.TryGetValue(item.Key, out string fingerprint)
                        || !string.Equals(fingerprint, item.Value, StringComparison.Ordinal)) unread++;
                }
                return unread;
            }
        }

        public static void MarkDateRead(int taiwuId, int date)
        {
            if (taiwuId <= 0 || date < 0) return;
            lock (Gate)
            {
                EnsureCache(taiwuId);
                Dictionary<string, string> items = BuildCurrent(taiwuId, date);
                foreach (KeyValuePair<string, string> item in items) _seen[item.Key] = item.Value;
                SaveSeen(taiwuId, _seen);
            }
        }

        public static void MarkCompanionRead(int taiwuId, int date, int npcId)
        {
            if (taiwuId <= 0 || npcId <= 0) return;
            lock (Gate)
            {
                EnsureCache(taiwuId);
                string id = CompanionItemId(date, npcId);
                if (!_current.TryGetValue(id, out string fingerprint)) return;
                _seen[id] = fingerprint;
                SaveSeen(taiwuId, _seen);
            }
        }

        public static void ResetForWorldExit()
        {
            lock (Gate)
            {
                Generating.Clear();
                _scope = null;
                _taiwuId = 0;
                _initialized = false;
                _dirty = false;
                _current.Clear();
                _seen.Clear();
            }
        }

        static void EnsureCache(int taiwuId)
        {
            string scope = ScopeFor(taiwuId);
            if (!_initialized || _taiwuId != taiwuId || !string.Equals(_scope, scope, StringComparison.Ordinal))
            {
                _scope = scope;
                _taiwuId = taiwuId;
                _current = BuildCurrent(taiwuId);
                if (!TryLoadSeen(taiwuId, out _seen))
                {
                    // 升级旧版本时不把多年旧档一次性全标红；从此刻的内容建立基线。
                    _seen = Clone(_current);
                    SaveSeen(taiwuId, _seen);
                }
                PruneSeenToCurrent();
                _initialized = true;
                _dirty = false;
                return;
            }
            if (!_dirty) return;
            _current = BuildCurrent(taiwuId);
            PruneSeenToCurrent();
            _dirty = false;
        }

        static void PruneSeenToCurrent()
        {
            if (_seen == null || _seen.Count == 0) return;
            var stale = new List<string>();
            foreach (string key in _seen.Keys)
                if (!_current.ContainsKey(key)) stale.Add(key);
            foreach (string key in stale) _seen.Remove(key);
        }

        static Dictionary<string, string> BuildCurrent(int taiwuId)
            => BuildCurrent(taiwuId, null);

        static Dictionary<string, string> BuildCurrent(int taiwuId, int? onlyDate)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            List<EventLogEntry> entries = EventLogStore.Load(taiwuId);
            for (int eventIndex = 0; eventIndex < entries.Count; eventIndex++)
            {
                EventLogEntry entry = entries[eventIndex];
                if (entry == null || onlyDate.HasValue && entry.Date != onlyDate.Value) continue;
                if (HasEventContent(entry))
                {
                    // JHYL_CHRONICLE_LEGACY_EVENT_STABLE_ID: EventLogStore 新记录在列表头插入，
                    // 所以不能把 eventIndex 当作旧记录身份。旧格式每月至多一条江湖事件，日期
                    // 本身就是稳定且足够的迁移键；新格式继续使用持久化 EventId。
                    string eventId = string.IsNullOrWhiteSpace(entry.EventId)
                        ? "legacy"
                        : entry.EventId.Trim();
                    result["event:" + entry.Date + ":" + eventId] = EventFingerprint(entry);
                }
                if (entry.CompanionActions == null) continue;
                for (int companionIndex = 0; companionIndex < entry.CompanionActions.Count; companionIndex++)
                {
                    CompanionMonthlyLogEntry companion = entry.CompanionActions[companionIndex];
                    if (companion == null) continue;
                    string id = companion.NpcId > 0
                        ? CompanionItemId(entry.Date, companion.NpcId)
                        : ("companion:" + entry.Date + ":unknown:" + eventIndex + ":" + companionIndex);
                    result[id] = CompanionFingerprint(companion);
                }
            }
            return result;
        }

        static bool HasEventContent(EventLogEntry entry)
            => entry != null
                && !(entry.EventId ?? string.Empty).StartsWith("monthly-digest:", StringComparison.Ordinal)
                && (!string.IsNullOrWhiteSpace(entry.Text)
                || !string.IsNullOrWhiteSpace(entry.Detail)
                || !string.IsNullOrWhiteSpace(entry.Brief)
                || entry.Actions != null && entry.Actions.Count > 0);

        static string EventFingerprint(EventLogEntry entry)
        {
            var text = new StringBuilder();
            Add(text, entry.Area); Add(text, entry.Text); Add(text, entry.Brief); Add(text, entry.Detail);
            if (entry.Actions != null) foreach (string action in entry.Actions) Add(text, action);
            return Fingerprint(text.ToString());
        }

        static string CompanionFingerprint(CompanionMonthlyLogEntry entry)
        {
            var text = new StringBuilder();
            Add(text, entry.Summary); Add(text, entry.Detail);
            if (entry.Outcomes != null) foreach (string outcome in entry.Outcomes) Add(text, outcome);
            return Fingerprint(text.ToString());
        }

        static void Add(StringBuilder builder, string value)
        {
            string text = value ?? string.Empty;
            builder.Append(text.Length).Append(':').Append(text).Append('|');
        }

        static string Fingerprint(string value)
        {
            // 稳定 FNV-1a；不能用 string.GetHashCode（跨进程随机化会把已读全部误判为未读）。
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                string text = value ?? string.Empty;
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    hash ^= (byte)c; hash *= 1099511628211UL;
                    hash ^= (byte)(c >> 8); hash *= 1099511628211UL;
                }
                return hash.ToString("x16");
            }
        }

        static Dictionary<string, string> Clone(Dictionary<string, string> source)
            => source == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(source, StringComparer.Ordinal);

        static bool TryLoadSeen(int taiwuId, out Dictionary<string, string> seen)
        {
            seen = null;
            string path = StatePath(taiwuId);
            if (!DurableFileStore.TryReadRecoverableText(path, MaxFileBytes, IsValidDocument,
                out string json, out _, out _)) return false;
            try
            {
                JObject root = JObject.Parse(json);
                JObject map = root["seen"] as JObject;
                var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
                if (map != null)
                    foreach (JProperty property in map.Properties())
                        parsed[property.Name] = property.Value?.ToString() ?? string.Empty;
                seen = parsed;
                return true;
            }
            catch { return false; }
        }

        static bool SaveSeen(int taiwuId, Dictionary<string, string> seen)
        {
            try
            {
                var map = new JObject();
                int count = 0;
                foreach (KeyValuePair<string, string> item in seen)
                {
                    if (count++ >= MaxSeenItems) break;
                    map[item.Key] = item.Value ?? string.Empty;
                }
                var root = new JObject { ["schemaVersion"] = SchemaVersion, ["seen"] = map };
                return DurableFileStore.TryWriteTextAtomic(StatePath(taiwuId),
                    root.ToString(Formatting.None), MaxFileBytes, IsValidDocument);
            }
            catch { return false; }
        }

        static bool IsValidDocument(string json)
        {
            if (!DurableFileStore.TryParseJsonStrict(json, 4, out JToken token)) return false;
            JObject root = token as JObject;
            if (root == null
                || !DurableFileStore.HasOnlyProperties(root, "schemaVersion", "seen")
                || root["schemaVersion"]?.Type != JTokenType.Integer
                || root["schemaVersion"].Value<int>() != SchemaVersion) return false;
            JObject map = root["seen"] as JObject;
            if (map == null || map.Count > MaxSeenItems) return false;
            foreach (JProperty property in map.Properties())
                if (property.Name.Length == 0 || property.Name.Length > 512
                    || property.Value.Type != JTokenType.String
                    || property.Value.ToString().Length > 128) return false;
            return true;
        }
    }
}
