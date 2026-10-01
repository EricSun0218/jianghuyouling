namespace JianghuYouling.Core.Llm
{
    /// <summary>已解析的 LLM 连接配置(base/path/model/key)。</summary>
    public sealed class LlmConfig
    {
        public string BaseUrl { get; set; }
        public string ChatPath { get; set; }
        public string Model { get; set; }
        public string ApiKey { get; set; }
        public bool StripThink { get; set; } = true;

        public OpenAiCompatibleClient CreateClient() =>
            new OpenAiCompatibleClient(BaseUrl, ChatPath, ApiKey, Model);
    }
}
