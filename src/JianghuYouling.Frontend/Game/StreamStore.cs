using System.IO;

namespace JianghuYouling
{
    /// <summary>流式输出开关持久化(默认开)。开=边想边显示思考/工具/逐字回话;关=无思考、整段即显。
    /// 存于 Settings/stream.txt;启动时由 Plugin 载入 → TalkOrchestrator.StreamingEnabled。</summary>
    public static class StreamStore
    {
        static string PathFor() => Path.Combine(JianghuYoulingPaths.Settings, "stream.txt");

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
