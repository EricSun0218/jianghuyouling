param([string]$Root = (Join-Path $PSScriptRoot '..'))
$ErrorActionPreference = 'Stop'

# Compile the complete production service and toggle store. Only engine/model/storage
# surfaces are stubbed here; durable setting IO is separately covered by DevTest.
# No model, user configuration, game process or player save is accessed.
$service = Get-Content -LiteralPath (Join-Path $Root 'src/JianghuYouling.Frontend/Portrait/PortraitService.cs') -Raw
$store = Get-Content -LiteralPath (Join-Path $Root 'src/JianghuYouling.Frontend/Game/PersonaAutoAppendStore.cs') -Raw
$store = [regex]::Replace($store, '(?m)^using [^;]+;\r?\n', '')
$fixture = @'
namespace UnityEngine {
    public sealed class WaitUntil {
        public readonly Func<bool> Ready;
        public WaitUntil(Func<bool> ready) { Ready = ready; }
    }
    public static class Debug {
        public static void Log(string value) { }
        public static void LogWarning(string value) { }
    }
}
namespace JianghuYouling.Core.Llm {
    public enum LlmReasoningPolicy { Auto }
    public sealed class LlmResult { public bool Ok = true; public string Content = "new append"; }
    public sealed class OpenAiCompatibleClient {
        public int Calls;
        public bool Defer;
        public System.Threading.Tasks.TaskCompletionSource<LlmResult> Pending;
        public CancellationToken Token;
        public System.Threading.Tasks.Task<LlmResult> SendAsync(object messages, int count,
            CancellationToken ct, int timeoutSec, string tag, LlmReasoningPolicy reasoningPolicy) {
            Calls++; Token = ct;
            if (!Defer) return System.Threading.Tasks.Task.FromResult(new LlmResult());
            Pending = new System.Threading.Tasks.TaskCompletionSource<LlmResult>();
            return Pending.Task;
        }
    }
}
namespace JianghuYouling.Core.Prompt {
    public sealed class NpcProfileForPrompt { public string CustomPersona, CustomPersonaMode; }
    public static class TalkPromptBuilder { public static string FormatWorldMonth(int date) => date.ToString(); }
    public static class PortraitDistiller {
        public static bool UsesCustomPersonaAppendMode(NpcProfileForPrompt p) => !string.IsNullOrWhiteSpace(p.CustomPersona);
        public static object BuildMessages(string prior, NpcProfileForPrompt p, IList<string> life,
            IList<string> secrets, IList<string> memories, bool explicitAppendRequest = false) => new object();
        public static object BuildCustomAppendRepairMessages(string prior, NpcProfileForPrompt p,
            IList<string> life, IList<string> secrets, IList<string> memories, string raw, string reason,
            bool explicitAppendRequest) => new object();
        public static string Clean(string value) => value;
        public static bool TryMergeCustomPersonaAppend(string prior, string raw, NpcProfileForPrompt p,
            IList<string> life, IList<string> secrets, IList<string> memories, out string merged, out string reason) {
            merged = raw; reason = null; return !string.IsNullOrEmpty(raw);
        }
        public static bool IsEvidenceGrounded(string raw, NpcProfileForPrompt p, IList<string> life,
            IList<string> secrets, IList<string> memories, out string reason) { reason = null; return raw != null; }
        public static string CustomPersonaAppendFallback(string prior) => prior ?? "empty append";
        public static string SourceEvidenceFingerprint(NpcProfileForPrompt p, IList<string> life,
            IList<string> secrets, string unused) => "source";
    }
}
namespace JianghuYouling.Core.Persona {
    public static class PersonaStore {
        public static readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        public static string Load(string path, string taiwu, string npc) => Values.TryGetValue(npc, out var value) ? value : null;
        public static string LoadMode(string path, string taiwu, string npc) => "append";
    }
}
namespace JianghuYouling.Core.Memory {
    public sealed class MemoryEntry {
        public string Id, SourceKind, SourceId, Type, Keywords, Content;
        public bool Valid, IsCore;
        public int Importance, WorldDate;
    }
    public sealed class NpcMemoryStore {
        public bool LoadReliable = true;
        public List<MemoryEntry> All = new List<MemoryEntry>();
        public static NpcMemoryStore Load(string path, string taiwu, string npc) => new NpcMemoryStore();
    }
}
namespace JianghuYouling {
    public static class JianghuYoulingPaths {
        public static string Settings => "test-settings";
        public static string Personas => "test-personas";
        public static string Memories => "test-memories";
    }
    internal static class DurableSettingsStore {
        internal const int Small = 16384;
        internal static string Value;
        internal static bool FailSave;
        internal static bool TryLoad(string path, int max, Func<string, bool> validator, out string value) {
            value = Value; return validator(value);
        }
        internal static bool Save(string path, string value, int max, Func<string, bool> validator) {
            if (FailSave || !validator(value)) return false;
            Value = value; return true;
        }
    }
    public static class WorldLifecycle {
        public static int Generation = 1;
        public static bool IsSameWorld(int value) => value == Generation;
    }
    public sealed class NpcSnapshot {
        public int TaiwuId = 1, NpcId;
        public string Name = "fixture";
        public List<(int date, string type, string text)> LifeRecords = new List<(int, string, string)>();
        public List<(int date, string type, string text)> Secrets = new List<(int, string, string)>();
    }
    public static class LlmService {
        public static Core.Llm.OpenAiCompatibleClient Client;
        public static Core.Llm.OpenAiCompatibleClient GetBackgroundClient() => Client;
    }
    public sealed class TalkEntryHost {
        public static TalkEntryHost Instance = new TalkEntryHost();
        public readonly Queue<IEnumerator> Jobs = new Queue<IEnumerator>();
        public void StartCoroutine(IEnumerator job) { Jobs.Enqueue(job); }
    }
    public static class PortraitStore {
        public static readonly Dictionary<int, string> Values = new Dictionary<int, string>();
        public static int Writes;
        public static string GetPortrait(NpcSnapshot s) => Values.TryGetValue(s.NpcId, out var v) ? v : null;
        public static string BuildSimple(NpcSnapshot s) => "deterministic fallback";
        public static bool NeedsUpgrade(NpcSnapshot s, string fingerprint) => true;
        public static string GetMemoryFingerprint(NpcSnapshot s) => "old-memory";
        public static string GetSourceFingerprint(NpcSnapshot s) => "old-source";
        public static Core.Prompt.NpcProfileForPrompt BuildProfile(NpcSnapshot s) => new Core.Prompt.NpcProfileForPrompt();
        public static bool Save(NpcSnapshot s, string text, string origin, int memories, string fingerprint, string source) {
            Writes++; Values[s.NpcId] = text; return true;
        }
    }
    public static class PersonaAutoAppendTests {
        static int Checks;
        static readonly NpcSnapshot Custom = new NpcSnapshot { NpcId = 2 };
        static readonly NpcSnapshot Normal = new NpcSnapshot { NpcId = 3 };
        sealed class Runner {
            readonly Stack<IEnumerator> stack = new Stack<IEnumerator>();
            UnityEngine.WaitUntil wait;
            internal Runner(IEnumerator routine) { stack.Push(routine); }
            internal bool Pump() {
                for (int steps = 0; stack.Count > 0; steps++) {
                    if (steps > 500) throw new Exception("Coroutine did not settle");
                    if (wait != null && !wait.Ready()) return false;
                    wait = null;
                    var current = stack.Peek();
                    if (!current.MoveNext()) { stack.Pop(); (current as IDisposable)?.Dispose(); continue; }
                    if (current.Current is IEnumerator nested) stack.Push(nested);
                    else if (current.Current is UnityEngine.WaitUntil condition) wait = condition;
                }
                return true;
            }
        }
        static void Check(bool ok, string label) {
            if (!ok) throw new Exception("FAIL " + label);
            Checks++; Console.WriteLine("PASS " + label);
        }
        static void Reset(bool enabled = true) {
            PortraitService.ResetForWorldExit();
            WorldLifecycle.Generation = 1;
            DurableSettingsStore.Value = null; DurableSettingsStore.FailSave = false;
            PersonaAutoAppendStore.Save(enabled);
            Core.Persona.PersonaStore.Values.Clear();
            Core.Persona.PersonaStore.Values["2"] = "player custom persona";
            PortraitStore.Values.Clear(); PortraitStore.Writes = 0;
            LlmService.Client = new Core.Llm.OpenAiCompatibleClient();
            TalkEntryHost.Instance.Jobs.Clear();
        }
        static void DrainJobs() {
            while (TalkEntryHost.Instance.Jobs.Count > 0)
                if (!new Runner(TalkEntryHost.Instance.Jobs.Dequeue()).Pump()) throw new Exception("Unexpected pending request");
        }
        public static void RunAll() {
            Reset();
            PortraitService.GetImmediateAndScheduleUpgrade(Custom); DrainJobs();
            Check(LlmService.Client.Calls == 1 && PortraitStore.Writes == 1, "enabled custom persona auto-generates");

            Reset(false); PortraitStore.Values[2] = "existing supplement";
            string ready = PortraitService.GetImmediateAndScheduleUpgrade(Custom);
            new Runner(PortraitService.EnsureSeeded(Custom, v => ready = v)).Pump();
            new Runner(PortraitService.Consolidate(Custom)).Pump();
            Check(ready == "existing supplement" && PortraitStore.GetPortrait(Custom) == ready,
                "disabled still returns and preserves existing supplement");
            Check(TalkEntryHost.Instance.Jobs.Count == 0 && LlmService.Client.Calls == 0 && PortraitStore.Writes == 0,
                "disabled single chat and consolidation make no request or write");

            Reset(false); ready = null;
            new Runner(PortraitService.EnsureSeededReady(Custom, v => ready = v)).Pump();
            Check(!string.IsNullOrWhiteSpace(ready) && LlmService.Client.Calls == 0 && PortraitStore.Writes == 0,
                "disabled missing group portrait returns fallback without generation");
            new Runner(PortraitService.RegenerateReady(Custom, v => ready = v)).Pump();
            Check(ready == "new append" && LlmService.Client.Calls == 1 && PortraitStore.Writes == 1,
                "explicit AI update works while automatic append is disabled");

            Reset(false);
            PortraitService.GetImmediateAndScheduleUpgrade(Normal); DrainJobs();
            Check(LlmService.Client.Calls == 1 && PortraitStore.Writes == 1, "normal persona automatic generation unaffected");
            new Runner(PortraitService.Consolidate(Normal)).Pump();
            Check(LlmService.Client.Calls == 2 && PortraitStore.Writes == 2, "normal persona consolidation unaffected");
            PortraitStore.Values.Clear(); ready = null;
            new Runner(PortraitService.EnsureSeededReady(Normal, v => ready = v)).Pump();
            Check(ready == "new append" && LlmService.Client.Calls == 3, "normal group-seat portrait still generates");

            Reset(); PortraitService.GetImmediateAndScheduleUpgrade(Custom);
            PersonaAutoAppendStore.Save(false); PersonaAutoAppendStore.Save(true); DrainJobs();
            Check(LlmService.Client.Calls == 0 && PortraitStore.Writes == 0, "off-on cannot revive queued custom append");
            PortraitService.GetImmediateAndScheduleUpgrade(Custom); DrainJobs();
            Check(LlmService.Client.Calls == 1, "new automatic requests resume after re-enable");

            Reset(); PortraitService.GetImmediateAndScheduleUpgrade(Custom);
            var scheduled = TalkEntryHost.Instance.Jobs.Dequeue();
            Check(scheduled.MoveNext() && scheduled.Current is IEnumerator, "scheduled upgrade yields nested generator");
            PersonaAutoAppendStore.Save(false); PersonaAutoAppendStore.Save(true);
            new Runner((IEnumerator)scheduled.Current).Pump(); new Runner(scheduled).Pump();
            Check(LlmService.Client.Calls == 0 && PortraitStore.Writes == 0,
                "off-on between scheduling and nested coroutine start cannot revive old request");

            Reset(); ready = null;
            var entering = PortraitService.EnsureSeededReady(Custom, v => ready = v);
            Check(entering.MoveNext() && entering.Current is IEnumerator, "group setup yields nested generator");
            PersonaAutoAppendStore.Save(false); PersonaAutoAppendStore.Save(true);
            new Runner((IEnumerator)entering.Current).Pump(); new Runner(entering).Pump();
            Check(LlmService.Client.Calls == 0 && PortraitStore.Writes == 0 && !string.IsNullOrWhiteSpace(ready),
                "off-on before nested group generation keeps member but does not generate");

            Reset(); LlmService.Client.Defer = true;
            PortraitService.GetImmediateAndScheduleUpgrade(Custom);
            var pending = new Runner(TalkEntryHost.Instance.Jobs.Dequeue());
            Check(!pending.Pump() && LlmService.Client.Calls == 1, "background request reaches pending model");
            PersonaAutoAppendStore.Save(false); PersonaAutoAppendStore.Save(true);
            Check(LlmService.Client.Token.IsCancellationRequested, "disabling cancels in-flight custom append immediately");
            LlmService.Client.Pending.SetResult(new Core.Llm.LlmResult());
            Check(pending.Pump() && PortraitStore.Writes == 0, "late model result cannot write after off-on");

            Reset(); LlmService.Client.Defer = true; ready = null;
            var group = new Runner(PortraitService.EnsureSeededReady(Custom, v => ready = v));
            group.Pump(); PersonaAutoAppendStore.Save(false);
            Check(group.Pump() && !string.IsNullOrWhiteSpace(ready) && PortraitStore.Writes == 0,
                "cancelled group-seat request still supplies fallback so member can join");

            Reset(); PortraitStore.Values[2] = "existing supplement"; LlmService.Client.Defer = true;
            var update = new Runner(PortraitService.Consolidate(Custom)); update.Pump();
            PersonaAutoAppendStore.Save(false); LlmService.Client.Pending.SetResult(new Core.Llm.LlmResult());
            Check(update.Pump() && PortraitStore.GetPortrait(Custom) == "existing supplement" && PortraitStore.Writes == 0,
                "closing-chat consolidation cannot overwrite after disable");

            Reset(); LlmService.Client.Defer = true;
            var manual = new Runner(PortraitService.RegenerateReady(Custom, v => ready = v)); manual.Pump();
            PersonaAutoAppendStore.Save(false);
            Check(!LlmService.Client.Token.IsCancellationRequested, "toggle does not cancel explicit manual update");
            LlmService.Client.Pending.SetResult(new Core.Llm.LlmResult());
            Check(manual.Pump() && PortraitStore.Writes == 1, "in-flight manual update may still commit");

            Reset(); LlmService.Client.Defer = true;
            var normal = new Runner(PortraitService.EnsureSeededReady(Normal, v => ready = v)); normal.Pump();
            PersonaAutoAppendStore.Save(false);
            Check(!LlmService.Client.Token.IsCancellationRequested, "toggle does not cancel normal persona request");
            LlmService.Client.Pending.SetResult(new Core.Llm.LlmResult());
            Check(normal.Pump() && PortraitStore.Writes == 1, "normal persona result may still commit");

            Reset(); LlmService.Client.Defer = true;
            var first = new Runner(PortraitService.EnsureSeededReady(Custom, v => { })); first.Pump();
            var second = new Runner(PortraitService.EnsureSeededReady(Custom, v => ready = v));
            Check(!second.Pump(), "second automatic request waits on single-flight lease");
            PersonaAutoAppendStore.Save(false); PersonaAutoAppendStore.Save(true);
            Check(second.Pump() && first.Pump() && LlmService.Client.Calls == 1 && PortraitStore.Writes == 0,
                "off-on cannot revive request waiting on a prior lease");

            Reset(); LlmService.Client.Defer = true;
            var failed = new Runner(PortraitService.EnsureSeededReady(Custom, v => { })); failed.Pump();
            DurableSettingsStore.FailSave = true;
            Check(!PersonaAutoAppendStore.Save(false) && PersonaAutoAppendStore.Load()
                && !LlmService.Client.Token.IsCancellationRequested, "failed setting save leaves enabled state and work intact");
            LlmService.Client.Pending.SetResult(new Core.Llm.LlmResult()); failed.Pump();

            Reset(); LlmService.Client.Defer = true; ready = null;
            var changedWorld = new Runner(PortraitService.EnsureSeededReady(Custom, v => ready = v)); changedWorld.Pump();
            WorldLifecycle.Generation++; PersonaAutoAppendStore.Save(false);
            Check(changedWorld.Pump() && ready == null && PortraitStore.Writes == 0,
                "toggle cancellation never publishes fallback into a different world");
            Console.WriteLine("PASS " + Checks + " persona auto-append lifecycle checks");
        }
    }
}
'@
Add-Type -TypeDefinition ("using System.IO;`n" + $service + $store + $fixture) -WarningAction SilentlyContinue
[JianghuYouling.PersonaAutoAppendTests]::RunAll()
