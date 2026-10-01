namespace JianghuYouling.Core.Llm
{
    /// <summary>
    /// 每次请求独立的推理策略。它不表示 UI 是否展示思考；调用方应分别决定展示策略，
    /// 避免全局静态开关在并发单聊、群聊和过月任务之间串扰。
    /// </summary>
    public enum LlmReasoningPolicy
    {
        /// <summary>沿用模型的原生自动/自适应推理策略。</summary>
        Auto = 0,
        /// <summary>请求较低推理预算；模型不支持低档时安全退化到其最低可用档。</summary>
        Low = 1,
        /// <summary>关闭推理；对 thinking-only 模型只能省略控制字段并在本地隔离思考正文。</summary>
        Off = 2,
    }
}
