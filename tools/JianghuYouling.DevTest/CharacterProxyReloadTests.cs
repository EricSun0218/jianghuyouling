using System;
using System.IO;
using System.Linq;
using System.Reflection;
using JianghuYouling.Core.Memory;
using JianghuYouling.Core.Persona;

namespace JianghuYouling.DevTest
{
    internal static class CharacterProxyReloadTests
    {
        internal static void Run()
        {
            foreach (int date in new[] { 177, 178, 179 }) RunReload(178, date);
            RunReload(-1, 177);
            Console.WriteLine("[PASS] Proxy reload preserves identity, special portrait and content across all month boundaries");
        }

        private static void RunReload(int copiedDate, int restoredDate)
        {
            const int taiwu = 7451, source = 15427, proxy = 19522;
            string previousRoot = JianghuYoulingPaths.Root;
            uint previousWorld = JianghuYoulingPaths.CurrentWorldId;
            bool previousDateReady = WorldLifecycle.HasWorldDate;
            BasicGameData previousData = SingletonObject.DataForTest;
            string root = Path.Combine(Path.GetTempPath(), "JHYL_ProxyReload_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                JianghuYoulingPaths.Root = root;
                JianghuYoulingPaths.CurrentWorldId = 557117641;
                SingletonObject.DataForTest = new BasicGameData { CurrDate = 178 };
                WorldLifecycle.HasWorldDate = true;
                CharacterProxyIdentityService.ResetForWorldExit();
                TalkOrchestrator.ClearedNpcIdsForTest.Clear();
                var register = typeof(CharacterProxyIdentityService).GetMethod("Register", BindingFlags.NonPublic | BindingFlags.Static);
                Check((bool)register.Invoke(null, new object[] { taiwu, source, proxy, (short)55, true, copiedDate }), "initial copy mapping");
                Directory.CreateDirectory(JianghuYoulingPaths.Novels);
                Directory.CreateDirectory(JianghuYoulingPaths.Personas);
                Directory.CreateDirectory(JianghuYoulingPaths.Memories);
                Directory.CreateDirectory(JianghuYoulingPaths.ChatLogs);
                File.WriteAllText(Path.Combine(JianghuYoulingPaths.Novels, "Novel_" + taiwu + "_" + proxy + ".json"), "existing novel");
                File.WriteAllText(PersonaStore.CanonicalPath(JianghuYoulingPaths.Personas, proxy.ToString()), "existing persona");
                File.WriteAllText(Path.Combine(JianghuYoulingPaths.Memories, "LifeExperience_" + taiwu + "_" + proxy + ".json"), "existing life summary");
                File.WriteAllText(Path.Combine(JianghuYoulingPaths.ChatLogs, "chat_history.json"), "existing history");
                var memory = NpcMemoryStore.Load(JianghuYoulingPaths.Memories, taiwu.ToString(), proxy.ToString());
                memory.Add(new MemoryEntry { Id = "existing-memory", Content = "聊天记忆保留", Type = MemoryType.Event,
                    Importance = 7, WorldDate = 178, SourceKind = "chat", SourceId = "chat-178" });
                Check(memory.Save(), "initial memory");
                var retainedFiles = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                    .ToDictionary(p => p, p => File.ReadAllBytes(p));

                CharacterProxyIdentityService.ResetForWorldExit();
                WorldLifecycle.HasWorldDate = false;
                SingletonObject.DataForTest.CurrDate = 0;
                Check(CharacterProxyIdentityService.TryGetDisplayTemplate(taiwu, proxy, out short early) && early == 55, "early skin");
                WorldLifecycle.HasWorldDate = true;
                SingletonObject.DataForTest.CurrDate = restoredDate;
                for (int reload = 0; reload < 2; reload++)
                {
                    CharacterProxyIdentityService.ResetForWorldExit();
                    Check(CharacterProxyIdentityService.ResolveKnown(taiwu, source) == proxy, "proxy route retained");
                    Check(CharacterProxyIdentityService.TryGetDisplayTemplate(taiwu, proxy, out short skin) && skin == 55, "special skin retained");
                    Check(CharacterProxyIdentityService.IsKnownProxy(taiwu, proxy), "known proxy retained");
                    Check(CharacterProxyIdentityService.ResolveKnown(taiwu, 202) == 202, "ordinary character unaffected");
                    foreach (var file in retainedFiles)
                        Check(File.Exists(file.Key) && File.ReadAllBytes(file.Key).SequenceEqual(file.Value),
                            "identity file byte-identical: " + Path.GetFileName(file.Key));
                }
                Check(TalkOrchestrator.ClearedNpcIdsForTest.Count == 0, "proxy reload never clears chat");
            }
            finally
            {
                CharacterProxyIdentityService.ResetForWorldExit();
                JianghuYoulingPaths.Root = previousRoot;
                JianghuYoulingPaths.CurrentWorldId = previousWorld;
                WorldLifecycle.HasWorldDate = previousDateReady;
                SingletonObject.DataForTest = previousData;
                TalkOrchestrator.ClearedNpcIdsForTest.Clear();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private static void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("Proxy rollback retention: " + label);
        }
    }
}
