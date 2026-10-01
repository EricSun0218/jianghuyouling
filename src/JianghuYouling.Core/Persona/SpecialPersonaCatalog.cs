using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Persona
{
    /// <summary>
    /// 内置特殊角色人设目录。角色只按 Config.Character.TemplateId 命中；绝不接收
    /// EventActors、运行时 charId 或名字猜测。净化后的正文与映射 manifest 均嵌入 Core DLL，
    /// 游戏运行时不依赖用户桌面目录。
    /// </summary>
    public static class SpecialPersonaCatalog
    {
        private const string ResourcePrefix = "JianghuYouling.Core.Persona.Resources.";
        private const string ManifestFile = "special-personas.manifest.json";
        private const string WorldBookFile = "default-worldbook.txt";
        private const string AssistantFile = "persona-assistant-linger.txt";
        private const short CricketFirstTemplateId = 968;
        private const short CricketLastTemplateId = 1011;

        private sealed class CatalogState
        {
            public readonly Dictionary<short, string> ExactResourceByTemplateId = new Dictionary<short, string>();
            public readonly Dictionary<short, string> ExactNameByTemplateId = new Dictionary<short, string>();
            public readonly Dictionary<short, string> FallbackResourceByTemplateId = new Dictionary<short, string>();
            public readonly Dictionary<short, string> FallbackNameByTemplateId = new Dictionary<short, string>();
            public readonly List<string> SanitizedResourceFiles = new List<string>();
            public string ManifestJson;
        }

        private static readonly object TextGate = new object();
        private static readonly Dictionary<string, string> TextCache =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Lazy<CatalogState> State = new Lazy<CatalogState>(LoadState, true);

        /// <summary>按权威 Character template id 取净化后的内置固定人设；普通角色返回 null。</summary>
        public static string Resolve(short characterTemplateId)
        {
            string resource;
            if (State.Value.ExactResourceByTemplateId.TryGetValue(characterTemplateId, out resource))
                return ReadResource(resource);
            if (State.Value.FallbackResourceByTemplateId.TryGetValue(characterTemplateId, out resource))
                return ReadResource(resource);
            return null;
        }

        /// <summary>供诊断/UI 使用的特殊角色名；蛐蛐通用兜底不伪造具体名字。</summary>
        public static string ResolveDisplayName(short characterTemplateId)
        {
            string displayName;
            if (State.Value.ExactNameByTemplateId.TryGetValue(characterTemplateId, out displayName))
                return displayName;
            return State.Value.FallbackNameByTemplateId.TryGetValue(characterTemplateId, out displayName)
                ? displayName : null;
        }

        public static string AssistantPersona
            => ReadResource(AssistantFile) ?? "带点江湖灵气、亲切机敏，忠于太吾并如实提供帮助。";

        public static string DefaultWorldBook
            => ReadResource(WorldBookFile) ?? Prompt.WorldLore.Base;

        public static string ManifestJson => State.Value.ManifestJson ?? "{}";

        public static short[] ExactTemplateIds
            => State.Value.ExactResourceByTemplateId.Keys.OrderBy(x => x).ToArray();

        /// <summary>仅供离线审计测试枚举嵌入净化资源，不用于对话路由。</summary>
        public static string[] SanitizedResourceFilesForAudit
            => State.Value.SanitizedResourceFiles.OrderBy(x => x, StringComparer.Ordinal).ToArray();

        /// <summary>仅供离线审计测试读取指定 manifest 白名单资源。</summary>
        public static string ReadSanitizedResourceForAudit(string resourceFile)
        {
            if (string.IsNullOrWhiteSpace(resourceFile)
                || !State.Value.SanitizedResourceFiles.Contains(resourceFile)) return null;
            return ReadResource(resourceFile);
        }

        private static CatalogState LoadState()
        {
            var state = new CatalogState();
            try
            {
                string manifest = ReadResource(ManifestFile);
                if (string.IsNullOrWhiteSpace(manifest)) return state;
                state.ManifestJson = manifest;
                var root = JObject.Parse(manifest);
                var mapping = root["mappingAuthority"] as JObject;
                if (!string.Equals(mapping?["namespace"]?.ToString(), "Config.Character.TemplateId", StringComparison.Ordinal))
                    return new CatalogState();

                AddAuditFile(state, ManifestFile);
                AddAuditFile(state, WorldBookFile);
                var entries = root["entries"] as JArray;
                if (entries != null)
                    foreach (var token in entries)
                    {
                        var entry = token as JObject;
                        if (entry == null) continue;
                        string resource = entry["resourceFile"]?.ToString();
                        string displayName = entry["displayName"]?.ToString();
                        AddAuditFile(state, resource);
                        string role = entry["role"]?.ToString();
                        bool exactRole = string.Equals(role, "character", StringComparison.Ordinal);
                        bool maleFallback = string.Equals(role, "cricket-fallback-male", StringComparison.Ordinal);
                        bool femaleFallback = string.Equals(role, "cricket-fallback-female", StringComparison.Ordinal);
                        if (!exactRole && !maleFallback && !femaleFallback) continue;
                        var ids = entry["characterTemplateIds"] as JArray;
                        if (ids == null) continue;
                        foreach (var idToken in ids)
                        {
                            int raw = idToken.Value<int>();
                            if (raw <= 0 || raw > short.MaxValue || string.IsNullOrWhiteSpace(resource)) continue;
                            short id = (short)raw;
                            if (exactRole)
                            {
                                // Duplicate exact IDs invalidate the manifest instead of allowing order-dependent routing.
                                if (state.ExactResourceByTemplateId.ContainsKey(id)) return new CatalogState();
                                state.ExactResourceByTemplateId.Add(id, resource);
                                state.ExactNameByTemplateId.Add(id, displayName ?? "特殊角色");
                            }
                            else
                            {
                                // Build 24185552 Character.ref.txt is authoritative: even cricket IDs are male,
                                // odd IDs are female.  Reject a crossed namespace/gender map instead of silently
                                // injecting a feminine card into a male character.
                                bool expectedMale = (id & 1) == 0;
                                if (id < CricketFirstTemplateId || id > CricketLastTemplateId
                                    || maleFallback != expectedMale || femaleFallback == expectedMale
                                    || state.FallbackResourceByTemplateId.ContainsKey(id)) return new CatalogState();
                                state.FallbackResourceByTemplateId.Add(id, resource);
                                state.FallbackNameByTemplateId.Add(id, displayName
                                    ?? (expectedMale ? "促织化形男通用" : "促织化形女通用"));
                            }
                        }
                    }

                if (!ValidateCricketFallback(root, state)) return new CatalogState();

                var worldBook = root["worldBook"] as JObject;
                AddAuditFile(state, worldBook?["resourceFile"]?.ToString());
                return state;
            }
            catch
            {
                // Optional persona routing fails closed; the rest of the mod and the built-in world lore remain usable.
                return new CatalogState();
            }
        }

        private static bool ValidateCricketFallback(JObject root, CatalogState state)
        {
            var fallback = root?["cricketFallback"] as JObject;
            if (fallback == null || fallback["firstTemplateId"]?.Value<int>() != CricketFirstTemplateId
                || fallback["lastTemplateId"]?.Value<int>() != CricketLastTemplateId) return false;
            string maleResource = fallback["maleResourceFile"]?.ToString();
            string femaleResource = fallback["femaleResourceFile"]?.ToString();
            if (!IsSafeResourceFile(maleResource) || !IsSafeResourceFile(femaleResource)
                || string.Equals(maleResource, femaleResource, StringComparison.Ordinal)) return false;

            var expected = new Dictionary<short, string>();
            if (!AddExpectedFallback(expected, fallback["maleTemplateIds"] as JArray, maleResource, true)
                || !AddExpectedFallback(expected, fallback["femaleTemplateIds"] as JArray, femaleResource, false)
                || expected.Count != CricketLastTemplateId - CricketFirstTemplateId + 1
                || state.FallbackResourceByTemplateId.Count != expected.Count) return false;
            for (short id = CricketFirstTemplateId; id <= CricketLastTemplateId; id++)
            {
                string expectedResource;
                string actualResource;
                if (!expected.TryGetValue(id, out expectedResource)
                    || !state.FallbackResourceByTemplateId.TryGetValue(id, out actualResource)
                    || !string.Equals(expectedResource, actualResource, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private static bool AddExpectedFallback(Dictionary<short, string> target, JArray ids,
            string resource, bool male)
        {
            if (target == null || ids == null || !IsSafeResourceFile(resource)) return false;
            foreach (var token in ids)
            {
                if (token == null || token.Type != JTokenType.Integer) return false;
                int raw = token.Value<int>();
                if (raw < CricketFirstTemplateId || raw > CricketLastTemplateId
                    || (((raw & 1) == 0) != male)) return false;
                short id = (short)raw;
                if (target.ContainsKey(id)) return false;
                target.Add(id, resource);
            }
            return true;
        }

        private static void AddAuditFile(CatalogState state, string file)
        {
            if (state == null || !IsSafeResourceFile(file) || state.SanitizedResourceFiles.Contains(file)) return;
            state.SanitizedResourceFiles.Add(file);
        }

        private static string ReadResource(string resourceFile)
        {
            if (!IsSafeResourceFile(resourceFile)) return null;
            lock (TextGate)
            {
                string cached;
                if (TextCache.TryGetValue(resourceFile, out cached)) return cached;
                try
                {
                    var assembly = typeof(SpecialPersonaCatalog).Assembly;
                    using (Stream stream = assembly.GetManifestResourceStream(ResourcePrefix + resourceFile))
                    {
                        if (stream == null) return null;
                        using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
                        {
                            string text = (reader.ReadToEnd() ?? "").Trim();
                            if (text.Length == 0) return null;
                            TextCache[resourceFile] = text;
                            return text;
                        }
                    }
                }
                catch { return null; }
            }
        }

        private static bool IsSafeResourceFile(string file)
        {
            if (string.IsNullOrWhiteSpace(file) || file.Length > 96 || file.IndexOf("..", StringComparison.Ordinal) >= 0)
                return false;
            for (int i = 0; i < file.Length; i++)
            {
                char c = file[i];
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '.') continue;
                return false;
            }
            return file.EndsWith(".txt", StringComparison.Ordinal) || file.EndsWith(".json", StringComparison.Ordinal);
        }
    }
}
