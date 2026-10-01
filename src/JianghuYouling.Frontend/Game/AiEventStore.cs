using System.IO;

namespace JianghuYouling
{
    /// <summary>每月「AI 江湖事件」开关(默认开)。关掉则过月不再生成江湖大事;同道主动行事由 CompanionMonthlyStore 单独控制。文件存设置目录。</summary>
    public static class AiEventStore
    {
        public static string SettingsPath => Path.Combine(JianghuYoulingPaths.Settings, "ai_event.txt");
        static string PathFor() => SettingsPath;

        public static bool Load()
        {
            try
            {
                var p = PathFor();
                if (DurableSettingsStore.TryLoad(p, DurableSettingsStore.Small,
                    DurableSettingsStore.IsLegacyBoolean, out string raw))
                {
                    string s = raw.Trim().ToLowerInvariant();
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
