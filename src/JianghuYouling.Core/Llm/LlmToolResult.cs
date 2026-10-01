using System.Collections.Generic;

namespace JianghuYouling.Core.Llm
{
    /// <summary>一轮工具调用的结果:要么模型发起 ToolCalls(本轮要执行的工具),要么给出最终 Content(回话)。</summary>
    public sealed class LlmToolResult
    {
        public bool Ok { get; set; }
        public bool Canceled { get; set; }                  // 被中断
        public string Content { get; set; }                 // 最终回话(无 tool_calls 时;有 tool_calls 时通常为空)
        public List<LlmToolCall> ToolCalls { get; set; }    // 模型本轮发起的工具调用
        public string Reasoning { get; set; }               // 供 UI 展示的归一化思考（可含 summary/inline think）
        public string ReplayReasoningContent { get; set; }  // 协议回灌专用：原样 reasoning_content，禁止 Trim/混入其他字段
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int CachedTokens { get; set; }
        public int CacheMissTokens { get; set; }
        public int CacheWriteTokens { get; set; }
        public int ReasoningTokens { get; set; }
        public int TotalTokens { get; set; }
        public long QueueMs { get; set; }
        public long TtftMs { get; set; }
        public int RetryCount { get; set; }
        public string RetryClass { get; set; }
        public string Error { get; set; }

        public bool HasToolCalls => ToolCalls != null && ToolCalls.Count > 0;
    }
}
