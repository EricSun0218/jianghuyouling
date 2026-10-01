using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Llm
{
    /// <summary>
    /// 流式工具响应累加器(纯逻辑,可离线单测):把一段段 SSE chunk 的 JSON 喂进来,
    /// 实时回调"正文增量 / 思考增量 / 工具名",收尾产出与非流式 ParseTool 同形的 LlmToolResult。
    /// 兼容多种思考来源(覆盖不同模型/服务端约定):① delta.reasoning_content(DeepSeek/MiniMax/GLM/Qwen 等);
    /// ② delta.reasoning(OpenRouter-Claude / LM Studio gpt-oss);③ delta.reasoning_details[](OpenRouter 新结构);
    /// ④ 正文内联 &lt;think&gt;/&lt;thinking&gt;/Kimi 的 ◁think▷ 标签(可跨 chunk 切断,半截标签先扣住)。
    /// 思考与工具名只是"展示";最终 Content/ToolCalls 才是 agentic 循环消费的真值。
    /// </summary>
    public sealed class StreamingToolCollector
    {
        private sealed class ToolCallBuilder
        {
            public string Id;
            public string Name;
            public readonly StringBuilder Args = new StringBuilder();
            public int ArgsBytes;
            public readonly JObject ExtraFields = new JObject();
        }

        private readonly StringBuilder _visibleContent = new StringBuilder();
        private readonly StringBuilder _inlineThinking = new StringBuilder();
        private readonly StringBuilder _tagPending = new StringBuilder();
        private bool _insideInlineThinking;
        private readonly StringBuilder _reasoning = new StringBuilder();    // 展示用：各家 reasoning/summary 的归一化文本
        private readonly StringBuilder _replayReasoning = new StringBuilder(); // 回灌用：逐片原样 reasoning_content
        private readonly JObject _assistantProviderFields = new JObject();
        private readonly StringBuilder _geminiSignatureCandidate = new StringBuilder();
        private bool _sawReplayReasoning;
        private readonly Dictionary<int, ToolCallBuilder> _tools = new Dictionary<int, ToolCallBuilder>();
        private readonly Dictionary<int, int> _parallelNoIndexSlots = new Dictionary<int, int>();
        private readonly HashSet<int> _named = new HashSet<int>();
        private bool _done;
        private string _lastCumulativeContent = "";
        private string _lastCumulativeReasoning = "";
        private int _totalToolArgumentBytes;
        private int _providerMetadataBytes;
        private IncrementalSecretRedactor _contentCallbackRedactor;
        private IncrementalSecretRedactor _thinkingCallbackRedactor;
        private bool _callbackRedactorsFlushed;

        public int PromptTokens, CompletionTokens, ReasoningTokens, TotalTokens, CachedTokens, CacheMissTokens, CacheWriteTokens;
        public bool SawDone { get; private set; }
        public bool SawFinishReason { get; private set; }
        public string FinishReason { get; private set; }
        public string ProtocolError { get; private set; }
        public bool IsProtocolComplete => string.IsNullOrEmpty(ProtocolError)
            && (RequireFinishReason ? SawFinishReason : (SawDone || SawFinishReason))
            && (!RequireDoneSentinel || SawDone);
        /// <summary>真实 HTTP 流必须拿到明确 finish_reason；[DONE] 只是 SSE 哨兵，不能证明工具参数完整。</summary>
        public bool RequireFinishReason { get; set; }
        /// <summary>真实 OpenAI SSE 还必须收到 data:[DONE]，不能只靠连接关闭推断结束。</summary>
        public bool RequireDoneSentinel { get; set; }
        /// <summary>Gemini 3 工具轮必须在第一个函数调用原位带 thought_signature，否则禁止执行副作用。</summary>
        public bool RequireGeminiThoughtSignature { get; set; }
        /// <summary>Only for non-Google compatibility gateways that strip Gemini metadata.</summary>
        public bool AllowGeminiSignatureCompatibilityFallback { get; set; }
        /// <summary>Provider-scoped compatibility for gateways that label a complete tool call as stop.</summary>
        public bool AllowStopFinishWithCompleteToolCalls { get; set; }
        public IList<ToolDef> AllowedTools { get; set; }
        /// <summary>真实工具请求即使本轮工具列表为空，也必须拒绝 provider 凭空返回的调用。</summary>
        public bool EnforceAllowedTools { get; set; }
        public string ExpectedToolChoice { get; set; } = "auto";
        public bool CumulativeContent { get; set; }
        public bool CumulativeReasoning { get; set; }
        public bool TreatStringNullFinishAsMissing { get; set; }
        /// <summary>
        /// Provider-scoped compatibility for non-semantic SSE heartbeat frames such as
        /// {"choices":[],"usage":null}.  It never relaxes finish_reason/[DONE] validation.
        /// </summary>
        public bool AllowEmptyChoicesHeartbeat { get; set; }
        /// <summary>仅用于实时 UI 回调的进程内凭据；最终结果仍由客户端统一 Sanitize。</summary>
        public string CallbackExactSecret { get; set; }
        public long InlineCharactersProcessed { get; private set; }
        public Action<string> OnContent;     // 可见正文增量
        public Action<string> OnThinking;    // 思考增量(reasoning_content + 内联 think)
        public Action<string> OnToolName;    // 某工具名首次出现

        /// <summary>喂入一个 SSE data 负载(已去掉 "data:" 前缀的 JSON 串)。返回 false = 收到 [DONE]/应停止。</summary>
        public bool Feed(string payload)
        {
            if (!string.IsNullOrEmpty(ProtocolError)) return false;
            if (_done)
            {
                if (!string.IsNullOrEmpty(payload)) ProtocolError = "SSE 在 [DONE]/终止错误后仍有 data 事件";
                return false;
            }
            if (string.IsNullOrEmpty(payload)) return true;
            if (payload == "[DONE]")
            {
                SawDone = true;
                if (RequireFinishReason && !SawFinishReason)
                    ProtocolError = "流式响应只有 [DONE]，缺少 finish_reason";
                _done = true;
                return false;
            }
            if (!LlmJsonProtocol.TryParseObject(payload, LlmProtocolLimits.MaxSseEventChars * 4, out var o, out var jsonError))
            {
                ProtocolError = "SSE data 不是完整 JSON:" + jsonError;
                _done = true;
                return false;
            }

            var usage = o["usage"];
            if (usage != null && usage.Type == JTokenType.Object)
            {
                var parsedUsage = LlmUsage.Parse(usage, new LlmUsage
                {
                    PromptTokens = PromptTokens, CompletionTokens = CompletionTokens,
                    ReasoningTokens = ReasoningTokens, TotalTokens = TotalTokens,
                    CacheHitTokens = CachedTokens, CacheMissTokens = CacheMissTokens,
                    CacheWriteTokens = CacheWriteTokens,
                });
                PromptTokens = parsedUsage.PromptTokens; CompletionTokens = parsedUsage.CompletionTokens;
                ReasoningTokens = parsedUsage.ReasoningTokens; TotalTokens = parsedUsage.TotalTokens;
                CachedTokens = parsedUsage.CacheHitTokens; CacheMissTokens = parsedUsage.CacheMissTokens;
                CacheWriteTokens = parsedUsage.CacheWriteTokens;
            }

            var choicesToken = o["choices"];
            if (choicesToken == null || choicesToken.Type == JTokenType.Null)
            {
                if (usage != null && usage.Type == JTokenType.Object) return true;
                ProtocolError = "SSE data 缺少 choices/usage"; _done = true; return false;
            }
            if (choicesToken.Type != JTokenType.Array)
            { ProtocolError = "SSE choices 不是数组"; _done = true; return false; }
            var choices = (JArray)choicesToken;
            if (choices.Count == 0)
            {
                if (usage != null && usage.Type == JTokenType.Object) return true;
                if (AllowEmptyChoicesHeartbeat && !SawFinishReason) return true;
                ProtocolError = "SSE 空 choices 没有 usage"; _done = true; return false;
            }
            if (choices.Count != 1 || choices[0] == null || choices[0].Type != JTokenType.Object)
            { ProtocolError = "SSE choices 数量不是 1 或首项不是对象"; _done = true; return false; }
            if (SawFinishReason)
            { ProtocolError = "SSE 在 finish_reason 后仍有 choice data"; _done = true; return false; }
            var finish = choices[0]["finish_reason"];
            if (finish != null && finish.Type != JTokenType.Null && !string.IsNullOrEmpty(finish.ToString()))
            {
                if (finish.Type != JTokenType.String)
                { ProtocolError = "SSE finish_reason 不是字符串"; _done = true; return false; }
                string reason = finish.Value<string>();
                if (TreatStringNullFinishAsMissing && string.Equals(reason, "null", StringComparison.OrdinalIgnoreCase))
                    reason = null;
                if (reason == null) { /* provider 的中间占位，不是完成原因 */ }
                else if (reason == "stop" || reason == "tool_calls")
                {
                    if (SawFinishReason && !string.Equals(FinishReason, reason, StringComparison.Ordinal))
                    {
                        ProtocolError = "流式响应出现冲突的 finish_reason:" + FinishReason + "/" + reason;
                        _done = true;
                        return false;
                    }
                    FinishReason = reason;
                    SawFinishReason = true;
                }
                else
                {
                    ProtocolError = "流式响应未完整结束(finish_reason=" + reason + ")";
                    _done = true;
                    return false;
                }
            }
            var delta = choices[0]["delta"];
            if (delta == null || delta.Type == JTokenType.Null)
            {
                if (SawFinishReason) return true;
                ProtocolError = "SSE choice 缺少 delta"; _done = true; return false;
            }
            if (delta.Type != JTokenType.Object)
            { ProtocolError = "SSE delta 不是对象"; _done = true; return false; }

            // Some compatibility gateways put the signature on the assistant delta's
            // top-level provider metadata instead of the first tool call. Capture the
            // opaque chunks here and normalize only after all calls have been validated.
            var signaturePiece = OpenAiCompatibleClient.FindGeminiThoughtSignatureFromAssistantEnvelope(delta);
            if (signaturePiece != null && signaturePiece.Type == JTokenType.String
                && !string.IsNullOrEmpty(signaturePiece.Value<string>()))
            {
                _geminiSignatureCandidate.Append(signaturePiece.Value<string>());
                if (Encoding.UTF8.GetByteCount(_geminiSignatureCandidate.ToString())
                    > LlmProtocolLimits.MaxProviderMetadataBytes)
                { ProtocolError = "Gemini thought_signature 过大"; _done = true; return false; }
            }

            var replayToken = delta["reasoning_content"];
            string replayDisplayPiece = null;
            if (replayToken != null && replayToken.Type != JTokenType.Null)
            {
                _sawReplayReasoning = true;
                string replayPiece = ResponseContentExtractor.ExtractReplayReasoningContent(delta) ?? "";
                if (CumulativeReasoning)
                {
                    if (!TryTakeCumulativeSuffix(replayPiece, ref _lastCumulativeReasoning, out replayPiece)) return false;
                    _replayReasoning.Length = 0;
                    _replayReasoning.Append(_lastCumulativeReasoning);
                }
                else _replayReasoning.Append(replayPiece);
                replayDisplayPiece = replayPiece;
            }
            var rc = ResponseContentExtractor.ExtractReasoningFields(delta);
            if (delta["reasoning_details"] != null && delta["reasoning_details"].Type != JTokenType.Null)
            {
                _assistantProviderFields["reasoning_details"] = delta["reasoning_details"].DeepClone();
                if (Encoding.UTF8.GetByteCount(_assistantProviderFields.ToString(Newtonsoft.Json.Formatting.None))
                    > LlmProtocolLimits.MaxProviderMetadataBytes)
                { ProtocolError = "assistant provider 推理字段过大"; _done = true; return false; }
            }
            if (!string.IsNullOrEmpty(rc))
            {
                // MiniMax M3 reasoning_details/reasoning_content 都可能出现；若已用 reasoning_content，
                // details 仅作为展示的同源累计视图，避免把两份相同思考重复拼接。
                string displayPiece = rc;
                if (CumulativeReasoning && replayToken == null)
                {
                    if (!TryTakeCumulativeSuffix(rc, ref _lastCumulativeReasoning, out displayPiece)) return false;
                }
                else if (CumulativeReasoning && replayToken != null) displayPiece = replayDisplayPiece;
                AppendReasoning(displayPiece);
            }

            var contentToken = delta["content"] ?? delta["parts"];
            if (contentToken != null && contentToken.Type != JTokenType.Null)
            {
                if (contentToken.Type == JTokenType.String) FeedContent(NormalizeContentDelta(contentToken.ToString()));
                else
                {
                    ResponseContentExtractor.ExtractForStreaming(contentToken, out var c, out var structuredThinking);
                    if (!string.IsNullOrEmpty(structuredThinking))
                    {
                        string displayPiece = structuredThinking;
                        if (CumulativeReasoning && replayToken == null
                            && !TryTakeCumulativeSuffix(structuredThinking, ref _lastCumulativeReasoning, out displayPiece)) return false;
                        AppendReasoning(displayPiece);
                    }
                    if (!string.IsNullOrEmpty(c)) FeedContent(NormalizeContentDelta(c));
                }
            }

            var toolCallsToken = delta["tool_calls"];
            if (toolCallsToken != null && toolCallsToken.Type != JTokenType.Null && toolCallsToken.Type != JTokenType.Array)
            { ProtocolError = "流式 tool_calls 不是数组"; _done = true; return false; }
            var tcs = toolCallsToken as JArray;
            if (tcs != null)
            {
                if (tcs.Count > LlmProtocolLimits.MaxToolCalls)
                { ProtocolError = "流式工具调用超过上限"; _done = true; return false; }
                for (int ordinal = 0; ordinal < tcs.Count; ordinal++)
                {
                    var tc = tcs[ordinal];
                    if (tc == null || tc.Type != JTokenType.Object)
                    { ProtocolError = "tool_call 分片不是对象"; _done = true; return false; }
                    var functionToken = tc["function"];
                    if (functionToken != null && functionToken.Type != JTokenType.Null
                        && functionToken.Type != JTokenType.Object)
                    { ProtocolError = "tool_call function 不是对象"; _done = true; return false; }
                    int? forced = null;
                    if (tcs.Count > 1)
                    {
                        var explicitIndex = tc?["index"];
                        if (explicitIndex != null && explicitIndex.Type != JTokenType.Null)
                        {
                            if (explicitIndex.Type != JTokenType.Integer)
                            { ProtocolError = "tool_call index 不是有效整数"; _done = true; return false; }
                            try { forced = explicitIndex.Value<int>(); }
                            catch { ProtocolError = "tool_call index 不是有效整数"; _done = true; return false; }
                            if (forced.Value < 0 || forced.Value >= LlmProtocolLimits.MaxToolCalls)
                            { ProtocolError = "tool_call index 超出允许范围"; return false; }
                            _parallelNoIndexSlots[ordinal] = forced.Value;
                        }
                        else forced = ResolveParallelNoIndex(tc, ordinal);
                    }
                    FeedToolCall(tc, forced);
                    if (!string.IsNullOrEmpty(ProtocolError)) { _done = true; return false; }
                }
            }

            return true;
        }

        // 正文增量使用有限状态机，只处理新到字符；不再每个 delta 对全部历史重新 SplitThink（O(n²)）。
        private void FeedContent(string piece)
        {
            if (string.IsNullOrEmpty(piece) || ProtocolError != null) return;
            foreach (char c in piece)
            {
                InlineCharactersProcessed++;
                _tagPending.Append(c);
                DrainInlinePending(false);
                if (ProtocolError != null) return;
            }
        }

        private string NormalizeContentDelta(string value)
        {
            if (!CumulativeContent || string.IsNullOrEmpty(value)) return value;
            return TryTakeCumulativeSuffix(value, ref _lastCumulativeContent, out var suffix) ? suffix : "";
        }

        private bool TryTakeCumulativeSuffix(string value, ref string previous, out string suffix)
        {
            value = value ?? ""; previous = previous ?? ""; suffix = null;
            if (!value.StartsWith(previous, StringComparison.Ordinal))
            {
                ProtocolError = "provider 累计流字段发生非单调回退";
                _done = true;
                return false;
            }
            suffix = value.Substring(previous.Length);
            previous = value;
            return true;
        }

        private void DrainInlinePending(bool flush)
        {
            while (_tagPending.Length > 0 && ProtocolError == null)
            {
                var tags = _insideInlineThinking ? ThinkClose : ThinkOpen;
                string pending = _tagPending.ToString();
                bool isPrefix = false;
                string exact = null;
                foreach (string tag in tags)
                {
                    if (pending.Length <= tag.Length
                        && string.Compare(pending, 0, tag, 0, pending.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        isPrefix = true;
                        if (pending.Length == tag.Length) exact = tag;
                    }
                }
                if (exact != null)
                {
                    _tagPending.Length = 0;
                    _insideInlineThinking = !_insideInlineThinking;
                    continue;
                }
                if (isPrefix && !flush) return;
                char first = _tagPending[0];
                _tagPending.Remove(0, 1);
                AppendInlineText(first.ToString());
            }
        }

        private void AppendInlineText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (_insideInlineThinking)
            {
                if (_inlineThinking.Length + _reasoning.Length + text.Length > LlmProtocolLimits.MaxReasoningChars)
                { ProtocolError = "流式思考正文超过上限"; _done = true; return; }
                _inlineThinking.Append(text);
                EmitThinking(text);
            }
            else
            {
                if (_visibleContent.Length + text.Length > LlmProtocolLimits.MaxVisibleChars)
                { ProtocolError = "流式可见正文超过上限"; _done = true; return; }
                _visibleContent.Append(text);
                EmitContent(text);
            }
        }

        private void AppendReasoning(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (_reasoning.Length + _inlineThinking.Length + text.Length > LlmProtocolLimits.MaxReasoningChars)
            { ProtocolError = "流式思考正文超过上限"; _done = true; return; }
            _reasoning.Append(text);
            EmitThinking(text);
        }

        private void EmitContent(string text)
        {
            if (_contentCallbackRedactor == null)
                _contentCallbackRedactor = new IncrementalSecretRedactor(CallbackExactSecret);
            _contentCallbackRedactor.Push(text, OnContent);
        }

        private void EmitThinking(string text)
        {
            if (_thinkingCallbackRedactor == null)
                _thinkingCallbackRedactor = new IncrementalSecretRedactor(CallbackExactSecret);
            _thinkingCallbackRedactor.Push(text, OnThinking);
        }

        private void FlushCallbackRedactors()
        {
            if (_callbackRedactorsFlushed) return;
            _callbackRedactorsFlushed = true;
            _contentCallbackRedactor?.Flush(OnContent);
            _thinkingCallbackRedactor?.Flush(OnThinking);
        }

        private int _lastIdx = -1;   // 上一次 tool_call 分片的 index;某些端流式分片不带 index → 沿用它,保证同一调用的 name/arguments 拼到同一 builder(免参数落到无名 builder 被丢弃→空参 {} 调用)

        private int ResolveParallelNoIndex(JToken tc, int ordinal)
        {
            int matched = FindExistingTool(tc);
            if (matched >= 0) { _parallelNoIndexSlots[ordinal] = matched; return matched; }
            if (_parallelNoIndexSlots.TryGetValue(ordinal, out int mapped)) return mapped;
            // Gemini 可能先单独送 ordinal 0 的 signature，下一 delta 才一次给出多个 call。
            // 复用那个尚无 id/name 的 builder，避免把签名留在最终会被丢弃的孤儿上。
            int orphan = -1;
            foreach (var pair in _tools)
                if (string.IsNullOrEmpty(pair.Value.Id) && string.IsNullOrEmpty(pair.Value.Name)
                    && !_parallelNoIndexSlots.ContainsValue(pair.Key))
                {
                    if (orphan >= 0)
                    { ProtocolError = "多个无身份 tool_call 无法安全归并"; return -1; }
                    orphan = pair.Key;
                }
            if (orphan >= 0) { _parallelNoIndexSlots[ordinal] = orphan; return orphan; }
            int created = NextToolIndex();
            _parallelNoIndexSlots[ordinal] = created;
            return created;
        }

        private int FindExistingTool(JToken tc)
        {
            string id = tc?["id"]?.ToString();
            string name = tc?["function"]?["name"]?.ToString();
            if (!string.IsNullOrEmpty(id))
            {
                foreach (var pair in _tools) if (pair.Value.Id == id) return pair.Key;
                return -1; // 新 id 必须新建；绝不能再按同名 function 合并并行调用。
            }
            if (!string.IsNullOrEmpty(name))
            {
                int found = -1;
                foreach (var pair in _tools)
                    if (pair.Value.Name == name) { if (found >= 0) return -1; found = pair.Key; }
                if (found >= 0) return found;
            }
            return -1;
        }

        private int NextToolIndex()
        {
            int idx = 0;
            while (_tools.ContainsKey(idx)) idx++;
            return idx;
        }

        private int ResolveSingleNoIndex(JToken tc)
        {
            int matched = FindExistingTool(tc);
            if (matched >= 0) return matched;
            string id = tc?["id"]?.ToString();
            string name = tc?["function"]?["name"]?.ToString();
            // 已经出现多个无-index并行调用后，孤立的 args-only 分片无法判断属于哪一个。
            // 猜到 _lastIdx 可能把两个副作用参数串错，必须让整轮失败并由上层重取。
            if (string.IsNullOrEmpty(id) && string.IsNullOrEmpty(name) && _tools.Count > 1)
            {
                ProtocolError = "无 index 的并行 tool_call 后出现无法归属的参数分片";
                return -1;
            }
            if (_lastIdx >= 0 && _tools.TryGetValue(_lastIdx, out var last))
            {
                bool idConflict = !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(last.Id) && id != last.Id;
                bool nameConflict = !string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(last.Name) && name != last.Name;
                if (!idConflict && !nameConflict) return _lastIdx;
            }
            if (!string.IsNullOrEmpty(id) || !string.IsNullOrEmpty(name)) return NextToolIndex();
            return _lastIdx < 0 ? 0 : _lastIdx;
        }

        private void FeedToolCall(JToken tc, int? forcedIndex)
        {
            if (tc == null || tc.Type != JTokenType.Object) { ProtocolError = "tool_call 分片不是对象"; return; }
            // 缺 index 才回退到上一个(首次=0);index 存在时照常按序——绝不把真正并行的多个 tool_call 误拼成一个。
            var idxTok = tc["index"];
            int idx;
            if (forcedIndex == null && idxTok != null && idxTok.Type != JTokenType.Null && idxTok.Type != JTokenType.Integer)
            { ProtocolError = "tool_call index 不是有效整数"; return; }
            try { idx = forcedIndex ?? ((idxTok != null && idxTok.Type != JTokenType.Null) ? idxTok.Value<int>() : ResolveSingleNoIndex(tc)); }
            catch { ProtocolError = "tool_call index 不是有效整数"; return; }
            if ((idx < 0 || idx >= LlmProtocolLimits.MaxToolCalls) && string.IsNullOrEmpty(ProtocolError)) ProtocolError = "tool_call index 超出允许范围";
            if (idx < 0 || idx >= LlmProtocolLimits.MaxToolCalls) return;
            _lastIdx = idx;
            ToolCallBuilder b;
            if (!_tools.TryGetValue(idx, out b))
            {
                if (_tools.Count >= LlmProtocolLimits.MaxToolCalls) { ProtocolError = "流式工具调用超过上限"; return; }
                b = new ToolCallBuilder(); _tools[idx] = b;
            }
            var idToken = tc["id"];
            if (idToken != null && idToken.Type != JTokenType.Null && idToken.Type != JTokenType.String)
            { ProtocolError = "tool_call id 不是字符串"; return; }
            string id = idToken?.Type == JTokenType.String ? idToken.Value<string>() : null;
            if (!string.IsNullOrEmpty(id))
            {
                if (id.Length > LlmProtocolLimits.MaxToolIdChars) { ProtocolError = "tool_call id 过长"; return; }
                if (!string.IsNullOrEmpty(b.Id) && !string.Equals(b.Id, id, StringComparison.Ordinal))
                { ProtocolError = "同一 tool_call index 的 id 冲突"; return; }
                b.Id = id;
            }
            var fn = tc["function"];
            if (fn != null && fn.Type != JTokenType.Null && fn.Type != JTokenType.Object)
            { ProtocolError = "tool_call function 不是对象"; return; }
            if (fn != null && fn.Type != JTokenType.Null)
            {
                var nameToken = fn["name"];
                if (nameToken != null && nameToken.Type != JTokenType.Null && nameToken.Type != JTokenType.String)
                { ProtocolError = "tool_call 函数名不是字符串"; return; }
                string name = nameToken?.Type == JTokenType.String
                    ? nameToken.Value<string>() : null;
                if (!string.IsNullOrEmpty(name))
                {
                    if (name.Length > LlmProtocolLimits.MaxToolNameChars) { ProtocolError = "tool_call 函数名过长"; return; }
                    if (!string.IsNullOrEmpty(b.Name) && !string.Equals(b.Name, name, StringComparison.Ordinal))
                    { ProtocolError = "同一 tool_call index 的函数名冲突"; return; }
                    b.Name = name;
                    // Tool-name callbacks are an untrusted streaming side channel just like
                    // content/reasoning callbacks.  Do not expose an unknown or credential-
                    // bearing provider value to UI/log code before ToResult performs the full
                    // schema validation.  The complete call is still retained below so the
                    // normal protocol validator can return a deterministic failure.
                    if (_named.Add(idx) && ToolNameIsSafeForCallback(name))
                    { try { OnToolName?.Invoke(name); } catch { } }
                }
                var argsToken = fn["arguments"];
                if (argsToken != null && argsToken.Type != JTokenType.Null && argsToken.Type != JTokenType.String)
                { ProtocolError = "tool_call arguments 不是 JSON 字符串"; return; }
                string args = argsToken?.Type == JTokenType.String ? argsToken.Value<string>() : null;
                if (!string.IsNullOrEmpty(args))
                {
                    int bytes = Encoding.UTF8.GetByteCount(args);
                    if (b.ArgsBytes + bytes > LlmProtocolLimits.MaxToolArgumentsBytes
                        || _totalToolArgumentBytes + bytes > LlmProtocolLimits.MaxTotalToolArgumentsBytes)
                    { ProtocolError = "流式工具参数超过字节上限"; return; }
                    b.Args.Append(args); b.ArgsBytes += bytes; _totalToolArgumentBytes += bytes;
                }
            }
            // Gemini 3 的 extra_content.google.thought_signature 必须随下一轮 assistant.tool_calls
            // 原样回传。保留所有非标准 provider 字段，兼容未来扩展；index 仅为流式定位，不回灌。
            var tcObj = tc as JObject;
            if (tcObj != null)
                foreach (var p in tcObj.Properties())
                    if (p.Name != "index" && p.Name != "id" && p.Name != "type" && p.Name != "function")
                    {
                        _providerMetadataBytes += Encoding.UTF8.GetByteCount(p.Name)
                            + Encoding.UTF8.GetByteCount(p.Value?.ToString(Newtonsoft.Json.Formatting.None) ?? "null");
                        if (_providerMetadataBytes > LlmProtocolLimits.MaxProviderMetadataBytes)
                        { ProtocolError = "provider tool_call 扩展字段过大"; return; }
                        MergeProviderField(b.ExtraFields, p.Name, p.Value);
                    }
        }

        private bool ToolNameIsSafeForCallback(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!string.Equals(name, SecretRedactor.Redact(name, CallbackExactSecret),
                    StringComparison.Ordinal)) return false;
            if (!EnforceAllowedTools && AllowedTools == null) return true;
            if (AllowedTools == null) return false;
            foreach (ToolDef tool in AllowedTools)
                if (tool != null && string.Equals(tool.Name, name, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static void MergeProviderField(JObject target, string name, JToken incoming)
        {
            if (target == null || string.IsNullOrEmpty(name) || incoming == null) return;
            var existing = target[name];
            if (existing == null || existing.Type == JTokenType.Null)
            {
                target[name] = incoming.DeepClone();
                return;
            }
            if (existing.Type == JTokenType.Object && incoming.Type == JTokenType.Object)
            {
                var dst = (JObject)existing;
                foreach (var p in ((JObject)incoming).Properties()) MergeProviderField(dst, p.Name, p.Value);
                return;
            }
            if (existing.Type == JTokenType.String && incoming.Type == JTokenType.String)
            {
                string nextValue = incoming.Value<string>() ?? "";
                // OpenAI-compatible delta 字段语义是逐片追加；thought_signature 是不透明字节串，
                // 不做 prefix/suffix 去重猜测（A+A 必须仍为 AA）。其他扩展字符串按最新值覆盖。
                if (name == "thought_signature") target[name] = (existing.Value<string>() ?? "") + nextValue;
                else target[name] = nextValue;
                return;
            }
            target[name] = incoming.DeepClone();
        }

        /// <summary>收尾:补发被扣住的尾部,产出最终结果。多次调用安全(幂等)。</summary>
        public LlmToolResult ToResult()
        {
            FlushTail();
            FlushCallbackRedactors();
            if (!string.IsNullOrEmpty(ProtocolError)) return new LlmToolResult { Ok = false, Error = ProtocolError };
            string visible = _visibleContent.ToString();
            string reasoning = (_reasoning.ToString() + _inlineThinking.ToString()).Trim();

            var calls = new List<LlmToolCall>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var idxs = new List<int>(_tools.Keys); idxs.Sort();
            foreach (var i in idxs)
            {
                var b = _tools[i];
                if (string.IsNullOrWhiteSpace(b.Name))
                    return new LlmToolResult { Ok = false, Error = "流式 tool_call 缺少函数名" };
                if (string.IsNullOrWhiteSpace(b.Id))
                    return new LlmToolResult { Ok = false, Error = "流式 tool_call 缺少原始 id" };
                string id = b.Id;
                if (!ids.Add(id))
                    return new LlmToolResult { Ok = false, Error = "流式 tool_call id 重复:" + id };
                if (b.Args.Length == 0)
                    return new LlmToolResult { Ok = false, Error = "流式 tool_call 缺少 arguments" };
                string arguments = b.Args.ToString();
                string canonicalName = ToolArgumentsValidator.NormalizeKnownReadOnlyAlias(
                    b.Name, AllowedTools, arguments);
                if (!LlmJsonProtocol.TryParseObject(arguments, LlmProtocolLimits.MaxToolArgumentsBytes, out _, out var argumentError))
                    return new LlmToolResult { Ok = false, Error = "流式工具参数不完整:" + argumentError };
                if ((EnforceAllowedTools || AllowedTools != null)
                    && !ToolArgumentsValidator.ValidateCall(AllowedTools, canonicalName, arguments, out var validationError))
                    return new LlmToolResult { Ok = false, Error = "流式工具参数未通过 schema:" + validationError };
                if (b.ExtraFields.Count > 0
                    && Encoding.UTF8.GetByteCount(b.ExtraFields.ToString(Newtonsoft.Json.Formatting.None)) > LlmProtocolLimits.MaxProviderMetadataBytes)
                    return new LlmToolResult { Ok = false, Error = "provider tool_call 扩展字段过大" };
                calls.Add(new LlmToolCall
                {
                    Id = id,
                    Name = canonicalName,
                    ArgumentsJson = arguments,
                    ExtraFields = b.ExtraFields.Count > 0 ? (JObject)b.ExtraFields.DeepClone() : null,
                });
            }

            if (RequireFinishReason)
            {
                if (!SawFinishReason)
                    return new LlmToolResult { Ok = false, Error = "流式响应缺少 finish_reason" };
                if (AllowStopFinishWithCompleteToolCalls && calls.Count > 0 && FinishReason == "stop")
                    FinishReason = "tool_calls";
                if (calls.Count > 0 && FinishReason != "tool_calls")
                    return new LlmToolResult { Ok = false, Error = "finish_reason=" + FinishReason + " 却携带工具调用" };
                if (calls.Count == 0 && FinishReason == "tool_calls")
                    return new LlmToolResult { Ok = false, Error = "finish_reason=tool_calls 但响应没有完整工具调用" };
            }
            if (RequireDoneSentinel && !SawDone)
                return new LlmToolResult { Ok = false, Error = "流式响应缺少 [DONE] 哨兵" };

            if (calls.Count == 0 && string.Equals(FinishReason, "stop", StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(visible))
                return new LlmToolResult { Ok = false, Error = "流式工具轮最终正文为空" };

            string expected = (ExpectedToolChoice ?? "auto").Trim().ToLowerInvariant();
            if (expected == "none" && calls.Count != 0)
                return new LlmToolResult { Ok = false, Error = "tool_choice=none 但模型仍返回工具调用" };
            if (RequireGeminiThoughtSignature && calls.Count > 0)
            {
                JToken candidate = _geminiSignatureCandidate.Length > 0
                    ? (JToken)new JValue(_geminiSignatureCandidate.ToString())
                    : null;
                if (!OpenAiCompatibleClient.EnsureGeminiThoughtSignature(calls, candidate,
                    AllowGeminiSignatureCompatibilityFallback, out _))
                    return new LlmToolResult { Ok = false, Error = "Gemini 3 首个 tool_call 缺少 thought_signature" };
            }

            if (calls.Count > 0 && _assistantProviderFields.Count > 0)
            {
                if (Encoding.UTF8.GetByteCount(_assistantProviderFields.ToString(Newtonsoft.Json.Formatting.None))
                    > LlmProtocolLimits.MaxProviderMetadataBytes)
                    return new LlmToolResult { Ok = false, Error = "assistant provider 推理字段过大" };
                calls[0].AssistantExtraFields = (JObject)_assistantProviderFields.DeepClone();
            }

            return new LlmToolResult
            {
                Ok = true,
                Content = visible.Trim(),
                ToolCalls = calls.Count > 0 ? calls : null,
                Reasoning = string.IsNullOrEmpty(reasoning) ? null : reasoning,
                ReplayReasoningContent = _sawReplayReasoning ? _replayReasoning.ToString() : null,
                PromptTokens = PromptTokens,
                CompletionTokens = CompletionTokens,
                CachedTokens = CachedTokens,
                CacheMissTokens = CacheMissTokens,
                CacheWriteTokens = CacheWriteTokens,
                ReasoningTokens = ReasoningTokens,
                TotalTokens = TotalTokens,
            };
        }

        // 把先前为防半截标签而扣住的尾部全部补发
        private void FlushTail()
        {
            DrainInlinePending(true);
        }

        // 各模型/服务端的内联思考标签变体:统一先归一化成 <think>…</think> 再切分。
        // ◁think▷ = Kimi/Moonshot 原始特殊符号(未被服务端解析进 reasoning_content 时会随正文吐出)。
        private static readonly string[] ThinkOpen = { "<think>", "<thinking>", "<thought>", "<analysis>", "◁think▷" };
        private static readonly string[] ThinkClose = { "</think>", "</thinking>", "</thought>", "</analysis>", "◁/think▷" };

        // 把原始串切成"可见正文"与"思考":先归一化标签变体,再移除成对 <think>…</think>(内部入思考);末尾未闭合的整段算思考。
        // 可见正文是单调前缀增长的(只追加内容、不回退),故按已发长度做增量发送安全。
        public static void SplitThink(string raw, out string visible, out string thinking)
        {
            raw = CanonTags(raw);
            var vis = new StringBuilder();
            var think = new StringBuilder();
            int i = 0;
            while (i < raw.Length)
            {
                int open = IndexOfCi(raw, "<think>", i);
                if (open < 0) { vis.Append(raw, i, raw.Length - i); break; }
                vis.Append(raw, i, open - i);
                int innerStart = open + 7;
                int close = IndexOfCi(raw, "</think>", innerStart);
                if (close < 0) { think.Append(raw, innerStart, raw.Length - innerStart); break; }
                think.Append(raw, innerStart, close - innerStart);
                i = close + 8;
            }
            visible = vis.ToString();
            thinking = think.ToString();
        }

        // s 末尾若是任一 tag 变体的真前缀(如 "<thi" 之于 "<think>"、"◁thi" 之于 "◁think▷"),扣住最长的那截,免把半截标签当正文吐出。
        private static int SafeEmitLen(string s, string[] tags)
        {
            int hold = 0;
            foreach (var tag in tags)
            {
                int max = Math.Min(tag.Length - 1, s.Length);
                for (int k = max; k >= 1; k--)
                    if (string.Compare(s, s.Length - k, tag, 0, k, StringComparison.OrdinalIgnoreCase) == 0) { if (k > hold) hold = k; break; }
            }
            return s.Length - hold;
        }

        // 归一化思考标签变体为 <think>…</think>(只换完整成对标签;末尾半截标签留待 SafeEmitLen 扣住,下个 chunk 补齐后再换)。
        private static string CanonTags(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            if (raw.IndexOf('◁') >= 0) raw = raw.Replace("◁/think▷", "</think>").Replace("◁think▷", "<think>");
            if (raw.IndexOf("hinking", StringComparison.OrdinalIgnoreCase) >= 0)
            { raw = ReplaceCi(raw, "</thinking>", "</think>"); raw = ReplaceCi(raw, "<thinking>", "<think>"); }
            if (raw.IndexOf("thought", StringComparison.OrdinalIgnoreCase) >= 0)
            { raw = ReplaceCi(raw, "</thought>", "</think>"); raw = ReplaceCi(raw, "<thought>", "<think>"); }
            if (raw.IndexOf("analysis", StringComparison.OrdinalIgnoreCase) >= 0)
            { raw = ReplaceCi(raw, "</analysis>", "</think>"); raw = ReplaceCi(raw, "<analysis>", "<think>"); }
            return raw;
        }

        private static string ReplaceCi(string s, string oldV, string newV)
        {
            int idx = s.IndexOf(oldV, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return s;
            var sb = new StringBuilder(s.Length); int prev = 0;
            while (idx >= 0) { sb.Append(s, prev, idx - prev).Append(newV); prev = idx + oldV.Length; idx = s.IndexOf(oldV, prev, StringComparison.OrdinalIgnoreCase); }
            sb.Append(s, prev, s.Length - prev); return sb.ToString();
        }

        private static int IndexOfCi(string s, string sub, int start)
            => s.IndexOf(sub, start, StringComparison.OrdinalIgnoreCase);
    }
}
