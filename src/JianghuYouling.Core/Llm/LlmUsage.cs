using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Llm
{
    internal struct LlmUsage
    {
        public int PromptTokens;
        public int CompletionTokens;
        public int ReasoningTokens;
        public int TotalTokens;
        public int CacheHitTokens;
        public int CacheMissTokens;
        public int CacheWriteTokens;

        public static LlmUsage Parse(JToken usage, LlmUsage fallback = default)
        {
            if (usage == null || usage.Type != JTokenType.Object) return fallback;
            var u = fallback;
            u.PromptTokens = NonNegativeInt(usage["prompt_tokens"] ?? usage["input_tokens"]
                ?? usage["prompt_token_count"] ?? usage["input_token_count"], u.PromptTokens);
            u.CompletionTokens = NonNegativeInt(usage["completion_tokens"] ?? usage["output_tokens"]
                ?? usage["candidates_token_count"] ?? usage["output_token_count"], u.CompletionTokens);
            u.ReasoningTokens = NonNegativeInt(Nested(usage, "completion_tokens_details", "reasoning_tokens")
                ?? Nested(usage, "output_tokens_details", "reasoning_tokens") ?? usage["reasoning_tokens"]
                ?? usage["thoughts_token_count"] ?? usage["thought_tokens"], u.ReasoningTokens);
            u.CacheHitTokens = NonNegativeInt(Nested(usage, "prompt_tokens_details", "cached_tokens")
                ?? usage["prompt_cache_hit_tokens"] ?? usage["cached_tokens"]
                ?? usage["cache_read_input_tokens"] ?? usage["cached_content_token_count"]
                ?? usage["total_cached_tokens"], u.CacheHitTokens);
            u.CacheMissTokens = NonNegativeInt(usage["prompt_cache_miss_tokens"]
                ?? Nested(usage, "prompt_tokens_details", "uncached_tokens")
                ?? usage["cache_miss_tokens"], u.CacheMissTokens);
            u.CacheWriteTokens = NonNegativeInt(usage["cache_creation_input_tokens"]
                ?? Nested(usage, "prompt_tokens_details", "cache_creation_tokens")
                ?? usage["cache_write_input_tokens"] ?? usage["cache_write_tokens"], u.CacheWriteTokens);
            u.TotalTokens = NonNegativeInt(usage["total_tokens"] ?? usage["total_token_count"], u.TotalTokens);
            if (u.TotalTokens <= 0 && (u.PromptTokens > 0 || u.CompletionTokens > 0))
                u.TotalTokens = SafeAdd(u.PromptTokens, u.CompletionTokens);
            if (u.CacheMissTokens <= 0 && u.PromptTokens > 0)
            {
                // Anthropic usage 的 input_tokens 是未命中部分，cache_read/cache_creation 另列；
                // OpenAI/DeepSeek/Gemini 的 prompt_tokens 通常是总输入，按总量减命中。
                if (usage["cache_read_input_tokens"] != null || usage["cache_creation_input_tokens"] != null)
                    u.CacheMissTokens = u.PromptTokens;
                else if (u.CacheHitTokens >= 0 && u.CacheHitTokens <= u.PromptTokens)
                    u.CacheMissTokens = u.PromptTokens - u.CacheHitTokens;
            }
            return u;
        }

        private static JToken Nested(JToken root, string objectName, string valueName)
        {
            // Some compatible providers (MiMo V2.5 included) explicitly emit
            // `prompt_tokens_details: null`. A JSON null is a non-null JValue, so the
            // null-conditional indexer still throws unless the parent is an object.
            return (root?[objectName] as JObject)?[valueName];
        }

        private static int NonNegativeInt(JToken token, int fallback)
        {
            if (token == null || token.Type == JTokenType.Null) return fallback;
            long value;
            try { value = token.Value<long>(); }
            catch { return fallback; }
            if (value < 0) return fallback;
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }

        private static int SafeAdd(int a, int b)
        {
            long sum = (long)a + b;
            return sum > int.MaxValue ? int.MaxValue : (int)sum;
        }
    }
}
