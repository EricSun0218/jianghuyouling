using System;

namespace JianghuYouling.Core.Llm
{
    /// <summary>仅描述已由厂商协议确认的差异；未知兼容端保持最小 OpenAI 请求面。</summary>
    internal sealed class ProviderCapabilities
    {
        public bool IsOfficialDeepSeek;
        public bool IsDeepSeekV4;
        public bool IsDeepSeekV4Pro;
        public bool IsDeepSeekV4Flash;
        public bool IsLegacyDeepSeekAlias;
        public bool IsMiMo;
        public bool IsOfficialMiMo;
        /// <summary>
        /// DeepSeek V4 / Xiaomi MiMo 思考模式协议：同一工具循环内所有带 tool_calls 的助手
        /// 消息必须原样回灌 reasoning_content；反之，思考关闭的请求不得携带该字段。其他
        /// provider 不受此约束，也不会被主动发送未由自身响应产生的回灌字段。
        /// </summary>
        public bool RequiresThinkingReasoningReplay;
        public bool IsOfficialGoogle;
        public bool IsGemini;
        public bool IsMiniMax;
        public bool IsMiniMaxM3;
        public bool IsMiniMaxM2;
        /// <summary>
        /// 本机 Ollama 的 OpenAI 兼容端。识别官方默认回环端口，或回环地址上
        /// Ollama 特有的 name:tag 模型名；远程兼容服务不会进入此分支。
        /// </summary>
        public bool IsLocalOllama;
        /// <summary>
        /// 本机 Ollama 上的千问新开源 Qwen3.8。该模型使用原生 /api/chat，绕开
        /// Ollama 当前 /v1/chat/completions 对官方 27B 变体可能无响应的问题。
        /// </summary>
        public bool IsLocalOllamaQwen38;
        public bool IsQwen;
        public bool IsQwenThinkingOnly;
        public bool IsOfficialAnthropic;
        public bool AnthropicUsesAdaptiveThinking;
        public bool AnthropicUsesManualThinking;
        public bool AnthropicThinkingDefaultsOnCanDisable;
        public bool AnthropicThinkingAlwaysOn;
        public bool AnthropicForbidsSamplingParameters;
        public bool IsOfficialMoonshot;
        public bool IsAlibabaModelStudio;
        public bool IsKimiK3;
        public bool IsKimiK27Code;
        public bool IsKimiHybrid;
        public bool UsesMoonshotThinkingObject;
        public bool IsOfficialZhipu;
        public bool IsGlm;
        public bool IsGlm52;
        public bool IsOpenAiReasoningChat;
        /// <summary>
        /// 该模型确实接受 OpenAI 的 reasoning_effort 参数。IsOpenAiReasoningChat 的子集：
        /// o1-mini / o1-preview / gpt-5-chat 属于非推理变体，携带该字段会被直接 400，故排除。
        /// 仅门控 reasoning_effort 的下发，不改这些模型的 max_completion_tokens / 温度既有语义。
        /// </summary>
        public bool SupportsReasoningEffort;
        public bool UsesMaxCompletionTokens;
        public bool SupportsSamplingTemperature = true;
        /// <summary>
        /// Anthropic 的 OpenAI SDK 兼容层会静默忽略 response_format。静默忽略比显式
        /// 400 更危险，因为调用方会误以为 JSON 约束已生效，故在发包前按官方端点禁用。
        /// </summary>
        public bool SupportsResponseFormat = true;
        /// <summary>GLM 流式工具参数需要显式 tool_stream=true。</summary>
        public bool RequiresToolStream;
        public bool CumulativeStreamingContent;
        public bool CumulativeStreamingReasoning;
        public bool SupportsStreamUsage = true;
        /// <summary>
        /// DeepSeek V4 compatible endpoints can emit metadata/heartbeat SSE frames whose
        /// choices array is empty and whose usage value is absent or null.  They carry no
        /// semantic model output; completion is still proven by finish_reason + [DONE].
        /// Unknown providers remain strict so a malformed stream is never accepted broadly.
        /// </summary>
        public bool AllowsEmptyChoicesHeartbeat;
        /// <summary>
        /// The ggchan gcli OpenAI-compatible stream currently finishes a complete function
        /// call with finish_reason=stop instead of tool_calls. Keep this provider-scoped:
        /// unknown endpoints must continue to fail closed on the contradictory envelope.
        /// </summary>
        public bool AllowsStopFinishWithCompleteToolCalls;

        public static ProviderCapabilities Resolve(Uri endpoint, string model)
        {
            string host = (endpoint?.Host ?? "").ToLowerInvariant();
            string m = (model ?? "").Trim().ToLowerInvariant();
            var c = new ProviderCapabilities();

            c.IsLocalOllama = endpoint != null && endpoint.IsLoopback
                && (endpoint.Port == 11434 || LooksLikeOllamaModelTag(m));
            c.IsLocalOllamaQwen38 = c.IsLocalOllama && LooksLikeQwen38Model(m);

            c.IsOfficialDeepSeek = host == "api.deepseek.com" || host.EndsWith(".api.deepseek.com", StringComparison.Ordinal);
            c.IsDeepSeekV4 = m.Contains("deepseek-v4") || (c.IsOfficialDeepSeek && (m == "deepseek-chat" || m == "deepseek-reasoner"));
            // Interactive Pro orchestration is an explicit model protocol.  The retiring
            // deepseek-chat/reasoner aliases are still covered by V4 wire compatibility on the
            // official endpoint, but must not silently inherit Pro's auto-tool behavior.
            c.IsDeepSeekV4Pro = m == "deepseek-v4-pro";
            // Flash-specific orchestration must never leak into Pro or legacy aliases.  Only an
            // explicit Flash model id opts into the auto-only thinking tool protocol.
            c.IsDeepSeekV4Flash = m == "deepseek-v4-flash";
            c.IsLegacyDeepSeekAlias = c.IsOfficialDeepSeek && (m == "deepseek-chat" || m == "deepseek-reasoner");
            c.AllowsEmptyChoicesHeartbeat = c.IsDeepSeekV4;

            c.IsOfficialMiMo = host == "api.xiaomimimo.com"
                || host.EndsWith(".xiaomimimo.com", StringComparison.Ordinal);
            c.IsMiMo = MatchesModelFamily(m, "mimo-v2.5")
                || MatchesModelFamily(m, "mimo-v2.5-pro")
                || MatchesModelFamily(m, "mimo-v2.5-pro-ultraspeed")
                || c.IsOfficialMiMo;
            c.RequiresThinkingReasoningReplay = c.IsDeepSeekV4 || c.IsMiMo;

            c.IsOfficialGoogle = host == "generativelanguage.googleapis.com" || host.EndsWith(".aiplatform.googleapis.com", StringComparison.Ordinal);
            c.IsGemini = m.Contains("gemini");
            c.AllowsStopFinishWithCompleteToolCalls = host == "gcli.ggchan.dev" && c.IsGemini;

            c.IsMiniMax = m.Contains("minimax") || host == "api.minimax.io" || host == "api.minimaxi.com";
            c.IsMiniMaxM3 = c.IsMiniMax && (m.Contains("minimax-m3") || m.Contains("minimax/m3"));
            c.IsMiniMaxM2 = c.IsMiniMax && (m.Contains("minimax-m2") || m.Contains("minimax/m2"));
            c.CumulativeStreamingContent = c.IsMiniMaxM3;
            c.CumulativeStreamingReasoning = c.IsMiniMaxM3;

            c.IsQwen = m.Contains("qwen") || m.Contains("qwq");
            c.IsQwenThinkingOnly = m.Contains("qwq") || m.Contains("-thinking") || m.Contains("_thinking")
                || m.EndsWith("thinking", StringComparison.Ordinal)
                || MatchesModelFamily(m, "qwen3.7-max-preview")
                || MatchesModelFamily(m, "qwen3.7-max-2026-05-17")
                || MatchesModelFamily(m, "qwen3.8-max-preview");

            c.IsOfficialAnthropic = host == "api.anthropic.com";
            c.SupportsResponseFormat = !c.IsOfficialAnthropic;
            c.AnthropicThinkingDefaultsOnCanDisable = c.IsOfficialAnthropic
                && (m.StartsWith("claude-opus-5", StringComparison.Ordinal)
                    || m.StartsWith("claude-sonnet-5", StringComparison.Ordinal));
            c.AnthropicThinkingAlwaysOn = c.IsOfficialAnthropic
                && (m.StartsWith("claude-fable-5", StringComparison.Ordinal)
                    || m.StartsWith("claude-mythos-5", StringComparison.Ordinal)
                    || m.StartsWith("claude-mythos-preview", StringComparison.Ordinal));
            c.AnthropicUsesAdaptiveThinking = c.IsOfficialAnthropic
                && (m.StartsWith("claude-opus-4-6", StringComparison.Ordinal)
                    || m.StartsWith("claude-opus-4-7", StringComparison.Ordinal)
                    || m.StartsWith("claude-opus-4-8", StringComparison.Ordinal)
                    || m.StartsWith("claude-opus-5", StringComparison.Ordinal)
                    || m.StartsWith("claude-sonnet-4-6", StringComparison.Ordinal)
                    || m.StartsWith("claude-sonnet-5", StringComparison.Ordinal));
            c.AnthropicUsesManualThinking = c.IsOfficialAnthropic
                && (m.StartsWith("claude-opus-4-5", StringComparison.Ordinal)
                    || m.StartsWith("claude-sonnet-4-5", StringComparison.Ordinal)
                    || m.StartsWith("claude-haiku-4-5", StringComparison.Ordinal));
            c.AnthropicForbidsSamplingParameters = c.IsOfficialAnthropic
                && (m.StartsWith("claude-opus-4-7", StringComparison.Ordinal)
                    || m.StartsWith("claude-opus-4-8", StringComparison.Ordinal)
                    || m.StartsWith("claude-opus-5", StringComparison.Ordinal)
                    || m.StartsWith("claude-sonnet-5", StringComparison.Ordinal)
                    || m.StartsWith("claude-fable-5", StringComparison.Ordinal)
                    || m.StartsWith("claude-mythos-5", StringComparison.Ordinal)
                    || m.StartsWith("claude-mythos-preview", StringComparison.Ordinal));

            c.IsOfficialMoonshot = host == "api.moonshot.ai" || host == "api.moonshot.cn";
            c.IsAlibabaModelStudio = (host.Contains("dashscope")
                    && host.EndsWith(".aliyuncs.com", StringComparison.Ordinal))
                || host.EndsWith(".maas.aliyuncs.com", StringComparison.Ordinal);
            c.IsKimiK3 = MatchesModelFamily(m, "kimi-k3");
            c.IsKimiK27Code = MatchesModelFamily(m, "kimi-k2.7-code");
            c.IsKimiHybrid = MatchesModelFamily(m, "kimi-k2.6")
                || MatchesModelFamily(m, "kimi-k2.5");
            c.UsesMoonshotThinkingObject = c.IsOfficialMoonshot && c.IsKimiHybrid;
            // Alibaba's self-hosted bare Kimi deployments do not expose structured output.
            // Provider-supplied kimi/... deployments do, so keep their response_format support.
            if (c.IsAlibabaModelStudio && m.IndexOf('/') < 0
                && (c.IsKimiHybrid || c.IsKimiK27Code))
                c.SupportsResponseFormat = false;
            if (c.IsMiniMaxM2 || c.IsMiniMaxM3)
                c.SupportsResponseFormat = false;

            c.IsOfficialZhipu = host == "open.bigmodel.cn";
            c.IsGlm = MatchesModelFamily(m, "glm") || c.IsOfficialZhipu;
            c.IsGlm52 = MatchesModelFamily(m, "glm-5.2");
            c.RequiresToolStream = c.IsGlm
                && (c.IsOfficialZhipu || c.IsAlibabaModelStudio);

            c.IsOpenAiReasoningChat = StartsModelFamily(m, "o1") || StartsModelFamily(m, "o3")
                || StartsModelFamily(m, "o4") || IsKnownGpt5Family(m);
            // reasoning_effort 仅 o1/o3/o4/gpt-5 推理族支持；o1-mini/o1-preview 及 gpt-5-chat(-latest) 是非推理变体，
            // 携带该字段会被 OpenAI 直接 HTTP 400（"Unsupported parameter: reasoning_effort"），故用能力位精确排除。
            c.SupportsReasoningEffort = c.IsOpenAiReasoningChat
                && !m.StartsWith("o1-mini", StringComparison.Ordinal)
                && !m.StartsWith("o1-preview", StringComparison.Ordinal)
                && m.IndexOf("-chat", StringComparison.Ordinal) < 0;
            c.UsesMaxCompletionTokens = c.IsOpenAiReasoningChat || c.IsMiniMaxM3 || c.IsKimiK3 || c.IsMiMo;
            // Kimi K3 固定 temperature=1、top_p=.95、presence/frequency=0，官方要求调用方
            // 省略这些采样字段；这里与 OpenAI 推理族共用“不要发送 temperature”能力位。
            if (c.IsOpenAiReasoningChat || c.IsKimiK3 || c.IsKimiK27Code
                || c.IsKimiHybrid)
                c.SupportsSamplingTemperature = false;
            if (c.IsAlibabaModelStudio && (c.IsMiniMaxM2 || c.IsMiniMaxM3))
                c.SupportsSamplingTemperature = false;
            if (c.AnthropicForbidsSamplingParameters)
                c.SupportsSamplingTemperature = false;
            return c;
        }

        private static bool StartsModelFamily(string model, string family)
        {
            if (string.IsNullOrEmpty(model) || !model.StartsWith(family, StringComparison.Ordinal)) return false;
            return model.Length == family.Length || model[family.Length] == '-' || model[family.Length] == '_'
                || char.IsDigit(model[family.Length]);
        }

        private static bool LooksLikeOllamaModelTag(string model)
        {
            int colon = model?.LastIndexOf(':') ?? -1;
            return colon > 0 && colon + 1 < model.Length;
        }

        private static bool LooksLikeQwen38Model(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return false;
            int slash = model.LastIndexOf('/');
            string leaf = slash >= 0 && slash + 1 < model.Length
                ? model.Substring(slash + 1) : model;
            return string.Equals(leaf, "qwen3.8", StringComparison.Ordinal)
                || leaf.StartsWith("qwen3.8:", StringComparison.Ordinal)
                || leaf.StartsWith("qwen3.8-", StringComparison.Ordinal)
                || leaf.StartsWith("qwen3.8_", StringComparison.Ordinal);
        }

        private static bool MatchesModelFamily(string model, string family)
        {
            if (StartsModelFamily(model, family)) return true;
            int slash = model?.LastIndexOf('/') ?? -1;
            return slash >= 0 && slash + 1 < model.Length
                && StartsModelFamily(model.Substring(slash + 1), family);
        }

        private static bool IsKnownGpt5Family(string model)
            => StartsModelFamily(model, "gpt-5")
                || StartsModelFamily(model, "gpt-5.1")
                || StartsModelFamily(model, "gpt-5.2")
                || StartsModelFamily(model, "gpt-5.4")
                || StartsModelFamily(model, "gpt-5.5")
                || StartsModelFamily(model, "gpt-5.6");
    }
}
