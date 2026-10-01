using System.IO;

namespace JianghuYouling
{
    /// <summary>过月完成后是否自动弹出「本月动向」。纪事仍会照常归档并显示未读状态。</summary>
    public static class MonthlyDigestPopupStore
    {
        public static string SettingsPath => Path.Combine(JianghuYoulingPaths.Settings, "monthly_digest_popup.txt");

        public static bool Load()
        {
            try
            {
                if (DurableSettingsStore.TryLoad(SettingsPath, DurableSettingsStore.Small,
                    DurableSettingsStore.IsLegacyBoolean, out string raw))
                {
                    string value = raw.Trim().ToLowerInvariant();
                    return !(value == "0" || value == "off" || value == "false"
                        || value == "关" || value == "否" || value == "no");
                }
            }
            catch { }
            return false;
        }

        public static bool Save(bool enabled)
        {
            try
            {
                return DurableSettingsStore.Save(SettingsPath, enabled ? "1" : "0",
                    DurableSettingsStore.Small, DurableSettingsStore.IsLegacyBoolean);
            }
            catch { return false; }
        }
    }
}
