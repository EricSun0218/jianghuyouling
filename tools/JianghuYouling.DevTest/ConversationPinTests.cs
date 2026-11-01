using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JianghuYouling.Core.Behavior;
using Newtonsoft.Json;
using Nav = JianghuYouling.ConversationNavigationStore;
using Paths = JianghuYouling.JianghuYoulingPaths;

namespace JianghuYouling.DevTest
{
    internal static class ConversationPinTests
    {
        private sealed class Entry
        {
            internal string Id;
            internal long Activity;
            internal bool Pinned;
        }

        public static void Run()
        {
            string originalRoot = Paths.Root;
            uint originalWorld = Paths.CurrentWorldId;
            string root = Path.Combine(Path.GetTempPath(), "jyl_pins_" + Guid.NewGuid().ToString("N"));
            try
            {
                Paths.Root = Path.Combine(root, "world-a");
                Paths.CurrentWorldId = 91234;
                Nav.ResetCacheForWorldExit();
                Directory.CreateDirectory(Paths.Intents);
                const int player = 1001;
                const string single = "single:1001:2001", group = "group:1001:id:test-group", assistant = "assistant:1001";
                string path = Path.Combine(Paths.Intents, "conversation_navigation_1001.json");

                Require(Nav.Hide(player, single), "existing hide state");
                foreach (string id in new[] { single, group, assistant })
                    Require(Nav.SetPinned(player, id, true), "pin all conversation kinds");
                Require(Nav.SetPinned(player, single, true) && Nav.Load(player).Pinned.Count == 3, "pin idempotence");
                Nav.Load(player).Pinned.Clear();
                Require(Nav.Load(player).Pinned.Count == 3, "snapshot cannot mutate cached pins");
                Nav.ResetCacheForWorldExit();
                Require(Nav.Load(player).Pinned.Count == 3 && Nav.Load(player).Hidden.Contains(single), "reload and hide precedence");
                Require(Nav.Restore(player, single) && Nav.Load(player).Pinned.Contains(single), "restore retains pin");
                Require(Nav.SetPinned(player, group, false) && !Nav.Load(player).Pinned.Contains(group), "unpin");
                Nav.ResetCacheForWorldExit();
                Require(!Nav.Load(player).Pinned.Contains(group) && Nav.Load(player).Pinned.Count == 2, "unpin persists");
                Require(Nav.Hide(player, single) && Nav.ReplaceSingleIdentity(player, 2001, 3001), "identity transfer");
                Require(Nav.Load(player).Pinned.Contains("single:1001:3001")
                    && Nav.Load(player).Hidden.Contains("single:1001:3001")
                    && !Nav.Load(player).Pinned.Contains(single) && !Nav.Load(player).Hidden.Contains(single), "both flags transfer");
                Require(Nav.Load(1002).Pinned.Count == 0, "player isolation");

                byte[] committed = File.ReadAllBytes(path);
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Require(!Nav.SetPinned(player, group, true), "failed commit rejects pin");
                    Require(!Nav.SetPinned(player, assistant, false), "failed commit rejects unpin");
                    Require(!Nav.Load(player).Pinned.Contains(group) && Nav.Load(player).Pinned.Contains(assistant), "failed commit leaves cached view unchanged");
                }
                Require(File.ReadAllBytes(path).SequenceEqual(committed), "failed commit preserves disk state");
                Nav.ResetCacheForWorldExit();
                Require(!Nav.Load(player).Pinned.Contains(group) && Nav.Load(player).Pinned.Contains(assistant), "failed write never appears after restart");

                Paths.Root = Path.Combine(root, "world-b");
                Paths.CurrentWorldId = 91235;
                Require(Nav.Load(player).Pinned.Count == 0, "world cache isolation");
                Directory.CreateDirectory(Paths.Intents);
                File.WriteAllBytes(Path.Combine(Paths.Intents, "conversation_navigation_1001.json"), committed);
                Nav.ResetCacheForWorldExit();
                Require(!Nav.Load(player).LoadReliable && !Nav.SetPinned(player, single, true), "foreign world file rejected");
                Paths.Root = Path.Combine(root, "world-a");
                Paths.CurrentWorldId = 91234;
                Nav.ResetCacheForWorldExit();
                Require(Nav.Load(player).Pinned.Contains(assistant), "returning world restores pins");

                // Seed a full shard directly, then exercise the real overflow write/read paths.
                const int manyPlayer = 3001;
                string manyPath = Path.Combine(Paths.Intents, "conversation_navigation_3001.json");
                var many = Enumerable.Range(0, 2048).Select(i => "single:3001:" + i).ToArray();
                File.WriteAllText(manyPath, JsonConvert.SerializeObject(new
                {
                    Version = 1, WorldId = Paths.CurrentWorldId, TaiwuId = manyPlayer,
                    Hidden = new[] { "assistant:3001" }, Pinned = many,
                }));
                Require(Nav.SetPinned(manyPlayer, "single:3001:9000", true), "pin shard overflow");
                Nav.ResetCacheForWorldExit();
                Require(Nav.Load(manyPlayer).Pinned.Count == 2049 && Nav.Load(manyPlayer).Hidden.Contains("assistant:3001"), "overflow retains all flags");
                Require(Nav.SetPinned(manyPlayer, "single:3001:9000", false), "unpin overflow shard");

                // Existing navigation metadata is user data: adding pin state never discards hides.
                const int hiddenOnlyPlayer = 4001;
                File.WriteAllText(Path.Combine(Paths.Intents, "conversation_navigation_4001.json"),
                    JsonConvert.SerializeObject(new { Version = 1, WorldId = Paths.CurrentWorldId,
                        TaiwuId = hiddenOnlyPlayer, Hidden = new[] { "assistant:4001" } }));
                Require(Nav.SetPinned(hiddenOnlyPlayer, "assistant:4001", true)
                    && Nav.Load(hiddenOnlyPlayer).Hidden.Contains("assistant:4001"), "existing hidden data preserved");

                File.WriteAllText(path, "{broken");
                File.WriteAllText(path + ".bak", "{broken");
                Nav.ResetCacheForWorldExit();
                Require(!Nav.Load(player).LoadReliable && Nav.Load(player).Pinned.Count == 0, "corrupt load exposes no partial pins");
                Require(!Nav.SetPinned(player, single, true) && !Nav.SetPinned(player, assistant, false), "corrupt state refuses edits");
                Require(File.ReadAllText(path) == "{broken", "corrupt evidence not overwritten");
                Require(!Nav.SetPinned(player, "unknown:x", true), "invalid identity rejected");
                TestOrder();
                TestClickGuard();
                Console.WriteLine("PASS conversation pins: all kinds, durable edits, isolation, failure safety, ordering and pooled clicks");
            }
            finally
            {
                Nav.ResetCacheForWorldExit();
                Paths.Root = originalRoot;
                Paths.CurrentWorldId = originalWorld;
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private static int Compare(Entry a, Entry b)
            => ConversationNavigationOrder.Compare(a.Pinned, a.Activity, a.Id, a.Id,
                b.Pinned, b.Activity, b.Id, b.Id);

        private static void TestOrder()
        {
            var firstPin = new Entry { Id = "a", Activity = 2, Pinned = true };
            var secondPin = new Entry { Id = "b", Activity = 1, Pinned = true };
            var plain = new Entry { Id = "c", Activity = 9 };
            var entries = new List<Entry> { plain, secondPin, firstPin };
            entries.Sort(Compare);
            Require(entries.Select(x => x.Id).SequenceEqual(new[] { "a", "b", "c" }), "pins first then recent activity");
            entries.Remove(plain);
            plain.Activity = 20;
            ConversationNavigationOrder.InsertSorted(entries, plain, Compare);
            Require(entries.Last() == plain, "ordinary new message cannot pass pins");
            entries.Remove(secondPin);
            secondPin.Activity = 21;
            ConversationNavigationOrder.InsertSorted(entries, secondPin, Compare);
            Require(entries.First() == secondPin, "pinned new message moves within pin group");
            firstPin.Pinned = false;
            entries.Sort(Compare);
            Require(entries.Select(x => x.Id).SequenceEqual(new[] { "b", "c", "a" }), "unpin restores recent-message order");
            // Fast insertion and full refresh agree for every tie and pin/activity combination.
            var random = new Random(12345);
            var candidates = Enumerable.Range(0, 250).Select(i => new Entry
                { Id = i.ToString("D4"), Activity = random.Next(10), Pinned = random.Next(2) == 0 }).ToList();
            var fast = new List<Entry>();
            foreach (Entry candidate in candidates) ConversationNavigationOrder.InsertSorted(fast, candidate, Compare);
            candidates.Sort(Compare);
            Require(fast.SequenceEqual(candidates), "fast path equals full sort including ties");
        }

        private static void TestClickGuard()
        {
            var guard = new NavigationRowBindingGuard();
            guard.Bind("single:1001:2001");
            guard.CapturePointerDown(NavigationRowAction.Pin);
            Require(!guard.TryConsume(NavigationRowAction.Open, out _), "pin cannot open chat");
            Require(!guard.TryConsume(NavigationRowAction.Hide, out _), "pin cannot hide chat");
            Require(guard.TryConsume(NavigationRowAction.Pin, out _), "pin uses independent click capture");
            Require(!guard.TryConsume(NavigationRowAction.Pin, out _), "pin click consumed once");
            guard.CapturePointerDown(NavigationRowAction.Pin);
            guard.Bind("group:1001:id:another");
            Require(!guard.TryConsume(NavigationRowAction.Pin, out _), "rebound row rejects old pin click");
            guard.CapturePointerDown(NavigationRowAction.Pin);
            guard.Clear();
            Require(!guard.TryConsume(NavigationRowAction.Pin, out _), "world/pool clear rejects pin click");
        }

        private static void Require(bool condition, string label)
        {
            if (!condition) throw new Exception("Conversation pins: " + label);
        }
    }
}
