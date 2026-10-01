using System;
using System.IO;

namespace JianghuYouling.Core.Persistence
{
    /// <summary>聊天图片只保存文件名；拒绝目录、绝对路径与非图片扩展名。</summary>
    public static class ChatImageReference
    {
        public const int MaxFileNameChars = 180;

        public static bool IsValid(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaxFileNameChars
                || !string.Equals(value, Path.GetFileName(value), StringComparison.Ordinal)
                || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            string extension = Path.GetExtension(value);
            if (!string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase)) return false;
            foreach (char c in value)
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')) return false;
            return true;
        }
    }
}
