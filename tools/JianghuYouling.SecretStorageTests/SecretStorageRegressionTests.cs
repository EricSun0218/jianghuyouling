using System;
using System.IO;
using System.Linq;
using System.Text;
using JianghuYouling.Core.Security;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.SecretStorageTests
{
    internal static class SecretStorageRegressionTests
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            string root = Path.Combine(Path.GetTempPath(),
                "jyl_secret_regression_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                TestLegacyMigration(root);
                TestMixedReplicaScrub(root);
                TestRotationAndClearScrub(root);
                TestCorruptProtectedNeverFallsBack(root);
                TestReplicaScrubFailureIsFailClosed(root);
                TestStrictCommitValidator(root);
                TestAllProtectedConfigKinds(root);
                Console.WriteLine("[PASS] ProtectedConfigFile main/tmp/bak secret-storage regression");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[FAIL] " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void TestLegacyMigration(string root)
        {
            string dir = NewCaseDirectory(root, "legacy");
            string absent = Path.Combine(dir, "absent.json");
            Require(ProtectedConfigFile.TryGetReplicaPresence(absent, out bool absentPresent, out _)
                    && !absentPresent,
                "missing config was not distinguished from a presence-check failure");
            Require(!ProtectedConfigFile.TryGetReplicaPresence("bad\0config", out _, out var presenceError)
                    && !string.IsNullOrWhiteSpace(presenceError),
                "invalid config path was incorrectly reported as simply absent");
            string path = Path.Combine(dir, "llm.json");
            string secret = NewSecret("legacy");
            WriteJson(path, new JObject
            {
                ["baseUrl"] = "https://example.invalid/v1",
                ["model"] = "dummy",
                ["apiKey"] = secret
            });
            WriteJson(path + ".bak", new JObject
            {
                ["baseUrl"] = "https://example.invalid/v1",
                ["model"] = "dummy-old",
                ["apiKey"] = secret
            });

            Require(ProtectedConfigFile.TryLoad(path, out var config, out var loaded, out var error),
                "legacy plaintext migration failed: " + ErrorKind(error));
            Require(string.Equals(loaded, secret, StringComparison.Ordinal),
                "legacy migration returned a different key");
            Require(config?["apiKeyProtected"]?.Type == JTokenType.String,
                "legacy migration did not publish apiKeyProtected");
            AssertCommittedReplicasContainNoSecret(path, secret);

            string orphan = Path.Combine(dir, "minimax.json");
            WriteJson(orphan + ".bak", new JObject
            {
                ["baseUrl"] = "https://example.invalid/v1",
                ["apiKey"] = secret
            });
            Require(ProtectedConfigFile.TryGetReplicaPresence(orphan, out bool orphanPresent, out _)
                    && orphanPresent,
                "backup-only protected-config candidate was reported absent");
            Require(ProtectedConfigFile.TryMigrateLegacyFile(orphan, out error),
                "backup-only legacy migration failed: " + ErrorKind(error));
            AssertCommittedReplicasContainNoSecret(orphan, secret);

            // Simulate a first-ever save that durably flushed tmp immediately before the
            // final Move. Upper layers use the tri-state replica-presence check to enter
            // this recovery path without conflating an I/O error with absence.
            string firstTmp = Path.Combine(dir, "first-tmp.json");
            File.Copy(orphan, firstTmp + ".tmp");
            Require(ProtectedConfigFile.TryGetReplicaPresence(firstTmp, out bool firstTmpPresent, out _)
                    && firstTmpPresent,
                "tmp-only protected-config candidate was reported absent");
            Require(ProtectedConfigFile.TryLoad(firstTmp, out _, out var firstTmpSecret, out error),
                "tmp-only protected config did not recover: " + ErrorKind(error));
            Require(string.Equals(firstTmpSecret, secret, StringComparison.Ordinal),
                "tmp-only protected config returned a different key");
            AssertCommittedReplicasContainNoSecret(firstTmp, secret);
        }

        private static void TestMixedReplicaScrub(string root)
        {
            string dir = NewCaseDirectory(root, "mixed");
            string path = Path.Combine(dir, "tts.json");
            string secret = NewSecret("mixed");
            var clean = new JObject
            {
                ["provider"] = "openai",
                ["baseUrl"] = "https://example.invalid/v1",
                ["model"] = "dummy-tts"
            };
            Require(ProtectedConfigFile.TrySave(path, clean, secret, out var saveError),
                "mixed setup save failed: " + ErrorKind(saveError));

            var plaintext = new JObject
            {
                ["provider"] = "openai",
                ["baseUrl"] = "https://example.invalid/v1",
                ["apiKey"] = secret
            };
            var mixedMain = JObject.Parse(File.ReadAllText(path, Utf8NoBom));
            mixedMain["apiKey"] = secret;
            WriteJson(path, mixedMain);
            WriteJson(path + ".bak", plaintext);
            WriteJson(path + ".tmp", plaintext);

            Require(ProtectedConfigFile.TryLoad(path, out _, out var loaded, out var error),
                "protected main did not scrub mixed replicas: " + ErrorKind(error));
            Require(string.Equals(loaded, secret, StringComparison.Ordinal),
                "mixed replica scrub returned a different key");
            AssertCommittedReplicasContainNoSecret(path, secret);

            // A missing main with an otherwise schema-valid orphan backup must not keep
            // plaintext bytes hidden in an unrelated field during a fresh save.
            string orphan = Path.Combine(dir, "orphan.json");
            WriteJson(orphan + ".bak", new JObject { ["note"] = secret });
            Require(ProtectedConfigFile.TrySave(orphan, clean, secret, out saveError),
                "fresh save did not replace unsafe orphan backup: " + ErrorKind(saveError));
            AssertCommittedReplicasContainNoSecret(orphan, secret);
        }

        private static void TestCorruptProtectedNeverFallsBack(string root)
        {
            string dir = NewCaseDirectory(root, "corrupt-valid-json");
            string path = Path.Combine(dir, "llm.json");
            string secret = NewSecret("corrupt");
            WriteJson(path, new JObject { ["apiKeyProtected"] = "not-valid-dpapi" });
            WriteJson(path + ".bak", new JObject { ["apiKey"] = secret, ["model"] = "legacy" });

            Require(!ProtectedConfigFile.TryLoad(path, out var config, out var loaded, out _),
                "corrupt protected main downgraded to plaintext backup");
            Require(config == null && loaded == null,
                "failed protected load released configuration or plaintext key");

            string truncatedDir = NewCaseDirectory(root, "corrupt-truncated");
            string truncated = Path.Combine(truncatedDir, "tts.json");
            string corruptBytes = "{\"apiKeyProtected\":\"truncated";
            File.WriteAllText(truncated, corruptBytes, Utf8NoBom);
            WriteJson(truncated + ".bak", new JObject { ["apiKey"] = secret });
            Require(!ProtectedConfigFile.TryLoad(truncated, out config, out loaded, out _),
                "truncated protected main downgraded to plaintext backup");
            Require(config == null && loaded == null,
                "truncated protected failure released plaintext state");
            Require(string.Equals(File.ReadAllText(truncated, Utf8NoBom), corruptBytes,
                    StringComparison.Ordinal),
                "fail-closed read mutated the corrupt protected commit");

            string adjacentDir = NewCaseDirectory(root, "corrupt-adjacent");
            string adjacent = Path.Combine(adjacentDir, "minimax.json");
            WriteJson(adjacent, new JObject
            {
                ["apiKeyProtected"] = "not-valid-dpapi",
                ["apiKey"] = secret
            });
            Require(!ProtectedConfigFile.TryLoad(adjacent, out config, out loaded, out _),
                "corrupt protected field fell back to adjacent plaintext field");
            Require(config == null && loaded == null,
                "adjacent corrupt+plain failure released plaintext state");
        }

        private static void TestRotationAndClearScrub(string root)
        {
            string dir = NewCaseDirectory(root, "rotate-clear");
            string path = Path.Combine(dir, "llm.json");
            string first = NewSecret("first");
            string second = NewSecret("second");
            var config = new JObject
            {
                ["baseUrl"] = "https://example.invalid/v1",
                ["model"] = "dummy"
            };
            Require(ProtectedConfigFile.TrySave(path, config, first, out var error),
                "initial key save failed: " + ErrorKind(error));
            Require(ProtectedConfigFile.TrySave(path, config, second, out error),
                "key rotation failed: " + ErrorKind(error));
            foreach (string candidate in new[] { path, path + ".bak" })
            {
                var parsed = JObject.Parse(File.ReadAllText(candidate, Utf8NoBom));
                string protectedValue = parsed[LocalSecretProtector.ProtectedApiKeyField]?.ToString();
                Require(!string.IsNullOrEmpty(protectedValue)
                        && LocalSecretProtector.TryUnprotect(protectedValue, out string actual, out _)
                        && string.Equals(actual, second, StringComparison.Ordinal),
                    Path.GetFileName(candidate) + " retained a stale key after rotation");
            }
            Require(ProtectedConfigFile.TrySave(path, config, "", out error),
                "key clear failed: " + ErrorKind(error));
            foreach (string candidate in new[] { path, path + ".bak" })
            {
                var parsed = JObject.Parse(File.ReadAllText(candidate, Utf8NoBom));
                Require(parsed.Property("apiKey", StringComparison.OrdinalIgnoreCase) == null
                        && parsed.Property(LocalSecretProtector.ProtectedApiKeyField,
                            StringComparison.OrdinalIgnoreCase) == null,
                    Path.GetFileName(candidate) + " retained secret material after clear");
            }
            Require(!File.Exists(path + ".tmp"), "rotation/clear left a tmp replica");
        }

        private static void TestStrictCommitValidator(string root)
        {
            string dir = NewCaseDirectory(root, "strict-commit");
            string secret = NewSecret("strict");
            string plainPath = Path.Combine(dir, "plain.json");
            Require(!ProtectedConfigFile.TryWriteJsonDurable(plainPath,
                    new JObject { ["apiKey"] = secret }, out _),
                "commit validator accepted legacy plaintext apiKey");
            Require(!File.Exists(plainPath) && !File.Exists(plainPath + ".bak"),
                "rejected plaintext commit wrote a replica");

            string aliasPath = Path.Combine(dir, "alias.json");
            Require(!ProtectedConfigFile.TryWriteJsonDurable(aliasPath,
                    new JObject { ["ApiKey"] = secret }, out _),
                "commit validator accepted case-variant plaintext ApiKey");
            Require(!File.Exists(aliasPath) && !File.Exists(aliasPath + ".bak"),
                "rejected alias commit wrote a replica");

            string nestedPath = Path.Combine(dir, "nested.json");
            Require(!ProtectedConfigFile.TryWriteJsonDurable(nestedPath,
                    new JObject { ["provider"] = new JObject { ["apiKey"] = secret } }, out _),
                "commit validator accepted nested plaintext apiKey");
            Require(!File.Exists(nestedPath) && !File.Exists(nestedPath + ".bak"),
                "rejected nested commit wrote a replica");

            string hiddenPath = Path.Combine(dir, "hidden-secret.json");
            Require(!ProtectedConfigFile.TrySave(hiddenPath,
                    new JObject { ["note"] = secret }, secret, out _),
                "save accepted plaintext key bytes hidden in a non-secret field");
            Require(!File.Exists(hiddenPath) && !File.Exists(hiddenPath + ".bak"),
                "rejected hidden-secret save wrote a replica");
        }

        private static void TestReplicaScrubFailureIsFailClosed(string root)
        {
            string dir = NewCaseDirectory(root, "scrub-failure");
            string path = Path.Combine(dir, "tts.json");
            string secret = NewSecret("locked-backup");
            Require(ProtectedConfigFile.TrySave(path,
                    new JObject { ["provider"] = "openai", ["model"] = "dummy" },
                    secret, out var saveError),
                "locked-backup setup failed: " + ErrorKind(saveError));
            WriteJson(path + ".bak", new JObject { ["apiKey"] = secret });

            JObject config;
            string loaded;
            using (new FileStream(path + ".bak", FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Require(!ProtectedConfigFile.TryLoad(path, out config, out loaded, out _),
                    "load succeeded although plaintext backup could not be scrubbed");
                Require(config == null && loaded == null,
                    "scrub failure released decrypted configuration or key");
            }

            Require(ProtectedConfigFile.TryLoad(path, out config, out loaded, out var loadError),
                "load did not recover after replica lock was released: " + ErrorKind(loadError));
            Require(config != null && string.Equals(loaded, secret, StringComparison.Ordinal),
                "post-lock protected load returned inconsistent data");
            AssertCommittedReplicasContainNoSecret(path, secret);
        }

        private static void TestAllProtectedConfigKinds(string root)
        {
            foreach (string fileName in new[] { "llm.json", "tts.json", "minimax.json" })
            {
                string dir = NewCaseDirectory(root, "kind-" + Path.GetFileNameWithoutExtension(fileName));
                string path = Path.Combine(dir, fileName);
                string secret = NewSecret(Path.GetFileNameWithoutExtension(fileName));
                var config = new JObject
                {
                    ["baseUrl"] = "https://example.invalid/v1",
                    ["model"] = "dummy"
                };
                Require(ProtectedConfigFile.TrySave(path, config, secret, out var saveError),
                    fileName + " save failed: " + ErrorKind(saveError));
                Require(ProtectedConfigFile.TryLoad(path, out _, out var loaded, out var loadError),
                    fileName + " load failed: " + ErrorKind(loadError));
                Require(string.Equals(loaded, secret, StringComparison.Ordinal),
                    fileName + " key round-trip mismatch");
                AssertCommittedReplicasContainNoSecret(path, secret);
            }
        }

        private static void AssertCommittedReplicasContainNoSecret(string path, string secret)
        {
            Require(File.Exists(path), "committed main is missing");
            Require(File.Exists(path + ".bak"), "committed backup is missing");
            Require(!File.Exists(path + ".tmp"), "successful commit left a tmp replica");
            byte[] needle = Encoding.UTF8.GetBytes(secret);
            foreach (string candidate in new[] { path, path + ".bak" })
            {
                byte[] bytes = File.ReadAllBytes(candidate);
                Require(!Contains(bytes, needle), Path.GetFileName(candidate) + " contains plaintext key bytes");
                var parsed = JObject.Parse(Encoding.UTF8.GetString(bytes));
                Require(!parsed.DescendantsAndSelf().OfType<JProperty>()
                        .Any(p => string.Equals(p.Name, "apiKey", StringComparison.OrdinalIgnoreCase)),
                    Path.GetFileName(candidate) + " contains a plaintext apiKey field");
            }
        }

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            if (needle == null || needle.Length == 0) return false;
            if (haystack == null || haystack.Length < needle.Length) return false;
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return true;
            }
            return false;
        }

        private static string NewCaseDirectory(string root, string name)
        {
            string dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string NewSecret(string scope)
            => "jyl-test-" + scope + "-" + Guid.NewGuid().ToString("N");

        private static void WriteJson(string path, JObject value)
            => File.WriteAllText(path, value.ToString(Formatting.Indented), Utf8NoBom);

        private static string ErrorKind(string error)
            => string.IsNullOrWhiteSpace(error) ? "unspecified" : "reported";

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
