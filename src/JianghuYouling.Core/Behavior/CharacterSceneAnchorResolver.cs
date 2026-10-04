using System;
using System.Collections.Generic;

namespace JianghuYouling.Core.Behavior
{
    /// <summary>Follows authoritative carried/following relationships to a physical map anchor.
    /// A missing endpoint or a cycle is unknown, never permission to reuse a stale location.</summary>
    public static class CharacterSceneAnchorResolver
    {
        public static bool TryResolve(int characterId, Func<int, int?> readAnchor, out int anchorId)
        {
            anchorId = 0;
            if (characterId <= 0 || readAnchor == null) return false;
            var visited = new HashSet<int>();
            int current = characterId;
            while (visited.Count < 64 && visited.Add(current))
            {
                int? next;
                try { next = readAnchor(current); }
                catch { return false; }
                if (!next.HasValue || next.Value <= 0) return false;
                if (next.Value == current) { anchorId = current; return true; }
                current = next.Value;
            }
            return false;
        }
    }
}
