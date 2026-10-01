using System.IO;

namespace JianghuYouling
{
    /// <summary>悬浮助手「灵儿」总开关(默认开)。关=不创建/不显示挂件、不主动消息。存 Settings/assistant_enabled.txt。</summary>
    public static class AssistantEnabledStore
    {
        private static string PathFor() => Path.Combine(JianghuYoulingPaths.Settings, "assistant_enabled.txt");

        public static bool Load()
        {
            try
            {
                var p = PathFor();
                if (DurableSettingsStore.TryLoad(p, DurableSettingsStore.Small,
                    DurableSettingsStore.IsLegacyBoolean, out string raw))
                {
                    var s = raw.Trim().ToLowerInvariant();
                    return !(s == "0" || s == "off" || s == "false" || s == "关" || s == "否" || s == "no");
                }
            }
            catch { }
            return true;   // 默认开
        }

        public static bool Save(bool on)
        {
            try { return DurableSettingsStore.Save(PathFor(), on ? "1" : "0", DurableSettingsStore.Small,
                DurableSettingsStore.IsLegacyBoolean); }
            catch { return false; }
        }
    }
}
