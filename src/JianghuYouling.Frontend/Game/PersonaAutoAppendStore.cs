using System;
using System.IO;

namespace JianghuYouling
{
    /// <summary>全局自定义人设自动补充开关。关闭只停止自动写入，不删除或屏蔽已有补充。</summary>
    public static class PersonaAutoAppendStore
    {
        public static string SettingsPath
            => Path.Combine(JianghuYoulingPaths.Settings, "persona_auto_append.txt");

        internal static event Action Disabled;

        private static bool IsBoolean(string value) => value == "0" || value == "1";

        public static bool Load()
        {
            try
            {
                if (DurableSettingsStore.TryLoad(SettingsPath, DurableSettingsStore.Small,
                    IsBoolean, out string value)) return value == "1";
            }
            catch { }
            return true;
        }

        public static bool Save(bool enabled)
        {
            bool saved;
            try
            {
                saved = DurableSettingsStore.Save(SettingsPath, enabled ? "1" : "0",
                    DurableSettingsStore.Small, IsBoolean);
            }
            catch { return false; }
            if (saved && !enabled) Disabled?.Invoke();
            return saved;
        }
    }
}
