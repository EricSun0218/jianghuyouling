// Minimal compile-time Unity/game/frontend surface for executing the production
// GroupChatOrchestrator persistence machine (load/save/archive/delete/clear) in the
// offline DevTest process. No game, Unity runtime or LLM endpoint is loaded; every
// stub is inert so only the real transcript/segment/journal/memory files change.
using System;
using System.Collections;
using System.Collections.Generic;

namespace UnityEngine
{
    public static class Debug
    {
        public static void Log(object message) { }
        public static void LogWarning(object message) { }
        public static void LogError(object message) { }
    }

    public static class Time
    {
        public static float unscaledTime => 0f;
    }

    public sealed class Coroutine { }

    public sealed class WaitUntil
    {
        public WaitUntil(Func<bool> predicate) { }
    }

    public sealed class WaitForSecondsRealtime
    {
        public WaitForSecondsRealtime(float time) { }
    }
}

// 默认没有游戏单例；日期边界测试可注入实例，结束时恢复。
public static class SingletonObject
{
    public static BasicGameData DataForTest;
    public static T getInstance<T>() where T : class => DataForTest as T;
}

public sealed class BasicGameData
{
    public int CurrDate;
}

namespace JianghuYouling
{
    public sealed class TaiwuDirectActionReceipt
    {
        public int TargetId;

        public List<int> TargetIds()
            => TargetId > 0 ? new List<int> { TargetId } : new List<int>();

        public TaiwuDirectActionReceipt ForTarget(int targetId)
            => TargetId == targetId ? this : null;
    }

    internal static class WorldLifecycle
    {
        public static int Generation = 1;
        public static bool HasWorldDate;
        public static uint WorldId => JianghuYoulingPaths.CurrentWorldId;
        public static bool IsSameWorld(int generation) => generation == Generation;
    }

    public static class JianghuYoulingPaths
    {
        // 测试逐用例指向独立临时目录,互不串扰。
        public static string Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "JHYL_GroupArcTest");
        public static uint CurrentWorldId = 0;
        public static string ChatLogs => System.IO.Path.Combine(Root, "ChatLogs");
        public static string Memories => System.IO.Path.Combine(Root, "Memories");
        public static string Events => System.IO.Path.Combine(Root, "Events");
        public static string Settings => System.IO.Path.Combine(Root, "Settings");
        public static string Intents => System.IO.Path.Combine(Root, "Intents");
        public static string Personas => System.IO.Path.Combine(Root, "Personas");
        public static string Novels => System.IO.Path.Combine(Root, "Novels");
    }

    public sealed class NpcSnapshot
    {
        public int NpcId, TaiwuId, CurrentDate;
        public string Name, Gender, TaiwuName, TaiwuGender, LocationText;
    }

    public sealed class CompanionMonthlyResult
    {
        public int NpcId;
        public string Name;
        public string LocationText;
        public string Summary;
        public string Detail;
        public readonly List<string> Outcomes = new List<string>();
    }

    public static class NpcSnapshotReader
    {
        public static IEnumerator Fetch(int npcId, Action<NpcSnapshot> done) { done?.Invoke(null); yield break; }
        public static IEnumerator FetchDisplayOnly(int npcId, Action<NpcSnapshot> done,
            Func<bool> stillCurrent = null) { done?.Invoke(null); yield break; }
        public static void FetchGroupMembers(int taiwuId, Action<List<int>> onResult) => onResult?.Invoke(new List<int>());
    }

    public sealed class TalkEntryHost : UnityEngine.MonoBehaviour
    {
        public static TalkEntryHost Instance { get; set; }
    }

    public sealed class ConfigHost : UnityEngine.MonoBehaviour
    {
        public static ConfigHost Instance => null;
    }

    public static class GroupRoundsStore
    {
        public const int Max = 3;
        public static int Rounds = 1;
        public static int Load() => Rounds;
    }

    public static class PlayerTalkMarkStore
    {
        public static void Mark(int taiwuId, int npcId, int date) { }
        public static bool ReplaceIdentity(int taiwuId, int oldId, int newId) => true;
    }

    public static class CommissionStore
    {
        public static bool ReplaceIdentity(int taiwuId, int oldId, int newId) => true;
    }

    public static class NativeInteractionRecordingStore
    {
        public static bool ReplaceIdentity(int oldId, int newId) => true;
        public static bool RemoveIdentity(int npcId) => true;
    }

    public static class ChatWindow
    {
        public static void NotifyConversationIdentityMigrated(int taiwuId, int oldId, int newId) { }
    }

    public static class PortraitService
    {
        private sealed class NoopLease : IDisposable { public void Dispose() { } }
        public static IDisposable BeginInteractiveBurst() => new NoopLease();
        public static IEnumerator Consolidate(NpcSnapshot snap) { yield break; }
        public static IEnumerator EnsureSeededReady(NpcSnapshot snap, Action<string> onReady)
        {
            onReady?.Invoke(string.Empty);
            yield break;
        }
        public static void Invalidate(int taiwuId, int npcId) { }
        public static void CancelInteractiveGeneration(NpcSnapshot snap) { }
    }

    public static class AssistantWidget
    {
        private sealed class NoopLease : IDisposable { public void Dispose() { } }
        public static IDisposable BeginInteractivePriority() => new NoopLease();
    }

    public static class MemoryRecaller
    {
        public sealed class RecallResult { }
    }

    public static class PortraitStore
    {
        public static string GetPortrait(NpcSnapshot snap) => null;
        public static bool Delete(int taiwuId, int npcId) => true;
        public static bool ReplaceIdentity(int taiwuId, int oldId, int newId) => true;
    }

    public static class AssistantOrchestrator
    {
        public static IEnumerable<string> RecentPlayerSpeechExamples(int taiwuId, int cap)
            => Array.Empty<string>();
    }

    public static class LlmService
    {
        public static JianghuYouling.Core.Llm.OpenAiCompatibleClient Client;
        public static JianghuYouling.Core.Llm.OpenAiCompatibleClient GetClient() => Client;
        public static JianghuYouling.Core.Llm.OpenAiCompatibleClient GetBackgroundClient() => Client;
    }

    public class TalkOrchestrator
    {
        public static readonly List<int> ClearedNpcIdsForTest = new List<int>();
        public static bool ClearConversation(int taiwuId, int npcId)
        { ClearedNpcIdsForTest.Add(npcId); return true; }
        public static bool MigrateConversationIdentity(int taiwuId, int oldId, int newId) => true;
        public static bool FinalizeConversationIdentityMigration(int taiwuId, int npcId) => true;
        public static Func<int, string> ReplyForTest;
        public static Func<int, IList<JianghuYouling.Core.Prompt.TalkTurn>, bool> ProjectForTest;
        public sealed class GroupContext
        {
            public string SelfName;
            public List<string> Others;
            public string RelationshipContext;
            public Func<long> RelationshipRevision;
            public Action MarkRelationshipsDirty;
            public long SeenRelationshipRevision;
            public string IdentityContext;
            public Func<NpcSnapshot, string> RefreshIdentityContext;
            public int MemberId;
            public int DispatchOrder;
            public JianghuYouling.Core.Tools.GroupActionCoordinator ActionCoordinator;
            public HashSet<int> ParticipantIds;
            public string GroupId;
            public string ExchangeId;
            public string AttemptId;
            public List<string> CompanionNames;
            public List<string> OrdinaryChannelNames;
            public bool MustReply;
            public string CurrentTurnInput;
            public Func<int, int, string> ReserveModelRequest;
            public Func<bool> DispatchStillValid;
            public Func<int, List<string>, List<string>, List<string>, bool> PrepareCommit;
            public string MemoryRecallTopic;
            public Func<int, MemoryRecaller.RecallResult> GetCachedMemoryRecall;
            public Action<int, MemoryRecaller.RecallResult> StoreCachedMemoryRecall;
        }

        public GroupContext GroupCtx;
        public TaiwuDirectActionReceipt DirectTaiwuAction;
        public Action<string, string, Action> GrantSink;
        public Action<string> SysSink;
        public IReadOnlyList<string> LastTurnMemoryIds => null;
        public IReadOnlyList<string> LastTurnActions => null;
        public IReadOnlyList<string> LastTurnToolResults => null;
        public string LastNpcLocationText => null;
        public string LastContactMode => null;
        public static string TaiwuVoice = "";
        public static IEnumerable<string> RecentPlayerSpeechExamples(int taiwuId, int cap)
            => Array.Empty<string>();

        public IEnumerator ProcessTurn(int npcId, string playerInput, Action<string> onReply, Action<string> onError,
            Action<string> onDelta = null, Action<string> onProgress = null, Action<int, int> onTokens = null,
            Action<string> onThinking = null, Action onResetReply = null, string retryAvoid = null,
            Action<bool> onContactMode = null)
        {
            onContactMode?.Invoke(false);
            if (ReplyForTest != null)
            {
                onReply?.Invoke(ReplyForTest(npcId));
                yield break;
            }
            onError?.Invoke("离线测试桩不产生对话");
            yield break;
        }

        public void Cancel() { }
        internal void CaptureCurrentTurnState(out List<string> memoryIds, out List<string> actions,
            out List<string> toolResults)
        {
            memoryIds = LastTurnMemoryIds == null ? null : new List<string>(LastTurnMemoryIds);
            actions = LastTurnActions == null ? null : new List<string>(LastTurnActions);
            toolResults = LastTurnToolResults == null ? null : new List<string>(LastTurnToolResults);
        }
        public void CommitRecallStatistics(int npcId) { }
        public void ClearDeferredNotices() { }
        public void FlushDeferredNoticesNow() { }
        public static string CleanSuggestion(string s) => s;
        public static string CleanSuggestion(string s, int maxChars) => s;

        internal static string GroupChatProjectionTurnId(int taiwuId, int npcId,
            string groupId, string exchangeId)
            => "grp-" + JianghuYouling.Core.Tools.OperationId.FromStableKey(
                "group-chat-history|" + JianghuYoulingPaths.CurrentWorldId + "|" + taiwuId
                + "|" + npcId + "|" + (groupId ?? "") + "|" + (exchangeId ?? ""));

        internal static bool SynchronizeGroupChatTranscripts(int taiwuId, int npcId,
            string npcName, string groupId, IEnumerable<string> affectedExchangeIds,
            IList<JianghuYouling.Core.Prompt.TalkTurn> desired) => ProjectForTest?.Invoke(npcId, desired) ?? true;

        public static bool FinalizeRecoveredGroupCommit(int taiwuId, int npcId, string groupId,
            string exchangeId, string attemptId, string playerInput, int worldDate) => true;

        public static string RecoveredGroupAttemptState(int taiwuId, int npcId, string groupId,
            string exchangeId, string attemptId, string playerInput, int worldDate,
            List<string> operationIds) => "not_dispatched";

        public static void BeginRecoveredGroupAttemptReconciliation(int taiwuId, int npcId, string groupId,
            string exchangeId, string attemptId, string playerInput, int worldDate,
            List<string> operationIds, Action onResolved) { }
    }
}

namespace JianghuYouling.Effects
{
    public static class EffectHandler
    {
        public static void EnsureCharacterProxy(int npcId,
            Action<bool, int, int, bool, bool, int, short, string> done)
            => throw new InvalidOperationException("Reload tests must never create game characters.");
        public static void QuerySameBlockChars(int taiwuId, Action<List<int>> onResult, bool includeHostile = true)
            => onResult?.Invoke(new List<int>());

        public static void QueryRosterRelations(IList<int> charIds, bool includeEmpty, Action<string> onResult)
            => onResult?.Invoke(string.Empty);

        public static void QueryRosterRelations(IList<int> charIds, bool includeEmpty, Action<bool, string> onResult)
            => onResult?.Invoke(true, string.Empty);
    }
}
