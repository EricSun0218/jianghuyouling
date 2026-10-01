using System.IO;

namespace JianghuYouling
{
    /// <summary>「太吾口吻」持久化:自由文本,描述主角太吾平日说话的语气/风格(如"豪爽直率""沉默寡言""文绉绉""贫嘴")。
    /// 只影响『代笔』(替太吾拟话)的口吻,不影响 NPC。空=默认。存于 Settings/taiwu_voice.txt。</summary>
    public static class TaiwuVoiceStore
    {
        static string PathFor() => Path.Combine(JianghuYoulingPaths.Settings, "taiwu_voice.txt");

        public static string Load()
        {
            try { var p = PathFor(); if (DurableSettingsStore.TryLoad(p, DurableSettingsStore.Text, IsValid,
                out string value)) return value.Trim(); }
            catch { }
            return "";
        }

        public static bool Save(string s)
        {
            try
            {
                string value = (s ?? "").Trim();
                if (!DurableSettingsStore.Save(PathFor(), value, DurableSettingsStore.Text, IsValid))
                    return false;
                // Keep the recovery replica cleared too; the first commit is already
                // the durable commit point, while this rotation prevents stale revival.
                if (value.Length == 0)
                    DurableSettingsStore.Save(PathFor(), value, DurableSettingsStore.Text, IsValid);
                return true;
            }
            catch { return false; }
        }

        private static bool IsValid(string value) => DurableSettingsStore.IsFreeText(value, 32768);
    }
}
