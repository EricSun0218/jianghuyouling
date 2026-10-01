using System.IO;

namespace JianghuYouling
{
    /// <summary>玩家给悬浮助手起的名字(默认「灵儿」)。存 Settings/assistant_name.txt,一行。</summary>
    public static class AssistantNameStore
    {
        public const string Default = "灵儿";
        private static string PathFor() => Path.Combine(JianghuYoulingPaths.Settings, "assistant_name.txt");

        public static string Load()
        {
            try { var p = PathFor(); if (DurableSettingsStore.TryLoad(p, DurableSettingsStore.Small, IsValid,
                out string value)) { var s = value.Trim(); if (s.Length > 0) return s; } }
            catch { }
            return Default;
        }

        public static bool Save(string s)
        {
            try
            {
                var v = (s ?? "").Trim();
                return DurableSettingsStore.Save(PathFor(), v.Length > 0 ? v : Default,
                    DurableSettingsStore.Small, IsValid);
            }
            catch { return false; }
        }

        private static bool IsValid(string value) => DurableSettingsStore.IsSingleLine(value, 64, false)
            && !string.IsNullOrWhiteSpace(value);
    }
}
