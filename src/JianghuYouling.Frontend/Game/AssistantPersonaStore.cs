using System.IO;
using JianghuYouling.Core.Persona;

namespace JianghuYouling
{
    /// <summary>悬浮助手「灵儿」的自定义人设(自由文本;空=用内置默认)。存 Settings/assistant_persona.txt。
    /// 注入助手系统提示词,作为她的性格底色/说话风格/自我认知。</summary>
    public static class AssistantPersonaStore
    {
        private const int MaxBytes = 2 * 1024 * 1024;
        private static string PathFor() => Path.Combine(JianghuYoulingPaths.Settings, "assistant_persona.txt");

        public static string Load()
        {
            try { var p = PathFor(); if (DurableSettingsStore.TryLoad(p, MaxBytes, IsValid,
                out string value)) return value.Trim(); }
            catch { }
            return "";
        }

        public static bool Save(string s)
        {
            try
            {
                string value = (s ?? "").Trim();
                bool saved = DurableSettingsStore.Save(PathFor(), value, MaxBytes, IsValid);
                if (!saved) return false;
                return value.Length != 0 || DurableSettingsStore.Save(PathFor(), value, MaxBytes, IsValid);
            }
            catch { return false; }
        }

        private static bool IsValid(string value)
            => DurableSettingsStore.IsFreeText(value, PersonaStore.MaxCustomPersonaChars);
    }
}
