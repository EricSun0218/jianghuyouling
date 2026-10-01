using System.IO;

namespace JianghuYouling
{
    /// <summary>灵儿代码委托的独立现实时间发送频率。</summary>
    public static class AssistantCommissionIntervalStore
    {
        public const int Off = 0, Low = 1, Mid = 2, High = 3;
        public const int Default = Mid;
        public static string SettingsPath => Path.Combine(JianghuYoulingPaths.Settings,
            "assistant_commission_interval.txt");

        public static int Load()
        {
            try
            {
                if (DurableSettingsStore.TryLoad(SettingsPath, DurableSettingsStore.Small,
                        IsValid, out string raw))
                {
                    string value = raw.Trim().ToLowerInvariant();
                    if (value == "0" || value == "off" || value == "关") return Off;
                    if (value == "1" || value == "low" || value == "低") return Low;
                    if (value == "2" || value == "mid" || value == "中") return Mid;
                    if (value == "3" || value == "high" || value == "高") return High;
                }
            }
            catch { }
            return Default;
        }

        public static bool Save(int level)
        {
            if (level < Off) level = Off;
            if (level > High) level = High;
            string value = level == Off ? "off" : level == Low ? "low"
                : level == High ? "high" : "mid";
            try
            {
                return DurableSettingsStore.Save(SettingsPath, value,
                    DurableSettingsStore.Small, IsValid);
            }
            catch { return false; }
        }

        public static string Label(int level)
            => level == Off ? "关" : level == Low ? "低" : level == High ? "高" : "中";

        /// <summary>现实时间固定发送间隔；低 90、中 60（默认）、高 30 分钟。</summary>
        public static void IntervalRange(int level, out float min, out float max)
        {
            switch (level)
            {
                case High: min = max = 1800f; break;
                case Low: min = max = 5400f; break;
                default: min = max = 3600f; break;
            }
        }

        private static bool IsValid(string raw)
        {
            if (!DurableSettingsStore.IsSingleLine(raw, 16, false)) return false;
            string value = raw.Trim().ToLowerInvariant();
            return value == "0" || value == "1" || value == "2" || value == "3"
                || value == "off" || value == "low" || value == "mid" || value == "high"
                || value == "关" || value == "低" || value == "中" || value == "高";
        }
    }
}
