using System;
using System.Collections.Generic;
using System.Linq;
using GameData.Domains.Mod;
using JianghuYouling.Backend;
using JianghuYouling.Core.Behavior;

namespace JianghuYouling.BackendContractTests
{
    internal static class CharacterProxyArchiveTests
    {
        private const string Forward = "character_proxy_source_v1_100";
        private const string Reverse = "character_proxy_reverse_v1_200";
        private static ModId Id(int version, ulong file = 42, byte source = 1)
            => new ModId(file, ((ulong)34 << 32) + (ulong)version, source);
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("Proxy archive: " + message);
        }
        private static void Throws(Action action, string message)
        {
            try { action(); }
            catch (InvalidOperationException) { return; }
            throw new Exception("Proxy archive did not fail closed: " + message);
        }
        private static int Read(Dictionary<ModId, SerializableModData> archive, ModId current, string key)
        {
            Check(CharacterProxyArchiveReader.TryGetInt(archive, current, key, out int value), key);
            return value;
        }
        internal static void Run()
        {
            // These are the game's real ModId and SerializableModData types, not a string-key mock.
            ModId old = Id(29), brokenRelease = Id(30), current = Id(31);
            Check(old.ToString() == current.ToString() && !old.Equals(current), "native version key contract");
            var oldData = new SerializableModData();
            oldData.Set(Forward, 200);
            oldData.Set(Reverse, 100);
            oldData.Set("character_proxy_ever_joined_v1_200", 1);
            oldData.Set("character_proxy_replacement_v2_300", 200);
            oldData.Set("character_proxy_pending_legacy_v2_100", 300);
            var archive = new Dictionary<ModId, SerializableModData> {
                [old] = oldData, [brokenRelease] = new SerializableModData(),
                [current] = new SerializableModData()
            };
            Check(!archive[current].Get(Forward, out int _), "upgrade recreates empty current bucket");
            Check(Read(archive, current, Forward) == 200 && Read(archive, current, Reverse) == 100,
                "multiple upgrades recover both directions");
            Check(CharacterProxyIdentity.HasConsistentMapping(100, 200,
                Read(archive, current, Reverse), Read(archive, current, Forward), 900), "recovered ownership");
            Check(Read(archive, current, "character_proxy_ever_joined_v1_200") == 1, "join state retained");
            Check(Read(archive, current, "character_proxy_replacement_v2_300") == 200, "replacement retained");
            Check(Read(archive, current, "character_proxy_pending_legacy_v2_100") == 300, "pending state retained");
            Check(archive.Count == 3 && !archive[current].ContainsKey(Forward)
                && !archive[brokenRelease].ContainsKey(Forward) && oldData.Get(Forward, out int original)
                && original == 200, "reads never copy or mutate archive values");

            archive[brokenRelease].Set(Forward, 201);
            Check(Read(archive, current, Forward) == 201, "newest record wins regardless of enumeration order");
            var reversed = archive.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value);
            Check(Read(reversed, current, Forward) == 201, "reversed bucket order");
            Check(!CharacterProxyIdentity.HasConsistentMapping(100, 200,
                Read(archive, current, Reverse), Read(archive, current, Forward), 900), "stale pair rejected");
            archive[current].Set(Forward, 200);
            archive[current].Set(Reverse, 101);
            Check(!CharacterProxyIdentity.HasConsistentMapping(100, 200,
                Read(archive, current, Reverse), Read(archive, current, Forward), 900), "reused target rejected");
            archive[current].Set(Reverse, 100);
            Check(!CharacterProxyIdentity.HasConsistentMapping(100, 200, 100, 200, 200), "Taiwu never proxy");
            archive[current].Set("character_proxy_pending_legacy_v2_100", 0);
            Check(Read(archive, current, "character_proxy_pending_legacy_v2_100") == 0, "cleared pending not resurrected");
            archive[current].Set(Forward, 0);
            Check(Read(archive, current, Forward) == 0, "explicit invalidation shadows older mapping");

            var other = new SerializableModData();
            other.Set("foreign", 123);
            archive[Id(29, 43)] = other;
            archive[Id(29, 42, 0)] = other;
            archive[Id(32)] = other;
            Check(!CharacterProxyArchiveReader.TryGetInt(archive, current, "foreign", out _),
                "other files, sources and future versions excluded");
            var nextWorld = new Dictionary<ModId, SerializableModData> { [current] = new SerializableModData() };
            Check(!CharacterProxyArchiveReader.TryGetInt(nextWorld, current, Reverse, out _), "world reload has no stale cache");
            var malformed = new SerializableModData();
            malformed.Set(Reverse, "not an integer");
            archive[current] = malformed;
            Throws(() => Read(archive, current, Reverse), "malformed current must not revive old identity");
            Throws(() => CharacterProxyArchiveReader.TryGetInt(null, current, Reverse, out _), "missing archive");
            Throws(() => CharacterProxyArchiveReader.TryGetInt(
                new Dictionary<ModId, SerializableModData> { [old] = oldData }, current, Reverse, out _), "unloaded current bucket");
            Check(CharacterProxyArchiveReader.ResolveLoadedMod(new[] { Id(31, 43), current }, current.ToString())
                .Equals(current), "loaded identity resolution");
            Throws(() => CharacterProxyArchiveReader.ResolveLoadedMod(new[] { old, current }, current.ToString()),
                "ambiguous loaded identity");
            Throws(() => CharacterProxyArchiveReader.ResolveLoadedMod(new[] { Id(31, 43) }, current.ToString()),
                "missing loaded identity");
            Console.WriteLine("PASS native-version proxy archive upgrade regression");
        }

        internal static void VerifyIntegration(string backend)
        {
            foreach (string name in new[] { "Source", "Reverse", "Replacement", "PendingLegacy", "EverJoined" })
                Check(backend.Contains("TryGetCharacterProxyArchiveInt(CharacterProxy" + name + "Key("),
                    name + " reads use version-aware archive");
            Check(backend.Contains("CharacterProxyArchiveField?.GetValue(DomainManager.Mod)")
                && backend.Contains("ModDomain.GetLoadedModIds()"), "reads use current live domain and loaded identity");
            Check(!backend.Contains("catch { proxyId = 0; return false; }")
                && !backend.Contains("catch { sourceId = 0; return false; }"), "read failures cannot masquerade as absent identities");
            string loadGuard = backend.Substring(backend.IndexOf("internal static class LegacyCharacterProxyLoadGuardPatch",
                StringComparison.Ordinal));
            Check(loadGuard.Contains("catch (Exception e)")
                && loadGuard.Contains("[JHYL_PROXY_LOAD_IDENTITY_UNAVAILABLE]"), "native load guard isolates unavailable archive");
            int hunterStart = backend.IndexOf("internal static class CharacterProxyAnimalDowngradeGuardPatch", StringComparison.Ordinal);
            int hunterLookup = backend.IndexOf("!BackendPluginMain.IsCharacterProxy(character.GetId())", hunterStart, StringComparison.Ordinal);
            Check(backend.IndexOf("try", hunterStart, StringComparison.Ordinal) < hunterLookup,
                "native animal conversion guard catches archive read failure");
        }
    }
}
