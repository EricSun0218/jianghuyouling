using System.IO;

namespace JianghuYouling
{
    /// <summary>「显示思考过程」开关持久化(默认开)。开=流式时在回话上方灰字逐字展示推理模型的思考;关=只显示工具调用与正文回话,隐去思考。
    /// 与流式开关解耦:关掉思考≠关流式(逐字回话+工具调用仍在)。存于 Settings/show_thinking.txt;启动时由 Plugin 载入 → ChatWindow.ShowThinking。</summary>
    public static class ThinkingStore
    {
        static string PathFor() => Path.Combine(JianghuYoulingPaths.Settings, "show_thinking.txt");

        public static bool Load()
        {
            try
            {
                var p = PathFor();
                if (DurableSettingsStore.TryLoad(p, DurableSettingsStore.Small,
                    DurableSettingsStore.IsLegacyBoolean, out string raw))
                {
                    string s = raw.Trim().ToLowerInvariant();
                    return s == "1" || s == "on" || s == "true" || s == "开" || s == "是" || s == "yes";
                }
            }
            catch { }
            // JHYL_THINKING_DEFAULT_ON: 没有本地设置文件时默认显示思量;已有 show_thinking.txt 会原样尊重玩家选择。
            return true;
        }

        public static bool Save(bool on)
        {
            try { return DurableSettingsStore.Save(PathFor(), on ? "1" : "0", DurableSettingsStore.Small,
                DurableSettingsStore.IsLegacyBoolean); }
            catch { return false; }
        }
    }
}
