using System;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;
using JianghuYouling.Core.Security;
using JianghuYouling.Core.Web;

namespace JianghuYouling
{
    /// <summary>语音(TTS)配置:默认火山 Seed-TTS 2.0，也支持 MiniMax、OpenAI 兼容与 DashScope Qwen。
    /// 只读取 Settings/tts.json 中独立配置的 provider/baseUrl/apiKey/model；不会复用主模型密钥。
    /// key 仅以 Windows DPAPI CurrentUser 密文存本地。另含按性别/年龄选音色、按内容定语速情绪。</summary>
    public static class TtsConfig
    {
        const string DefaultSeedTts2Endpoint = "https://openspeech.bytedance.com/api/v3/tts/unidirectional";
        const string DefaultSeedTts2Resource = "seed-tts-2.0";
        static string PathFor() => Path.Combine(JianghuYoulingPaths.Settings, "tts.json");
        static bool _legacyMiniMaxMigrated;
        static long _cacheRevision = 1;
        public static string LastSaveError { get; private set; }
        public static long CacheRevision => Interlocked.Read(ref _cacheRevision);

        /// <summary>(provider: "volcengine-seed-audio"|"minimax"|"openai"|"dashscope-qwen", baseUrl, apiKey, model)。只读显式 tts.json;未配置=全空。
        /// 刻意不回退到主 LLM/MiniMax 接口——语音是可选项，没配就是没配，不会复用主模型密钥。</summary>
        public static (string provider, string baseUrl, string apiKey, string model) Resolve()
        {
            try
            {
                if (!EnsureLegacyMiniMaxProtected(out _)) return (null, null, null, null);
                var p = PathFor();
                if (!ProtectedConfigFile.TryGetReplicaPresence(p, out bool anyReplica, out _))
                    return (null, null, null, null);
                if (anyReplica)
                {
                    if (!ProtectedConfigFile.TryLoad(p, out var o, out var k, out _)) return (null, null, null, null);
                    string b = o["baseUrl"]?.ToString(), m = o["model"]?.ToString(), prov = o["provider"]?.ToString();
                    // 语音整体可选:接口/模型默认已填好,真正的开关是「密钥」——没填密钥=不启用。
                    if (!string.IsNullOrWhiteSpace(k) && !string.IsNullOrWhiteSpace(b))
                    {
                        if (string.IsNullOrWhiteSpace(m))
                        {
                            if (!TtsSettings.TryLoad(out var ttsSettings)) return (null, null, null, null);
                            m = ttsSettings.Model;
                        }
                        // The former built-in Seed Audio 1.0 default uses the same new-console
                        // API Key. Upgrade its effective runtime endpoint immediately so a user
                        // need not open and resave settings before the first 2.0 playback.
                        if (IsBuiltInSeedAudio1Default(prov, b, m))
                        {
                            prov = TtsProviderUtil.ProviderVolcengineSeedAudio;
                            b = DefaultSeedTts2Endpoint;
                            m = DefaultSeedTts2Resource;
                        }
                        if (string.IsNullOrWhiteSpace(prov)) prov = TtsProviderUtil.ResolveProvider(b, m);
                        else prov = TtsProviderUtil.ResolveProvider(b, m, prov);
                        return (prov, b.Trim(), k.Trim(), (m ?? "").Trim());
                    }
                }
            }
            catch { }
            return (null, null, null, null);
        }

        static bool IsBuiltInSeedAudio1Default(string provider, string baseUrl, string model)
        {
            string p = (provider ?? "").Trim().ToLowerInvariant();
            string b = (baseUrl ?? "").Trim().TrimEnd('/').ToLowerInvariant();
            string m = (model ?? "").Trim().ToLowerInvariant();
            return (p.Length == 0 || p == TtsProviderUtil.ProviderVolcengineSeedAudio)
                && b == "https://openspeech.bytedance.com/api/v3/tts/create"
                && m == "seed-audio-1.0";
        }

        /// <summary>语音是否已启用(填了密钥)。接口/模型有默认值,故用密钥作整体开关;未启用时「语音」按钮引导去设置页。</summary>
        public static bool IsConfigured { get { var r = Resolve(); return !string.IsNullOrWhiteSpace(r.apiKey); } }

        /// <summary>读 tts.json 原始(provider/baseUrl/apiKey),供设置页回填;无则返回空串三元组。</summary>
        public static (string provider, string baseUrl, string apiKey) LoadRaw()
        {
            try
            {
                if (!EnsureLegacyMiniMaxProtected(out _)) return ("", "", "");
                var p = PathFor();
                if (!ProtectedConfigFile.TryGetReplicaPresence(p, out bool anyReplica, out _))
                    return ("", "", "");
                if (anyReplica)
                {
                    if (!ProtectedConfigFile.TryLoad(p, out var o, out var apiKey, out _)) return ("", "", "");
                    return (o["provider"]?.ToString() ?? "", o["baseUrl"]?.ToString() ?? "", apiKey ?? "");
                }
            }
            catch { }
            return ("", "", "");
        }

        public static bool SaveExplicit(string provider, string baseUrl, string apiKey, string model)
        {
            LastSaveError = null;
            if (!EnsureLegacyMiniMaxProtected(out var migrationError))
            {
                LastSaveError = migrationError;
                return false;
            }
            var o = new JObject
            {
                ["provider"] = provider ?? "",
                ["baseUrl"] = baseUrl ?? "",
                ["model"] = model ?? ""
            };
            bool ok = ProtectedConfigFile.TrySave(PathFor(), o, (apiKey ?? "").Trim(), out var error);
            LastSaveError = error;
            if (ok)
            {
                Interlocked.Increment(ref _cacheRevision);
                try { VoicePlayer.InvalidateCacheForConfigurationChange(); } catch { }
            }
            return ok;
        }

        static bool EnsureLegacyMiniMaxProtected(out string error)
        {
            error = null;
            if (_legacyMiniMaxMigrated) return true;
            string legacyPath = Path.Combine(JianghuYoulingPaths.Settings, "minimax.json");
            if (!ProtectedConfigFile.TryMigrateLegacyFile(legacyPath, out error)) return false;
            _legacyMiniMaxMigrated = true;
            return true;
        }

        /// <summary>按 provider + 性别(0女1男)+ 年龄选音色。助手用甜美女声。</summary>
        public static string PickVoice(string provider, bool isAssistant, int gender, int age, string features, string behavior = null)
        {
            if (provider == TtsProviderUtil.ProviderVolcengineSeedAudio)
            {
                if (isAssistant || gender == 0) return "zh_female_vv_uranus_bigtts";
                string source = (features ?? "") + " " + (behavior ?? "");
                return age >= 42 || ContainsAny(source, "冷静", "沉着", "谨慎", "多疑", "冷漠", "文雅")
                    ? "zh_male_dayi_saturn_bigtts"
                    : "zh_male_dayi_uranus_bigtts";
            }
            if (provider == "openai")
            {
                // OpenAI 系统音色少(alloy/echo/fable/onyx/nova/shimmer)。#13 只按性别+年龄粗分,不再掺性情(反而选不准)。
                if (isAssistant) return "shimmer";
                if (gender == 0) return age >= 42 ? "shimmer" : "nova";   // 女:年长 shimmer / 年轻 nova
                return age >= 45 ? "onyx" : "alloy";                      // 男:年长 onyx / 年轻 alloy
            }
            if (provider == TtsProviderUtil.ProviderDashScopeQwen)
            {
                if (isAssistant) return "Cherry";
                return gender == 0 ? "Cherry" : "Ethan";
            }
            // minimax
            if (isAssistant) return MiniMaxConfig.AssistantVoice;
            return MiniMaxConfig.PickVoice(gender, age, features, behavior);
        }

        public static string PerformanceProfile(string provider, bool isAssistant, int gender, int age,
            string features, string behavior = null)
        {
            if (provider != TtsProviderUtil.ProviderVolcengineSeedAudio) return "";
            if (isAssistant) return "年轻灵动的女性伙伴，清亮亲近，机敏而温柔";
            string lifeStage = age < 16 ? "少年" : age < 30 ? "年轻" : age < 50 ? "成熟" : "年长";
            string sex = gender == 0 ? "女性" : "男性";
            string source = (features ?? "") + " " + (behavior ?? "");
            string temperament = ContainsAny(source, "刚烈", "勇", "豪", "莽", "暴躁", "桀骜")
                ? "有力量、直率果断"
                : ContainsAny(source, "仁善", "温柔", "慈", "谦", "文雅", "热情")
                    ? "温和真诚、富有亲和力"
                    : ContainsAny(source, "冷静", "沉着", "谨慎", "多疑", "冷漠")
                        ? "克制沉稳、略带距离感"
                        : ContainsAny(source, "活泼", "机敏", "聪慧", "幽默", "乐观")
                            ? "灵动自然、反应敏捷"
                            : "自然可信、符合江湖人物身份";
            return lifeStage + sex + "，" + temperament + "，保持同一人物声线稳定";
        }

        /// <summary>按回话内容定语速+情绪(启发式):激动/感叹→快+喜,悲→慢+哀,愤怒→快+怒,平静→常速。</summary>
        public static (float speed, string emotion) Prosody(string text)
        {
            if (string.IsNullOrEmpty(text)) return (1f, null);
            int ex = Count(text, '!') + Count(text, '！');
            bool sad = ContainsAny(text, "难过", "伤心", "悲", "哀", "泪", "痛", "唉", "叹", "可惜", "遗憾") || text.Contains("…");
            bool angry = ContainsAny(text, "可恶", "该死", "休得", "放肆", "岂有此理", "滚", "无耻", "卑鄙");
            bool happy = ContainsAny(text, "哈哈", "好极", "妙", "痛快", "甚好", "大喜", "太好了", "欢喜");
            bool fearful = ContainsAny(text, "小心", "别过来", "救命", "糟了", "危险", "害怕", "恐怕");
            bool surprised = ContainsAny(text, "什么", "竟然", "怎么会", "不可能", "原来如此")
                || Count(text, '?') + Count(text, '？') > 0;
            if (angry) return (1.12f, "angry");
            if (fearful) return (1.08f, "fearful");
            if (surprised) return (1.08f, "surprised");
            if (ex >= 2 || happy) return (1.14f, "happy");
            if (ex == 1) return (1.06f, "happy");
            if (sad) return (0.88f, "sad");
            return (1.0f, "neutral");
        }

        static int Count(string s, char c) { int n = 0; foreach (var ch in s) if (ch == c) n++; return n; }
        static bool ContainsAny(string s, params string[] keys) { foreach (var k in keys) if (s.IndexOf(k, StringComparison.Ordinal) >= 0) return true; return false; }
    }
}
