using System;
using System.Collections.Generic;
using System.IO;
using JianghuYouling.Core.Security;

namespace JianghuYouling
{
    /// <summary>
    /// Loads the exact credentials that may occur in historical player-visible text.
    /// Callers which export or forward local text must fail closed when a credential
    /// configuration exists but its DPAPI-backed main/tmp/bak set is not reliable.
    /// </summary>
    internal static class ConfiguredSecretSnapshot
    {
        private static readonly object Gate = new object();
        private static readonly string[] FileNames = { "llm.json", "tts.json", "minimax.json", "image_generation.json" };

        internal static bool TryLoad(out string[] exactSecrets, out string error)
        {
            exactSecrets = Array.Empty<string>();
            error = null;
            lock (Gate)
            {
                try
                {
                    var secrets = new List<string>();
                    foreach (string fileName in FileNames)
                    {
                        string path = Path.Combine(JianghuYoulingPaths.Settings, fileName);
                        if (!ProtectedConfigFile.TryGetReplicaPresence(path, out bool anyReplica, out _))
                        {
                            error = "受保护的接口凭据副本无法安全检查，已拒绝处理本地文本";
                            return false;
                        }
                        if (!anyReplica) continue;
                        if (!ProtectedConfigFile.TryLoad(path, out _, out string secret, out _))
                        {
                            error = "受保护的接口凭据配置无法可靠读取，已拒绝处理本地文本";
                            return false;
                        }
                        // LlmService and TtsConfig both trim before constructing clients;
                        // redact the same effective credential rather than only the raw
                        // value returned by the protected document.
                        string effectiveSecret = (secret ?? "").Trim();
                        if (effectiveSecret.Length == 0) continue;
                        // SecretRedactor intentionally ignores sub-four-character exact
                        // values to avoid replacing common prose. Such a credential cannot
                        // be exported safely, so refuse instead of silently weakening.
                        if (effectiveSecret.Length < 4)
                        {
                            error = "接口凭据过短，无法安全脱敏，已拒绝处理本地文本";
                            return false;
                        }
                        if (!secrets.Contains(effectiveSecret)) secrets.Add(effectiveSecret);
                    }
                    exactSecrets = secrets.ToArray();
                    return true;
                }
                catch
                {
                    exactSecrets = Array.Empty<string>();
                    error = "受保护的接口凭据配置无法安全检查，已拒绝处理本地文本";
                    return false;
                }
            }
        }

        internal static string[] LoadOrThrow()
        {
            if (TryLoad(out string[] exactSecrets, out string error)) return exactSecrets;
            throw new InvalidDataException(error ?? "接口凭据无法安全检查");
        }

    }
}
