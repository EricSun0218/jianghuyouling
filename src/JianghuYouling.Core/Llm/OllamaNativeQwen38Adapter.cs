using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Llm
{
    /// <summary>
    /// Adapts the OpenAI-shaped request/response used by the Mod to Ollama's native
    /// /api/chat protocol for local Qwen3.8.  Keeping the adapter at the wire boundary
    /// lets the existing schema validation, tool authorization and agent loop remain
    /// authoritative.
    /// </summary>
    internal static class OllamaNativeQwen38Adapter
    {
        internal static Uri NativeChatEndpoint(Uri configuredEndpoint)
        {
            if (configuredEndpoint == null) throw new ArgumentNullException(nameof(configuredEndpoint));
            var builder = new UriBuilder(configuredEndpoint)
            {
                Path = "/api/chat",
                Query = string.Empty,
                Fragment = string.Empty,
            };
            return builder.Uri;
        }

        internal static bool TryBuildRequest(JObject openAiBody, out JObject nativeBody,
            out string error)
        {
            nativeBody = null;
            error = null;
            if (openAiBody == null)
            {
                error = "本地 Qwen3.8 请求体为空";
                return false;
            }

            var model = openAiBody["model"];
            var messages = openAiBody["messages"] as JArray;
            var stream = openAiBody["stream"];
            if (model == null || model.Type != JTokenType.String
                || string.IsNullOrWhiteSpace(model.Value<string>()))
            {
                error = "本地 Qwen3.8 请求缺少模型名";
                return false;
            }
            if (messages == null)
            {
                error = "本地 Qwen3.8 请求缺少消息列表";
                return false;
            }
            if (stream == null || stream.Type != JTokenType.Boolean)
            {
                error = "本地 Qwen3.8 请求缺少流式标记";
                return false;
            }

            var toolNames = CollectToolNames(messages);
            var nativeMessages = new JArray();
            var systemContents = new List<string>();
            foreach (JToken token in messages)
            {
                if (!(token is JObject message)
                    || message["role"] == null || message["role"].Type != JTokenType.String
                    || message["content"] == null || message["content"].Type != JTokenType.String)
                {
                    error = "本地 Qwen3.8 消息格式无效";
                    return false;
                }

                string role = message["role"].Value<string>();
                var converted = new JObject
                {
                    ["role"] = role,
                    ["content"] = message["content"].Value<string>() ?? string.Empty,
                };
                if (message["reasoning_content"] != null)
                {
                    if (message["reasoning_content"].Type != JTokenType.String)
                    {
                        error = "本地 Qwen3.8 历史思考格式无效";
                        return false;
                    }
                    converted["thinking"] = message["reasoning_content"].Value<string>() ?? string.Empty;
                }
                if (message["tool_calls"] != null)
                {
                    if (!TryConvertRequestToolCalls(message["tool_calls"], out var calls,
                        out error)) return false;
                    converted["tool_calls"] = calls;
                }
                if (string.Equals(role, "tool", StringComparison.Ordinal))
                {
                    var id = message["tool_call_id"];
                    if (id == null || id.Type != JTokenType.String
                        || string.IsNullOrWhiteSpace(id.Value<string>()))
                    {
                        error = "本地 Qwen3.8 工具结果缺少调用标识";
                        return false;
                    }
                    string callId = id.Value<string>();
                    converted["tool_call_id"] = callId;
                    if (!toolNames.TryGetValue(callId, out string toolName)
                        || string.IsNullOrWhiteSpace(toolName))
                    {
                        error = "本地 Qwen3.8 工具结果找不到对应函数名";
                        return false;
                    }
                    converted["tool_name"] = toolName;
                }
                if (string.Equals(role, "system", StringComparison.Ordinal))
                {
                    // Qwen3.8's GGUF chat template accepts exactly one leading system message.
                    // The Mod intentionally composes independent authoritative system blocks;
                    // join those blocks at the native wire boundary without changing their order.
                    systemContents.Add(converted["content"].Value<string>() ?? string.Empty);
                    continue;
                }
                nativeMessages.Add(converted);
            }

            if (systemContents.Count > 0)
            {
                nativeMessages.Insert(0, new JObject
                {
                    ["role"] = "system",
                    ["content"] = string.Join("\n\n", systemContents),
                });
            }

            nativeBody = new JObject
            {
                ["model"] = model.Value<string>(),
                ["messages"] = nativeMessages,
                ["stream"] = stream.Value<bool>(),
            };
            if (openAiBody["tools"] != null)
            {
                if (openAiBody["tools"].Type != JTokenType.Array)
                {
                    error = "本地 Qwen3.8 工具定义不是数组";
                    return false;
                }
                nativeBody["tools"] = openAiBody["tools"].DeepClone();
            }

            var responseFormat = openAiBody["response_format"] as JObject;
            if (responseFormat != null)
            {
                if (!string.Equals(responseFormat["type"]?.Value<string>(), "json_object",
                    StringComparison.Ordinal))
                {
                    error = "本地 Qwen3.8 只支持 json_object 结构化输出";
                    return false;
                }
                nativeBody["format"] = "json";
            }

            string effort = openAiBody["reasoning_effort"]?.Type == JTokenType.String
                ? openAiBody["reasoning_effort"].Value<string>() : null;
            if (string.Equals(effort, "none", StringComparison.OrdinalIgnoreCase))
                nativeBody["think"] = false;
            else if (string.Equals(effort, "low", StringComparison.OrdinalIgnoreCase))
                nativeBody["think"] = "low";

            var options = new JObject();
            CopyInteger(openAiBody, options, "max_tokens", "num_predict");
            CopyInteger(openAiBody, options, "max_completion_tokens", "num_predict");
            CopyNumber(openAiBody, options, "temperature", "temperature");
            if (options.Count > 0) nativeBody["options"] = options;
            return true;
        }

        internal static bool TryNormalizeResponse(string nativeJson, out string openAiJson,
            out string error)
        {
            openAiJson = null;
            error = null;
            if (!LlmJsonProtocol.TryParseObject(nativeJson, LlmProtocolLimits.MaxResponseBytes,
                out var response, out var parseError))
            {
                error = "本地 Qwen3.8 返回无效 JSON:" + parseError;
                return false;
            }
            if (response["error"] != null)
            {
                error = "本地 Qwen3.8 返回错误:" + SafeError(response["error"]);
                return false;
            }
            if (response["done"] == null || response["done"].Type != JTokenType.Boolean
                || !response["done"].Value<bool>())
            {
                error = "本地 Qwen3.8 非流式响应未完整结束";
                return false;
            }
            if (!(response["message"] is JObject message))
            {
                error = "本地 Qwen3.8 响应缺少 message";
                return false;
            }
            if (!TryConvertResponseMessage(message, ResponseSeed(response), out var converted,
                out int callCount, out error)) return false;

            string finishReason = callCount > 0 ? "tool_calls"
                : NormalizeFinishReason(response["done_reason"]);
            var result = new JObject
            {
                ["choices"] = new JArray(new JObject
                {
                    ["index"] = 0,
                    ["message"] = converted,
                    ["finish_reason"] = finishReason,
                }),
                ["usage"] = BuildUsage(response),
            };
            openAiJson = result.ToString(Formatting.None);
            return true;
        }

        private static Dictionary<string, string> CollectToolNames(JArray messages)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JToken token in messages)
            {
                var calls = (token as JObject)?["tool_calls"] as JArray;
                if (calls == null) continue;
                foreach (JToken callToken in calls)
                {
                    var call = callToken as JObject;
                    string id = call?["id"]?.Type == JTokenType.String
                        ? call["id"].Value<string>() : null;
                    string name = (call?["function"] as JObject)?["name"]?.Type == JTokenType.String
                        ? call["function"]["name"].Value<string>() : null;
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                        result[id] = name;
                }
            }
            return result;
        }

        private static bool TryConvertRequestToolCalls(JToken token, out JArray converted,
            out string error)
        {
            converted = null;
            error = null;
            if (!(token is JArray calls) || calls.Count > LlmProtocolLimits.MaxToolCalls)
            {
                error = "本地 Qwen3.8 工具调用列表无效";
                return false;
            }
            converted = new JArray();
            foreach (JToken callToken in calls)
            {
                var call = callToken as JObject;
                var function = call?["function"] as JObject;
                string id = call?["id"]?.Type == JTokenType.String
                    ? call["id"].Value<string>() : null;
                string name = function?["name"]?.Type == JTokenType.String
                    ? function["name"].Value<string>() : null;
                string arguments = function?["arguments"]?.Type == JTokenType.String
                    ? function["arguments"].Value<string>() : null;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)
                    || !LlmJsonProtocol.TryParseObject(arguments,
                        LlmProtocolLimits.MaxToolArgumentsBytes, out var argumentsObject, out _))
                {
                    error = "本地 Qwen3.8 历史工具调用不完整";
                    return false;
                }
                converted.Add(new JObject
                {
                    ["id"] = id,
                    ["function"] = new JObject
                    {
                        ["name"] = name,
                        ["arguments"] = argumentsObject,
                    },
                });
            }
            return true;
        }

        internal static bool TryConvertResponseMessage(JObject message, string responseSeed,
            out JObject converted, out int callCount, out string error)
        {
            converted = null;
            callCount = 0;
            error = null;
            if (message == null)
            {
                error = "本地 Qwen3.8 响应消息为空";
                return false;
            }
            var content = message["content"];
            var thinking = message["thinking"];
            if (content != null && content.Type != JTokenType.Null
                && content.Type != JTokenType.String)
            {
                error = "本地 Qwen3.8 正文不是字符串";
                return false;
            }
            if (thinking != null && thinking.Type != JTokenType.Null
                && thinking.Type != JTokenType.String)
            {
                error = "本地 Qwen3.8 思考不是字符串";
                return false;
            }
            converted = new JObject
            {
                ["role"] = "assistant",
                ["content"] = content?.Type == JTokenType.String
                    ? content.Value<string>() ?? string.Empty : string.Empty,
            };
            if (thinking != null && thinking.Type == JTokenType.String)
                converted["reasoning_content"] = thinking.Value<string>() ?? string.Empty;

            var calls = message["tool_calls"];
            if (calls != null && calls.Type != JTokenType.Null)
            {
                if (!TryConvertResponseToolCalls(calls, responseSeed, out var normalized,
                    out error)) return false;
                callCount = normalized.Count;
                if (callCount > 0) converted["tool_calls"] = normalized;
            }
            return true;
        }

        private static bool TryConvertResponseToolCalls(JToken token, string responseSeed,
            out JArray converted, out string error)
        {
            converted = null;
            error = null;
            if (!(token is JArray calls) || calls.Count > LlmProtocolLimits.MaxToolCalls)
            {
                error = "本地 Qwen3.8 工具调用列表无效";
                return false;
            }
            converted = new JArray();
            int totalBytes = 0;
            for (int i = 0; i < calls.Count; i++)
            {
                var call = calls[i] as JObject;
                var function = call?["function"] as JObject;
                string name = function?["name"]?.Type == JTokenType.String
                    ? function["name"].Value<string>() : null;
                JToken argumentsToken = function?["arguments"];
                if (string.IsNullOrWhiteSpace(name) || name.Length > LlmProtocolLimits.MaxToolNameChars
                    || argumentsToken == null)
                {
                    error = "本地 Qwen3.8 工具调用缺少函数名或参数";
                    return false;
                }
                string arguments;
                if (argumentsToken.Type == JTokenType.Object)
                    arguments = argumentsToken.ToString(Formatting.None);
                else if (argumentsToken.Type == JTokenType.String
                    && LlmJsonProtocol.TryParseObject(argumentsToken.Value<string>(),
                        LlmProtocolLimits.MaxToolArgumentsBytes, out var parsed, out _))
                    arguments = parsed.ToString(Formatting.None);
                else
                {
                    error = "本地 Qwen3.8 工具参数不是 JSON 对象";
                    return false;
                }
                int bytes = Encoding.UTF8.GetByteCount(arguments);
                totalBytes += bytes;
                if (bytes > LlmProtocolLimits.MaxToolArgumentsBytes
                    || totalBytes > LlmProtocolLimits.MaxTotalToolArgumentsBytes)
                {
                    error = "本地 Qwen3.8 工具参数超过上限";
                    return false;
                }
                string id = call?["id"]?.Type == JTokenType.String
                    ? call["id"].Value<string>() : null;
                if (string.IsNullOrWhiteSpace(id))
                    id = SyntheticToolCallId(responseSeed, i, name, arguments);
                if (id.Length > LlmProtocolLimits.MaxToolIdChars)
                {
                    error = "本地 Qwen3.8 工具调用标识过长";
                    return false;
                }
                converted.Add(new JObject
                {
                    ["index"] = i,
                    ["id"] = id,
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = name,
                        ["arguments"] = arguments,
                    },
                });
            }
            return true;
        }

        internal static JObject BuildUsage(JObject response)
        {
            int prompt = NonNegativeInt(response?["prompt_eval_count"]);
            int completion = NonNegativeInt(response?["eval_count"]);
            long total = (long)prompt + completion;
            return new JObject
            {
                ["prompt_tokens"] = prompt,
                ["completion_tokens"] = completion,
                ["total_tokens"] = total > int.MaxValue ? int.MaxValue : (int)total,
            };
        }

        internal static string ResponseSeed(JObject response)
            => (response?["model"]?.ToString() ?? string.Empty) + "|"
                + (response?["created_at"]?.ToString() ?? string.Empty);

        internal static string NormalizeFinishReason(JToken token)
        {
            string value = token?.Type == JTokenType.String ? token.Value<string>() : null;
            return string.IsNullOrWhiteSpace(value) ? "stop" : value.Trim().ToLowerInvariant();
        }

        private static string SyntheticToolCallId(string responseSeed, int index,
            string name, string arguments)
        {
            byte[] bytes = Encoding.UTF8.GetBytes((responseSeed ?? string.Empty) + "|"
                + index + "|" + name + "|" + arguments);
            byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(bytes);
            var text = new StringBuilder(24);
            for (int i = 0; i < 12; i++) text.Append(hash[i].ToString("x2"));
            return "call_ollama_" + text;
        }

        private static int NonNegativeInt(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return 0;
            try
            {
                long value = token.Value<long>();
                if (value <= 0) return 0;
                return value > int.MaxValue ? int.MaxValue : (int)value;
            }
            catch { return 0; }
        }

        private static void CopyInteger(JObject source, JObject destination,
            string sourceName, string destinationName)
        {
            JToken token = source[sourceName];
            if (token == null || token.Type != JTokenType.Integer) return;
            long value;
            try { value = token.Value<long>(); }
            catch { return; }
            if (value >= 0) destination[destinationName] = value;
        }

        private static void CopyNumber(JObject source, JObject destination,
            string sourceName, string destinationName)
        {
            JToken token = source[sourceName];
            if (token == null || (token.Type != JTokenType.Integer
                && token.Type != JTokenType.Float)) return;
            destination[destinationName] = token.DeepClone();
        }

        private static string SafeError(JToken token)
        {
            string value = token?.Type == JTokenType.String
                ? token.Value<string>() : token?["message"]?.ToString();
            value = SecretRedactor.Redact(value ?? "未知错误").Replace('\r', ' ').Replace('\n', ' ');
            return value.Length > 400 ? value.Substring(0, 400) : value;
        }
    }

    /// <summary>Stateful NDJSON-to-OpenAI stream normalization for one native chat request.</summary>
    internal sealed class OllamaNativeQwen38StreamAdapter
    {
        private JArray _pendingToolCalls;
        private string _responseSeed = string.Empty;
        private bool _done;

        internal bool TryConvertLine(string line, out string openAiPayload,
            out bool emitDoneSentinel, out string error)
        {
            openAiPayload = null;
            emitDoneSentinel = false;
            error = null;
            if (_done)
            {
                if (!string.IsNullOrWhiteSpace(line)) error = "本地 Qwen3.8 在完成后仍返回数据";
                return error == null;
            }
            if (string.IsNullOrWhiteSpace(line)) return true;
            if (!LlmJsonProtocol.TryParseObject(line, LlmProtocolLimits.MaxSseEventChars * 4,
                out var response, out var parseError))
            {
                error = "本地 Qwen3.8 流式分片不是完整 JSON:" + parseError;
                return false;
            }
            if (response["error"] != null)
            {
                error = "本地 Qwen3.8 流式返回错误";
                return false;
            }
            if (response["done"] == null || response["done"].Type != JTokenType.Boolean)
            {
                error = "本地 Qwen3.8 流式分片缺少 done";
                return false;
            }
            if (!(response["message"] is JObject message))
            {
                error = "本地 Qwen3.8 流式分片缺少 message";
                return false;
            }
            string seed = OllamaNativeQwen38Adapter.ResponseSeed(response);
            if (!string.IsNullOrEmpty(seed.Replace("|", string.Empty))) _responseSeed = seed;
            if (message["tool_calls"] != null && message["tool_calls"].Type != JTokenType.Null)
            {
                if (!(message["tool_calls"] is JArray chunkCalls))
                {
                    error = "本地 Qwen3.8 流式工具调用不是数组";
                    return false;
                }
                if (_pendingToolCalls == null) _pendingToolCalls = new JArray();
                if (_pendingToolCalls.Count + chunkCalls.Count > LlmProtocolLimits.MaxToolCalls)
                {
                    error = "本地 Qwen3.8 流式工具调用超过上限";
                    return false;
                }
                foreach (JToken call in chunkCalls) _pendingToolCalls.Add(call.DeepClone());

                // Ollama 的原生流会把不同工具放在不同 NDJSON 分片，官方要求逐片累积。
                // 每次累积后立即复用最终转换器校验名称、对象参数及总字节上限，避免把
                // 多个合法小分片聚成一个越界的内存对象后才在流末尾拒绝。
                var validationMessage = new JObject
                {
                    ["content"] = string.Empty,
                    ["tool_calls"] = _pendingToolCalls.DeepClone(),
                };
                if (!OllamaNativeQwen38Adapter.TryConvertResponseMessage(validationMessage,
                    _responseSeed, out _, out _, out error)) return false;
            }

            bool done = response["done"].Value<bool>();
            var responseMessage = (JObject)message.DeepClone();
            if (!done) responseMessage.Remove("tool_calls");
            else if (_pendingToolCalls != null) responseMessage["tool_calls"] = _pendingToolCalls.DeepClone();
            if (!OllamaNativeQwen38Adapter.TryConvertResponseMessage(responseMessage,
                _responseSeed, out var converted, out int callCount, out error)) return false;

            var delta = new JObject();
            if (converted["content"]?.Type == JTokenType.String
                && converted["content"].Value<string>().Length > 0)
                delta["content"] = converted["content"].Value<string>();
            if (converted["reasoning_content"]?.Type == JTokenType.String)
                delta["reasoning_content"] = converted["reasoning_content"].Value<string>() ?? string.Empty;
            if (done && callCount > 0) delta["tool_calls"] = converted["tool_calls"].DeepClone();

            if (delta.Count > 0 || done)
            {
                var choice = new JObject
                {
                    ["index"] = 0,
                    ["delta"] = delta,
                    ["finish_reason"] = done
                        ? (callCount > 0 ? "tool_calls"
                            : OllamaNativeQwen38Adapter.NormalizeFinishReason(response["done_reason"]))
                        : JValue.CreateNull(),
                };
                var normalized = new JObject
                {
                    ["choices"] = new JArray(choice),
                };
                if (done) normalized["usage"] = OllamaNativeQwen38Adapter.BuildUsage(response);
                openAiPayload = normalized.ToString(Formatting.None);
            }
            if (done)
            {
                _done = true;
                emitDoneSentinel = true;
            }
            return true;
        }
    }
}
