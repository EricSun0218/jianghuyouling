using System;
using System.IO;
using Newtonsoft.Json.Linq;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Security;

namespace JianghuYouling
{
    /// <summary>
    /// 加载 LLM 客户端。配置来源优先级:① 游戏内设置页写入的 llm.json ②回退旧版模组管理设置。
    /// 旧版模组管理设置仅兼容历史用户,避免覆盖新设置页保存的配置。
    /// </summary>
    public static class LlmService
    {
        private static OpenAiCompatibleClient _client;
        private static bool _loaded;
        private static OpenAiCompatibleClient _bgClient;   // 后台模型 client(画像/记忆精排/代笔);未单设则为 null、回退主 client
        private static bool _bgLoaded;

        // 把 Core 的大模型性能日志接到 Unity 日志(落 Player.log)。静态构造在任何 GetClient/调用前先跑一次。
        static LlmService()
        {
            LlmLog.Sink = s => { try { UnityEngine.Debug.Log(s); } catch { } };
        }

        public static bool IsConfigured
        {
            get
            {
                var client = GetClient();
                return client != null && string.IsNullOrWhiteSpace(client.ConfigurationError);
            }
        }

        public static OpenAiCompatibleClient GetClient()
        {
            if (_loaded) return _client;
            _loaded = true;
            try
            {
                var c = Resolve();
                if (!c.ok) { _client = null; return null; }
                _client = new OpenAiCompatibleClient(c.baseUrl, c.chatPath, c.apiKey, c.model);
                ApplyMaxTokens(_client, c.maxTok);
                ApplyContextWindow(_client, c.contextWindow);
            }
            catch { _client = null; }
            return _client;
        }

        /// <summary>无副作用辅助调用(画像蒸馏、记忆精排、代笔等)专用 client:与主 client 同一接口/密钥/路径,
        /// 仅模型名换成「后台模型」。设置里留空、或与主模型相同 → 直接复用主 client。会改变游戏状态的江湖事件与同道过月不得调用此入口。</summary>
        public static OpenAiCompatibleClient GetBackgroundClient()
        {
            if (_bgLoaded) return _bgClient ?? GetClient();
            _bgLoaded = true;
            try
            {
                var c = Resolve();
                if (!c.ok || string.IsNullOrWhiteSpace(c.bgModel) || c.bgModel == c.model)
                { _bgClient = null; return GetClient(); }   // 未单设后台模型 → 用主 client
                _bgClient = new OpenAiCompatibleClient(c.baseUrl, c.chatPath, c.apiKey, c.bgModel);
                ApplyMaxTokens(_bgClient, c.maxTok);
                ApplyContextWindow(_bgClient, c.contextWindow);
            }
            catch { _bgClient = null; return GetClient(); }
            return _bgClient;
        }

        const int DefaultMaxTokens = 32768;
        const int DefaultContextWindow = OpenAiCompatibleClient.DefaultContextWindowTokens;
        internal const int ContextWindowDefaultVersion = 3;
        internal const string ContextWindowDefaultVersionField = "contextWindowDefaultVersion";
        internal const int MaxTokensDefaultVersion = 1;
        internal const string MaxTokensDefaultVersionField = "maxTokensDefaultVersion";
        private const int LegacyContextWindowDefault = 32768;
        private const int PreviousContextWindowDefault = 262144;
        private const int LegacyMaxTokensDefault = 8192;

        // JHYL_MAX_TOKENS_DEFAULT_32768
        // 设置里的 max_tokens:留空 → 显式使用运行默认 32768;填 0 → 不发 max_tokens(由服务端按模型上限放开);填正数 → 照此为上限。
        private static void ApplyMaxTokens(OpenAiCompatibleClient client, string raw)
        {
            if (client == null) return;
            if (string.IsNullOrWhiteSpace(raw)) { client.DefaultMaxTokens = DefaultMaxTokens; return; }
            if (int.TryParse(raw.Trim(), out int v) && v >= 0) client.DefaultMaxTokens = v;
        }

        private static void ApplyContextWindow(OpenAiCompatibleClient client, string raw)
        {
            if (client == null) return;
            // llm.json 缺少该字段（含旧版迁移配置）或留空时使用统一 1M 默认预算。
            // 该值只决定本地裁剪上限，不会填充请求；8K/16K 本地模型须在设置页显式写实际值。
            if (string.IsNullOrWhiteSpace(raw)) { client.ContextWindowTokens = DefaultContextWindow; return; }
            if (int.TryParse(raw.Trim(), out int v) && v >= 8192) client.ContextWindowTokens = v;
            else client.ContextWindowTokens = DefaultContextWindow;
        }

        /// <summary>
        /// 旧版设置页会把当时的 32768 默认值直接写入 llm.json。若没有版本标记，
        /// 这个值无法与“玩家主动选择 32768”区分，只能按旧默认迁移一次；玩家在新版
        /// 再明确保存 32768 时会带版本标记，之后始终原样保留。
        /// </summary>
        internal static string ReadConfiguredContextWindow(JObject config)
        {
            string raw = config?["contextWindow"]?.ToString();
            int version = config?[ContextWindowDefaultVersionField]?.Value<int?>() ?? 0;
            if (int.TryParse((raw ?? string.Empty).Trim(), out int value))
            {
                if (version < 2 && value == LegacyContextWindowDefault)
                    return DefaultContextWindow.ToString();
                string model = (config?["model"]?.ToString() ?? string.Empty).Trim();
                bool deepSeekV4 = model.IndexOf("deepseek-v4", StringComparison.OrdinalIgnoreCase) >= 0;
                // v2/262144 may also be a deliberate cap for a local or smaller compatible
                // endpoint.  Auto-upgrade only the DeepSeek V4 family whose official window is
                // known to be one million; never silently overstate an unknown model's capacity.
                if (version < ContextWindowDefaultVersion && value == PreviousContextWindowDefault && deepSeekV4)
                    return DefaultContextWindow.ToString();
            }
            return raw;
        }

        /// <summary>
        /// 旧设置页会把当时的 8192 默认直接写入配置。没有新版本标记时，只在当前
        /// 上下文确实容得下 32768 输出和安全余量时迁移；旧本地 8K/16K/32K 模型
        /// 必须保留 8192，否则升级后输入预算会直接变成 0。
        /// </summary>
        internal static string ReadConfiguredMaxTokens(JObject config)
        {
            string raw = config?["maxTokens"]?.ToString();
            int version = config?[MaxTokensDefaultVersionField]?.Value<int?>() ?? 0;
            if (version < MaxTokensDefaultVersion
                && int.TryParse((raw ?? string.Empty).Trim(), out int value)
                && value == LegacyMaxTokensDefault)
            {
                string contextRaw = config?["contextWindow"]?.ToString();
                bool explicitSmallContext = int.TryParse((contextRaw ?? string.Empty).Trim(),
                    out int contextWindow) && contextWindow > 0
                    && contextWindow < DefaultMaxTokens + 1024;
                if (!explicitSmallContext) return DefaultMaxTokens.ToString();
            }
            return raw;
        }

        /// <summary>按当前模型上下文、输出上限与安全余量计算真实输入预算。</summary>
        public static int InputBudgetFor(OpenAiCompatibleClient client)
            => client != null ? client.EffectiveInputBudget() : 0;

        private struct RawCfg { public string baseUrl, apiKey, chatPath, model, maxTok, bgModel, contextWindow; public bool ok; }

        // 统一解析配置:优先 llm.json(游戏内「设置」写入),回退旧版模组管理设置页(Config.lua 曾声明的 llm_* InputField)。
        // baseUrl 与 model 都非空才算配置成功(本地模型 apiKey 可空);bgModel 可空。主/后台 client 共用此结果,只是取的模型名不同。
        private static RawCfg Resolve()
        {
            var c = new RawCfg { chatPath = "/chat/completions" };
            // ① 当前游戏内设置页
            // JHYL_LLM_JSON_FIRST:旧 Config.lua 残留值不应覆盖玩家在设置页保存的 llm.json。
            try
            {
                string path = Path.Combine(JianghuYoulingPaths.Settings, "llm.json");
                if (!ProtectedConfigFile.TryGetReplicaPresence(path, out bool anyReplica, out _)) return c;
                if (anyReplica)
                {
                    if (!ProtectedConfigFile.TryLoad(path, out var o, out var protectedApiKey, out _)) return c;
                    string b = o["baseUrl"]?.ToString(); string m = o["model"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(b) && !string.IsNullOrWhiteSpace(m))
                    {
                        c.baseUrl = b.Trim(); c.apiKey = (protectedApiKey ?? "").Trim(); c.model = m.Trim();
                        string cp = o["chatPath"]?.ToString();
                        c.chatPath = string.IsNullOrWhiteSpace(cp) ? "/chat/completions" : cp.Trim();
                        c.maxTok = ReadConfiguredMaxTokens(o);
                        c.contextWindow = ReadConfiguredContextWindow(o);
                        c.bgModel = (o["bgModel"]?.ToString() ?? "").Trim();
                        c.ok = true;
                        return c;
                    }
                }
            }
            catch { }
            // ② 旧版模组管理设置页(兼容回退)
            try
            {
                string modId = Plugin.Instance?.ModIdStr;
                if (!string.IsNullOrEmpty(modId))
                {
                    string b = null, k = null, m = null, cp = null, mt = null, bg = null;
                    ModManager.GetSetting(modId, "llm_base_url", ref b);
                    ModManager.GetSetting(modId, "llm_api_key", ref k);
                    ModManager.GetSetting(modId, "llm_model", ref m);
                    ModManager.GetSetting(modId, "llm_chat_path", ref cp);
                    ModManager.GetSetting(modId, "llm_max_tokens", ref mt);
                    ModManager.GetSetting(modId, "llm_bg_model", ref bg);
                    if (!string.IsNullOrWhiteSpace(b) && !string.IsNullOrWhiteSpace(m))
                    {
                        string migratedMaxTokens = ReadConfiguredMaxTokens(new JObject
                        {
                            ["maxTokens"] = mt ?? ""
                        });
                        var migrated = new JObject
                        {
                            ["baseUrl"] = b.Trim(),
                            ["chatPath"] = string.IsNullOrWhiteSpace(cp) ? "/chat/completions" : cp.Trim(),
                            ["model"] = m.Trim(),
                            ["bgModel"] = (bg ?? "").Trim(),
                            ["maxTokens"] = migratedMaxTokens ?? "",
                            [MaxTokensDefaultVersionField] = MaxTokensDefaultVersion,
                            ["contextWindow"] = DefaultContextWindow.ToString(),
                            [ContextWindowDefaultVersionField] = ContextWindowDefaultVersion
                        };
                        string path = Path.Combine(JianghuYoulingPaths.Settings, "llm.json");
                        if (!ProtectedConfigFile.TrySave(path, migrated, (k ?? "").Trim(), out _)) return c;
                        c.baseUrl = b.Trim(); c.apiKey = (k ?? "").Trim(); c.model = m.Trim();
                        if (!string.IsNullOrWhiteSpace(cp)) c.chatPath = cp.Trim();
                        c.maxTok = migratedMaxTokens; c.bgModel = (bg ?? "").Trim();
                        c.contextWindow = DefaultContextWindow.ToString(); c.ok = true;
                        return c;
                    }
                }
            }
            catch { }
            return c;
        }

        public static void Reload() { _loaded = false; _client = null; _bgLoaded = false; _bgClient = null; }

        /// <summary>
        /// 读取 llm.json 原始 JObject(供配置 UI 预填用)。文件缺失/解析失败返回 null。
        /// 不影响 GetClient 的懒加载缓存。
        /// </summary>
        public static JObject ReadRaw()
        {
            try
            {
                string path = Path.Combine(JianghuYoulingPaths.Settings, "llm.json");
                if (!ProtectedConfigFile.TryGetReplicaPresence(path, out bool anyReplica, out _)
                    || !anyReplica) return null;
                return ProtectedConfigFile.TryLoadForDisplay(path, out var display, out _) ? display : null;
            }
            catch { return null; }
        }

        /// <summary>供设置页保存；apiKey 只以 DPAPI CurrentUser 密文落盘，失败时不写明文。</summary>
        public static bool SaveRaw(JObject configWithoutSecret, string apiKey, out string error)
        {
            string path = Path.Combine(JianghuYoulingPaths.Settings, "llm.json");
            bool ok = ProtectedConfigFile.TrySave(path, configWithoutSecret, (apiKey ?? "").Trim(), out error);
            if (ok) Reload();
            return ok;
        }
    }
}
