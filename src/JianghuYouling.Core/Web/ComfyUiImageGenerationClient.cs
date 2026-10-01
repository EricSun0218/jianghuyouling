using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JianghuYouling.Core.Llm;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Web
{
    /// <summary>
    /// Thin adapter for the stock ComfyUI HTTP API. The workflow graph is supplied by
    /// the host and only exact value markers are substituted, so model text cannot
    /// invent nodes, paths or executable workflow structure.
    /// </summary>
    internal static class ComfyUiImageGenerationClient
    {
        const string PromptMarker = "{{JHYL_PROMPT}}";
        const string PromptMarkerCompact = "{JHYL_PROMPT}";
        const string ReferenceMarker = "{{JHYL_REFERENCE_IMAGE}}";
        const string ReferenceMarkerCompact = "{JHYL_REFERENCE_IMAGE}";
        const string ModelMarker = "{{JHYL_MODEL}}";
        const string ModelMarkerCompact = "{JHYL_MODEL}";
        const string WidthMarker = "{{JHYL_WIDTH}}";
        const string WidthMarkerCompact = "{JHYL_WIDTH}";
        const string HeightMarker = "{{JHYL_HEIGHT}}";
        const string HeightMarkerCompact = "{JHYL_HEIGHT}";
        internal const int MaxWorkflowBytes = ImageGenerationClient.MaxComfyUiWorkflowBytes;
        const int MaxResponseBytes = ImageGenerationClient.MaxResponseBytes;
        const int MaxPollAttempts = 960;
        static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
        static readonly TimeSpan TotalTimeout = TimeSpan.FromMinutes(8);
        static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        static readonly HttpClient Http = CreateHttp();

        static HttpClient CreateHttp()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TotalTimeout
            };
        }

        internal static bool TryValidateWorkflow(string workflowJson, string model, string prompt,
            string referenceName, out JObject workflow, out string error)
        {
            workflow = null;
            error = null;
            if (string.IsNullOrWhiteSpace(workflowJson)
                || Encoding.UTF8.GetByteCount(workflowJson) > MaxWorkflowBytes)
            { error = "ComfyUI 工作流为空或超过 8 MB"; return false; }
            try
            {
                using (var reader = new JsonTextReader(new StringReader(workflowJson))
                {
                    MaxDepth = 128,
                    DateParseHandling = DateParseHandling.None,
                    FloatParseHandling = FloatParseHandling.Decimal
                })
                {
                    JToken parsed = JToken.ReadFrom(reader, new JsonLoadSettings
                    {
                        CommentHandling = CommentHandling.Ignore,
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                        LineInfoHandling = LineInfoHandling.Ignore
                    });
                    workflow = parsed as JObject;
                }
            }
            catch (Exception ex)
            { error = "ComfyUI 工作流不是有效的 API JSON：" + ex.GetBaseException().Message; return false; }
            if (workflow == null)
            { error = "ComfyUI 工作流根节点必须是对象"; return false; }

            int promptMarkers = 0, referenceMarkers = 0, modelMarkers = 0;
            int widthMarkers = 0, heightMarkers = 0, unknownMarkers = 0;
            ReplaceMarkers(workflow, model ?? "", prompt ?? "", referenceName ?? "reference.png",
                ref promptMarkers, ref referenceMarkers, ref modelMarkers, ref widthMarkers, ref heightMarkers,
                ref unknownMarkers);
            if (promptMarkers == 0 || referenceMarkers == 0 || widthMarkers == 0 || heightMarkers == 0)
            {
                error = "ComfyUI 工作流必须包含提示词、参考图、宽度和高度四个 JHYL 标记";
                workflow = null;
                return false;
            }
            if (modelMarkers > 0 && string.IsNullOrWhiteSpace(model))
            { error = "ComfyUI 工作流使用了模型标记，但生图模型为空"; workflow = null; return false; }
            if (unknownMarkers > 0)
            { error = "ComfyUI 工作流含有不受支持或未替换的 JHYL 标记"; workflow = null; return false; }
            string rendered = workflow.ToString(Formatting.None);
            if (Encoding.UTF8.GetByteCount(rendered) > MaxWorkflowBytes)
            { error = "替换后的 ComfyUI 工作流超过 8 MB"; workflow = null; return false; }
            return true;
        }

        static void ReplaceMarkers(JToken token, string model, string prompt, string referenceName,
            ref int promptMarkers, ref int referenceMarkers, ref int modelMarkers,
            ref int widthMarkers, ref int heightMarkers, ref int unknownMarkers)
        {
            if (token is JContainer container)
            {
                foreach (JToken child in new List<JToken>(container.Children()))
                    ReplaceMarkers(child, model, prompt, referenceName, ref promptMarkers, ref referenceMarkers,
                        ref modelMarkers, ref widthMarkers, ref heightMarkers, ref unknownMarkers);
                return;
            }
            if (!(token is JValue value) || value.Type != JTokenType.String) return;
            string text = value.Value<string>();
            switch (text)
            {
                case PromptMarker:
                case PromptMarkerCompact:
                    value.Value = prompt; promptMarkers++; break;
                case ReferenceMarker:
                case ReferenceMarkerCompact:
                    value.Value = referenceName; referenceMarkers++; break;
                case ModelMarker:
                case ModelMarkerCompact:
                    value.Value = model; modelMarkers++; break;
                case WidthMarker:
                case WidthMarkerCompact:
                    value.Value = ImageGenerationClient.LocalLandscapeWidth; widthMarkers++; break;
                case HeightMarker:
                case HeightMarkerCompact:
                    value.Value = ImageGenerationClient.LocalLandscapeHeight; heightMarkers++; break;
                default:
                    if (LooksLikeMarker(text)) unknownMarkers++;
                    break;
            }
        }

        static bool LooksLikeMarker(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 96) return false;
            return (text.StartsWith("{JHYL_", StringComparison.Ordinal)
                    && text.EndsWith("}", StringComparison.Ordinal))
                || (text.StartsWith("{{JHYL_", StringComparison.Ordinal)
                    && text.EndsWith("}}", StringComparison.Ordinal));
        }

        internal static JObject BuildSubmissionBody(JObject workflow, string clientId)
        {
            return new JObject
            {
                ["prompt"] = workflow?.DeepClone() ?? new JObject(),
                ["client_id"] = clientId ?? string.Empty
            };
        }

        public static async Task<ImageGenerationResult> GenerateAsync(
            ImageGenerationRequest request, Uri endpoint, CancellationToken cancellationToken)
        {
            string key = (request.ApiKey ?? "").Trim();
            using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                lifetime.CancelAfter(TotalTimeout);
                try
                {
                    string uploaded = await UploadReferenceAsync(
                        endpoint, key, request.ReferencePng, lifetime.Token).ConfigureAwait(false);
                    if (!TryValidateWorkflow(request.WorkflowJson, request.Model, request.Prompt, uploaded,
                        out JObject workflow, out string workflowError))
                        return Fail(workflowError);
                    string clientId = "jhyl-" + Guid.NewGuid().ToString("N");
                    string promptId = await SubmitAsync(endpoint, key, workflow, clientId, lifetime.Token)
                        .ConfigureAwait(false);
                    for (int attempt = 0; attempt < MaxPollAttempts; attempt++)
                    {
                        lifetime.Token.ThrowIfCancellationRequested();
                        JObject history = await GetJsonAsync(Operation(endpoint,
                            "history/" + Uri.EscapeDataString(promptId)), key, lifetime.Token)
                            .ConfigureAwait(false);
                        JObject entry = history[promptId] as JObject;
                        if (entry == null && (history["outputs"] != null || history["status"] != null))
                            entry = history;
                        if (entry != null)
                        {
                            if (TryFindOutput(entry, out string fileName, out string subfolder, out string type))
                                return await DownloadAsync(endpoint, key, fileName, subfolder, type,
                                    lifetime.Token).ConfigureAwait(false);
                            if (IsFinished(entry, out string finishError))
                                return Fail(finishError ?? "ComfyUI 工作流已结束，但没有返回图片");
                        }
                        if (attempt + 1 < MaxPollAttempts)
                            await Task.Delay(PollInterval, lifetime.Token).ConfigureAwait(false);
                    }
                    return Fail("ComfyUI 生图等待超时");
                }
                catch (OperationCanceledException)
                { return Fail(cancellationToken.IsCancellationRequested ? "生图已取消" : "ComfyUI 生图等待超时"); }
                catch (InvalidDataException ex) { return Fail(ex.Message); }
                catch (Exception ex)
                { return Fail(SafeError(ex.GetBaseException().Message, key, request.Prompt)); }
            }
        }

        static async Task<string> UploadReferenceAsync(Uri endpoint, string key, byte[] bytes,
            CancellationToken cancellationToken)
        {
            using (var form = new MultipartFormDataContent("jhyl-" + Guid.NewGuid().ToString("N")))
            {
                var image = new ByteArrayContent(bytes);
                image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                form.Add(image, "image", "jhyl-reference-" + Guid.NewGuid().ToString("N") + ".png");
                form.Add(new StringContent("input", Encoding.UTF8), "type");
                form.Add(new StringContent("false", Encoding.UTF8), "overwrite");
                JObject response = await SendJsonAsync(HttpMethod.Post, Operation(endpoint, "upload/image"),
                    key, form, cancellationToken).ConfigureAwait(false);
                return SafeName(response["name"]?.ToString(), "ComfyUI 上传响应缺少有效文件名");
            }
        }

        static async Task<string> SubmitAsync(Uri endpoint, string key, JObject workflow, string clientId,
            CancellationToken cancellationToken)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(BuildSubmissionBody(workflow, clientId).ToString(Formatting.None));
            if (bytes.Length > MaxWorkflowBytes) throw new InvalidDataException("ComfyUI 提交正文超过 8 MB");
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            JObject response = await SendJsonAsync(HttpMethod.Post, Operation(endpoint, "prompt"), key,
                content, cancellationToken).ConfigureAwait(false);
            return SafeName(response["prompt_id"]?.ToString(), "ComfyUI 没有返回任务编号");
        }

        static async Task<JObject> GetJsonAsync(Uri uri, string key, CancellationToken cancellationToken)
        {
            return await SendJsonAsync(HttpMethod.Get, uri, key, null, cancellationToken).ConfigureAwait(false);
        }

        static async Task<JObject> SendJsonAsync(HttpMethod method, Uri uri, string key, HttpContent content,
            CancellationToken cancellationToken)
        {
            using (var message = new HttpRequestMessage(method, uri))
            {
                if (key.Length > 0) message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                message.Content = content;
                using (HttpResponseMessage response = await Http.SendAsync(message,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    if ((int)response.StatusCode >= 300 && (int)response.StatusCode <= 399)
                        throw new InvalidDataException("ComfyUI 拒绝了重定向响应");
                    byte[] raw = await ReadBoundedAsync(response.Content, MaxResponseBytes, cancellationToken)
                        .ConfigureAwait(false);
                    string text;
                    try { text = StrictUtf8.GetString(raw); }
                    catch { throw new InvalidDataException("ComfyUI 返回了无效 UTF-8 数据"); }
                    JObject root;
                    try { root = JObject.Parse(text); }
                    catch { throw new InvalidDataException("ComfyUI 返回非 JSON（HTTP " + (int)response.StatusCode + "）"); }
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidDataException("ComfyUI HTTP " + (int)response.StatusCode + "："
                            + SafeError(ExtractError(root), key, null));
                    return root;
                }
            }
        }

        static bool TryFindOutput(JObject entry, out string fileName, out string subfolder, out string type)
        {
            fileName = subfolder = type = null;
            JObject outputs = entry["outputs"] as JObject;
            if (outputs == null) return false;
            foreach (JProperty node in outputs.Properties())
            {
                JArray images = node.Value?["images"] as JArray;
                if (images == null) continue;
                foreach (JToken image in images)
                {
                    string candidate = image?["filename"]?.ToString();
                    if (string.IsNullOrWhiteSpace(candidate)) continue;
                    fileName = SafeName(candidate, "ComfyUI 输出文件名无效");
                    subfolder = SafeName(image?["subfolder"]?.ToString() ?? "", "ComfyUI 输出目录无效", true);
                    type = SafeName(image?["type"]?.ToString() ?? "output", "ComfyUI 输出类型无效");
                    return true;
                }
            }
            return false;
        }

        static bool IsFinished(JObject entry, out string error)
        {
            error = null;
            JObject status = entry["status"] as JObject;
            if (status == null) return false;
            string state = status["status_str"]?.ToString();
            bool complete = status["completed"]?.Value<bool?>() == true;
            if (string.Equals(state, "error", StringComparison.OrdinalIgnoreCase))
            {
                error = "ComfyUI 工作流执行失败";
                return true;
            }
            return complete || string.Equals(state, "success", StringComparison.OrdinalIgnoreCase);
        }

        static async Task<ImageGenerationResult> DownloadAsync(Uri endpoint, string key, string fileName,
            string subfolder, string type, CancellationToken cancellationToken)
        {
            Uri baseView = Operation(endpoint, "view");
            var builder = new UriBuilder(baseView)
            {
                Query = "filename=" + Uri.EscapeDataString(fileName)
                    + "&subfolder=" + Uri.EscapeDataString(subfolder ?? "")
                    + "&type=" + Uri.EscapeDataString(type ?? "output")
            };
            using (var message = new HttpRequestMessage(HttpMethod.Get, builder.Uri))
            {
                if (key.Length > 0) message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                using (HttpResponseMessage response = await Http.SendAsync(message,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidDataException("ComfyUI 读取图片失败（HTTP " + (int)response.StatusCode + "）");
                    byte[] bytes = await ReadBoundedAsync(response.Content, ImageGenerationClient.MaxImageBytes,
                        cancellationToken).ConfigureAwait(false);
                    string mediaType = DetectImageType(bytes);
                    if (mediaType == null) throw new InvalidDataException("ComfyUI 输出不是受支持的 PNG/JPEG 图片");
                    return new ImageGenerationResult { Ok = true, Bytes = bytes, MediaType = mediaType };
                }
            }
        }

        internal static Uri Operation(Uri endpoint, string operation)
        {
            var builder = new UriBuilder(endpoint);
            string path = (builder.Path ?? "/").TrimEnd('/');
            builder.Path = path + "/" + operation.TrimStart('/');
            builder.Query = string.Empty;
            builder.Fragment = string.Empty;
            return builder.Uri;
        }

        static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken ct)
        {
            if (content == null) return Array.Empty<byte>();
            if (content.Headers.ContentLength.HasValue && content.Headers.ContentLength.Value > maximum)
                throw new InvalidDataException("ComfyUI 响应超过安全上限");
            using (Stream input = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var output = new MemoryStream())
            {
                byte[] buffer = new byte[81920];
                while (true)
                {
                    int remaining = maximum - (int)output.Length;
                    int read = await input.ReadAsync(buffer, 0, Math.Min(buffer.Length, remaining + 1), ct)
                        .ConfigureAwait(false);
                    if (read <= 0) return output.ToArray();
                    if (read > remaining) throw new InvalidDataException("ComfyUI 响应超过安全上限");
                    output.Write(buffer, 0, read);
                }
            }
        }

        static string SafeName(string value, string error, bool allowEmpty = false)
        {
            value = value ?? string.Empty;
            if ((!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Length > 512)
                throw new InvalidDataException(error);
            foreach (char c in value) if (char.IsControl(c)) throw new InvalidDataException(error);
            return value;
        }

        static string ExtractError(JObject root)
        {
            return root?["error"]?["message"]?.ToString()
                ?? root?["error"]?.ToString(Formatting.None)
                ?? root?["node_errors"]?.ToString(Formatting.None)
                ?? root?["message"]?.ToString()
                ?? "服务端拒绝请求";
        }

        static string SafeError(string value, string key, string prompt)
        {
            string safe = SecretRedactor.Redact(value ?? "ComfyUI 请求失败", key, prompt);
            safe = safe.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (safe.Length > 800) safe = safe.Substring(0, 800) + "…";
            return safe.Length == 0 ? "ComfyUI 请求失败" : safe;
        }

        static string DetectImageType(byte[] bytes)
        {
            if (bytes != null && bytes.Length >= 24
                && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
                && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
                return "image/png";
            if (bytes != null && bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8
                && bytes[bytes.Length - 2] == 0xFF && bytes[bytes.Length - 1] == 0xD9)
                return "image/jpeg";
            return null;
        }

        static ImageGenerationResult Fail(string error)
        {
            return new ImageGenerationResult { Ok = false,
                Error = string.IsNullOrWhiteSpace(error) ? "ComfyUI 请求失败" : error };
        }
    }
}
