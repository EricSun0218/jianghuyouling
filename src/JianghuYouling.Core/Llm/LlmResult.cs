namespace JianghuYouling.Core.Llm
{
    /// <summary>LLM 调用结果。Content 已剥离思维链;Reasoning 为思维链(若有)。</summary>
    public sealed class LlmResult
    {
        public bool Ok { get; set; }
        public string Content { get; set; }
        public string Reasoning { get; set; }
        public string RawContent { get; set; }
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int CachedTokens { get; set; }
        public int CacheMissTokens { get; set; }
        public int CacheWriteTokens { get; set; }
        public int ReasoningTokens { get; set; }
        public int TotalTokens { get; set; }
        public long QueueMs { get; set; }          // 后台/全局并发闸排队耗时
        public long TtftMs { get; set; }           // 首响应头/首个流式数据到达；未观测为 0
        public int RetryCount { get; set; }        // 网络或能力兼容重发次数
        public string RetryClass { get; set; }     // transient_connection/json_mode_unsupported/...
        public int DroppedChunks { get; set; }   // 流式中 JSON 解析失败被跳过的 chunk 数(诊断首字丢失)
        public bool Canceled { get; set; }        // 被外部中断(中断按钮)取消,而非失败 —— 调用方据此不报错、不重试
        public string Error { get; set; }
    }
}
