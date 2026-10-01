using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using JianghuYouling.Core.Prompt;

namespace JianghuYouling.Core.Llm
{
    /// <summary>
    /// OpenAI 兼容多 provider 客户端(MiniMax / DeepSeek / OpenAI / Qwen / 本地 Ollama·vLLM·llama.cpp 等)。
    /// 非流式。两类调用:① SendAsync 纯文本/JSON 完成(画像蒸馏、对话压缩、记忆选取等辅助);
    /// ② SendToolRoundAsync 工具调用一轮(主对话的 agentic 循环用)。自动剥离 &lt;think&gt; 与 reasoning_content。可独立单测。
    /// </summary>
    public sealed class OpenAiCompatibleClient
    {
        // Google documents this exact sentinel for function-call history that has no
        // original Gemini thought signature (for example, a compatibility gateway
        // that stripped provider metadata). Never use it for the official Google
        // endpoint: an official Gemini 3 response is required to carry a real signature.
        internal const string GeminiSignatureCompatibilitySentinel = "skip_thought_signature_validator";
        private static readonly HttpClient Http = CreateHttp();
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        // 全局并发上限:群聊/过月会同时发多路 LLM 请求,严格接口(尤其国内中转)峰值并发一高就 429 / 连接被重置。
        // 用一个进程级信号量给所有聊天补全限流(每次实际 HTTP 请求占一格,完成即释放),超出的排队等候——不丢请求、只削峰。
        // 日志实测:并发 5 时国内中转大量「remote party closed transport stream / An error occurred while sending the request」→ 降到 3 削峰。
        private const int MaxConcurrentRequests = 3;
        private static readonly LlmPriorityConcurrencyGate _gate =
            new LlmPriorityConcurrencyGate(MaxConcurrentRequests);
        // DeepSeek V4 官方端点有明确且远高于本模组需求的并发额度。月度事件与五名人物
        // 各自仍保持单链串行，但可并行推进，避免通用中转端实测得出的三路保守闸让
        // 六条互不冲突的主动行动链累计数分钟排队。只放宽官方主机 + 显式 V4 模型。
        private const int MaxOfficialDeepSeekV4ConcurrentRequests = 8;
        private static readonly LlmPriorityConcurrencyGate _officialDeepSeekV4Gate =
            new LlmPriorityConcurrencyGate(MaxOfficialDeepSeekV4ConcurrentRequests);
        // Ollama 官方默认同一模型只并行处理 1 路。画像、召回、灵儿主动消息与玩家对话
        // 若按云端并发 3 路同时打进本机 9B 模型，会放大上下文/显存占用，并在低资源机器
        // 上表现为连接重置。只给官方默认回环端口单独串行；云端与其他兼容端仍走原 3 路闸。
        private static readonly LlmPriorityConcurrencyGate _localOllamaGate =
            new LlmPriorityConcurrencyGate(1);
        // 非月度后台工作流最多占两格。过月江湖事件/同道 Agent 可在前台无人等待时
        // 借满全局三格；一旦单聊、群聊、代笔等前台请求排队，下一枚空槽优先交给前台。
        private static readonly System.Threading.SemaphoreSlim _backgroundGate = new System.Threading.SemaphoreSlim(2, 2);
        private readonly string _endpoint;
        private readonly Uri _endpointUri;
        private readonly string _apiKey;
        private readonly string _model;
        private readonly string _configurationError;
        private readonly ProviderCapabilities _capabilities;
        private int _migrationWarningEmitted;
        private int _reasoningOffWarningEmitted;
        private bool _jsonModeSupported = true;   // 探测:provider 拒收 response_format 后置 false
        private volatile bool _reasoningEffortRejected;   // 探测:严格端拒收 reasoning_effort(按名未识别的非推理/兼容端)后置 true,后续请求剥离该字段,不再每轮 400
        // Some Ollama model templates can generate ordinary chat but cannot construct the native
        // tool parser.  Once the server states that exact capability failure, remember it for this
        // client and preserve conversation in a strictly non-action text mode.
        private int _nativeToolsRejected;
        private const string NativeToolsUnavailableInstruction =
            "当前本地模型模板不支持结构化工具调用。本轮只能进行普通对话：不得声称已经查询、修改或执行任何游戏状态；若玩家要求实际行动，应明确说明当前模型不支持工具调用，需更换支持 tools/function calling 的模型。";

        public OpenAiCompatibleClient(string baseUrl, string chatPath, string apiKey, string model)
        {
            _endpoint = NormalizeEndpoint(baseUrl, chatPath);
            _apiKey = apiKey ?? "";
            _model = (model ?? "").Trim();
            if (!Uri.TryCreate(_endpoint, UriKind.Absolute, out _endpointUri))
                _configurationError = "LLM endpoint 必须是绝对 http/https URL";
            else
                _configurationError = ValidateTransportConfiguration(_endpointUri, _apiKey);
            _capabilities = ProviderCapabilities.Resolve(_endpointUri, _model);
            _streamOptionsSupported = _capabilities.SupportsStreamUsage;
            _jsonModeSupported = _capabilities.SupportsResponseFormat;
        }

        /// <summary>旧别名不会被客户端偷换；调用方可在设置/连接测试处直接展示迁移告警。</summary>
        public string ModelMigrationWarning => _capabilities.IsLegacyDeepSeekAlias
            ? "DeepSeek 官方已公告 deepseek-chat/deepseek-reasoner 将于 2026-07-24 退役；请显式迁移到 deepseek-v4-flash 或 deepseek-v4-pro。当前配置未被自动改写。"
            : null;

        /// <summary>传输配置的只读校验结果；不包含 API key，null 表示配置可用。</summary>
        public string ConfigurationError => _configurationError;

        /// <summary>
        /// DeepSeek V4 Flash 的思考工具协议只支持省略 tool_choice 的 auto 模式。
        /// 编排器据此用强约束提示和本地结果门控保持思考，不切换 provider 私有工具模式。
        /// </summary>
        public bool UsesDeepSeekV4FlashThinkingToolProtocol => _capabilities.IsDeepSeekV4Flash;

        /// <summary>
        /// DeepSeek V4 Pro has only high/max reasoning levels. This capability is deliberately
        /// exact and never includes the retiring deepseek-chat/reasoner aliases.
        /// </summary>
        public bool UsesDeepSeekV4ProThinkingProtocol
            => _capabilities.IsDeepSeekV4Pro;

        /// <summary>
        /// Recommended number of independently progressing monthly planners. The wire gate
        /// remains authoritative. Official DeepSeek V4 can keep six companion chains warm beside
        /// the event chain while preserving one of its eight slots for foreground chat; generic
        /// cloud endpoints retain the proven conservative scheduler and local Ollama stays serial.
        /// </summary>
        public int RecommendedMonthlyPlannerConcurrency
            => _capabilities.IsLocalOllama ? 1
                : _capabilities.IsOfficialDeepSeek && _capabilities.IsDeepSeekV4 ? 6 : 4;

        // 端点规范化:补前导斜杠、避免把完整 endpoint 与 path 重复拼接(消除 .../v1chat/... 与双 path 404)。
        private static string NormalizeEndpoint(string baseUrl, string chatPath)
        {
            string b = (baseUrl ?? "").Trim().TrimEnd('/');
            string p = (chatPath ?? "").Trim();
            if (string.IsNullOrEmpty(p)) p = "/chat/completions";
            if (!p.StartsWith("/")) p = "/" + p;
            // Normalize only provider roots whose documented OpenAI-compatible prefix is known.
            // Google documents https://generativelanguage.googleapis.com/v1beta/openai/ as the
            // OpenAI base URL. Players commonly paste either the host root or /v1beta; accepting
            // those forms prevents a valid Gemini key from being sent to the native-API path by
            // mistake. Do not rewrite arbitrary Google paths (or any unknown provider).
            Uri configured;
            if (string.Equals(p, "/chat/completions", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(b, UriKind.Absolute, out configured))
            {
                string configuredPath = (configured.AbsolutePath ?? "").TrimEnd('/');
                if (string.Equals(configured.Host, "generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(configuredPath)) b += "/v1beta/openai";
                    else if (string.Equals(configuredPath, "/v1beta", StringComparison.OrdinalIgnoreCase)) b += "/openai";
                }
                else if (string.Equals(configured.Host, "gcli.ggchan.dev", StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrEmpty(configuredPath))
                {
                    b += "/v1";
                }
            }
            if (b.EndsWith(p, StringComparison.OrdinalIgnoreCase)) return b;
            if (b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return b;
            return b + p;
        }

        private static HttpClient CreateHttp()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            // 关键:Mono/.NET 的 ServicePointManager.DefaultConnectionLimit 默认仅 2 → 非流式 HttpClient 同主机最多 2 条连接,
            // 与并发闸(3)不匹配:第 3 路在连接层排队/失败(日志大量 5s「An error occurred while sending the request」根因之一)。放开到 64,让并发闸成为唯一的真实限流。
            try { if (ServicePointManager.DefaultConnectionLimit < 64) ServicePointManager.DefaultConnectionLimit = 64; } catch { }
            try { ServicePointManager.Expect100Continue = false; } catch { }   // 省一次 100-continue 往返,降首字延迟
            try
            {
                var handler = new HttpClientHandler { MaxResponseHeadersLength = LlmProtocolLimits.MaxHttpHeaderBytes / 1024 };
                return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(300) };
            }
            catch { return new HttpClient { Timeout = TimeSpan.FromSeconds(300) }; }
        }

        private static string ValidateTransportConfiguration(Uri endpoint, string apiKey)
        {
            if (endpoint == null) return "LLM endpoint 无效";
            if (!string.IsNullOrEmpty(endpoint.UserInfo)) return "LLM endpoint 不得包含 userinfo";
            if (SecretRedactor.ContainsHeaderBreak(apiKey)) return "API key 含非法换行符";
            bool https = string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            bool http = string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
            if (!https && !http) return "LLM endpoint 只允许 http/https";
            if (http && !IsLoopbackHost(endpoint.Host)) return "远程 LLM endpoint 必须使用 HTTPS；HTTP 仅允许本机回环地址";
            if (http && string.Equals(endpoint.DnsSafeHost, "localhost", StringComparison.OrdinalIgnoreCase)
                && !LocalhostResolvesOnlyToLoopback())
                return "localhost 解析到了非回环地址，已拒绝 HTTP 请求";
            if (!https && !string.IsNullOrWhiteSpace(apiKey) && !IsLoopbackHost(endpoint.Host))
                return "带 API key 的远程请求必须使用 HTTPS";
            return null;
        }

        private static bool LocalhostResolvesOnlyToLoopback()
        {
            try
            {
                var addresses = Dns.GetHostAddresses("localhost");
                if (addresses == null || addresses.Length == 0) return false;
                foreach (var address in addresses) if (!IPAddress.IsLoopback(address)) return false;
                return true;
            }
            catch { return false; }
        }

        private void EmitProviderWarnings()
        {
            string warning = ModelMigrationWarning;
            if (warning != null && Interlocked.Exchange(ref _migrationWarningEmitted, 1) == 0)
                LlmLog.Line("[江湖有灵][LLM配置告警] " + warning);
        }

        private void EmitReasoningPolicyWarning(LlmReasoningPolicy policy)
        {
            if (policy != LlmReasoningPolicy.Off
                || (!_capabilities.IsMiniMaxM2 && !_capabilities.IsQwenThinkingOnly
                    && !_capabilities.IsKimiK3 && !_capabilities.IsKimiK27Code)) return;
            if (Interlocked.Exchange(ref _reasoningOffWarningEmitted, 1) == 0)
                LlmLog.Line("[江湖有灵][LLM配置告警] 当前模型是 thinking-only，无法真正关闭内部推理；客户端仍会把推理与可见正文严格分离。");
        }

        // 瞬时连接级错误(并发峰值下国内中转常见):连接被重置 / 传输流关闭 / 发送失败 → 值得短暂退避后重试一次。
        // 真正的鉴权/参数错误走 HTTP 状态码(有 resp 返回),不会抛到这里,故不会误重试。
        private static bool IsTransientConnError(Exception ex)
        {
            // Unity/Mono 在 ReadAsStringAsync 解析残缺 chunked 响应时直接抛 WebException
            // (ServerProtocolViolation, "Expecting chunk trailer.")，不会包装成 HttpRequestException。
            // 沿异常链精确识别该传输中断；证书 TrustFailure 等永久错误不能因此被误重试。
            for (var cur = ex; cur != null; cur = cur.InnerException)
            {
                if (cur is System.IO.InvalidDataException) return false;
                if (cur is System.Net.Http.HttpRequestException || cur is System.IO.IOException || cur is System.Net.Sockets.SocketException) return true;
                var e = (cur.Message ?? "").ToLowerInvariant();
                if (e.Contains("expecting chunk trailer")) return true;
                if (e.Contains("error occurred while sending") || e.Contains("transport connection")
                    || e.Contains("transport stream") || e.Contains("connection was closed")
                    || e.Contains("connection reset") || e.Contains("已中止")) return true;
            }
            return false;
        }

        /// <summary>工作流层只可重试瞬时服务错误；鉴权、参数、上下文过长等永久错误必须立即返回。</summary>
        public static bool IsRetryableServiceError(string error)
        {
            if (string.IsNullOrWhiteSpace(error)) return false;
            string e = error.ToLowerInvariant();
            // 显式 HTTP/限流状态优先于正文措辞；例如“API key rate limit (429)”不能被 api key 字样误判为永久鉴权错误。
            if (e.Contains("429") || e.Contains("rate limit") || e.Contains("too many requests")
                || e.Contains("http 408") || e.Contains("http 409") || e.Contains("http 425")
                || e.Contains("http 500") || e.Contains("http 502") || e.Contains("http 503") || e.Contains("http 504"))
                return true;
            if (e.Contains("401") || e.Contains("403") || e.Contains("unauthorized") || e.Contains("forbidden")
                || e.Contains("api key") || e.Contains("authentication") || e.Contains("鉴权") || e.Contains("密钥")
                || e.Contains("context length") || e.Contains("maximum context") || e.Contains("too many tokens")
                || e.Contains("上下文") || e.Contains("invalid request") || e.Contains("bad request") || e.Contains("http 400"))
                return false;
            return e.Contains("请求超时") || e.Contains("timeout") || e.Contains("timed out")
                || e.Contains("temporarily unavailable") || e.Contains("overloaded")
                || e.Contains("expecting chunk trailer") || e.Contains("connection reset")
                || e.Contains("transport stream") || e.Contains("connection was closed")
                || e.Contains("error occurred while sending") || e.Contains("已中止")
                || e.Contains("httprequestexception") || e.Contains("ioexception")
                || e.Contains("socketexception") || e.Contains("webexception");
        }

        // 每次请求的输出上限(注意:推理模型的思考 token 也算在内)。DeepSeek V4 官方
        // Agent 接入示例使用 32768；低于模型真实思考需要的硬上限会在 finish_reason=length
        // 时丢掉完整动作与正文。调用处传 0(或负)=用这个实例默认；把实例默认设为 0
        // 则不发 max_tokens，由服务端按模型自身上限决定。
        public int DefaultMaxTokens { get; set; } = 32768;

        /// <summary>
        /// 云端主力模型的默认上下文预算。默认主模型 DeepSeek V4 的官方服务已把
        /// 1M 作为标准窗口；世界书、人设、技能、记忆和多轮工具回执不应再按旧 256K
        /// 默认提前裁剪。较小本地模型或兼容网关必须在设置页显式填写实际值。
        /// </summary>
        public const int DefaultContextWindowTokens = 1000000;

        // 保留旧公开常量名，避免第三方测试/旧编译引用断裂；语义已升级为当前默认值。
        public const int ConservativeDefaultContextWindowTokens = DefaultContextWindowTokens;

        private int _contextWindowTokens = ConservativeDefaultContextWindowTokens;
        /// <summary>调用方声明的模型上下文窗口；最小 8192，未声明时按 1000000。</summary>
        public int ContextWindowTokens
        {
            get => _contextWindowTokens;
            set => _contextWindowTokens = Math.Max(8192, value);
        }

        /// <summary>Provider/model identity used only for local token-estimator calibration.</summary>
        public string TokenBudgetIdentity
            => (_endpointUri?.Host ?? "invalid") + "|" + (_model ?? string.Empty);

        /// <summary>上下文减去输出预留与安全余量后的推荐输入预算。</summary>
        public int EffectiveInputBudget(int maxOutputTokens = 0, int safetyTokens = 1024)
        {
            int output;
            if (maxOutputTokens > 0)
                output = maxOutputTokens;
            else if (DefaultMaxTokens > 0)
                output = DefaultMaxTokens;
            else
                output = Math.Min(8192, Math.Max(1024, ContextWindowTokens / 4));
            int safety = Math.Max(0, safetyTokens);
            long budget = (long)ContextWindowTokens - output - safety;
            int raw = budget <= 0 ? 0 : (int)Math.Min(int.MaxValue, budget);
            return PromptTokenCalibrator.CalibratedBudget(TokenBudgetIdentity, raw);
        }

        // ===== 纯文本 / JSON 完成(辅助调用:画像、压缩、记忆选取)=====
        public Task<LlmResult> SendAsync(IList<LlmMessage> messages, int maxTokens = 0,
            double temperature = 0.8, CancellationToken ct = default, int timeoutSec = 120, bool jsonObject = false, string tag = null,
            LlmReasoningPolicy reasoningPolicy = LlmReasoningPolicy.Auto)
            => SendAsyncWithTrace(messages, maxTokens, temperature, ct, timeoutSec, jsonObject, tag, reasoningPolicy, null);

        public async Task<LlmResult> SendAsyncWithTrace(IList<LlmMessage> messages, int maxTokens = 0,
            double temperature = 0.8, CancellationToken ct = default, int timeoutSec = 120, bool jsonObject = false, string tag = null,
            LlmReasoningPolicy reasoningPolicy = LlmReasoningPolicy.Auto, LlmTraceContext trace = null)
        {
            EmitProviderWarnings();
            EmitReasoningPolicyWarning(reasoningPolicy);
            int __estimatedPrompt = PromptBudgeter.Estimate(messages, null);
            var __sw = System.Diagnostics.Stopwatch.StartNew();
            long __workflowQueueMs = 0;
            LlmResult __res;
            try
            {
                var __execution = await RunWithWorkflowGateAsync(tag, ct, workload =>
                    // SendRawAsync applies timeoutSec only after its global provider slot is acquired.
                    SendAsyncCore(messages, maxTokens, temperature, ct, timeoutSec, jsonObject,
                        reasoningPolicy, workload)).ConfigureAwait(false);
                __res = __execution.Result;
                __workflowQueueMs = __execution.QueueMs;
            }
            catch (OperationCanceledException)
            { __res = new LlmResult { Ok = false, Canceled = true, Error = "已取消" }; }
            __res = Sanitize(__res); __res.QueueMs += __workflowQueueMs;
            PromptTokenCalibrator.Observe(TokenBudgetIdentity, __estimatedPrompt, __res.PromptTokens);
            LlmLog.RecordWithTrace(SecretRedactor.Redact(tag, _apiKey), SecretRedactor.Redact(_model, _apiKey), "纯文本", __sw.ElapsedMilliseconds, __res.PromptTokens, __res.CompletionTokens, __res.Ok, __res.Error,
                __res.CachedTokens, __res.CacheWriteTokens, __res.QueueMs, __res.TtftMs, __res.RetryCount, __res.RetryClass,
                __res.ReasoningTokens, __res.TotalTokens, __res.CacheMissTokens, TraceForResult(trace, __res));
            return __res;
        }

        private async Task<LlmResult> SendAsyncCore(IList<LlmMessage> messages, int maxTokens = 0,
            double temperature = 0.8, CancellationToken ct = default, int timeoutSec = 120, bool jsonObject = false,
            LlmReasoningPolicy reasoningPolicy = LlmReasoningPolicy.Auto,
            LlmWorkloadKind workload = LlmWorkloadKind.Foreground)
        {
            if (_configurationError != null) return new LlmResult { Ok = false, Error = _configurationError };
            bool tryJson = jsonObject && _jsonModeSupported;
            var raw = await SendRawAsync(BuildBodyWithPolicy(messages, maxTokens, temperature, tryJson, null, null, false, reasoningPolicy), ct, timeoutSec, workload).ConfigureAwait(false);
            string jsonModeError = raw.error;
            if (jsonModeError == null && (raw.status < 200 || raw.status >= 300))
                jsonModeError = ExtractError(raw.text, raw.status);
            if (jsonObject && !raw.canceled && tryJson && LooksLikeJsonModeUnsupported(jsonModeError))
            {
                _jsonModeSupported = false;   // 此 provider 不支持 response_format,关掉重发
                var retried = await SendRawAsync(BuildBodyWithPolicy(messages, maxTokens, temperature, false, null, null, false, reasoningPolicy), ct, timeoutSec, workload).ConfigureAwait(false);
                raw = CombineRawMetrics(raw, retried, "json_mode_unsupported");
            }
            // 严格端拒收 reasoning_effort → 记忆并剥离该字段重发一次(json 模式沿用当前已探明状态)。
            if (!raw.canceled && raw.error == null && (raw.status < 200 || raw.status >= 300)
                && ShouldRetryWithoutReasoningEffort(ExtractError(raw.text, raw.status)))
            {
                var retried = await SendRawAsync(BuildBodyWithPolicy(messages, maxTokens, temperature, jsonObject && _jsonModeSupported, null, null, false, reasoningPolicy), ct, timeoutSec, workload).ConfigureAwait(false);
                raw = CombineRawMetrics(raw, retried, "reasoning_effort_unsupported");
            }
            if (raw.canceled) return ApplyMetrics(new LlmResult { Ok = false, Canceled = true, Error = "已取消" }, raw);
            if (raw.error != null) return ApplyMetrics(new LlmResult { Ok = false, Error = raw.error }, raw);
            return ApplyMetrics(Parse(raw.text, raw.status), raw);
        }

        // ===== 工具调用一轮:发 messages + tools,返回 ToolCalls 或最终 Content =====
        public Task<LlmToolResult> SendToolRoundAsync(IList<LlmMessage> messages, IList<ToolDef> tools,
            string toolChoice = "auto", int maxTokens = 0, double temperature = 0.8,
            CancellationToken ct = default, int timeoutSec = 120, string tag = null, bool noThinking = false,
            LlmReasoningPolicy reasoningPolicy = LlmReasoningPolicy.Auto)
            => SendToolRoundAsyncWithTrace(messages, tools, toolChoice, maxTokens, temperature, ct, timeoutSec,
                tag, noThinking, reasoningPolicy, null);

        public async Task<LlmToolResult> SendToolRoundAsyncWithTrace(IList<LlmMessage> messages, IList<ToolDef> tools,
            string toolChoice = "auto", int maxTokens = 0, double temperature = 0.8,
            CancellationToken ct = default, int timeoutSec = 120, string tag = null, bool noThinking = false,
            LlmReasoningPolicy reasoningPolicy = LlmReasoningPolicy.Auto, LlmTraceContext trace = null)
        {
            EmitProviderWarnings();
            EmitReasoningPolicyWarning(noThinking ? LlmReasoningPolicy.Off : reasoningPolicy);
            bool __textOnly = Volatile.Read(ref _nativeToolsRejected) != 0 && tools != null && tools.Count > 0;
            IList<LlmMessage> __estimatedMessages = __textOnly
                ? WithNativeToolsUnavailableInstruction(messages)
                : messages;
            int __estimatedPrompt = PromptBudgeter.Estimate(__estimatedMessages, __textOnly ? null : tools);
            var __sw = System.Diagnostics.Stopwatch.StartNew();
            long __workflowQueueMs = 0;
            LlmToolResult __res;
            try
            {
                var __execution = await RunWithWorkflowGateAsync(tag, ct, workload =>
                    SendToolRoundCoreAsync(messages, tools, toolChoice, maxTokens, temperature,
                        ct, timeoutSec, noThinking, reasoningPolicy, workload)).ConfigureAwait(false);
                __res = __execution.Result;
                __workflowQueueMs = __execution.QueueMs;
            }
            catch (OperationCanceledException)
            { __res = new LlmToolResult { Ok = false, Canceled = true, Error = "已取消" }; }
            __res = Sanitize(__res); __res.QueueMs += __workflowQueueMs;
            PromptTokenCalibrator.Observe(TokenBudgetIdentity, __estimatedPrompt, __res.PromptTokens);
            LlmLog.RecordWithTrace(SecretRedactor.Redact(tag, _apiKey), SecretRedactor.Redact(_model, _apiKey), "工具", __sw.ElapsedMilliseconds, __res.PromptTokens, __res.CompletionTokens, __res.Ok, __res.Error,
                __res.CachedTokens, __res.CacheWriteTokens, __res.QueueMs, __res.TtftMs, __res.RetryCount, __res.RetryClass,
                __res.ReasoningTokens, __res.TotalTokens, __res.CacheMissTokens, TraceForResult(trace, __res));
            return __res;
        }

        private sealed class WorkflowExecution<T>
        {
            public T Result;
            public long QueueMs;
        }

        private static async Task<WorkflowExecution<T>> RunWithWorkflowGateAsync<T>(
            string tag, CancellationToken cancellationToken,
            Func<LlmWorkloadKind, Task<T>> run)
        {
            LlmWorkloadKind workload = LlmWorkloadPolicy.Classify(tag);
            bool enteredBackground = false;
            long queueMs = 0;
            try
            {
                if (workload == LlmWorkloadKind.Background)
                {
                    var queue = System.Diagnostics.Stopwatch.StartNew();
                    // Queueing is not model execution. Do not consume the wire timeout while
                    // healthy portrait/memory/summary work waits behind another background job.
                    await _backgroundGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    queueMs = queue.ElapsedMilliseconds;
                    enteredBackground = true;
                }

                return new WorkflowExecution<T>
                {
                    Result = await run(workload).ConfigureAwait(false),
                    QueueMs = queueMs,
                };
            }
            finally
            {
                if (enteredBackground) _backgroundGate.Release();
            }
        }

        private async Task<LlmToolResult> SendToolRoundCoreAsync(IList<LlmMessage> messages, IList<ToolDef> tools,
            string toolChoice = "auto", int maxTokens = 0, double temperature = 0.8,
            CancellationToken ct = default, int timeoutSec = 120, bool noThinking = false,
            LlmReasoningPolicy reasoningPolicy = LlmReasoningPolicy.Auto,
            LlmWorkloadKind workload = LlmWorkloadKind.Foreground)
        {
            if (_configurationError != null) return new LlmToolResult { Ok = false, Error = _configurationError };
            if (!ValidateToolRequest(tools, toolChoice, out var normalizedChoice, out var toolError))
                return new LlmToolResult { Ok = false, Error = toolError };
            toolChoice = normalizedChoice;
            bool textOnlyFallback = tools != null && tools.Count > 0
                && Volatile.Read(ref _nativeToolsRejected) != 0;
            var requestMessages = textOnlyFallback ? WithNativeToolsUnavailableInstruction(messages) : messages;
            var body = BuildBodyWithPolicy(requestMessages, maxTokens, temperature, false,
                textOnlyFallback ? null : ToolsToJson(tools), toolChoice, noThinking, reasoningPolicy);
            var raw = await SendRawAsync(body, ct, timeoutSec, workload).ConfigureAwait(false);
            if (raw.canceled) return ApplyMetrics(new LlmToolResult { Ok = false, Canceled = true, Error = "已取消" }, raw);
            if (raw.error != null && !textOnlyFallback
                && TryDisableUnsupportedNativeTools(raw.error))
            {
                // HTTP 4xx failures are transport errors and never reach ParseToolValidated below.
                // Handle the exact Ollama template-capability response here, then continue through
                // the normal parse/metrics path with the text-only retry.
                var retried = await SendRawAsync(BuildBodyWithPolicy(
                    WithNativeToolsUnavailableInstruction(messages), maxTokens, temperature,
                    false, null, toolChoice, noThinking, reasoningPolicy),
                    ct, timeoutSec, workload).ConfigureAwait(false);
                raw = CombineRawMetrics(raw, retried, "ollama_native_tools_unsupported_text_only");
                textOnlyFallback = true;
                if (raw.canceled) return ApplyMetrics(new LlmToolResult { Ok = false, Canceled = true, Error = "已取消" }, raw);
            }
            if (raw.error != null) return ApplyMetrics(new LlmToolResult { Ok = false, Error = raw.error }, raw);
            var parsed = ParseToolValidatedWithSchemaForProvider(raw.text, raw.status, RequiresGeminiToolSignature(),
                AllowsGeminiSignatureCompatibilityFallback(), tools, toolChoice);
            if (!parsed.Ok && ShouldRetryWithoutReasoningEffort(parsed.Error))
            {
                // 严格端拒收 reasoning_effort：记忆后剥离该字段重发一次（保持 toolChoice/thinking 语义不变）。
                var retried = await SendRawAsync(BuildBodyWithPolicy(requestMessages,
                    maxTokens, temperature, false,
                    textOnlyFallback ? null : ToolsToJson(tools), toolChoice,
                    noThinking, reasoningPolicy), ct, timeoutSec, workload).ConfigureAwait(false);
                raw = CombineRawMetrics(raw, retried, "reasoning_effort_unsupported");
                if (raw.canceled) return ApplyMetrics(new LlmToolResult { Ok = false, Canceled = true, Error = "已取消" }, raw);
                if (raw.error != null) return ApplyMetrics(new LlmToolResult { Ok = false, Error = raw.error }, raw);
                parsed = ParseToolValidatedWithSchemaForProvider(raw.text, raw.status, RequiresGeminiToolSignature(),
                    AllowsGeminiSignatureCompatibilityFallback(), tools, toolChoice);
            }
            if (!parsed.Ok && !textOnlyFallback && TryDisableUnsupportedNativeTools(parsed.Error))
            {
                // The model itself is still usable for dialogue. Retry exactly once without a
                // tools field and add a fail-closed instruction so prose cannot impersonate a
                // successful game action. Subsequent turns skip the known-bad tools request.
                var retried = await SendRawAsync(BuildBodyWithPolicy(
                    WithNativeToolsUnavailableInstruction(messages), maxTokens, temperature,
                    false, null, toolChoice, noThinking, reasoningPolicy),
                    ct, timeoutSec, workload).ConfigureAwait(false);
                raw = CombineRawMetrics(raw, retried, "ollama_native_tools_unsupported_text_only");
                if (raw.canceled) return ApplyMetrics(new LlmToolResult { Ok = false, Canceled = true, Error = "已取消" }, raw);
                if (raw.error != null) return ApplyMetrics(new LlmToolResult { Ok = false, Error = raw.error }, raw);
                parsed = ParseToolValidatedWithSchemaForProvider(raw.text, raw.status,
                    RequiresGeminiToolSignature(), AllowsGeminiSignatureCompatibilityFallback(),
                    tools, toolChoice);
                textOnlyFallback = true;
            }
            var result = ApplyMetrics(parsed, raw);
            if (textOnlyFallback && string.IsNullOrWhiteSpace(result.RetryClass))
                result.RetryClass = "ollama_native_tools_unsupported_text_only";
            if (UsesGeminiSignatureCompatibilityFallback(result))
                result.RetryClass = JoinRetryClass(result.RetryClass, "gemini_signature_compat");
            return result;
        }

        private static LlmTraceContext TraceForResult(LlmTraceContext trace, LlmResult result)
        {
            if (trace == null) return null;
            string outcome = result == null ? "client_null"
                : result.Canceled ? "canceled"
                : result.Ok ? "completed"
                : "failed";
            return trace.WithObservedResult(null, outcome);
        }

        private static LlmTraceContext TraceForResult(LlmTraceContext trace, LlmToolResult result)
        {
            if (trace == null) return null;
            string outcome = result == null ? "client_null"
                : result.Canceled ? "canceled"
                : !result.Ok ? "failed"
                : result.HasToolCalls ? "tool_calls"
                : string.IsNullOrWhiteSpace(result.Content) ? "empty_success"
                : "completed";
            return trace.WithObservedResult(ToolNames(result), outcome);
        }

        private static string ToolNames(LlmToolResult result)
        {
            if (result?.ToolCalls == null || result.ToolCalls.Count == 0) return null;
            var names = new List<string>();
            foreach (LlmToolCall call in result.ToolCalls)
            {
                string name = call?.Name;
                if (string.IsNullOrWhiteSpace(name) || names.Contains(name)) continue;
                names.Add(name);
                if (names.Count >= 8) break;
            }
            return names.Count == 0 ? null : string.Join(",", names.ToArray());
        }

        // ===== 工具调用一轮(流式):发 stream=true,边收边回调正文/思考/工具名;收尾返回与非流式同形的结果 =====
        // onContent/onThinking/onToolName 可空;任一回调抛错都被吞掉,不影响累加。失败/取消回退到 LlmToolResult.Ok=false。
        // 关键修复:这台游戏的 Unity 2022.3 Mono 的 System.Net.Http 没有 SocketsHttpHandler,HttpClient 走 MonoWebRequestHandler,
        // 会把整段响应缓冲完才返回(忽略 ResponseHeadersRead)→ 流式失效 + 慢推理模型直接 120s 超时。故流式改走 raw TcpClient+SslStream,
        // 自己写 HTTP/1.1 + 增量读 chunked/SSE,彻底绕开 Mono 的缓冲。非流式路径仍用 HttpClient(缓冲对它无碍)。
        private bool _streamOptionsSupported = true;   // 探测:provider 拒收 stream_options 后置 false
        private const int IdleStreamSec = 60;          // 改"总超时"为"分块间空闲超时":任意 chunk 到达即算活着(推理模型先吐思考、首字可能慢)

        public Task<LlmToolResult> SendToolRoundStreamAsync(IList<LlmMessage> messages, IList<ToolDef> tools,
            Action<string> onContent, Action<string> onThinking, Action<string> onToolName,
            string toolChoice = "auto", int maxTokens = 0, double temperature = 0.8,
            CancellationToken ct = default, int timeoutSec = 120, string tag = null,
            LlmReasoningPolicy reasoningPolicy = LlmReasoningPolicy.Auto)
            => SendToolRoundStreamAsyncWithTrace(messages, tools, onContent, onThinking, onToolName,
                toolChoice, maxTokens, temperature, ct, timeoutSec, tag, reasoningPolicy, null);

        public async Task<LlmToolResult> SendToolRoundStreamAsyncWithTrace(IList<LlmMessage> messages, IList<ToolDef> tools,
            Action<string> onContent, Action<string> onThinking, Action<string> onToolName,
            string toolChoice = "auto", int maxTokens = 0, double temperature = 0.8,
            CancellationToken ct = default, int timeoutSec = 120, string tag = null,
            LlmReasoningPolicy reasoningPolicy = LlmReasoningPolicy.Auto, LlmTraceContext trace = null)
        {
            EmitProviderWarnings();
            EmitReasoningPolicyWarning(reasoningPolicy);
            bool __textOnly = Volatile.Read(ref _nativeToolsRejected) != 0 && tools != null && tools.Count > 0;
            IList<LlmMessage> __estimatedMessages = __textOnly
                ? WithNativeToolsUnavailableInstruction(messages)
                : messages;
            int __estimatedPrompt = PromptBudgeter.Estimate(__estimatedMessages, __textOnly ? null : tools);
            var __sw = System.Diagnostics.Stopwatch.StartNew();
            long __workflowQueueMs = 0;
            LlmToolResult __res;
            try
            {
                var __execution = await RunWithWorkflowGateAsync(tag, ct, workload =>
                    SendToolRoundStreamCoreAsync(messages, tools, onContent, onThinking, onToolName,
                        toolChoice, maxTokens, temperature, ct, timeoutSec, reasoningPolicy,
                        workload)).ConfigureAwait(false);
                __res = __execution.Result;
                __workflowQueueMs = __execution.QueueMs;
            }
            catch (OperationCanceledException)
            { __res = new LlmToolResult { Ok = false, Canceled = true, Error = "已取消" }; }
            __res = Sanitize(__res); __res.QueueMs += __workflowQueueMs;
            PromptTokenCalibrator.Observe(TokenBudgetIdentity, __estimatedPrompt, __res.PromptTokens);
            LlmLog.RecordWithTrace(SecretRedactor.Redact(tag, _apiKey), SecretRedactor.Redact(_model, _apiKey), "流式", __sw.ElapsedMilliseconds, __res.PromptTokens, __res.CompletionTokens, __res.Ok, __res.Error,
                __res.CachedTokens, __res.CacheWriteTokens, __res.QueueMs, __res.TtftMs, __res.RetryCount, __res.RetryClass,
                __res.ReasoningTokens, __res.TotalTokens, __res.CacheMissTokens, TraceForResult(trace, __res));
            return __res;
        }

        private async Task<LlmToolResult> SendToolRoundStreamCoreAsync(IList<LlmMessage> messages, IList<ToolDef> tools,
            Action<string> onContent, Action<string> onThinking, Action<string> onToolName,
            string toolChoice = "auto", int maxTokens = 0, double temperature = 0.8,
            CancellationToken ct = default, int timeoutSec = 120,
            LlmReasoningPolicy reasoningPolicy = LlmReasoningPolicy.Auto,
            LlmWorkloadKind workload = LlmWorkloadKind.Foreground)
        {
            if (_configurationError != null) return new LlmToolResult { Ok = false, Error = _configurationError };
            if (!ValidateToolRequest(tools, toolChoice, out var normalizedChoice, out var toolError))
                return new LlmToolResult { Ok = false, Error = toolError };
            toolChoice = normalizedChoice;
            // 并发闸：流式整段在飞期间占一格。健康排队不消耗建连/空闲 timeout，
            // 但外层取消与群聊绝对硬时限仍会通过 ct 终止排队。
            var requestGate = RequestGate();
            var gateSw = System.Diagnostics.Stopwatch.StartNew();
            LlmPriorityConcurrencyGate.Lease requestLease;
            try { requestLease = await requestGate.AcquireAsync(workload, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return new LlmToolResult { Ok = false, Canceled = true, Error = "已取消", QueueMs = gateSw.ElapsedMilliseconds }; }
            long queueMs = gateSw.ElapsedMilliseconds;
            try
            {
            bool textOnlyFallback = tools != null && tools.Count > 0
                && Volatile.Read(ref _nativeToolsRejected) != 0;
            var requestMessages = textOnlyFallback ? WithNativeToolsUnavailableInstruction(messages) : messages;
            var collector = NewStreamingCollector(onContent, onThinking, onToolName, tools, toolChoice);
            bool useUsage = _streamOptionsSupported;
            int retryCount = 0;
            string retryClass = textOnlyFallback
                ? "ollama_native_tools_unsupported_text_only"
                : null;
            var wireSw = System.Diagnostics.Stopwatch.StartNew();
            var body = BuildStreamBody(requestMessages, maxTokens, temperature,
                textOnlyFallback ? null : tools, toolChoice, useUsage, false, reasoningPolicy);
            var r = await RawSseStreamAsync(body, collector, ct, timeoutSec).ConfigureAwait(false);

            // provider 拒收 stream_options(MiniMax 会因任何未知字段整体 400 且不指明)→ 去掉它重试一次
            if (!r.Ok && !r.Canceled && useUsage && LooksLikeParamRejected(r.Error))
            {
                _streamOptionsSupported = false;
                retryCount++; retryClass = JoinRetryClass(retryClass, "stream_options_unsupported");
                collector = NewStreamingCollector(onContent, onThinking, onToolName, tools, toolChoice);
                body = BuildStreamBody(requestMessages, maxTokens, temperature,
                    textOnlyFallback ? null : tools, toolChoice, false, false, reasoningPolicy);
                long retryOffset = wireSw.ElapsedMilliseconds;
                r = await RawSseStreamAsync(body, collector, ct, timeoutSec).ConfigureAwait(false);
                if (r.TtftMs > 0) r.TtftMs += retryOffset;
            }
            if (!r.Ok && !r.Canceled && ShouldRetryWithoutReasoningEffort(r.Error))
            {
                retryCount++; retryClass = JoinRetryClass(retryClass, "reasoning_effort_unsupported");
                collector = NewStreamingCollector(onContent, onThinking, onToolName, tools, toolChoice);
                body = BuildStreamBody(requestMessages, maxTokens, temperature,
                    textOnlyFallback ? null : tools, toolChoice, _streamOptionsSupported, false, reasoningPolicy);
                long retryOffset = wireSw.ElapsedMilliseconds;
                r = await RawSseStreamAsync(body, collector, ct, timeoutSec).ConfigureAwait(false);
                if (r.TtftMs > 0) r.TtftMs += retryOffset;
            }
            if (!r.Ok && !r.Canceled && !textOnlyFallback
                && TryDisableUnsupportedNativeTools(r.Error))
            {
                retryCount++;
                retryClass = JoinRetryClass(retryClass,
                    "ollama_native_tools_unsupported_text_only");
                textOnlyFallback = true;
                requestMessages = WithNativeToolsUnavailableInstruction(messages);
                collector = NewStreamingCollector(onContent, onThinking, onToolName, tools, toolChoice);
                body = BuildStreamBody(requestMessages, maxTokens, temperature, null,
                    toolChoice, _streamOptionsSupported, false, reasoningPolicy);
                long retryOffset = wireSw.ElapsedMilliseconds;
                r = await RawSseStreamAsync(body, collector, ct, timeoutSec).ConfigureAwait(false);
                if (r.TtftMs > 0) r.TtftMs += retryOffset;
            }
            if (r.Canceled) return new LlmToolResult { Ok = false, Canceled = true, Error = "已取消", QueueMs = queueMs,
                TtftMs = r.TtftMs, RetryCount = retryCount, RetryClass = retryClass };
            if (!r.Ok) return new LlmToolResult
            {
                Ok = false, Error = r.Error, QueueMs = queueMs,
                TtftMs = r.TtftMs, RetryCount = retryCount, RetryClass = retryClass,
                // A failed stream may already have been billed before its transport/protocol
                // failure.  Preserve any usage frame received so logs do not misleadingly say 0/0.
                PromptTokens = collector.PromptTokens,
                CompletionTokens = collector.CompletionTokens,
                CachedTokens = collector.CachedTokens,
                CacheMissTokens = collector.CacheMissTokens,
                CacheWriteTokens = collector.CacheWriteTokens,
                ReasoningTokens = collector.ReasoningTokens,
                TotalTokens = collector.TotalTokens,
            };
            var result = collector.ToResult();
            result.QueueMs = queueMs; result.TtftMs = r.TtftMs;
            result.RetryCount = retryCount; result.RetryClass = retryClass;
            if (UsesGeminiSignatureCompatibilityFallback(result))
                result.RetryClass = JoinRetryClass(result.RetryClass, "gemini_signature_compat");
            return result;
            }
            finally { requestLease.Dispose(); }
        }

        private JObject BuildStreamBody(IList<LlmMessage> messages, int maxTokens, double temperature, IList<ToolDef> tools,
            string toolChoice, bool includeUsage, bool noThinking, LlmReasoningPolicy reasoningPolicy)
        {
            var body = BuildBodyWithPolicy(messages, maxTokens, temperature, false, ToolsToJson(tools), toolChoice, noThinking, reasoningPolicy);
            body["stream"] = true;
            if (includeUsage) body["stream_options"] = new JObject { ["include_usage"] = true };
            if (_capabilities.RequiresToolStream && body["tools"] is JArray streamTools && streamTools.Count > 0)
                body["tool_stream"] = true;
            return body;
        }

        private StreamingToolCollector NewStreamingCollector(Action<string> onContent, Action<string> onThinking, Action<string> onToolName,
            IList<ToolDef> tools, string toolChoice)
            => new StreamingToolCollector
            {
                OnContent = onContent,
                OnThinking = onThinking,
                OnToolName = onToolName,
                RequireGeminiThoughtSignature = RequiresGeminiToolSignature(),
                AllowGeminiSignatureCompatibilityFallback = AllowsGeminiSignatureCompatibilityFallback(),
                AllowStopFinishWithCompleteToolCalls = _capabilities.AllowsStopFinishWithCompleteToolCalls,
                RequireFinishReason = true,
                RequireDoneSentinel = true,
                AllowedTools = tools,
                EnforceAllowedTools = true,
                ExpectedToolChoice = toolChoice,
                CumulativeContent = _capabilities.CumulativeStreamingContent,
                CumulativeReasoning = _capabilities.CumulativeStreamingReasoning,
                TreatStringNullFinishAsMissing = _capabilities.IsQwen,
                AllowEmptyChoicesHeartbeat = _capabilities.AllowsEmptyChoicesHeartbeat,
                CallbackExactSecret = _apiKey,
            };

        private bool RequiresGeminiToolSignature()
        {
            string model = (_model ?? "").ToLowerInvariant();
            return model.Contains("gemini-3") || model.Contains("gemini_3") || model.Contains("gemini 3");
        }

        private bool AllowsGeminiSignatureCompatibilityFallback()
            => RequiresGeminiToolSignature() && !_capabilities.IsOfficialGoogle;

        private static bool UsesGeminiSignatureCompatibilityFallback(LlmToolResult result)
        {
            var sig = result?.ToolCalls != null && result.ToolCalls.Count > 0
                ? result.ToolCalls[0]?.ExtraFields?["extra_content"]?["google"]?["thought_signature"]
                : null;
            return string.Equals(sig?.ToString(), GeminiSignatureCompatibilitySentinel, StringComparison.Ordinal);
        }

        private struct SseResult { public bool Ok; public bool Canceled; public string Error; public long TtftMs; }

        // 原始套接字流式:连接 → (TLS) → 写 HTTP/1.1 POST → 增量读响应体喂 collector。空闲超时,非阻塞主线程。
        private async Task<SseResult> RawSseStreamAsync(JObject body, StreamingToolCollector collector,
            CancellationToken ct, int timeoutSec)
        {
            if (_configurationError != null) return new SseResult { Error = _configurationError };
            bool nativeQwen38 = _capabilities.IsLocalOllamaQwen38;
            JObject wireBody = body;
            Uri uri = _endpointUri;
            if (nativeQwen38)
            {
                if (!OllamaNativeQwen38Adapter.TryBuildRequest(body, out wireBody,
                    out string adapterError))
                    return new SseResult { Error = adapterError };
                uri = OllamaNativeQwen38Adapter.NativeChatEndpoint(_endpointUri);
            }
            bool https = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase);
            int port = uri.IsDefaultPort ? (https ? 443 : 80) : uri.Port;
            string connectHost = uri.DnsSafeHost;
            string path = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
            byte[] bodyBytes = Encoding.UTF8.GetBytes(wireBody.ToString(Formatting.None));
            if (bodyBytes.Length > LlmProtocolLimits.MaxRequestBytes)
                return new SseResult { Error = "LLM 请求体超过 " + LlmProtocolLimits.MaxRequestBytes + " 字节上限" };

            var head = new StringBuilder();
            head.Append("POST ").Append(path).Append(" HTTP/1.1\r\n");
            head.Append("Host: ").Append(uri.Authority);
            head.Append("\r\n");
            if (!string.IsNullOrEmpty(_apiKey)) head.Append("Authorization: Bearer ").Append(_apiKey).Append("\r\n");
            head.Append("Content-Type: application/json\r\n");
            head.Append(nativeQwen38
                ? "Accept: application/x-ndjson, application/json\r\n"
                : "Accept: text/event-stream\r\n");               // Ollama 原生流为 NDJSON，其余接口为 SSE
            head.Append("Accept-Encoding: identity\r\n");          // 显式拒绝 gzip:SSE 体若被压缩会喂给 UTF-8 解码器变乱码
            head.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
            head.Append("Connection: close\r\n\r\n");               // 不复用连接:服务端收尾即关,免 keep-alive 复用坑
            byte[] headBytes = Encoding.UTF8.GetBytes(head.ToString());

            System.Net.Sockets.TcpClient tcp = null;
            System.Net.Security.SslStream ssl = null;
            System.IO.Stream stream = null;
            var wireSw = System.Diagnostics.Stopwatch.StartNew();
            long ttftMs = 0;
            try
            {
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
                tcp = new System.Net.Sockets.TcpClient();
                using (ct.Register(() => { try { tcp.Close(); } catch { } }))   // 取消 → 关 socket,任何阻塞读/连即刻醒来
                {
                    int setupTimeoutSec = timeoutSec > 0 ? Math.Max(15, timeoutSec) : 120;
                    using (var setup = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        setup.CancelAfter(TimeSpan.FromSeconds(setupTimeoutSec));
                        using (setup.Token.Register(() => { try { tcp.Close(); } catch { } }))
                        try
                        {
                            await tcp.ConnectAsync(connectHost, port).ConfigureAwait(false);
                            if (IsLoopbackHost(connectHost))
                            {
                                var remote = tcp.Client.RemoteEndPoint as IPEndPoint;
                                if (remote == null || !IPAddress.IsLoopback(remote.Address))
                                    return new SseResult { Error = "回环 endpoint 解析到了非本机地址", TtftMs = ttftMs };
                            }
                            System.IO.Stream net = tcp.GetStream();
                            if (https)
                            {
                                // 正常做证书链 + 主机名校验；仅本地回环容忍自签名证书。
                                ssl = new System.Net.Security.SslStream(net, false,
                                    (sender, cert, chain, errors) => errors == System.Net.Security.SslPolicyErrors.None || IsLoopbackHost(connectHost));
                                await ssl.AuthenticateAsClientAsync(connectHost).ConfigureAwait(false);
                                stream = ssl;
                            }
                            else stream = net;

                            await stream.WriteAsync(headBytes, 0, headBytes.Length, setup.Token).ConfigureAwait(false);
                            await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length, setup.Token).ConfigureAwait(false);
                            await stream.FlushAsync(setup.Token).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            if (ct.IsCancellationRequested)
                                return new SseResult { Canceled = true, Error = "已取消", TtftMs = ttftMs };
                            if (setup.IsCancellationRequested)
                                return new SseResult { Error = "流式连接或握手超时(" + setupTimeoutSec + "s)", TtftMs = ttftMs };
                            throw;
                        }
                    }

                    var buf = new byte[16384];
                    var headerAcc = new System.Collections.Generic.List<byte>(1024);
                    bool headersDone = false;
                    HttpResponseHead responseHead = default;
                    SseBodyReader sse = null;
                    SseEventReader events = null;
                    OllamaNativeQwen38StreamAdapter nativeStream = null;
                    string nativeProtocolError = null;

                    while (true)
                    {
                        int n;
                        using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            int idleSec = timeoutSec > 0 ? Math.Max(IdleStreamSec, timeoutSec) : IdleStreamSec;
                            idle.CancelAfter(TimeSpan.FromSeconds(idleSec));
                            try { n = await stream.ReadAsync(buf, 0, buf.Length, idle.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException)
                            {
                                if (ct.IsCancellationRequested) return new SseResult { Canceled = true, Error = "已取消", TtftMs = ttftMs };
                                return new SseResult { Error = "流式空闲超时(" + idleSec + "s无数据)", TtftMs = ttftMs };   // 触发上层非流式回退
                            }
                        }
                        if (n <= 0) break;   // 连接关闭(Connection: close 正常收尾)

                        if (!headersDone)
                        {
                            int bodyStart = -1;
                            for (int i = 0; i < n; i++)
                            {
                                headerAcc.Add(buf[i]);
                                if (headerAcc.Count > LlmProtocolLimits.MaxHttpHeaderBytes)
                                    return new SseResult { Error = "HTTP 响应头超过上限", TtftMs = ttftMs };
                                int c = headerAcc.Count;
                                if (c >= 4 && headerAcc[c - 4] == 13 && headerAcc[c - 3] == 10 && headerAcc[c - 2] == 13 && headerAcc[c - 1] == 10)
                                {
                                    headersDone = true; bodyStart = i + 1;
                                    if (!TryParseHeaders(headerAcc, out responseHead, out var headerError))
                                        return new SseResult { Error = headerError, TtftMs = ttftMs };
                                    break;
                                }
                            }
                            if (!headersDone) continue;   // headers 跨多次读

                            if (responseHead.Status < 200 || responseHead.Status >= 300)
                            {
                                string errText = await ReadErrorBodyAsync(stream, buf, bodyStart, n - bodyStart,
                                    responseHead, ct).ConfigureAwait(false);
                                return new SseResult { Error = ExtractError(errText, responseHead.Status), TtftMs = ttftMs };
                            }
                            if (nativeQwen38)
                            {
                                if (!IsOllamaNativeStreamContentType(responseHead.ContentType))
                                    return new SseResult { Error = "本地 Qwen3.8 流式接口返回了非 NDJSON Content-Type", TtftMs = ttftMs };
                                nativeStream = new OllamaNativeQwen38StreamAdapter();
                                sse = new SseBodyReader(responseHead.Chunked, line =>
                                {
                                    if (nativeProtocolError != null) return;
                                    if (!nativeStream.TryConvertLine(line, out string payload,
                                        out bool doneSentinel, out string conversionError))
                                    {
                                        nativeProtocolError = conversionError;
                                        return;
                                    }
                                    if (!string.IsNullOrEmpty(payload))
                                    {
                                        if (ttftMs == 0) ttftMs = wireSw.ElapsedMilliseconds;
                                        collector.Feed(payload);
                                    }
                                    if (doneSentinel) collector.Feed("[DONE]");
                                });
                            }
                            else
                            {
                                if (!IsEventStreamContentType(responseHead.ContentType))
                                    return new SseResult { Error = "流式接口返回了非 SSE Content-Type", TtftMs = ttftMs };
                                events = new SseEventReader(payload =>
                                {
                                    if (ttftMs == 0 && payload != "[DONE]") ttftMs = wireSw.ElapsedMilliseconds;
                                    collector.Feed(payload);
                                });
                                sse = new SseBodyReader(responseHead.Chunked, events.FeedLine);
                            }
                            if (bodyStart < n)
                            {
                                sse.Feed(buf, bodyStart, n - bodyStart);
                            }
                        }
                        else
                        {
                            sse.Feed(buf, 0, n);
                        }
                        if (sse != null && sse.ProtocolError != null)
                            return new SseResult { Error = sse.ProtocolError, TtftMs = ttftMs };
                        if (events != null && events.ProtocolError != null)
                            return new SseResult { Error = events.ProtocolError, TtftMs = ttftMs };
                        if (nativeProtocolError != null)
                            return new SseResult { Error = nativeProtocolError, TtftMs = ttftMs };
                        // Collector-level protocol errors can be raised by a complete SSE event
                        // before the HTTP response itself ends.  Abort the socket immediately
                        // instead of paying/waiting for the provider to finish the whole response.
                        if (!string.IsNullOrEmpty(collector.ProtocolError))
                            return new SseResult { Error = collector.ProtocolError, TtftMs = ttftMs };
                        if (sse != null && (sse.Done || (!responseHead.Chunked && responseHead.ContentLength >= 0
                            && sse.DecodedBytes == responseHead.ContentLength))) break;
                    }
                    sse?.Flush();
                    events?.Complete();
                    if (sse != null && !string.IsNullOrEmpty(sse.ProtocolError))
                        return new SseResult { Error = sse.ProtocolError, TtftMs = ttftMs };
                    if (events != null && !string.IsNullOrEmpty(events.ProtocolError))
                        return new SseResult { Error = events.ProtocolError, TtftMs = ttftMs };
                    if (!string.IsNullOrEmpty(nativeProtocolError))
                        return new SseResult { Error = nativeProtocolError, TtftMs = ttftMs };
                    if (sse != null && responseHead.ContentLength >= 0 && sse.DecodedBytes != responseHead.ContentLength)
                        return new SseResult { Error = "HTTP Content-Length 与实际响应体不一致", TtftMs = ttftMs };
                    if (!string.IsNullOrEmpty(collector.ProtocolError))
                        return new SseResult { Error = collector.ProtocolError, TtftMs = ttftMs };
                    if (!collector.IsProtocolComplete)
                        return new SseResult { Error = "流式响应在 finish_reason/[DONE] 前中断", TtftMs = ttftMs };
                    return new SseResult { Ok = true, TtftMs = ttftMs };
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new SseResult { Canceled = true, Error = "已取消", TtftMs = ttftMs }; }
            catch (Exception ex) { return new SseResult { Error = "流式传输异常:" + ExceptionTypeChain(ex), TtftMs = ttftMs }; }
            finally
            {
                try { ssl?.Dispose(); } catch { }
                try { stream?.Dispose(); } catch { }
                try { tcp?.Close(); } catch { }
            }
        }

        private struct HttpResponseHead
        {
            public int Status;
            public bool Chunked;
            public long ContentLength;
            public string ContentType;
        }

        private static bool TryParseHeaders(System.Collections.Generic.List<byte> acc, out HttpResponseHead head, out string error)
        {
            head = new HttpResponseHead { ContentLength = -1 }; error = null;
            if (acc == null || acc.Count == 0 || acc.Count > LlmProtocolLimits.MaxHttpHeaderBytes)
            { error = "HTTP 响应头为空或超过上限"; return false; }
            foreach (byte b in acc) if (b > 127) { error = "HTTP 响应头含非 ASCII 字节"; return false; }
            string text = Encoding.ASCII.GetString(acc.ToArray());
            if (!text.EndsWith("\r\n\r\n", StringComparison.Ordinal))
            { error = "HTTP 响应头未以 CRLF CRLF 结束"; return false; }
            string[] lines = text.Substring(0, text.Length - 4).Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0 || (!lines[0].StartsWith("HTTP/1.1 ", StringComparison.Ordinal)
                && !lines[0].StartsWith("HTTP/1.0 ", StringComparison.Ordinal)))
            { error = "HTTP 状态行无效"; return false; }
            string[] statusParts = lines[0].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (statusParts.Length < 2 || statusParts[1].Length != 3
                || !AllAsciiDigits(statusParts[1]) || !int.TryParse(statusParts[1], out head.Status))
            { error = "HTTP 状态码无效"; return false; }

            string transferEncoding = null;
            bool sawContentLength = false;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0 || char.IsWhiteSpace(line[0])) { error = "HTTP 响应头含非法折行"; return false; }
                int colon = line.IndexOf(':');
                if (colon <= 0) { error = "HTTP 响应头字段缺冒号"; return false; }
                string key = line.Substring(0, colon);
                for (int k = 0; k < key.Length; k++)
                    if (!(char.IsLetterOrDigit(key[k]) || "!#$%&'*+-.^_`|~".IndexOf(key[k]) >= 0))
                    { error = "HTTP 响应头字段名无效"; return false; }
                string value = line.Substring(colon + 1).Trim();
                if (ContainsInvalidHeaderValueChar(value)) { error = "HTTP 响应头字段值含非法控制字符"; return false; }
                if (key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                {
                    if (transferEncoding != null) { error = "HTTP Transfer-Encoding 重复"; return false; }
                    transferEncoding = value;
                }
                else if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (!AllAsciiDigits(value) || !long.TryParse(value, out long length)
                        || length < 0 || length > LlmProtocolLimits.MaxResponseBytes)
                    { error = "HTTP Content-Length 无效或超过上限"; return false; }
                    if (sawContentLength && head.ContentLength != length) { error = "HTTP Content-Length 冲突"; return false; }
                    sawContentLength = true; head.ContentLength = length;
                }
                else if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    if (head.ContentType != null) { error = "HTTP Content-Type 重复"; return false; }
                    head.ContentType = value;
                }
            }
            if (transferEncoding != null)
            {
                if (!string.Equals(transferEncoding.Trim(), "chunked", StringComparison.OrdinalIgnoreCase))
                { error = "HTTP Transfer-Encoding 只支持单一 chunked"; return false; }
                if (sawContentLength) { error = "HTTP 响应同时含 Transfer-Encoding 与 Content-Length"; return false; }
                head.Chunked = true;
            }
            return true;
        }

        private static bool AllAsciiDigits(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (char c in value) if (c < '0' || c > '9') return false;
            return true;
        }

        private static bool IsEventStreamContentType(string value)
        {
            string mediaType = (value ?? "").Split(';')[0].Trim();
            return string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsOllamaNativeStreamContentType(string value)
        {
            string mediaType = (value ?? "").Split(';')[0].Trim();
            return string.Equals(mediaType, "application/x-ndjson", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsInvalidHeaderValueChar(string value)
        {
            if (value == null) return false;
            foreach (char c in value) if ((c < 0x20 && c != '\t') || c == 0x7f) return true;
            return false;
        }

        // 严格解码 non-2xx 错误体；保留状态码，限制为 16KiB，防网关错误页挤爆内存。
        private static async Task<string> ReadErrorBodyAsync(System.IO.Stream stream, byte[] initial, int offset, int count,
            HttpResponseHead head, CancellationToken ct)
        {
            var text = new StringBuilder();
            var reader = new SseBodyReader(head.Chunked, line =>
            {
                if (text.Length > 0) text.Append('\n');
                text.Append(line);
            }, LlmProtocolLimits.MaxHttpErrorBytes, LlmProtocolLimits.MaxHttpErrorBytes);
            if (count > 0) reader.Feed(initial, offset, count);
            var buf = new byte[8192];
            try
            {
                while (reader.ProtocolError == null && !reader.Done
                    && (head.Chunked || head.ContentLength < 0 || reader.DecodedBytes < head.ContentLength))
                {
                    int n;
                    using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        idle.CancelAfter(TimeSpan.FromSeconds(10));
                        n = await stream.ReadAsync(buf, 0, buf.Length, idle.Token).ConfigureAwait(false);
                    }
                    if (n <= 0) break;
                    reader.Feed(buf, 0, n);
                }
                reader.Flush();
            }
            catch (OperationCanceledException) { throw; }
            catch { return ""; }
            if (reader.ProtocolError != null) return "";
            if (head.ContentLength >= 0 && reader.DecodedBytes != head.ContentLength) return "";
            return text.ToString();
        }

        // 本地回环主机:localhost / 127.x / ::1。仅这些地址在自签名证书时放行(流量不出本机,无远程 MITM 风险)。
        private static bool IsLoopbackHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            host = host.Trim().ToLowerInvariant().Trim('[', ']');
            if (host == "localhost") return true;
            return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
        }

        // provider 因未知参数(stream_options 等)整体拒绝的 400 特征(MiniMax 不指明字段)
        private static bool LooksLikeParamRejected(string err)
        {
            if (string.IsNullOrEmpty(err)) return false;
            string e = err.ToLowerInvariant();
            // 只能在服务端明确点名该字段时降级。泛化的 invalid request 也可能是 schema、
            // tool_choice 或模型名错误，贸然重发会掩盖真因并额外计费。
            return e.Contains("stream_options") || e.Contains("include_usage");
        }

        private bool TryDisableUnsupportedNativeTools(string error)
        {
            if (!_capabilities.IsLocalOllama || string.IsNullOrWhiteSpace(error)) return false;
            string normalized = error.ToLowerInvariant();
            bool explicitlyUnsupported = normalized.Contains("unable to generate parser for this template")
                || normalized.Contains("does not support tool calls")
                || normalized.Contains("does not support tools")
                || normalized.Contains("tool calling is not supported")
                || normalized.Contains("function calling is not supported");
            if (!explicitlyUnsupported) return false;
            Interlocked.Exchange(ref _nativeToolsRejected, 1);
            return true;
        }

        private static IList<LlmMessage> WithNativeToolsUnavailableInstruction(
            IList<LlmMessage> messages)
        {
            var copy = new List<LlmMessage>((messages?.Count ?? 0) + 1);
            if (messages != null)
                foreach (var message in messages)
                    if (message != null) copy.Add(message);
            // BuildBodyWithPolicy groups system messages before conversation messages while
            // retaining their relative order, so this remains the final authoritative system
            // constraint without rewriting caller-owned history.
            copy.Add(LlmMessage.System(NativeToolsUnavailableInstruction));
            return copy;
        }

        // 严格端明确点名 reasoning_effort 不支持的 400(OpenAI: "Unsupported parameter: 'reasoning_effort'…";
        // 兼容端: "unknown/unexpected parameter reasoning_effort" 等)。只在服务端点名该字段且带拒绝措辞时才认,
        // 避免把取值错误或无关 400 误判为字段不支持(我们只发 "low",合法推理模型不会因取值报错)。
        private static bool LooksLikeReasoningEffortRejected(string err)
        {
            if (string.IsNullOrEmpty(err)) return false;
            string e = err.ToLowerInvariant();
            if (!e.Contains("reasoning_effort")) return false;
            return e.Contains("unsupported") || e.Contains("not support") || e.Contains("does not support")
                || e.Contains("unknown") || e.Contains("unrecognized") || e.Contains("unexpected")
                || e.Contains("not allowed") || e.Contains("not permitted") || e.Contains("invalid")
                || e.Contains("不支持") || e.Contains("无法识别") || e.Contains("未知") || e.Contains("非法");
        }

        // 仅 OpenAI 推理分支会以“可剥离”方式下发 reasoning_effort；Gemini/DeepSeek/
        // Kimi K3/GLM 5.2 的该字段由各自官方协议决定，不在探测范围，避免无效等体重发。
        private bool ReasoningEffortStrippable() => _capabilities.IsOpenAiReasoningChat;

        // 首次命中"reasoning_effort 不支持" → 记忆 _reasoningEffortRejected(按本客户端=provider/model 维度),
        // 返回 true 让调用方剥离该字段重发一次;此后 AddThinkingControl 不再下发,不会每轮重复 400。
        private bool ShouldRetryWithoutReasoningEffort(string error)
        {
            if (_reasoningEffortRejected || !ReasoningEffortStrippable()) return false;
            if (!LooksLikeReasoningEffortRejected(error)) return false;
            _reasoningEffortRejected = true;
            return true;
        }

        // 从错误响应体里抽人话(base_resp / error.message),取不到给 HTTP 状态码
        private static string ExtractError(string text, int status)
        {
            if (LlmJsonProtocol.TryParseObject(text, LlmProtocolLimits.MaxHttpErrorBytes, out var o, out _))
                return JsonErrorWithStatus(o, status);
            string snippet = string.IsNullOrEmpty(text) ? "" : (text.Length > 200 ? text.Substring(0, 200) : text);
            return "HTTP " + status + (snippet.Length == 0 ? "" : (" | " + SecretRedactor.Redact(snippet)));
        }

        // 底层 HTTP:发 body,回 (text,status) 或 error/canceled。分级超时 + 外部中断接线。
        private async Task<RawResp> SendRawAsync(JObject body, CancellationToken ct, int timeoutSec,
            LlmWorkloadKind workload)
        {
            if (_configurationError != null) return new RawResp { error = _configurationError };
            Uri requestUri = _endpointUri;
            JObject wireBody = body;
            bool nativeQwen38 = _capabilities.IsLocalOllamaQwen38;
            if (nativeQwen38)
            {
                if (!OllamaNativeQwen38Adapter.TryBuildRequest(body, out wireBody,
                    out string adapterError))
                    return new RawResp { error = adapterError };
                requestUri = OllamaNativeQwen38Adapter.NativeChatEndpoint(_endpointUri);
            }
            string bodyJson = wireBody?.ToString(Formatting.None) ?? "{}";
            if (Encoding.UTF8.GetByteCount(bodyJson) > LlmProtocolLimits.MaxRequestBytes)
                return new RawResp { error = "LLM 请求体超过 " + LlmProtocolLimits.MaxRequestBytes + " 字节上限" };
            // 全局 provider 闸只负责限并发；健康排队不消耗模型执行 timeout。
            // 外层工作流取消（换存档、关窗口、群聊硬时限）仍会通过 ct 立即终止排队。
            var requestGate = RequestGate();
            var gateSw = System.Diagnostics.Stopwatch.StartNew();
            LlmPriorityConcurrencyGate.Lease requestLease;
            try { requestLease = await requestGate.AcquireAsync(workload, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return new RawResp { error = "已取消", canceled = true, queueMs = gateSw.ElapsedMilliseconds }; }
            long queueMs = gateSw.ElapsedMilliseconds;
            try
            {
            string lastErr = null;
            int retryCount = 0;
            string retryClass = null;
            var wireSw = System.Diagnostics.Stopwatch.StartNew();
            // 本机服务可能正在启动/装载模型；只对 Ollama 多给一次较长退避。
            // 云端仍保持原来的 2 次尝试与 450ms 退避，避免改变费用和限流行为。
            int maxAttempts = _capabilities.IsLocalOllama ? 3 : 2;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec <= 0 ? 120 : timeoutSec)))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
                using (var req = new HttpRequestMessage(HttpMethod.Post, requestUri))
                {
                    if (!string.IsNullOrEmpty(_apiKey))   // 本地模型(vLLM/Ollama/llama.cpp)常无需密钥 → 留空则不发 Authorization 头
                        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
                    req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
                    try
                    {
                        using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false))
                        {
                            long ttftMs = wireSw.ElapsedMilliseconds;
                            // JHYL_RESPONSE_BODY_LINKED_TOKEN:ResponseHeadersRead 只覆盖到响应头；
                            // 正文必须继续使用 linked.Token，否则“已收头但 body 卡住”会逃逸总 deadline。
                            var text = await ReadResponseTextWithCancellationAsync(resp.Content, linked.Token, LlmProtocolLimits.MaxResponseBytes).ConfigureAwait(false);
                            if (nativeQwen38 && resp.IsSuccessStatusCode)
                            {
                                if (!OllamaNativeQwen38Adapter.TryNormalizeResponse(text,
                                    out string normalized, out string normalizeError))
                                    return new RawResp { error = normalizeError,
                                        status = (int)resp.StatusCode, queueMs = queueMs,
                                        ttftMs = ttftMs, retryCount = retryCount,
                                        retryClass = retryClass };
                                text = normalized;
                            }
                            return new RawResp { text = text, status = (int)resp.StatusCode, queueMs = queueMs, ttftMs = ttftMs,
                                retryCount = retryCount, retryClass = retryClass };
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new RawResp { error = "已取消", canceled = true,
                        queueMs = queueMs, ttftMs = wireSw.ElapsedMilliseconds, retryCount = retryCount, retryClass = retryClass }; }
                    catch (OperationCanceledException) { return new RawResp { error = "请求超时(" + (timeoutSec <= 0 ? 120 : timeoutSec) + "s)",
                        queueMs = queueMs, ttftMs = wireSw.ElapsedMilliseconds, retryCount = retryCount, retryClass = retryClass }; }
                    catch (Exception ex)
                    {
                        lastErr = "HTTP 传输异常:" + ExceptionTypeChain(ex);
                        if (attempt + 1 < maxAttempts && !ct.IsCancellationRequested && IsTransientConnError(ex))
                        {
                            retryCount++;
                            retryClass = "transient_connection";
                            int retryDelayMs = _capabilities.IsLocalOllama ? 750 * (attempt + 1) : 450;
                            try { await Task.Delay(retryDelayMs, ct).ConfigureAwait(false); }
                            catch { return new RawResp { error = "已取消", canceled = true, queueMs = queueMs,
                                ttftMs = wireSw.ElapsedMilliseconds, retryCount = retryCount, retryClass = retryClass }; }
                            continue;
                        }
                        return new RawResp { error = lastErr, queueMs = queueMs, ttftMs = wireSw.ElapsedMilliseconds,
                            retryCount = retryCount, retryClass = retryClass };
                    }
                }
            }
            return new RawResp { error = lastErr ?? "未知错误", queueMs = queueMs, ttftMs = wireSw.ElapsedMilliseconds,
                retryCount = retryCount, retryClass = retryClass };
            }
            finally { requestLease.Dispose(); }
        }

        private LlmPriorityConcurrencyGate RequestGate()
            => _capabilities.IsLocalOllama ? _localOllamaGate
                : _capabilities.IsOfficialDeepSeek && _capabilities.IsDeepSeekV4
                    ? _officialDeepSeekV4Gate : _gate;

        private static async Task<string> ReadResponseTextWithCancellationAsync(HttpContent content, CancellationToken ct, int maxBytes)
        {
            if (content == null) return "";
            if (content.Headers?.ContentLength != null && (content.Headers.ContentLength < 0 || content.Headers.ContentLength > maxBytes))
                throw new System.IO.InvalidDataException("LLM 响应体超过 " + maxBytes + " 字节上限");
            var streamTask = content.ReadAsStreamAsync();
            if (!streamTask.IsCompleted)
            {
                var canceled = Task.Delay(Timeout.Infinite, ct);
                if (await Task.WhenAny(streamTask, canceled).ConfigureAwait(false) != streamTask)
                    ct.ThrowIfCancellationRequested();
            }
            using (var stream = await streamTask.ConfigureAwait(false))
            using (var sink = new System.IO.MemoryStream())
            {
                var buffer = new byte[16384];
                while (true)
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                    if (read <= 0) break;
                    if (sink.Length + read > maxBytes)
                        throw new System.IO.InvalidDataException("LLM 响应体超过 " + maxBytes + " 字节上限");
                    sink.Write(buffer, 0, read);
                }
                try { return StrictUtf8.GetString(sink.ToArray()); }
                catch (DecoderFallbackException)
                { throw new System.IO.InvalidDataException("LLM 响应体含非法 UTF-8"); }
            }
        }

        private struct RawResp
        {
            public string text; public int status; public string error; public bool canceled;
            public long queueMs; public long ttftMs; public int retryCount; public string retryClass;
        }

        private static RawResp CombineRawMetrics(RawResp first, RawResp final, string compatibilityClass)
        {
            final.queueMs += first.queueMs;
            final.ttftMs += first.ttftMs; // 兼容性重发前的首次往返也在用户感知 TTFT 内
            final.retryCount += first.retryCount + 1;
            final.retryClass = JoinRetryClass(first.retryClass, JoinRetryClass(compatibilityClass, final.retryClass));
            return final;
        }

        private static string JoinRetryClass(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first)) return second;
            if (string.IsNullOrWhiteSpace(second) || string.Equals(first, second, StringComparison.Ordinal)) return first;
            return first + "+" + second;
        }

        private static LlmResult ApplyMetrics(LlmResult result, RawResp raw)
        {
            result = result ?? new LlmResult { Ok = false, Error = "空结果" };
            result.QueueMs = raw.queueMs; result.TtftMs = raw.ttftMs;
            result.RetryCount = raw.retryCount; result.RetryClass = raw.retryClass;
            return result;
        }

        private static LlmToolResult ApplyMetrics(LlmToolResult result, RawResp raw)
        {
            result = result ?? new LlmToolResult { Ok = false, Error = "空结果" };
            result.QueueMs = raw.queueMs; result.TtftMs = raw.ttftMs;
            result.RetryCount = raw.retryCount; result.RetryClass = raw.retryClass;
            return result;
        }

        private static JArray ToolsToJson(IList<ToolDef> tools)
        {
            if (tools == null || tools.Count == 0) return null;
            var arr = new JArray();
            foreach (var t in tools) arr.Add(t.ToJson());
            return arr;
        }

        private static bool ValidateToolRequest(IList<ToolDef> tools, string toolChoice, out string normalizedChoice, out string error)
        {
            string rawChoice = string.IsNullOrWhiteSpace(toolChoice) ? "auto" : toolChoice.Trim();
            normalizedChoice = rawChoice.ToLowerInvariant();
            error = null;
            if (normalizedChoice != "auto" && normalizedChoice != "none")
            { error = "tool_choice 只允许 auto/none"; return false; }
            if (!ToolArgumentsValidator.ValidateDefinitions(tools, out error)) return false;
            return true;
        }

        private LlmResult Sanitize(LlmResult result)
        {
            result = result ?? new LlmResult { Ok = false, Error = "空结果" };
            result.Error = SecretRedactor.Redact(result.Error, _apiKey);
            result.Content = SecretRedactor.Redact(result.Content, _apiKey);
            result.Reasoning = SecretRedactor.Redact(result.Reasoning, _apiKey);
            result.RawContent = SecretRedactor.Redact(result.RawContent, _apiKey);
            return result;
        }

        private LlmToolResult Sanitize(LlmToolResult result)
        {
            result = result ?? new LlmToolResult { Ok = false, Error = "空结果" };
            result.Error = SecretRedactor.Redact(result.Error, _apiKey);
            result.Content = SecretRedactor.Redact(result.Content, _apiKey);
            result.Reasoning = SecretRedactor.Redact(result.Reasoning, _apiKey);
            result.ReplayReasoningContent = SecretRedactor.Redact(result.ReplayReasoningContent, _apiKey);
            if (result.ToolCalls != null)
                foreach (var call in result.ToolCalls)
                    if (call != null)
                    {
                        // ExtraFields/AssistantExtraFields 只装不透明的 provider 回灌字节(Gemini 3
                        // thought_signature / MiniMax reasoning_details)。这类长 base64url 串对宽口径正则
                        // (SkLike 等)有非零概率误命中,一旦命中就把整条 tool_call 连同副作用拒掉。故对它们
                        // 仅做 exactSecret(_apiKey)精确比对 fail-closed:真·apiKey 泄漏仍拒执行,而不透明签名
                        // 不因启发式误报被拒(也不能脱敏改写——改了签名回灌会被 provider 判无效)。
                        if (ContainsSensitiveValue(call.Id) || ContainsSensitiveValue(call.Name)
                            || ContainsExactSecret(call.ExtraFields) || ContainsExactSecret(call.AssistantExtraFields))
                        {
                            result.Ok = false;
                            result.ToolCalls = null;
                            result.Error = "provider 工具元数据含疑似密钥，已拒绝执行";
                            break;
                        }
                        string sanitized = SecretRedactor.Redact(call.ArgumentsJson, _apiKey);
                        if (!string.Equals(sanitized, call.ArgumentsJson, StringComparison.Ordinal))
                        {
                            result.Ok = false;
                            result.ToolCalls = null;
                            result.Error = "工具参数含疑似密钥，已拒绝执行";
                            break;
                        }
                    }
            return result;
        }

        private bool ContainsSensitiveValue(string value)
            => !string.Equals(value, SecretRedactor.Redact(value, _apiKey), StringComparison.Ordinal);

        private bool ContainsSensitiveValue(JToken value)
        {
            if (value == null) return false;
            string serialized = value.ToString(Formatting.None);
            return ContainsSensitiveValue(serialized);
        }

        // 仅对本客户端自己的 apiKey 做精确比对(与 SecretRedactor.Redact 的 exactSecret 语义一致:Ordinal、
        // 长度≥4),不跑宽口径正则。用于不透明 provider 回灌字段的 fail-closed,避免误报误拒。
        private bool ContainsExactSecret(JToken value)
        {
            if (value == null || string.IsNullOrEmpty(_apiKey) || _apiKey.Length < 4) return false;
            return value.ToString(Formatting.None).IndexOf(_apiKey, StringComparison.Ordinal) >= 0;
        }

        private static bool LooksLikeJsonModeUnsupported(string err)
        {
            if (string.IsNullOrEmpty(err)) return false;
            string e = err.ToLowerInvariant();
            return e.Contains("response_format")
                || (e.Contains("json") && (e.Contains("not support") || e.Contains("unsupported") || e.Contains("invalid") || e.Contains("不支持")));
        }

        // 只接受 temperature=1 的模型(Kimi kimi-k2.5/k2.x 等)。命中则请求强制温度=1,否则 provider 直接 400。
        private bool TemperatureLockedToOne(string model)
        {
            var m = (model ?? "").ToLowerInvariant();
            return m.Contains("kimi-k2") || m.Contains("kimi-latest");
        }

        // 兼容旧前端二进制；新代码不得读取这个进程级值，必须向每次 Send* 传 LlmReasoningPolicy。
        [Obsolete("请使用每次请求的 LlmReasoningPolicy，静态开关存在并发串扰。")]
        public static bool RequestThinking;

        // 按模型族发"开启思考"开关——只对【默认关思考】的家族发,免误改了别家的智能默认反而拖慢。
        // 绝大多数家族(DeepSeek-reasoner / GLM / 豆包思考版 / Kimi思考版 / 混元T1)默认就【会】吐思考、本就能显示,
        // 且多是"动态/auto"——简单话不想、难题才想;若强行 thinking:{type:enabled} 反而逼它【每轮都想】、把简单对话也拖慢。
        // 唯独通义千问 Qwen3 是真·默认【关】,不发 enable_thinking 就一个思考字都不返回——这才是"很多模型不显示思考"的真正主因,只补它。
        // 用 enable_thinking 控思考的家族(Qwen/DeepSeek/GLM/豆包/Kimi/混元/MiniMax/文心/阶跃等国产+中转);
        // OpenAI/Claude/Gemini 不认这个字段(它们用 reasoning_effort 或无),不发,免严格端 400。
        private static bool HonorsEnableThinking(string model)
        {
            var m = (model ?? "").ToLowerInvariant();
            return m.Contains("qwen") || m.Contains("qwq") || m.Contains("deepseek") || m.Contains("glm") || m.Contains("zhipu")
                || m.Contains("doubao") || m.Contains("kimi") || m.Contains("moonshot") || m.Contains("hunyuan")
                || m.Contains("minimax") || m.Contains("ernie") || m.Contains("step") || m.Contains("yi-");
        }

        private void AddThinkingControl(JObject body, LlmReasoningPolicy policy)
        {
            var m = (_model ?? "").ToLowerInvariant();
            if (_capabilities.IsLocalOllama)
            {
                // Ollama 当前 /v1/chat/completions 正式支持的是 OpenAI 形态的
                // reasoning_effort:none/low，而不是各模型原生的 enable_thinking /
                // thinking 对象。代笔、主动消息等 Off 请求若发错字段，Qwen 会继续默认
                // 思考并耗尽 8K 输出，最终只返回 finish_reason=length。
                if (policy == LlmReasoningPolicy.Off) body["reasoning_effort"] = "none";
                else if (policy == LlmReasoningPolicy.Low) body["reasoning_effort"] = "low";
                return;
            }
            if (_capabilities.IsMiMo)
            {
                // MiMo V2.5 官方 OpenAI 兼容协议使用 thinking.type；思考工具轮的
                // reasoning_content 会由 MsgToJson 在后续轮原样回灌。
                body["thinking"] = new JObject
                {
                    ["type"] = policy == LlmReasoningPolicy.Off ? "disabled" : "enabled"
                };
                return;
            }
            if (_capabilities.IsGemini)
            {
                if (_capabilities.IsOfficialGoogle && policy != LlmReasoningPolicy.Off)
                {
                    var thinkingConfig = new JObject { ["include_thoughts"] = true };
                    if (policy == LlmReasoningPolicy.Low)
                    {
                        if (m.Contains("gemini-3")) thinkingConfig["thinking_level"] = "low";
                        else if (m.Contains("gemini-2.5")) thinkingConfig["thinking_budget"] = 1024;
                    }
                    body["extra_body"] = new JObject
                    {
                        ["google"] = new JObject
                        {
                            ["thinking_config"] = thinkingConfig
                        }
                    };
                }
                else if (policy == LlmReasoningPolicy.Low) body["reasoning_effort"] = "low";
                else if (policy == LlmReasoningPolicy.Off)
                {
                    // Gemini 3 与 2.5 Pro 不能关闭，只降到最低；2.5 Flash 等可用 none。
                    body["reasoning_effort"] = (m.Contains("gemini-3") || m.Contains("pro")) ? "low" : "none";
                }
                return;
            }
            if (_capabilities.IsDeepSeekV4)
            {
                bool enabled = DeepSeekThinkingEnabled(policy);
                body["thinking"] = new JObject { ["type"] = enabled ? "enabled" : "disabled" };
                // V4 Pro 只支持 high/max，没有真正的 low。普通聊天明确请求 Low 时必须
                // 关闭思考，而不能把轻量轮静默升级成 high（实机曾因此让一次物品查询
                // 生成 4440 个思考 token、等待 104 秒）。Flash 的工具协议依赖 thinking+auto，
                // Low 仍保持 high；复杂 Agent 以 Auto 显式请求高推理。
                if (enabled) body["reasoning_effort"] = "high";
                return;
            }
            if (_capabilities.IsMiniMax)
            {
                body["reasoning_split"] = true;
                if (_capabilities.IsMiniMaxM3)
                    body["thinking"] = new JObject { ["type"] = policy == LlmReasoningPolicy.Off ? "disabled" : "adaptive" };
                // M2.x 是 thinking-only；发送 disabled 只会被接受但不生效，故不虚构已关闭。
                return;
            }
            if (_capabilities.UsesMoonshotThinkingObject)
            {
                if (policy != LlmReasoningPolicy.Auto)
                    body["thinking"] = new JObject
                    {
                        ["type"] = policy == LlmReasoningPolicy.Off
                            ? "disabled" : "enabled"
                    };
                return;
            }
            if (_capabilities.IsKimiHybrid)
            {
                // DashScope K2.5/K2.6 use enable_thinking rather than Moonshot's
                // thinking.type object. Their documented default is non-thinking, so Low must
                // opt in explicitly; all sampling fields remain omitted for the fixed profile.
                // Unknown compatibility proxies keep the minimal OpenAI request surface.
                if (_capabilities.IsAlibabaModelStudio)
                {
                    if (policy == LlmReasoningPolicy.Off) body["enable_thinking"] = false;
                    else if (policy == LlmReasoningPolicy.Low) body["enable_thinking"] = true;
                }
                return;
            }
            if (_capabilities.IsKimiK3)
            {
                // K3 是 thinking-only：不携带字段时使用官方默认 max；Low/Off 都只能安全
                // 降到最低受支持档，绝不发送无效的 enable_thinking=false。
                if (policy != LlmReasoningPolicy.Auto && _capabilities.IsOfficialMoonshot)
                    body["reasoning_effort"] = "low";
                return;
            }
            if (_capabilities.IsKimiK27Code)
            {
                // K2.7 Code/Highspeed are thinking-only and preserved-thinking is always on.
                // Moonshot requires callers to omit thinking controls and fixed sampling fields;
                // later tool rounds replay reasoning_content through MsgToJson.
                return;
            }
            if (_capabilities.IsGlm)
            {
                if (_capabilities.IsAlibabaModelStudio)
                {
                    if (policy == LlmReasoningPolicy.Off) body["enable_thinking"] = false;
                    else if (policy == LlmReasoningPolicy.Low)
                    {
                        body["enable_thinking"] = true;
                        if (_capabilities.IsGlm52) body["reasoning_effort"] = "low";
                    }
                    return;
                }
                if (policy == LlmReasoningPolicy.Off)
                    body["thinking"] = new JObject { ["type"] = "disabled" };
                else if (policy == LlmReasoningPolicy.Low)
                {
                    body["thinking"] = new JObject { ["type"] = "enabled" };
                    if (_capabilities.IsGlm52) body["reasoning_effort"] = "low";
                }
                return;
            }
            if (_capabilities.IsQwen)
            {
                if (_capabilities.IsQwenThinkingOnly) return;
                if (policy == LlmReasoningPolicy.Off) body["enable_thinking"] = false;
                else if (policy == LlmReasoningPolicy.Low)
                {
                    body["enable_thinking"] = true;
                    body["thinking_budget"] = 1024;
                }
                return;
            }
            if (_capabilities.IsOfficialAnthropic)
            {
                if (policy == LlmReasoningPolicy.Off)
                {
                    if (_capabilities.AnthropicThinkingDefaultsOnCanDisable)
                        body["thinking"] = new JObject { ["type"] = "disabled" };
                    else if (_capabilities.AnthropicThinkingAlwaysOn)
                        // Fable/Mythos 5 cannot disable thinking. Preserve the user's
                        // low-cost intent with the lowest supported effort instead.
                        body["output_config"] = new JObject { ["effort"] = "low" };
                    return;
                }
                if (policy == LlmReasoningPolicy.Low)
                {
                    if (_capabilities.AnthropicThinkingAlwaysOn)
                    {
                        // Always-on models reject disabled/manual thinking and need no
                        // thinking object. Effort is their supported cost/depth control.
                        body["output_config"] = new JObject { ["effort"] = "low" };
                    }
                    else if (_capabilities.AnthropicUsesAdaptiveThinking)
                    {
                        // Claude 4.6 已推荐 adaptive；4.7/4.8 与 Claude 5 会拒绝旧的
                        // type=enabled。output_config.effort 是当前原生低档控制字段。
                        body["thinking"] = new JObject { ["type"] = "adaptive" };
                        body["output_config"] = new JObject { ["effort"] = "low" };
                    }
                    else if (_capabilities.AnthropicUsesManualThinking)
                    {
                        // Claude 4.5 使用手动扩展思考。官方兼容示例预算为 2000，
                        // 同时遵守 budget_tokens >=1024 且严格小于输出上限。
                        int outputLimit = (int?)(body["max_completion_tokens"] ?? body["max_tokens"]) ?? 0;
                        if (outputLimit > 1024)
                            body["thinking"] = new JObject
                            {
                                ["type"] = "enabled",
                                ["budget_tokens"] = Math.Min(2000, outputLimit - 1),
                            };
                    }
                }
                return;
            }
            if (_capabilities.IsOpenAiReasoningChat)
            {
                // reasoning_effort 仅 o1/o3/o4/gpt-5 真·推理族支持;o1-mini/o1-preview/gpt-5-chat 等非推理变体发送即 400,
                // 由 SupportsReasoningEffort 精确排除。运行期若严格端仍拒收,_reasoningEffortRejected 记忆后剥离重发。
                if (policy != LlmReasoningPolicy.Auto && _capabilities.SupportsReasoningEffort && !_reasoningEffortRejected)
                    body["reasoning_effort"] = "low";
                return;
            }
            // 未识别端点无法确认是否支持 reasoning_effort:非推理 OpenAI(gpt-4o/gpt-4.1/gpt-4-turbo)、Claude 及严格兼容端
            // 会因该字段整请求 400,而三条能力协商重试都不认它 → 每句硬失败。故默认分支不再无条件下发 reasoning_effort;
            // 真正支持它的 OpenAI 推理族已在上面的 IsOpenAiReasoningChat 分支按能力位下发,其余家族(GLM/豆包/Kimi 等)走 enable_thinking。
            if (policy == LlmReasoningPolicy.Off && HonorsEnableThinking(m)) body["enable_thinking"] = false;
        }

        private bool DeepSeekThinkingEnabled(LlmReasoningPolicy policy)
        {
            if (!_capabilities.IsDeepSeekV4 || policy == LlmReasoningPolicy.Off) return false;
            if (policy == LlmReasoningPolicy.Low && !_capabilities.IsDeepSeekV4Flash) return false;
            if (_capabilities.IsLegacyDeepSeekAlias && policy == LlmReasoningPolicy.Auto)
                return string.Equals(_model, "deepseek-reasoner", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        private bool ReasoningReplayThinkingEnabled(LlmReasoningPolicy policy)
        {
            if (_capabilities.IsDeepSeekV4) return DeepSeekThinkingEnabled(policy);
            if (_capabilities.IsMiMo) return policy != LlmReasoningPolicy.Off;
            return false;
        }

        // 工具循环历史里是否存在“无可回灌思考”的助手工具消息。空串是合法回灌值(provider 确实
        // 发过 reasoning_content 字段)；只有 null(那一轮响应根本没有该字段)才代表思考模式
        // 协议无法满足。
        private static bool HasToolCallMessageWithoutReasoningReplay(IList<LlmMessage> messages)
        {
            if (messages == null) return false;
            foreach (var m in messages)
                if (m != null && m.Role == "assistant" && m.ToolCalls != null && m.ToolCalls.Count > 0
                    && m.ReasoningContent == null)
                    return true;
            return false;
        }

        private JObject BuildBody(IList<LlmMessage> messages, int maxTokens, double temperature, bool jsonMode, JArray tools,
            string toolChoice, bool noThinking = false)
            => BuildBodyWithPolicy(messages, maxTokens, temperature, jsonMode, tools, toolChoice, noThinking,
                noThinking ? LlmReasoningPolicy.Off : LlmReasoningPolicy.Auto);

        private JObject BuildBodyWithPolicy(IList<LlmMessage> messages, int maxTokens, double temperature, bool jsonMode, JArray tools,
            string toolChoice, bool noThinking, LlmReasoningPolicy reasoningPolicy)
        {
            LlmReasoningPolicy effectivePolicy = noThinking ? LlmReasoningPolicy.Off : reasoningPolicy;
            // MiMo V2.5 currently aborts response_format=json_object requests when thinking
            // is enabled, often after spending the entire completion budget on reasoning.
            // Structured callers need a complete machine-readable object, so only that
            // request shape is forced to the provider's documented thinking-off mode.
            if (jsonMode && _capabilities.IsMiMo) effectivePolicy = LlmReasoningPolicy.Off;
            bool deepSeekThinking = DeepSeekThinkingEnabled(effectivePolicy);
            bool reasoningReplayThinking = ReasoningReplayThinkingEnabled(effectivePolicy);
            // JHYL_REASONING_REPLAY_CONSISTENCY:
            // DeepSeek V4 与 MiMo V2.5 的思考工具循环都要求请求中每条 assistant.tool_calls
            // 原样带回当轮 reasoning_content。若历史工具轮根本没产出该字段，本轮只能保持
            // 思考关闭，不能伪造思考内容或把整个后续请求送成 400。
            if (reasoningReplayThinking && HasToolCallMessageWithoutReasoningReplay(messages))
            {
                effectivePolicy = LlmReasoningPolicy.Off;
                deepSeekThinking = false;
                reasoningReplayThinking = false;
            }
            // 同一协议的对称面：明确关闭思考时不携带 reasoning_content。只对声明了
            // 回灌协议的 provider 生效；其他 provider 维持“谁产出、回灌谁”。
            bool omitReasoningReplay = _capabilities.RequiresThinkingReasoningReplay && !reasoningReplayThinking;

            var arr = new JArray();
            // OpenAI 兼容请求保持最小公共消息面。Anthropic 官方明确说明其兼容层不支持
            // prompt caching，不能把 native cache_control 塞进兼容 messages；其余 provider
            // 的自动前缀缓存也不需要请求扩展字段。
            // 兼容"system 消息必须全在最前"的严格模型:先按原相对顺序拼入所有 system,再拼其余(工具调用与其结果配对顺序不变)。
            foreach (var m in messages) if (m != null && m.Role == "system") arr.Add(MsgToJson(m));
            foreach (var m in messages) if (m != null && m.Role != "system") arr.Add(MsgToJson(m));
            if (omitReasoningReplay)
                foreach (var token in arr)
                    if (token is JObject serialized) serialized.Remove("reasoning_content");
            if (maxTokens <= 0) maxTokens = DefaultMaxTokens;   // 0/负 → 用实例默认上限(当前默认 32768);实例默认也为 0 时整体省略 max_tokens
            // 个别模型只接受固定温度；OpenAI o1/o3/o4 ChatCompletions 则不接收 temperature。
            if (TemperatureLockedToOne(_model)) temperature = 1.0;
            var body = new JObject
            {
                ["model"] = _model,
                ["messages"] = arr,
                ["stream"] = false,
            };
            if (_capabilities.SupportsSamplingTemperature && !deepSeekThinking) body["temperature"] = temperature;
            // 抑制"无限重复/复读卡死"(推理/弱模型常见的原地打转):frequency_penalty 罚高频 token、presence_penalty 鼓励换话题。
            // 严格推理端(o1/o3/gpt-5,温度锁定那批)不认这两个惩罚项、会整请求 400 → 这些端一律不发。
            if (!TemperatureLockedToOne(_model) && _capabilities.SupportsSamplingTemperature
                && !_capabilities.IsOfficialDeepSeek && !_capabilities.IsMiniMax && !_capabilities.IsOfficialAnthropic)
            {
                body["frequency_penalty"] = 0.5;
                body["presence_penalty"] = 0.3;
            }
            if (maxTokens > 0) body[_capabilities.UsesMaxCompletionTokens ? "max_completion_tokens" : "max_tokens"] = maxTokens;
            if (jsonMode && _capabilities.SupportsResponseFormat)
                body["response_format"] = new JObject { ["type"] = "json_object" };
            if (tools != null)
            {
                JArray requestTools = tools;
                string wireToolChoice = toolChoice;
                if (string.Equals(toolChoice, "none", StringComparison.OrdinalIgnoreCase))
                {
                    // Prose-only closure is enforced by withholding every tool. No provider
                    // receives tool_choice=none, avoiding another capability-specific branch.
                    requestTools = null;
                    wireToolChoice = null;
                }
                else
                {
                    // All tool-bearing requests use one common wire mode.
                    wireToolChoice = "auto";
                }

                if (requestTools != null) body["tools"] = requestTools;
                if (_capabilities.RequiresToolStream
                    && requestTools is JArray streamedTools
                    && streamedTools.Count > 0)
                    body["tool_stream"] = true;
                // DeepSeek V4 thinking + auto 不支持显式 tool_choice；省略即为服务端默认 auto。
                if (requestTools != null && !string.IsNullOrEmpty(wireToolChoice)
                    && !(deepSeekThinking
                        && string.Equals(wireToolChoice, "auto", StringComparison.OrdinalIgnoreCase)))
                    body["tool_choice"] = wireToolChoice;
            }
            // 推理策略属于本次请求，和 UI 是否展示分离；工具模式不再改变思考策略。
            AddThinkingControl(body, effectivePolicy);
            return body;
        }

        // 消息序列化:支持 assistant.tool_calls 与 role=tool 回传。这里只生成 OpenAI
        // 兼容消息；Anthropic 等厂商的 native content/cache block 不得混入。
        private static JObject MsgToJson(LlmMessage m)
        {
            var o = new JObject { ["role"] = m.Role };
            if (m.Role == "tool")
            {
                o["tool_call_id"] = m.ToolCallId ?? "";
                o["content"] = m.Content ?? "";
                return o;
            }
            if (m.ToolCalls != null && m.ToolCalls.Count > 0)
            {
                o["content"] = m.Content ?? "";
                if (m.ReasoningContent != null) o["reasoning_content"] = m.ReasoningContent;
                var assistantExtra = m.ToolCalls[0]?.AssistantExtraFields;
                if (assistantExtra?["reasoning_details"] != null)
                    o["reasoning_details"] = assistantExtra["reasoning_details"].DeepClone();
                var tc = new JArray();
                foreach (var c in m.ToolCalls)
                {
                    var call = new JObject();
                    if (c.ExtraFields != null)
                        foreach (var p in c.ExtraFields.Properties())
                            if (p.Name != "index" && p.Name != "id" && p.Name != "type" && p.Name != "function")
                                call[p.Name] = p.Value?.DeepClone();
                    call["id"] = c.Id;
                    call["type"] = "function";
                    call["function"] = new JObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson ?? "{}" };
                    tc.Add(call);
                }
                o["tool_calls"] = tc;
                return o;
            }
            o["content"] = m.Content ?? "";
            return o;
        }

        /// <summary>
        /// Normalizes Gemini signature aliases emitted by OpenAI-compatible gateways onto
        /// the documented first tool-call location. If a non-Google gateway has stripped
        /// the metadata completely, the caller may opt into Google's documented validator
        /// bypass sentinel. Tool ids, names, arguments and authorization are still validated
        /// before this method runs; the sentinel only repairs reasoning-history continuity.
        /// </summary>
        internal static bool EnsureGeminiThoughtSignature(IList<LlmToolCall> calls, JToken providerEnvelope,
            bool allowCompatibilityFallback, out bool fallbackApplied)
        {
            fallbackApplied = false;
            if (calls == null || calls.Count == 0 || calls[0] == null) return false;

            JToken signature = FindGeminiThoughtSignature(calls[0].ExtraFields, 0);
            if (!IsNonEmptyString(signature))
                signature = IsNonEmptyString(providerEnvelope)
                    ? providerEnvelope
                    : FindGeminiThoughtSignatureFromAssistantEnvelope(providerEnvelope);
            if (!IsNonEmptyString(signature) && allowCompatibilityFallback)
            {
                signature = new JValue(GeminiSignatureCompatibilitySentinel);
                fallbackApplied = true;
            }
            if (!IsNonEmptyString(signature)) return false;
            if (Encoding.UTF8.GetByteCount(signature.Value<string>()) > LlmProtocolLimits.MaxProviderMetadataBytes)
                return false;

            var extra = calls[0].ExtraFields ?? new JObject();
            var extraContentToken = extra["extra_content"];
            if (extraContentToken != null && extraContentToken.Type != JTokenType.Null
                && extraContentToken.Type != JTokenType.Object) return false;
            var extraContent = extraContentToken as JObject ?? new JObject();
            var googleToken = extraContent["google"];
            if (googleToken != null && googleToken.Type != JTokenType.Null
                && googleToken.Type != JTokenType.Object) return false;
            var google = googleToken as JObject ?? new JObject();
            google["thought_signature"] = signature.DeepClone();
            extraContent["google"] = google;
            extra["extra_content"] = extraContent;
            if (Encoding.UTF8.GetByteCount(extra.ToString(Formatting.None)) > LlmProtocolLimits.MaxProviderMetadataBytes)
                return false;
            calls[0].ExtraFields = extra;
            return true;
        }

        internal static JToken FindGeminiThoughtSignature(JToken token, int depth)
        {
            if (token == null || depth > 12) return null;
            if (token is JObject obj)
            {
                foreach (string name in new[] { "thought_signature", "thoughtSignature" })
                {
                    var direct = obj[name];
                    if (IsNonEmptyString(direct)) return direct;
                }
                foreach (var property in obj.Properties())
                {
                    var nested = FindGeminiThoughtSignature(property.Value, depth + 1);
                    if (IsNonEmptyString(nested)) return nested;
                }
            }
            else if (token is JArray array)
            {
                foreach (var item in array)
                {
                    var nested = FindGeminiThoughtSignature(item, depth + 1);
                    if (IsNonEmptyString(nested)) return nested;
                }
            }
            return null;
        }

        /// <summary>
        /// Reads only assistant-level aliases. Deliberately excludes tool_calls so a
        /// signature attached to the second parallel call can never be borrowed by the first.
        /// </summary>
        internal static JToken FindGeminiThoughtSignatureFromAssistantEnvelope(JToken token)
        {
            if (!(token is JObject obj)) return null;
            foreach (string name in new[] { "thought_signature", "thoughtSignature" })
            {
                var direct = obj[name];
                if (IsNonEmptyString(direct)) return direct;
            }
            // extra_content is assistant-level provider metadata. Do not scan content/parts:
            // a gateway can mirror parallel function-call parts there, which would make the
            // signature's ordinal ambiguous just like scanning tool_calls itself.
            var nested = FindGeminiThoughtSignature(obj["extra_content"], 0);
            if (IsNonEmptyString(nested)) return nested;
            return null;
        }

        private static bool IsNonEmptyString(JToken token)
            => token != null && token.Type == JTokenType.String && !string.IsNullOrEmpty(token.Value<string>());

        private static LlmResult Parse(string text, int status)
        {
            if (status < 200 || status >= 300)
            {
                if (LlmJsonProtocol.TryParseObject(text, LlmProtocolLimits.MaxResponseBytes, out var errorBody, out _))
                    return new LlmResult { Ok = false, Error = JsonErrorWithStatus(errorBody, status) };
                return new LlmResult { Ok = false, Error = NonJsonError(text, status, null) };
            }
            try
            {
                if (!LlmJsonProtocol.TryParseObject(text, LlmProtocolLimits.MaxResponseBytes, out var o, out var parseError))
                    return new LlmResult { Ok = false, Error = NonJsonError(text, status, new JsonReaderException(parseError)) };
                var choices = o["choices"] as JArray;
                if (choices == null || choices.Count == 0)
                {
                    return new LlmResult { Ok = false, Error = JsonErrorWithStatus(o, status) };
                }
                if (choices.Count != 1) return new LlmResult { Ok = false, Error = "正文响应 choices 数量不是 1" };
                var choice = choices[0];
                string finishReason = choice?["finish_reason"]?.Type == JTokenType.Null ? null : choice?["finish_reason"]?.ToString();
                if (string.IsNullOrEmpty(finishReason)) return new LlmResult { Ok = false, Error = "正文响应缺少 finish_reason" };
                if (!string.Equals(finishReason, "stop", StringComparison.Ordinal))
                    return new LlmResult { Ok = false, Error = "正文响应未完整结束(finish_reason=" + finishReason + ")" };
                var msg = choice?["message"];
                if (msg == null || msg.Type != JTokenType.Object) return new LlmResult { Ok = false, Error = "正文响应缺少 message" };
                ResponseContentExtractor.Extract(msg?["content"] ?? msg?["parts"], out var visTxt, out var inlineThink, out var raw);
                var reasoning = ResponseContentExtractor.ExtractReasoningFields(msg);
                var content = (visTxt ?? "").Trim();
                reasoning = ResponseContentExtractor.JoinReasoning(reasoning, inlineThink);
                if (content.Length == 0) return new LlmResult { Ok = false, Error = "正文响应为空" };
                if (content.Length > LlmProtocolLimits.MaxVisibleChars) return new LlmResult { Ok = false, Error = "正文超过字符上限" };
                if ((reasoning?.Length ?? 0) > LlmProtocolLimits.MaxReasoningChars) return new LlmResult { Ok = false, Error = "思考正文超过字符上限" };
                var usage = LlmUsage.Parse(o["usage"]);
                return new LlmResult
                {
                    Ok = true,
                    Content = content,
                    Reasoning = reasoning,
                    RawContent = raw,
                    PromptTokens = usage.PromptTokens,
                    CompletionTokens = usage.CompletionTokens,
                    ReasoningTokens = usage.ReasoningTokens,
                    TotalTokens = usage.TotalTokens,
                    CachedTokens = usage.CacheHitTokens,
                    CacheMissTokens = usage.CacheMissTokens,
                    CacheWriteTokens = usage.CacheWriteTokens,
                };
            }
            catch (Exception ex)
            {
                return new LlmResult { Ok = false, Error = NonJsonError(text, status, ex) };
            }
        }

        // 响应体非 JSON(网关/代理/认证层常返回纯文本错误,如「Authentication Fails (governor)」)→ 把状态码+原文当错误直接呈现,
        // 别再报「parse: Unexpected character…」那种把真因(认证失败/限流/欠费)埋掉的 JSON 解析错。
        private static string NonJsonError(string text, int status, Exception ex)
        {
            string body = (text ?? "").Trim();
            string snip = body.Length > 240 ? body.Substring(0, 240) : body;
            snip = SecretRedactor.Redact(snip);
            if (string.IsNullOrEmpty(body))
                return "接口返回空响应(HTTP " + status + ")" + (status == 401 || status == 403 ? ":认证失败,请检查 apiKey" : "");
            string hint = "";
            if (status == 401 || status == 403) hint = "(认证失败,请检查 apiKey 是否正确/有权限) ";
            else if (status == 402) hint = "(余额不足/欠费) ";
            else if (status == 429) hint = "(请求过于频繁或额度耗尽) ";
            // body 看着不像 JSON(网关纯文本错误)→ 只呈现状态码+原文,不附 JSON 解析异常;像 JSON 才保留少量异常线索
            bool looksJson = body.StartsWith("{") || body.StartsWith("[");
            string parse = looksJson && ex != null
                ? ("响应处理失败(parse_type=" + ExceptionTypeChain(ex) + ") ")
                : "";
            return "接口返回 HTTP " + status + " " + hint + parse + snip;
        }

        private static string JsonErrorWithStatus(JObject body, int status)
        {
            string detail = body?["base_resp"]?["status_msg"]?.ToString()
                ?? (body?["error"]?.Type == JTokenType.String
                    ? body["error"].Value<string>()
                    : body?["error"]?["message"]?.ToString());
            if (string.IsNullOrWhiteSpace(detail)) return "HTTP " + status;
            detail = SecretRedactor.Redact(detail.Trim()).Replace('\r', ' ').Replace('\n', ' ');
            if (detail.Length > 400) detail = detail.Substring(0, 400);
            return "HTTP " + status + ": " + detail;
        }

        private static string ExceptionTypeChain(Exception error)
        {
            if (error == null) return "Unknown";
            var parts = new List<string>(3);
            for (Exception current = error; current != null && parts.Count < 3; current = current.InnerException)
                parts.Add(current.GetType().Name);
            return string.Join("<=", parts);
        }

        // 保留两参数入口供离线反射测试；真实请求会按模型启用额外协议校验。
        private static LlmToolResult ParseTool(string text, int status)
            => ParseToolValidated(text, status, false);

        // 解析工具响应。工具会产生游戏副作用，因此 id/name/arguments/结束原因必须 fail-closed，
        // 绝不能随机补 id 或把缺失参数猜成 {} 后执行。
        private static LlmToolResult ParseToolValidated(string text, int status, bool requireGeminiThoughtSignature)
            => ParseToolValidatedCore(text, status, requireGeminiThoughtSignature, false, null, "auto", false);

        private static LlmToolResult ParseToolValidatedWithSchema(string text, int status, bool requireGeminiThoughtSignature,
            IList<ToolDef> tools, string expectedToolChoice)
            => ParseToolValidatedCore(text, status, requireGeminiThoughtSignature, false, tools, expectedToolChoice, true);

        private static LlmToolResult ParseToolValidatedWithSchemaForProvider(string text, int status,
            bool requireGeminiThoughtSignature, bool allowGeminiSignatureCompatibilityFallback,
            IList<ToolDef> tools, string expectedToolChoice)
            => ParseToolValidatedCore(text, status, requireGeminiThoughtSignature,
                allowGeminiSignatureCompatibilityFallback, tools, expectedToolChoice, true);

        private static LlmToolResult ParseToolValidatedCore(string text, int status, bool requireGeminiThoughtSignature,
            bool allowGeminiSignatureCompatibilityFallback, IList<ToolDef> tools,
            string expectedToolChoice, bool enforceAllowedTools)
        {
            if (status < 200 || status >= 300)
            {
                if (LlmJsonProtocol.TryParseObject(text, LlmProtocolLimits.MaxResponseBytes, out var errorBody, out _))
                    return new LlmToolResult { Ok = false, Error = JsonErrorWithStatus(errorBody, status) };
                return new LlmToolResult { Ok = false, Error = NonJsonError(text, status, null) };
            }
            try
            {
                if (!LlmJsonProtocol.TryParseObject(text, LlmProtocolLimits.MaxResponseBytes, out var o, out var parseError))
                    return new LlmToolResult { Ok = false, Error = NonJsonError(text, status, new JsonReaderException(parseError)) };
                var choices = o["choices"] as JArray;
                if (choices == null || choices.Count == 0)
                {
                    return new LlmToolResult { Ok = false, Error = JsonErrorWithStatus(o, status) };
                }
                if (choices.Count != 1) return new LlmToolResult { Ok = false, Error = "工具响应 choices 数量不是 1" };
                var choice = choices[0];
                var finishToken = choice?["finish_reason"];
                if (finishToken != null && finishToken.Type != JTokenType.Null && finishToken.Type != JTokenType.String)
                    return new LlmToolResult { Ok = false, Error = "工具响应 finish_reason 不是字符串" };
                string finishReason = finishToken?.Type == JTokenType.Null ? null : finishToken?.Value<string>();
                if (string.IsNullOrEmpty(finishReason))
                    return new LlmToolResult { Ok = false, Error = "工具响应缺少 finish_reason" };
                if (!string.IsNullOrEmpty(finishReason) && finishReason != "stop" && finishReason != "tool_calls")
                    return new LlmToolResult { Ok = false, Error = "工具响应未完整结束(finish_reason=" + finishReason + ")" };
                var msg = choice?["message"];
                if (msg == null || msg.Type != JTokenType.Object) return new LlmToolResult { Ok = false, Error = "工具响应缺少 message" };
                ResponseContentExtractor.Extract(msg?["content"] ?? msg?["parts"], out var visTxt, out var inlineThink, out var raw);
                var reasoning = ResponseContentExtractor.ExtractReasoningFields(msg);
                var replayReasoning = ResponseContentExtractor.ExtractReplayReasoningContent(msg);
                var content = (visTxt ?? "").Trim();
                reasoning = ResponseContentExtractor.JoinReasoning(reasoning, inlineThink);
                var calls = new List<LlmToolCall>();
                var callIds = new HashSet<string>(StringComparer.Ordinal);
                var toolCallsToken = msg?["tool_calls"];
                if (toolCallsToken != null && toolCallsToken.Type != JTokenType.Null && toolCallsToken.Type != JTokenType.Array)
                    return new LlmToolResult { Ok = false, Error = "tool_calls 不是数组" };
                var tcs = toolCallsToken as JArray;
                if (tcs != null)
                {
                    if (tcs.Count > LlmProtocolLimits.MaxToolCalls)
                        return new LlmToolResult { Ok = false, Error = "工具调用超过上限" };
                    int totalArgumentBytes = 0;
                    foreach (var tc in tcs)
                    {
                        if (tc == null || tc.Type != JTokenType.Object)
                            return new LlmToolResult { Ok = false, Error = "tool_call 不是对象" };
                        var fn = tc["function"];
                        if (fn == null || fn.Type != JTokenType.Object)
                            return new LlmToolResult { Ok = false, Error = "tool_call 缺少 function" };
                        var idToken = tc["id"];
                        if (idToken == null || idToken.Type != JTokenType.String)
                            return new LlmToolResult { Ok = false, Error = "tool_call 缺少字符串原始 id" };
                        string id = idToken.Value<string>();
                        if (string.IsNullOrWhiteSpace(id))
                            return new LlmToolResult { Ok = false, Error = "tool_call 缺少原始 id" };
                        if (id.Length > LlmProtocolLimits.MaxToolIdChars)
                            return new LlmToolResult { Ok = false, Error = "tool_call id 过长" };
                        if (!callIds.Add(id))
                            return new LlmToolResult { Ok = false, Error = "tool_call id 重复:" + id };
                        var nameToken = fn["name"];
                        if (nameToken == null || nameToken.Type != JTokenType.String)
                            return new LlmToolResult { Ok = false, Error = "tool_call 缺少字符串函数名" };
                        string name = nameToken.Value<string>();
                        if (string.IsNullOrWhiteSpace(name))
                            return new LlmToolResult { Ok = false, Error = "tool_call 缺少函数名" };
                        if (name.Length > LlmProtocolLimits.MaxToolNameChars)
                            return new LlmToolResult { Ok = false, Error = "tool_call 函数名过长" };
                        var argsToken = fn["arguments"];
                        if (argsToken == null || argsToken.Type != JTokenType.String)
                            return new LlmToolResult { Ok = false, Error = "tool_call arguments 必须是 JSON 字符串" };
                        string arguments = argsToken.Value<string>();
                        name = ToolArgumentsValidator.NormalizeKnownReadOnlyAlias(name, tools, arguments);
                        int argumentBytes = Encoding.UTF8.GetByteCount(arguments);
                        totalArgumentBytes += argumentBytes;
                        if (argumentBytes > LlmProtocolLimits.MaxToolArgumentsBytes
                            || totalArgumentBytes > LlmProtocolLimits.MaxTotalToolArgumentsBytes)
                            return new LlmToolResult { Ok = false, Error = "工具参数超过字节上限" };
                        if (!LlmJsonProtocol.TryParseObject(arguments, LlmProtocolLimits.MaxToolArgumentsBytes, out _, out var argumentError))
                            return new LlmToolResult { Ok = false, Error = "工具参数不完整:" + argumentError };
                        if (enforceAllowedTools && !ToolArgumentsValidator.ValidateCall(tools, name, arguments, out var validationError))
                            return new LlmToolResult { Ok = false, Error = "工具参数未通过 schema:" + validationError };
                        var extra = new JObject();
                        var tcObj = tc as JObject;
                        if (tcObj != null)
                            foreach (var p in tcObj.Properties())
                                if (p.Name != "index" && p.Name != "id" && p.Name != "type" && p.Name != "function")
                                    extra[p.Name] = p.Value?.DeepClone();
                        if (extra.Count > 0 && Encoding.UTF8.GetByteCount(extra.ToString(Formatting.None)) > LlmProtocolLimits.MaxProviderMetadataBytes)
                            return new LlmToolResult { Ok = false, Error = "provider tool_call 扩展字段过大" };
                        calls.Add(new LlmToolCall
                        {
                            Id = id,
                            Name = name,
                            ArgumentsJson = arguments,
                            ExtraFields = extra.Count > 0 ? extra : null,
                        });
                    }
                }
                if (calls.Count > 0 && msg?["reasoning_details"] != null)
                {
                    var assistantFields = new JObject
                    {
                        ["reasoning_details"] = msg["reasoning_details"].DeepClone()
                    };
                    if (Encoding.UTF8.GetByteCount(assistantFields.ToString(Formatting.None))
                        > LlmProtocolLimits.MaxProviderMetadataBytes)
                        return new LlmToolResult { Ok = false, Error = "assistant provider 推理字段过大" };
                    calls[0].AssistantExtraFields = assistantFields;
                }
                if (finishReason == "tool_calls" && calls.Count == 0)
                    return new LlmToolResult { Ok = false, Error = "finish_reason=tool_calls 但响应没有完整工具调用" };
                if (finishReason != "tool_calls" && calls.Count > 0)
                    return new LlmToolResult { Ok = false, Error = "finish_reason=" + finishReason + " 却携带工具调用" };
                string expected = (expectedToolChoice ?? "auto").Trim().ToLowerInvariant();
                if (expected == "none" && calls.Count > 0)
                    return new LlmToolResult { Ok = false, Error = "tool_choice=none 但模型仍返回工具调用" };
                if (finishReason == "stop" && calls.Count == 0 && string.IsNullOrWhiteSpace(content))
                    return new LlmToolResult { Ok = false, Error = "工具轮最终正文为空" };
                if (content.Length > LlmProtocolLimits.MaxVisibleChars)
                    return new LlmToolResult { Ok = false, Error = "工具轮正文超过字符上限" };
                if ((reasoning?.Length ?? 0) > LlmProtocolLimits.MaxReasoningChars)
                    return new LlmToolResult { Ok = false, Error = "工具轮思考超过字符上限" };
                if (requireGeminiThoughtSignature && calls.Count > 0)
                {
                    if (!EnsureGeminiThoughtSignature(calls, msg,
                        allowGeminiSignatureCompatibilityFallback, out _))
                        return new LlmToolResult { Ok = false, Error = "Gemini 3 首个 tool_call 缺少 thought_signature" };
                }
                var usage = LlmUsage.Parse(o["usage"]);
                return new LlmToolResult
                {
                    Ok = true,
                    Content = content,
                    ToolCalls = calls.Count > 0 ? calls : null,
                    Reasoning = reasoning,
                    ReplayReasoningContent = replayReasoning,
                    PromptTokens = usage.PromptTokens,
                    CompletionTokens = usage.CompletionTokens,
                    ReasoningTokens = usage.ReasoningTokens,
                    TotalTokens = usage.TotalTokens,
                    CachedTokens = usage.CacheHitTokens,
                    CacheMissTokens = usage.CacheMissTokens,
                    CacheWriteTokens = usage.CacheWriteTokens,
                };
            }
            catch (Exception ex)
            {
                return new LlmToolResult { Ok = false, Error = NonJsonError(text, status, ex) };
            }
        }

        internal static int ReadCachedTokens(JToken usage, int fallback = 0)
            => LlmUsage.Parse(usage, new LlmUsage { CacheHitTokens = fallback }).CacheHitTokens;

        internal static int ReadCacheWriteTokens(JToken usage, int fallback = 0)
            => LlmUsage.Parse(usage, new LlmUsage { CacheWriteTokens = fallback }).CacheWriteTokens;

        internal static int ReadCacheMissTokens(JToken usage, int fallback = 0)
            => LlmUsage.Parse(usage, new LlmUsage { CacheMissTokens = fallback }).CacheMissTokens;

        internal static int ReadReasoningTokens(JToken usage, int fallback = 0)
            => LlmUsage.Parse(usage, new LlmUsage { ReasoningTokens = fallback }).ReasoningTokens;

        internal static int ReadTotalTokens(JToken usage, int fallback = 0)
            => LlmUsage.Parse(usage, new LlmUsage { TotalTokens = fallback }).TotalTokens;
    }
}
