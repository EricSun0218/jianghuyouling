using System.IO;

namespace JianghuYouling
{
    /// <summary>难度设置(简单/均衡/困难)持久化。改变 NPC 被说动答应请求的难易(注入对话提示词)。
    /// 存于 Settings/difficulty.txt;游戏启动时由 Plugin 载入 → TalkOrchestrator.Difficulty。</summary>
    public static class DifficultyStore
    {
        public static readonly string[] Options = { "简单", "均衡", "困难" };

        static string PathFor() => Path.Combine(JianghuYoulingPaths.Settings, "difficulty.txt");

        public static string Load()
        {
            try
            {
                var p = PathFor();
                if (DurableSettingsStore.TryLoad(p, DurableSettingsStore.Small, IsValid, out string raw))
                {
                    string s = raw.Trim();
                    if (s == "简单" || s == "均衡" || s == "困难") return s;
                }
            }
            catch { }
            return "均衡";
        }

        public static bool Save(string d)
        {
            try
            {
                return DurableSettingsStore.Save(PathFor(), (d == "简单" || d == "困难") ? d : "均衡",
                    DurableSettingsStore.Small, IsValid);
            }
            catch { return false; }
        }

        private static bool IsValid(string value)
        {
            if (!DurableSettingsStore.IsSingleLine(value, 8, false)) return false;
            string s = value.Trim();
            return s == "简单" || s == "均衡" || s == "困难";
        }
    }
}
