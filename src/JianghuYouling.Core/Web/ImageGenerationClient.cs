using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JianghuYouling.Core.Llm;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Web
{
    public sealed class ImageGenerationRequest
    {
        public string Provider;
        public string Endpoint;
        public string ApiKey;
        public string Model;
        public string Prompt;
        public byte[] ReferencePng;
        public string Size;
        public bool Watermark;
        /// <summary>
        /// Host-owned ComfyUI workflow in API format. The model only supplies Prompt;
        /// it can never replace the trusted graph itself.
        /// </summary>
        public string WorkflowJson;
    }

    public sealed class ImageGenerationResult
    {
        public bool Ok;
        public byte[] Bytes;
        public string MediaType;
        public string Error;
    }

    /// <summary>
    /// Bounded image-generation wire adapter. Doubao Ark follows the official
    /// /api/v3/images/generations contract; OpenAI follows the official multipart
    /// /v1/images/edits reference-image contract; OpenRouter follows the provider-neutral
    /// input_references shape used by OpenRouter-compatible image services.
    /// </summary>
    public static class ImageGenerationClient
    {
        public const string ProviderDoubao = "doubao-ark";
        public const string ProviderOpenAI = "openai";
        public const string ProviderOpenRouter = "openrouter";
        public const string ProviderComfyUI = "comfyui";
        public const string FixedLandscapeSize = "2048x1152";
        public const string FixedLandscapeAspectRatio = "16:9";
        public const string LocalLandscapeSize = "1024x576";
        public const int LocalLandscapeWidth = 1024;
        public const int LocalLandscapeHeight = 576;
        public const int MaxComfyUiWorkflowBytes = 8_000_000;
        public const int MaxPromptChars = 12000;
        public const int MaxReferenceBytes = 16 * 1024 * 1024;
        public const int MaxImageBytes = 32 * 1024 * 1024;
        public const int MaxResponseBytes = 48 * 1024 * 1024;

        static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        static readonly HttpClient Http = CreateHttp();

        static HttpClient CreateHttp()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromMinutes(8)
            };
        }

        public static bool TryValidate(ImageGenerationRequest request, out Uri endpoint, out string error)
        {
            endpoint = null;
            error = null;
            if (request == null) { error = "生图请求为空"; return false; }
            string provider = NormalizeProvider(request.Provider);
            if (provider != ProviderDoubao && provider != ProviderOpenAI
                && provider != ProviderOpenRouter && provider != ProviderComfyUI)
            { error = "不支持的生图协议"; return false; }
            if (!Uri.TryCreate((request.Endpoint ?? "").Trim(), UriKind.Absolute, out endpoint)
                || (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
            { error = "生图接口必须是完整的 HTTP(S) 地址"; return false; }
            if (endpoint.UserInfo.Length > 0 || !string.IsNullOrEmpty(endpoint.Query)
                || !string.IsNullOrEmpty(endpoint.Fragment) || SecretRedactor.ContainsHeaderBreak(request.Endpoint))
            { error = "生图接口不能包含凭据、查询参数、片段或换行"; return false; }
            if (endpoint.Scheme == Uri.UriSchemeHttp && !endpoint.IsLoopback)
            { error = "远程生图接口必须使用 HTTPS"; return false; }
            if (provider == ProviderComfyUI && !endpoint.IsLoopback)
            { error = "ComfyUI 生图只允许连接本机回环地址"; return false; }
            if (string.IsNullOrWhiteSpace(request.ApiKey) && !endpoint.IsLoopback)
            { error = "未配置生图 API Key"; return false; }
            if ((provider != ProviderComfyUI && string.IsNullOrWhiteSpace(request.Model))
                || (request.Model ?? "").Length > 256
                || SecretRedactor.ContainsHeaderBreak(request.Model))
            { error = "生图模型为空或无效"; return false; }
            if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > MaxPromptChars)
            { error = "生图提示词为空或超过 12000 字"; return false; }
            if (request.ReferencePng == null || request.ReferencePng.Length < 64
                || request.ReferencePng.Length > MaxReferenceBytes || !LooksLikePng(request.ReferencePng))
            { error = "人物参考图不是有效 PNG，或超过 16 MiB"; return false; }
            if (provider == ProviderComfyUI
                && !ComfyUiImageGenerationClient.TryValidateWorkflow(
                    request.WorkflowJson, request.Model, request.Prompt, "reference.png", out _, out error))
                return false;
            return true;
        }

        public static async Task<ImageGenerationResult> GenerateAsync(
            ImageGenerationRequest request, CancellationToken cancellationToken)
        {
            if (!TryValidate(request, out Uri endpoint, out string error)) return Fail(error);
            string provider = NormalizeProvider(request.Provider);
            string key = (request.ApiKey ?? "").Trim();
            if (provider == ProviderComfyUI)
                return await ComfyUiImageGenerationClient.GenerateAsync(request, endpoint, cancellationToken)
                    .ConfigureAwait(false);
            try
            {
                using (var message = new HttpRequestMessage(HttpMethod.Post, endpoint))
                {
                    if (key.Length > 0)
                        message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                    message.Content = BuildHttpContent(request, provider);
                    using (HttpResponseMessage response = await Http.SendAsync(
                        message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                    {
                        byte[] raw = await ReadBoundedAsync(response.Content, MaxResponseBytes, cancellationToken)
                            .ConfigureAwait(false);
                        if (!TryDecodeUtf8(raw, out string text)) return Fail("生图服务返回了无效 UTF-8 数据");
                        JObject root;
                        try { root = JObject.Parse(text); }
                        catch { return Fail("生图服务返回非 JSON（HTTP " + (int)response.StatusCode + "）"); }
                        if (!response.IsSuccessStatusCode)
                            return Fail("生图 HTTP " + (int)response.StatusCode + "：" +
                                SafeProviderError(ExtractError(root), key, request.Prompt));
                        string b64 = root["data"]?[0]?["b64_json"]?.ToString();
                        if (string.IsNullOrWhiteSpace(b64)) return Fail("生图服务没有返回 b64_json 图片");
                        if (b64.Length > ((MaxImageBytes + 2L) / 3L) * 4L + 8L || HasWhitespace(b64))
                            return Fail("生图结果超过 32 MiB 或 Base64 无效");
                        byte[] bytes;
                        try { bytes = Convert.FromBase64String(b64); }
                        catch { return Fail("生图结果不是有效 Base64"); }
                        if (bytes.Length < 64 || bytes.Length > MaxImageBytes)
                            return Fail("生图结果为空或超过 32 MiB");
                        string mediaType = root["data"]?[0]?["media_type"]?.ToString();
                        if (string.IsNullOrWhiteSpace(mediaType)) mediaType = DetectMediaType(bytes);
                        if (mediaType == null) return Fail("生图结果不是受支持的 PNG/JPEG 图片");
                        return new ImageGenerationResult { Ok = true, Bytes = bytes, MediaType = mediaType };
                    }
                }
            }
            catch (OperationCanceledException) { return Fail("生图已取消或超时"); }
            catch (InvalidDataException ex) { return Fail(ex.Message); }
            catch (Exception ex)
            {
                return Fail(SafeProviderError(ex.GetBaseException().Message, key, request.Prompt));
            }
        }

        static JObject BuildDoubaoBody(ImageGenerationRequest request)
        {
            return new JObject
            {
                ["model"] = request.Model.Trim(),
                ["prompt"] = request.Prompt,
                ["image"] = new JArray("data:image/png;base64," + Convert.ToBase64String(request.ReferencePng)),
                ["response_format"] = "b64_json",
                ["size"] = FixedLandscapeSize,
                ["stream"] = false,
                ["watermark"] = request.Watermark
            };
        }

        static JObject BuildOpenRouterBody(ImageGenerationRequest request)
        {
            return new JObject
            {
                ["model"] = request.Model.Trim(),
                ["prompt"] = request.Prompt,
                ["n"] = 1,
                ["stream"] = false,
                ["output_format"] = "png",
                ["aspect_ratio"] = FixedLandscapeAspectRatio,
                ["input_references"] = new JArray(new JObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JObject
                    {
                        ["url"] = "data:image/png;base64," + Convert.ToBase64String(request.ReferencePng)
                    }
                })
            };
        }

        internal static HttpContent BuildHttpContent(ImageGenerationRequest request, string provider = null)
        {
            string normalized = NormalizeProvider(provider ?? request?.Provider);
            if (normalized == ProviderOpenAI) return BuildOpenAIContent(request);
            JObject body = normalized == ProviderDoubao
                ? BuildDoubaoBody(request)
                : BuildOpenRouterBody(request);
            byte[] json = Encoding.UTF8.GetBytes(body.ToString(Newtonsoft.Json.Formatting.None));
            if (json.Length > 24 * 1024 * 1024)
                throw new InvalidDataException("生图请求超过 24 MiB 安全上限");
            var content = new ByteArrayContent(json);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            { CharSet = "utf-8" };
            return content;
        }

        static MultipartFormDataContent BuildOpenAIContent(ImageGenerationRequest request)
        {
            // GPT Image reference workflows use the official image edit multipart
            // contract. gpt-image-2 always applies high input fidelity, so the
            // unsupported input_fidelity field is intentionally omitted.
            var form = new MultipartFormDataContent("jhyl-" + Guid.NewGuid().ToString("N"));
            try
            {
                AddUtf8FormField(form, "model", request.Model.Trim());
                AddUtf8FormField(form, "prompt", request.Prompt);
                AddUtf8FormField(form, "n", "1");
                AddUtf8FormField(form, "size", FixedLandscapeSize);
                AddUtf8FormField(form, "output_format", "png");
                var image = new ByteArrayContent(request.ReferencePng);
                image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                form.Add(image, "image[]", "reference.png");
                return form;
            }
            catch
            {
                form.Dispose();
                throw;
            }
        }

        static void AddUtf8FormField(MultipartFormDataContent form, string name, string value)
        {
            var field = new StringContent(value ?? string.Empty, Encoding.UTF8);
            form.Add(field, name);
        }

        static string ExtractError(JObject root)
        {
            return root?["error"]?["message"]?.ToString()
                ?? root?["error"]?.ToString(Newtonsoft.Json.Formatting.None)
                ?? root?["message"]?.ToString()
                ?? "服务端拒绝请求";
        }

        static string SafeProviderError(string value, string key, string prompt)
        {
            string safe = SecretRedactor.Redact(value ?? "生图请求失败", key, prompt);
            safe = safe.Replace((prompt ?? ""), "[PROMPT_REDACTED]");
            safe = safe.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (safe.Length > 800) safe = safe.Substring(0, 800) + "…";
            return safe.Length == 0 ? "生图请求失败" : safe;
        }

        static string NormalizeProvider(string value)
        {
            string v = (value ?? "").Trim().ToLowerInvariant();
            if (v.Length == 0 || v == ProviderDoubao) return ProviderDoubao;
            if (v == ProviderOpenAI) return ProviderOpenAI;
            if (v == ProviderOpenRouter) return ProviderOpenRouter;
            if (v == ProviderComfyUI) return ProviderComfyUI;
            return v;
        }

        public static bool IsSupportedProvider(string value)
        {
            string provider = NormalizeProvider(value);
            return provider == ProviderDoubao || provider == ProviderOpenAI
                || provider == ProviderOpenRouter || provider == ProviderComfyUI;
        }

        public static bool TryValidateComfyUiWorkflow(string workflowJson, string model, out string error)
        {
            return ComfyUiImageGenerationClient.TryValidateWorkflow(workflowJson, model,
                "配置校验提示词", "reference.png", out _, out error);
        }

        public static string FixedSizeForProvider(string provider)
        {
            string normalized = NormalizeProvider(provider);
            if (normalized == ProviderOpenRouter) return FixedLandscapeAspectRatio;
            if (normalized == ProviderComfyUI) return LocalLandscapeSize;
            return FixedLandscapeSize;
        }

        static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken ct)
        {
            if (content == null) return Array.Empty<byte>();
            long? declared = content.Headers.ContentLength;
            if (declared.HasValue && (declared.Value < 0 || declared.Value > maximum))
                throw new InvalidDataException("生图响应 Content-Length 超过安全上限");
            using (Stream input = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var output = new MemoryStream(declared.HasValue ? (int)declared.Value : 81920))
            {
                byte[] buffer = new byte[81920];
                int total = 0;
                while (true)
                {
                    int read = await input.ReadAsync(buffer, 0, Math.Min(buffer.Length, maximum - total + 1), ct)
                        .ConfigureAwait(false);
                    if (read <= 0) break;
                    total += read;
                    if (total > maximum) throw new InvalidDataException("生图响应正文超过安全上限");
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
        }

        static bool TryDecodeUtf8(byte[] bytes, out string text)
        {
            try { text = StrictUtf8.GetString(bytes ?? Array.Empty<byte>()); return true; }
            catch { text = null; return false; }
        }

        static bool HasWhitespace(string value)
        {
            foreach (char c in value) if (char.IsWhiteSpace(c)) return true;
            return false;
        }

        static bool LooksLikePng(byte[] bytes)
        {
            return bytes != null && bytes.Length >= 24
                && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
                && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;
        }

        public static bool TryGetImageDimensions(byte[] bytes, out int width, out int height)
        {
            width = 0; height = 0;
            if (LooksLikePng(bytes))
            {
                width = ReadBigEndianInt32(bytes, 16);
                height = ReadBigEndianInt32(bytes, 20);
                return width > 0 && height > 0;
            }
            if (bytes == null || bytes.Length < 12 || bytes[0] != 0xFF || bytes[1] != 0xD8) return false;
            int index = 2;
            while (index + 8 < bytes.Length)
            {
                if (bytes[index] != 0xFF) { index++; continue; }
                while (index < bytes.Length && bytes[index] == 0xFF) index++;
                if (index >= bytes.Length) return false;
                int marker = bytes[index++];
                if (marker == 0xD8 || marker == 0xD9) continue;
                if (index + 1 >= bytes.Length) return false;
                int length = (bytes[index] << 8) | bytes[index + 1];
                if (length < 2 || index + length > bytes.Length) return false;
                bool startOfFrame = marker >= 0xC0 && marker <= 0xCF
                    && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (startOfFrame)
                {
                    if (length < 7) return false;
                    height = (bytes[index + 3] << 8) | bytes[index + 4];
                    width = (bytes[index + 5] << 8) | bytes[index + 6];
                    return width > 0 && height > 0;
                }
                index += length;
            }
            return false;
        }

        static int ReadBigEndianInt32(byte[] bytes, int offset)
        {
            if (bytes == null || offset < 0 || offset + 3 >= bytes.Length) return 0;
            long value = ((long)bytes[offset] << 24) | ((long)bytes[offset + 1] << 16)
                | ((long)bytes[offset + 2] << 8) | bytes[offset + 3];
            return value > int.MaxValue ? 0 : (int)value;
        }

        static string DetectMediaType(byte[] bytes)
        {
            if (LooksLikePng(bytes)) return "image/png";
            if (bytes != null && bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8
                && bytes[bytes.Length - 2] == 0xFF && bytes[bytes.Length - 1] == 0xD9)
                return "image/jpeg";
            return null;
        }

        static ImageGenerationResult Fail(string error)
        {
            return new ImageGenerationResult
            {
                Ok = false,
                Error = string.IsNullOrWhiteSpace(error) ? "生图请求失败" : error
            };
        }
    }
}
