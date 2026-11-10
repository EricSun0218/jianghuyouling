using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Persona;
using JianghuYouling.Core.Prompt;
using JianghuYouling.Core.Security;
using JianghuYouling.Core.Memory;

// Runs the production portrait service, prompts, merge validation and setting persistence.
// Unity scheduling, snapshots and portrait storage are synthetic; no game/save is accessed.
namespace UnityEngine
{
    public sealed class WaitUntil
    {
        public readonly Func<bool> Ready;
        public WaitUntil(Func<bool> ready) { Ready = ready; }
    }
    public static class Debug
    {
        public static void Log(string text) { }
        public static void LogWarning(string text) { Console.WriteLine(text); }
    }
}
namespace JianghuYouling
{
    public static class JianghuYoulingPaths
    {
        public static string Root;
        public static string Settings => Path.Combine(Root, "Settings");
        public static string Personas => Path.Combine(Root, "Personas");
        public static string Memories => Path.Combine(Root, "Memories");
    }
    public static class WorldLifecycle
    {
        public static int Generation => 1;
        public static bool IsSameWorld(int generation) => generation == Generation;
    }
    public sealed class NpcSnapshot
    {
        public int NpcId = 92001, TaiwuId = 92000;
        public string Name = "沈砚";
        public List<(int date, string type, string text)> LifeRecords = new List<(int, string, string)>();
        public List<(int date, string type, string text)> Secrets = new List<(int, string, string)>();
    }
    public sealed class TalkEntryHost
    {
        public static readonly TalkEntryHost Instance = new TalkEntryHost();
        public readonly Queue<IEnumerator> Jobs = new Queue<IEnumerator>();
        public void StartCoroutine(IEnumerator routine) { Jobs.Enqueue(routine); }
    }
    public static class LlmService
    {
        public static OpenAiCompatibleClient Client;
        public static int ClientRequests;
        public static OpenAiCompatibleClient GetBackgroundClient() { ClientRequests++; return Client; }
    }
    public static class PortraitStore
    {
        public static string Portrait, Origin, MemoryFingerprint, SourceFingerprint;
        public static int Writes;
        public static string GetPortrait(NpcSnapshot snap) => Portrait;
        public static string BuildSimple(NpcSnapshot snap) => PortraitDistiller.CustomPersonaAppendFallback(null);
        public static bool NeedsUpgrade(NpcSnapshot snap, string source) => source != SourceFingerprint || Origin != "llm-custom-append";
        public static string GetMemoryFingerprint(NpcSnapshot snap) => MemoryFingerprint;
        public static string GetSourceFingerprint(NpcSnapshot snap) => SourceFingerprint;
        public static bool Save(NpcSnapshot snap, string text, string origin, int count, string memory, string source)
        {
            Portrait = text; Origin = origin; MemoryFingerprint = memory; SourceFingerprint = source; Writes++;
            return true;
        }
        public static NpcProfileForPrompt BuildProfile(NpcSnapshot snap) => new NpcProfileForPrompt
        {
            NpcId = snap.NpcId, TaiwuId = snap.TaiwuId, Name = snap.Name,
            Gender = "男", TaiwuName = "太吾", TaiwuGender = "女", Age = 31,
            Behavior = "刚正", OrgTitle = "江湖散人", GradeName = "七品",
            Relation = "好友", FavorLevel = "亲近", PersonalitiesText = "守信、审慎", FeaturesText = "重诺"
        };
    }
    internal static class Program
    {
        private static void Check(bool value, string label)
        {
            if (!value) throw new Exception(label);
            Console.WriteLine("PASS " + label);
        }
        private static async Task Drive(IEnumerator root)
        {
            var stack = new Stack<IEnumerator>(); stack.Push(root);
            DateTime deadline = DateTime.UtcNow.AddMinutes(8);
            try
            {
                while (stack.Count > 0)
                {
                    if (DateTime.UtcNow > deadline) throw new TimeoutException("Portrait coroutine timeout");
                    var top = stack.Peek();
                    if (!top.MoveNext()) { stack.Pop(); (top as IDisposable)?.Dispose(); continue; }
                    if (top.Current is IEnumerator nested) stack.Push(nested);
                    else if (top.Current is UnityEngine.WaitUntil wait)
                        while (!wait.Ready())
                        {
                            if (DateTime.UtcNow > deadline) throw new TimeoutException("Portrait request timeout");
                            await Task.Delay(20);
                        }
                }
            }
            finally { while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose(); }
        }
        private static void Remember(int month, string content)
        {
            var store = NpcMemoryStore.Load(JianghuYoulingPaths.Memories, "92000", "92001");
            store.Add(new MemoryEntry { Content = content, Type = MemoryType.Promise,
                WorldDate = month, Importance = 9, SourceKind = "dialogue", SourceId = "fixture-" + month });
            Check(store.Save(), "isolated evidence memory saved " + month);
        }
        public static async Task<int> Main(string[] args)
        {
            if (args.Length != 2 || args[0] != "--protected-config")
            {
                Console.WriteLine("Live opt-in only: --protected-config <path>. No game RPC is used.");
                return 2;
            }
            string root = Path.Combine(Path.GetTempPath(), "jyl_portrait_live_" + Guid.NewGuid().ToString("N"));
            JianghuYoulingPaths.Root = root;
            try
            {
                if (!ProtectedConfigFile.TryLoad(args[1], out var config, out var key, out _))
                    throw new InvalidOperationException("Protected model config unavailable");
                string model = config["bgModel"]?.ToString();
                if (string.IsNullOrWhiteSpace(model)) model = config["model"]?.ToString();
                LlmService.Client = new OpenAiCompatibleClient(config["baseUrl"]?.ToString(), config["chatPath"]?.ToString(), key, model);
                key = null;
                var snap = new NpcSnapshot();
                snap.LifeRecords.Add((100, "关系", "长期共同追查旧案后，沈砚与太吾结成案卷同盟，约定继续共同查案。"));
                const string authored = "沈砚固定自称为 JHYL_PERSONA_ANCHOR；审慎守信，重视证据。";
                Check(PersonaStore.Save(JianghuYoulingPaths.Personas, "92000", "92001", authored, "replace"), "isolated custom persona saved");
                Check(PersonaAutoAppendStore.Load(), "automatic append defaults on");
                Remember(100, "在与太吾共同追查旧案后，我答应今后与太吾继续共同查案，并长期互通旧案线索。此约定是刚刚作出的。"
                    + "太吾同意与我共同守护乡民案卷，我也明确答应每年与太吾一起核对案卷。");
                string ready = null;
                await Drive(PortraitService.EnsureSeededReady(snap, value => ready = value));
                Console.WriteLine("automatic origin=" + PortraitStore.Origin + " requests=" + LlmService.ClientRequests
                    + " writes=" + PortraitStore.Writes + " chars=" + (ready?.Length ?? 0)
                    + " heading=" + PortraitDistiller.IsCustomPersonaAppendLayer(ready)
                    + " bullet=" + (ready?.Contains("- ") ?? false)
                    + " restatesAuthored=" + (ready?.Contains("JHYL_PERSONA_ANCHOR") ?? false));
                Check(PortraitStore.Origin == "llm-custom-append" && PortraitDistiller.IsCustomPersonaAppendLayer(ready)
                    && !ready.Contains("JHYL_PERSONA_ANCHOR"), "enabled service accepts validated real model result, including no-new-content");
                // Automatic append may legitimately abstain. Use the existing explicit update
                // path to establish nonempty real-model content for the preservation checks.
                if (!ready.Contains("- "))
                {
                    await Drive(PortraitService.RegenerateReady(snap, value => ready = value));
                    Check(PortraitStore.Origin == "llm-custom-append" && ready.Contains("- "),
                        "explicit setup produces real supplement when automatic pass abstains");
                }
                string prior = ready;
                int requests = LlmService.ClientRequests, writes = PortraitStore.Writes;
                Check(PersonaAutoAppendStore.Save(false) && !PersonaAutoAppendStore.Load(), "global setting persists off");
                snap.LifeRecords.Add((101, "经历", "后来沈砚与太吾共同找回被盗案卷，约定每年共同核对旧案记录。"));
                Remember(101, "我刚与太吾找回了被盗的旧案卷，并答应长期共同保护曾帮助查案的证人。");
                await Drive(PortraitService.EnsureSeeded(snap, value => ready = value));
                await Drive(PortraitService.EnsureSeededReady(snap, value => ready = value));
                await Drive(PortraitService.Consolidate(snap));
                Check(ready == prior && PortraitStore.Portrait == prior && PortraitStore.Writes == writes,
                    "disabled keeps using real generated supplement without rewriting it");
                Check(LlmService.ClientRequests == requests && TalkEntryHost.Instance.Jobs.Count == 0,
                    "disabled never acquires model client or schedules generation");
                await Drive(PortraitService.RegenerateReady(snap, value => ready = value));
                Check(LlmService.ClientRequests == requests + 1 && PortraitStore.Writes == writes + 1
                    && PortraitStore.Origin == "llm-custom-append", "manual update while disabled uses real model and commits");
                Check(PersonaStore.Load(JianghuYoulingPaths.Personas, "92000", "92001") == authored,
                    "player authored persona remains unchanged");
                Check(PersonaAutoAppendStore.Save(true), "global setting re-enabled");
                snap.LifeRecords.Add((102, "经历", "沈砚把多年积累的案卷目录交由太吾共同保管，确立了长期共同保管案卷的约定。"));
                Remember(102, "我刚把多年积累的案卷目录交给太吾共同保管，今后我与太吾各保留一份，若一方遭遇不测，另一方继续查案。");
                PortraitService.GetImmediateAndScheduleUpgrade(snap);
                while (TalkEntryHost.Instance.Jobs.Count > 0) await Drive(TalkEntryHost.Instance.Jobs.Dequeue());
                Check(LlmService.ClientRequests == requests + 2 && PortraitStore.Writes == writes + 2
                    && PortraitStore.Origin == "llm-custom-append", "re-enabled background append uses real model and commits");
                Console.WriteLine("PASS production portrait service live; synthetic snapshots/storage; game RPC calls=0");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message); return 1; }
            finally
            {
                PortraitService.ResetForWorldExit();
                // root is a fresh GUID-named test-only directory, never a user save/config root.
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
