using System;
using System.Security.Cryptography;
using System.Text;

namespace JianghuYouling.Core.Tools
{
    /// <summary>前后端副作用幂等键。随机键用于即时动作；稳定键用于可跨重启重放的队列意图。</summary>
    public static class OperationId
    {
        public static string New() => Guid.NewGuid().ToString("N");

        public static string FromStableKey(string key)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key ?? ""));
                var sb = new StringBuilder(32);
                for (int i = 0; i < 16; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        public static bool IsValid(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 32) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }
    }
}
