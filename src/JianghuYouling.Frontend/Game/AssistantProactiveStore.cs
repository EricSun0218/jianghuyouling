using System.IO;

namespace JianghuYouling
{
    /// <summary>悬浮助手「主动消息」级别:0=关 / 1=低频(默认) / 2=中频 / 3=高频。存 Settings/assistant_proactive.txt。
    /// 数字按当前四档读取；旧版布尔 on/true/开仍按中频迁移。</summary>
    public static class AssistantProactiveStore
    {
        public const int Off = 0, Low = 1, Mid = 2, High = 3;
        public static string SettingsPath => Path.Combine(JianghuYoulingPaths.Settings, "assistant_proactive.txt");
        private static string PathFor() => SettingsPath;

        public static int Load()
        {
            try
            {
                var p = PathFor();
                if (DurableSettingsStore.TryLoad(p, DurableSettingsStore.Small, IsValid, out string raw))
                {
                    var s = raw.Trim().ToLowerInvariant();
                    if (s == "0" || s == "off" || s == "false" || s == "关" || s == "否" || s == "no") return Off;
                    if (s == "1" || s == "低" || s == "low") return Low;
                    if (s == "2" || s == "中" || s == "mid") return Mid;
                    if (s == "3" || s == "高" || s == "high") return High;
                    if (s == "on" || s == "true" || s == "开" || s == "是" || s == "yes") return Mid;
                }
            }
            catch { }
            return Low;
        }

        public static bool IsOn() => Load() > Off;

        public static bool Save(int level)
        {
            if (level < Off) level = Off; if (level > High) level = High;
            string value = level == Off ? "off" : level == Low ? "low" : level == High ? "high" : "mid";
            try { return DurableSettingsStore.Save(PathFor(), value, DurableSettingsStore.Small, IsValid); }
            catch { return false; }
        }

        private static bool IsValid(string value)
        {
            if (!DurableSettingsStore.IsSingleLine(value, 16, false)) return false;
            string s = value.Trim().ToLowerInvariant();
            return s == "0" || s == "1" || s == "2" || s == "3" || s == "off" || s == "on"
                || s == "false" || s == "true" || s == "关" || s == "开" || s == "否" || s == "是"
                || s == "no" || s == "yes"
                || s == "低" || s == "中" || s == "高" || s == "low" || s == "mid" || s == "high";
        }

        // 频率级别 → 主动消息随机间隔(秒,真实时间)
        public static void IntervalRange(int level, out float min, out float max)
        {
            switch (level)
            {
                case High: min = 60f; max = 120f; break;      // 高:1-2 分钟(用户要求最高频更勤)
                case Low: min = 600f; max = 1200f; break;     // 低:10-20 分钟
                default: min = 180f; max = 420f; break;       // 中:3-7 分钟
            }
        }
    }
}
