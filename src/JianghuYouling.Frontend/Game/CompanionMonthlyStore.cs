using System.Globalization;
using System.IO;
using JianghuYouling.Core.Behavior;

namespace JianghuYouling
{
    /// <summary>每月「同道主动行事」开关(默认开)。关掉只停从全部当前同道中稳定抽选并主动做事,不影响 AI 江湖事件。</summary>
    public static class CompanionMonthlyStore
    {
        public static string SettingsPath => Path.Combine(JianghuYoulingPaths.Settings, "companion_monthly_actions.txt");
        static string PathFor() => SettingsPath;
        public static string CountPath => Path.Combine(JianghuYoulingPaths.Settings, "companion_monthly_count.txt");
        public const int DefaultCount = CompanionMonthlySelectionPolicy.DefaultCount;
        public const int MaxCount = CompanionMonthlySelectionPolicy.MaxCount;

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
            return true;
        }

        public static bool Save(bool on)
        {
            try { return DurableSettingsStore.Save(PathFor(), on ? "1" : "0", DurableSettingsStore.Small,
                DurableSettingsStore.IsLegacyBoolean); }
            catch { return false; }
        }

        public static int LoadCount()
        {
            try
            {
                if (DurableSettingsStore.TryLoad(CountPath, DurableSettingsStore.Small,
                    IsLegacyCount, out string raw)
                    && int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value))
                {
                    int normalized = value > MaxCount ? MaxCount : value;
                    if (normalized != value) SaveCount(normalized);
                    return normalized;
                }
            }
            catch { }
            return DefaultCount;
        }

        public static bool SaveCount(int count)
        {
            if (count < 0 || count > MaxCount) return false;
            try
            {
                return DurableSettingsStore.Save(CountPath,
                    count.ToString(CultureInfo.InvariantCulture), DurableSettingsStore.Small, IsValidCount);
            }
            catch { return false; }
        }

        private static bool IsValidCount(string raw)
            => raw != null && raw.Length <= 10
                && int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                && value >= 0 && value <= MaxCount;

        private static bool IsLegacyCount(string raw)
            => raw != null && raw.Length <= 10
                && int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                && value >= 0;
    }
}
