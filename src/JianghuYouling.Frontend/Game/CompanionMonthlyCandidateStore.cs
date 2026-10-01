using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace JianghuYouling
{
    /// <summary>
    /// Per-save active-person list shared by monthly actions and NPC proactive messages. Current
    /// eligible companions are included by default; Included adds ordinary NPCs, while Excluded
    /// lets the player remove a companion without that companion reappearing on the next refresh.
    /// </summary>
    public static class CompanionMonthlyCandidateStore
    {
        private const int CurrentVersion = 1;
        private const int MaxIds = 512;
        private static readonly object Gate = new object();

        private sealed class State
        {
            public int Version = CurrentVersion;
            public uint WorldId;
            public int TaiwuId;
            public List<int> Included = new List<int>();
            public List<int> Excluded = new List<int>();
        }

        public sealed class Snapshot
        {
            public List<int> Included = new List<int>();
            public List<int> Excluded = new List<int>();
        }

        private static string PathFor(int taiwuId)
            => Path.Combine(JianghuYoulingPaths.Intents, "monthly_candidates_" + taiwuId + ".json");

        public static Snapshot Load(int taiwuId)
        {
            lock (Gate)
            {
                State state = LoadState(taiwuId);
                return new Snapshot
                {
                    Included = new List<int>(state.Included),
                    Excluded = new List<int>(state.Excluded),
                };
            }
        }

        public static bool Add(int taiwuId, int npcId)
        {
            if (taiwuId <= 0 || npcId <= 0 || npcId == taiwuId) return false;
            lock (Gate)
            {
                State state = LoadState(taiwuId);
                state.Excluded.Remove(npcId);
                if (!state.Included.Contains(npcId)) state.Included.Add(npcId);
                Normalize(state, taiwuId);
                return SaveState(taiwuId, state);
            }
        }

        public static bool Remove(int taiwuId, int npcId, bool isCurrentCompanion)
        {
            if (taiwuId <= 0 || npcId <= 0 || npcId == taiwuId) return false;
            lock (Gate)
            {
                State state = LoadState(taiwuId);
                state.Included.Remove(npcId);
                if (isCurrentCompanion && !state.Excluded.Contains(npcId)) state.Excluded.Add(npcId);
                else if (!isCurrentCompanion) state.Excluded.Remove(npcId);
                Normalize(state, taiwuId);
                return SaveState(taiwuId, state);
            }
        }

        public static bool ReplaceIdentity(int taiwuId, int oldNpcId, int newNpcId)
        {
            if (taiwuId <= 0 || oldNpcId <= 0 || newNpcId <= 0 || oldNpcId == newNpcId) return true;
            lock (Gate)
            {
                State state = LoadState(taiwuId);
                bool included = state.Included.Remove(oldNpcId);
                bool excluded = state.Excluded.Remove(oldNpcId);
                if (included && !state.Included.Contains(newNpcId)) state.Included.Add(newNpcId);
                if (excluded && !state.Excluded.Contains(newNpcId)) state.Excluded.Add(newNpcId);
                Normalize(state, taiwuId);
                return SaveState(taiwuId, state);
            }
        }

        public static List<int> BuildEffective(int taiwuId, IList<int> eligibleCompanions,
            IList<int> eligibleExplicit)
        {
            Snapshot state = Load(taiwuId);
            var excluded = new HashSet<int>(state.Excluded);
            var result = new SortedSet<int>();
            if (eligibleCompanions != null)
                foreach (int id in eligibleCompanions)
                    if (id > 0 && id != taiwuId && !excluded.Contains(id)
                        && !ArchivedCharacterStatusStore.IsDead(taiwuId, id)) result.Add(id);
            if (eligibleExplicit != null)
                foreach (int id in eligibleExplicit)
                    if (id > 0 && id != taiwuId && !excluded.Contains(id)
                        && !ArchivedCharacterStatusStore.IsDead(taiwuId, id)) result.Add(id);
            return new List<int>(result);
        }

        private static State LoadState(int taiwuId)
        {
            string path = PathFor(taiwuId);
            try
            {
                if (!File.Exists(path)) return NewState(taiwuId);
                string json = File.ReadAllText(path);
                if (json.Length > DurableSettingsStore.Text) return NewState(taiwuId);
                State state = JsonConvert.DeserializeObject<State>(json) ?? NewState(taiwuId);
                if (state.Version != CurrentVersion || state.WorldId != JianghuYoulingPaths.CurrentWorldId
                    || state.TaiwuId != taiwuId) return NewState(taiwuId);
                Normalize(state, taiwuId);
                return state;
            }
            catch { return NewState(taiwuId); }
        }

        private static State NewState(int taiwuId)
            => new State { WorldId = JianghuYoulingPaths.CurrentWorldId, TaiwuId = taiwuId };

        private static void Normalize(State state, int taiwuId)
        {
            state.Version = CurrentVersion;
            state.WorldId = JianghuYoulingPaths.CurrentWorldId;
            state.TaiwuId = taiwuId;
            state.Included = NormalizeIds(state.Included, taiwuId);
            state.Excluded = NormalizeIds(state.Excluded, taiwuId);
            var excluded = new HashSet<int>(state.Excluded);
            state.Included.RemoveAll(excluded.Contains);
        }

        private static List<int> NormalizeIds(IList<int> source, int taiwuId)
        {
            var sorted = new SortedSet<int>();
            if (source != null)
                foreach (int id in source)
                {
                    if (id <= 0 || id == taiwuId) continue;
                    sorted.Add(id);
                    if (sorted.Count >= MaxIds) break;
                }
            return new List<int>(sorted);
        }

        private static bool SaveState(int taiwuId, State state)
        {
            try
            {
                Normalize(state, taiwuId);
                string json = JsonConvert.SerializeObject(state, Formatting.Indented);
                return DurableSettingsStore.Save(PathFor(taiwuId), json, DurableSettingsStore.Text,
                    value => !string.IsNullOrWhiteSpace(value) && value.Length <= DurableSettingsStore.Text);
            }
            catch { return false; }
        }
    }
}
