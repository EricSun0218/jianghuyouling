using System.IO;

namespace JianghuYouling
{
    /// <summary>代笔篇幅：0=简短，1=适中（默认），2=详细。</summary>
    public static class GhostwriteLengthStore
    {
        public const int Short = 0;
        public const int Medium = 1;
        public const int Detailed = 2;
        public const int Default = Medium;

        public static string SettingsPath
            => Path.Combine(JianghuYoulingPaths.Settings, "ghostwrite_length.txt");

        public static int Load()
        {
            try
            {
                if (DurableSettingsStore.TryLoad(SettingsPath, DurableSettingsStore.Small, IsValid,
                        out string raw)
                    && int.TryParse(raw.Trim(), out int value))
                    return value;
            }
            catch { }
            return Default;
        }

        public static bool Save(int value)
        {
            if (!IsValidValue(value)) value = Default;
            try
            {
                return DurableSettingsStore.Save(SettingsPath, value.ToString(),
                    DurableSettingsStore.Small, IsValid);
            }
            catch { return false; }
        }

        public static string Label(int value)
        {
            switch (value)
            {
                case Short: return "简短";
                case Detailed: return "详细";
                default: return "适中";
            }
        }

        /// <summary>模型输出后的代码侧硬上限，防止提示词未被遵守。</summary>
        public static int MaxChars(int value)
        {
            switch (value)
            {
                case Short: return 30;
                case Detailed: return 100;
                default: return 60;
            }
        }

        public static string PromptDirective(int value)
        {
            switch (value)
            {
                case Short:
                    return "篇幅简短：不超过30字";
                case Detailed:
                    return "篇幅详细：不超过100字，可以多句，但不要换行";
                default:
                    return "篇幅适中：不超过60字，可以有两三句，但不要换行";
            }
        }

        private static bool IsValid(string value)
            => DurableSettingsStore.IsSingleLine(value, 4, false)
                && int.TryParse(value.Trim(), out int parsed)
                && IsValidValue(parsed);

        private static bool IsValidValue(int value)
            => value >= Short && value <= Detailed;
    }
}
