using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using JianghuYouling.Core.Security;
using Newtonsoft.Json.Linq;

namespace JianghuYouling
{
    /// <summary>
    /// Named LLM configurations.  Every profile is a standalone ProtectedConfigFile so
    /// each API key remains a root-level DPAPI value and receives the same main/tmp/bak
    /// validation as the live llm.json.  The index never contains a secret.
    /// </summary>
    internal static class LlmProfileStore
    {
        internal const int MaxProfiles = 24;
        internal const int MaxNameChars = 32;
        private const int SchemaVersion = 1;

        internal static string IndexPath => Path.Combine(JianghuYoulingPaths.Settings,
            "llm-profiles.json");

        private static string ProfilesDirectory => Path.Combine(JianghuYoulingPaths.Settings,
            "LlmProfiles");

        internal static IReadOnlyList<string> List(out string selected, out string error)
        {
            JObject index = ReadIndex(out error);
            var names = new List<string>();
            selected = null;
            if (index == null) return names;
            selected = CleanName(index.Value<string>("selected"));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (index["profiles"] is JArray profiles)
            {
                foreach (JToken token in profiles)
                {
                    string name = CleanName(token?["name"]?.ToString());
                    string id = token?["id"]?.ToString();
                    if (!IsValidName(name) || !IsValidId(id)
                        || !string.Equals(id, ProfileId(name), StringComparison.Ordinal)
                        || !seen.Add(name)) continue;
                    names.Add(name);
                }
            }
            names.Sort(StringComparer.CurrentCultureIgnoreCase);
            if (!string.IsNullOrEmpty(selected) && !seen.Contains(selected)) selected = null;
            return names;
        }

        internal static bool Save(string name, JObject configWithoutSecret, string apiKey,
            out string error)
        {
            name = CleanName(name);
            error = null;
            if (!IsValidName(name))
            {
                error = "配置名称须为 1～" + MaxNameChars + " 个可见字符";
                return false;
            }
            if (configWithoutSecret == null)
            {
                error = "模型配置为空";
                return false;
            }

            string selected;
            IReadOnlyList<string> existing = List(out selected, out error);
            if (error != null) return false;
            bool replacing = Contains(existing, name);
            if (!replacing && existing.Count >= MaxProfiles)
            {
                error = "最多保存 " + MaxProfiles + " 个模型配置";
                return false;
            }

            string id = ProfileId(name);
            string profilePath = ProfilePath(id);
            var profile = (JObject)configWithoutSecret.DeepClone();
            RemoveSecretAliases(profile);
            profile["schema"] = SchemaVersion;
            profile["profileName"] = name;

            JObject index = BuildIndex(existing, name);
            SettingsBatchTransaction transaction = null;
            try
            {
                transaction = SettingsBatchTransaction.Capture(new[] { profilePath, IndexPath });
                if (!ProtectedConfigFile.TrySave(profilePath, profile, apiKey ?? string.Empty,
                        out error)
                    || !ProtectedConfigFile.TrySave(IndexPath, index, string.Empty, out error))
                {
                    transaction.Rollback();
                    return false;
                }
                transaction.Commit();
                return true;
            }
            catch (Exception e)
            {
                transaction?.Rollback();
                error = "模型配置档案保存失败:" + e.GetType().Name;
                return false;
            }
            finally { transaction?.Dispose(); }
        }

        internal static bool TryLoad(string name, out JObject config, out string apiKey,
            out string error)
        {
            config = null;
            apiKey = null;
            name = CleanName(name);
            if (!IsValidName(name))
            {
                error = "模型配置名称无效";
                return false;
            }
            string selected;
            IReadOnlyList<string> names = List(out selected, out error);
            if (error != null) return false;
            if (!Contains(names, name))
            {
                error = "找不到模型配置「" + name + "」";
                return false;
            }
            if (!ProtectedConfigFile.TryLoad(ProfilePath(ProfileId(name)), out JObject loaded,
                    out apiKey, out error)) return false;
            if (!string.Equals(CleanName(loaded.Value<string>("profileName")), name,
                    StringComparison.OrdinalIgnoreCase))
            {
                config = null;
                apiKey = null;
                error = "模型配置索引与内容不一致";
                return false;
            }
            loaded.Remove("schema");
            loaded.Remove("profileName");
            loaded.Remove(LocalSecretProtector.ProtectedApiKeyField);
            config = loaded;
            return true;
        }

        internal static bool Select(string name, out string error)
        {
            name = CleanName(name);
            string selected;
            IReadOnlyList<string> names = List(out selected, out error);
            if (error != null) return false;
            if (!Contains(names, name))
            {
                error = "找不到模型配置「" + name + "」";
                return false;
            }
            return ProtectedConfigFile.TrySave(IndexPath, BuildIndex(names, name), string.Empty,
                out error);
        }

        internal static bool Delete(string name, out string nextSelected, out string error)
        {
            nextSelected = null;
            name = CleanName(name);
            string selected;
            IReadOnlyList<string> names = List(out selected, out error);
            if (error != null) return false;
            if (!Contains(names, name))
            {
                error = "找不到模型配置「" + name + "」";
                return false;
            }
            var remaining = new List<string>();
            foreach (string candidate in names)
                if (!string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                    remaining.Add(candidate);
            if (remaining.Count > 0)
                nextSelected = !string.Equals(selected, name, StringComparison.OrdinalIgnoreCase)
                    && Contains(remaining, selected) ? selected : remaining[0];

            string profilePath = ProfilePath(ProfileId(name));
            SettingsBatchTransaction transaction = null;
            try
            {
                transaction = SettingsBatchTransaction.Capture(new[] { profilePath, IndexPath });
                DeleteReplicaSet(profilePath);
                if (!ProtectedConfigFile.TrySave(IndexPath,
                        BuildIndex(remaining, nextSelected), string.Empty, out error)
                    || AnyReplica(profilePath))
                {
                    if (error == null) error = "旧配置文件未能可靠移除";
                    transaction.Rollback();
                    return false;
                }
                transaction.Commit();
                return true;
            }
            catch (Exception e)
            {
                transaction?.Rollback();
                error = "删除模型配置失败:" + e.GetType().Name;
                return false;
            }
            finally { transaction?.Dispose(); }
        }

        private static JObject ReadIndex(out string error)
        {
            error = null;
            if (!ProtectedConfigFile.TryGetReplicaPresence(IndexPath, out bool any, out error))
                return null;
            if (!any) { error = null; return NewIndex(); }
            if (!ProtectedConfigFile.TryLoad(IndexPath, out JObject index, out string key,
                    out error)) return null;
            if (!string.IsNullOrEmpty(key) || index.Value<int?>("schema") != SchemaVersion
                || !(index["profiles"] is JArray))
            {
                error = "模型配置索引格式无效";
                return null;
            }
            return index;
        }

        private static JObject NewIndex()
            => new JObject { ["schema"] = SchemaVersion, ["selected"] = "",
                ["profiles"] = new JArray() };

        private static JObject BuildIndex(IEnumerable<string> names, string selected)
        {
            var array = new JArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (names != null)
                foreach (string raw in names)
                {
                    string name = CleanName(raw);
                    if (!IsValidName(name) || !seen.Add(name)) continue;
                    array.Add(new JObject { ["name"] = name, ["id"] = ProfileId(name) });
                }
            string selectedName = CleanName(selected);
            if (IsValidName(selectedName) && seen.Add(selectedName))
                array.Add(new JObject { ["name"] = selectedName,
                    ["id"] = ProfileId(selectedName) });
            return new JObject { ["schema"] = SchemaVersion,
                ["selected"] = ContainsNames(array, selectedName) ? selectedName : "",
                ["profiles"] = array };
        }

        private static bool ContainsNames(JArray array, string name)
        {
            if (!IsValidName(name)) return false;
            foreach (JToken token in array)
                if (string.Equals(token?["name"]?.ToString(), name,
                        StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool Contains(IEnumerable<string> names, string name)
        {
            if (names == null || string.IsNullOrEmpty(name)) return false;
            foreach (string candidate in names)
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string ProfilePath(string id)
            => Path.Combine(ProfilesDirectory, id + ".json");

        private static string ProfileId(string name)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(CleanName(name).ToUpperInvariant());
            try
            {
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(bytes);
                    var text = new StringBuilder(hash.Length * 2);
                    foreach (byte value in hash) text.Append(value.ToString("x2"));
                    Array.Clear(hash, 0, hash.Length);
                    return text.ToString();
                }
            }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }

        private static string CleanName(string value) => (value ?? string.Empty).Trim();

        private static bool IsValidName(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > MaxNameChars) return false;
            foreach (char c in value)
                if (char.IsControl(c) || c == '/' || c == '\\') return false;
            return true;
        }

        private static bool IsValidId(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        private static void RemoveSecretAliases(JObject config)
        {
            var remove = new List<string>();
            foreach (JProperty property in config.Properties())
                if (string.Equals(property.Name, "apiKey", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, LocalSecretProtector.ProtectedApiKeyField,
                        StringComparison.OrdinalIgnoreCase)) remove.Add(property.Name);
            foreach (string name in remove) config.Remove(name);
        }

        private static void DeleteReplicaSet(string mainPath)
        {
            foreach (string path in new[] { mainPath, mainPath + ".tmp", mainPath + ".bak" })
                if (File.Exists(path)) File.Delete(path);
        }

        private static bool AnyReplica(string mainPath)
            => File.Exists(mainPath) || File.Exists(mainPath + ".tmp")
                || File.Exists(mainPath + ".bak");
    }
}
