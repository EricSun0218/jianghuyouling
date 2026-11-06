param(
    [string]$SourcePath = (Join-Path $PSScriptRoot '../src/JianghuYouling.Frontend/Game/CompanionMonthlyActions.cs'),
    [switch]$AgainstHead
)

$ErrorActionPreference = 'Stop'
$source = if ($AgainstHead) {
    $baseline = git -C (Join-Path $PSScriptRoot '..') show HEAD:src/JianghuYouling.Frontend/Game/CompanionMonthlyActions.cs
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read HEAD for regression reproduction' }
    $baseline -join "`n"
} else { Get-Content -LiteralPath $SourcePath -Raw }
function Get-SourceSlice([string]$Start, [string]$End) {
    $from = $source.IndexOf($Start, [StringComparison]::Ordinal)
    $to = $source.IndexOf($End, $from + $Start.Length, [StringComparison]::Ordinal)
    if ($from -lt 0 -or $to -le $from) { throw "Missing production source boundary: $Start" }
    $source.Substring($from, $to - $from)
}

# Execute the production scene builder, validity checks and RunOne pre-model coroutine.
# Only engine/RPC/lifecycle surfaces are stubbed; no game process, model or save is used.
$scene = (Get-SourceSlice 'private sealed class CompanionSceneContext' 'internal string Render(int actorId)') + '}'
$builder = Get-SourceSlice 'private static IEnumerator BuildCompanionSceneContext(' 'private static IEnumerator PopulateRemoteContacts('
$prepare = Get-SourceSlice 'private static IEnumerator PrepareActorLocalScene(' 'private static string BuildCandidateDiag('
$run = (Get-SourceSlice 'private static IEnumerator RunOne(' 'result.Name = string.IsNullOrWhiteSpace(snap.Name)') + @'
            result.Ready = true;
            result.ActorScene = scene.ActorScene;
            result.Area = scene.AreaId;
            result.Block = scene.BlockId;
            result.Present = scene.PresentIds;
            onDone(result);
        }
'@
$fixture = @'
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
namespace GameData.Domains.Character { public enum AgeGroup { Baby, Adult } }
public static class CompanionSceneLifecycleTests
{
    static Action DuringFetch, DuringActorQuery, DuringSharedQuery;
    static Action PendingRead;
    static bool BatchActive, SharedQueryTimeout, ActorQueryOk;
    static int ClearEpoch, ActorQueries, Checks;
    static class Time { internal static float unscaledTime; }
    static class Debug { internal static void Log(string s) {} internal static void LogWarning(string s) {} }
    static class WorldLifecycle {
        internal static int Generation;
        internal static uint WorldId;
        internal static bool IsSameWorld(int generation) => Generation == generation;
    }
    static class MonthlySettlement {
        internal static bool Advancing;
        internal static bool IsAdvancingMonthForMonthlyWork() => Advancing;
    }
    static class TalkOrchestrator {
        internal static long CaptureConversationClearEpoch(int taiwu, int npc) => ClearEpoch;
    }
    sealed class NpcSnapshot {
        internal int NpcId, TaiwuId;
        internal bool IsDead = false;
        internal int PhysiologicalAge = 30, ActualAge = 30;
        internal GameData.Domains.Character.AgeGroup GameAgeGroup = GameData.Domains.Character.AgeGroup.Adult;
    }
    static class NpcSnapshotReader {
        internal static bool Available;
        internal static short Area, Block;
        internal static bool TryGetCurrentWorldArea(out short area, out short block) {
            area = Area; block = Block; return Available;
        }
        internal static bool TryGetCurrentWorldArea(out short area, out short block, out short template, out string name) {
            template = 0; name = "Taiwu scene"; return TryGetCurrentWorldArea(out area, out block);
        }
        internal static IEnumerator Fetch(int id, Action<NpcSnapshot> done, bool unused, Func<bool> current) {
            DuringFetch?.Invoke();
            yield return null;
            if (current()) done(new NpcSnapshot { NpcId = id, TaiwuId = 1 });
        }
    }
    static class EffectHandler {
        internal const float ReadOnlyQueryWaitSeconds = 2;
        internal static void QuerySameBlockChars(int taiwu, Action<List<int>> done, bool unused) {
            DuringSharedQuery?.Invoke();
            if (!SharedQueryTimeout) PendingRead = () => done(new List<int> { 2, 10 });
        }
        internal static void QueryActorBlockChars(int actor, Action<bool, short, short, List<int>> done) {
            ActorQueries++;
            DuringActorQuery?.Invoke();
            PendingRead = () => done(ActorQueryOk, 8, 9, new List<int> { actor, 20 });
        }
        internal static void QueryCharNamesAndGenders(List<int> ids, Action<List<string>, List<string>> done) {
            PendingRead = () => done(ids.ConvertAll(id => "#" + id), ids.ConvertAll(id => "unknown"));
        }
    }
    sealed class SecretRef {}
    sealed class CompanionExecutionArbiter {}
    sealed class CompanionActionNoveltySnapshot {}
    sealed class CompanionMonthlyStopState { internal string Code = null; }
    sealed class CompanionMonthlyResult {
        internal bool Ready, ActorScene;
        internal short Area, Block;
        internal HashSet<int> Present;
        internal string StopCode;
    }
    static class MonthlyInterruption {
        internal static string Describe(string code) => code;
        internal static CompanionMonthlyResult Stop(CompanionMonthlyResult result, string code) { result.StopCode = code; return result; }
    }
    static class OperationId { internal static string FromStableKey(string key) => key; }
    sealed class LlmTraceContext { internal LlmTraceContext(string a, string b, string c) {} }
    static bool PredicateIsCurrent(Func<bool> current) => current == null || current();
    static bool BatchIsCurrent(int epoch, int generation) => BatchActive && WorldLifecycle.IsSameWorld(generation) && !MonthlySettlement.Advancing;
    static bool RunIsCurrent(int epoch, int generation, int taiwu, int npc, long clearEpoch)
        => BatchIsCurrent(epoch, generation) && clearEpoch == ClearEpoch;

    static void Drive(IEnumerator root) {
        var stack = new Stack<IEnumerator>(); stack.Push(root);
        for (int steps = 0; stack.Count > 0; steps++) {
            if (steps > 100) throw new Exception("Coroutine did not settle");
            var top = stack.Peek();
            if (!top.MoveNext()) { stack.Pop(); continue; }
            if (top.Current is IEnumerator nested) { stack.Push(nested); continue; }
            Time.unscaledTime += 1;
            var read = PendingRead; PendingRead = null; read?.Invoke();
        }
    }
    static void Reset() {
        WorldLifecycle.Generation = 1; WorldLifecycle.WorldId = 7;
        NpcSnapshotReader.Available = true; NpcSnapshotReader.Area = 3; NpcSnapshotReader.Block = 4;
        BatchActive = ActorQueryOk = true; SharedQueryTimeout = MonthlySettlement.Advancing = false;
        ClearEpoch = ActorQueries = 0; Time.unscaledTime = 0;
        DuringFetch = DuringActorQuery = DuringSharedQuery = PendingRead = null;
    }
    static CompanionSceneContext Build() {
        CompanionSceneContext result = null;
        Drive(BuildCompanionSceneContext(1, 1, new List<int> { 2 }, new List<int> { 2, 3 },
            value => result = value, () => BatchIsCurrent(1, 1)));
        return result;
    }
    static CompanionMonthlyResult Run(CompanionSceneContext scene, int id, CancellationToken token = default(CancellationToken)) {
        CompanionMonthlyResult result = null;
        Drive(RunOne(id, 1, 100, 1, 1, token, scene, new CompanionExecutionArbiter(), null,
            new CompanionActionNoveltySnapshot(), new CompanionMonthlyResult(), new CompanionMonthlyStopState(),
            value => result = value, null));
        return result;
    }
    static void Check(bool condition, string label) {
        if (!condition) throw new Exception("FAIL: " + label);
        Checks++; Console.WriteLine("PASS " + label);
    }
    static void CheckManual(CompanionSceneContext scene, string label) {
        var result = Run(scene, 3);
        Check(result != null && result.Ready && result.ActorScene && result.StopCode == null
            && result.Area == 8 && result.Block == 9 && result.Present.Contains(20)
            && !result.Present.Contains(1) && !result.Present.Contains(2) && !result.Present.Contains(10), label);
        Check(!scene.ActorScene && scene.PresentIds.Contains(2), "shared companion context stays isolated: " + label);
    }
    public static void RunAll() {
        Reset(); var scene = Build(); NpcSnapshotReader.Block++;
        CheckManual(scene, "queued non-companion starts after Taiwu moves");
        var companion = Run(scene, 2);
        Check(companion != null && !companion.Ready && companion.StopCode == "location_changed", "queued companion still stops on Taiwu movement");
        Reset(); scene = Build(); DuringFetch = () => NpcSnapshotReader.Block++;
        CheckManual(scene, "non-companion snapshot preload survives Taiwu movement");
        Reset(); scene = Build(); DuringActorQuery = () => NpcSnapshotReader.Available = false;
        CheckManual(scene, "actor scene RPC survives unavailable Taiwu location");
        Reset(); DuringSharedQuery = () => NpcSnapshotReader.Block++; scene = Build();
        CheckManual(scene, "movement during shared scene construction does not discard manual candidates");
        Reset(); NpcSnapshotReader.Available = false; scene = Build();
        CheckManual(scene, "missing Taiwu scene does not discard manual candidates");
        companion = Run(scene, 2);
        Check(companion != null && !companion.Ready && companion.StopCode == "location_unavailable", "companion needs an available shared scene");
        Reset(); SharedQueryTimeout = true; scene = Build();
        CheckManual(scene, "shared scene RPC timeout does not discard manual candidates");
        companion = Run(scene, 2);
        Check(companion != null && !companion.Ready && companion.StopCode == "location_unavailable", "incomplete shared scene cannot authorize companion actions");
        Reset(); scene = Build(); companion = Run(scene, 2);
        Check(companion != null && companion.Ready && !companion.ActorScene && companion.Area == 3
            && companion.Block == 4 && ActorQueries == 0, "unchanged companion uses the original shared scene");
        Reset(); scene = Build(); DuringFetch = () => NpcSnapshotReader.Block++;
        companion = Run(scene, 2);
        Check(companion != null && !companion.Ready && companion.StopCode == "location_changed", "companion preload still stops on movement");
        Reset(); scene = Build(); ActorQueryOk = false;
        Check(Run(scene, 3) == null && ActorQueries == 1, "failed actor scene read never reaches planning");
        Reset(); scene = Build(); DuringFetch = () => WorldLifecycle.Generation++;
        Check(Run(scene, 3) == null && ActorQueries == 0, "save generation change still cancels non-companion preload");
        Reset(); scene = Build(); DuringFetch = () => WorldLifecycle.WorldId++;
        Check(Run(scene, 3) == null && ActorQueries == 0, "world identity change still cancels non-companion preload");
        Reset(); scene = Build(); DuringFetch = () => ClearEpoch++;
        var cleared = Run(scene, 3);
        Check(cleared != null && !cleared.Ready && cleared.StopCode == "conversation_cleared", "clearing conversation still cancels preload");
        Reset(); scene = Build(); DuringFetch = () => MonthlySettlement.Advancing = true;
        var advanced = Run(scene, 3);
        Check(advanced != null && !advanced.Ready && advanced.StopCode == "native_month_advance", "next month still cancels preload");
        Reset(); scene = Build(); DuringActorQuery = () => WorldLifecycle.Generation++;
        Check(Run(scene, 3) == null, "save switch during actor scene query cannot finish preload");
        Reset(); scene = Build(); using (var cancellation = new CancellationTokenSource()) {
            DuringFetch = cancellation.Cancel;
            var canceled = Run(scene, 3, cancellation.Token);
            Check(canceled != null && !canceled.Ready && canceled.StopCode == "batch_invalidated", "batch cancellation still stops manual candidates");
        }
        Console.WriteLine("PASS " + Checks + " companion scene lifecycle checks");
    }
'@
Add-Type -TypeDefinition ($fixture + $scene + $builder + $prepare + $run + '}') -WarningAction SilentlyContinue
[CompanionSceneLifecycleTests]::RunAll()

$batch = Get-SourceSlice 'CompanionSceneContext scene = null;' 'CompanionActionNoveltySnapshot noveltySnapshot ='
if ($batch.Contains('scene.IsCurrent()')) { throw 'Shared Taiwu location still rejects the entire candidate batch' }
Write-Output 'PASS shared batch has no Taiwu-location veto'
