using System.IO;

namespace JianghuYouling
{
    /// <summary>群聊自动轮数上限。每条玩家消息都会重新开启 1~3 轮 NPC 交流，
    /// 每轮群内所有成员各自回应；它不限制玩家继续发送多少条消息。存于 Settings/group_rounds.txt。</summary>
    public static class GroupRoundsStore
    {
        public const int Min = 1, Max = 3, Default = 1;
        public static string SettingsPath => Path.Combine(JianghuYoulingPaths.Settings, "group_rounds.txt");
        static string PathFor() => SettingsPath;

        public static int Load()
        {
            try { var p = PathFor(); if (DurableSettingsStore.TryLoad(p, DurableSettingsStore.Small, IsReadableLegacy,
                out string raw) && int.TryParse(raw.Trim(), out int v)) return Clamp(v); }
            catch { }
            return Default;
        }

        public static bool Save(int rounds)
        {
            try { return DurableSettingsStore.Save(PathFor(), Clamp(rounds).ToString(), DurableSettingsStore.Small, IsValid); }
            catch { return false; }
        }

        public static int Clamp(int v) => v < Min ? Min : (v > Max ? Max : v);

        private static bool IsValid(string value)
            => DurableSettingsStore.IsSingleLine(value, 4, false) && int.TryParse(value.Trim(), out int v)
                && v >= Min && v <= Max;

        // 旧版允许 4/5 轮；升级后可靠读取并收敛为 3，而不是把合法旧设置当损坏回退到 1。
        private static bool IsReadableLegacy(string value)
            => DurableSettingsStore.IsSingleLine(value, 4, false) && int.TryParse(value.Trim(), out int v)
                && v >= Min && v <= 5;
    }
}
