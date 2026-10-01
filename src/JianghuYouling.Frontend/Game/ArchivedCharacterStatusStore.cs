using System;
using System.Collections.Generic;
using System.IO;
using JianghuYouling.Core.Persistence;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JianghuYouling
{
    /// <summary>
    /// 保存已确认死亡人物的灵魂状态。聊天、群聊、人设、记忆和画像仍由各自原文件保存；
    /// 本表只负责让导航与灵魂会话稳定显示“已故”，绝不把“剧情退场/临时移除”猜成死亡。
    /// </summary>
    internal static class ArchivedCharacterStatusStore
    {
        private const int SchemaVersion = 1;
        private const int MaxPeople = 100000;
        private const int MaxFileBytes = 2 * 1024 * 1024;
        private static readonly object Gate = new object();
        private static string _scope;
        private static int _taiwuId;
        private static HashSet<int> _dead = new HashSet<int>();
        private static HashSet<int> _soulEntryHandled = new HashSet<int>();

        private static string ScopeFor(int taiwuId)
            => JianghuYoulingPaths.CurrentWorldId + "|" + taiwuId;

        private static string PathFor(int taiwuId)
            => Path.Combine(JianghuYoulingPaths.ChatLogs,
                "archived_character_status_" + taiwuId + ".json");

        internal static bool IsDead(int taiwuId, int npcId)
        {
            if (taiwuId <= 0 || npcId <= 0) return false;
            lock (Gate)
            {
                EnsureLoaded(taiwuId);
                return _dead.Contains(npcId);
            }
        }

        internal static bool NeedsSoulEntryHandling(int taiwuId, int npcId)
        {
            if (taiwuId <= 0 || npcId <= 0) return false;
            lock (Gate)
            {
                EnsureLoaded(taiwuId);
                return _dead.Contains(npcId) && !_soulEntryHandled.Contains(npcId);
            }
        }

        internal static bool MarkSoulEntryHandled(int taiwuId, int npcId)
        {
            if (taiwuId <= 0 || npcId <= 0) return false;
            lock (Gate)
            {
                EnsureLoaded(taiwuId);
                if (!_dead.Contains(npcId)) return false;
                if (_soulEntryHandled.Contains(npcId)) return true;
                if (!_soulEntryHandled.Add(npcId)) return true;
                if (Save(taiwuId)) return true;
                _soulEntryHandled.Remove(npcId);
                return false;
            }
        }

        /// <summary>
        /// 合并一次权威核验：alive 可撤销回档后过时的已故标记；dead 新增已故标记；
        /// missing 不传入，因此剧情退场和已被本体回收的旧死者都不会被误改。
        /// </summary>
        internal static bool ApplyAuthoritative(int taiwuId,
            IEnumerable<int> alive, IEnumerable<int> dead, out bool changed)
        {
            changed = false;
            if (taiwuId <= 0) return false;
            lock (Gate)
            {
                EnsureLoaded(taiwuId);
                var before = new HashSet<int>(_dead);
                var handledBefore = new HashSet<int>(_soulEntryHandled);
                if (alive != null)
                    foreach (int id in alive)
                        if (id > 0)
                        {
                            changed |= _dead.Remove(id);
                            changed |= _soulEntryHandled.Remove(id);
                        }
                if (dead != null)
                    foreach (int id in dead)
                        if (id > 0) changed |= _dead.Add(id);
                if (!changed) return true;
                if (Save(taiwuId)) return true;
                _dead = before;
                _soulEntryHandled = handledBefore;
                changed = false;
                return false;
            }
        }

        internal static void ResetForWorldExit()
        {
            lock (Gate)
            {
                _scope = null;
                _taiwuId = 0;
                _dead.Clear();
                _soulEntryHandled.Clear();
            }
        }

        private static void EnsureLoaded(int taiwuId)
        {
            string scope = ScopeFor(taiwuId);
            if (_taiwuId == taiwuId && string.Equals(_scope, scope,
                StringComparison.Ordinal)) return;
            _scope = scope;
            _taiwuId = taiwuId;
            _dead = new HashSet<int>();
            _soulEntryHandled = new HashSet<int>();
            if (!DurableFileStore.TryReadRecoverableText(PathFor(taiwuId), MaxFileBytes,
                raw => IsValidDocument(raw, taiwuId), out string json, out _, out _)
                || string.IsNullOrWhiteSpace(json)) return;
            try
            {
                JObject root = JObject.Parse(json);
                JArray ids = root["dead"] as JArray;
                if (ids == null) return;
                foreach (JToken token in ids)
                    if (token.Type == JTokenType.Integer)
                    {
                        int id = token.Value<int>();
                        if (id > 0) _dead.Add(id);
                    }
                JArray handled = root["soulEntryHandled"] as JArray;
                if (handled != null)
                    foreach (JToken token in handled)
                        if (token.Type == JTokenType.Integer)
                        {
                            int id = token.Value<int>();
                            if (id > 0 && _dead.Contains(id)) _soulEntryHandled.Add(id);
                        }
            }
            catch { _dead.Clear(); _soulEntryHandled.Clear(); }
        }

        private static bool Save(int taiwuId)
        {
            try
            {
                var ids = new List<int>(_dead);
                ids.Sort();
                var handled = new List<int>(_soulEntryHandled);
                handled.Sort();
                var root = new JObject
                {
                    ["schemaVersion"] = SchemaVersion,
                    ["worldId"] = JianghuYoulingPaths.CurrentWorldId,
                    ["taiwuId"] = taiwuId,
                    ["dead"] = new JArray(ids),
                    ["soulEntryHandled"] = new JArray(handled),
                };
                return DurableFileStore.TryWriteTextAtomic(PathFor(taiwuId),
                    root.ToString(Formatting.None), MaxFileBytes,
                    raw => IsValidDocument(raw, taiwuId));
            }
            catch { return false; }
        }

        private static bool IsValidDocument(string json, int taiwuId)
        {
            if (!DurableFileStore.TryParseJsonStrict(json, 8, out JToken token)
                || !(token is JObject root)
                || !DurableFileStore.HasOnlyProperties(root,
                    "schemaVersion", "worldId", "taiwuId", "dead", "soulEntryHandled")
                || root["schemaVersion"]?.Type != JTokenType.Integer
                || root["schemaVersion"].Value<int>() != SchemaVersion
                || root["worldId"]?.Type != JTokenType.Integer
                || root["worldId"].Value<uint>() != JianghuYoulingPaths.CurrentWorldId
                || root["taiwuId"]?.Type != JTokenType.Integer
                || root["taiwuId"].Value<int>() != taiwuId
                || !(root["dead"] is JArray ids) || ids.Count > MaxPeople) return false;
            JArray handled = root["soulEntryHandled"] as JArray;
            // 兼容本功能开发期已经生成的首版状态文件；下一次成功写入会补齐处理记录。
            if (root["soulEntryHandled"] != null && handled == null) return false;
            if (handled != null && handled.Count > MaxPeople) return false;
            var seen = new HashSet<int>();
            foreach (JToken idToken in ids)
            {
                if (idToken.Type != JTokenType.Integer) return false;
                int id = idToken.Value<int>();
                if (id <= 0 || !seen.Add(id)) return false;
            }
            var handledSeen = new HashSet<int>();
            if (handled != null)
                foreach (JToken idToken in handled)
                {
                    if (idToken.Type != JTokenType.Integer) return false;
                    int id = idToken.Value<int>();
                    if (id <= 0 || !seen.Contains(id) || !handledSeen.Add(id)) return false;
                }
            return true;
        }
    }
}
