using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using JianghuYouling.Core.Llm;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Web
{
    public sealed class MiniMaxAudioResult { public bool Ok; public byte[] Mp3; public string Error; public string Format; }

    /// <summary>
    /// MiniMax T2A、火山 Seed-TTS 2.0、OpenAI 兼容 speech 与 DashScope Qwen 非实时语音客户端。
    /// 所有响应均有长度上限；音频必须同时通过 Content-Type 和文件头校验；失败不抛出。
    /// </summary>
    public static class MiniMaxClient
    {
        public const int MaxAudioBytes = 32 * 1024 * 1024;
        public const int MaxJsonBytes = MaxAudioBytes * 2 + 512 * 1024; // MiniMax 非流式 audio 是 hex。
        const int MaxRedirects = 5;
        static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        static readonly HttpClient _http = CreateHttp();

        static HttpClient CreateHttp()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        }

        /// <summary>MiniMax T2A v2。baseUrl 形如 https://api.minimaxi.com/v1；返回经文件头校验的 MP3。</summary>
        public static async Task<MiniMaxAudioResult> TextToSpeechAsync(
            string baseUrl, string apiKey, string text, string voiceId,
            string model = "speech-02-turbo", float speed = 1f, string emotion = null, float vol = 1f, int pitch = 0, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return Fail("未配置 MiniMax 语音密钥");
            if (string.IsNullOrWhiteSpace(text)) return Fail("空文本");
            apiKey = apiKey.Trim();
            if (!TtsProviderUtil.TryValidateApiEndpoint(baseUrl, apiKey, out var endpoint, out var endpointError)) return Fail(endpointError);
            if (text.Length >= 10000) return Fail("MiniMax TTS 文本必须少于 10000 字符");
            speed = Clamp(speed, 0.5f, 2f);
            vol = Clamp(vol, 0.01f, 10f);
            pitch = Math.Max(-12, Math.Min(12, pitch));

            string url = endpoint.AbsoluteUri.TrimEnd('/') + "/t2a_v2";
            var vs = new JObject { ["voice_id"] = voiceId ?? "female-shaonv", ["speed"] = speed, ["vol"] = vol, ["pitch"] = pitch };
            if (!string.IsNullOrEmpty(emotion) && emotion != "neutral") vs["emotion"] = emotion;
            var body = new JObject
            {
                ["model"] = string.IsNullOrWhiteSpace(model) ? "speech-02-turbo" : model,
                ["text"] = text,
                ["stream"] = false,
                ["voice_setting"] = vs,
                ["audio_setting"] = new JObject { ["sample_rate"] = 32000, ["bitrate"] = 128000, ["format"] = "mp3", ["channel"] = 1 },
            };

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                    req.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    {
                        byte[] raw = await ReadBoundedAsync(resp.Content, MaxJsonBytes, ct).ConfigureAwait(false);
                        if (!TryDecodeUtf8(raw, out var json)) return Fail("MiniMax 返回不是有效 UTF-8 JSON");
                        JObject o;
                        try { o = JObject.Parse(json); }
                        catch { return Fail("MiniMax 返回非 JSON(HTTP " + (int)resp.StatusCode + ")"); }

                        int code = o["base_resp"]?["status_code"]?.Value<int?>() ?? -1;
                        if (!resp.IsSuccessStatusCode || code != 0)
                        {
                            string msg = o["base_resp"]?["status_msg"]?.ToString() ?? ("status " + code);
                            return Fail(SafeExternalError(msg, apiKey));
                        }
                        string hex = o["data"]?["audio"]?.ToString();
                        if (string.IsNullOrEmpty(hex)) return Fail("MiniMax 返回中没有音频数据");
                        if (!TtsProviderUtil.TryDecodeHex(hex, MaxAudioBytes, out var bytes) || bytes == null || bytes.Length < 100)
                            return Fail("MiniMax 音频 hex 无效或超过 32 MiB");
                        if (!TtsProviderUtil.TryDetectAudioFormat(bytes, "audio/mpeg", out var format, out var formatError) || format != "mp3")
                            return Fail(formatError ?? "MiniMax 音频不是 MP3");
                        return new MiniMaxAudioResult { Ok = true, Mp3 = bytes, Format = format };
                    }
                }
            }
            catch (OperationCanceledException) { return Fail("已取消/超时"); }
            catch (InvalidDataException e) { return Fail(e.Message); }
            catch (Exception e) { return Fail(FormatException(e, apiKey)); }
        }

        /// <summary>
        /// 火山豆包 Seed Audio 非流式 HTTP 接口。新版控制台使用 X-Api-Key；自然语言
        /// voiceDescription 与每段独立语速/情绪共同驱动人物化配音。
        /// </summary>
        public static async Task<MiniMaxAudioResult> VolcengineSeedAudioAsync(
            string endpointUrl, string apiKey, string model, string spokenText,
            string voiceDescription, float speed, string emotion, float volume, int pitch,
            CancellationToken ct = default)
        {
            apiKey = (apiKey ?? "").Trim();
            if (apiKey.Length == 0) return Fail("未配置火山配音 API Key");
            if (string.IsNullOrWhiteSpace(spokenText)) return Fail("空文本");
            if (!TtsProviderUtil.TryValidateApiEndpoint(endpointUrl, apiKey, out Uri endpoint, out string endpointError))
                return Fail(endpointError);
            if (!string.Equals(endpoint.Host, "openspeech.bytedance.com", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(endpoint.AbsolutePath.TrimEnd('/'), "/api/v3/tts/create", StringComparison.Ordinal))
                return Fail("火山 Seed Audio 接口必须是 https://openspeech.bytedance.com/api/v3/tts/create");
            speed = Clamp(speed, 0.5f, 2f);
            volume = Clamp(volume, 0.5f, 2f);
            pitch = Math.Max(-12, Math.Min(12, pitch));
            int speechRate = speed >= 1f
                ? (int)Math.Round((speed - 1f) * 100f)
                : (int)Math.Round((speed - 1f) * 100f);
            speechRate = Math.Max(-50, Math.Min(100, speechRate));
            int loudnessRate = volume >= 1f
                ? (int)Math.Round((volume - 1f) * 100f)
                : (int)Math.Round((volume - 1f) * 100f);
            loudnessRate = Math.Max(-50, Math.Min(100, loudnessRate));
            string prompt = BuildSeedAudioPrompt(spokenText, voiceDescription, emotion, speed);
            if (prompt.Length > 3000) return Fail("火山配音单段提示词超过 3000 字符");
            var body = new JObject
            {
                ["model"] = string.IsNullOrWhiteSpace(model) ? "seed-audio-1.0" : model.Trim(),
                ["text_prompt"] = prompt,
                ["audio_config"] = new JObject
                {
                    ["format"] = "mp3",
                    ["sample_rate"] = 32000,
                    ["speech_rate"] = speechRate,
                    ["loudness_rate"] = loudnessRate,
                    ["pitch_rate"] = pitch,
                    ["enable_subtitle"] = false
                },
                ["watermark"] = new JObject { ["aigc_watermark"] = false }
            };

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, endpoint))
                {
                    req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
                    req.Headers.TryAddWithoutValidation("X-Api-Request-Id", Guid.NewGuid().ToString());
                    req.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    {
                        byte[] raw = await ReadBoundedAsync(resp.Content, MaxJsonBytes, ct).ConfigureAwait(false);
                        if (!TryDecodeUtf8(raw, out string json)) return Fail("火山配音返回不是有效 UTF-8 JSON");
                        JObject root;
                        try { root = JObject.Parse(json); }
                        catch { return Fail("火山配音返回非 JSON（HTTP " + (int)resp.StatusCode + "）"); }
                        int code = root["code"]?.Value<int?>() ?? (resp.IsSuccessStatusCode ? 0 : -1);
                        if (!resp.IsSuccessStatusCode || code != 0)
                        {
                            string message = root["message"]?.ToString() ?? ("code " + code);
                            return Fail("火山配音 HTTP " + (int)resp.StatusCode + "：" +
                                SafeExternalError(SecretRedactor.Redact(message, apiKey, spokenText, prompt), apiKey));
                        }
                        string b64 = root["audio"]?.ToString();
                        if (string.IsNullOrWhiteSpace(b64)) return Fail("火山配音返回中没有 audio 数据");
                        if (b64.Length > ((MaxAudioBytes + 2L) / 3L) * 4L + 8L || b64.IndexOfAny(new[] { '\r', '\n', ' ', '\t' }) >= 0)
                            return Fail("火山配音音频超过 32 MiB 或 Base64 无效");
                        byte[] audio;
                        try { audio = Convert.FromBase64String(b64); }
                        catch { return Fail("火山配音 audio 不是有效 Base64"); }
                        if (!TtsProviderUtil.TryDetectAudioFormat(audio, "audio/mpeg", out string format, out string formatError)
                            || format != "mp3") return Fail(formatError ?? "火山配音返回的不是 MP3");
                        return new MiniMaxAudioResult { Ok = true, Mp3 = audio, Format = format };
                    }
                }
            }
            catch (OperationCanceledException) { return Fail("已取消/超时"); }
            catch (InvalidDataException e) { return Fail(e.Message); }
            catch (Exception e) { return Fail(FormatException(e, apiKey)); }
        }

        /// <summary>
        /// 火山豆包 Seed-TTS 2.0 HTTP Chunked 接口。响应是逐行 JSON 事件；只有收到
        /// 20000000 完成事件且所有 Base64 音频均通过总量与文件头校验才会交给播放器。
        /// </summary>
        public static async Task<MiniMaxAudioResult> VolcengineSeedTts2Async(
            string endpointUrl, string apiKey, string resourceId, string spokenText,
            string speaker, string performanceProfile, float speed, string emotion,
            float volume, CancellationToken ct = default)
        {
            apiKey = (apiKey ?? "").Trim();
            if (apiKey.Length == 0) return Fail("未配置火山配音 API Key");
            if (string.IsNullOrWhiteSpace(spokenText)) return Fail("空文本");
            if (!TtsProviderUtil.TryValidateApiEndpoint(endpointUrl, apiKey, out Uri endpoint, out string endpointError))
                return Fail(endpointError);
            if (!string.Equals(endpoint.Host, "openspeech.bytedance.com", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(endpoint.AbsolutePath.TrimEnd('/'), "/api/v3/tts/unidirectional", StringComparison.Ordinal))
                return Fail("火山 Seed-TTS 2.0 接口必须是 https://openspeech.bytedance.com/api/v3/tts/unidirectional");
            string resource = string.IsNullOrWhiteSpace(resourceId) ? "seed-tts-2.0" : resourceId.Trim();
            if (!string.Equals(resource, "seed-tts-2.0", StringComparison.OrdinalIgnoreCase))
                return Fail("火山 Seed-TTS 2.0 资源模型必须是 seed-tts-2.0");
            string voice = string.IsNullOrWhiteSpace(speaker)
                ? "zh_female_vv_uranus_bigtts" : speaker.Trim();
            if (voice.Length > 256 || voice.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                return Fail("火山配音音色 ID 无效");
            speed = Clamp(speed, 0.5f, 2f);
            volume = Clamp(volume, 0.5f, 2f);
            int speechRate = Math.Max(-50, Math.Min(100, (int)Math.Round((speed - 1f) * 100f)));
            int loudnessRate = Math.Max(-50, Math.Min(100, (int)Math.Round((volume - 1f) * 100f)));
            string direction = BuildSeedTts2Direction(performanceProfile, emotion, speed);
            var additions = new JObject
            {
                ["disable_markdown_filter"] = true,
                ["explicit_language"] = "zh-cn",
                ["silence_duration"] = 180,
                ["aigc_watermark"] = false,
                ["context_texts"] = new JArray("[# " + direction + "]")
            };
            var body = new JObject
            {
                ["req_params"] = new JObject
                {
                    ["text"] = spokenText,
                    ["speaker"] = voice,
                    ["audio_params"] = new JObject
                    {
                        ["format"] = "mp3",
                        ["sample_rate"] = 32000,
                        ["bit_rate"] = 128000,
                        ["speech_rate"] = speechRate,
                        ["loudness_rate"] = loudnessRate
                    },
                    ["additions"] = additions.ToString(Newtonsoft.Json.Formatting.None)
                }
            };

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, endpoint))
                {
                    req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
                    req.Headers.TryAddWithoutValidation("X-Api-Resource-Id", "seed-tts-2.0");
                    req.Headers.TryAddWithoutValidation("X-Api-Request-Id", Guid.NewGuid().ToString("N"));
                    req.Headers.TryAddWithoutValidation("X-Control-Require-Usage-Tokens-Return", "*");
                    req.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    {
                        byte[] raw = await ReadBoundedAsync(resp.Content, MaxJsonBytes, ct).ConfigureAwait(false);
                        if (!TryDecodeUtf8(raw, out string eventText)) return Fail("火山配音返回不是有效 UTF-8 事件流");
                        bool completed = false;
                        using (var audio = new MemoryStream())
                        using (var reader = new StringReader(eventText))
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                line = line.Trim();
                                if (line.Length == 0) continue;
                                JObject evt;
                                try { evt = JObject.Parse(line); }
                                catch { return Fail("火山配音返回了无法解析的事件"); }
                                int code = evt["code"]?.Value<int?>() ?? -1;
                                if (code == 20000000)
                                {
                                    completed = true;
                                    continue;
                                }
                                if (code != 0)
                                {
                                    string message = evt["message"]?.ToString() ?? ("code " + code);
                                    return Fail("火山配音 HTTP " + (int)resp.StatusCode + "：" +
                                        SafeExternalError(SecretRedactor.Redact(message, apiKey, spokenText, direction), apiKey));
                                }
                                string b64 = evt["data"]?.ToString();
                                if (string.IsNullOrWhiteSpace(b64)) continue;
                                if (b64.Length > ((MaxAudioBytes + 2L) / 3L) * 4L + 8L
                                    || b64.IndexOfAny(new[] { '\r', '\n', ' ', '\t' }) >= 0)
                                    return Fail("火山配音音频超过 32 MiB 或 Base64 无效");
                                byte[] chunk;
                                try { chunk = Convert.FromBase64String(b64); }
                                catch { return Fail("火山配音事件中的 data 不是有效 Base64"); }
                                if (audio.Length + chunk.Length > MaxAudioBytes)
                                    return Fail("火山配音音频超过 32 MiB");
                                audio.Write(chunk, 0, chunk.Length);
                            }
                            if (!resp.IsSuccessStatusCode)
                                return Fail("火山配音 HTTP " + (int)resp.StatusCode);
                            if (!completed) return Fail("火山配音事件流未正常完成");
                            byte[] bytes = audio.ToArray();
                            if (bytes.Length < 100) return Fail("火山配音返回中没有音频数据");
                            if (!TtsProviderUtil.TryDetectAudioFormat(bytes, "audio/mpeg", out string format,
                                    out string formatError) || format != "mp3")
                                return Fail(formatError ?? "火山配音返回的不是 MP3");
                            return new MiniMaxAudioResult { Ok = true, Mp3 = bytes, Format = format };
                        }
                    }
                }
            }
            catch (OperationCanceledException) { return Fail("已取消/超时"); }
            catch (InvalidDataException e) { return Fail(e.Message); }
            catch (Exception e) { return Fail(FormatException(e, apiKey)); }
        }

        static string BuildSeedTts2Direction(string performanceProfile, string emotion, float speed)
        {
            string profile = string.IsNullOrWhiteSpace(performanceProfile)
                ? "符合当前人物身份与年龄，保持人物声线稳定" : performanceProfile.Trim();
            if (profile.Length > 320) profile = profile.Substring(0, 320);
            string mood = string.IsNullOrWhiteSpace(emotion) || emotion == "neutral" ? "自然、贴合语义"
                : emotion == "happy" ? "欣喜明快"
                : emotion == "sad" ? "悲伤克制"
                : emotion == "angry" ? "愤怒有力度"
                : emotion == "fearful" ? "紧张恐惧"
                : emotion == "surprised" ? "惊讶鲜明"
                : emotion;
            string pace = speed >= 1.12f ? "整体稍快" : speed <= 0.9f ? "整体稍慢" : "整体自然";
            return "人物表演：" + profile + "。本段以" + mood + "为主，" + pace
                + "；同一段内的重音、停顿、情绪强弱和局部语速必须随台词含义自然变化，不要播音腔。";
        }

        static string BuildSeedAudioPrompt(string spokenText, string voiceDescription, string emotion, float speed)
        {
            string mood = string.IsNullOrWhiteSpace(emotion) || emotion == "neutral" ? "自然、贴合语境"
                : emotion == "happy" ? "欣喜、明快"
                : emotion == "sad" ? "悲伤、克制"
                : emotion == "angry" ? "愤怒、有力度"
                : emotion == "fearful" ? "紧张、恐惧"
                : emotion == "surprised" ? "惊讶、反应鲜明"
                : emotion;
            string pace = speed >= 1.12f ? "稍快" : speed <= 0.9f ? "稍慢" : "自然";
            string description = string.IsNullOrWhiteSpace(voiceDescription)
                ? "自然可信的中文武侠人物声音" : voiceDescription.Trim();
            return "使用" + description + "，以" + mood + "的动态语气和" + pace
                + "语速说出台词。只朗读引号内台词，不朗读任何说明：\n“" + spokenText + "”";
        }

        /// <summary>OpenAI 兼容 TTS：POST {base}/audio/speech，返回 MP3/WAV/OGG 二进制音频。</summary>
        public static async Task<MiniMaxAudioResult> OpenAiSpeechAsync(
            string baseUrl, string apiKey, string model, string voice, string text,
            string format = "mp3", float speed = 1f, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(text)) return Fail("空文本");
            apiKey = (apiKey ?? "").Trim();
            if (!TtsProviderUtil.TryValidateApiEndpoint(baseUrl, apiKey, out var endpoint, out var endpointError)) return Fail(endpointError);
            string requestedFormat = NormalizeAudioFormat(format);
            if (requestedFormat == null) return Fail("仅支持 MP3/WAV/OGG 音频格式");
            speed = Clamp(speed, 0.5f, 2f);

            string url = endpoint.AbsoluteUri.TrimEnd('/') + "/audio/speech";
            var body = new JObject
            {
                ["model"] = string.IsNullOrWhiteSpace(model) ? "tts-1" : model,
                ["input"] = text,
                ["voice"] = string.IsNullOrWhiteSpace(voice) ? "nova" : voice,
                ["response_format"] = requestedFormat,
                ["speed"] = speed,
            };
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    if (!string.IsNullOrEmpty(apiKey)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                    req.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    {
                        byte[] bytes = await ReadBoundedAsync(resp.Content, MaxAudioBytes, ct).ConfigureAwait(false);
                        string responseType = resp.Content?.Headers?.ContentType?.MediaType;
                        bool jsonType = IsJsonContentType(responseType);
                        bool looksJson = LooksLikeJson(bytes);
                        if (!resp.IsSuccessStatusCode || jsonType || looksJson)
                        {
                            string msg = DecodeErrorText(bytes);
                            try
                            {
                                var o = JObject.Parse(msg);
                                msg = o["error"]?["message"]?.ToString() ?? o["message"]?.ToString() ?? msg;
                            }
                            catch { }
                            return Fail("TTS HTTP " + (int)resp.StatusCode + ":" + SafeExternalError(msg, apiKey));
                        }
                        if (bytes == null || bytes.Length < 100) return Fail("音频为空");
                        if (!TtsProviderUtil.TryDetectAudioFormat(bytes, responseType, out var actualFormat, out var formatError))
                            return Fail(formatError);
                        if (!string.Equals(actualFormat, requestedFormat, StringComparison.Ordinal))
                            return Fail("TTS 返回格式与请求格式不一致");
                        return new MiniMaxAudioResult { Ok = true, Mp3 = bytes, Format = actualFormat };
                    }
                }
            }
            catch (OperationCanceledException) { return Fail("已取消/超时"); }
            catch (InvalidDataException e) { return Fail(e.Message); }
            catch (Exception e) { return Fail(FormatException(e, apiKey)); }
        }

        /// <summary>阿里云百炼/DashScope Qwen 非实时 TTS。调用方应先用 SplitDashScopeText 分块。</summary>
        public static async Task<MiniMaxAudioResult> DashScopeQwenSpeechAsync(
            string baseUrl, string apiKey, string model, string voice, string text,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return Fail("未配置 DashScope 语音密钥");
            if (string.IsNullOrWhiteSpace(text)) return Fail("空文本");
            apiKey = apiKey.Trim();
            if (!TtsProviderUtil.TryValidateApiEndpoint(baseUrl, apiKey, out var endpoint, out var endpointError)) return Fail(endpointError);
            if (TtsProviderUtil.IsDashScopeUnsupportedAppOrRealtime(baseUrl, model))
                return Fail("当前只支持 Qwen 非实时 TTS；请使用 /api/v1 地址和非 realtime 模型");
            if (!TtsProviderUtil.IsDashScopeTextWithinLimits(text))
                return Fail("Qwen TTS 单块超过 600 字符或估算 512 tokens；请先分块");

            string url = TtsProviderUtil.BuildDashScopeGenerationUrl(endpoint.AbsoluteUri);
            var body = new JObject
            {
                ["model"] = string.IsNullOrWhiteSpace(model) ? "qwen3-tts-flash" : model,
                ["input"] = new JObject
                {
                    ["text"] = text,
                    ["voice"] = string.IsNullOrWhiteSpace(voice) ? "Cherry" : voice,
                    ["language_type"] = "Chinese",
                },
            };

            try
            {
                string audioUrl;
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                    req.Headers.TryAddWithoutValidation("X-DashScope-SSE", "disable");
                    req.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    {
                        byte[] raw = await ReadBoundedAsync(resp.Content, MaxJsonBytes, ct).ConfigureAwait(false);
                        if (!TryDecodeUtf8(raw, out var json)) return Fail("DashScope 返回不是有效 UTF-8 JSON");
                        if (!resp.IsSuccessStatusCode)
                            return Fail("DashScope TTS HTTP " + (int)resp.StatusCode + ":" + SafeExternalError(ExtractJsonMessage(json), apiKey));
                        try { audioUrl = TtsProviderUtil.ParseDashScopeAudioUrl(json); }
                        catch { return Fail("DashScope 返回 JSON 无法解析"); }
                        if (string.IsNullOrWhiteSpace(audioUrl)) return Fail("DashScope 返回中没有 output.audio.url");
                    }
                }

                return await DownloadDashScopeAudioAsync(audioUrl, apiKey, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return Fail("已取消/超时"); }
            catch (InvalidDataException e) { return Fail(e.Message); }
            catch (Exception e) { return Fail(FormatException(e, apiKey)); }
        }

        static async Task<MiniMaxAudioResult> DownloadDashScopeAudioAsync(string audioUrl, string apiKey, CancellationToken ct)
        {
            if (!TtsProviderUtil.TryValidateDashScopeAudioUrl(audioUrl, out var current, out var validationError))
                return Fail(validationError);

            for (int redirect = 0; redirect <= MaxRedirects; redirect++)
            {
                string dnsError = await ValidatePublicDnsAsync(current, ct).ConfigureAwait(false);
                if (dnsError != null) return Fail(dnsError);

                using (var req = new HttpRequestMessage(HttpMethod.Get, current))
                using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    int status = (int)resp.StatusCode;
                    if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                    {
                        if (redirect >= MaxRedirects) return Fail("下载 TTS 音频重定向过多");
                        string location = resp.Headers.Location?.OriginalString;
                        if (!TtsProviderUtil.TryResolveDashScopeRedirect(current, location, out var next, out var redirectError))
                            return Fail(redirectError);
                        current = next;
                        continue;
                    }

                    byte[] bytes = await ReadBoundedAsync(resp.Content, MaxAudioBytes, ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        return Fail("下载 TTS 音频失败 HTTP " + status + ":" + SafeExternalError(DecodeErrorText(bytes), apiKey));
                    if (bytes == null || bytes.Length < 100) return Fail("音频为空");
                    string contentType = resp.Content?.Headers?.ContentType?.MediaType;
                    if (!TtsProviderUtil.TryDetectAudioFormat(bytes, contentType, out var format, out var formatError))
                        return Fail(formatError);
                    return new MiniMaxAudioResult { Ok = true, Mp3 = bytes, Format = format };
                }
            }
            return Fail("下载 TTS 音频重定向过多");
        }

        static async Task<string> ValidatePublicDnsAsync(Uri uri, CancellationToken ct)
        {
            try
            {
                Task<IPAddress[]> lookup = Dns.GetHostAddressesAsync(uri.DnsSafeHost);
                Task completed = await Task.WhenAny(lookup, Task.Delay(TimeSpan.FromSeconds(5), ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (completed != lookup) return "DashScope OSS 主机 DNS 查询超时";
                IPAddress[] addresses = await lookup.ConfigureAwait(false);
                if (addresses == null || addresses.Length == 0) return "DashScope OSS 主机没有可用地址";
                foreach (var address in addresses)
                    if (TtsProviderUtil.IsForbiddenNetworkAddress(address))
                        return "DashScope OSS 主机解析到私网、回环或保留地址，已阻止下载";
                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return "DashScope OSS 主机 DNS 校验失败"; }
        }

        static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken ct)
        {
            if (content == null) return Array.Empty<byte>();
            long? declared = content.Headers.ContentLength;
            if (declared.HasValue && (declared.Value < 0 || declared.Value > maxBytes))
                throw new InvalidDataException("TTS 响应 Content-Length 超过 " + maxBytes + " 字节上限");

            using (Stream input = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var output = new MemoryStream(declared.HasValue ? (int)declared.Value : Math.Min(81920, maxBytes)))
            {
                var buffer = new byte[81920];
                int total = 0;
                while (true)
                {
                    int read = await input.ReadAsync(buffer, 0, Math.Min(buffer.Length, maxBytes - total + 1), ct).ConfigureAwait(false);
                    if (read <= 0) break;
                    total += read;
                    if (total > maxBytes) throw new InvalidDataException("TTS 响应正文超过 " + maxBytes + " 字节上限");
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
        }

        static MiniMaxAudioResult Fail(string error)
        {
            return new MiniMaxAudioResult { Ok = false, Error = string.IsNullOrWhiteSpace(error) ? "语音请求失败" : error };
        }

        static string NormalizeAudioFormat(string format)
        {
            string f = (format ?? "").Trim().ToLowerInvariant();
            if (f == "mp3" || f == "wav" || f == "ogg") return f;
            return null;
        }

        static float Clamp(float value, float min, float max)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return min;
            return Math.Max(min, Math.Min(max, value));
        }

        static bool IsJsonContentType(string contentType)
        {
            string ct = (contentType ?? "").Trim().ToLowerInvariant();
            return ct == "application/json" || ct.EndsWith("+json", StringComparison.Ordinal);
        }

        static bool LooksLikeJson(byte[] bytes)
        {
            if (bytes == null) return false;
            int i = 0;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) i = 3;
            while (i < bytes.Length && (bytes[i] == (byte)' ' || bytes[i] == (byte)'\t' || bytes[i] == (byte)'\r' || bytes[i] == (byte)'\n')) i++;
            return i < bytes.Length && (bytes[i] == (byte)'{' || bytes[i] == (byte)'[');
        }

        static bool TryDecodeUtf8(byte[] bytes, out string text)
        {
            text = null;
            try { text = StrictUtf8.GetString(bytes ?? Array.Empty<byte>()); return true; }
            catch { return false; }
        }

        static string DecodeErrorText(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";
            try { return StrictUtf8.GetString(bytes); }
            catch { return "非文本错误响应"; }
        }

        static string ExtractJsonMessage(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";
            try
            {
                var o = JObject.Parse(json);
                return o["message"]?.ToString() ?? o["error"]?["message"]?.ToString() ?? o["code"]?.ToString() ?? "provider error";
            }
            catch { return "provider error"; }
        }

        // 所有 provider 原始错误先过统一脱敏器，再移除签名 URL 查询串和控制字符。
        static string SafeExternalError(string value, string explicitSecret)
        {
            string safe = SecretRedactor.Redact(value ?? "", explicitSecret);
            safe = Regex.Replace(safe, @"https?://[^\s]+", m =>
            {
                try
                {
                    var u = new Uri(m.Value.TrimEnd('.', ',', ';', ')', ']', '}'));
                    return u.Scheme + "://" + u.Host + u.AbsolutePath;
                }
                catch { return "[REDACTED_URL]"; }
            });
            safe = safe.Replace("\r", " ").Replace("\n", " ").Trim();
            return safe.Length > 300 ? safe.Substring(0, 300) : safe;
        }

        static string FormatException(Exception e, string explicitSecret)
        {
            if (e == null) return "异常";
            var root = e.GetBaseException();
            // Exception messages from HTTP/TLS stacks may embed a full endpoint, signed query,
            // proxy response body or local path. Types retain actionable classification without
            // copying those private values into Player.log or model-facing diagnostics.
            string msg = e.GetType().Name;
            if (root != null && root != e) msg += " | inner=" + root.GetType().Name;
            return SafeExternalError(msg, explicitSecret);
        }
    }
}
