using System.IO;
using JianghuYouling.Core.Persistence;
using JianghuYouling.Core.Prompt;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JianghuYouling
{
    /// <summary>全局 NPC 隐藏思考表达提示词；以单一严格 JSON 文档耐久提交。</summary>
    internal static class ThinkingPromptRuleStore
    {
        private const int CurrentVersion = 1;
        private const string FileName = "thinking_prompt_rule.json";
        private static readonly object Gate = new object();

        public static string Load()
        {
            lock (Gate) return LoadFromDirectory(JianghuYoulingPaths.Settings);
        }

        public static string LoadForPrompt() => Load();

        public static bool Save(string prompt)
        {
            if (!ThinkingPromptRule.TryValidate(prompt, out _)) return false;
            lock (Gate) return SaveToDirectory(JianghuYoulingPaths.Settings, prompt.Trim());
        }

        public static bool Reset() => Save(ThinkingPromptRule.DefaultPrompt);

        internal static string LoadFromDirectory(string settingsDirectory)
        {
            string path = Path.Combine(settingsDirectory ?? string.Empty, FileName);
            if (!DurableSettingsStore.TryLoad(path, DurableSettingsStore.Text,
                IsValidDocument, out string raw)
                || !TryParse(raw, out string prompt))
                return ThinkingPromptRule.DefaultPrompt;
            return prompt;
        }

        internal static bool SaveFromDirectory(string settingsDirectory, string prompt)
        {
            if (!ThinkingPromptRule.TryValidate(prompt, out _)) return false;
            lock (Gate) return SaveToDirectory(settingsDirectory, prompt.Trim());
        }

        internal static bool ResetFromDirectory(string settingsDirectory)
            => SaveFromDirectory(settingsDirectory, ThinkingPromptRule.DefaultPrompt);

        private static bool SaveToDirectory(string settingsDirectory, string prompt)
        {
            var json = new JObject
            {
                ["version"] = CurrentVersion,
                ["prompt"] = prompt,
            };
            string raw = json.ToString(Formatting.None);
            string path = Path.Combine(settingsDirectory ?? string.Empty, FileName);
            return DurableSettingsStore.Save(path, raw, DurableSettingsStore.Text,
                IsValidDocument);
        }

        private static bool IsValidDocument(string raw) => TryParse(raw, out _);

        private static bool TryParse(string raw, out string prompt)
        {
            prompt = null;
            try
            {
                if (string.IsNullOrWhiteSpace(raw) || raw.IndexOf('\0') >= 0
                    || !DurableFileStore.TryParseJsonStrict(raw, 8, out JToken root)
                    || !(root is JObject json)
                    || !DurableFileStore.HasOnlyProperties(json, "version", "prompt")
                    || json["version"]?.Type != JTokenType.Integer
                    || json["prompt"]?.Type != JTokenType.String
                    || json["version"].Value<int>() != CurrentVersion)
                    return false;
                string value = json["prompt"].Value<string>();
                if (!ThinkingPromptRule.TryValidate(value, out _)) return false;
                prompt = value.Trim();
                return true;
            }
            catch { return false; }
        }
    }
}
