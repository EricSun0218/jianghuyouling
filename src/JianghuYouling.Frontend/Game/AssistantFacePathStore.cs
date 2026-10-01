using System.IO;

namespace JianghuYouling
{
    /// <summary>灵儿挂件自定义形象图片路径。存 Settings/assistant_face_path.txt;空=使用 Mod 内置灵儿图片。</summary>
    public static class AssistantFacePathStore
    {
        private static string PathFor() => Path.Combine(JianghuYoulingPaths.Settings, "assistant_face_path.txt");

        public static string Load()
        {
            try
            {
                var p = PathFor();
                if (DurableSettingsStore.TryLoad(p, DurableSettingsStore.Small, IsValid, out string value))
                    return value.Trim();
            }
            catch { }
            return "";
        }

        public static bool Save(string path)
        {
            try
            {
                string value = (path ?? "").Trim();
                bool saved = DurableSettingsStore.Save(PathFor(), value, DurableSettingsStore.Small, IsValid);
                if (!saved) return false;
                return value.Length != 0 || DurableSettingsStore.Save(PathFor(), value, DurableSettingsStore.Small, IsValid);
            }
            catch { return false; }
        }

        public static bool Clear()
        {
            try
            {
                // Commit the tombstone twice so both main and bak represent the
                // explicit clear; a later corrupt main cannot resurrect the old path.
                return DurableSettingsStore.Save(PathFor(), string.Empty, DurableSettingsStore.Small, IsValid)
                    && DurableSettingsStore.Save(PathFor(), string.Empty, DurableSettingsStore.Small, IsValid);
            }
            catch { return false; }
        }

        private static bool IsValid(string value) => DurableSettingsStore.IsSingleLine(value, 4096);
    }
}
