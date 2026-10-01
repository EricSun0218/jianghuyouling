namespace JianghuYouling.Core.Llm
{
    internal static class LlmProtocolLimits
    {
        public const int MaxRequestBytes = 8 * 1024 * 1024;
        public const int MaxResponseBytes = 8 * 1024 * 1024;
        public const int MaxHttpHeaderBytes = 64 * 1024;
        public const int MaxHttpErrorBytes = 16 * 1024;
        public const int MaxSseLineChars = 2 * 1024 * 1024;
        public const int MaxSseEventChars = 2 * 1024 * 1024;
        public const int MaxVisibleChars = 2 * 1024 * 1024;
        public const int MaxReasoningChars = 4 * 1024 * 1024;
        public const int MaxToolDefinitions = 128;
        public const int MaxToolCalls = 32;
        public const int MaxToolArgumentsBytes = 64 * 1024;
        public const int MaxTotalToolArgumentsBytes = 512 * 1024;
        public const int MaxProviderMetadataBytes = 64 * 1024;
        public const int MaxToolIdChars = 512;
        public const int MaxToolNameChars = 64;
        public const int MaxJsonDepth = 64;
    }
}
