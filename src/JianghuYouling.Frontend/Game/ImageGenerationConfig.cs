using System;
using System.IO;
using System.Threading;
using JianghuYouling.Core.Security;
using JianghuYouling.Core.Web;
using Newtonsoft.Json.Linq;

namespace JianghuYouling
{
    /// <summary>生图使用独立 DPAPI 凭据，不复用聊天或配音 Key。</summary>
    public static class ImageGenerationConfig
    {
        public const string DefaultProvider = ImageGenerationClient.ProviderDoubao;
        public const string DefaultEndpoint = "https://ark.cn-beijing.volces.com/api/v3/images/generations";
        public const string DefaultModel = "doubao-seedream-5-0-pro-260628";
        public const string DefaultSize = ImageGenerationClient.FixedLandscapeSize;
        public const int MaxStyleChars = 4000;
        public const int MaxWorkflowPathChars = 1024;

        static long _revision = 1;
        static string PathFor() => Path.Combine(JianghuYoulingPaths.Settings, "image_generation.json");
        public static string DefaultComfyWorkflowPath =>
            Path.Combine(JianghuYoulingPaths.Settings, "comfyui_workflow_api.json");

        public static string LastSaveError { get; private set; }
        public static long Revision => Interlocked.Read(ref _revision);

        public sealed class Values
        {
            public string Provider = DefaultProvider;
            public string Endpoint = DefaultEndpoint;
            public string ApiKey = "";
            public string Model = DefaultModel;
            public string Size = DefaultSize;
            public string Style = "";
            public bool Watermark;
            public string WorkflowPath = DefaultComfyWorkflowPath;
            public string WorkflowJson = "";
        }

        public static Values LoadRaw()
        {
            var result = new Values();
            try
            {
                string path = PathFor();
                if (!ProtectedConfigFile.TryGetReplicaPresence(path, out bool any, out _) || !any)
                    return result;
                if (!ProtectedConfigFile.TryLoad(path, out JObject o, out string key, out _)) return result;
                result.Provider = Text(o, "provider", DefaultProvider);
                result.Endpoint = Text(o, "endpoint", DefaultEndpoint);
                result.ApiKey = key ?? "";
                result.Model = Text(o, "model", DefaultModel);
                // 旧配置中的 1K/2K/4K 或任意宽高值不再沿用，避免参考图比例
                // 影响服务端自适应输出，所有新请求固定为横向 16:9。
                result.Size = ImageGenerationClient.FixedSizeForProvider(result.Provider);
                result.Style = (o?["style"]?.ToString() ?? "").Trim();
                if (result.Style.Length > MaxStyleChars) result.Style = result.Style.Substring(0, MaxStyleChars);
                result.Watermark = o?["watermark"]?.Value<bool?>() ?? false;
                result.WorkflowPath = Text(o, "workflowPath", DefaultComfyWorkflowPath);
            }
            catch { }
            return result;
        }

        public static Values Resolve()
        {
            return TryResolve(out Values value, out _) ? value : null;
        }

        public static bool TryResolve(out Values value, out string error)
        {
            value = LoadRaw();
            error = null;
            string provider = (value.Provider ?? "").Trim();
            if (!ImageGenerationClient.IsSupportedProvider(provider))
            { error = "生图协议无效"; value = null; return false; }
            if (!Uri.TryCreate((value.Endpoint ?? "").Trim(), UriKind.Absolute, out Uri endpoint)
                || (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
            { error = "生图接口不是完整的 HTTP(S) 地址"; value = null; return false; }
            if (endpoint.Scheme == Uri.UriSchemeHttp && !endpoint.IsLoopback)
            { error = "远程生图接口必须使用 HTTPS"; value = null; return false; }
            if (string.IsNullOrWhiteSpace(value.ApiKey) && !endpoint.IsLoopback)
            { error = "远程生图接口未配置独立 API Key"; value = null; return false; }
            bool comfy = string.Equals(provider, ImageGenerationClient.ProviderComfyUI,
                StringComparison.OrdinalIgnoreCase);
            if (comfy && !endpoint.IsLoopback)
            { error = "ComfyUI 只允许连接本机回环地址"; value = null; return false; }
            if (!comfy && string.IsNullOrWhiteSpace(value.Model))
            { error = "生图模型为空"; value = null; return false; }
            if (comfy && !TryReadWorkflow(value.WorkflowPath, out value.WorkflowJson, out error))
            { value = null; return false; }
            if (!TryValidateValues(value, out error))
            { value = null; return false; }
            return true;
        }

        public static bool IsConfigured => TryResolve(out _, out _);

        public static bool SaveExplicit(Values value)
        {
            LastSaveError = null;
            value = value ?? new Values();
            // 空 Key 既可能表示“从未启用/玩家明确清空”，也可能只是已有 DPAPI 配置
            // 暂时不可读。后一种情况绝不能把可恢复副本覆盖成空配置。
            if (string.IsNullOrWhiteSpace(value.ApiKey))
            {
                string protectedPath = PathFor();
                if (!ProtectedConfigFile.TryGetReplicaPresence(protectedPath,
                    out bool anyReplica, out string presenceError))
                {
                    LastSaveError = presenceError ?? "无法确认已有生图配置状态";
                    return false;
                }
                if (anyReplica && !ProtectedConfigFile.TryLoad(protectedPath,
                    out _, out _, out string loadError))
                {
                    LastSaveError = "已有生图配置无法可靠读取，已保留原文件："
                        + (loadError ?? "请检查文件权限或恢复副本");
                    return false;
                }
            }
            string style = (value.Style ?? "").Trim();
            if (style.Length > MaxStyleChars)
            {
                LastSaveError = "生图风格说明超过 " + MaxStyleChars + " 字";
                return false;
            }
            string provider = string.IsNullOrWhiteSpace(value.Provider)
                ? DefaultProvider : value.Provider.Trim();
            if (!ImageGenerationClient.IsSupportedProvider(provider))
            {
                LastSaveError = "不支持的生图协议";
                return false;
            }
            string workflowPath = (value.WorkflowPath ?? "").Trim();
            if (workflowPath.Length > MaxWorkflowPathChars || ContainsControl(workflowPath))
            {
                LastSaveError = "ComfyUI 工作流路径无效";
                return false;
            }
            string workflowForValidation = "";
            if (string.Equals(provider, ImageGenerationClient.ProviderComfyUI,
                StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadWorkflow(workflowPath, out string workflowJson, out string workflowError))
                { LastSaveError = workflowError; return false; }
                if (!ImageGenerationClient.TryValidateComfyUiWorkflow(
                    workflowJson, value.Model, out workflowError))
                { LastSaveError = workflowError; return false; }
                workflowForValidation = workflowJson;
            }
            value.Provider = provider;
            value.WorkflowPath = workflowPath;
            value.WorkflowJson = workflowForValidation;
            // 配置页允许把远程生图保持在“尚未启用”状态（Key 为空）并保存其它设置。
            // 这里只给内存中的校验请求临时补非空值；落盘仍保存真实空 Key，运行时
            // TryResolve 也继续 fail-closed，直到玩家真正配置凭据。
            if (!TryValidateValues(value, out string validationError,
                allowMissingRemoteApiKeyForSave: true))
            { LastSaveError = validationError; return false; }
            var metadata = new JObject
            {
                ["provider"] = provider,
                ["endpoint"] = (value.Endpoint ?? "").Trim(),
                ["model"] = (value.Model ?? "").Trim(),
                ["size"] = ImageGenerationClient.FixedSizeForProvider(provider),
                ["style"] = style,
                ["watermark"] = value.Watermark
                , ["workflowPath"] = workflowPath
            };
            bool ok = ProtectedConfigFile.TrySave(PathFor(), metadata, (value.ApiKey ?? "").Trim(), out string error);
            LastSaveError = error;
            if (ok) Interlocked.Increment(ref _revision);
            return ok;
        }

        static string Text(JObject o, string name, string fallback)
        {
            string value = o?[name]?.ToString();
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        static bool TryReadWorkflow(string path, out string json, out string error)
        {
            json = null;
            error = null;
            path = (path ?? "").Trim();
            if (path.Length == 0)
            { error = "未填写 ComfyUI 工作流 API JSON 路径"; return false; }
            try
            {
                string full = Path.GetFullPath(path);
                var info = new FileInfo(full);
                if (!info.Exists)
                { error = "找不到 ComfyUI 工作流文件：" + full; return false; }
                if (info.Length <= 1 || info.Length > ImageGenerationClient.MaxComfyUiWorkflowBytes)
                { error = "ComfyUI 工作流为空或超过 8 MB"; return false; }
                byte[] bytes = File.ReadAllBytes(full);
                var utf8 = new System.Text.UTF8Encoding(false, true);
                json = utf8.GetString(bytes);
                if (json.Length > 0 && json[0] == '\uFEFF') json = json.Substring(1);
                return true;
            }
            catch (Exception ex)
            { error = "读取 ComfyUI 工作流失败：" + ex.GetBaseException().Message; return false; }
        }

        static bool ContainsControl(string value)
        {
            foreach (char c in value ?? "") if (char.IsControl(c)) return true;
            return false;
        }

        static bool TryValidateValues(Values value, out string error,
            bool allowMissingRemoteApiKeyForSave = false)
        {
            byte[] reference = new byte[64];
            byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            Buffer.BlockCopy(signature, 0, reference, 0, signature.Length);
            string apiKey = value.ApiKey;
            if (allowMissingRemoteApiKeyForSave && string.IsNullOrWhiteSpace(apiKey)
                && Uri.TryCreate((value.Endpoint ?? string.Empty).Trim(), UriKind.Absolute,
                    out Uri endpoint)
                && !endpoint.IsLoopback)
                apiKey = string.Concat("configuration", "-", "validation", "-", "only");
            return ImageGenerationClient.TryValidate(new ImageGenerationRequest
            {
                Provider = value.Provider,
                Endpoint = value.Endpoint,
                ApiKey = apiKey,
                Model = value.Model,
                Prompt = "配置校验提示词",
                ReferencePng = reference,
                WorkflowJson = value.WorkflowJson,
                Size = ImageGenerationClient.FixedSizeForProvider(value.Provider),
                Watermark = value.Watermark
            }, out _, out error);
        }
    }
}
