using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Llm
{
    /// <summary>模型发起的一次工具调用(assistant 消息里的 tool_calls 项)。</summary>
    public sealed class LlmToolCall
    {
        public string Id { get; set; }            // tool_call_id,回传 tool 结果时要对上
        public string Name { get; set; }          // function.name
        public string ArgumentsJson { get; set; } // function.arguments(原始 JSON 字符串)
        /// <summary>
        /// provider 附在 tool_call 上、后续轮必须原样回灌的扩展字段。
        /// 典型为 Gemini 3 extra_content.google.thought_signature。
        /// 不含 id/type/function/index 这些标准字段。
        /// </summary>
        public JObject ExtraFields { get; set; }
        /// <summary>
        /// 随工具轮 assistant 消息整体回灌的受控 provider 字段（当前仅 MiniMax reasoning_details）。
        /// 挂在首个调用上可在既有 agent 循环不改签名的情况下跨轮保存，但序列化时不属于 tool_call 本身。
        /// </summary>
        public JObject AssistantExtraFields { get; set; }
    }

    /// <summary>一条对话消息。role: system | user | assistant | tool。</summary>
    public sealed class LlmMessage
    {
        public string Role { get; set; }
        public string Content { get; set; }
        public string ToolCallId { get; set; }              // role=tool:对应的 tool_call id
        public List<LlmToolCall> ToolCalls { get; set; }    // role=assistant:模型发起的工具调用
        public string ReasoningContent { get; set; }         // DeepSeek 等要求工具轮下一步原样回灌 reasoning_content
        public bool CacheBoundary { get; set; }              // 本地稳定前缀标记；OpenAI 兼容请求不得直接翻译成 native cache_control
        public bool IsProactive { get; set; }                // 本地助手历史元数据：无人提问时的主动消息；上游请求序列化会忽略
        [Newtonsoft.Json.JsonIgnore]
        public bool IsUntrustedContextData { get; set; }     // 本地预算/信任元数据；上游请求不序列化

        public LlmMessage() { }
        public LlmMessage(string role, string content) { Role = role; Content = content; }

        public static LlmMessage System(string c) => new LlmMessage("system", c);
        public static LlmMessage User(string c) => new LlmMessage("user", c);
        public static LlmMessage Assistant(string c) => new LlmMessage("assistant", c);

        /// <summary>工具执行结果回传给模型(role=tool)。</summary>
        public static LlmMessage Tool(string toolCallId, string content) =>
            new LlmMessage("tool", content) { ToolCallId = toolCallId };

        /// <summary>模型本轮发起了工具调用的 assistant 消息(回灌进历史,供后续轮次)。</summary>
        public static LlmMessage WithToolCalls(List<LlmToolCall> calls, string content = null, string reasoningContent = null) =>
            new LlmMessage("assistant", content) { ToolCalls = calls, ReasoningContent = reasoningContent };
    }
}
