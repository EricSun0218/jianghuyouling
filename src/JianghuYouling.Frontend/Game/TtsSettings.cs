using System;
using System.IO;
using JianghuYouling.Core.Persistence;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JianghuYouling
{
    /// <summary>语音可调参数。严格 JSON，经 main/tmp/bak 恢复与 Flush(true) 原子发布。</summary>
    public sealed class TtsSettings
    {
        const int MaxFileBytes = 16 * 1024;
        public string Voice = "";
        public float Speed = 1.0f;
        public float Vol = 1.0f;
        public int Pitch = 0;
        public string Emotion = "";
        public string Model = "";
        public bool Dynamic = true;
        public bool DialogueOnly = false;

        static string PathFor() => Path.Combine(JianghuYoulingPaths.Settings, "tts_params.json");

        public static TtsSettings Load()
        {
            return TryLoad(out var settings) ? settings : new TtsSettings();
        }

        public static bool TryLoad(out TtsSettings settings)
        {
            settings = new TtsSettings();
            string path = PathFor();
            bool any = File.Exists(path) || File.Exists(path + ".tmp") || File.Exists(path + ".bak");
            if (!any) return true;
            if (!DurableFileStore.TryReadRecoverableText(path, MaxFileBytes, IsValidDocument,
                    out string json, out _, out _)) return false;
            if (!TryParse(json, out var parsed)) return false;
            settings = parsed;
            return true;
        }

        public bool Save()
        {
            string normalizedEmotion = (Emotion ?? "").Trim().ToLowerInvariant();
            var document = new JObject
            {
                ["voice"] = Voice ?? "",
                ["speed"] = Speed,
                ["vol"] = Vol,
                ["pitch"] = Pitch,
                ["emotion"] = normalizedEmotion,
                ["model"] = Model ?? "",
                ["dynamic"] = Dynamic,
                ["dialogueOnly"] = DialogueOnly,
            };
            string json = document.ToString(Formatting.None);
            return DurableFileStore.TryWriteTextAtomic(PathFor(), json, MaxFileBytes, IsValidDocument);
        }

        static bool TryParse(string json, out TtsSettings settings)
        {
            settings = null;
            if (!DurableFileStore.TryParseJsonStrict(json, 8, out JToken root) || !(root is JObject o) ||
                !DurableFileStore.HasOnlyProperties(o, "voice", "speed", "vol", "pitch", "emotion", "model", "dynamic", "dialogueOnly"))
                return false;
            if (!TryBoundedString(o["voice"], 256, out string voice) ||
                !TryBoundedString(o["emotion"], 32, out string emotion) ||
                !TryBoundedString(o["model"], 256, out string model) ||
                !TryFloat(o["speed"], 0.5f, 2f, 1f, out float speed) ||
                !TryFloat(o["vol"], 0f, 2f, 1f, out float vol) ||
                !TryInt(o["pitch"], -12, 12, 0, out int pitch) ||
                !TryBool(o["dynamic"], true, out bool dynamic) ||
                !TryBool(o["dialogueOnly"], false, out bool dialogueOnly) ||
                !IsAllowedEmotion(emotion)) return false;

            settings = new TtsSettings
            {
                Voice = voice,
                Speed = speed,
                Vol = vol,
                Pitch = pitch,
                Emotion = (emotion ?? "").Trim().ToLowerInvariant(),
                Model = model,
                Dynamic = dynamic,
                DialogueOnly = dialogueOnly,
            };
            return true;
        }

        static bool IsValidDocument(string json) => TryParse(json, out _);

        static bool TryBoundedString(JToken token, int maxChars, out string value)
        {
            value = "";
            if (token == null) return true;
            if (token.Type != JTokenType.String) return false;
            value = token.Value<string>() ?? "";
            return value.Length <= maxChars && value.IndexOf('\0') < 0 && value.IndexOf('\r') < 0 && value.IndexOf('\n') < 0;
        }

        static bool TryFloat(JToken token, float min, float max, float fallback, out float value)
        {
            value = fallback;
            if (token == null) return true;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float) return false;
            try
            {
                double d = token.Value<double>();
                if (double.IsNaN(d) || double.IsInfinity(d) || d < min || d > max) return false;
                value = (float)d;
                return true;
            }
            catch { return false; }
        }

        static bool TryInt(JToken token, int min, int max, int fallback, out int value)
        {
            value = fallback;
            if (token == null) return true;
            if (token.Type != JTokenType.Integer) return false;
            try
            {
                long number = token.Value<long>();
                if (number < min || number > max) return false;
                value = (int)number;
                return true;
            }
            catch { return false; }
        }

        static bool TryBool(JToken token, bool fallback, out bool value)
        {
            value = fallback;
            if (token == null) return true;
            if (token.Type != JTokenType.Boolean) return false;
            value = token.Value<bool>();
            return true;
        }

        static bool IsAllowedEmotion(string value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "": case "neutral": case "happy": case "sad": case "angry":
                case "fearful": case "disgusted": case "surprised": return true;
                default: return false;
            }
        }
    }
}
