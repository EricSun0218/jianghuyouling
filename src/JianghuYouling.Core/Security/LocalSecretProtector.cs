using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Persistence;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Security
{
    /// <summary>Windows DPAPI CurrentUser 保护本机 API 密钥；密文只能由同一 Windows 用户解开。</summary>
    public static class LocalSecretProtector
    {
        public const string ProtectedApiKeyField = "apiKeyProtected";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("JianghuYouling.LocalSecret.v1");

        public static bool TryProtect(string plaintext, out string protectedBase64, out string error)
        {
            protectedBase64 = null;
            error = null;
            try
            {
                byte[] clear = Encoding.UTF8.GetBytes(plaintext ?? "");
                try
                {
                    byte[] cipher = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
                    protectedBase64 = Convert.ToBase64String(cipher);
                    return !string.IsNullOrEmpty(protectedBase64);
                }
                finally { Array.Clear(clear, 0, clear.Length); }
            }
            catch
            {
                error = "Windows DPAPI CurrentUser 密钥保护失败";
                return false;
            }
        }

        public static bool TryUnprotect(string protectedBase64, out string plaintext, out string error)
        {
            plaintext = null;
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(protectedBase64))
                {
                    error = "受保护密钥为空";
                    return false;
                }
                byte[] cipher = Convert.FromBase64String(protectedBase64.Trim());
                byte[] clear = null;
                try
                {
                    clear = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
                    plaintext = new UTF8Encoding(false, true).GetString(clear);
                    return true;
                }
                finally
                {
                    Array.Clear(cipher, 0, cipher.Length);
                    if (clear != null) Array.Clear(clear, 0, clear.Length);
                }
            }
            catch
            {
                error = "受保护密钥无法由当前 Windows 用户解开";
                return false;
            }
        }
    }

    /// <summary>
    /// 带 apiKeyProtected 的小型 JSON 配置读写。旧 apiKey 只允许一次性读出并立即迁移；
    /// 只要 DPAPI 或耐久发布失败就封闭失败，绝不把密钥降级写回明文。
    /// </summary>
    public static class ProtectedConfigFile
    {
        private const int MaxConfigBytes = 1024 * 1024;
        private const int MaxPlainApiKeyChars = 16 * 1024;
        private const int MaxProtectedApiKeyChars = 128 * 1024;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>
        /// Reliably distinguishes “no durable candidate” from “candidate exists” and
        /// “presence could not be checked”. Callers must use this instead of checking
        /// only the main file: a crash may leave the last committed value in .bak, or a
        /// first-ever fully flushed value in .tmp. Presence says only that recovery must
        /// be attempted; TryLoad remains the authority on validity.
        /// </summary>
        public static bool TryGetReplicaPresence(string path, out bool anyReplica, out string error)
        {
            anyReplica = false;
            error = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "配置路径为空，无法安全检查候选副本";
                return false;
            }
            try
            {
                string full = Path.GetFullPath(path);
                foreach (string candidate in new[] { full, full + ".bak", full + ".tmp" })
                {
                    try
                    {
                        File.GetAttributes(candidate);
                        anyReplica = true;
                        return true;
                    }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                }
                return true;
            }
            catch
            {
                error = "配置候选副本无法安全检查";
                return false;
            }
        }

        public static bool TryLoad(string path, out JObject config, out string apiKey, out string error)
        {
            config = null;
            apiKey = null;
            error = null;
            bool protectedReplicaEvidence = HasProtectedReplicaEvidence(path);
            // 迁移中断自愈:0.29→0.30 首次明文迁移在写完 .tmp、File.Replace 之前中断时,
            // 会留下「main(和 bak,若存在)仍是合法明文旧文档 + 孤儿 .tmp 携带 DPAPI 文档」。
            // 该 tmp 从未提交,却会被下面的降级防护当作保护证据而永久 fail-closed,玩家的
            // 明文 Key 明明还在却每次重启都不可用。仅对这一种状态放行:归档孤儿 tmp 后按明文
            // 重走迁移。保护证据一旦出现在已提交副本(main/.bak),仍维持既有 fail-closed。
            if (protectedReplicaEvidence && TryReleaseInterruptedMigrationTmp(path))
                protectedReplicaEvidence = HasProtectedReplicaEvidence(path);
            if (!TryPeekFirstValidDocument(path, out var peeked))
            {
                // TryReadJson below supplies the stable public error text.  Peeking is
                // deliberately non-mutating so a corrupt protected commit can never be
                // replaced by a legacy plaintext fallback before this decision is made.
                peeked = null;
            }
            if (protectedReplicaEvidence && peeked != null
                && peeked.Property(LocalSecretProtector.ProtectedApiKeyField, StringComparison.Ordinal) == null
                && peeked.Property("apiKey", StringComparison.Ordinal) != null)
            {
                error = "检测到损坏或失配的受保护密钥副本，拒绝降级读取明文 apiKey";
                return false;
            }
            if (!TryReadJson(path, out var disk, out error)) return false;

            JProperty protectedProperty = disk.Property(LocalSecretProtector.ProtectedApiKeyField, StringComparison.Ordinal);
            JProperty plainProperty = disk.Property("apiKey", StringComparison.Ordinal);
            if (protectedProperty != null)
            {
                // 保护字段一旦存在便是唯一权威；损坏时绝不回退旁边残留的明文。
                if (!LocalSecretProtector.TryUnprotect(protectedProperty.Value?.ToString(), out var clear, out error)) return false;
                var cleaned = (JObject)disk.DeepClone();
                if (plainProperty != null) cleaned.Remove("apiKey");
                // A clean main does not imply clean replicas: an older plaintext main may
                // still be parked in .bak by File.Replace.  Do not release the decrypted
                // key until main/tmp/bak have all passed the strict commit validator.
                if (!TryEnsureStrictReplicas(path, cleaned, clear, out error)) return false;
                apiKey = clear;
                config = cleaned;
                return true;
            }

            if (plainProperty != null)
            {
                string legacy = plainProperty.Value?.ToString() ?? "";
                var migrated = (JObject)disk.DeepClone();
                migrated.Remove("apiKey");
                if (!string.IsNullOrEmpty(legacy))
                {
                    if (!LocalSecretProtector.TryProtect(legacy, out var protectedValue, out error)) return false;
                    migrated[LocalSecretProtector.ProtectedApiKeyField] = protectedValue;
                }
                if (!TryWriteAndVerify(path, migrated, legacy, out error)) return false;
                apiKey = legacy;
                config = migrated;
                return true;
            }

            // Empty-key configurations still scrub a stale plaintext backup left by an
            // earlier non-empty configuration before returning success.
            if (!TryEnsureStrictReplicas(path, disk, "", out error)) return false;
            apiKey = "";
            config = disk;
            return true;
        }

        public static bool TryLoadForDisplay(string path, out JObject displayConfig, out string error)
        {
            displayConfig = null;
            if (!TryLoad(path, out var disk, out var apiKey, out error)) return false;
            displayConfig = (JObject)disk.DeepClone();
            displayConfig.Remove(LocalSecretProtector.ProtectedApiKeyField);
            displayConfig["apiKey"] = apiKey ?? ""; // 仅内存 UI 回填，禁止直接序列化此对象。
            return true;
        }

        public static bool TrySave(string path, JObject config, string apiKey, out string error)
        {
            error = null;
            if (config == null) { error = "配置为空"; return false; }
            var protectedConfig = (JObject)config.DeepClone();
            RemoveSecretFieldsCaseInsensitive(protectedConfig);
            string clear = apiKey ?? "";
            if (!string.IsNullOrEmpty(clear))
            {
                if (!LocalSecretProtector.TryProtect(clear, out var protectedValue, out error)) return false;
                protectedConfig[LocalSecretProtector.ProtectedApiKeyField] = protectedValue;
            }
            return TryWriteAndVerify(path, protectedConfig, clear, out error);
        }

        /// <summary>迁移不再使用但可能仍留有明文的旧配置；文件不存在视为成功。</summary>
        public static bool TryMigrateLegacyFile(string path, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path)) return true;
            string full;
            try { full = Path.GetFullPath(path); }
            catch { error = "旧配置路径无效"; return false; }
            if (!TryGetReplicaPresence(full, out bool anyReplica, out error)) return false;
            if (!anyReplica) return true;
            return TryLoad(path, out _, out _, out error);
        }

        static bool TryWriteAndVerify(string path, JObject config, string expectedApiKey, out string error)
        {
            error = null;
            if (config == null)
            {
                error = "配置内容为空";
                return false;
            }
            string proposed = config.ToString(Formatting.Indented);
            if (!IsValidCommittedConfigDocument(proposed)
                || DocumentContainsPlaintextSecret(proposed, expectedApiKey)
                || !TryValidateExpectedProtectedKey(config, expectedApiKey, out error))
            {
                if (error == null) error = "配置提交前密钥验证失败";
                return false;
            }
            if (!TryWriteJsonDurableCore(path, config, expectedApiKey, out error)) return false;
            if (!TryValidateCommittedReplica(Path.GetFullPath(path), expectedApiKey,
                    requireExpectedKey: true, out error)
                || File.Exists(Path.GetFullPath(path) + ".tmp")
                || !TryValidateCommittedReplica(Path.GetFullPath(path) + ".bak", expectedApiKey,
                    requireExpectedKey: true, out error))
            {
                if (error == null) error = "配置 main/tmp/bak 严格提交验证失败";
                return false;
            }
            return true;
        }

        static bool TryValidateExpectedProtectedKey(JObject config, string expectedApiKey, out string error)
        {
            error = null;
            JProperty protectedProperty = config?.Property(LocalSecretProtector.ProtectedApiKeyField,
                StringComparison.Ordinal);
            if (string.IsNullOrEmpty(expectedApiKey))
            {
                if (protectedProperty == null) return true;
                error = "空密钥配置不应含保护字段";
                return false;
            }
            if (protectedProperty == null
                || !LocalSecretProtector.TryUnprotect(protectedProperty.Value?.ToString(),
                    out var actual, out error)
                || !string.Equals(actual, expectedApiKey, StringComparison.Ordinal))
            {
                if (error == null) error = "受保护密钥提交值不一致";
                return false;
            }
            return true;
        }

        public static bool TryWriteJsonDurable(string path, JObject config, out string error)
            => TryWriteJsonDurableCore(path, config, null, out error);

        static bool TryWriteJsonDurableCore(string path, JObject config,
            string expectedApiKeyOrNull, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(path) || config == null) { error = "配置路径或内容为空"; return false; }
                string json = config.ToString(Formatting.Indented);
                if (!IsValidCommittedConfigDocument(json)) { error = "配置提交内容含明文密钥或不符合严格格式"; return false; }
                if (DocumentContainsPlaintextSecret(json, expectedApiKeyOrNull))
                { error = "配置提交内容仍含明文密钥字节"; return false; }
                if (StrictUtf8.GetByteCount(json) > MaxConfigBytes) { error = "配置超过 1 MiB 上限"; return false; }
                // null 仅供不携带密钥语义的通用 JSON 写入口；ProtectedConfigFile 的保存/迁移
                // 始终传当前期望 key（空串表示明确清除）。把“精确当前 key”纳入 DurableFileStore
                // 的副本 validator 后，File.Replace 产生的旧-key .bak 会在同一提交中被重建，
                // 不会因其仍是格式合法的 DPAPI 文档而被误当成可保留的历史副本。
                bool requireExpectedKey = expectedApiKeyOrNull != null;
                Func<string, bool> commitValidator = candidate =>
                {
                    if (!IsValidCommittedConfigDocument(candidate)
                        || DocumentContainsPlaintextSecret(candidate, expectedApiKeyOrNull)) return false;
                    if (!requireExpectedKey) return true;
                    return LlmJsonProtocol.TryParseObject(candidate, MaxConfigBytes, out JObject parsed, out _)
                        && TryValidateExpectedProtectedKey(parsed, expectedApiKeyOrNull, out _);
                };
                bool saved = DurableFileStore.TryWriteTextAtomic(Path.GetFullPath(path), json,
                    MaxConfigBytes, commitValidator);
                if (!saved) error = "配置 main/tmp/bak 耐久提交或语义读回失败";
                return saved;
            }
            catch
            {
                error = "配置耐久原子保存失败";
                return false;
            }
        }

        static bool TryReadJson(string path, out JObject config, out string error)
        {
            config = null;
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(path)) { error = "配置文件不存在"; return false; }
                if (!DurableFileStore.TryReadRecoverableText(Path.GetFullPath(path), MaxConfigBytes,
                    IsValidConfigDocument, out string json, out bool anyCandidate, out _))
                {
                    error = anyCandidate ? "配置 main/tmp/bak 均无法可靠恢复" : "配置文件不存在";
                    return false;
                }
                if (!LlmJsonProtocol.TryParseObject(json, MaxConfigBytes, out config, out _)
                    || !ValidateSecretFields(config, out error))
                { error = error ?? "配置 JSON 不符合严格 object 格式"; config = null; return false; }
                return true;
            }
            catch
            {
                error = "配置文件无法安全读取";
                return false;
            }
        }

        static bool IsValidConfigDocument(string json)
        {
            if (string.IsNullOrWhiteSpace(json)
                || !LlmJsonProtocol.TryParseObject(json, MaxConfigBytes, out JObject parsed, out _)) return false;
            return ValidateSecretFields(parsed, out _);
        }

        /// <summary>
        /// Commit-time validation is intentionally stricter than read-time validation:
        /// exact legacy apiKey is accepted only while selecting an old document for
        /// migration, never by tmp/main/bak publication or repair.
        /// </summary>
        static bool IsValidCommittedConfigDocument(string json)
        {
            if (string.IsNullOrWhiteSpace(json)
                || !LlmJsonProtocol.TryParseObject(json, MaxConfigBytes, out JObject parsed, out _)
                || !ValidateSecretFields(parsed, out _)) return false;
            return ValidateCommittedSecretFields(parsed, isRoot: true, out _);
        }

        static bool ValidateCommittedSecretFields(JToken token, bool isRoot, out string error)
        {
            error = null;
            if (token is JObject obj)
            {
                foreach (JProperty property in obj.Properties())
                {
                    if (string.Equals(property.Name, "apiKey", StringComparison.OrdinalIgnoreCase))
                    {
                        error = "提交文档不得含任何大小写形式的明文 apiKey 字段";
                        return false;
                    }
                    if (string.Equals(property.Name, LocalSecretProtector.ProtectedApiKeyField,
                            StringComparison.OrdinalIgnoreCase)
                        && (!isRoot || !string.Equals(property.Name,
                            LocalSecretProtector.ProtectedApiKeyField, StringComparison.Ordinal)))
                    {
                        error = "受保护密钥字段只能以规范名称出现在配置根对象";
                        return false;
                    }
                    if (!ValidateCommittedSecretFields(property.Value, isRoot: false, out error)) return false;
                }
            }
            else if (token is JArray array)
            {
                foreach (JToken child in array)
                    if (!ValidateCommittedSecretFields(child, isRoot: false, out error)) return false;
            }
            return true;
        }

        static bool TryEnsureStrictReplicas(string path, JObject config, string expectedApiKey, out string error)
        {
            error = null;
            string full;
            try { full = Path.GetFullPath(path); }
            catch { error = "配置路径无效"; return false; }

            if (TryValidateCommittedReplica(full, expectedApiKey, requireExpectedKey: true, out _)
                && !File.Exists(full + ".tmp")
                && TryValidateCommittedReplica(full + ".bak", expectedApiKey,
                    requireExpectedKey: true, out _)) return true;

            return TryWriteAndVerify(full, config, expectedApiKey, out error);
        }

        static bool TryValidateCommittedReplica(string path, string expectedApiKey,
            bool requireExpectedKey, out string error)
        {
            error = null;
            if (!DurableFileStore.TryReadStrictUtf8(path, MaxConfigBytes, out string json)
                || !IsValidCommittedConfigDocument(json))
            {
                error = "配置副本不是严格受保护文档";
                return false;
            }
            if (DocumentContainsPlaintextSecret(json, expectedApiKey))
            {
                error = "配置副本仍含明文密钥字节";
                return false;
            }
            if (!requireExpectedKey) return true;
            if (!LlmJsonProtocol.TryParseObject(json, MaxConfigBytes, out JObject parsed, out _))
            {
                error = "配置副本无法严格解析";
                return false;
            }
            JProperty protectedProperty = parsed.Property(LocalSecretProtector.ProtectedApiKeyField,
                StringComparison.Ordinal);
            if (string.IsNullOrEmpty(expectedApiKey))
            {
                if (protectedProperty == null) return true;
                error = "空密钥配置不应含保护字段";
                return false;
            }
            if (protectedProperty == null
                || !LocalSecretProtector.TryUnprotect(protectedProperty.Value?.ToString(),
                    out var actual, out error)
                || !string.Equals(actual, expectedApiKey, StringComparison.Ordinal))
            {
                if (error == null) error = "受保护密钥读回不一致";
                return false;
            }
            return true;
        }

        static bool DocumentContainsPlaintextSecret(string json, string expectedApiKey)
        {
            if (string.IsNullOrEmpty(expectedApiKey)
                || !LlmJsonProtocol.TryParseObject(json, MaxConfigBytes, out JObject parsed, out _)) return false;
            return TokenContainsPlaintextSecret(parsed, expectedApiKey, isRoot: true);
        }

        static bool TokenContainsPlaintextSecret(JToken token, string expectedApiKey, bool isRoot)
        {
            if (token is JObject obj)
            {
                foreach (JProperty property in obj.Properties())
                {
                    // DPAPI ciphertext is the one intentional secret-bearing value.
                    if (isRoot && string.Equals(property.Name,
                            LocalSecretProtector.ProtectedApiKeyField, StringComparison.Ordinal)) continue;
                    if (SecretTextContains(property.Name, expectedApiKey)
                        || TokenContainsPlaintextSecret(property.Value, expectedApiKey, isRoot: false)) return true;
                }
            }
            else if (token is JArray array)
            {
                foreach (JToken child in array)
                    if (TokenContainsPlaintextSecret(child, expectedApiKey, isRoot: false)) return true;
            }
            else if (token is JValue value && value.Type == JTokenType.String)
            {
                return SecretTextContains(value.Value<string>(), expectedApiKey);
            }
            return false;
        }

        static bool SecretTextContains(string value, string expectedApiKey)
        {
            if (value == null || string.IsNullOrEmpty(expectedApiKey)) return false;
            // Short local-proxy tokens are legal and commonly collide with ordinary
            // prose/property names; exact-value matching still prevents storing them as
            // a plaintext field without rejecting every incidental character.
            return expectedApiKey.Length < 8
                ? string.Equals(value, expectedApiKey, StringComparison.Ordinal)
                : value.IndexOf(expectedApiKey, StringComparison.Ordinal) >= 0;
        }

        static bool TryPeekFirstValidDocument(string path, out JObject config)
        {
            config = null;
            string full;
            try { full = Path.GetFullPath(path); }
            catch { return false; }
            foreach (string candidate in new[] { full, full + ".tmp", full + ".bak" })
            {
                if (!DurableFileStore.TryReadStrictUtf8(candidate, MaxConfigBytes, out string json)
                    || !LlmJsonProtocol.TryParseObject(json, MaxConfigBytes, out JObject parsed, out _)
                    || !ValidateSecretFields(parsed, out _)) continue;
                config = parsed;
                return true;
            }
            return false;
        }

        static bool HasProtectedReplicaEvidence(string path)
        {
            string full;
            try { full = Path.GetFullPath(path); }
            catch { return false; }
            foreach (string candidate in new[] { full, full + ".tmp", full + ".bak" })
                if (HasProtectedEvidenceInReplica(candidate)) return true;
            return false;
        }

        static bool HasProtectedEvidenceInReplica(string candidate)
        {
            if (!DurableFileStore.TryReadStrictUtf8(candidate, MaxConfigBytes, out string json)) return false;
            if (json.IndexOf("\"" + LocalSecretProtector.ProtectedApiKeyField + "\"",
                    StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return LlmJsonProtocol.TryParseObject(json, MaxConfigBytes, out JObject parsed, out _)
                && ContainsProperty(parsed, LocalSecretProtector.ProtectedApiKeyField);
        }

        /// <summary>
        /// 只认「保护证据仅存在于未提交 .tmp,且 main 是合法明文旧文档、.bak 不存在或同为
        /// 合法明文旧文档」的迁移中断态;归档(不删除)孤儿 tmp 使明文迁移可以重跑。
        /// 其它任何形态(证据在 main/.bak、main 损坏、副本无法严格读取)一律不动,维持 fail-closed。
        /// </summary>
        static bool TryReleaseInterruptedMigrationTmp(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string tmp = full + ".tmp";
                if (!HasProtectedEvidenceInReplica(tmp)) return false;
                if (HasProtectedEvidenceInReplica(full) || HasProtectedEvidenceInReplica(full + ".bak")) return false;
                if (!IsLegacyPlaintextDocument(full)) return false;
                if (File.Exists(full + ".bak") && !IsLegacyPlaintextDocument(full + ".bak")) return false;
                string archive = tmp + ".orphan-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")
                    + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                File.Move(tmp, archive);
                return !File.Exists(tmp) && File.Exists(archive);
            }
            catch { return false; }
        }

        /// <summary>合法明文旧文档 = 严格可读、字段校验通过、根级含 apiKey、不含任何保护字段。</summary>
        static bool IsLegacyPlaintextDocument(string candidate)
        {
            if (!DurableFileStore.TryReadStrictUtf8(candidate, MaxConfigBytes, out string json)
                || !LlmJsonProtocol.TryParseObject(json, MaxConfigBytes, out JObject parsed, out _)
                || !ValidateSecretFields(parsed, out _)) return false;
            return parsed.Property("apiKey", StringComparison.Ordinal) != null
                && !ContainsProperty(parsed, LocalSecretProtector.ProtectedApiKeyField);
        }

        static bool ContainsProperty(JToken token, string propertyName)
        {
            if (token is JObject obj)
            {
                foreach (JProperty property in obj.Properties())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                        || ContainsProperty(property.Value, propertyName)) return true;
                }
            }
            else if (token is JArray array)
            {
                foreach (JToken child in array)
                    if (ContainsProperty(child, propertyName)) return true;
            }
            return false;
        }

        static bool ValidateSecretFields(JObject config, out string error)
        {
            error = null;
            foreach (var property in config.Properties())
            {
                bool plain = string.Equals(property.Name, "apiKey", StringComparison.Ordinal);
                bool protectedField = string.Equals(property.Name, LocalSecretProtector.ProtectedApiKeyField, StringComparison.Ordinal);
                bool secretAlias = string.Equals(property.Name, "apiKey", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(property.Name, LocalSecretProtector.ProtectedApiKeyField, StringComparison.OrdinalIgnoreCase);
                if (secretAlias && !plain && !protectedField)
                {
                    error = "密钥字段名称大小写不合法";
                    return false;
                }
                if (!plain && !protectedField) continue;
                if (property.Value == null || property.Value.Type != JTokenType.String)
                {
                    error = "密钥字段必须是字符串";
                    return false;
                }
                int length = property.Value.ToString().Length;
                int max = protectedField ? MaxProtectedApiKeyChars : MaxPlainApiKeyChars;
                if (length > max)
                {
                    error = "密钥字段超过大小上限";
                    return false;
                }
            }
            return true;
        }

        static void RemoveSecretFieldsCaseInsensitive(JObject config)
        {
            var names = new System.Collections.Generic.List<string>();
            foreach (var property in config.Properties())
            {
                if (string.Equals(property.Name, "apiKey", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(property.Name, LocalSecretProtector.ProtectedApiKeyField, StringComparison.OrdinalIgnoreCase))
                    names.Add(property.Name);
            }
            foreach (string name in names) config.Remove(name);
        }
    }
}
