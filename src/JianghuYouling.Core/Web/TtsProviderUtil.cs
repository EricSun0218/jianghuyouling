using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Web
{
    /// <summary>语音 provider 识别、DashScope 输入切分，以及音频/网络边界的纯逻辑校验。</summary>
    public static class TtsProviderUtil
    {
        public const string ProviderMiniMax = "minimax";
        public const string ProviderOpenAi = "openai";
        public const string ProviderDashScopeQwen = "dashscope-qwen";
        public const string ProviderVolcengineSeedAudio = "volcengine-seed-audio";

        // DashScope 官方文档：Qwen-TTS 为 512 tokens，其余相关非实时模型为 600 字符。
        // 客户端同时守住两条边界；token 数为保守估算，无法替代 provider 自身 tokenizer。
        public const int DashScopeMaxCharacters = 600;
        public const int DashScopeMaxEstimatedTokens = 512;

        // A provider's per-request ceiling is not a safe ceiling for one click: DashScope
        // needs several paid requests for a long reply. Bound the complete utterance before
        // any request is dispatched, then share one synthesis-time budget across its chunks.
        public const int MaxSpeechCharacters = 6000;
        public const int MaxSpeechChunks = 10;
        public const int SpeechSynthesisDeadlineMilliseconds = 120 * 1000;

        /// <summary>Tracks only provider synthesis time; audio playback does not consume the network deadline.</summary>
        public sealed class TtsSynthesisBudget
        {
            readonly int _limitMilliseconds;
            long _consumedMilliseconds;

            public TtsSynthesisBudget(int limitMilliseconds = SpeechSynthesisDeadlineMilliseconds)
            {
                _limitMilliseconds = Math.Max(1, limitMilliseconds);
            }

            public long ConsumedMilliseconds => _consumedMilliseconds;

            /// <summary>Returns false before dispatch once the shared deadline is exhausted.</summary>
            public bool TryGetNextRequestTimeout(out int timeoutMilliseconds)
            {
                long remaining = _limitMilliseconds - _consumedMilliseconds;
                if (remaining <= 0)
                {
                    timeoutMilliseconds = 0;
                    return false;
                }
                timeoutMilliseconds = (int)Math.Min(int.MaxValue, remaining);
                return true;
            }

            public void Charge(long elapsedMilliseconds)
            {
                if (elapsedMilliseconds <= 0) return;
                _consumedMilliseconds = Math.Min(_limitMilliseconds,
                    _consumedMilliseconds + elapsedMilliseconds);
            }
        }

        public static string ResolveProvider(string baseUrl, string model, string explicitProvider = null)
        {
            if (!string.IsNullOrWhiteSpace(explicitProvider))
            {
                string explicitValue = explicitProvider.Trim().ToLowerInvariant();
                if (explicitValue == ProviderMiniMax || explicitValue == ProviderOpenAi || explicitValue == ProviderDashScopeQwen
                    || explicitValue == ProviderVolcengineSeedAudio)
                    return explicitValue;
            }
            string b = (baseUrl ?? "").Trim().ToLowerInvariant();
            string m = (model ?? "").Trim().ToLowerInvariant();
            if (b.Contains("minimax")) return ProviderMiniMax;
            if (b.Contains("openspeech.bytedance.com")
                && (b.Contains("/api/v3/tts/create") || b.Contains("/api/v3/tts/unidirectional")))
                return ProviderVolcengineSeedAudio;
            if (IsDashScopeQwenNonRealtime(b, m)) return ProviderDashScopeQwen;
            return ProviderOpenAi;
        }

        public static bool IsDashScopeQwenNonRealtime(string baseUrl, string model)
        {
            string b = (baseUrl ?? "").Trim().ToLowerInvariant();
            string m = (model ?? "").Trim().ToLowerInvariant();
            if (IsDashScopeUnsupportedAppOrRealtime(b, m)) return false;
            bool host = b.Contains("maas.aliyuncs.com") || b.Contains("dashscope.aliyuncs.com") ||
                        b.Contains("dashscope-intl.aliyuncs.com");
            return host && m.Contains("qwen") && m.Contains("tts");
        }

        public static bool IsDashScopeUnsupportedAppOrRealtime(string baseUrl, string model)
        {
            string b = (baseUrl ?? "").Trim().ToLowerInvariant();
            string m = (model ?? "").Trim().ToLowerInvariant();
            return b.Contains("/apps/") ||
                   b.Contains("api-ws") ||
                   b.StartsWith("ws://", StringComparison.Ordinal) ||
                   b.StartsWith("wss://", StringComparison.Ordinal) ||
                   m.Contains("realtime");
        }

        public static string BuildDashScopeGenerationUrl(string baseUrl)
        {
            string b = (baseUrl ?? "").Trim().TrimEnd('/');
            const string suffix = "/services/aigc/multimodal-generation/generation";
            if (b.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return b;
            return b + suffix;
        }

        public static string ParseDashScopeAudioUrl(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            var o = JObject.Parse(json);
            return o["output"]?["audio"]?["url"]?.ToString()
                ?? o["output"]?["audio_url"]?.ToString()
                ?? o["output"]?["url"]?.ToString()
                ?? o["audio"]?["url"]?.ToString();
        }

        /// <summary>
        /// 按句号/问叹号/分号/换行等自然停顿切分；单句过长时再在逗号、空白或硬边界切开。
        /// 每块同时不超过 600 个 UTF-16 字符和 512 个保守估算 token。
        /// </summary>
        public static IReadOnlyList<string> SplitDashScopeText(string text)
        {
            var chunks = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return chunks;

            int start = 0;
            while (start < text.Length)
            {
                int cut = FindDashScopeCut(text, start);
                if (cut <= start) cut = Math.Min(text.Length, start + 1);
                string chunk = text.Substring(start, cut - start);
                if (!string.IsNullOrWhiteSpace(chunk)) chunks.Add(chunk);
                start = cut;
            }
            return chunks;
        }

        /// <summary>
        /// Seed-TTS uses one performance direction per request. Keep paragraphs together, then
        /// separate quoted dialogue from surrounding narration inside each paragraph. This avoids
        /// both sentence-by-sentence stutter and asking the provider to synthesize quote/ellipsis-only
        /// fragments. The complete utterance planner still caps all paid requests before dispatch.
        /// </summary>
        public static IReadOnlyList<string> SplitDynamicProsodyText(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return result;

            int lineStart = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                bool atEnd = i == text.Length;
                if (!atEnd && text[i] != '\r' && text[i] != '\n') continue;
                if (i > lineStart) SplitNarrationAndDialogue(text.Substring(lineStart, i - lineStart), result);
                if (!atEnd && text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                lineStart = i + 1;
            }

            // A paragraph or quote can reach the hard cut without a natural stop. Re-run those few
            // oversized chunks through the existing conservative splitter first.
            var bounded = new List<string>();
            foreach (string part in result)
            {
                if (part.Length <= DashScopeMaxCharacters
                    && EstimateDashScopeTokens(part) <= DashScopeMaxEstimatedTokens) bounded.Add(part);
                else
                {
                    foreach (string subpart in SplitDashScopeText(part)) AddReadableChunk(bounded, subpart);
                }
            }
            if (bounded.Count <= MaxSpeechChunks) return bounded;
            if (text.Length > MaxSpeechChunks * DashScopeMaxCharacters) return bounded;

            // More than ten paragraph/dialogue spans are coalesced only as required by the
            // request budget. Symbol-only material was already discarded and cannot reappear.
            return BalanceDynamicChunks(string.Join("", bounded), MaxSpeechChunks, DashScopeMaxCharacters);
        }

        static void SplitNarrationAndDialogue(string line, List<string> chunks)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            int start = 0;
            int quoteDepth = 0;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                bool asciiQuote = c == '"';
                if (IsDialogueQuoteOpen(c) || (asciiQuote && quoteDepth == 0))
                {
                    if (quoteDepth == 0)
                    {
                        AddReadableChunk(chunks, line.Substring(start, i - start));
                        start = i;
                    }
                    quoteDepth++;
                    continue;
                }
                if (!IsDialogueQuoteClose(c) && !(asciiQuote && quoteDepth > 0)) continue;
                if (quoteDepth <= 0) continue;
                quoteDepth--;
                if (quoteDepth != 0) continue;
                AddReadableChunk(chunks, line.Substring(start, i + 1 - start));
                start = i + 1;
            }
            if (start < line.Length) AddReadableChunk(chunks, line.Substring(start));
        }

        static bool IsDialogueQuoteOpen(char c)
            => c == '「' || c == '『' || c == '“' || c == '‘';

        static bool IsDialogueQuoteClose(char c)
            => c == '」' || c == '』' || c == '”' || c == '’';

        static void AddReadableChunk(List<string> chunks, string value)
        {
            string part = (value ?? "").Trim();
            if (ContainsReadableSpeech(part)) chunks.Add(part);
        }

        /// <summary>Letters and digits are speakable; isolated quotes, emoji and punctuation are not.</summary>
        public static bool ContainsReadableSpeech(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            for (int i = 0; i < value.Length; i++)
                if (char.IsLetterOrDigit(value[i])) return true;
            return false;
        }

        public static bool IsDialogueSpeechChunk(string value)
        {
            string part = (value ?? "").Trim();
            if (part.Length == 0) return false;
            char first = part[0];
            return IsDialogueQuoteOpen(first) || first == '"';
        }

        static IReadOnlyList<string> BalanceDynamicChunks(string text, int maximumChunks, int maximumCharacters)
        {
            var chunks = new List<string>();
            int start = 0;
            while (start < text.Length && chunks.Count < maximumChunks)
            {
                int slots = maximumChunks - chunks.Count;
                int remaining = text.Length - start;
                if (slots == 1 || remaining <= 1)
                {
                    chunks.Add(text.Substring(start));
                    start = text.Length;
                    break;
                }

                int minimumTake = Math.Max(1, remaining - (slots - 1) * maximumCharacters);
                int desiredTake = Math.Max(minimumTake,
                    Math.Min(maximumCharacters, (remaining + slots - 1) / slots));
                int maximumTake = Math.Min(maximumCharacters, remaining);
                int cut = FindBalancedNaturalCut(text, start, minimumTake, desiredTake, maximumTake);
                string part = text.Substring(start, cut - start);
                if (!string.IsNullOrWhiteSpace(part)) chunks.Add(part);
                start = cut;
            }
            if (start < text.Length)
            {
                // This path is unreachable for a <=6000-character utterance and ten 600-char
                // slots, but fail visibly if future limits drift instead of silently truncating.
                chunks.Add(text.Substring(start));
            }
            return chunks;
        }

        static int FindBalancedNaturalCut(string text, int start, int minimumTake,
            int desiredTake, int maximumTake)
        {
            int minEnd = Math.Min(text.Length, start + minimumTake);
            int desiredEnd = Math.Min(text.Length, start + desiredTake);
            int maxEnd = Math.Min(text.Length, start + maximumTake);
            for (int end = desiredEnd; end <= maxEnd; end++)
                if (end > start && IsSentenceBoundary(text[end - 1])) return AvoidSurrogateSplit(text, end);
            for (int end = desiredEnd; end >= minEnd; end--)
                if (end > start && IsSentenceBoundary(text[end - 1])) return AvoidSurrogateSplit(text, end);
            return AvoidSurrogateSplit(text, desiredEnd);
        }

        static int AvoidSurrogateSplit(string text, int cut)
        {
            if (cut > 0 && cut < text.Length && char.IsHighSurrogate(text[cut - 1])
                && char.IsLowSurrogate(text[cut])) cut--;
            return Math.Max(1, cut);
        }

        /// <summary>
        /// Builds the complete request plan before network dispatch. Failure always returns an
        /// empty plan, so callers cannot accidentally synthesize a truncated or partially billed reply.
        /// </summary>
        public static bool TryPlanSpeech(string provider, string text, out IReadOnlyList<string> chunks, out string error)
        {
            chunks = Array.Empty<string>();
            error = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "没有可朗读的文字";
                return false;
            }
            if (text.Length > MaxSpeechCharacters)
            {
                error = "单次语音最多朗读 " + MaxSpeechCharacters + " 个字符";
                return false;
            }

            IReadOnlyList<string> planned = string.Equals(provider, ProviderVolcengineSeedAudio,
                    StringComparison.OrdinalIgnoreCase)
                ? SplitDynamicProsodyText(text)
                : (string.Equals(provider, ProviderDashScopeQwen, StringComparison.OrdinalIgnoreCase)
                    ? SplitDashScopeText(text)
                    : new[] { text });
            if (planned == null || planned.Count == 0)
            {
                error = "没有可朗读的文字";
                return false;
            }
            if (planned.Count > MaxSpeechChunks)
            {
                error = "单次语音最多合成 " + MaxSpeechChunks + " 段";
                return false;
            }
            chunks = planned;
            return true;
        }

        /// <summary>
        /// Cache identity keeps the complete normalized endpoint plus a one-way account digest
        /// and a runtime configuration revision. The plaintext API key is never retained in the key.
        /// </summary>
        public static string BuildTtsCacheScope(string provider, string baseUrl, string apiKey, long configRevision)
        {
            string endpoint;
            if (Uri.TryCreate((baseUrl ?? "").Trim(), UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                var builder = new UriBuilder(uri)
                {
                    UserName = "",
                    Password = "",
                    Query = "",
                    Fragment = ""
                };
                if ((builder.Scheme == Uri.UriSchemeHttp && builder.Port == 80)
                    || (builder.Scheme == Uri.UriSchemeHttps && builder.Port == 443)) builder.Port = -1;
                endpoint = builder.Uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path,
                    UriFormat.UriEscaped).TrimEnd('/');
            }
            else
            {
                endpoint = "invalid:" + Sha256Hex(baseUrl ?? "");
            }

            return (provider ?? "").Trim().ToLowerInvariant() + "|" + endpoint
                + "|account=" + Sha256Hex(apiKey ?? "")
                + "|revision=" + Math.Max(0L, configRevision);
        }

        static string Sha256Hex(string value)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        public static bool IsDashScopeTextWithinLimits(string text)
        {
            return !string.IsNullOrWhiteSpace(text)
                && text.Length <= DashScopeMaxCharacters
                && EstimateDashScopeTokens(text) <= DashScopeMaxEstimatedTokens;
        }

        /// <summary>
        /// 无本地 Qwen tokenizer 时的保守估算：CJK/非 ASCII 符号按 1 token，ASCII 连续词按每 2 字符 1 token，
        /// 空白和 ASCII 标点按组计。它宁可早切，不会把估算当成 provider 的权威计费数。
        /// </summary>
        public static int EstimateDashScopeTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var counter = new DashScopeTokenCounter();
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    counter.AddSymbol(2); i++; continue;
                }
                if (c <= 0x7F && char.IsLetterOrDigit(c))
                {
                    counter.AddAscii(); continue;
                }
                if (char.IsWhiteSpace(c))
                {
                    counter.AddWhitespace(); continue;
                }
                counter.AddSymbol(1);
            }
            return counter.Count;
        }

        static int FindDashScopeCut(string text, int start)
        {
            int hardEnd = Math.Min(text.Length, start + DashScopeMaxCharacters);
            int lastSentenceEnd = -1;
            int lastSoftEnd = -1;
            int acceptedEnd = start;
            var counter = new DashScopeTokenCounter();

            for (int i = start; i < hardEnd; i++)
            {
                int next = i + 1;
                // 不在 surrogate pair 中间切断。
                if (i + 1 < text.Length && char.IsHighSurrogate(text[i]) && char.IsLowSurrogate(text[i + 1]))
                {
                    if (next + 1 - start > DashScopeMaxCharacters) break;
                    next++;
                    counter.AddSymbol(2);
                    i++;
                }
                else if (text[i] <= 0x7F && char.IsLetterOrDigit(text[i])) counter.AddAscii();
                else if (char.IsWhiteSpace(text[i])) counter.AddWhitespace();
                else counter.AddSymbol(1);
                if (counter.Count > DashScopeMaxEstimatedTokens) break;
                acceptedEnd = next;
                char tail = text[next - 1];
                if (IsSentenceBoundary(tail)) lastSentenceEnd = next;
                else if (IsSoftBoundary(tail)) lastSoftEnd = next;
            }

            if (acceptedEnd >= text.Length) return text.Length;
            // 避免为了一个很早的标点产生极短块；优先选择落在已接纳区间后半段的自然边界。
            int half = start + Math.Max(1, (acceptedEnd - start) / 2);
            if (lastSentenceEnd >= half) return lastSentenceEnd;
            if (lastSoftEnd >= half) return lastSoftEnd;
            return acceptedEnd;
        }

        struct DashScopeTokenCounter
        {
            int _committed;
            int _asciiRun;
            int _whitespaceRun;

            public int Count => _committed + (_asciiRun + 1) / 2 + (_whitespaceRun + 3) / 4;

            public void AddAscii()
            {
                FlushWhitespace();
                _asciiRun++;
            }

            public void AddWhitespace()
            {
                FlushAscii();
                _whitespaceRun++;
            }

            public void AddSymbol(int weight)
            {
                FlushAscii();
                FlushWhitespace();
                _committed += Math.Max(1, weight);
            }

            void FlushAscii()
            {
                if (_asciiRun <= 0) return;
                _committed += (_asciiRun + 1) / 2;
                _asciiRun = 0;
            }

            void FlushWhitespace()
            {
                if (_whitespaceRun <= 0) return;
                _committed += (_whitespaceRun + 3) / 4;
                _whitespaceRun = 0;
            }
        }

        static bool IsSentenceBoundary(char c)
        {
            return c == '。' || c == '！' || c == '？' || c == '!' || c == '?' ||
                   c == '；' || c == ';' || c == '\n' || c == '\r';
        }

        static bool IsSoftBoundary(char c)
        {
            return c == '，' || c == ',' || c == '、' || c == '：' || c == ':' || char.IsWhiteSpace(c);
        }

        /// <summary>校验带凭据的远端 TTS 入口。远端必须 HTTPS；仅 loopback 开发入口可使用 HTTP。</summary>
        public static bool TryValidateApiEndpoint(string baseUrl, string apiKey, out Uri uri, out string error)
        {
            uri = null;
            error = null;
            string raw = (baseUrl ?? "").Trim();
            if (string.IsNullOrEmpty(raw)) { error = "未配置 TTS 接口"; return false; }
            if (ContainsCrlf(raw) || ContainsCrlf(apiKey)) { error = "TTS 接口或密钥包含非法换行"; return false; }
            if (!Uri.TryCreate(raw, UriKind.Absolute, out uri)) { error = "TTS 接口不是有效绝对 URL"; return false; }
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            { error = "TTS 接口只允许 HTTP/HTTPS"; uri = null; return false; }
            if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            { error = "TTS baseUrl 不得包含用户信息、查询串或片段"; uri = null; return false; }
            if (!string.IsNullOrWhiteSpace(apiKey) && uri.Scheme != Uri.UriSchemeHttps && !IsLoopbackHost(uri.Host))
            { error = "携带密钥的远端 TTS 接口必须使用 HTTPS"; uri = null; return false; }
            return true;
        }

        /// <summary>只允许 DashScope 官方返回的公网阿里云 OSS 地址；官方 HTTP 签名 URL 仍被允许。</summary>
        public static bool TryValidateDashScopeAudioUrl(string value, out Uri uri, out string error)
        {
            uri = null;
            error = null;
            string raw = (value ?? "").Trim();
            if (string.IsNullOrEmpty(raw) || ContainsCrlf(raw) || !Uri.TryCreate(raw, UriKind.Absolute, out uri))
            { error = "DashScope 音频 URL 无效"; uri = null; return false; }
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            { error = "DashScope 音频 URL 只允许 HTTP/HTTPS"; uri = null; return false; }
            if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            { error = "DashScope 音频 URL 含用户信息或片段"; uri = null; return false; }
            if (!uri.IsDefaultPort && !((uri.Scheme == Uri.UriSchemeHttp && uri.Port == 80) || (uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443)))
            { error = "DashScope 音频 URL 使用了非标准端口"; uri = null; return false; }
            if (!IsAllowedAliyunOssHost(uri.DnsSafeHost))
            { error = "DashScope 音频 URL 不是受信任的阿里云 OSS 主机"; uri = null; return false; }
            return true;
        }

        public static bool TryResolveDashScopeRedirect(Uri current, string location, out Uri next, out string error)
        {
            next = null;
            error = null;
            if (current == null || string.IsNullOrWhiteSpace(location) || ContainsCrlf(location))
            { error = "DashScope 音频重定向缺少有效 Location"; return false; }
            if (!Uri.TryCreate(current, location.Trim(), out var resolved))
            { error = "DashScope 音频重定向 Location 无效"; return false; }
            return TryValidateDashScopeAudioUrl(resolved.AbsoluteUri, out next, out error);
        }

        public static bool IsAllowedAliyunOssHost(string host)
        {
            string h = (host ?? "").Trim().TrimEnd('.').ToLowerInvariant();
            const string suffix = ".aliyuncs.com";
            if (!h.EndsWith(suffix, StringComparison.Ordinal)) return false;
            string prefix = h.Substring(0, h.Length - suffix.Length);
            string[] labels = prefix.Split('.');
            if (labels.Length == 0) return false;
            string service = labels[labels.Length - 1];
            if (!(service == "oss" || service.StartsWith("oss-", StringComparison.Ordinal))) return false;
            if (service.Contains("internal") || service.Contains("intranet") || service.Contains("vpc")) return false;
            return true;
        }

        public static bool IsForbiddenNetworkAddress(IPAddress address)
        {
            if (address == null) return true;
            if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
                address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None)) return true;

            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            byte[] b = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                int a = b[0], c = b[1], d = b[2];
                return a == 0 || a == 10 || a == 127 ||
                       (a == 100 && c >= 64 && c <= 127) ||
                       (a == 169 && c == 254) ||
                       (a == 172 && c >= 16 && c <= 31) ||
                       (a == 192 && c == 0 && (d == 0 || d == 2)) ||
                       (a == 192 && c == 168) ||
                       (a == 198 && (c == 18 || c == 19)) ||
                       (a == 198 && c == 51 && d == 100) ||
                       (a == 203 && c == 0 && d == 113) ||
                       a >= 224;
            }
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal ||
                       (b.Length > 0 && (b[0] & 0xFE) == 0xFC); // fc00::/7 ULA
            }
            return true;
        }

        public static bool TryDetectAudioFormat(byte[] bytes, string contentType, out string format, out string error)
        {
            format = null;
            error = null;
            string declared = FormatFromContentType(contentType);
            string magic = FormatFromMagic(bytes);
            if (declared == null) { error = "音频 Content-Type 未声明受支持格式"; return false; }
            if (magic == null) { error = "音频文件头不是受支持的 MP3/WAV/OGG"; return false; }
            if (!string.Equals(declared, magic, StringComparison.Ordinal))
            { error = "音频 Content-Type 与文件头不一致"; return false; }
            format = magic;
            return true;
        }

        /// <summary>兼容旧调用；未知或声明/文件头不一致时返回 null，不再猜成 MP3。</summary>
        public static string DetectAudioFormat(byte[] bytes, string contentType = null, string url = null)
        {
            return TryDetectAudioFormat(bytes, contentType, out var format, out _) ? format : null;
        }

        public static bool TryDecodeHex(string hex, int maxBytes, out byte[] bytes)
        {
            bytes = null;
            if (string.IsNullOrEmpty(hex) || maxBytes <= 0 || (hex.Length & 1) != 0 || hex.Length / 2 > maxBytes) return false;
            var result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                int hi = HexNibble(hex[i * 2]);
                int lo = HexNibble(hex[i * 2 + 1]);
                if (hi < 0 || lo < 0) return false;
                result[i] = (byte)((hi << 4) | lo);
            }
            bytes = result;
            return true;
        }

        static string FormatFromContentType(string contentType)
        {
            string ct = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
            if (ct == "audio/mpeg" || ct == "audio/mp3" || ct == "audio/x-mpeg") return "mp3";
            if (ct == "audio/wav" || ct == "audio/x-wav" || ct == "audio/wave") return "wav";
            if (ct == "audio/ogg" || ct == "application/ogg") return "ogg";
            return null;
        }

        static string FormatFromMagic(byte[] bytes)
        {
            if (bytes == null) return null;
            if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
                bytes[8] == (byte)'W' && bytes[9] == (byte)'A' && bytes[10] == (byte)'V' && bytes[11] == (byte)'E') return "wav";
            if (bytes.Length >= 4 && bytes[0] == (byte)'O' && bytes[1] == (byte)'g' && bytes[2] == (byte)'g' && bytes[3] == (byte)'S') return "ogg";
            if (bytes.Length >= 3 && bytes[0] == (byte)'I' && bytes[1] == (byte)'D' && bytes[2] == (byte)'3') return "mp3";
            if (bytes.Length >= 2 && bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0) return "mp3";
            return null;
        }

        static int HexNibble(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        static bool ContainsCrlf(string value)
        {
            return !string.IsNullOrEmpty(value) && (value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0);
        }

        static bool IsLoopbackHost(string host)
        {
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            string value = (host ?? "").Trim('[', ']');
            return IPAddress.TryParse(value, out var ip) && IPAddress.IsLoopback(ip);
        }
    }
}
