using System;
using JianghuYouling.Core.Persistence;

namespace JianghuYouling
{
    /// <summary>Durable, bounded, strict-UTF8 persistence for small player settings.</summary>
    internal static class DurableSettingsStore
    {
        internal const int Small = 16 * 1024;
        internal const int Text = 256 * 1024;

        internal static bool TryLoad(string path, int maxBytes, Func<string, bool> validator,
            out string value)
        {
            return DurableFileStore.TryReadRecoverableText(path, maxBytes, validator,
                out value, out _, out _);
        }

        internal static bool Save(string path, string value, int maxBytes, Func<string, bool> validator)
        {
            return DurableFileStore.TryWriteTextAtomic(path, value ?? string.Empty, maxBytes, validator);
        }

        internal static bool IsSingleLine(string value, int maxChars, bool allowEmpty = true)
        {
            if (value == null || value.Length > maxChars || value.IndexOf('\0') >= 0
                || value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0) return false;
            return allowEmpty || value.Length > 0;
        }

        internal static bool IsFreeText(string value, int maxChars)
            => value != null && value.Length <= maxChars && value.IndexOf('\0') < 0;

        internal static bool IsLegacyBoolean(string value)
        {
            if (!IsSingleLine(value, 16, false)) return false;
            switch (value.Trim().ToLowerInvariant())
            {
                case "0": case "1": case "off": case "on": case "false": case "true":
                case "关": case "开": case "否": case "是": case "no": case "yes": return true;
                default: return false;
            }
        }
    }
}
