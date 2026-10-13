using System;
using System.Collections.Generic;
using GameData.Domains.Mod;

namespace JianghuYouling.Backend
{
    // A loaded archive retains separate native ModId buckets for each release.
    // Read the newest applicable value without copying or modifying any bucket.
    internal static class CharacterProxyArchiveReader
    {
        internal static ModId ResolveLoadedMod(IEnumerable<ModId> loadedMods, string modIdString)
        {
            ModId? result = null;
            foreach (ModId id in loadedMods)
            {
                if (!string.Equals(id.ToString(), modIdString, StringComparison.Ordinal)) continue;
                if (result.HasValue) throw new InvalidOperationException("Ambiguous loaded Mod identity");
                result = id;
            }
            return result ?? throw new InvalidOperationException("Loaded Mod identity unavailable");
        }

        internal static bool TryGetInt(IReadOnlyDictionary<ModId, SerializableModData> archive,
            ModId current, string key, out int value)
        {
            value = 0;
            if (archive == null || !archive.TryGetValue(current, out var currentData) || currentData == null)
                throw new InvalidOperationException("Current Mod archive unavailable");
            SerializableModData selected = null;
            ulong selectedVersion = 0;
            foreach (var pair in archive)
            {
                ModId id = pair.Key;
                if (id.FileId != current.FileId || id.Source != current.Source || id.Version > current.Version)
                    continue;
                if (pair.Value == null) throw new InvalidOperationException("Mod archive bucket unavailable");
                if (!pair.Value.ContainsKey(key) || (selected != null && id.Version <= selectedVersion)) continue;
                selected = pair.Value;
                selectedVersion = id.Version;
            }
            if (selected == null) return false;
            // Explicit zero is a newer cleared state, not a reason to resurrect an older value.
            // A malformed newer value must also never fall through to an older identity.
            if (!selected.Get(key, out value))
                throw new InvalidOperationException("Invalid character proxy archive value");
            return true;
        }
    }
}
