using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Persistence;

namespace JianghuYouling
{
    /// <summary>灵儿(助手)对话历史持久化:把滚动历史(user/assistant)存盘,跨重开游戏保留,开窗时回放——把灵儿当 NPC 一样有历史。
    /// 存于当前 WorldId 的 ChatLogs/assistant_history_太吾Id.json;只留最近 MaxKeep 条。</summary>
    public static class AssistantHistoryStore
    {
        const int MaxKeep = 40;
        const int MaxDiskItems = 400;
        const int MaxFileBytes = 2 * 1024 * 1024;
        static readonly object Sync = new object();
        static readonly HashSet<string> Unreliable = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        static string PathFor(int taiwuId) => Path.Combine(JianghuYoulingPaths.ChatLogs,
            taiwuId > 0 ? ("assistant_history_" + taiwuId + ".json") : "assistant_history.json");

        public static System.DateTime LastActivityUtc(int taiwuId)
        {
            try
            {
                string path = PathFor(taiwuId);
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : System.DateTime.MinValue;
            }
            catch { return System.DateTime.MinValue; }
        }

        public static List<LlmMessage> Load(int taiwuId)
        {
            var list = new List<LlmMessage>();
            lock (Sync) try
            {
                var p = PathFor(taiwuId);
                // v1 单一文件只迁移复制一次；之后每个太吾各自读写，绝不跨存档串历史。
                bool any;
                string source;
                if (!DurableFileStore.TryReadRecoverableText(p, MaxFileBytes, IsValidDocument,
                    out string json, out any, out source) && !any && taiwuId > 0)
                {
                    string legacy = PathFor(0);
                    if (DurableFileStore.TryReadRecoverableText(legacy, MaxFileBytes, IsValidDocument,
                        out string oldJson, out _, out _) &&
                        DurableFileStore.TryWriteTextAtomic(p, oldJson, MaxFileBytes, IsValidDocument))
                        json = oldJson;
                }
                if (json == null)
                {
                    if (any) Unreliable.Add(p); else Unreliable.Remove(p);
                    return list;
                }
                Unreliable.Remove(p);
                var arr = JArray.Parse(json);
                int start = arr.Count > MaxKeep ? arr.Count - MaxKeep : 0;
                for (int index = start; index < arr.Count; index++)
                {
                    var it = arr[index];
                    bool fromPlayer = it.Value<bool?>("p") ?? false;
                    string text = it.Value<string>("t") ?? "";
                    if (string.IsNullOrEmpty(text)) continue;
                    var message = fromPlayer ? LlmMessage.User(text) : LlmMessage.Assistant(text);
                    message.IsProactive = !fromPlayer && (it.Value<bool?>("a") ?? false);
                    list.Add(message);
                }
            }
            catch { }
            return list;
        }

        /// <summary>原子写入助手历史。只有主文件已完整替换后才返回 true；失败时保留旧主文件。</summary>
        public static bool Save(List<LlmMessage> history, int taiwuId)
            => SaveCore(history, taiwuId, false);

        private static bool SaveCore(List<LlmMessage> history, int taiwuId, bool explicitReset)
        {
            lock (Sync) try
            {
                string path = PathFor(taiwuId);
                if (!explicitReset && Unreliable.Contains(path)) return false;
                if (!explicitReset)
                {
                    bool any;
                    if (!DurableFileStore.TryReadRecoverableText(path, MaxFileBytes, IsValidDocument,
                        out _, out any, out _) && any)
                    { Unreliable.Add(path); return false; }
                }
                var flat = new List<LlmMessage>();
                if (history != null)
                    foreach (var m in history)
                        if (m != null && (m.Role == "user" || m.Role == "assistant") && !string.IsNullOrEmpty(m.Content))
                            flat.Add(m);
                int start = flat.Count > MaxKeep ? flat.Count - MaxKeep : 0;
                var arr = new JArray();
                for (int i = start; i < flat.Count; i++)
                {
                    var o = new JObject();
                    o["p"] = flat[i].Role == "user";
                    o["t"] = flat[i].Content;
                    if (flat[i].IsProactive) o["a"] = true;
                    arr.Add(o);
                }
                bool saved = DurableFileStore.TryWriteTextAtomic(path, arr.ToString(), MaxFileBytes, IsValidDocument);
                if (saved) Unreliable.Remove(path);
                return saved;
            }
            catch { return false; }
        }

        /// <summary>
        /// 原子持久化“空历史”。保留一个空数组主文件可阻止旧版 assistant_history.json 在下次载入时被重新迁回。
        /// 重复清空同一文件仍成功，便于 UI 在 I/O 故障解除后幂等重试。
        /// </summary>
        public static bool Clear(int taiwuId)
        {
            var empty = new List<LlmMessage>();
            return SaveCore(empty, taiwuId, true) && SaveCore(empty, taiwuId, true);
        }

        private static bool IsValidDocument(string json)
        {
            if (!DurableFileStore.TryParseJsonStrict(json, 12, out JToken root)
                || !(root is JArray array) || array.Count > MaxDiskItems) return false;
            foreach (JToken token in array)
            {
                if (!(token is JObject item) || !DurableFileStore.HasOnlyProperties(item, "p", "t", "a")
                    || item["p"]?.Type != JTokenType.Boolean
                    || !DurableFileStore.IsBoundedString(item["t"], 65536, false)
                    || (item["a"] != null && item["a"].Type != JTokenType.Boolean)) return false;
                if (string.IsNullOrEmpty(item.Value<string>("t"))) return false;
            }
            return true;
        }
    }
}
