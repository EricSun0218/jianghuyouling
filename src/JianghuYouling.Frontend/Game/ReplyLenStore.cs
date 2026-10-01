using System.IO;

namespace JianghuYouling
{
    /// <summary>回复篇幅设置持久化:0=简短 1=适中(默认) 2=详细 3=不限。
    /// 存于 Settings/replylen.txt;启动时由 Plugin 载入 → TalkOrchestrator.ReplyLength,对话时注入提示词【篇幅】。</summary>
    public static class ReplyLenStore
    {
        static string PathFor() => Path.Combine(JianghuYoulingPaths.Settings, "replylen.txt");

        public static int Load()
        {
            try
            {
                var p = PathFor();
                if (DurableSettingsStore.TryLoad(p, DurableSettingsStore.Small, IsValid, out string raw)
                    && int.TryParse(raw.Trim(), out int v) && v >= 0 && v <= 3)
                    return v;
            }
            catch { }
            return 1;   // 默认适中
        }

        public static bool Save(int v)
        {
            if (v < 0 || v > 3) v = 1;
            try { return DurableSettingsStore.Save(PathFor(), v.ToString(), DurableSettingsStore.Small, IsValid); }
            catch { return false; }
        }

        private static bool IsValid(string value)
            => DurableSettingsStore.IsSingleLine(value, 4, false) && int.TryParse(value.Trim(), out int v)
                && v >= 0 && v <= 3;

        public static string Label(int v)
        {
            switch (v) { case 0: return "简短"; case 2: return "详细"; case 3: return "不限"; default: return "适中"; }
        }
    }
}
