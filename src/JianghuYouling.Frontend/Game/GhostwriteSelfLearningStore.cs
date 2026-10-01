using System.IO;

namespace JianghuYouling
{
    /// <summary>代笔自学习开关：默认开启；关闭后代笔完全沿用原有默认/自定义口吻与篇幅。</summary>
    public static class GhostwriteSelfLearningStore
    {
        public static string SettingsPath
            => Path.Combine(JianghuYoulingPaths.Settings, "ghostwrite_self_learning.txt");

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
            return true;
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
