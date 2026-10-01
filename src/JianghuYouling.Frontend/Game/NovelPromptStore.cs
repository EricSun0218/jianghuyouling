using System.IO;
using JianghuYouling.Core.Prompt;

namespace JianghuYouling
{
    /// <summary>所有人物共用的小说写作要求；人物素材始终按当前人物另行组装。</summary>
    public static class NovelPromptStore
    {
        public static string SettingsPath
            => Path.Combine(JianghuYoulingPaths.Settings, "npc_novel_prompt.txt");

        public static string Load()
        {
            try
            {
                if (DurableSettingsStore.TryLoad(SettingsPath, DurableSettingsStore.Text,
                        NpcNovelPromptBuilder.IsValidCustomPrompt, out string value))
                    return value.Trim();
            }
            catch { }
            return NpcNovelPromptBuilder.DefaultPrompt;
        }

        public static bool Save(string value)
        {
            string normalized = (value ?? string.Empty).Trim();
            if (!NpcNovelPromptBuilder.IsValidCustomPrompt(normalized)) return false;
            try
            {
                return DurableSettingsStore.Save(SettingsPath, normalized,
                    DurableSettingsStore.Text, NpcNovelPromptBuilder.IsValidCustomPrompt);
            }
            catch { return false; }
        }
    }
}
