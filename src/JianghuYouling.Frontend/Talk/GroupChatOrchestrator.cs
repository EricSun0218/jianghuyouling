using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Prompt;
using JianghuYouling.Core.Text;
using JianghuYouling.Core.Memory;
using JianghuYouling.Core.Persistence;
using JianghuYouling.Core.Tools;
using JianghuYouling.Effects;

namespace JianghuYouling
{
    /// <summary>
    /// Shared emergency fuse for one player-triggered group turn, including all automatic
    /// NPC-to-NPC rounds.  Normal three-round eight-member conversations fit below both
    /// ceilings; the circuit exists only to stop an abnormal provider/tool retry cascade.
    /// </summary>
    internal sealed class GroupTurnRequestBudget
    {
        internal const int MaxLogicalRequestsPerTurn = 128;
        // Emergency-only ceiling: even 96 theoretical full-context requests from an eight-member,
        // three-round group fit below it after retry reservation. It must never become a normal
        // reply-length or tool-depth limit.
        internal const long MaxEstimatedBilledTokensPerTurn = 300000000L;
        internal const int RetryReservationMultiplier = 2;

        private readonly object _gate = new object();
        private int _requests;
        private long _estimatedBilledTokens;
        private bool _blocked;

        internal string Reserve(int estimatedInputTokens, int estimatedOutputTokens)
        {
            lock (_gate)
            {
                if (_blocked) return Diagnostic(0, estimatedInputTokens, estimatedOutputTokens);
                long input = Math.Max(0, estimatedInputTokens);
                long output = Math.Max(0, estimatedOutputTokens);
                long estimate = checked((input + output) * RetryReservationMultiplier);
                if (_requests >= MaxLogicalRequestsPerTurn
                    || _estimatedBilledTokens + estimate > MaxEstimatedBilledTokensPerTurn)
                {
                    _blocked = true;
                    return Diagnostic(estimate, estimatedInputTokens, estimatedOutputTokens);
                }

                _requests++;
                _estimatedBilledTokens += estimate;
                return null;
            }
        }

        private string Diagnostic(long next, int input, int output)
            => "requests=" + _requests + "/" + MaxLogicalRequestsPerTurn
                + " estimated_billed=" + _estimatedBilledTokens + "/"
                + MaxEstimatedBilledTokensPerTurn + " next=" + next
                + " input=" + Math.Max(0, input) + " output=" + Math.Max(0, output);
    }

    /// <summary>
    /// 多人群聊编排:太吾说一句后由群内全体成员各自接话；成员保留完整人格、记忆与工具能力，
    /// 真副作用按导演顺序串行提交，并由父群事务、个人动作日志与后端 operation ledger 三层对账。
    /// 共享对话记录按"这支小队"持久化(与单聊同理),重开群聊即载入此前对话。各成员仍是完整人格(画像/人设/记忆)。
    /// </summary>
    public sealed class GroupChatOrchestrator
    {
        public sealed class Member
        {
            public int Id; public string Name;
            public NpcSnapshot Snap;
        }
        /// <summary>一条群聊记录(可序列化持久化)。</summary>
        public sealed class Line
        {
            public string Id;
            public string ExchangeId;
            // Optional for v1-v4 compatibility.  New member lines carry the exact
            // per-round commit attempt that produced their memory/action receipt.
            public string CommitAttemptId;
            public int SpeakerId;
            public string Speaker;
            public bool IsTaiwu;
            public string Text;
            public int Date;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string LocationText;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string ContactMode;
            public DateTime CreatedUtc;
            public List<string> MemoryIds;
            public List<string> Actions;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public List<string> ToolResults;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string ImageFileName;
        }
        private sealed class MemberRef { public int Id; public string Name; }
        private sealed class LinkedMemoryRemoval { public int NpcId; public List<string> MemoryIds = new List<string>(); }
        private sealed class GroupDocument
        {
            public int Version = CurrentGroupDocumentVersion;
            public uint WorldId;
            public int TaiwuId;
            public string GroupId;
            public long Revision;
            public string IntegritySha256;
            public List<MemberRef> Members = new List<MemberRef>();
            public List<Line> Lines = new List<Line>();
            // v6-v7 legacy barrier. v8 keeps this mirror for diagnostics/integrity only;
            // PendingContextProjectionExchangeIds is the authoritative per-exchange queue.
            public bool MemoryProjectionPending;
            public List<string> PendingContextProjectionExchangeIds = new List<string>();
            // 删除/清空已提交聊天文件、但对应来源记忆尚未确认刷新时的持久化事务日志。
            // 重启后会按这些 exchange 幂等重放，避免孤儿群聊记忆永久残留。
            public List<string> PendingMemoryRefreshExchangeIds = new List<string>();
            public List<LinkedMemoryRemoval> PendingLinkedMemoryRemovals = new List<LinkedMemoryRemoval>();
            // v7 归档分段:活跃文档声明已提交的不可变归档段数量(读者只认 1..N,更高
            // index 的段文件是未提交暂存);段文档记录自身段号,活跃文档恒为 0。
            public int ArchivedSegmentCount;
            public int ArchiveSegmentIndex;
        }

        private sealed class RecoveredDocument
        {
            public string Path;
            public int Rank;
            public GroupDocument Document;
        }

        private readonly List<Member> _members = new List<Member>();
        private readonly HashSet<int> _authoritativeCompanionIds = new HashSet<int>();
        private List<Line> _transcript = new List<Line>();
        // 已封存段的只读缓存(旧→新)。按活跃文档声明的段数惰性载入;归档/删轮/清空后失效重读。
        private List<Line> _archivedLines = new List<Line>();
        private bool _archivedLoaded;
        private int _archivedSegmentCount;
        private int _taiwuId;
        private string _key;
        private string _currentExchangeId;
        private readonly HashSet<string> _contextProjectionInFlight =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _pendingContextProjectionExchangeIds =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _deletedLineIds = new HashSet<string>();
        private readonly HashSet<string> _pendingMemoryRemovalExchangeIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<int, HashSet<string>> _pendingLinkedMemoryRemovals = new Dictionary<int, HashSet<string>>();
        private static readonly ConcurrentDictionary<string, object> FileLocks = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, long> FileEpochs = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, HashSet<string>> DeletedLineIdsByPath = new ConcurrentDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, HashSet<string>> CompletedMemoryRefreshIdsByPath = new ConcurrentDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, HashSet<string>> CompletedLinkedMemoryRemovalIdsByPath = new ConcurrentDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        private static readonly object ActiveAttemptsGate = new object();
        private static readonly Dictionary<string, Dictionary<string, string>> ActiveAttemptOwnersByPath =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, long> GroupEpochsByPath =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private long _fileEpoch;
        private long _documentRevision;
        private bool _memoryProjectionPending;
        // False means at least one transcript candidate existed but none could be
        // validated. Never turn that state into an empty chat: doing so would lose
        // parent transaction evidence and could authorize duplicate mutations.
        private bool _transcriptReliable;
        private System.Threading.CancellationTokenSource _cts;
        private int _worldGeneration;
        private uint _journalWorldId;
        private string _journalPath;
        private readonly string _ownerId = Guid.NewGuid().ToString("N");
        private readonly HashSet<string> _ownedAttemptIds = new HashSet<string>(StringComparer.Ordinal);
        private long _groupEpoch;
        private GroupExchangeJournal _exchangeJournal;
        private readonly List<Coroutine> _setupCoroutines = new List<Coroutine>();
        private readonly List<NpcSnapshot> _setupPortraits = new List<NpcSnapshot>();
        private IDisposable _setupPortraitPriority;
        private IDisposable _setupAssistantPriority;
        private IDisposable _turnPortraitPriority;
        private IDisposable _turnAssistantPriority;
        private readonly List<TalkOrchestrator> _activeRuns = new List<TalkOrchestrator>();   // 本轮【并发】在跑的各成员编排器(供中断:全部 Cancel)
        // Cancel 只能请求子编排器停止，不能证明它的 finally 已经收口。保留 worker 状态直到
        // finally 真正完成；被父协程安全断开的 worker 会在自己的 finally 中补写回执并恢复事务。
        private readonly List<MemberRun> _activeMemberRuns = new List<MemberRun>();
        private string _lastTaiwuLocationText;
        private static readonly System.Random _rng = new System.Random();   // 乱序邀人 / 兜底随机(主线程协程内用)
        private static int _scheduledWorldRecoveryGeneration = -1;
        private static uint _scheduledWorldRecoveryWorldId;

        private const int RenderLines = 18;     // 喂给成员的最近对话行数
        private const int MaxGroupMembers = 8;
        // This is a deadlock watchdog, not a target latency. Eight members share the client's
        // three request slots and a member may legitimately perform several tool rounds, so a
        // short wall-clock timeout would cancel healthy requests that are merely waiting in line.
        private const float MemberGenerationIdleTimeoutSeconds = 300f;
        // Unlike the renewable idle watchdog, this batch deadline cannot be extended by a
        // provider that emits one tiny SSE heartbeat forever.  It is deliberately generous:
        // healthy queued members and multi-step tools keep the full ten minutes.
        private const float GroupBatchHardTimeoutSeconds = 600f;
        private const float MemberCancellationDrainSeconds = 10f;
        // 单文件损坏防护上限。活跃文档由归档分段保证远低于此值;段体积由构造保证约等于
        // 归档阈值+一轮,群聊记录整体可无限增长,不存在"写满后永久保存失败"的死路。
        private const int MaxTranscriptDocumentBytes = 16 * 1024 * 1024;
        private const int CurrentGroupDocumentVersion = 8;
        // 活跃文档序列化字节超过该阈值即把最老的完整轮次封存进归档段;离线测试可注入小阈值。
        internal static int ArchiveThresholdBytes = 2 * 1024 * 1024;
        // 归档后活跃文档必须保留的可见行数,须大于喂给成员的 RenderLines 窗口,归档不改变提示语义。
        internal static int MinActiveVisibleLines = 40;
        // 一次调用最多封存的段数:迁移期超大文档分多次屏障提交摊还,避免单帧同步冻结;
        // 离线测试可注入更大值一次归档到位。
        internal static int ArchiveMaxPassesPerCall = 2;
        // 每条路径只警告一次"活跃文档超限仍未归档",避免刷屏。
        private static readonly HashSet<string> OversizeWarnedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        // 群聊各处一律不设 token 上限(传 0=用接口默认、不artificial截断):导演/成员回话都让推理模型先吐够思考再出正文,
        // 免 256 这种小额度被思考占满、挑人名字吐不出 → 群聊"只有一个人回"的根因。

        public IReadOnlyList<Member> Members => _members;

        /// <summary>
        /// 把既有群聊中的固定模板原 id 改成永久副本 id。只改人物身份字段，发言原文、姓名、
        /// 工具结果与时间地点均保持原样；每个文档仍走原有三副本提交和完整性校验。
        /// </summary>
        internal static bool MigrateMemberIdentity(int taiwuId, int oldNpcId, int newNpcId)
        {
            if (taiwuId <= 0 || oldNpcId < 0 || newNpcId < 0) return false;
            if (oldNpcId == newNpcId) return true;
            uint worldId = JianghuYoulingPaths.CurrentWorldId;
            if (worldId == 0) return false;
            try
            {
                string directory = JianghuYoulingPaths.ChatLogs;
                if (!Directory.Exists(directory)) return true;
                foreach (string candidate in Directory.GetFiles(directory, "Group_*.json"))
                {
                    string fileName = Path.GetFileName(candidate);
                    if (string.IsNullOrWhiteSpace(fileName)
                        || fileName.IndexOf(".archive.", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;
                    bool reliable;
                    GroupDocument document = ReadDocumentRecoverable(candidate, true,
                        worldId, taiwuId, null, null, out reliable);
                    if (!reliable) return false;
                    if (document == null || document.Members == null) continue;
                    bool containsOld = false;
                    foreach (MemberRef member in document.Members)
                        if (member != null && member.Id == oldNpcId) { containsOld = true; break; }
                    if (!containsOld) continue;

                    string journalPath = Path.Combine(directory,
                        "GroupTxn_" + document.GroupId + ".json");
                    var journal = new GroupExchangeJournal(journalPath, worldId,
                        taiwuId, document.GroupId);
                    if (!journal.ReplaceNpcIdentity(oldNpcId, newNpcId)) return false;

                    bool alreadyHasNew = false;
                    foreach (MemberRef member in document.Members)
                        if (member != null && member.Id == newNpcId) { alreadyHasNew = true; break; }
                    if (alreadyHasNew)
                        document.Members.RemoveAll(m => m != null && m.Id == oldNpcId);
                    else
                        foreach (MemberRef member in document.Members)
                            if (member != null && member.Id == oldNpcId) member.Id = newNpcId;

                    foreach (Line line in document.Lines ?? new List<Line>())
                        if (line != null && !line.IsTaiwu && line.SpeakerId == oldNpcId)
                            line.SpeakerId = newNpcId;

                    for (int archiveIndex = 1;
                        archiveIndex <= document.ArchivedSegmentCount; archiveIndex++)
                    {
                        string archivePath = ArchiveSegmentPathFor(directory,
                            document.GroupId, archiveIndex);
                        GroupDocument segment = ReadArchiveSegmentAt(archivePath, archiveIndex,
                            worldId, taiwuId, document.GroupId, null);
                        if (segment == null) return false;
                        bool segmentHasNew = segment.Members != null
                            && segment.Members.Exists(m => m != null && m.Id == newNpcId);
                        if (segment.Members != null)
                        {
                            if (segmentHasNew)
                                segment.Members.RemoveAll(m => m != null && m.Id == oldNpcId);
                            else
                                foreach (MemberRef member in segment.Members)
                                    if (member != null && member.Id == oldNpcId) member.Id = newNpcId;
                        }
                        foreach (Line line in segment.Lines ?? new List<Line>())
                            if (line != null && !line.IsTaiwu && line.SpeakerId == oldNpcId)
                                line.SpeakerId = newNpcId;
                        segment.Revision = Math.Max(0, segment.Revision) + 1;
                        if (!WriteArchiveSegment(archivePath, segment)) return false;
                    }

                    var linked = new Dictionary<int, HashSet<string>>();
                    foreach (LinkedMemoryRemoval removal in document.PendingLinkedMemoryRemovals
                        ?? new List<LinkedMemoryRemoval>())
                    {
                        if (removal == null) continue;
                        int id = removal.NpcId == oldNpcId ? newNpcId : removal.NpcId;
                        if (!linked.TryGetValue(id, out HashSet<string> memoryIds))
                            linked[id] = memoryIds = new HashSet<string>(StringComparer.Ordinal);
                        foreach (string memoryId in removal.MemoryIds ?? new List<string>())
                            if (!string.IsNullOrWhiteSpace(memoryId)) memoryIds.Add(memoryId);
                    }
                    document.PendingLinkedMemoryRemovals = new List<LinkedMemoryRemoval>();
                    foreach (KeyValuePair<int, HashSet<string>> pair in linked)
                        if (pair.Key > 0 && pair.Value.Count > 0)
                            document.PendingLinkedMemoryRemovals.Add(new LinkedMemoryRemoval
                            { NpcId = pair.Key, MemoryIds = new List<string>(pair.Value) });

                    document.Revision = Math.Max(0, document.Revision) + 1;
                    WriteDocumentAtomic(candidate, document,
                        ".identity-" + Guid.NewGuid().ToString("N") + ".tmp");
                }
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[江湖有灵] 人物副本群聊身份迁移失败 " + oldNpcId + "→"
                    + newNpcId + ":" + e.GetType().Name);
                return false;
            }
        }
        /// <summary>
        /// Stable persisted conversation identity. New groups receive an independent identity,
        /// which remains unchanged when the current group adds members.
        /// </summary>
        public string GroupId => _key;

        /// <summary>
        /// Creates a durable identity for one newly-created group instance. Membership is not an
        /// identity: two groups may contain the same people while keeping separate transcripts.
        /// </summary>
        internal static string CreateNewGroupId(int taiwuId)
        {
            if (taiwuId <= 0) throw new ArgumentOutOfRangeException(nameof(taiwuId));
            return taiwuId + "_g_" + Guid.NewGuid().ToString("N");
        }
        // UI 渲染/导出看到的是完整历史:已封存段(旧→新)+活跃文档的可见行拼接。
        // 提示窗、路由、记忆投影仍只消费活跃 _transcript。
        public IReadOnlyList<Line> Transcript
        {
            get
            {
                EnsureArchiveLoaded();
                var lines = VisibleLines(_archivedLines);
                lines.AddRange(VisibleLines(_transcript));
                return lines;
            }
        }

        internal static List<string> RecentPlayerSpeechExamples(int taiwuId, int maxExamples)
        {
            var result = new List<string>();
            if (taiwuId <= 0 || maxExamples <= 0) return result;
            try
            {
                List<GroupSession> sessions = new GroupChatOrchestrator().LoadRecentSessions(
                    taiwuId, 0, int.MaxValue, 64, null, true);
                foreach (GroupSession session in sessions)
                {
                    if (session?.Lines == null) continue;
                    for (int i = session.Lines.Count - 1; i >= 0 && result.Count < maxExamples; i--)
                    {
                        Line line = session.Lines[i];
                        if (line == null || !line.IsTaiwu || string.IsNullOrWhiteSpace(line.Text)) continue;
                        result.Add(line.Text);
                    }
                    if (result.Count >= maxExamples) break;
                }
            }
            catch { }
            return result;
        }

        /// <summary>Drop world-scoped tombstones/leases after the lifecycle generation is fenced.</summary>
        public static void ResetForWorldExit()
        {
            FileEpochs.Clear();
            DeletedLineIdsByPath.Clear();
            CompletedMemoryRefreshIdsByPath.Clear();
            CompletedLinkedMemoryRemovalIdsByPath.Clear();
            lock (ActiveAttemptsGate)
            {
                ActiveAttemptOwnersByPath.Clear();
                GroupEpochsByPath.Clear();
                _scheduledWorldRecoveryGeneration = -1;
                _scheduledWorldRecoveryWorldId = 0;
            }
        }

        public void Cancel()
        {
            CancelSetupCoroutines();
            ReleaseTurnPriority();
            string exchange = _currentExchangeId;
            CloseInterjectionWindow(exchange, true);
            if (_currentExchangeId == exchange) _currentExchangeId = null;
            try { _cts?.Cancel(); } catch { }
            // 请求中断不等于 worker 已经退出。旧实现会在这里立即 RecoverOwnedAttempts，
            // 与仍可能提交后端动作的子协程竞速，造成“游戏状态已变、群记录却没有绿字”。
            // 把未结束 worker 标为安全断开；它们只会在 finally 捕获最终回执后自行恢复。
            foreach (MemberRun run in new List<MemberRun>(_activeMemberRuns))
            {
                if (run == null || run.Done) continue;
                run.ParentDetached = true;
                run.Error = string.IsNullOrWhiteSpace(run.Error) ? "已中断" : run.Error;
                try { run.Orch?.Cancel(); } catch { }
            }
            foreach (var o in new List<TalkOrchestrator>(_activeRuns)) { try { o?.Cancel(); } catch { } }
        }

        private void CancelSetupCoroutines()
        {
            foreach (NpcSnapshot snapshot in new List<NpcSnapshot>(_setupPortraits))
                try { PortraitService.CancelInteractiveGeneration(snapshot); } catch { }
            _setupPortraits.Clear();
            var host = TalkEntryHost.Instance;
            if (host != null)
                foreach (Coroutine coroutine in new List<Coroutine>(_setupCoroutines))
                    try { if (coroutine != null) host.StopCoroutine(coroutine); } catch { }
            _setupCoroutines.Clear();
            ReleaseSetupPriority();
        }

        private void ReleaseSetupPriority()
        {
            IDisposable portrait = _setupPortraitPriority;
            IDisposable assistant = _setupAssistantPriority;
            _setupPortraitPriority = null;
            _setupAssistantPriority = null;
            try { portrait?.Dispose(); } catch { }
            try { assistant?.Dispose(); } catch { }
        }

        private void ReleaseTurnPriority()
        {
            IDisposable portrait = _turnPortraitPriority;
            IDisposable assistant = _turnAssistantPriority;
            _turnPortraitPriority = null;
            _turnAssistantPriority = null;
            try { portrait?.Dispose(); } catch { }
            try { assistant?.Dispose(); } catch { }
        }

        // 多页签:宿主群聊页签把自己的绿提示/系统提示入口透传进来,转挂到每个成员 orch → 成员落地绿提示落回本群页签,不串到当前活动页。
        public Action<string, string, Action> GrantSink;
        public Action<string> SysSink;

        // 太吾途中插话:回复进行中再发一句 → 排队,ProcessGroupTurn 每步开头取出、插进对话流;导演与后续发言者都会看到并据此接话。
        private sealed class PendingInterjection
        {
            public string ExchangeId;
            public string Text;
        }

        private readonly object _interjectionGate = new object();
        private readonly Queue<PendingInterjection> _pendingInterjections = new Queue<PendingInterjection>();
        private bool _acceptingInterjections;
        private string _acceptingInterjectionExchangeId;

        public bool Interject(string text)
        {
            Line ignored;
            return Interject(text, out ignored);
        }

        public bool Interject(string text, out Line committedLine)
        {
            committedLine = null;
            if (!_transcriptReliable || string.IsNullOrWhiteSpace(text) || !WorldLifecycle.IsSameWorld(_worldGeneration)
                || !HasCurrentFileEpoch()) return false;
            // UI 已经显示的插话必须当场进 transcript；旧实现只排队到“下一轮开头”，默认 1 轮时
            // 会永久丢失，并在下次发言后错序出现。队列现在只保留“需要继续一轮回应”的信号。
            string normalized = text.Trim();
            lock (_interjectionGate)
            {
                string exchange = _currentExchangeId;
                if (!_acceptingInterjections || string.IsNullOrWhiteSpace(exchange)
                    || !string.Equals(exchange, _acceptingInterjectionExchangeId, StringComparison.Ordinal))
                    return false;
                var line = NewLine(exchange, _taiwuId, "太吾", true, normalized, CurrentDate(),
                    _lastTaiwuLocationText, "群聊");
                _transcript.Add(line);
                if (!SaveTranscript(contentChanged: true, changedExchangeId: exchange))
                { _transcript.Remove(line); return false; }
                _pendingInterjections.Enqueue(new PendingInterjection { ExchangeId = exchange, Text = normalized });
                committedLine = CloneLine(line);
            }
            MarkTaiwuTalkedToMembers();
            return true;
        }

        /// <summary>开群:各成员【并发】读取权威快照并准备缺失/过期画像，全部就绪后才载入群聊记录。
        /// 每成员一条独立 setup 协程,写入按 roster 下标的定长数组,全部完成后按原序装配 _members(不并发改 _members,免竞争)。</summary>
        public IEnumerator Open(int taiwuId, IList<KeyValuePair<int, string>> roster, Action<string> onProgress,
            Action onReady, Action<string> onError = null, string persistedGroupId = null)
        {
            CancelSetupCoroutines();
            _setupPortraitPriority = PortraitService.BeginInteractiveBurst();
            _setupAssistantPriority = AssistantWidget.BeginInteractivePriority();
            try
            {
            _worldGeneration = WorldLifecycle.Generation;
            if (!WorldLifecycle.IsSameWorld(_worldGeneration)) yield break;
            _taiwuId = taiwuId;
            _members.Clear();
            var acceptedRoster = new List<KeyValuePair<int, string>>();
            var seenRosterIds = new HashSet<int>();
            if (roster != null)
                foreach (var candidate in roster)
                {
                    if (candidate.Key <= 0 || candidate.Key == taiwuId || !seenRosterIds.Add(candidate.Key)) continue;
                    if (acceptedRoster.Count >= MaxGroupMembers) continue;
                    acceptedRoster.Add(candidate);
                }
            int n = acceptedRoster.Count;
            var results = new Member[n];
            var done = new bool[n];
            int portraitsReady = 0;
            onProgress?.Invoke("正在核对人物资料（0/" + n + "）…");
            Action memberReady = () =>
            {
                portraitsReady++;
                onProgress?.Invoke("正在核对人物资料（" + portraitsReady + "/" + n + "）…");
            };
            Action<string> portraitMissing = name =>
                onProgress?.Invoke("「" + (string.IsNullOrWhiteSpace(name) ? "群聊成员" : name)
                    + "」完全没有画像，正在生成…");
            var host = TalkEntryHost.Instance;
            if (host != null)
            {
                for (int i = 0; i < n; i++)
                    _setupCoroutines.Add(host.StartCoroutine(
                        SetupMember(taiwuId, acceptedRoster[i], i, results, done, memberReady,
                            portraitMissing)));
                // Every HTTP request owns a bounded 180-second execution timeout. Queue time is
                // deliberately excluded: eight cold portraits share three provider slots and
                // must not be reported as failed merely because a healthy earlier wave is active.
                yield return new WaitUntil(() =>
                {
                    for (int k = 0; k < n; k++) if (!done[k]) return false;
                    return true;
                });
                _setupCoroutines.Clear();
            }
            else
            {
                for (int i = 0; i < n; i++) yield return SetupMember(taiwuId,
                    acceptedRoster[i], i, results, done, memberReady, portraitMissing);   // 无宿主兜底:串行
            }
            if (!WorldLifecycle.IsSameWorld(_worldGeneration)) yield break;
            for (int i = 0; i < n; i++)
                if (!done[i] || results[i] == null)
                {
                    onError?.Invoke(!done[i]
                        ? "人物画像生成超时，本次群聊尚未开始，请稍后重试"
                        : "有群聊成员的人物资料未能准备完成，本次群聊尚未开始");
                    yield break;
                }
            for (int i = 0; i < n; i++) if (results[i] != null) _members.Add(results[i]);   // 按 roster 原序装配,保证确定性
            // 群聊是共享传音频道，不要求所有成员同地。这里只刷新“当前同道”标签；
            // 每位成员真正行动前，TalkOrchestrator 会重新读取同块/同道并自动切换 Remote 工具面。
            yield return RefreshCompanionRoster(taiwuId);
            _key = IsSafePersistedGroupId(persistedGroupId, taiwuId)
                ? persistedGroupId.Trim()
                : CreateNewGroupId(taiwuId);
            _journalWorldId = JianghuYoulingPaths.CurrentWorldId;
            if (_journalWorldId > 0)
            {
                _journalPath = Path.Combine(JianghuYoulingPaths.ChatLogs, "GroupTxn_" + _key + ".json");
                _exchangeJournal = new GroupExchangeJournal(
                    _journalPath,
                    _journalWorldId, _taiwuId, _key);
                _groupEpoch = CurrentGroupEpoch(_journalPath);
            }
            if (!LoadTranscript())
            {
                onError?.Invoke("群聊记录及其恢复副本均无法可靠读取；为防止历史丢失或动作重复，本群聊已封闭停止");
                yield break;
            }
            // Creating a group is itself a durable navigation event. Persist the empty member
            // roster immediately so the new group remains in the left navigator before anyone
            // has spoken and after a restart.
            if (!HasRecoverableDocument(ConvPath()) && !SaveTranscript())
            {
                onError?.Invoke("新群聊未能可靠写入磁盘，请稍后重试");
                yield break;
            }
            ReplayPreparedGroupExchanges();
            ReplayPendingMemoryRefresh();
            // A crash can happen after the parent transcript commit but before the cheap
            // per-member shared-memory projection. Rebuild it deterministically on open so
            // an existing group is never required to send (or close) one more turn before
            // every participant remembers the already committed exchange.
            ConsolidateMemoryOnly();
            MaybeArchiveOldExchanges();   // 屏障已清则封存超限旧轮
            onReady?.Invoke();
            }
            finally
            {
                _setupCoroutines.Clear();
                _setupPortraits.Clear();
                ReleaseSetupPriority();
            }
        }

        /// <summary>
        /// Adds people to this exact persisted group. The founding GroupId, transcript path,
        /// archive paths and transaction journal remain unchanged, so existing history stays in
        /// place and the new members receive the same prior transcript instead of opening a new
        /// member-set conversation.
        /// </summary>
        public IEnumerator AddMembers(IList<KeyValuePair<int, string>> additions,
            Action<string> onProgress, Action onReady, Action<string> onError = null)
        {
            if (!_transcriptReliable || !WorldLifecycle.IsSameWorld(_worldGeneration)
                || string.IsNullOrWhiteSpace(_key) || !HasCurrentFileEpoch()
                || !HasCurrentGroupEpoch())
            {
                onError?.Invoke("当前群聊状态尚未可靠就绪，不能加人");
                yield break;
            }
            if (_activeMemberRuns.Count > 0 || _ownedAttemptIds.Count > 0
                || !string.IsNullOrWhiteSpace(_currentExchangeId))
            {
                onError?.Invoke("本轮群聊仍在收尾，请稍后再加人");
                yield break;
            }

            var existingIds = ExpectedMemberIds();
            var accepted = new List<KeyValuePair<int, string>>();
            if (additions != null)
                foreach (KeyValuePair<int, string> candidate in additions)
                {
                    if (candidate.Key <= 0 || candidate.Key == _taiwuId
                        || existingIds.Contains(candidate.Key)
                        || accepted.Exists(x => x.Key == candidate.Key)) continue;
                    if (_members.Count + accepted.Count >= MaxGroupMembers) break;
                    accepted.Add(candidate);
                }
            if (accepted.Count == 0)
            {
                onError?.Invoke(_members.Count >= MaxGroupMembers
                    ? "这个群已经有八人，不能再加了"
                    : "没有选中新的群成员");
                yield break;
            }

            CancelSetupCoroutines();
            _setupPortraitPriority = PortraitService.BeginInteractiveBurst();
            _setupAssistantPriority = AssistantWidget.BeginInteractivePriority();
            try
            {
                int n = accepted.Count;
                var results = new Member[n];
                var done = new bool[n];
                int ready = 0;
                onProgress?.Invoke("正在准备新成员（0/" + n + "）…");
                Action memberReady = () =>
                {
                    ready++;
                    onProgress?.Invoke("正在准备新成员（" + ready + "/" + n + "）…");
                };
                Action<string> portraitMissing = name =>
                    onProgress?.Invoke("「" + (string.IsNullOrWhiteSpace(name) ? "新成员" : name)
                        + "」完全没有画像，正在生成…");
                var host = TalkEntryHost.Instance;
                if (host != null)
                {
                    for (int i = 0; i < n; i++)
                        _setupCoroutines.Add(host.StartCoroutine(
                            SetupMember(_taiwuId, accepted[i], i, results, done, memberReady,
                                portraitMissing)));
                    yield return new WaitUntil(() =>
                    {
                        for (int i = 0; i < n; i++) if (!done[i]) return false;
                        return true;
                    });
                    _setupCoroutines.Clear();
                }
                else
                {
                    for (int i = 0; i < n; i++)
                        yield return SetupMember(_taiwuId, accepted[i], i, results, done, memberReady,
                            portraitMissing);
                }
                if (!WorldLifecycle.IsSameWorld(_worldGeneration)) yield break;
                for (int i = 0; i < n; i++)
                    if (!done[i] || results[i] == null)
                    {
                        onError?.Invoke("有新成员的人物资料未能准备完成，本群成员没有改变");
                        yield break;
                    }
                var prepared = new List<Member>();
                for (int i = 0; i < n; i++) prepared.Add(results[i]);
                if (!TryCommitAddedMembers(prepared, out string error))
                {
                    onError?.Invoke(error ?? "群成员保存失败，原群没有改变");
                    yield break;
                }
                yield return RefreshCompanionRoster(_taiwuId);
                onReady?.Invoke();
            }
            finally
            {
                _setupCoroutines.Clear();
                _setupPortraits.Clear();
                ReleaseSetupPriority();
            }
        }

        private bool TryCommitAddedMembers(IList<Member> additions, out string error)
        {
            error = null;
            if (additions == null || additions.Count == 0) return true;
            string path = ConvPath();
            HashSet<int> oldMembers = ExpectedMemberIds();
            int originalCount = _members.Count;
            try
            {
                lock (GetFileLock(path))
                {
                    if (_fileEpoch != CurrentFileEpoch(path))
                    { error = "群聊已被其他窗口改动，请重新打开后再加人"; return false; }
                    bool reliable;
                    GroupDocument disk = ReadDocumentRecoverable(path, preserveCorrupt: true,
                        JianghuYoulingPaths.CurrentWorldId, _taiwuId, _key, oldMembers, out reliable);
                    if (!reliable || disk == null)
                    {
                        _transcriptReliable = false;
                        error = "原群聊记录无法可靠读取，已拒绝修改成员";
                        return false;
                    }
                    if (disk.PendingMemoryRefreshExchangeIds == null
                        || disk.PendingMemoryRefreshExchangeIds.Count > 0
                        || disk.PendingLinkedMemoryRemovals == null
                        || disk.PendingLinkedMemoryRemovals.Count > 0)
                    {
                        error = "群聊历史仍在清理或恢复，请稍后再加人";
                        return false;
                    }
                    if (!ReconcileArchivedSegments(disk))
                    {
                        error = "群聊归档尚未可靠同步，请稍后再加人";
                        return false;
                    }
                    _transcript = disk.Lines ?? new List<Line>();
                    _documentRevision = disk.Revision;
                    LoadPendingContextProjections(disk);
                    foreach (Member addition in additions)
                    {
                        if (addition == null || addition.Id <= 0 || addition.Id == _taiwuId
                            || oldMembers.Contains(addition.Id)) continue;
                        if (_members.Count >= MaxGroupMembers) break;
                        _members.Add(addition);
                        oldMembers.Add(addition.Id);
                    }
                    if (_members.Count == originalCount)
                    { error = "没有新的群成员"; return false; }

                    // Membership changes do not create a new conversation. Rewriting the active
                    // document with the same GroupId atomically publishes the expanded roster.
                    long nextRevision = checked(Math.Max(_documentRevision, disk.Revision) + 1);
                    GroupDocument updated = MakeDocument(_transcript, nextRevision);
                    WriteDocumentAtomic(path, updated, ".tmp");
                    _documentRevision = nextRevision;
                    _transcriptReliable = true;
                    return true;
                }
            }
            catch (Exception ex)
            {
                while (_members.Count > originalCount) _members.RemoveAt(_members.Count - 1);
                error = "群成员保存失败：" + ex.GetType().Name;
                Debug.LogWarning("[JHYL_GROUP_MEMBER_ADD] commit_failed group=" + _key
                    + " error=" + ex.GetType().Name);
                return false;
            }
        }

        /// <summary>
        /// 左侧永久会话导航的只读回看兜底。这里不读取画像、不恢复工具事务，也不授权发送；
        /// 正常续聊仍须走 Open 完成成员快照、画像与事务恢复。
        /// </summary>
        public bool OpenPersistedHistory(int taiwuId, IList<KeyValuePair<int, string>> roster,
            out string error, string persistedGroupId = null)
        {
            error = null;
            CancelSetupCoroutines();
            try
            {
                _worldGeneration = WorldLifecycle.Generation;
                if (taiwuId <= 0 || !WorldLifecycle.IsSameWorld(_worldGeneration))
                { error = "当前存档身份不可用"; return false; }
                _taiwuId = taiwuId;
                _members.Clear();
                var seen = new HashSet<int>();
                if (roster != null)
                    foreach (var pair in roster)
                    {
                        if (pair.Key <= 0 || pair.Key == taiwuId || !seen.Add(pair.Key)) continue;
                        if (_members.Count >= MaxGroupMembers) break;
                        _members.Add(new Member { Id = pair.Key, Name = pair.Value });
                    }
                if (_members.Count == 0) { error = "群成员身份缺失"; return false; }
                _key = IsSafePersistedGroupId(persistedGroupId, taiwuId)
                    ? persistedGroupId.Trim()
                    : GroupKey(taiwuId, _members);
                _journalWorldId = JianghuYoulingPaths.CurrentWorldId;
                if (_journalWorldId == 0) { error = "当前世界身份不可用"; return false; }
                _journalPath = Path.Combine(JianghuYoulingPaths.ChatLogs, "GroupTxn_" + _key + ".json");
                _exchangeJournal = new GroupExchangeJournal(
                    _journalPath, _journalWorldId, _taiwuId, _key);
                _groupEpoch = CurrentGroupEpoch(_journalPath);
                if (!LoadTranscript())
                { error = "群聊记录及其恢复副本无法可靠读取"; return false; }
                return true;
            }
            catch (Exception ex)
            {
                error = "群聊历史读取失败：" + ex.GetType().Name;
                return false;
            }
            finally
            {
                ReleaseSetupPriority();
            }
        }

        /// <summary>
        /// Replays every durable group transaction that belongs to the current world,
        /// including journals created by an earlier Taiwu identity. Recovery is scoped
        /// entirely by the persisted world/taiwu/group identities and never creates a
        /// new mutation operation id.
        /// </summary>
        public static void RecoverWorldTransactions()
        {
            uint worldId = JianghuYoulingPaths.CurrentWorldId;
            string directory = JianghuYoulingPaths.ChatLogs;
            int generation = WorldLifecycle.Generation;
            if (worldId == 0 || string.IsNullOrWhiteSpace(directory)
                || !Directory.Exists(directory) || !WorldLifecycle.IsSameWorld(generation)) return;

            MonoBehaviour host = TalkEntryHost.Instance;
            if (host == null) host = ConfigHost.Instance;
            if (host == null) return;
            lock (ActiveAttemptsGate)
            {
                if (_scheduledWorldRecoveryGeneration == generation
                    && _scheduledWorldRecoveryWorldId == worldId) return;
                _scheduledWorldRecoveryGeneration = generation;
                _scheduledWorldRecoveryWorldId = worldId;
            }
            try
            {
                host.StartCoroutine(RecoverWorldTransactionsCoroutine(worldId, directory, generation));
            }
            catch (Exception ex)
            {
                ResetScheduledWorldRecovery(worldId, generation);
                Debug.LogWarning("[江湖有灵] 启动群事务恢复失败 error_type=" + ex.GetType().Name);
            }
        }

        private static IEnumerator RecoverWorldTransactionsCoroutine(uint worldId, string directory, int generation)
        {
            IEnumerator<string> candidates;
            Exception scanError;
            if (!TryCreateRecoveryCandidateEnumerator(directory, "GroupTxn_*.json*", out candidates, out scanError))
            {
                ResetScheduledWorldRecovery(worldId, generation);
                Debug.LogWarning("[江湖有灵] 扫描群事务日志失败 error_type="
                    + scanError.GetType().Name);
                yield break;
            }

            try
            {
                while (WorldLifecycle.IsSameWorld(generation)
                    && JianghuYoulingPaths.CurrentWorldId == worldId)
                {
                    bool hasNext;
                    string candidate;
                    if (!TryMoveNextRecoveryCandidate(candidates, out hasNext, out candidate, out scanError))
                    {
                        ResetScheduledWorldRecovery(worldId, generation);
                        Debug.LogWarning("[江湖有灵] 扫描群事务日志失败 error_type="
                            + scanError.GetType().Name);
                        yield break;
                    }
                    if (!hasNext) break;
                    string journalPath;
                    if (TryNormalizeRecoveryJournalCandidate(candidate, out journalPath))
                        RecoverWorldTransaction(journalPath, worldId, directory, generation);

                    // A corrupt or very large world must not freeze the game's main
                    // thread. Each filesystem candidate consumes at most one frame.
                    yield return null;
                }
            }
            finally
            {
                try { candidates.Dispose(); } catch { }
            }

            if (!WorldLifecycle.IsSameWorld(generation)
                || JianghuYoulingPaths.CurrentWorldId != worldId)
            {
                ResetScheduledWorldRecovery(worldId, generation);
                yield break;
            }
            if (!TryCreateRecoveryCandidateEnumerator(directory, "Group_*.json*", out candidates, out scanError))
            {
                ResetScheduledWorldRecovery(worldId, generation);
                Debug.LogWarning("[江湖有灵] 扫描群聊上下文投影提交屏障失败 error_type="
                    + scanError.GetType().Name);
                yield break;
            }
            var projectionRetries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                while (WorldLifecycle.IsSameWorld(generation)
                    && JianghuYoulingPaths.CurrentWorldId == worldId)
                {
                    bool hasNext;
                    string candidate;
                    if (!TryMoveNextRecoveryCandidate(candidates, out hasNext, out candidate, out scanError))
                    {
                        ResetScheduledWorldRecovery(worldId, generation);
                        Debug.LogWarning("[江湖有灵] 扫描群聊上下文投影提交屏障失败 error_type="
                            + scanError.GetType().Name);
                        yield break;
                    }
                    if (!hasNext) break;
                    string transcriptPath;
                    if (TryNormalizeRecoveryTranscriptCandidate(candidate, out transcriptPath))
                    {
                        if (!RecoverWorldMemoryProjection(transcriptPath, worldId, directory, generation))
                            projectionRetries.Add(transcriptPath);
                    }
                    yield return null;
                }
            }
            finally
            {
                try { candidates.Dispose(); } catch { }
            }

            int completedAttempts = 1;
            while (GroupMemoryProjectionPolicy.ShouldRetryStartupRecovery(
                projectionRetries.Count > 0, completedAttempts)
                && WorldLifecycle.IsSameWorld(generation)
                && JianghuYoulingPaths.CurrentWorldId == worldId)
            {
                yield return new WaitForSecondsRealtime(
                    GroupMemoryProjectionPolicy.StartupRecoveryDelaySeconds(completedAttempts));
                var stillPending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string transcriptPath in projectionRetries)
                {
                    if (!WorldLifecycle.IsSameWorld(generation)
                        || JianghuYoulingPaths.CurrentWorldId != worldId) break;
                    if (!RecoverWorldMemoryProjection(transcriptPath, worldId, directory, generation))
                        stillPending.Add(transcriptPath);
                    yield return null;
                }
                projectionRetries = stillPending;
                completedAttempts++;
            }
            if (projectionRetries.Count > 0
                && WorldLifecycle.IsSameWorld(generation)
                && JianghuYoulingPaths.CurrentWorldId == worldId)
                Debug.LogWarning("[江湖有灵] 群聊上下文投影启动恢复已达有界重试上限 pending_count="
                    + projectionRetries.Count + " attempts=" + completedAttempts);
            ResetScheduledWorldRecovery(worldId, generation);
        }

        private static bool TryCreateRecoveryCandidateEnumerator(string directory, string pattern,
            out IEnumerator<string> candidates, out Exception error)
        {
            candidates = null;
            error = null;
            try
            {
                candidates = Directory.EnumerateFiles(directory, pattern).GetEnumerator();
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                return false;
            }
        }

        private static bool TryMoveNextRecoveryCandidate(IEnumerator<string> candidates,
            out bool hasNext, out string candidate, out Exception error)
        {
            hasNext = false;
            candidate = null;
            error = null;
            try
            {
                hasNext = candidates.MoveNext();
                if (hasNext) candidate = candidates.Current;
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                return false;
            }
        }

        private static void ResetScheduledWorldRecovery(uint worldId, int generation)
        {
            lock (ActiveAttemptsGate)
                if (_scheduledWorldRecoveryGeneration == generation
                    && _scheduledWorldRecoveryWorldId == worldId)
                {
                    _scheduledWorldRecoveryGeneration = -1;
                    _scheduledWorldRecoveryWorldId = 0;
                }
        }

        private static bool TryNormalizeRecoveryJournalCandidate(string candidate, out string journalPath)
        {
            journalPath = candidate;
            if (string.IsNullOrWhiteSpace(candidate)) return false;
            bool temporary = candidate.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
            bool backup = candidate.EndsWith(".bak", StringComparison.OrdinalIgnoreCase);
            if (temporary || backup) journalPath = candidate.Substring(0, candidate.Length - 4);
            if (!journalPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
            if (!temporary && !backup) return true;
            // The canonical path validates/reconciles all three replicas. Only use a
            // sidecar as the enumeration representative when the canonical file is gone.
            if (File.Exists(journalPath)) return false;
            if (backup && File.Exists(journalPath + ".tmp")) return false;
            return true;
        }

        private static bool TryNormalizeRecoveryTranscriptCandidate(string candidate, out string transcriptPath)
        {
            transcriptPath = candidate;
            if (string.IsNullOrWhiteSpace(candidate)) return false;
            bool temporary = candidate.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
            bool backup = candidate.EndsWith(".bak", StringComparison.OrdinalIgnoreCase);
            if (temporary || backup) transcriptPath = candidate.Substring(0, candidate.Length - 4);
            string name = Path.GetFileName(transcriptPath);
            if (string.IsNullOrWhiteSpace(name)
                || !name.StartsWith("Group_", StringComparison.OrdinalIgnoreCase)
                || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
            if (!temporary && !backup) return true;
            if (File.Exists(transcriptPath)) return false;
            if (backup && File.Exists(transcriptPath + ".tmp")) return false;
            return true;
        }

        private static bool RecoverWorldMemoryProjection(string transcriptPath, uint worldId,
            string directory, int generation)
        {
            if (!WorldLifecycle.IsSameWorld(generation)
                || JianghuYoulingPaths.CurrentWorldId != worldId) return true;
            try
            {
                bool reliable;
                GroupDocument document = ReadDocumentRecoverable(transcriptPath, preserveCorrupt: true,
                    worldId, 0, null, null, out reliable);
                if (!reliable) return false;
                if (document == null || document.TaiwuId <= 0
                    || string.IsNullOrWhiteSpace(document.GroupId)
                    || document.Members == null || document.Members.Count == 0) return true;
                string expectedName = "Group_" + document.GroupId + ".json";
                if (!string.Equals(Path.GetFileName(transcriptPath), expectedName,
                    StringComparison.OrdinalIgnoreCase)) return true;
                if (!GroupMemoryProjectionPolicy.NeedsRecovery(DocumentNeedsMemoryProjection(document))
                    && (document.PendingMemoryRefreshExchangeIds == null
                        || document.PendingMemoryRefreshExchangeIds.Count == 0)
                    && (document.PendingLinkedMemoryRemovals == null
                        || document.PendingLinkedMemoryRemovals.Count == 0)) return true;

                var recovered = new GroupChatOrchestrator
                {
                    _worldGeneration = generation,
                    _taiwuId = document.TaiwuId,
                    _key = document.GroupId,
                    _journalWorldId = worldId,
                    _journalPath = Path.Combine(directory, "GroupTxn_" + document.GroupId + ".json"),
                    _transcript = document.Lines ?? new List<Line>(),
                    _documentRevision = document.Revision,
                    _archivedSegmentCount = document.ArchivedSegmentCount,
                    _transcriptReliable = true,
                };
                recovered.LoadPendingContextProjections(document);
                var memberIds = new HashSet<int>();
                foreach (MemberRef member in document.Members)
                {
                    if (member == null || member.Id <= 0 || member.Id == document.TaiwuId
                        || !memberIds.Add(member.Id)) return true;
                    recovered._members.Add(new Member { Id = member.Id, Name = member.Name });
                }
                recovered.MergePendingMemoryRefresh(document);
                recovered.MergePendingLinkedMemoryRemovals(document);
                recovered.NormalizeTranscript();
                lock (GetFileLock(transcriptPath))
                    recovered._fileEpoch = CurrentFileEpoch(transcriptPath);
                recovered._groupEpoch = CurrentGroupEpoch(recovered._journalPath);
                recovered.ReplayPendingMemoryRefresh();
                if (recovered._pendingMemoryRemovalExchangeIds.Count > 0
                    || recovered._pendingLinkedMemoryRemovals.Count > 0) return false;
                // 摘要压缩或完整原文回退由后台协程完成；成功调度即可结束启动扫描。若个人
                // 上下文存盘失败，持久 pending 标记仍在，后台协程会有界重试，重启后也可恢复。
                return !recovered._memoryProjectionPending || recovered.ConsolidateMemoryOnly();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[江湖有灵] 群聊上下文投影恢复失败 error_type=" + ex.GetType().Name);
                return false;
            }
        }

        private static void RecoverWorldTransaction(string journalPath, uint worldId,
            string directory, int generation)
        {
            if (!WorldLifecycle.IsSameWorld(generation)
                || JianghuYoulingPaths.CurrentWorldId != worldId) return;
            try
            {
                GroupExchangeJournal journal;
                int persistedTaiwuId;
                string groupId;
                IReadOnlyList<GroupExchangeJournal.Entry> entries;
                if (!GroupExchangeJournal.TryOpenExisting(journalPath, worldId, out journal,
                        out persistedTaiwuId, out groupId, out entries)
                    || journal == null || entries == null || entries.Count == 0
                    || persistedTaiwuId <= 0 || string.IsNullOrWhiteSpace(groupId)) return;

                string expectedJournalName = "GroupTxn_" + groupId + ".json";
                if (!string.Equals(Path.GetFileName(journalPath), expectedJournalName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogWarning("[江湖有灵] 群事务日志文件名与持久化 GroupId 不一致，拒绝恢复:"
                        + Path.GetFileName(journalPath));
                    return;
                }

                string transcriptPath = Path.Combine(directory, "Group_" + groupId + ".json");
                GroupDocument document = ReadDocumentRecoverable(transcriptPath, preserveCorrupt: true,
                    worldId, persistedTaiwuId, groupId, null);
                if (document == null || document.Members == null || document.Members.Count == 0) return;

                var memberIds = MemberIds(document);
                bool journalScopeValid = memberIds.Count == document.Members.Count
                    && !memberIds.Contains(persistedTaiwuId);
                foreach (GroupExchangeJournal.Entry entry in entries)
                    if (entry == null || !memberIds.Contains(entry.NpcId))
                    {
                        journalScopeValid = false;
                        break;
                    }
                if (!journalScopeValid)
                {
                    Debug.LogWarning("[江湖有灵] 群事务成员身份与 transcript 不一致，拒绝恢复 group=" + groupId);
                    return;
                }

                var recovered = new GroupChatOrchestrator
                {
                    _worldGeneration = generation,
                    _taiwuId = persistedTaiwuId,
                    _key = groupId,
                    _journalWorldId = worldId,
                    _journalPath = journalPath,
                    _exchangeJournal = journal,
                    _transcript = document.Lines ?? new List<Line>(),
                    _documentRevision = document.Revision,
                    _archivedSegmentCount = document.ArchivedSegmentCount,
                    _transcriptReliable = true,
                };
                recovered.LoadPendingContextProjections(document);
                foreach (MemberRef member in document.Members)
                    recovered._members.Add(new Member { Id = member.Id, Name = member.Name });
                recovered.MergePendingMemoryRefresh(document);
                recovered.MergePendingLinkedMemoryRemovals(document);
                recovered.NormalizeTranscript();
                lock (GetFileLock(transcriptPath))
                    recovered._fileEpoch = CurrentFileEpoch(transcriptPath);
                recovered._groupEpoch = CurrentGroupEpoch(journalPath);
                recovered.ReplayPreparedGroupExchanges();
                // World-start recovery must also consume cleanup work persisted in the
                // transcript document. Waiting for this exact group to be opened leaves
                // deleted exchanges orphaned in member memory after a process crash.
                recovered.ReplayPendingMemoryRefresh();
                // The transcript is the parent commit. Its per-member memory view is a
                // deterministic projection, so rebuilding it here closes the opposite
                // crash window (transcript committed, projection not yet written).
                recovered.ConsolidateMemoryOnly();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[江湖有灵] 群事务恢复失败 " + Path.GetFileName(journalPath)
                    + ":" + ex.GetType().Name);
            }
        }

        private IEnumerator RefreshCompanionRoster(int taiwuId)
        {
            bool teamDone = false;
            List<int> companions = null;
            NpcSnapshotReader.FetchGroupMembers(taiwuId, ids => { companions = ids; teamDone = true; });
            float deadline = Time.unscaledTime + 2.5f;
            while (!teamDone && Time.unscaledTime < deadline
                && WorldLifecycle.IsSameWorld(_worldGeneration)) yield return null;

            _authoritativeCompanionIds.Clear();
            if (teamDone && companions != null && WorldLifecycle.IsSameWorld(_worldGeneration))
                foreach (int id in companions)
                    if (id > 0) _authoritativeCompanionIds.Add(id);
        }

        // 单成员入座：关系/好感随首个 CharacterDisplayData 权威快照一起取得；成员之间并发。
        // 画像只看当前是否存在可读正文，完全缺失时才执行 single-flight 蒸馏。
        private IEnumerator SetupMember(int taiwuId, KeyValuePair<int, string> m, int idx, Member[] results, bool[] done,
            Action onPortraitReady, Action<string> onPortraitMissing = null)
        {
            int id = m.Key;
            NpcSnapshot snap = null;
            try
            {
                yield return NpcSnapshotReader.Fetch(id, s => snap = s);
                if (snap == null || snap.NpcId < 0) yield break;
                _setupPortraits.Add(snap);
                string portrait = PortraitStore.GetPortrait(snap);
                if (string.IsNullOrWhiteSpace(portrait))
                {
                    onPortraitMissing?.Invoke(string.IsNullOrWhiteSpace(m.Value) ? snap.Name : m.Value);
                    yield return PortraitService.EnsureSeededReady(snap, value => portrait = value);
                }
                if (string.IsNullOrWhiteSpace(portrait)) yield break;
                results[idx] = new Member
                {
                    Id = id,
                    Name = string.IsNullOrWhiteSpace(m.Value) ? (snap.Name ?? ("#" + id)) : m.Value,
                    Snap = snap
                };
            }
            finally
            {
                if (snap != null) _setupPortraits.Remove(snap);
                done[idx] = true;
                onPortraitReady?.Invoke();
            }
        }

        private string BuildIdentityContext(Member current, NpcSnapshot currentSnapshot = null)
        {
            var sb = new StringBuilder();
            NpcSnapshot source = currentSnapshot ?? current?.Snap
                ?? _members.Find(x => x?.Snap != null)?.Snap;
            string taiwuName = string.IsNullOrWhiteSpace(source?.TaiwuName) ? "太吾" : source.TaiwuName.Trim();
            string taiwuGender = string.IsNullOrWhiteSpace(source?.TaiwuGender) ? "未知" : source.TaiwuGender.Trim();
            sb.Append("太吾/玩家=").Append(taiwuName).Append("(#").Append(_taiwuId)
                .Append(",").Append(taiwuGender).Append(")");
            foreach (Member member in _members)
            {
                if (member == null || member.Id <= 0) continue;
                string name = string.IsNullOrWhiteSpace(member.Name) ? ("#" + member.Id) : member.Name.Trim();
                NpcSnapshot memberSnapshot = member.Id == current?.Id && currentSnapshot != null
                    ? currentSnapshot : member.Snap;
                string gender = string.IsNullOrWhiteSpace(memberSnapshot?.Gender)
                    ? "未知" : memberSnapshot.Gender.Trim();
                sb.Append("；").Append(member.Id == current?.Id ? "当前发言者=" : "群成员=")
                    .Append(name).Append("(#").Append(member.Id).Append(",").Append(gender).Append(")");
            }
            return sb.ToString();
        }

        /// <summary>一次群聊发送(按轮·真并发):太吾说一句 → 每一轮让在场众人【并发】各跑一遍完整 ProcessTurn(各看本轮此刻的对话快照),
        /// 故每轮所有成员都形成可见回应；第二轮起众人能看到上一轮各人所说、彼此接话/反驳。没有新的玩家消息时最多三轮；太吾中途插话会作为新的玩家消息重新计算自动轮数。
        /// 子-agent 架构:每个成员一个独立 TalkOrchestrator 实例(per-turn 落地/提示状态已实例化、各记各的),N 路 LLM 往返并行、互不串台。</summary>
        public IEnumerator ProcessGroupTurn(string playerInput,
            Action<string> onProgress,
            Action<string> onSpeakerStart,
            Action<string, string> onSpeakerDelta,
            Action<string, string, Line> onSpeakerDone,
            Action onTurnDone, Action<string> onError,
            Action onSpeakerCancel = null,
            Action onRoundThinkStart = null, Action onRoundThinkEnd = null,
            Action<string, IList<string>> onResultOnly = null,
            TaiwuDirectActionReceipt directTaiwuAction = null,
            Action<Line> onPlayerCommitted = null)
        {
            ReleaseTurnPriority();
            _turnPortraitPriority = PortraitService.BeginInteractiveBurst();
            _turnAssistantPriority = AssistantWidget.BeginInteractivePriority();
            try
            {
            Debug.Log("[JHYL_GROUP_TURN_START] members=" + _members.Count
                + " input_chars=" + (playerInput == null ? 0 : playerInput.Length));
            _cts = new System.Threading.CancellationTokenSource();
            var ct = _cts.Token;
            CloseInterjectionWindow(null, true);
            if (!_transcriptReliable) { onError?.Invoke("群聊记录读取不可靠，本轮未发送"); yield break; }
            var client = LlmService.GetClient();
            if (client == null) { onError?.Invoke("未配置接口"); yield break; }
            if (_members.Count == 0) { onError?.Invoke("群里没有其他人"); yield break; }
            var host = TalkEntryHost.Instance;
            if (host == null) { onError?.Invoke("宿主缺失"); yield break; }
            // 共享频道允许远隔成员继续说话；刷新同道标签即可。每个子 Agent 在工具路由前
            // 都会调用 RecalculateRemoteFromAuthoritativePresence，物理动作仍严格要求同块/同道。
            yield return RefreshCompanionRoster(_taiwuId);

            // 与群关系矩阵并行读取太吾本人的权威位置。它只用于记录这句玩家发言
            // 的说话者地点，不参与群成员是否同地的判定。
            NpcSnapshot taiwuDisplay = null;
            bool taiwuDisplayDone = false;
            host.StartCoroutine(NpcSnapshotReader.FetchDisplayOnly(_taiwuId,
                value => { taiwuDisplay = value; taiwuDisplayDone = true; },
                () => !ct.IsCancellationRequested && WorldLifecycle.IsSameWorld(_worldGeneration)));

            // Every participant receives the same authoritative all-pairs matrix. One batched
            // read per group turn is both faster and stronger than letting each member guess or
            // issue an incomplete per-person query later in its own agent loop.
            var relationIds = new List<int> { _taiwuId };
            foreach (Member member in _members)
                if (member != null && member.Id > 0 && !relationIds.Contains(member.Id)) relationIds.Add(member.Id);
            bool relationDone = false;
            bool relationOk = false;
            string relationshipContext = null;
            EffectHandler.QueryRosterRelations(relationIds, true,
                (ok, value) => { relationOk = ok; relationshipContext = value; relationDone = true; });
            float relationDeadline = Time.unscaledTime + 8f;
            while ((!relationDone || !taiwuDisplayDone) && Time.unscaledTime < relationDeadline
                && !ct.IsCancellationRequested) yield return null;
            if (!relationDone || !relationOk)
            {
                Debug.LogWarning("[JHYL_GROUP_RELATION_PREFLIGHT] "
                    + (!relationDone ? "timeout" : "backend_failed") + " chars=" + relationIds.Count);
                onError?.Invoke("未能可靠读取群内所有人物的关系，本轮没有发送；请重试或查看日志");
                yield break;
            }
            if (string.IsNullOrWhiteSpace(relationshipContext)) relationshipContext = "群内各人之间均无显著关系";
            _lastTaiwuLocationText = PrimaryLocationText(taiwuDisplay?.LocationText)
                ?? "去向不明（游戏当前未提供有效地点）";
            Debug.Log("[JHYL_GROUP_RELATION_PREFLIGHT] members=" + _members.Count
                + " chars=" + relationIds.Count + " pairs=" + (relationIds.Count * (relationIds.Count - 1) / 2));

            long turnEpoch;
            string transcriptPath = ConvPath();
            lock (GetFileLock(transcriptPath)) turnEpoch = CurrentFileEpoch(transcriptPath);
            if (_fileEpoch != turnEpoch) { onError?.Invoke("群聊记录已被清空，本轮作废"); yield break; }

            string exchangeId = NewId("x");
            _currentExchangeId = exchangeId;
            var playerLine = NewLine(exchangeId, _taiwuId, "太吾", true, playerInput, CurrentDate(),
                _lastTaiwuLocationText, "群聊");
            _transcript.Add(playerLine);
            if (!SaveTranscript(contentChanged: true, changedExchangeId: exchangeId))
            {
                _transcript.Remove(playerLine);
                EndInterjectionExchange(exchangeId);
                onError?.Invoke("群聊记录保存失败，本轮作废");
                yield break;
            }
            try { onPlayerCommitted?.Invoke(CloneLine(playerLine)); } catch { }
            // JHYL_COMPANION_MONTHLY_GROUP_CHAT_MARK
            MarkTaiwuTalkedToMembers();
            BeginInterjectionWindow(exchangeId);

            int maxAutoRounds = directTaiwuAction != null
                ? 1
                : GroupRoundsStore.Load();   // 按钮行动只请收件人当轮回应；普通群聊仍自动交流 1~3 轮
            var groupRequestBudget = new GroupTurnRequestBudget();
            long relationshipRevision = 0L;
            var memoryRecallCache = new ConcurrentDictionary<string, MemoryRecaller.RecallResult>(StringComparer.Ordinal);
            string activeMemoryRecallTopic = playerInput ?? string.Empty;
            string activePlayerRecallTopic = activeMemoryRecallTopic;
            int totalRound = 0;
            int roundsSincePlayerMessage = 0;
            bool firstPlayerMessage = true;
            bool contextSaved = false;
            while (firstPlayerMessage || roundsSincePlayerMessage < maxAutoRounds
                || HasPendingInterjections(exchangeId))
            {
                if (!TurnStillCurrent(ct, turnEpoch)) { RecoverOwnedAttempts(exchangeId); EndInterjectionExchange(exchangeId); onError?.Invoke("已中断"); yield break; }
                string interjectionInput = DrainInterjections(exchangeId);
                if (!string.IsNullOrWhiteSpace(interjectionInput))
                {
                    // An interjection is a new player message, not one of the NPCs' autonomous
                    // continuation rounds. It therefore starts a fresh bounded 1..3-round cycle.
                    roundsSincePlayerMessage = 0;
                    activeMemoryRecallTopic = interjectionInput.Trim();
                    activePlayerRecallTopic = activeMemoryRecallTopic;
                    memoryRecallCache.Clear();
                    Debug.Log("[JHYL_GROUP_INTERJECTION_RESET] auto_limit=" + maxAutoRounds);
                }
                totalRound++;
                roundsSincePlayerMessage++;
                // Bind every child receipt and transcript line in this round to one live
                // BasicGameData date. Member.Snap was captured when the tab opened and can
                // legitimately be months old while a group tab remains alive.
                int roundDate = CurrentDate();
                // The line is already durable in the transcript, but its exact text must
                // also drive this round's routing and action journal.
                string roundInput;
                if (firstPlayerMessage)
                    roundInput = string.IsNullOrWhiteSpace(interjectionInput)
                        ? playerInput
                        : interjectionInput;
                else roundInput = interjectionInput ?? "";
                string roundMemoryRecallTopic = string.IsNullOrWhiteSpace(roundInput)
                    ? activeMemoryRecallTopic
                    : roundInput.Trim();
                firstPlayerMessage = false;
                // 普通群聊采用全员回应；界面已明确选择行动对象时只让该收件人回应，
                // 但它仍能看到完整群聊历史与频道成员。
                var order = directTaiwuAction != null
                    ? DirectActionTargetIndices(directTaiwuAction.TargetIds())
                    : RoutedIndices(roundInput, totalRound);
                if (order == null || order.Count == 0)
                {
                    Debug.LogError("[JHYL_GROUP_AGENT_ROUTE] empty route round=" + totalRound + " members=" + _members.Count);
                    EndInterjectionExchange(exchangeId);
                    onError?.Invoke("群聊消息没有路由到任何成员，本轮已停止；请查看日志");
                    yield break;
                }
                Debug.Log("[JHYL_GROUP_AGENT_ROUTE] round=" + totalRound + " fanout=" + order.Count
                    + "/" + _members.Count + " mandatory=" + order.Count);
                var actionCoordinator = new JianghuYouling.Core.Tools.GroupActionCoordinator();
                var participantIds = new HashSet<int>();
                foreach (var member in _members) if (member != null && member.Id > 0) participantIds.Add(member.Id);
                var companionNames = new List<string>();
                var ordinaryChannelNames = new List<string>();
                foreach (var member in _members)
                    if (member != null)
                    {
                        if (_authoritativeCompanionIds.Contains(member.Id)) companionNames.Add(member.Name);
                        else ordinaryChannelNames.Add(member.Name);
                    }
                // —— 并发启动:每个成员一个独立 orch,各自跑 ProcessTurn(看的是本轮此刻的对话快照;回话稍后才追加进 transcript)——
                var runs = new List<MemberRun>();
                onRoundThinkStart?.Invoke();   // 先显示进度，再把 N 份巨型提示构造分散到不同帧，避免 Unity 单帧假死。
                onProgress?.Invoke("群聊第" + roundsSincePlayerMessage + "/" + maxAutoRounds
                    + "轮：0/" + order.Count + "人已完成");
                for (int oi = 0; oi < order.Count; oi++)
                {
                    int idx = order[oi];
                    var sp = _members[idx];
                    // 全员回答是群聊的产品契约。即便是 NPC 彼此续聊轮，只要该轮已经开始，
                    // 每个成员都必须形成一条可见回应，不能把已发出的完整 Agent 请求藏成“旁听”。
                    bool mustReply = true;
                    var others = new List<string>(); foreach (var m in _members) if (m != sp) others.Add(m.Name);
                    var run = new MemberRun
                    {
                        Sp = sp,
                        AttemptId = NewId("a"),
                        ExchangeId = exchangeId,
                        PlayerInput = roundInput,
                        WorldDate = roundDate,
                        MustReply = mustReply,
                    };
                    string memoryRecallCacheKey = sp.Id + "\n" + (activePlayerRecallTopic ?? string.Empty);
                    if (!RegisterOwnedAttempt(run.AttemptId))
                    {
                        foreach (var started in runs)
                        {
                            if (started == null) continue;
                            if (started.Done) ReleaseOwnedAttempt(started.AttemptId);
                            else
                            {
                                started.ParentDetached = true;
                                started.Error = "群聊已被其它页签清空";
                                try { started.Orch?.Cancel(); } catch { }
                            }
                        }
                        EndInterjectionExchange(exchangeId);
                        onError?.Invoke("群聊已被其它页签清空，本轮作废");
                        yield break;
                    }
                    var orch = new TalkOrchestrator
                    {
                        DirectTaiwuAction = directTaiwuAction?.ForTarget(sp.Id),
                        GroupCtx = new TalkOrchestrator.GroupContext
                        {
                            SelfName = sp.Name,
                            Others = others,
                            MemberId = sp.Id,
                            DispatchOrder = oi,
                            ActionCoordinator = actionCoordinator,
                            ParticipantIds = new HashSet<int>(participantIds),
                            GroupId = _key,
                            ExchangeId = exchangeId,
                            AttemptId = run.AttemptId,
                            CompanionNames = new List<string>(companionNames),
                            OrdinaryChannelNames = new List<string>(ordinaryChannelNames),
                            RelationshipContext = relationshipContext,
                            RelationshipRevision = () => Interlocked.Read(ref relationshipRevision),
                            MarkRelationshipsDirty = () => Interlocked.Increment(ref relationshipRevision),
                            SeenRelationshipRevision = 0L,
                            IdentityContext = BuildIdentityContext(sp),
                            RefreshIdentityContext = latest => BuildIdentityContext(sp, latest),
                            MustReply = mustReply,
                            CurrentTurnInput = roundInput,
                            MemoryRecallTopic = roundMemoryRecallTopic,
                            GetCachedMemoryRecall = memberId =>
                            {
                                memoryRecallCache.TryGetValue(memoryRecallCacheKey, out MemoryRecaller.RecallResult cached);
                                return cached;
                            },
                            StoreCachedMemoryRecall = (memberId, recalled) =>
                            {
                                if (memberId > 0 && recalled != null)
                                    memoryRecallCache[memoryRecallCacheKey] = recalled;
                            },
                            ReserveModelRequest = groupRequestBudget.Reserve,
                            DispatchStillValid = () => HasCurrentGroupEpoch() && HasCurrentFileEpoch(),
                            PrepareCommit = (npcId, memoryIds, actions, operationIds) =>
                            {
                                bool prepared = PrepareGroupExchange(exchangeId, run.AttemptId, npcId, roundInput,
                                    roundDate, memoryIds, actions, operationIds);
                                if (prepared)
                                {
                                    run.Prepared = true;
                                    run.MemoryIds = memoryIds == null ? null : new List<string>(memoryIds);
                                    run.Actions = actions == null ? null : new List<string>(actions);
                                }
                                return prepared;
                            },
                        },
                        GrantSink = GrantSink,
                        SysSink = SysSink
                    };
                    run.Orch = orch;
                    runs.Add(run); _activeRuns.Add(orch); _activeMemberRuns.Add(run);
                    host.StartCoroutine(RunMemberOrch(orch, sp, BuildGroupInputFor(sp), mustReply, run));
                    yield return null;
                }
                // 第一段截止只负责请求取消；绝不能把“Cancel 已调用”冒充“worker finally 已收口”。
                // 常见取消会在短暂 drain 窗内完成并照常提交；极端不返回的 worker 才安全断开，
                // 由它自己的 finally 在真正结束后补写 ToolResults 并走幂等恢复。
                // Watch for a genuinely stalled group rather than applying one absolute deadline
                // to all members.  Eight members share three provider slots and each member may
                // legitimately need a six-round tool chain.  Every completed member renews the
                // watchdog so healthy queued work and active multi-round tool chains are never
                // canceled merely for waiting their turn or for not having completed a member yet.
                long observedProgress = 0;
                foreach (MemberRun r in runs)
                    if (r != null) observedProgress += Interlocked.Read(ref r.ProgressVersion);
                int lastReportedCompleted = 0;
                float idleDeadline = Time.unscaledTime + MemberGenerationIdleTimeoutSeconds;
                float hardDeadline = Time.unscaledTime + GroupBatchHardTimeoutSeconds;
                while (!ct.IsCancellationRequested && FileEpochStillCurrent(turnEpoch)
                    && HasCurrentGroupEpoch() && !AllMemberRunsDone(runs))
                {
                    long progressNow = 0;
                    foreach (MemberRun r in runs)
                        if (r != null) progressNow += Interlocked.Read(ref r.ProgressVersion);
                    if (progressNow != observedProgress)
                    {
                        observedProgress = progressNow;
                        idleDeadline = Time.unscaledTime + MemberGenerationIdleTimeoutSeconds;
                    }
                    int completedNow = 0;
                    foreach (MemberRun r in runs) if (r != null && r.Done) completedNow++;
                    if (completedNow != lastReportedCompleted)
                    {
                        lastReportedCompleted = completedNow;
                        onProgress?.Invoke("群聊第" + roundsSincePlayerMessage + "/" + maxAutoRounds
                            + "轮：" + completedNow + "/" + runs.Count + "人已完成");
                    }
                    if (Time.unscaledTime > idleDeadline || Time.unscaledTime > hardDeadline) break;
                    yield return null;
                }
                onProgress?.Invoke("群聊第" + roundsSincePlayerMessage + "/" + maxAutoRounds
                    + "轮：" + runs.FindAll(r => r != null && r.Done).Count + "/" + runs.Count + "人已完成");
                bool hardDeadlineReached = !AllMemberRunsDone(runs)
                    && Time.unscaledTime > hardDeadline;
                if (hardDeadlineReached)
                    Debug.LogWarning("[JHYL_GROUP_BATCH_HARD_TIMEOUT] round=" + totalRound
                        + " seconds=" + GroupBatchHardTimeoutSeconds);

                bool turnInvalidated = !TurnStillCurrent(ct, turnEpoch);
                if (turnInvalidated)
                {
                    foreach (MemberRun r in runs)
                    {
                        if (r == null || r.Done) continue;
                        r.ParentDetached = true;
                        r.Error = string.IsNullOrWhiteSpace(r.Error) ? "已中断" : r.Error;
                        try { r.Orch?.Cancel(); } catch { }
                    }
                    PersistAndRecoverCompletedRuns(exchangeId, runs);
                    onRoundThinkEnd?.Invoke();
                    EndInterjectionExchange(exchangeId);
                    onError?.Invoke("已中断");
                    yield break;
                }

                bool timedOut = false;
                foreach (MemberRun r in runs)
                    if (r != null && !r.Done)
                    {
                        timedOut = true;
                        try { r.Orch?.Cancel(); } catch { }
                    }
                if (timedOut)
                {
                    float drainDeadline = Time.unscaledTime + MemberCancellationDrainSeconds;
                    while (Time.unscaledTime < drainDeadline && TurnStillCurrent(ct, turnEpoch)
                        && !AllMemberRunsDone(runs)) yield return null;
                    if (!TurnStillCurrent(ct, turnEpoch))
                    {
                        foreach (MemberRun r in runs)
                            if (r != null && !r.Done)
                            {
                                r.ParentDetached = true;
                                r.Error = string.IsNullOrWhiteSpace(r.Error) ? "已中断" : r.Error;
                                try { r.Orch?.Cancel(); } catch { }
                            }
                        PersistAndRecoverCompletedRuns(exchangeId, runs);
                        onRoundThinkEnd?.Invoke();
                        EndInterjectionExchange(exchangeId);
                        onError?.Invoke("已中断");
                        yield break;
                    }
                }

                bool detachedWorker = false;
                foreach (MemberRun r in runs)
                    if (r != null && !r.Done)
                    {
                        detachedWorker = true;
                        r.ParentDetached = true;
                        r.Reply = null;
                        r.Error = hardDeadlineReached
                            ? "群聊本轮已达到总时限；晚到执行结果将自动写入群聊记录"
                            : "生成超时；晚到执行结果将自动写入群聊记录";
                        try { r.Orch?.Cancel(); } catch { }
                    }
                onRoundThinkEnd?.Invoke();      // 移除集体思考气泡
                // The child barrier is prepared as soon as a side-effecting tool is dispatched,
                // but the complete visible receipt list only exists after the member loop exits.
                // Persist that final list before committing the parent transcript so crash recovery
                // can reconstruct the exact green rows, including a result-only (empty body) turn.
                foreach (var r in runs)
                    if (r.Done && !r.ParentDetached && r.Prepared && !_exchangeJournal.UpdateToolResults(exchangeId, r.AttemptId,
                            r.Sp.Id, r.ToolResults))
                    {
                        PersistAndRecoverCompletedRuns(exchangeId, runs);
                        EndInterjectionExchange(exchangeId);
                        onError?.Invoke("群聊执行结果保存失败，本轮作废");
                        yield break;
                    }
                if (!TurnStillCurrent(ct, turnEpoch))
                {
                    PersistAndRecoverCompletedRuns(exchangeId, runs);
                    EndInterjectionExchange(exchangeId);
                    onError?.Invoke("已中断");
                    yield break;
                }
                // First build every visible/hidden receipt line for this round, then checkpoint
                // the transcript once.  The member journals remain prepared until this single
                // parent commit succeeds, preserving crash safety while avoiding N full-history
                // JSON rewrites per round.
                var appended = new List<Line>();
                foreach (var r in runs)
                {
                    if (r == null || !r.Done || r.ParentDetached) continue;
                    if (!TurnStillCurrent(ct, turnEpoch))
                    {
                        PersistAndRecoverCompletedRuns(exchangeId, runs);
                        EndInterjectionExchange(exchangeId);
                        onError?.Invoke("已中断");
                        yield break;
                    }
                    string reply = string.IsNullOrWhiteSpace(r.Reply) ? null
                        : ReadableProseFormatter.EnsureParagraphs(MarkdownTmp.Normalize(r.Reply));
                    if ((string.IsNullOrWhiteSpace(reply) || IsSilent(reply)) && r.MustReply)
                    {
                        // Provider 偶尔仍会无视强制作答提示而返回“旁听”、省略号或空正文。
                        // 最后提交屏障必须兑现“人人回答”：使用不虚构行动/态度的中性角色句，
                        // 同时保留原错误到日志，不能再把这次已付费请求静默丢弃。
                        Debug.LogWarning("[JHYL_GROUP_MANDATORY_REPLY_FALLBACK] npc=" + r.Sp.Id
                            + " reason=" + (string.IsNullOrWhiteSpace(r.Error) ? "silent" : "member_error"));
                        reply = MandatoryReplyFallback();
                    }
                    r.DisplayReply = reply;
                    if (string.IsNullOrWhiteSpace(reply) || IsSilent(reply))
                    {
                        // 极少数成员可能先真实办事、最后选择旁听。写一条不可见索引行，把本轮
                        // memory/action receipt 仍挂进群文档，日后删轮/清空才能精确撤回其记忆。
                        if (r.Prepared || (r.MemoryIds != null && r.MemoryIds.Count > 0)
                            || (r.Actions != null && r.Actions.Count > 0)
                            || (r.ToolResults != null && r.ToolResults.Count > 0))
                        {
                            var hidden = NewLine(exchangeId, r.Sp.Id, r.Sp.Name, false, "",
                                roundDate, r.Orch.LastNpcLocationText, r.Orch.LastContactMode);
                            hidden.CommitAttemptId = r.AttemptId;
                            hidden.MemoryIds = r.MemoryIds; hidden.Actions = r.Actions;
                            hidden.ToolResults = r.ToolResults;
                            r.CommittedLine = hidden; appended.Add(hidden); _transcript.Add(hidden);
                        }
                        continue;
                    }
                    var replyLine = NewLine(exchangeId, r.Sp.Id, r.Sp.Name, false, reply,
                        roundDate, r.Orch.LastNpcLocationText, r.Orch.LastContactMode);
                    replyLine.CommitAttemptId = r.AttemptId;
                    replyLine.MemoryIds = r.MemoryIds;
                    replyLine.Actions = r.Actions;
                    replyLine.ToolResults = r.ToolResults;
                    r.CommittedLine = replyLine; appended.Add(replyLine); _transcript.Add(replyLine);
                }
                if (appended.Count > 0 && !SaveTranscript(
                        contentChanged: true, changedExchangeId: exchangeId))
                {
                    foreach (var line in appended) _transcript.Remove(line);
                    PersistAndRecoverCompletedRuns(exchangeId, runs);
                    EndInterjectionExchange(exchangeId);
                    onError?.Invoke("群聊记录保存失败，本轮作废");
                    yield break;
                }

                // —— 揭示:按既定顺序逐个显示(已并发生成并批量提交,这里只顺序揭示 + finalize)——
                int spoke = 0;
                int visibleResults = 0;
                var nextRoundMemoryTopic = new List<string>();
                MemberRun lastVisibleRun = runs.FindLast(r => r != null && r.Done
                    && !r.ParentDetached && !IsSilent(r.DisplayReply));
                foreach (var r in runs)
                {
                    if (r == null || !r.Done || r.ParentDetached) continue;
                    if (r.CommittedLine != null)
                    {
                        // 这一行已经包含在刚刚成功提交的父 transcript 中；只有越过该
                        // durable 边界后，子 Agent 的召回命中才有资格巩固。
                        r.Orch.CommitRecallStatistics(r.Sp.Id);
                    }
                    if (r.Prepared && r.CommittedLine != null)
                        FinalizePreparedMember(exchangeId, r.AttemptId, r.Sp.Id, roundInput,
                            roundDate);
                    ReleaseOwnedAttempt(r.AttemptId);
                    string reply = r.DisplayReply;
                    if (string.IsNullOrWhiteSpace(reply) || IsSilent(reply))
                    {
                        if (HasVisibleToolResults(r.ToolResults))
                        {
                            visibleResults++;
                            onResultOnly?.Invoke(DisplayMemberName(r), r.ToolResults);
                            // The callback renders the exact persisted ToolResults. Do not also
                            // flush action notices or the same green row could appear twice.
                            r.Orch.ClearDeferredNotices();
                            yield return null;
                        }
                        else r.Orch.ClearDeferredNotices();
                        continue;
                    }
                    spoke++;
                    nextRoundMemoryTopic.Add(r.Sp.Name + "：" + Trunc(reply, 360));
                    string displayName = DisplayMemberName(r);
                    onSpeakerStart?.Invoke(displayName);          // 名签 + 气泡
                    bool completingExchange = r == lastVisibleRun && !detachedWorker
                        && roundsSincePlayerMessage >= maxAutoRounds
                        && !HasPendingInterjections(exchangeId);
                    if (completingExchange)
                    {
                        // 最后一位仍显示生成气泡。封住插话后把整轮压缩为所有成员共用的个人
                        // 上下文；这里只压缩群聊，不额外提取长期记忆。压缩失败则完整保存原文。
                        CloseInterjectionWindow(exchangeId, true);
                        onProgress?.Invoke(displayName + " 正在生成中");
                        _contextProjectionInFlight.Add(exchangeId);
                        try
                        {
                            yield return ConsolidateNewExchangeMemory(exchangeId, ct, turnEpoch,
                                () => contextSaved = true);
                        }
                        finally { _contextProjectionInFlight.Remove(exchangeId); }
                        if (!TurnStillCurrent(ct, turnEpoch))
                        {
                            onSpeakerCancel?.Invoke();
                            EndInterjectionExchange(exchangeId);
                            onError?.Invoke("已中断；已保存的群聊记录保留");
                            yield break;
                        }
                    }
                    onSpeakerDone?.Invoke(displayName, reply, CloneLine(r.CommittedLine)); // 立即填成正文(已生成好)
                    r.Orch.FlushDeferredNoticesNow();           // 显示这位本轮落地动作的绿提示(各 flush 各的)
                    if (completingExchange && !contextSaved)
                    {
                        EndInterjectionExchange(exchangeId);
                        onError?.Invoke("群聊回复已保存，但个人上下文同步失败；本轮未完整完成，待后续恢复");
                        yield break;
                    }
                    yield return null;                           // 逐条揭示更自然
                }
                if (nextRoundMemoryTopic.Count > 0)
                {
                    activeMemoryRecallTopic = Trunc((roundMemoryRecallTopic ?? string.Empty)
                        + "\n【群内刚才的回应】\n" + string.Join("\n", nextRoundMemoryTopic.ToArray()), 1800);
                    // The first round's semantic selection remains an exchange-level cache.
                    // Later rounds locally re-rank the current store against this updated topic.
                }
                if (detachedWorker)
                {
                    // 不允许在仍有上一轮未知副作用的情况下进入下一群聊轮。已完成成员的
                    // 行与回执已经提交；晚到 worker 会在 finally 只补事务结果，不再发正文。
                    CloseInterjectionWindow(exchangeId, true);
                    ConsolidateMemoryOnly();
                    EndInterjectionExchange(exchangeId);
                    onError?.Invoke("部分群聊成员生成超时；已完成回复已保存，晚到执行结果会自动补入群聊记录");
                    yield break;
                }
                // 自然收场:本轮没人接话、且无待插话 → 收
                if (spoke == 0 && visibleResults == 0 && !HasPendingInterjections(exchangeId))
                {
                    string firstError = null;
                    foreach (MemberRun run in runs)
                        if (run != null && !string.IsNullOrWhiteSpace(run.Error)) { firstError = run.Error; break; }
                    Debug.LogWarning("[JHYL_GROUP_TURN_NO_VISIBLE_REPLY] round=" + totalRound
                        + " errors=" + runs.FindAll(x => x != null && !string.IsNullOrWhiteSpace(x.Error)).Count);
                    EndInterjectionExchange(exchangeId);
                    onError?.Invoke(string.IsNullOrWhiteSpace(firstError)
                        ? "群内成员本轮没有形成可显示回复，请重试"
                        : ("群聊成员生成失败：" + firstError));
                    yield break;
                }
            }
            // JHYL_GROUP_CHAT_CONSOLIDATE_AFTER_TURN: make group-chat memory available before monthly companion candidate checks.
            CloseInterjectionWindow(exchangeId, true);
            if (!TurnStillCurrent(ct, turnEpoch)) { EndInterjectionExchange(exchangeId); onError?.Invoke("已中断"); yield break; }
            // 只有最后一位的生成阶段已完成所有成员上下文落盘，才能通知 UI 本轮结束。
            if (!contextSaved)
            {
                EndInterjectionExchange(exchangeId);
                onError?.Invoke("群聊记录已保存，但个人上下文同步尚未完成");
                yield break;
            }
            EndInterjectionExchange(exchangeId);
            Debug.Log("[JHYL_GROUP_TURN_DONE] exchange=" + exchangeId + " rounds=" + totalRound);
            onTurnDone?.Invoke();
            }
            finally
            {
                EndInterjectionExchange(_currentExchangeId);
                ReleaseTurnPriority();
            }
        }

        private sealed class MemberRun
        {
            public Member Sp; public TalkOrchestrator Orch; public string Reply; public bool Done;
            public bool ContactModeKnown; public bool Remote;
            public long ProgressVersion;
            public string Error;
            public List<string> MemoryIds; public List<string> Actions; public List<string> ToolResults;
            public bool Prepared; public string AttemptId; public string DisplayReply; public Line CommittedLine;
            public string ExchangeId; public string PlayerInput; public int WorldDate; public bool MustReply;
            // ParentDetached means the parent will never commit this worker's response.  The
            // worker keeps its attempt lease until finally has captured and recovered receipts.
            public bool ParentDetached;

            public void Touch() => Interlocked.Increment(ref ProgressVersion);
        }

        private static string DisplayMemberName(MemberRun run)
        {
            string name = string.IsNullOrWhiteSpace(run?.Sp?.Name) ? "某人" : run.Sp.Name;
            return run != null && run.ContactModeKnown && run.Remote
                ? name + " · 千里传音" : name;
        }

        private static bool AllMemberRunsDone(IList<MemberRun> runs)
        {
            if (runs == null) return true;
            foreach (MemberRun run in runs) if (run != null && !run.Done) return false;
            return true;
        }

        /// <summary>
        /// The parent is aborting before it can commit reply rows. Only workers whose finally has
        /// completed may be recovered here; detached workers retain their attempt lease and recover
        /// themselves later. This is the cancellation counterpart of the normal parent commit.
        /// </summary>
        private void PersistAndRecoverCompletedRuns(string exchangeId, IList<MemberRun> runs)
        {
            var recover = new List<string>();
            if (runs != null)
                foreach (MemberRun run in runs)
                {
                    if (run == null || !run.Done || run.ParentDetached) continue;
                    if (!run.Prepared)
                    {
                        ReleaseOwnedAttempt(run.AttemptId);
                        continue;
                    }
                    if (_exchangeJournal != null && _exchangeJournal.UpdateToolResults(exchangeId,
                        run.AttemptId, run.Sp.Id, run.ToolResults))
                        recover.Add(run.AttemptId);
                    else
                    {
                        // Do not manufacture a receipt row from an older/incomplete journal payload.
                        // Release the in-memory lease so a later open can retry the durable entry.
                        Debug.LogWarning("[JHYL_GROUP_CANCEL_RECEIPT_SAVE_FAILED] exchange="
                            + exchangeId + " npc=" + run.Sp.Id);
                        ReleaseOwnedAttempt(run.AttemptId);
                    }
                }
            if (recover.Count > 0) RecoverOwnedAttempts(exchangeId, recover);
        }

        private void BeginInterjectionWindow(string exchangeId)
        {
            lock (_interjectionGate)
            {
                // Any entry from an interrupted/closed exchange is already represented by
                // its durable transcript line, but must never drive a later player turn.
                _pendingInterjections.Clear();
                _acceptingInterjectionExchangeId = exchangeId;
                _acceptingInterjections = !string.IsNullOrWhiteSpace(exchangeId);
            }
        }

        private void CloseInterjectionWindow(string exchangeId, bool discardQueued)
        {
            lock (_interjectionGate)
            {
                if (string.IsNullOrWhiteSpace(exchangeId)
                    || string.Equals(_acceptingInterjectionExchangeId, exchangeId, StringComparison.Ordinal))
                {
                    _acceptingInterjections = false;
                    _acceptingInterjectionExchangeId = null;
                }
                if (discardQueued) _pendingInterjections.Clear();
            }
        }

        private void EndInterjectionExchange(string exchangeId)
        {
            CloseInterjectionWindow(exchangeId, true);
            if (_currentExchangeId == exchangeId) _currentExchangeId = null;
        }

        private bool HasPendingInterjections(string exchangeId)
        {
            lock (_interjectionGate)
            {
                foreach (PendingInterjection pending in _pendingInterjections)
                    if (pending != null && string.Equals(pending.ExchangeId, exchangeId, StringComparison.Ordinal))
                        return true;
                return false;
            }
        }

        // 太吾中途插话已由 Interject 当场持久化；这里仅消费本 exchange 的“继续回应一轮”信号。
        private string DrainInterjections(string exchangeId)
        {
            var inputs = new List<string>();
            lock (_interjectionGate)
            {
                while (_pendingInterjections.Count > 0)
                {
                    PendingInterjection pending = _pendingInterjections.Dequeue();
                    if (pending != null && string.Equals(pending.ExchangeId, exchangeId, StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(pending.Text))
                        inputs.Add(pending.Text.Trim());
                }
            }
            return inputs.Count == 0 ? "" : string.Join("\n", inputs.ToArray());
        }

        private void MarkTaiwuTalkedToMembers()
        {
            int date = CurrentDate();
            foreach (var m in _members)
                if (m != null && m.Id > 0)
                    PlayerTalkMarkStore.Mark(_taiwuId, m.Id, date);
        }

        // 0..n-1 的乱序(Fisher–Yates),每轮重排在场成员的发言先后
        private List<int> ShuffledIndices(int n)
        {
            var list = new List<int>(n);
            for (int i = 0; i < n; i++) list.Add(i);
            for (int i = n - 1; i > 0; i--) { int j = _rng.Next(i + 1); var t = list[i]; list[i] = list[j]; list[j] = t; }
            return list;
        }

        private List<int> RoutedIndices(string playerInput, int round)
        {
            var all = ShuffledIndices(_members.Count);
            if (!string.IsNullOrWhiteSpace(playerInput))
                Debug.Log("[JHYL_GROUP_AGENT_ROUTE] all_members_reply fanout=" + all.Count + "/" + all.Count);
            return all;
        }

        private static string MandatoryReplyFallback()
            => "「我听见了。此事容我想清楚些，再同你们细说。」";

        // 识别模型违反群聊强制作答契约时返回的沉默占位；提交层会把它换成中性可见回应。
        private static bool IsSilent(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return true;
            string t = s.Trim().Trim('（', '(', '）', ')', '「', '」', '『', '』', '【', '】', '[', ']', '"', '“', '”', '*', '_', '~', '.', '。', '·', ' ', '\t', '　');
            if (t.Length == 0) return true;
            // 纯省略号/标点
            bool allDots = true; foreach (char c in t) if (c != '…' && c != '.' && c != '。' && c != '·' && c != ' ' && c != '、' && c != ',' && c != ',') { allDots = false; break; }
            if (allDots) return true;
            // 约定标记 + 常见"旁观/无话"旁白(去掉首尾省略号后再比,容『……旁听』『旁听……』)
            string core = t.Trim('…');
            string[] silentWords = { "旁听", "沉默", "沉默不语", "默然", "默然未语", "不语", "未语", "无话", "无话可说", "默默听着", "默默旁听", "静静听着", "静静旁观", "听着", "默默无言", "不语旁听", "无可奉告" };
            foreach (var w in silentWords) if (core == w) return true;
            return false;
        }

        // 单个成员(子-agent):用其【独立 orch】跑一遍完整 ProcessTurn——全套工具(可真赠物/传功/结义等)、各自长期记忆、落地照常,
        // 仅注入一段群聊框架提示。per-turn 落地/提示状态已实例化在该 orch 上,故可与别的成员【并发】跑而互不串台。回话写进 run。
        private IEnumerator RunMemberOrch(TalkOrchestrator orch, Member sp, string groupInput, bool mustReply, MemberRun run)
        {
            if (mustReply) groupInput += "\n(太吾正等着回应——你这一句务必接住、把话接下去,不可回『旁听』或沉默跳过。)";
            string captured = null;
            run.Touch();
            Debug.Log("[JHYL_GROUP_MEMBER_START] npc=" + sp.Id + " must_reply=" + mustReply);
            // try/finally:无论 ProcessTurn 正常结束、报错、还是抛异常被 Unity 释放,都置 run.Done(配合上面的超时,双保险绝不挂死)。
            try
            {
                yield return orch.ProcessTurn(sp.Id, groupInput,
                    reply => { run.Touch(); captured = reply; },
                    err => { run.Touch(); captured = null; run.Error = string.IsNullOrWhiteSpace(err) ? "未知错误" : err; },
                    onDelta: _ => run.Touch(),
                    onProgress: _ => run.Touch(),
                    onTokens: (_, __) => run.Touch(),
                    onThinking: _ => run.Touch(),
                    onResetReply: () => run.Touch(),
                    onContactMode: remote =>
                    {
                        run.Remote = remote;
                        run.ContactModeKnown = true;
                        run.Touch();
                    });
            }
            finally
            {
                try { orch?.GroupCtx?.ActionCoordinator?.CompleteMember(orch.GroupCtx.DispatchOrder); } catch { }
                try
                {
                    List<string> memoryIds = null, actions = null, toolResults = null;
                    if (orch != null) orch.CaptureCurrentTurnState(out memoryIds, out actions, out toolResults);
                    if (memoryIds != null) run.MemoryIds = memoryIds;
                    if (actions != null) run.Actions = actions;
                    if (toolResults != null) run.ToolResults = toolResults;
                }
                catch
                {
                    if (orch?.LastTurnMemoryIds != null) run.MemoryIds = new List<string>(orch.LastTurnMemoryIds);
                    if (orch?.LastTurnActions != null) run.Actions = new List<string>(orch.LastTurnActions);
                    if (orch?.LastTurnToolResults != null) run.ToolResults = new List<string>(orch.LastTurnToolResults);
                }
                run.Reply = captured;
                run.Touch();
                if (run.ParentDetached) RecoverDetachedMember(run);
                _activeRuns.Remove(orch);
                _activeMemberRuns.Remove(run);
                // Done is the final publication barrier: the parent may only read run fields after
                // snapshots and any detached recovery above have fully completed.
                run.Done = true;
                Debug.Log("[JHYL_GROUP_MEMBER_DONE] npc=" + sp.Id
                    + " reply_chars=" + (captured == null ? 0 : captured.Length)
                    + " error=" + (string.IsNullOrWhiteSpace(run.Error) ? "none" : "yes"));
            }
        }

        private void RecoverDetachedMember(MemberRun run)
        {
            if (run == null) return;
            try
            {
                if (run.Prepared)
                {
                    bool receiptSaved = _exchangeJournal != null
                        && _exchangeJournal.UpdateToolResults(run.ExchangeId, run.AttemptId,
                            run.Sp.Id, run.ToolResults);
                    if (receiptSaved)
                    {
                        RecoverOwnedAttempts(run.ExchangeId, new[] { run.AttemptId });
                        // A terminal recovered action creates a hidden result row. Refresh the
                        // group-memory projection now instead of waiting for the next window open.
                        RefreshMemoryForExchanges(new[] { run.ExchangeId });
                    }
                    else
                    {
                        Debug.LogWarning("[JHYL_GROUP_DETACHED_RECEIPT_SAVE_FAILED] exchange="
                            + run.ExchangeId + " npc=" + run.Sp.Id);
                        ReleaseOwnedAttempt(run.AttemptId);
                    }
                }
                else ReleaseOwnedAttempt(run.AttemptId);
            }
            catch (Exception ex)
            {
                ReleaseOwnedAttempt(run.AttemptId);
                Debug.LogWarning("[JHYL_GROUP_DETACHED_RECOVERY_FAILED] exchange="
                    + run.ExchangeId + " npc=" + (run.Sp?.Id ?? 0) + " error=" + ex.GetType().Name);
            }
        }

        // 给该成员看的对话上下文:取本群【近 RenderLines 行】。必须包含他自己先前说过的话，
        // 否则多轮群聊里角色会忘记自己刚说的承诺/问题而复读或自相矛盾。
        // 并发按轮制下,同一轮各成员看到的是"本轮开始时的对话快照";跨轮才看得到上一轮各人新说的。
        private string BuildGroupInputFor(Member sp)
        {
            var visible = VisibleLines(_transcript);
            int total = visible.Count;
            int from = System.Math.Max(0, total - RenderLines);
            var sb = new StringBuilder();
            for (int i = from; i < total; i++)
            {
                var l = visible[i];
                string turnContext = TalkPromptBuilder.RenderHistoricalTurnContext(
                    l.Date, l.LocationText, l.ContactMode);
                if (!string.IsNullOrEmpty(turnContext)) sb.Append(turnContext).Append('\n');
                sb.Append(l.IsTaiwu ? "太吾" : ("「" + l.Speaker + "」")).Append(":").Append(l.Text);
                if (!l.IsTaiwu)
                    sb.Append(TalkPromptBuilder.RenderExecutionHistorySuffix(l.Actions, l.ToolResults));
                sb.Append('\n');
            }
            string s = sb.ToString().Trim();
            return string.IsNullOrEmpty(s) ? "(众人一时无话,你也可起个话头)" : s;
        }

        /// <summary>"代笔":据群聊上下文替太吾拟一句要对众人说的话。纯文本建议,不触发任何发言。</summary>
        public IEnumerator SuggestLine(Action<string> onResult, Action<string> onError,
            System.Threading.CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested || !WorldLifecycle.IsSameWorld(_worldGeneration)) yield break;
            var client = LlmService.GetClient();
            if (client == null) { onError?.Invoke("未配置接口"); yield break; }
            if (_members.Count == 0) { onError?.Invoke("群里没有人"); yield break; }
            var sb = new StringBuilder();
            sb.Append("你在替玩家『太吾』构思下一句要发到共享群聊频道的话。频道参与者:").Append(string.Join("、", _members.ConvertAll(m => m.Name))).Append("。参与频道不等于同处一地。\n");
            sb.Append(RenderTranscript());
            int ghostwriteLength = GhostwriteLengthStore.Load();
            int ghostwriteMaxChars = GhostwriteLengthStore.MaxChars(ghostwriteLength);
            var localSpeech = new List<string>();
            for (int i = _transcript.Count - 1; i >= 0 && localSpeech.Count < 12; i--)
            {
                Line line = _transcript[i];
                if (line != null && line.IsTaiwu && !string.IsNullOrWhiteSpace(line.Text))
                    localSpeech.Add(line.Text);
            }
            GhostwriteImitationProfile learning = GhostwriteLearningContext.Build(_taiwuId, localSpeech);
            sb.Append("\n替太吾拟一段此刻自然会说的话(可向大家发问、回应某人、或起个话头),像真江湖客口吻。")
                .Append(GhostwriteLengthStore.PromptDirective(ghostwriteLength))
                .Append("。只输出这段发言本身,不要引号、不要旁白、不要写「太吾:」。");
            if (learning != null) sb.Append(learning.PromptDirective(ghostwriteMaxChars));
            if (!string.IsNullOrWhiteSpace(TalkOrchestrator.TaiwuVoice)) sb.Append("\n太吾平日说话的口吻:").Append(TalkOrchestrator.TaiwuVoice.Trim()).Append("。务必照此口吻遣词。");
            string system = "你是玩家的群聊参谋,只产出玩家这一句要说的话。";
            if (learning?.SampleCount > 0)
                system += "\n" + GhostwriteImitationProfileBuilder.HistoricalDataBoundary;
            var msgs = new List<LlmMessage> { LlmMessage.System(system) };
            GhostwriteLearningContext.AddHistoricalSamples(msgs, learning);
            msgs.Add(new LlmMessage("user", sb.ToString()));
            // 8K 同时容纳 thinking-only 模型推理并限制一句代笔的异常输出。
            var task = client.SendAsync(msgs, 8192, 0.8, cancellationToken, 45, false, "代笔·群聊",
                LlmReasoningPolicy.Off);
            yield return new WaitUntil(() => task.IsCompleted || cancellationToken.IsCancellationRequested
                || !WorldLifecycle.IsSameWorld(_worldGeneration));
            if (cancellationToken.IsCancellationRequested || !WorldLifecycle.IsSameWorld(_worldGeneration)) yield break;
            LlmResult r = null;
            try { r = task.Result; } catch (Exception e) { onError?.Invoke(e.GetType().Name); yield break; }
            if (r == null || !r.Ok || string.IsNullOrWhiteSpace(r.Content)) { onError?.Invoke(r?.Error ?? "生成失败"); yield break; }
            onResult?.Invoke(TalkOrchestrator.CleanSuggestion(r.Content, ghostwriteMaxChars));
        }

        /// <summary>关窗收尾：恢复未完成的群聊上下文压缩，并按有效长期记忆更新各自画像。</summary>
        public IEnumerator ConsolidateMembers()
        {
            if (!_transcriptReliable) yield break;
            ConsolidateMemoryOnly();   // 正常新轮已在最后一位生成完成前写入个人聊天上下文
            // ② 折进各自画像(画像蒸馏=LLM、较贵)——只在【真正关群窗】时做(复用单聊固化;Snap 在 Open 时已拉)
            foreach (var m in _members)
                if (m.Snap != null && m.Snap.TaiwuId > 0)
                    yield return PortraitService.Consolidate(m.Snap);
        }

        /// <summary>恢复/中断兜底：在后台重试尚未完成的群聊上下文压缩。</summary>
        public bool ConsolidateMemoryOnly(string pendingExchangeId = null,
            int completedAttempts = 0)
        {
            if (!_transcriptReliable || !WorldLifecycle.IsSameWorld(_worldGeneration)
                || !HasCurrentFileEpoch()) return false;
            // 活跃回合由最后一位同步收尾；切页/关窗不能抢先处理同一份个人上下文。
            if (!string.IsNullOrWhiteSpace(_currentExchangeId)
                && _cts != null && !_cts.IsCancellationRequested) return true;
            if (!TryLoadPendingMemoryProjection(out _)) return false;
            if (!_memoryProjectionPending) return true;
            var exchanges = ExchangesInOrder();
            string target = !string.IsNullOrWhiteSpace(pendingExchangeId)
                ? pendingExchangeId : _currentExchangeId;
            if (string.IsNullOrWhiteSpace(target)
                || !_pendingContextProjectionExchangeIds.Contains(target))
            {
                target = null;
                foreach (KeyValuePair<string, List<Line>> exchange in exchanges)
                    if (_pendingContextProjectionExchangeIds.Contains(exchange.Key))
                    { target = exchange.Key; break; }
            }
            if (string.IsNullOrWhiteSpace(target)) return false;
            if (_contextProjectionInFlight.Contains(target)) return true;
            MonoBehaviour host = TalkEntryHost.Instance;
            if (host == null) host = ConfigHost.Instance;
            if (host == null) return false;
            _contextProjectionInFlight.Add(target);
            try
            {
                host.StartCoroutine(ConsolidateDetachedGroupContext(target, completedAttempts));
                return true;
            }
            catch
            {
                _contextProjectionInFlight.Remove(target);
                return false;
            }
        }

        private IEnumerator ConsolidateDetachedGroupContext(string exchangeId,
            int completedAttempts)
        {
            try
            {
                yield return ConsolidateNewExchangeMemory(exchangeId,
                    CancellationToken.None, -1);
            }
            finally { _contextProjectionInFlight.Remove(exchangeId); }

            int attempts = completedAttempts + 1;
            if (!WorldLifecycle.IsSameWorld(_worldGeneration) || !HasCurrentFileEpoch()
                || !TryLoadPendingMemoryProjection(out _) || !_memoryProjectionPending)
                yield break;
            bool sameStillPending = _pendingContextProjectionExchangeIds.Contains(exchangeId);
            if (sameStillPending
                && !GroupMemoryProjectionPolicy.ShouldRetryStartupRecovery(true, attempts))
                yield break;
            if (sameStillPending)
                yield return new WaitForSecondsRealtime(
                    GroupMemoryProjectionPolicy.StartupRecoveryDelaySeconds(attempts));
            if (WorldLifecycle.IsSameWorld(_worldGeneration) && HasCurrentFileEpoch())
                ConsolidateMemoryOnly(sameStillPending ? exchangeId : null,
                    sameStillPending ? attempts : 0);
        }

        private IEnumerator ConsolidateNewExchangeMemory(string exchangeId, CancellationToken ct,
            long turnEpoch, Action onCommitted = null)
        {
            bool detached = turnEpoch < 0;
            if (string.IsNullOrWhiteSpace(exchangeId) || !_transcriptReliable
                || _members.Count == 0 || ct.IsCancellationRequested
                || !WorldLifecycle.IsSameWorld(_worldGeneration)
                || !detached && !TurnStillCurrent(ct, turnEpoch)
                || !HasCurrentFileEpoch()) yield break;
            if (!TryLoadPendingMemoryProjection(out long capturedRevision)
                || !_pendingContextProjectionExchangeIds.Contains(exchangeId)) yield break;
            if (_pendingMemoryRemovalExchangeIds.Count > 0
                || _pendingLinkedMemoryRemovals.Count > 0) yield break;

            List<Line> exchangeLines = null;
            foreach (KeyValuePair<string, List<Line>> pair in ExchangesInOrder())
                if (string.Equals(pair.Key, exchangeId, StringComparison.Ordinal))
                { exchangeLines = VisibleLines(pair.Value); break; }
            if (exchangeLines == null || exchangeLines.Count == 0) yield break;

            string compressedContext = null;
            bool usedOriginalFallback = true;
            OpenAiCompatibleClient client = LlmService.GetBackgroundClient();
            if (client != null)
            {
                var sources = new List<GroupContextCompressor.SourceLine>();
                foreach (Line line in exchangeLines)
                {
                    if (line == null || string.IsNullOrWhiteSpace(line.Id)) continue;
                    IList<string> results = line.ToolResults != null && line.ToolResults.Count > 0
                        ? (IList<string>)line.ToolResults : line.Actions;
                    sources.Add(new GroupContextCompressor.SourceLine
                    {
                        Id = line.Id,
                        SpeakerId = line.SpeakerId,
                        SpeakerName = line.IsTaiwu ? "太吾" : line.Speaker,
                        IsTaiwu = line.IsTaiwu,
                        Text = line.Text,
                        ToolResults = results,
                    });
                }
                if (sources.Count > 0)
                {
                    var task = client.SendAsync(GroupContextCompressor.BuildMessages(sources),
                        1024, 0.2, ct, 120, false, "群聊上下文压缩", LlmReasoningPolicy.Off);
                    while (!task.IsCompleted)
                    {
                        if (ct.IsCancellationRequested || !WorldLifecycle.IsSameWorld(_worldGeneration)
                            || !detached && !TurnStillCurrent(ct, turnEpoch)) yield break;
                        yield return null;
                    }
                    if (ct.IsCancellationRequested || !WorldLifecycle.IsSameWorld(_worldGeneration)
                        || !detached && !TurnStillCurrent(ct, turnEpoch)) yield break;
                    try
                    {
                        LlmResult response = task.Result;
                        usedOriginalFallback = response == null || !response.Ok
                            || !GroupContextCompressor.TryAcceptSummary(
                                response.Content, out compressedContext);
                    }
                    catch { usedOriginalFallback = true; }
                }
            }

            long capturedFileEpoch = _fileEpoch;
            bool allSaved = true;
            var affected = new[] { exchangeId };
            foreach (Member member in _members)
            {
                TalkTurn projected = BuildGroupChatHistoryTurn(member, exchangeId, exchangeLines,
                    compressedContext, usedOriginalFallback);
                if (projected == null || !FileEpochStillCurrent(capturedFileEpoch)
                    || !TalkOrchestrator.SynchronizeGroupChatTranscripts(_taiwuId, member.Id,
                        member.Name, _key, affected, new[] { projected }))
                    allSaved = false;
            }
            if (!allSaved || !TryCommitMemoryProjection(exchangeId,
                    capturedRevision, capturedFileEpoch))
            {
                Debug.LogWarning("[JHYL_GROUP_CONTEXT_COMPRESS] commit_pending group=" + _key
                    + " exchange=" + exchangeId);
                yield break;
            }
            Debug.Log("[JHYL_GROUP_CONTEXT_COMPRESS] success group=" + _key
                + " exchange=" + exchangeId + " members=" + _members.Count
                + " fallback_original=" + usedOriginalFallback);
            onCommitted?.Invoke();
            yield break;
        }
        private bool TryLoadPendingMemoryProjection(out long capturedRevision)
        {
            capturedRevision = 0;
            string path = ConvPath();
            lock (GetFileLock(path))
            {
                if (_fileEpoch != CurrentFileEpoch(path)) return false;
                bool reliable;
                GroupDocument disk = ReadDocumentRecoverable(path, preserveCorrupt: true,
                    JianghuYoulingPaths.CurrentWorldId, _taiwuId, _key, ExpectedMemberIds(), out reliable);
                if (!reliable)
                {
                    _transcriptReliable = false;
                    return false;
                }
                if (disk == null)
                {
                    // A never-used newly opened group legitimately has no document and no
                    // projection obligation. Non-empty in-memory state without a parent
                    // document is ambiguous and remains fail-closed.
                    if (_transcript.Count == 0 && !_memoryProjectionPending
                        && _pendingMemoryRemovalExchangeIds.Count == 0
                        && _pendingLinkedMemoryRemovals.Count == 0) return true;
                    _transcriptReliable = false;
                    return false;
                }
                _transcript = disk.Lines ?? new List<Line>();
                _documentRevision = disk.Revision;
                SyncArchivedSegmentCount(disk.ArchivedSegmentCount);
                LoadPendingContextProjections(disk);
                MergePendingMemoryRefresh(disk);
                MergePendingLinkedMemoryRemovals(disk, path);
                NormalizeTranscript();
                capturedRevision = disk.Revision;
                return true;
            }
        }

        private bool TryCommitMemoryProjection(string exchangeId, long capturedRevision,
            long capturedFileEpoch)
        {
            if (string.IsNullOrWhiteSpace(exchangeId)
                || !FileEpochStillCurrent(capturedFileEpoch)) return false;
            string path = ConvPath();
            try
            {
                lock (GetFileLock(path))
                {
                    if (_fileEpoch != CurrentFileEpoch(path)) return false;
                    bool reliable;
                    GroupDocument disk = ReadDocumentRecoverable(path, preserveCorrupt: true,
                        JianghuYoulingPaths.CurrentWorldId, _taiwuId, _key, ExpectedMemberIds(), out reliable);
                    if (!reliable || disk == null) { _transcriptReliable = false; return false; }
                    if (!PendingContextProjectionIds(disk).Contains(exchangeId))
                    {
                        LoadPendingContextProjections(disk);
                        _documentRevision = disk.Revision;
                        SyncArchivedSegmentCount(disk.ArchivedSegmentCount);
                        return true;
                    }
                    bool cleanupPending = disk.PendingMemoryRefreshExchangeIds == null
                        || disk.PendingMemoryRefreshExchangeIds.Count > 0
                        || disk.PendingLinkedMemoryRemovals == null
                        || disk.PendingLinkedMemoryRemovals.Count > 0;
                    if (!GroupMemoryProjectionPolicy.CanCommit(
                        durableProjectionPending: true,
                        allMemberStoresSaved: true,
                        cleanupTransactionPending: cleanupPending,
                        capturedTranscriptRevision: capturedRevision,
                        authoritativeTranscriptRevision: disk.Revision)) return false;

                    disk.Revision = checked(disk.Revision + 1);
                    disk.PendingContextProjectionExchangeIds.RemoveAll(id =>
                        string.Equals(id, exchangeId, StringComparison.Ordinal));
                    disk.MemoryProjectionPending = disk.PendingContextProjectionExchangeIds.Count > 0;
                    WriteDocumentAtomic(path, disk, ".tmp");
                    _transcript = disk.Lines ?? new List<Line>();
                    _documentRevision = disk.Revision;
                    SyncArchivedSegmentCount(disk.ArchivedSegmentCount);
                    LoadPendingContextProjections(disk);
                    // 屏障刚清空:被封存轮次的个人群聊上下文已 durable 确认,此刻归档不改变对账语义。
                    MaybeArchiveOldExchanges();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[江湖有灵] 群聊上下文投影提交屏障写入失败:" + ex.GetType().Name);
                return false;
            }
        }

        private TalkTurn BuildGroupChatHistoryTurn(Member self, string exchangeId,
            List<Line> lines, string compressedContext, bool useOriginalFallback)
        {
            List<Line> visible = VisibleLines(lines);
            if (self == null || self.Id <= 0 || string.IsNullOrWhiteSpace(exchangeId)
                || visible == null || visible.Count == 0) return null;
            var participants = new List<string> { "太吾" };
            foreach (Member member in _members)
                if (member != null && !string.IsNullOrWhiteSpace(member.Name)
                    && !participants.Contains(member.Name.Trim()))
                    participants.Add(member.Name.Trim());
            int latestDate = 0;
            foreach (Line line in visible)
                if (line != null && line.Date > latestDate) latestDate = line.Date;
            string context;
            if (!useOriginalFallback && !string.IsNullOrWhiteSpace(compressedContext))
            {
                context = "【群聊摘要｜参与者：" + string.Join("、", participants.ToArray())
                    + "】\n（这是多人群聊的共同摘要，不是太吾与你的私聊。）\n"
                    + compressedContext.Trim();
            }
            else
                context = BuildOriginalTranscript(participants, visible);
            string id = TalkOrchestrator.GroupChatProjectionTurnId(
                _taiwuId, self.Id, _key, exchangeId);
            return new TalkTurn
            {
                Id = id,
                ExchangeId = id,
                FromPlayer = false,
                Text = context,
                Date = latestDate,
                ContactMode = "群聊",
                Kind = TalkTurnKinds.GroupChatTranscript,
            };
        }

        private static string BuildOriginalTranscript(IList<string> participants, IList<Line> lines)
        {
            var transcript = new StringBuilder();
            transcript.Append("【群聊实录（压缩失败，保留完整原文）｜参与者：")
                .Append(string.Join("、", participants == null
                    ? Array.Empty<string>() : new List<string>(participants).ToArray()))
                .Append("】\n（以下是多人群聊原文，不是太吾与你的私聊。）\n");
            foreach (Line line in lines ?? Array.Empty<Line>())
            {
                if (line == null) continue;
                string speaker = line.IsTaiwu ? "太吾" :
                    (string.IsNullOrWhiteSpace(line.Speaker)
                        ? ("NPC#" + line.SpeakerId) : line.Speaker.Trim());
                transcript.Append(speaker).Append('：');
                if (!string.IsNullOrWhiteSpace(line.Text)) transcript.Append(line.Text.Trim());
                if (!line.IsTaiwu)
                    transcript.Append(TalkPromptBuilder.RenderExecutionHistorySuffix(
                        line.Actions, line.ToolResults));
                transcript.Append('\n');
            }
            return transcript.ToString().Trim();
        }
        private List<KeyValuePair<string, List<Line>>> ExchangesInOrder() => ExchangesInOrderOf(_transcript);

        private static List<KeyValuePair<string, List<Line>>> ExchangesInOrderOf(IEnumerable<Line> lines)
        {
            var result = new List<KeyValuePair<string, List<Line>>>();
            var map = new Dictionary<string, List<Line>>();
            foreach (var line in lines ?? new List<Line>())
            {
                if (line == null) continue;
                string id = string.IsNullOrWhiteSpace(line.ExchangeId) ? ("legacy-" + (line.Id ?? "0")) : line.ExchangeId;
                if (!map.TryGetValue(id, out var list))
                {
                    list = new List<Line>(); map[id] = list;
                    result.Add(new KeyValuePair<string, List<Line>>(id, list));
                }
                list.Add(line);
            }
            return result;
        }

        private string MemorySourceId(int memberId, string exchangeId)
            // 来源身份继续采用无碰撞的规范成员 ID 串，与文件名哈希解耦；这样升级文件键时
            // 已写入的 v2 来源不会变成一份重复记忆。最多 8 人使该串长度有明确上限。
            => "group:" + PreviousGroupKey(_taiwuId, _members) + ":" + exchangeId + ":" + memberId;

        private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? s : (s.Length <= n ? s : s.Substring(0, n) + "…");

        private string RenderTranscript()
        {
            var visible = VisibleLines(_transcript);
            int start = Math.Max(0, visible.Count - RenderLines);
            if (start >= visible.Count) return "(尚无人开口)\n";
            var sb = new StringBuilder("最近对话(由旧到新):\n");
            for (int i = start; i < visible.Count; i++)
            {
                var l = visible[i];
                string turnContext = TalkPromptBuilder.RenderHistoricalTurnContext(
                    l.Date, l.LocationText, l.ContactMode);
                if (!string.IsNullOrEmpty(turnContext)) sb.Append(turnContext).Append('\n');
                sb.Append(l.IsTaiwu ? "太吾" : ("「" + l.Speaker + "」")).Append(":").Append(l.Text);
                if (!l.IsTaiwu)
                    sb.Append(TalkPromptBuilder.RenderExecutionHistorySuffix(l.Actions, l.ToolResults));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static List<Line> VisibleLines(IEnumerable<Line> source)
        {
            var visible = new List<Line>();
            if (source == null) return visible;
            foreach (Line line in source)
                if (HasVisibleContent(line)) visible.Add(line);
            return visible;
        }

        private static bool HasVisibleContent(Line line)
            => line != null && (!string.IsNullOrWhiteSpace(line.Text)
                || HasVisibleToolResults(line.ToolResults));

        private static bool HasVisibleToolResults(IEnumerable<string> toolResults)
        {
            if (toolResults == null) return false;
            foreach (string result in toolResults)
                if (!string.IsNullOrWhiteSpace(result)) return true;
            return false;
        }

        // —— 持久化:按"太吾 + 这支小队成员集合"键存盘,重开同一支队即载入此前对话 ——
        private static string GroupKey(int taiwuId, List<Member> members)
        {
            var ids = new List<int>();
            foreach (var m in members) ids.Add(m.Id);
            ids.Sort();
            string canonical = taiwuId + "|" + string.Join(",", ids);
            byte[] digest;
            using (var sha = SHA256.Create()) digest = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
            var hex = new StringBuilder(digest.Length * 2);
            foreach (byte b in digest) hex.Append(b.ToString("x2"));
            return taiwuId + "_" + hex;
        }

        private List<int> DirectActionTargetIndices(IList<int> targetIds)
        {
            var result = new List<int>();
            if (targetIds == null || targetIds.Count == 0) return result;
            var seen = new HashSet<int>();
            foreach (int targetId in targetIds)
                for (int index = 0; index < _members.Count; index++)
                    if (_members[index] != null && _members[index].Id == targetId
                        && seen.Add(index))
                    {
                        result.Add(index);
                        break;
                    }
            return result;
        }

        private static bool IsSafePersistedGroupId(string groupId, int taiwuId)
        {
            if (string.IsNullOrWhiteSpace(groupId)) return false;
            string value = groupId.Trim();
            if (value.Length > 128
                || !value.StartsWith(taiwuId + "_", StringComparison.Ordinal)) return false;
            foreach (char c in value)
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z')
                    && !(c >= '0' && c <= '9') && c != '_' && c != '-')
                    return false;
            return true;
        }

        // v0.28/v0.29 开发版曾直接把全部成员 ID 拼进文件名；保留读取迁移入口。
        private static string PreviousGroupKey(int taiwuId, List<Member> members)
        {
            var ids = new List<int>();
            foreach (var m in members) ids.Add(m.Id);
            ids.Sort();
            return taiwuId + "_" + string.Join("-", ids);
        }

        private static string LegacyGroupKey(int taiwuId, List<Member> members)
        {
            var ids = new List<int>(); foreach (var m in members) ids.Add(m.Id); ids.Sort();
            int h = 17; foreach (var id in ids) h = h * 31 + id;
            return taiwuId + "_" + (h & 0x7fffffff).ToString("x");
        }

        private string ConvPath() => Path.Combine(JianghuYoulingPaths.ChatLogs, "Group_" + _key + ".json");

        private bool PrepareGroupExchange(string exchangeId, string attemptId, int npcId, string playerInput, int worldDate,
            IEnumerable<string> memoryIds, IEnumerable<string> actions, IEnumerable<string> operationIds)
        {
            if (_exchangeJournal == null || _journalWorldId == 0
                || string.IsNullOrWhiteSpace(attemptId) || !_ownedAttemptIds.Contains(attemptId)
                || JianghuYoulingPaths.CurrentWorldId != _journalWorldId
                || !WorldLifecycle.IsSameWorld(_worldGeneration)
                || (_cts != null && _cts.IsCancellationRequested)
                || !HasCurrentFileEpoch() || !HasCurrentGroupEpoch()) return false;
            bool saved = _exchangeJournal.Prepare(exchangeId, attemptId, npcId, playerInput, worldDate,
                memoryIds, actions, operationIds);
            if (!saved) Debug.LogWarning("[江湖有灵] 群聊 exchange prepared 写盘失败 exchange=" + exchangeId + " npc=" + npcId);
            return saved;
        }

        private void FinalizePreparedMember(string exchangeId, string attemptId, int npcId, string playerInput, int worldDate)
        {
            if (_exchangeJournal == null || JianghuYoulingPaths.CurrentWorldId != _journalWorldId) return;
            if (!TalkOrchestrator.FinalizeRecoveredGroupCommit(_taiwuId, npcId,
                _key, exchangeId, attemptId, playerInput, worldDate))
            {
                Debug.LogWarning("[江湖有灵] 群 transcript 已提交，成员动作日志待重启补收尾 exchange=" + exchangeId + " npc=" + npcId);
                return;
            }
            if (!_exchangeJournal.Complete(exchangeId, attemptId, npcId))
                Debug.LogWarning("[江湖有灵] 群 exchange 完成标记写盘失败，将在重启后幂等补做 exchange=" + exchangeId + " npc=" + npcId);
        }

        private void ReplayPreparedGroupExchanges() => RecoverPreparedExchange(null, null, true);

        private void RecoverOwnedAttempts(string onlyExchangeId)
        {
            var owned = new HashSet<string>(_ownedAttemptIds, StringComparer.Ordinal);
            try { RecoverPreparedExchange(onlyExchangeId, owned, false); }
            finally { foreach (string attemptId in owned) ReleaseOwnedAttempt(attemptId); }
        }

        private void RecoverOwnedAttempts(string onlyExchangeId, IEnumerable<string> attemptIds)
        {
            var owned = new HashSet<string>(StringComparer.Ordinal);
            if (attemptIds != null)
                foreach (string attemptId in attemptIds)
                    if (!string.IsNullOrWhiteSpace(attemptId) && _ownedAttemptIds.Contains(attemptId))
                        owned.Add(attemptId);
            if (owned.Count == 0) return;
            try { RecoverPreparedExchange(onlyExchangeId, owned, false); }
            finally { foreach (string attemptId in owned) ReleaseOwnedAttempt(attemptId); }
        }

        private void RecoverPreparedExchange(string onlyExchangeId, ISet<string> onlyAttemptIds, bool skipActiveAttempts)
        {
            if (_exchangeJournal == null || _journalWorldId == 0
                || JianghuYoulingPaths.CurrentWorldId != _journalWorldId
                || !WorldLifecycle.IsSameWorld(_worldGeneration)) return;
            var entries = _exchangeJournal.Snapshot();
            if (entries == null)
            {
                Debug.LogWarning("[江湖有灵] 群 exchange journal 不可可靠读取，暂停恢复");
                return;
            }
            GroupDocument disk;
            string transcriptPath = ConvPath();
            lock (GetFileLock(transcriptPath))
                disk = ReadDocumentRecoverable(transcriptPath, preserveCorrupt: true,
                    _journalWorldId, _taiwuId, _key, ExpectedMemberIds());
            // 一个 prepared 必然晚于本 exchange 的太吾行，而太吾行在启动成员 agent 前已提交。
            // 因此此处读不到群文档不是“空历史”，而是无法判断主写是否已提交；必须保留 journal，
            // 绝不能猜成未提交后误删其实已经被 transcript 引用的记忆。
            if (disk == null)
            {
                Debug.LogWarning("[江湖有灵] 群 transcript 不可可靠读取，prepared exchange 保留待恢复");
                return;
            }
            // 已提交的成员行可能已被封存进归档段;恢复判定必须把段也纳入搜索范围,
            // 否则会把已提交轮误判为"prepared 但无成员行",误删其成员记忆或重复补写回执行。
            List<Line> archivedForRecovery = null;
            bool archiveFullyReadable = true;
            if (disk.ArchivedSegmentCount > 0)
            {
                archivedForRecovery = new List<Line>();
                for (int index = 1; index <= disk.ArchivedSegmentCount; index++)
                {
                    GroupDocument segment = ReadArchiveSegment(index);
                    if (segment?.Lines == null) { archiveFullyReadable = false; continue; }
                    archivedForRecovery.AddRange(segment.Lines);
                }
            }
            foreach (var entry in entries)
            {
                if (entry == null || (!string.IsNullOrWhiteSpace(onlyExchangeId)
                    && !string.Equals(entry.ExchangeId, onlyExchangeId, StringComparison.Ordinal))) continue;
                if (onlyAttemptIds != null && (string.IsNullOrWhiteSpace(entry.AttemptId)
                    || !onlyAttemptIds.Contains(entry.AttemptId))) continue;
                if (entry.State == GroupExchangeJournal.CleanupCommitted)
                {
                    RecoverCommittedCleanupEntry(entry);
                    continue;
                }
                if (skipActiveAttempts && !string.IsNullOrWhiteSpace(entry.AttemptId)
                    && IsAttemptActive(_journalPath, entry.AttemptId)) continue;
                Line committedLine = null;
                foreach (var line in _transcript)
                    if (LineMatchesEntry(line, entry))
                    { committedLine = line; break; }
                if (committedLine == null && disk.Lines != null)
                    foreach (var line in disk.Lines)
                        if (LineMatchesEntry(line, entry))
                        { committedLine = line; break; }
                bool archivedHit = false;
                if (committedLine == null && archivedForRecovery != null)
                    foreach (var line in archivedForRecovery)
                        if (LineMatchesEntry(line, entry))
                        { committedLine = line; archivedHit = true; break; }
                if (archivedHit)
                {
                    // 段内命中即已提交:段不可变,不做活跃文档修复,更不能把段行克隆回活跃
                    // 文档(会造成跨段/活跃重复)。失败的清空同样以"行仍在"证明未落地,先复原
                    // prepared;事务负载与 journal 一致才可收尾,不一致保留 journal 待人工核对。
                    if ((entry.State == GroupExchangeJournal.CleanupIntent
                            || entry.State == GroupExchangeJournal.CleanupPending)
                        && !_exchangeJournal.RestorePrepared(entry.ExchangeId, entry.AttemptId, entry.NpcId))
                        continue;
                    if (committedLine.Date == entry.WorldDate && SameStrings(committedLine.MemoryIds, entry.MemoryIds)
                        && SameStrings(committedLine.Actions, entry.Actions)
                        && SameStrings(committedLine.ToolResults, entry.ToolResults))
                        FinalizePreparedMember(entry.ExchangeId, entry.AttemptId, entry.NpcId,
                            entry.PlayerInput, entry.WorldDate);
                    else
                        Debug.LogWarning("[江湖有灵] 归档段中的已提交行与 journal 负载不一致,保留待人工核对 exchange="
                            + entry.ExchangeId);
                    continue;
                }
                // 段不可读且活跃文档里找不到成员行时,无法证明该轮未提交;
                // fail-closed 保留 journal,绝不 cleanup(会误删已提交轮的成员记忆)。
                if (committedLine == null && !archiveFullyReadable)
                {
                    Debug.LogWarning("[江湖有灵] 归档段不可读,无法证明该轮未提交,prepared exchange 保留待恢复 exchange="
                        + entry.ExchangeId);
                    continue;
                }

                if (committedLine != null)
                {
                    // cleanup_intent (and legacy cleanup_pending) precedes the clear commit.
                    // A still-committed line proves the clear did not land, so restore the
                    // prepared state before finalizing.  This prevents a failed ClearHistory
                    // attempt from severing transcript -> memory references on restart.
                    if ((entry.State == GroupExchangeJournal.CleanupIntent
                            || entry.State == GroupExchangeJournal.CleanupPending)
                        && !_exchangeJournal.RestorePrepared(entry.ExchangeId, entry.AttemptId, entry.NpcId))
                        continue;
                    // The journal is the authoritative transaction receipt.  A line with the
                    // same IDs but altered date/memory/action payload is not enough proof to
                    // prune the child journal: repair and durably commit it first.
                    if (!EnsureCommittedLineTransaction(committedLine, entry)) continue;
                    FinalizePreparedMember(entry.ExchangeId, entry.AttemptId, entry.NpcId, entry.PlayerInput, entry.WorldDate);
                    continue;
                }

                if ((entry.State == GroupExchangeJournal.CleanupIntent
                        || entry.State == GroupExchangeJournal.CleanupPending))
                {
                    if (!_exchangeJournal.MarkCleanupCommitted(entry.ExchangeId, entry.AttemptId, entry.NpcId))
                        continue;
                    RecoverCommittedCleanupEntry(entry);
                    continue;
                }

                string childState = TalkOrchestrator.RecoveredGroupAttemptState(_taiwuId, entry.NpcId,
                    _key, entry.ExchangeId, entry.AttemptId, entry.PlayerInput, entry.WorldDate, entry.OperationIds);
                if (childState == "terminal")
                {
                    // The child mutation/rejection is already terminal but the process died before
                    // the parent row. Commit a hidden receipt row first; only then may child ACK/prune run.
                    Line receipt = NewLine(entry.ExchangeId, entry.NpcId, "", false, "", entry.WorldDate);
                    receipt.CommitAttemptId = entry.AttemptId;
                    receipt.MemoryIds = entry.MemoryIds == null ? null : new List<string>(entry.MemoryIds);
                    receipt.Actions = entry.Actions == null ? null : new List<string>(entry.Actions);
                    receipt.ToolResults = entry.ToolResults == null ? null : new List<string>(entry.ToolResults);
                    _transcript.Add(receipt);
                    if (!SaveTranscript(contentChanged: true,
                            changedExchangeId: entry.ExchangeId))
                    {
                        _transcript.Remove(receipt);
                        continue;
                    }
                    FinalizePreparedMember(entry.ExchangeId, entry.AttemptId, entry.NpcId,
                        entry.PlayerInput, entry.WorldDate);
                    continue;
                }
                if (childState == "unresolved")
                {
                    // Query-only reconciliation owns this case. Keep both journals; never manufacture
                    // a new operation id and never guess that a dispatching child was not sent.
                    TalkOrchestrator.BeginRecoveredGroupAttemptReconciliation(_taiwuId, entry.NpcId,
                        _key, entry.ExchangeId, entry.AttemptId, entry.PlayerInput, entry.WorldDate,
                        entry.OperationIds, () => RecoverPreparedExchange(entry.ExchangeId, null, true));
                    continue;
                }

                // prepared 但没有对应成员行：先 durable 标成 cleanup，再幂等撤回个人记忆。
                // 动作的单聊 PendingActionTurn 故意保留，避免一次已落地副作用被同句重发。
                if (entry.State == GroupExchangeJournal.Prepared
                    && !_exchangeJournal.MarkCleanupIntent(entry.ExchangeId, entry.AttemptId, entry.NpcId))
                {
                    Debug.LogWarning("[江湖有灵] 群 exchange cleanup intent 写盘失败 exchange=" + entry.ExchangeId);
                    continue;
                }
                if (!_exchangeJournal.MarkCleanupCommitted(entry.ExchangeId, entry.AttemptId, entry.NpcId))
                {
                    Debug.LogWarning("[江湖有灵] 群 exchange cleanup 标记写盘失败 exchange=" + entry.ExchangeId);
                    continue;
                }
                RecoverCommittedCleanupEntry(entry);
            }
        }

        private bool RecoverCommittedCleanupEntry(GroupExchangeJournal.Entry entry)
        {
            if (entry == null) return false;
            string childState = TalkOrchestrator.RecoveredGroupAttemptState(_taiwuId, entry.NpcId,
                _key, entry.ExchangeId, entry.AttemptId, entry.PlayerInput, entry.WorldDate, entry.OperationIds);
            if (childState == "unresolved")
            {
                TalkOrchestrator.BeginRecoveredGroupAttemptReconciliation(_taiwuId, entry.NpcId,
                    _key, entry.ExchangeId, entry.AttemptId, entry.PlayerInput, entry.WorldDate,
                    entry.OperationIds, () => RecoverPreparedExchange(entry.ExchangeId, null, true));
                return false;
            }
            if (childState == "terminal"
                && !TalkOrchestrator.FinalizeRecoveredGroupCommit(_taiwuId, entry.NpcId,
                    _key, entry.ExchangeId, entry.AttemptId, entry.PlayerInput, entry.WorldDate))
                return false;
            // terminal is now durably ACKed/pruned; not_dispatched has no child mutation.
            if (!RemovePreparedEntryMemories(entry)) return false;
            if (_exchangeJournal.Complete(entry.ExchangeId, entry.AttemptId, entry.NpcId)) return true;
            Debug.LogWarning("[江湖有灵] 群 exchange cleanup 完成标记失败，将幂等重放 exchange=" + entry.ExchangeId);
            return false;
        }

        private static bool LineMatchesEntry(Line line, GroupExchangeJournal.Entry entry)
        {
            if (line == null || entry == null || line.IsTaiwu || line.SpeakerId != entry.NpcId
                || !string.Equals(line.ExchangeId, entry.ExchangeId, StringComparison.Ordinal)) return false;
            return !string.IsNullOrWhiteSpace(entry.AttemptId)
                && string.Equals(line.CommitAttemptId, entry.AttemptId, StringComparison.Ordinal);
        }

        private bool EnsureCommittedLineTransaction(Line recoveredLine, GroupExchangeJournal.Entry entry)
        {
            if (recoveredLine == null || entry == null) return false;
            Line target = null;
            foreach (Line line in _transcript)
                if (LineMatchesEntry(line, entry)) { target = line; break; }

            bool added = false;
            if (target == null)
            {
                target = CloneLine(recoveredLine);
                _transcript.Add(target);
                added = true;
            }
            int oldDate = target.Date;
            List<string> oldMemoryIds = target.MemoryIds == null ? null : new List<string>(target.MemoryIds);
            List<string> oldActions = target.Actions == null ? null : new List<string>(target.Actions);
            List<string> oldToolResults = target.ToolResults == null ? null : new List<string>(target.ToolResults);
            bool changed = added || target.Date != entry.WorldDate
                || !SameStrings(target.MemoryIds, entry.MemoryIds)
                || !SameStrings(target.Actions, entry.Actions)
                || !SameStrings(target.ToolResults, entry.ToolResults);
            if (!changed) return true;

            target.Date = entry.WorldDate;
            target.MemoryIds = entry.MemoryIds == null ? null : new List<string>(entry.MemoryIds);
            target.Actions = entry.Actions == null ? null : new List<string>(entry.Actions);
            target.ToolResults = entry.ToolResults == null ? null : new List<string>(entry.ToolResults);
            if (SaveTranscript(contentChanged: true,
                    changedExchangeId: entry.ExchangeId)) return true;

            if (added) _transcript.Remove(target);
            else
            {
                target.Date = oldDate;
                target.MemoryIds = oldMemoryIds;
                target.Actions = oldActions;
                target.ToolResults = oldToolResults;
            }
            return false;
        }

        private static Line CloneLine(Line source)
            => source == null ? null : new Line
            {
                Id = source.Id,
                ExchangeId = source.ExchangeId,
                CommitAttemptId = source.CommitAttemptId,
                SpeakerId = source.SpeakerId,
                Speaker = source.Speaker,
                IsTaiwu = source.IsTaiwu,
                Text = source.Text,
                Date = source.Date,
                LocationText = source.LocationText,
                ContactMode = source.ContactMode,
                CreatedUtc = source.CreatedUtc,
                MemoryIds = source.MemoryIds == null ? null : new List<string>(source.MemoryIds),
                Actions = source.Actions == null ? null : new List<string>(source.Actions),
                ToolResults = source.ToolResults == null ? null : new List<string>(source.ToolResults),
                ImageFileName = source.ImageFileName,
            };

        private bool RemovePreparedEntryMemories(GroupExchangeJournal.Entry entry)
        {
            if (entry?.MemoryIds == null || entry.MemoryIds.Count == 0) return true;
            try
            {
                var memory = NpcMemoryStore.Load(JianghuYoulingPaths.Memories,
                    _taiwuId.ToString(), entry.NpcId.ToString());
                if (!memory.LoadReliable) return false;
                memory.RemoveByIds(entry.MemoryIds);
                if (!memory.Save()) return false;
                PortraitService.Invalidate(_taiwuId, entry.NpcId);
                return PortraitStore.Delete(_taiwuId, entry.NpcId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[江湖有灵] 群 exchange cleanup 失败 npc=" + entry.NpcId + ":" + ex.GetType().Name);
                return false;
            }
        }

        private IReadOnlyList<GroupExchangeJournal.Entry> PrepareJournalCleanupForClear()
        {
            if (_exchangeJournal == null) return new List<GroupExchangeJournal.Entry>();
            if (JianghuYoulingPaths.CurrentWorldId != _journalWorldId) return null;
            var entries = _exchangeJournal.Snapshot();
            if (entries == null) return null;
            foreach (var entry in entries)
            {
                if (entry == null) continue;
                if (entry.State != GroupExchangeJournal.CleanupIntent
                    && entry.State != GroupExchangeJournal.CleanupPending
                    && entry.State != GroupExchangeJournal.CleanupCommitted
                    && !_exchangeJournal.MarkCleanupIntent(entry.ExchangeId, entry.AttemptId, entry.NpcId)) return null;
                if (!_pendingLinkedMemoryRemovals.TryGetValue(entry.NpcId, out var ids))
                { ids = new HashSet<string>(StringComparer.Ordinal); _pendingLinkedMemoryRemovals[entry.NpcId] = ids; }
                if (entry.MemoryIds != null)
                    foreach (string id in entry.MemoryIds)
                        if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
            }
            return entries;
        }

        private bool LoadTranscript()
        {
            _transcriptReliable = false;
            try
            {
                _pendingMemoryRemovalExchangeIds.Clear();
                _pendingLinkedMemoryRemovals.Clear();
                _pendingContextProjectionExchangeIds.Clear();
                _memoryProjectionPending = false;
                var p = ConvPath();
                string previous = Path.Combine(JianghuYoulingPaths.ChatLogs, "Group_" + PreviousGroupKey(_taiwuId, _members) + ".json");
                string legacy = Path.Combine(JianghuYoulingPaths.ChatLogs, "Group_" + LegacyGroupKey(_taiwuId, _members) + ".json");
                // Only the old roster-derived canonical key may claim still-older roster-key
                // documents. A new unique group with the same people must start empty instead
                // of silently adopting an earlier group's transcript.
                bool allowRosterKeyMigration = string.Equals(
                    _key, GroupKey(_taiwuId, _members), StringComparison.Ordinal);
                lock (GetFileLock(p))
                {
                    _fileEpoch = CurrentFileEpoch(p);
                    GroupDocument doc = null;
                    bool canonicalReliable = true;
                    if (HasRecoverableDocument(p))
                        doc = ReadDocumentRecoverable(p, preserveCorrupt: true,
                            JianghuYoulingPaths.CurrentWorldId, _taiwuId, _key, ExpectedMemberIds(),
                            out canonicalReliable);
                    if (!canonicalReliable) return false;
                    if (doc != null)
                    {
                        _transcript = doc.Lines ?? new List<Line>();
                        _documentRevision = doc.Revision;
                        SyncArchivedSegmentCount(doc.ArchivedSegmentCount);
                        LoadPendingContextProjections(doc);
                        MergePendingMemoryRefresh(doc);
                        MergePendingLinkedMemoryRemovals(doc);
                        NormalizeTranscript();
                        // A canonical new-key document owns this group. Archive legacy
                        // key variants so history discovery cannot display them twice.
                        if (allowRosterKeyMigration)
                        {
                            ArchiveMigratedDocument(previous);
                            ArchiveMigratedDocument(legacy);
                        }
                        _transcriptReliable = true;
                        return true;
                    }

                    // 新 key 不可用时，同时读取两种旧 key；不能只迁移优先命中的一个，否则另一份
                    // 唯一历史会永久留在旧文件或在“全部场次”里重复出现。
                    var legacyDocuments = new List<KeyValuePair<string, GroupDocument>>();
                    if (allowRosterKeyMigration)
                    {
                        foreach (string candidate in new[] { previous, legacy })
                        {
                            if (!HasRecoverableDocument(candidate)) continue;
                            bool legacyReliable;
                            var old = ReadDocumentRecoverable(candidate, preserveCorrupt: true,
                                JianghuYoulingPaths.CurrentWorldId, _taiwuId, null, ExpectedMemberIds(),
                                out legacyReliable);
                            if (!legacyReliable) return false;
                            if (old != null) legacyDocuments.Add(
                                new KeyValuePair<string, GroupDocument>(candidate, old));
                        }
                    }
                    if (legacyDocuments.Count == 0)
                    {
                        NormalizeTranscript();
                        _transcriptReliable = true;
                        return true;
                    }
                    _transcript = MergeLegacyDocuments(legacyDocuments);
                    foreach (var pair in legacyDocuments)
                    {
                        _documentRevision = Math.Max(_documentRevision, pair.Value.Revision);
                        MergePendingContextProjections(pair.Value);
                        MergePendingMemoryRefresh(pair.Value);
                        MergePendingLinkedMemoryRemovals(pair.Value);
                    }
                    NormalizeTranscript();
                    _transcriptReliable = true;
                    // v1 哈希/v2 长成员键迁移到定长 SHA-256 键 + v3 文档；旧文件改扩展名保留备份，
                    // 不再被“载入全部场次”重复显示。
                    if (SaveTranscript())
                    {
                        foreach (var pair in legacyDocuments) ArchiveMigratedDocument(pair.Key);
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                _transcriptReliable = false;
                Debug.LogWarning("[江湖有灵] 群聊载入失败:" + ex.GetType().Name);
                return false;
            }
        }

        private bool SaveTranscript(bool contentChanged = false, string changedExchangeId = null)
        {
            if (contentChanged)
            {
                if (string.IsNullOrWhiteSpace(changedExchangeId))
                    changedExchangeId = _currentExchangeId;
                if (string.IsNullOrWhiteSpace(changedExchangeId)) return false;
                _pendingContextProjectionExchangeIds.Add(changedExchangeId);
                _memoryProjectionPending = true;
            }
            if (!_transcriptReliable) return false;
            if (_worldGeneration > 0 && !WorldLifecycle.IsSameWorld(_worldGeneration)) return false;
            if (JianghuYoulingPaths.CurrentWorldId == 0) return false;
            try
            {
                Directory.CreateDirectory(JianghuYoulingPaths.ChatLogs);
                var p = ConvPath();
                lock (GetFileLock(p))
                {
                    if (_fileEpoch != CurrentFileEpoch(p)) return false;
                    // 同一成员组允许开多个页签；保存前按稳定 Line.Id 合并盘上新行，并应用本页签删除集，
                    // 避免最后写入者覆盖另一页签刚写的群聊。
                    bool diskReliable;
                    var disk = ReadDocumentRecoverable(p, preserveCorrupt: true,
                        JianghuYoulingPaths.CurrentWorldId, _taiwuId, _key, ExpectedMemberIds(),
                        out diskReliable);
                    if (!diskReliable) { _transcriptReliable = false; return false; }
                    // A stale tab may save after another tab committed a line. Merge every
                    // per-exchange obligation; no tab may clear a sibling tab's pending work.
                    MergePendingContextProjections(disk);
                    var completedMemoryRefresh = CompletedMemoryRefreshIds(p);
                    if (disk?.PendingMemoryRefreshExchangeIds != null)
                        foreach (string exchangeId in disk.PendingMemoryRefreshExchangeIds)
                            if (!string.IsNullOrWhiteSpace(exchangeId) && !completedMemoryRefresh.Contains(exchangeId))
                                _pendingMemoryRemovalExchangeIds.Add(exchangeId);
                    foreach (string exchangeId in completedMemoryRefresh)
                        _pendingMemoryRemovalExchangeIds.Remove(exchangeId);
                    MergePendingLinkedMemoryRemovals(disk, p);
                    // 另一页签可能已把更老的轮次封存;活跃合并绝不能把已归档行重新写回活跃
                    // 文档,否则同一行会同时存在于段与活跃文档、并被再次归档成永久重复。
                    if (!ReconcileArchivedSegments(disk)) return false;
                    var merged = disk?.Lines ?? new List<Line>();
                    var sharedDeleted = DeletedLines(p);
                    foreach (var id in _deletedLineIds) sharedDeleted.Add(id);
                    merged.RemoveAll(x => x != null && !string.IsNullOrEmpty(x.Id) && sharedDeleted.Contains(x.Id));
                    var byId = new Dictionary<string, Line>();
                    foreach (var line in merged) if (line != null && !string.IsNullOrEmpty(line.Id)) byId[line.Id] = line;
                    foreach (var line in _transcript)
                    {
                        if (line == null) continue;
                        if (string.IsNullOrEmpty(line.Id)) line.Id = NewId("l");
                        if (sharedDeleted.Contains(line.Id)) continue;
                        if (byId.TryGetValue(line.Id, out var old))
                        {
                            int idx = merged.IndexOf(old); if (idx >= 0) merged[idx] = line;
                        }
                        else merged.Add(line);
                        byId[line.Id] = line;
                    }
                    _transcript = merged;
                    PrunePendingContextProjectionsWithoutLines();
                    long diskRevision = disk?.Revision ?? 0;
                    long nextRevision = checked(Math.Max(_documentRevision, diskRevision) + 1);
                    var doc = MakeDocument(_transcript, nextRevision);
                    WriteDocumentAtomic(p, doc, ".tmp");
                    _documentRevision = nextRevision;
                    _deletedLineIds.Clear();
                    WarnIfActiveDocumentOversized(p);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[江湖有灵] 群聊存盘失败:" + ex.GetType().Name);
                return false;
            }
        }

        /// <summary>清空这支小队的群聊记录(内存 + 文件)。</summary>
        public bool ClearHistory()
        {
            if (!_transcriptReliable || !WorldLifecycle.IsSameWorld(_worldGeneration)) return false;
            Cancel();
            if (JianghuYoulingPaths.CurrentWorldId == 0) return false;
            string path = ConvPath();
            // 先失效同组所有旧页签/固化任务，再扫描 SourceId。否则损坏文档场景下，另一
            // 页签可能在扫描之后才写入一条新来源记忆，使清空漏掉最后这条事实。
            lock (GetFileLock(path))
            {
                FileEpochs[path] = unchecked(CurrentFileEpoch(path) + 1);
                _fileEpoch = FileEpochs[path];
            }
            _groupEpoch = AdvanceGroupEpoch(_journalPath);
            // 快照 clear 入口的可恢复意图。任何在"空文档 durable 提交成功"之前失败的 return
            // 都必须还原本次改成的 CleanupIntent、并清掉本次新加的 pending，绝不留半态,
            // 否则会误删成员群动作记忆并卡死投影/归档。
            var journalBeforeClear = _exchangeJournal?.Snapshot();
            var pendingExchangeBeforeClear = new HashSet<string>(_pendingMemoryRemovalExchangeIds, StringComparer.Ordinal);
            var linkedRemovalsBeforeClear = new Dictionary<int, HashSet<string>>();
            foreach (var pair in _pendingLinkedMemoryRemovals)
                linkedRemovalsBeforeClear[pair.Key] = new HashSet<string>(pair.Value, StringComparer.Ordinal);
            void RestoreClearPreState()
            {
                if (journalBeforeClear != null && _exchangeJournal != null)
                    foreach (var entry in journalBeforeClear)
                        if (entry != null && entry.State != GroupExchangeJournal.CleanupIntent
                            && entry.State != GroupExchangeJournal.CleanupPending
                            && entry.State != GroupExchangeJournal.CleanupCommitted)
                            _exchangeJournal.RestorePrepared(entry.ExchangeId, entry.AttemptId, entry.NpcId);
                _pendingMemoryRemovalExchangeIds.Clear();
                foreach (string exchangeId in pendingExchangeBeforeClear) _pendingMemoryRemovalExchangeIds.Add(exchangeId);
                _pendingLinkedMemoryRemovals.Clear();
                foreach (var pair in linkedRemovalsBeforeClear)
                    _pendingLinkedMemoryRemovals[pair.Key] = new HashSet<string>(pair.Value, StringComparer.Ordinal);
            }
            var preparedForClear = PrepareJournalCleanupForClear();
            if (preparedForClear == null) { RestoreClearPreState(); return false; }
            // 同一成员组可开多个页签；清空必须覆盖本页内存和磁盘上由其他页签刚写入的全部轮次。
            var allExchangeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (string exchangeId in _pendingMemoryRemovalExchangeIds) allExchangeIds.Add(exchangeId);
            CollectExchangeIds(_transcript, allExchangeIds);
            // transcript/恢复副本可能全损坏；成员记忆里的 SourceId 是最后一份可用删除索引。
            // 任一记忆库不可可靠读取时先拒绝清空，不能先提交空聊天再丢失撤回线索。
            if (!CollectGroupMemoryExchangeIds(allExchangeIds)) { RestoreClearPreState(); return false; }
            EnqueueLinkedMemoryRemovals(_transcript);
            string previousPath = Path.Combine(JianghuYoulingPaths.ChatLogs,
                "Group_" + PreviousGroupKey(_taiwuId, _members) + ".json");
            string legacyPath = Path.Combine(JianghuYoulingPaths.ChatLogs,
                "Group_" + LegacyGroupKey(_taiwuId, _members) + ".json");
            bool persisted = false;
            int archivedCountBeforeClear = _archivedSegmentCount;
            try
            {
                lock (GetFileLock(path))
                {
                    var sharedDeleted = DeletedLines(path);
                    CollectCandidateExchangeIds(path, JianghuYoulingPaths.CurrentWorldId, _taiwuId,
                        _key, ExpectedMemberIds(), allExchangeIds);
                    CollectCandidateExchangeIds(previousPath, JianghuYoulingPaths.CurrentWorldId, _taiwuId,
                        null, ExpectedMemberIds(), allExchangeIds);
                    CollectCandidateExchangeIds(legacyPath, JianghuYoulingPaths.CurrentWorldId, _taiwuId,
                        null, ExpectedMemberIds(), allExchangeIds);
                    bool diskReliable;
                    var disk = ReadDocumentRecoverable(path, preserveCorrupt: true,
                        JianghuYoulingPaths.CurrentWorldId, _taiwuId, _key, ExpectedMemberIds(),
                        out diskReliable);
                    if (!diskReliable) { _transcriptReliable = false; RestoreClearPreState(); return false; }
                    if (disk?.Lines != null)
                    {
                        CollectExchangeIds(disk.Lines, allExchangeIds);
                        EnqueueLinkedMemoryRemovals(disk.Lines);
                    }
                    // 归档段里的轮次同样要撤回来源记忆:可读段逐一枚举 ExchangeId 与关联
                    // 记忆 ID;不可读段由上面的成员记忆反查兜底,不会漏成孤儿。
                    int segmentCountBeforeClear = Math.Max(_archivedSegmentCount, disk?.ArchivedSegmentCount ?? 0);
                    for (int index = 1; index <= segmentCountBeforeClear; index++)
                    {
                        var segment = ReadArchiveSegment(index);
                        if (segment?.Lines == null) continue;
                        CollectExchangeIds(segment.Lines, allExchangeIds);
                        EnqueueLinkedMemoryRemovals(segment.Lines);
                    }

                    foreach (string exchangeId in allExchangeIds)
                        _pendingMemoryRemovalExchangeIds.Add(exchangeId);

                    long nextRevision = checked(Math.Max(_documentRevision, disk?.Revision ?? 0) + 1);
                    // 清空后的活跃文档不再声明任何归档段;段文件随后 best-effort 删除。
                    _archivedSegmentCount = 0;
                    var clearedDocument = MakeDocument(new List<Line>(), nextRevision,
                        memoryProjectionPending: false);
                    WriteDocumentAtomic(path, clearedDocument, ".tmp");
                    _documentRevision = nextRevision;
                    _memoryProjectionPending = false;
                    _pendingContextProjectionExchangeIds.Clear();
                    // 空文档已 durable 提交后才写进程级删除墓碑，阻止同组旧页签把已清除行合并回
                    // 活跃文档。提交前任何失败都不会留下这些墓碑，避免下一次保存静默清空 transcript。
                    foreach (var line in _transcript)
                        if (line != null && !string.IsNullOrEmpty(line.Id)) sharedDeleted.Add(line.Id);
                    if (disk?.Lines != null)
                        foreach (var line in disk.Lines)
                            if (line != null && !string.IsNullOrEmpty(line.Id)) sharedDeleted.Add(line.Id);
                    _deletedLineIds.Clear();
                    _archivedLines = new List<Line>();
                    _archivedLoaded = true;
                    // Keep the active-document commit and removal of every segment in the same
                    // per-group snapshot gate. A background export holds this gate while it
                    // reads the active document and all declared segments, so it observes either
                    // the complete pre-clear transcript or the complete post-clear transcript.
                    DeleteArchiveSegmentsBestEffort();
                    persisted = true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[江湖有灵] 清空群聊存盘失败:" + ex.GetType().Name);
            }
            if (!persisted)
            {
                // 空文档未提交,活跃文档仍声明原有归档段,内存计数必须回滚保持一致。
                _archivedSegmentCount = archivedCountBeforeClear;
                // 空 transcript 未提交：还原本次改成的 CleanupIntent 与本次新加的 pending，
                // 否则下一次载入会把 clear 前的意图误当作删除成员记忆的许可。
                RestoreClearPreState();
                return false;
            }
            // Establish the second phase before deleting any referenced memory. If this
            // process stops between entries, recovery can distinguish committed entries;
            // remaining intents see the already-empty transcript and safely advance.
            foreach (var entry in preparedForClear)
                if (entry != null && !_exchangeJournal.MarkCleanupCommitted(
                    entry.ExchangeId, entry.AttemptId, entry.NpcId)) return false;
            _transcript = new List<Line>();
            _currentExchangeId = null;
            CloseInterjectionWindow(null, true);
            if (!string.Equals(previousPath, path, StringComparison.OrdinalIgnoreCase)) ArchiveMigratedDocument(previousPath);
            if (!string.Equals(legacyPath, path, StringComparison.OrdinalIgnoreCase)) ArchiveMigratedDocument(legacyPath);
            if (!RefreshMemoryForExchanges(allExchangeIds) || !RemovePendingLinkedMemories()) return false;
            // Do not drop the parent recovery index while a child operation is still
            // dispatching/unknown. Terminal children are ACKed and pruned first;
            // unresolved children remain query-only and complete on a later replay.
            RecoverPreparedExchange(null, null, true);

            // 来源记忆已确认刷新；再写一版去掉事务日志。此步失败仍返回 false，并保留
            // pending，重启/重试会安全地再次执行幂等刷新，而不会留下永久孤儿记忆。
            MarkMemoryRefreshCompleted(allExchangeIds);
            if (SaveTranscript()) return true;
            foreach (string exchangeId in allExchangeIds) _pendingMemoryRemovalExchangeIds.Add(exchangeId);
            return false;
        }

        public sealed class GroupSession
        {
            public string GroupId;
            public string Participants;
            public List<int> MemberIds;
            public List<Member> Members;
            public List<Line> Lines;
            public bool IsCurrent;
            public DateTime LastActivityUtc;
            public bool HasActiveLines;
            public int ArchivedSegmentCount;
        }

        /// <summary>
        /// 只读取群聊活跃文档中的成员索引与最近活动时间，供永久导航和选人窗口使用。
        /// 若活跃段没有可排序时间，只额外读取最新一个归档段；不会随群聊历史总量增长。
        /// </summary>
        public List<GroupSession> LoadSessionSummaries(int taiwuId)
        {
            if (TryLoadIndexedSessionSummaries(taiwuId, out List<GroupSession> indexed))
                return indexed;
            return new List<GroupSession>();
        }

        /// <summary>
        /// Fast sidebar path.  Missing/dirty metadata is reported to the caller instead of
        /// parsing every group transcript synchronously on the Unity thread.
        /// </summary>
        internal bool TryLoadIndexedSessionSummaries(int taiwuId,
            out List<GroupSession> list)
        {
            list = new List<GroupSession>();
            if (taiwuId <= 0) return false;
            if (ConversationSessionIndexStore.TryLoadGroups(taiwuId,
                out List<ConversationSessionIndexStore.GroupEntry> indexed))
            {
                foreach (ConversationSessionIndexStore.GroupEntry entry in indexed)
                {
                    var ids = new List<int>();
                    var members = new List<Member>();
                    var names = new List<string>();
                    foreach (ConversationSessionIndexStore.MemberEntry member in entry.Members)
                    {
                        ids.Add(member.Id);
                        members.Add(new Member { Id = member.Id, Name = member.Name });
                        if (!string.IsNullOrWhiteSpace(member.Name)
                            && !names.Contains(member.Name)) names.Add(member.Name);
                    }
                    list.Add(new GroupSession
                    {
                        GroupId = entry.GroupId,
                        Participants = names.Count > 0 ? string.Join("、", names) : "某队",
                        MemberIds = ids,
                        Members = members,
                        Lines = new List<Line>(),
                        IsCurrent = false,
                        LastActivityUtc = SafeUtcFromTicks(entry.LastActivityUtcTicks),
                        HasActiveLines = entry.HasActiveLines,
                        ArchivedSegmentCount = entry.ArchivedSegmentCount,
                    });
                }
                SortSessionSummaries(list);
                return true;
            }
            return false;
        }

        internal List<ConversationSessionIndexStore.GroupEntry>
            BuildSessionIndexSnapshot(int taiwuId, uint expectedWorldId,
            string directory, int expectedGeneration)
        {
            if (taiwuId <= 0 || expectedWorldId == 0 || string.IsNullOrWhiteSpace(directory)
                || JianghuYoulingPaths.CurrentWorldId != expectedWorldId
                || !WorldLifecycle.IsSameWorld(expectedGeneration)) return null;
            List<GroupSession> list = ScanSessionSummaries(
                taiwuId, expectedWorldId, directory, expectedGeneration, out bool complete);
            if (!complete) return null;
            if (JianghuYoulingPaths.CurrentWorldId != expectedWorldId
                || !WorldLifecycle.IsSameWorld(expectedGeneration)) return null;
            return list.ConvertAll(SessionIndexEntry);
        }

        private List<GroupSession> ScanSessionSummaries(int taiwuId, uint worldId,
            string directory, int expectedGeneration)
            => ScanSessionSummaries(taiwuId, worldId, directory, expectedGeneration, out _);

        private List<GroupSession> ScanSessionSummaries(int taiwuId, uint worldId,
            string directory, int expectedGeneration, out bool complete)
        {
            var list = new List<GroupSession>();
            complete = false;
            try
            {
                if (worldId == 0 || string.IsNullOrWhiteSpace(directory)
                    || !Directory.Exists(directory))
                {
                    complete = true;
                    return list;
                }
                foreach (string path in DiscoverDocumentPaths(
                    directory, "Group_" + taiwuId + "_*.json*"))
                {
                    if (expectedGeneration >= 0
                        && (JianghuYoulingPaths.CurrentWorldId != worldId
                            || !WorldLifecycle.IsSameWorld(expectedGeneration))) return list;
                    try
                    {
                        GroupDocument doc = ReadDocumentIndexSnapshot(
                            path, worldId, taiwuId);
                        if (doc == null)
                        {
                            // No replica passed integrity/identity validation.  This is not the
                            // same as a successfully parsed identity-less v1 transcript: committing
                            // a "complete" rebuild here would make a temporarily unreadable group
                            // disappear from the durable sidebar index. Keep the previous index and
                            // let the background rebuild retry later.
                            Debug.LogWarning("[江湖有灵] 群聊索引遇到暂不可读文件，保留旧索引并稍后重试 "
                                + Path.GetFileName(path));
                            complete = false;
                            return list;
                        }
                        if (string.IsNullOrWhiteSpace(doc.GroupId))
                        {
                            // Very early group-chat builds wrote a bare Line[] without a
                            // GroupId/member roster.  Such a file is still a valid piece of
                            // player history, but it cannot identify a navigation entry.  The
                            // index is derived data: one identity-less legacy transcript must
                            // not prevent every valid group and single conversation from
                            // appearing in the sidebar.
                            Debug.LogWarning("[江湖有灵] 群聊索引跳过无法识别身份的旧文件 "
                                + Path.GetFileName(path) + "；原聊天记录保持不变");
                            continue;
                        }
                        HashSet<int> ids = MemberIds(doc);
                        var members = new List<Member>();
                        var names = new List<string>();
                        if (doc.Members != null)
                            foreach (MemberRef member in doc.Members)
                            {
                                if (member == null || member.Id <= 0) continue;
                                members.Add(new Member { Id = member.Id, Name = member.Name });
                                if (!string.IsNullOrWhiteSpace(member.Name)
                                    && !names.Contains(member.Name)) names.Add(member.Name);
                            }
                        // 0.29 旧文档可能没有 Members；活跃行仍是有界的，可用于一次性迁移兼容。
                        if (ids.Count == 0)
                            foreach (Line line in VisibleLines(doc.Lines))
                            {
                                if (line == null || line.IsTaiwu || line.SpeakerId < 0) continue;
                                if (ids.Add(line.SpeakerId))
                                    members.Add(new Member { Id = line.SpeakerId, Name = line.Speaker });
                                if (!string.IsNullOrWhiteSpace(line.Speaker)
                                    && !names.Contains(line.Speaker)) names.Add(line.Speaker);
                            }
                        if (ids.Count == 0) continue;
                        DateTime order = LatestContentOrder(VisibleLines(doc.Lines));
                        if (order <= ContentOrderEpochUtc && doc.ArchivedSegmentCount > 0)
                        {
                            int newest = doc.ArchivedSegmentCount;
                            string archivePath = ArchiveSegmentPathFor(
                                directory, doc.GroupId, newest);
                            GroupDocument segment = ReadArchiveSegmentIndexSnapshot(
                                archivePath, newest, worldId, doc.TaiwuId, doc.GroupId);
                            order = LatestContentOrder(segment?.Lines);
                            if (order <= ContentOrderEpochUtc)
                                order = DocumentLastWriteUtc(archivePath);
                        }
                        if (order <= ContentOrderEpochUtc) order = DocumentLastWriteUtc(path);
                        list.Add(new GroupSession
                        {
                            GroupId = doc.GroupId,
                            Participants = names.Count > 0 ? string.Join("、", names) : "某队",
                            MemberIds = new List<int>(ids),
                            Members = members,
                            Lines = new List<Line>(),
                            IsCurrent = false,
                            LastActivityUtc = order,
                            HasActiveLines = VisibleLines(doc.Lines).Count > 0,
                            ArchivedSegmentCount = doc.ArchivedSegmentCount,
                        });
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[江湖有灵] 群聊索引读取失败，保留旧索引并稍后重试 "
                            + Path.GetFileName(path) + ":" + ex.GetType().Name);
                        complete = false;
                        return list;
                    }
                }
                SortSessionSummaries(list);
                complete = true;
            }
            catch { complete = false; }
            return list;
        }

        private static void SortSessionSummaries(List<GroupSession> list)
        {
            list.Sort((a, b) =>
            {
                int byActivity = a.LastActivityUtc.CompareTo(b.LastActivityUtc);
                return byActivity != 0 ? byActivity
                    : string.Compare(a.GroupId, b.GroupId, StringComparison.Ordinal);
            });
        }

        private static DateTime SafeUtcFromTicks(long ticks)
        {
            try
            {
                return ticks > 0 && ticks <= DateTime.MaxValue.Ticks
                    ? new DateTime(ticks, DateTimeKind.Utc)
                    : DateTime.MinValue;
            }
            catch { return DateTime.MinValue; }
        }

        private static ConversationSessionIndexStore.GroupEntry SessionIndexEntry(GroupSession session)
        {
            var entry = new ConversationSessionIndexStore.GroupEntry
            {
                GroupId = session.GroupId,
                HasActiveLines = session.HasActiveLines,
                ArchivedSegmentCount = session.ArchivedSegmentCount,
                LastActivityUtcTicks = session.LastActivityUtc.ToUniversalTime().Ticks,
            };
            if (session.Members != null)
                foreach (Member member in session.Members)
                    if (member != null && member.Id > 0)
                        entry.Members.Add(new ConversationSessionIndexStore.MemberEntry
                        {
                            Id = member.Id,
                            Name = member.Name,
                        });
            return entry;
        }

        /// <summary>
        /// 读取指定游戏月份区间内的有日期群聊行。每个场次从活跃段向最新归档倒读，
        /// 一旦整段早于下限即停止，并受全局行数预算约束；月度事件不再加载终身历史。
        /// </summary>
        public List<GroupSession> LoadRecentSessions(int taiwuId, int minDate, int maxDate,
            int maxVisibleLines = 1024, ISet<int> requiredMemberIds = null,
            bool taiwuLinesOnly = false)
        {
            var result = new List<GroupSession>();
            if (taiwuId <= 0 || maxDate < minDate) return result;
            maxVisibleLines = Math.Max(64, Math.Min(4096, maxVisibleLines));
            try
            {
                string dir = JianghuYoulingPaths.ChatLogs;
                if (!Directory.Exists(dir)) return result;
                List<GroupSession> summaries = LoadSessionSummaries(taiwuId);
                summaries.Sort((a, b) => b.LastActivityUtc.CompareTo(a.LastActivityUtc));
                int remaining = maxVisibleLines;
                foreach (GroupSession summary in summaries)
                {
                    if (remaining <= 0) break;
                    if (requiredMemberIds != null && requiredMemberIds.Count > 0
                        && (summary.MemberIds == null
                            || !summary.MemberIds.Exists(requiredMemberIds.Contains)))
                        continue;
                    string path = Path.Combine(dir, "Group_" + summary.GroupId + ".json");
                    GroupDocument doc = ReadDocumentRecoverable(path, preserveCorrupt: true,
                        JianghuYoulingPaths.CurrentWorldId, taiwuId, summary.GroupId, null);
                    if (doc == null) continue;
                    var picked = new List<Line>();
                    bool stop = CollectRecentDatedLines(VisibleLines(doc.Lines), minDate, maxDate,
                        taiwuLinesOnly, picked, ref remaining);
                    for (int index = doc.ArchivedSegmentCount; !stop && index > 0 && remaining > 0; index--)
                    {
                        GroupDocument segment = ReadArchiveSegmentAt(
                            ArchiveSegmentPathFor(dir, doc.GroupId, index), index,
                            JianghuYoulingPaths.CurrentWorldId, doc.TaiwuId, doc.GroupId, null);
                        stop = CollectRecentDatedLines(VisibleLines(segment?.Lines), minDate, maxDate,
                            taiwuLinesOnly, picked, ref remaining);
                    }
                    if (picked.Count == 0) continue;
                    picked.Sort((a, b) =>
                    {
                        int byDate = a.Date.CompareTo(b.Date);
                        return byDate != 0 ? byDate : a.CreatedUtc.CompareTo(b.CreatedUtc);
                    });
                    summary.Lines = picked;
                    result.Add(summary);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[江湖有灵] 近期群聊有界读取失败:" + ex.GetType().Name);
                result.Clear();
            }
            return result;
        }

        private static bool CollectRecentDatedLines(List<Line> source, int minDate, int maxDate,
            bool taiwuLinesOnly, List<Line> destination, ref int remaining)
        {
            if (source == null || source.Count == 0) return false;
            int newestDated = int.MinValue;
            for (int i = source.Count - 1; i >= 0 && remaining > 0; i--)
            {
                Line line = source[i];
                if (line == null || line.Date < 0) continue;
                if (line.Date > newestDated) newestDated = line.Date;
                if (line.Date < minDate || line.Date > maxDate) continue;
                if (taiwuLinesOnly && !line.IsTaiwu) continue;
                destination.Add(line);
                remaining--;
            }
            // 归档段按时间递增；整段已有日期且最新一行仍早于下限时，更早段无需再读。
            return newestDated != int.MinValue && newestDated < minDate;
        }

        /// <summary>载入该太吾【所有】群聊场次(各按成员集合分文件存),按场次时间先后排序;供群聊窗顺序拼显示所有场、每场标参与人。
        /// 排序优先用场次内容时间(真实行的最大 CreatedUtc):世界目录迁移会把旧文件 mtime 重写为拷贝瞬间,
        /// 只信 mtime 会打乱旧场次时间线;仅当内容时间缺失(0.29 旧数组行 CreatedUtc=default)才回退文件时间。</summary>
        public List<GroupSession> LoadAllSessions(int taiwuId)
            => LoadAllSessionsCore(taiwuId, JianghuYoulingPaths.CurrentWorldId,
                JianghuYoulingPaths.ChatLogs, readOnly: false, out _);

        internal List<GroupSession> LoadAllSessions(int taiwuId, uint worldId,
            string directory)
            => LoadAllSessionsCore(taiwuId, worldId, directory, readOnly: true, out _);

        internal bool TryLoadAllSessionsForExport(int taiwuId, uint worldId,
            string directory, out List<GroupSession> sessions)
        {
            sessions = LoadAllSessionsCore(taiwuId, worldId, directory,
                readOnly: true, out bool complete);
            return complete;
        }

        private List<GroupSession> LoadAllSessionsCore(int taiwuId, uint worldId,
            string directory, bool readOnly, out bool complete)
        {
            var list = new List<GroupSession>();
            complete = false;
            try
            {
                var dir = directory;
                if (worldId == 0 || string.IsNullOrWhiteSpace(dir)) return list;
                if (!Directory.Exists(dir))
                {
                    complete = true;
                    return list;
                }
                string curFile = "Group_" + _key + ".json";
                var files = DiscoverDocumentPaths(dir, "Group_" + taiwuId + "_*.json*");
                // 文件时间先做稳定预排:内容键相同或都缺失时,保持与旧行为一致的相对顺序。
                files.Sort((a, b) => DocumentLastWriteUtc(a).CompareTo(DocumentLastWriteUtc(b)));
                var ordered = new List<OrderedSession>();
                foreach (var f in files)
                {
                    try
                    {
                        var lines = new List<Line>();
                        GroupDocument doc;
                        // The active document declares the immutable segment set. Hold its
                        // per-group gate across the complete snapshot so clear/archive cannot
                        // replace that declaration and remove segments halfway through export.
                        lock (GetFileLock(f))
                        {
                            doc = readOnly
                                ? ReadDocumentIndexSnapshot(f, worldId, taiwuId)
                                : ReadDocumentRecoverable(f, preserveCorrupt: true,
                                    worldId, taiwuId, null, null);
                            // 场次历史 = 该场归档段(旧→新)+活跃文档;只认活跃文档声明的段数。
                            if (doc != null && doc.ArchivedSegmentCount > 0
                                && !string.IsNullOrWhiteSpace(doc.GroupId))
                                for (int index = 1; index <= doc.ArchivedSegmentCount; index++)
                                {
                                    string segmentPath = ArchiveSegmentPathFor(
                                        dir, doc.GroupId, index);
                                    var segment = readOnly
                                        ? ReadArchiveSegmentIndexSnapshot(segmentPath, index,
                                            worldId, doc.TaiwuId, doc.GroupId)
                                        : ReadArchiveSegmentAt(segmentPath, index,
                                            worldId, doc.TaiwuId, doc.GroupId, null);
                                    if (segment?.Lines == null)
                                    {
                                        if (readOnly) return list;
                                        lines.Add(MissingArchiveSegmentLine(index));
                                        continue;
                                    }
                                    lines.AddRange(VisibleLines(segment.Lines));
                                }
                            lines.AddRange(VisibleLines(doc?.Lines));
                        }
                        var names = new List<string>();
                        var memberIds = new List<int>();
                        var members = new List<Member>();
                        if (doc?.Members != null)
                            foreach (var m in doc.Members)
                            {
                                if (m == null) continue;
                                if (m.Id > 0 && !memberIds.Contains(m.Id)) memberIds.Add(m.Id);
                                if (!string.IsNullOrWhiteSpace(m.Name) && !names.Contains(m.Name)) names.Add(m.Name);
                                if (m.Id > 0) members.Add(new Member { Id = m.Id, Name = m.Name });
                            }
                        if (names.Count == 0)
                            foreach (var l in lines) if (l != null && !l.IsTaiwu && !string.IsNullOrWhiteSpace(l.Speaker) && !names.Contains(l.Speaker)) names.Add(l.Speaker);
                        if (doc == null || string.IsNullOrWhiteSpace(doc.GroupId)) return list;
                        if (memberIds.Count == 0) continue;
                        DateTime orderKey = SessionOrderKey(lines, f);
                        ordered.Add(new OrderedSession
                        {
                            Key = orderKey,
                            Index = ordered.Count,
                            Session = new GroupSession
                            {
                                GroupId = doc.GroupId,
                                Participants = names.Count > 0 ? string.Join("、", names) : "某队",
                                MemberIds = memberIds,
                                Members = members,
                                Lines = lines,
                                IsCurrent = string.Equals(Path.GetFileName(f), curFile, StringComparison.OrdinalIgnoreCase),
                                LastActivityUtc = orderKey
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[江湖有灵] 跳过损坏群聊文件 "
                            + Path.GetFileName(f) + ":" + ex.GetType().Name);
                        return list;
                    }
                }
                // 显式 Index 决胜负,保证排序稳定(List.Sort 本身不稳定)。
                ordered.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Index.CompareTo(b.Index));
                foreach (var entry in ordered) list.Add(entry.Session);
                complete = true;
            }
            catch { complete = false; }
            return list;
        }

        /// <summary>人物群聊历史的稳定向前游标。SourceArchiveIndex=-1 表示活跃文档，
        /// 正数表示对应归档段；SkipNewestInSource 只在该单个来源内移动，不会随历史总量增长而反复重读。</summary>
        public sealed class MemberHistoryCursor
        {
            public string SessionPath;
            public int SourceArchiveIndex;
            public int SkipNewestInSource;
            // 只在来源内已经读过一部分时设置。翻页间若活跃文档追加、或归档因删除而重写，
            // 旧 skip 已不再指向同一行边界，必须失败关闭而非重复/漏读。
            public string SourceFingerprint;
        }

        public sealed class MemberHistoryPage
        {
            public List<GroupSession> Sessions = new List<GroupSession>();
            public MemberHistoryCursor EarlierCursor;
        }

        /// <summary>
        /// 人物详情页和人物导出共用的成员定向游标分页。每次最多读取 pageSize 条可见行；
        /// 先以活跃文档成员元数据筛掉无关群聊，只会打开真正包含该 NPC 的归档段。
        /// </summary>
        public MemberHistoryPage LoadSessionsForMemberPage(int taiwuId, int npcId, string npcName,
            int pageSize, MemberHistoryCursor cursor)
            => LoadSessionsForMemberPage(taiwuId, npcId, npcName, pageSize, cursor,
                JianghuYoulingPaths.CurrentWorldId, JianghuYoulingPaths.ChatLogs,
                useCurrentWorldIndex: true);

        internal MemberHistoryPage LoadSessionsForMemberPage(int taiwuId, int npcId,
            string npcName, int pageSize, MemberHistoryCursor cursor, uint worldId,
            string directory)
            => LoadSessionsForMemberPage(taiwuId, npcId, npcName, pageSize, cursor,
                worldId, directory, useCurrentWorldIndex: false);

        private MemberHistoryPage LoadSessionsForMemberPage(int taiwuId, int npcId,
            string npcName, int pageSize, MemberHistoryCursor cursor, uint worldId,
            string directory, bool useCurrentWorldIndex)
        {
            var page = new MemberHistoryPage();
            if (taiwuId <= 0 || npcId < 0 || worldId == 0
                || string.IsNullOrWhiteSpace(directory)) return page;
            pageSize = Math.Max(50, Math.Min(1000, pageSize));
            try
            {
                string dir = directory;
                if (!Directory.Exists(dir)) return page;
                List<MemberSessionCandidate> candidates = MemberSessionCandidates(
                    dir, worldId, taiwuId, npcId, npcName, useCurrentWorldIndex);
                if (candidates.Count == 0) return page;

                int ci = candidates.Count - 1;
                int sourceArchiveIndex = -1;
                int skipNewestInSource = 0;
                var snapshottedCandidates = new List<MemberSessionCandidate>();
                if (cursor != null && !string.IsNullOrWhiteSpace(cursor.SessionPath))
                {
                    int found = candidates.FindIndex(x => string.Equals(x.Path, cursor.SessionPath,
                        StringComparison.OrdinalIgnoreCase));
                    // 游标绑定到精确场次。文件在翻页间被清理/迁移时宁可返回空页，
                    // 也不能悄悄从“最新场次”重启，导致用户看到重复或错序历史。
                    if (found < 0) return page;
                    ci = found;
                    sourceArchiveIndex = cursor.SourceArchiveIndex;
                    skipNewestInSource = Math.Max(0, cursor.SkipNewestInSource);
                    MemberSessionCandidate bound = candidates[ci];
                    if (!EnsureMemberCandidateDocument(bound, worldId, taiwuId,
                        npcId, npcName, readOnly: !useCurrentWorldIndex))
                        return page;
                    snapshottedCandidates.Add(bound);
                    // 归档数或来源内容在翻页间被清理/滚动时，旧游标已无法证明自己仍指向
                    // 同一不可变来源。宁可返回空页，也不能自动跳到另一段造成漏读/重读。
                    if (sourceArchiveIndex < -1 || sourceArchiveIndex == 0
                        || sourceArchiveIndex > bound.ArchivedSegmentCount
                        || sourceArchiveIndex > 0 && string.IsNullOrWhiteSpace(bound.Document.GroupId))
                        return page;
                    if (skipNewestInSource > 0)
                    {
                        List<Line> boundLines = MemberSourceLines(
                            dir, worldId, bound, sourceArchiveIndex,
                            readOnly: !useCurrentWorldIndex);
                        if (string.IsNullOrWhiteSpace(cursor.SourceFingerprint)
                            || !string.Equals(cursor.SourceFingerprint, MemberSourceFingerprint(boundLines),
                                StringComparison.Ordinal))
                            return page;
                    }
                }

                int remaining = pageSize;
                while (ci >= 0 && remaining > 0)
                {
                    int currentCandidateIndex = ci;
                    MemberSessionCandidate candidate = candidates[ci];
                    if (!EnsureMemberCandidateDocument(candidate, worldId,
                        taiwuId, npcId, npcName, readOnly: !useCurrentWorldIndex))
                    {
                        ci--;
                        sourceArchiveIndex = -1;
                        skipNewestInSource = 0;
                        continue;
                    }
                    if (!snapshottedCandidates.Contains(candidate))
                        snapshottedCandidates.Add(candidate);
                    var picked = new List<Line>();
                    while (ci == currentCandidateIndex && remaining > 0)
                    {
                        List<Line> lines = MemberSourceLines(
                            dir, worldId, candidate, sourceArchiveIndex,
                            readOnly: !useCurrentWorldIndex);
                        if (skipNewestInSource > lines.Count) return new MemberHistoryPage();
                        int available = Math.Max(0, lines.Count - skipNewestInSource);
                        if (available > 0)
                        {
                            int take = Math.Min(remaining, available);
                            int from = lines.Count - skipNewestInSource - take;
                            picked.InsertRange(0, lines.GetRange(from, take));
                            skipNewestInSource += take;
                            remaining -= take;
                            if (remaining == 0)
                            {
                                page.EarlierCursor = skipNewestInSource < lines.Count
                                    ? NewMemberCursor(candidate.Path, sourceArchiveIndex, skipNewestInSource,
                                        MemberSourceFingerprint(lines))
                                    : CursorBeforeSource(candidates, ci, sourceArchiveIndex);
                                break;
                            }
                        }

                        // 当前来源已取尽：活跃文档之前是最新归档段，之后逐段向旧；
                        // 归档段 1 之前才进入更早的群聊场次。
                        if (sourceArchiveIndex < 0 && candidate.ArchivedSegmentCount > 0
                            && !string.IsNullOrWhiteSpace(candidate.Document.GroupId))
                        {
                            sourceArchiveIndex = candidate.ArchivedSegmentCount;
                            skipNewestInSource = 0;
                        }
                        else if (sourceArchiveIndex > 1)
                        {
                            sourceArchiveIndex--;
                            skipNewestInSource = 0;
                        }
                        else
                        {
                            ci--;
                            sourceArchiveIndex = -1;
                            skipNewestInSource = 0;
                        }
                    }

                    if (picked.Count > 0)
                        page.Sessions.Insert(0, new GroupSession
                        {
                            Participants = candidate.Participants,
                            MemberIds = new List<int>(candidate.MemberIds),
                            Lines = picked,
                            IsCurrent = false,
                        });
                }
                // Candidate discovery, active lines and archive sources are deliberately read
                // without holding several group locks at once. Revalidate every active document
                // under its own gate before publishing the page: if clear/archive changed a
                // declaration midway through the read, discard the whole page rather than
                // returning a plausible-looking partial history.
                foreach (MemberSessionCandidate candidate in snapshottedCandidates)
                    if (!MemberCandidateSnapshotStillCurrent(candidate, worldId, taiwuId))
                        return new MemberHistoryPage();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[江湖有灵] 人物群聊游标分页读取失败:" + ex.GetType().Name);
                page.Sessions.Clear();
                page.EarlierCursor = null;
            }
            return page;
        }

        private static MemberHistoryCursor NewMemberCursor(string path, int sourceArchiveIndex, int skip,
            string sourceFingerprint = null)
            => new MemberHistoryCursor
            {
                SessionPath = path,
                SourceArchiveIndex = sourceArchiveIndex,
                SkipNewestInSource = Math.Max(0, skip),
                SourceFingerprint = skip > 0 ? sourceFingerprint : null,
            };

        private static string MemberSourceFingerprint(IList<Line> lines)
        {
            var canonical = new StringBuilder();
            canonical.Append(lines?.Count ?? 0).Append('\n');
            if (lines != null)
                foreach (Line line in lines)
                    canonical.Append(line?.Id ?? "").Append('|')
                        .Append(line?.ExchangeId ?? "").Append('|')
                        .Append(line?.CreatedUtc.ToUniversalTime().Ticks ?? 0L).Append('\n');
            byte[] digest;
            using (var sha = SHA256.Create())
                digest = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString()));
            var hex = new StringBuilder(digest.Length * 2);
            foreach (byte b in digest) hex.Append(b.ToString("x2"));
            return hex.ToString();
        }

        private static MemberHistoryCursor CursorBeforeSource(List<MemberSessionCandidate> candidates,
            int candidateIndex, int sourceArchiveIndex)
        {
            if (candidates == null || candidateIndex < 0 || candidateIndex >= candidates.Count) return null;
            MemberSessionCandidate candidate = candidates[candidateIndex];
            if (sourceArchiveIndex < 0 && candidate.ArchivedSegmentCount > 0)
                return NewMemberCursor(candidate.Path, candidate.ArchivedSegmentCount, 0);
            if (sourceArchiveIndex > 1) return NewMemberCursor(candidate.Path, sourceArchiveIndex - 1, 0);
            for (int ci = candidateIndex - 1; ci >= 0; ci--)
                if (candidates[ci].HasActiveLines || candidates[ci].ArchivedSegmentCount > 0)
                    return NewMemberCursor(candidates[ci].Path, -1, 0);
            return null;
        }

        private static List<Line> MemberSourceLines(string dir, uint worldId,
            MemberSessionCandidate candidate, int sourceArchiveIndex, bool readOnly)
        {
            if (candidate == null || candidate.Document == null) return new List<Line>();
            if (sourceArchiveIndex < 0) return VisibleLines(candidate.Document.Lines);
            if (sourceArchiveIndex <= 0 || sourceArchiveIndex > candidate.ArchivedSegmentCount
                || string.IsNullOrWhiteSpace(candidate.Document.GroupId)) return new List<Line>();
            string segmentPath = ArchiveSegmentPathFor(
                dir, candidate.Document.GroupId, sourceArchiveIndex);
            GroupDocument segment = readOnly
                ? ReadArchiveSegmentIndexSnapshot(segmentPath, sourceArchiveIndex,
                    worldId, candidate.Document.TaiwuId, candidate.Document.GroupId)
                : ReadArchiveSegmentAt(segmentPath, sourceArchiveIndex,
                    worldId, candidate.Document.TaiwuId, candidate.Document.GroupId, null);
            return segment?.Lines == null
                ? new List<Line> { MissingArchiveSegmentLine(sourceArchiveIndex) }
                : VisibleLines(segment.Lines);
        }

        private List<MemberSessionCandidate> MemberSessionCandidates(string dir, uint worldId,
            int taiwuId, int npcId, string npcName, bool useCurrentWorldIndex)
        {
            var candidates = new List<MemberSessionCandidate>();
            IEnumerable<GroupSession> summaries = useCurrentWorldIndex
                ? LoadSessionSummaries(taiwuId)
                : ScanSessionSummaries(taiwuId, worldId, dir, -1);
            foreach (GroupSession summary in summaries)
            {
                if (summary == null || string.IsNullOrWhiteSpace(summary.GroupId)
                    || summary.MemberIds == null || !summary.MemberIds.Contains(npcId)
                    || !summary.HasActiveLines && summary.ArchivedSegmentCount <= 0) continue;
                candidates.Add(new MemberSessionCandidate
                {
                    Path = Path.Combine(dir, "Group_" + summary.GroupId + ".json"),
                    MemberIds = new HashSet<int>(summary.MemberIds),
                    Participants = summary.Participants,
                    Order = summary.LastActivityUtc,
                    HasActiveLines = summary.HasActiveLines,
                    ArchivedSegmentCount = summary.ArchivedSegmentCount,
                });
            }
            candidates.Sort((a, b) =>
            {
                int byContent = a.Order.CompareTo(b.Order);
                return byContent != 0 ? byContent
                    : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            });
            return candidates;
        }

        private static bool EnsureMemberCandidateDocument(MemberSessionCandidate candidate,
            uint worldId, int taiwuId, int npcId, string npcName, bool readOnly)
        {
            if (candidate == null) return false;
            if (candidate.Document != null) return true;
            try
            {
                GroupDocument doc = readOnly
                    ? ReadDocumentIndexSnapshot(candidate.Path, worldId, taiwuId)
                    : ReadDocumentRecoverable(candidate.Path, preserveCorrupt: true,
                        worldId, taiwuId, null, candidate.MemberIds);
                if (doc == null) return false;
                HashSet<int> ids = MemberIds(doc);
                bool belongs = ids.Contains(npcId);
                if (!belongs && (doc.Members == null || doc.Members.Count == 0))
                    belongs = LinesContainMember(doc.Lines, npcId, npcName);
                if (!belongs) return false;
                candidate.Document = doc;
                candidate.MemberIds = ids;
                candidate.HasActiveLines = VisibleLines(doc.Lines).Count > 0;
                candidate.ArchivedSegmentCount = Math.Max(0, doc.ArchivedSegmentCount);
                return candidate.HasActiveLines || candidate.ArchivedSegmentCount > 0;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[江湖有灵] 人物群聊索引跳过损坏文件 "
                    + Path.GetFileName(candidate.Path) + ":" + ex.GetType().Name);
                return false;
            }
        }

        private static bool MemberCandidateSnapshotStillCurrent(MemberSessionCandidate candidate,
            uint worldId, int taiwuId)
        {
            if (candidate?.Document == null || string.IsNullOrWhiteSpace(candidate.Path))
                return false;
            lock (GetFileLock(candidate.Path))
            {
                GroupDocument current = ReadDocumentIndexSnapshot(
                    candidate.Path, worldId, taiwuId);
                return current != null && SameGroupDocument(current, candidate.Document);
            }
        }

        public static bool SessionContainsMember(GroupSession session, int npcId, string npcName)
        {
            if (session == null || npcId < 0) return false;
            if (session.MemberIds != null && session.MemberIds.Contains(npcId)) return true;
            return LinesContainMember(session.Lines, npcId, npcName);
        }

        private static bool LinesContainMember(IEnumerable<Line> lines, int npcId, string npcName)
        {
            if (lines == null) return false;
            foreach (Line line in lines)
            {
                if (line == null || line.IsTaiwu) continue;
                if (line.SpeakerId == npcId || !string.IsNullOrWhiteSpace(npcName)
                    && string.Equals(line.Speaker, npcName, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private sealed class MemberSessionCandidate
        {
            public string Path;
            public GroupDocument Document;
            public HashSet<int> MemberIds;
            public string Participants;
            public DateTime Order;
            public bool HasActiveLines;
            public int ArchivedSegmentCount;
        }

        private sealed class OrderedSession { public DateTime Key; public int Index; public GroupSession Session; }

        private static readonly DateTime ContentOrderEpochUtc = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // JHYL_GROUP_SESSION_CONTENT_ORDER:世界目录迁移(0.29→0.30)重写过旧 Group 文件的
        // mtime,纯 mtime 排序会打乱旧场次先后。优先取场次内容时间(真实行的最大 CreatedUtc);
        // 段缺失占位行(arc-missing-)的 CreatedUtc 是"现在",不能作内容键;内容时间缺失
        // (0.29 旧数组行 CreatedUtc=default)时回退文件时间。
        private static DateTime SessionOrderKey(List<Line> lines, string documentPath)
        {
            DateTime latest = LatestContentOrder(lines);
            return latest > ContentOrderEpochUtc ? latest : DocumentLastWriteUtc(documentPath);
        }

        private static DateTime LatestContentOrder(IEnumerable<Line> lines)
        {
            DateTime latest = DateTime.MinValue;
            if (lines == null) return latest;
            foreach (var line in lines)
            {
                if (line == null) continue;
                if (!string.IsNullOrEmpty(line.Id)
                    && line.Id.StartsWith("arc-missing-", StringComparison.Ordinal)) continue;
                DateTime stamp = line.CreatedUtc.ToUniversalTime();
                if (stamp > latest) latest = stamp;
            }
            return latest;
        }

        /// <summary>按轮删除/重试:按稳定 Line/Exchange ID 抹掉 transcript 行并存盘，
        /// 同时按 SourceId 精确撤销或重建各成员对应的群聊长期记忆。</summary>
        public bool DeleteLines(IEnumerable<Line> lines)
        {
            if (!_transcriptReliable || !WorldLifecycle.IsSameWorld(_worldGeneration)
                || lines == null || _transcript == null || !HasCurrentFileEpoch()) return false;
            var targets = new List<Line>();
            foreach (var line in lines) if (line != null) targets.Add(line);
            // 生成失败、尚未写入 transcript 的群聊轮只是 UI 占位，删除不需要触碰磁盘。
            if (targets.Count == 0) return true;
            // 不可读段的占位行只是 UI 提示,背后没有可校验、可撤回的轮次,删除 fail-closed。
            foreach (var line in targets)
                if (!string.IsNullOrEmpty(line.Id)
                    && line.Id.StartsWith("arc-missing-", StringComparison.Ordinal)) return false;

            var affected = new HashSet<string>(StringComparer.Ordinal);
            var targetLineIds = new HashSet<string>(StringComparer.Ordinal);
            bool hasStableTarget = false, removedAny = false, persisted = false, archiveRewritten = true;
            string path = ConvPath();
            lock (GetFileLock(path))
            {
                if (_fileEpoch != CurrentFileEpoch(path)) return false;
                bool diskReliable;
                var disk = ReadDocumentRecoverable(path, preserveCorrupt: true,
                    JianghuYoulingPaths.CurrentWorldId, _taiwuId, _key, ExpectedMemberIds(),
                    out diskReliable);
                if (!diskReliable) { _transcriptReliable = false; return false; }
                // 追平段计数前必须先剔除本页签内存里的已封存行(见 ReconcileArchivedSegments):
                // 裸同步会让稍后 SaveTranscript 的去重防线失效,把整段旧行重新写回活跃文档。
                if (!ReconcileArchivedSegments(disk)) return false;
                EnsureArchiveLoaded();
                var sharedDeleted = DeletedLines(path);
                foreach (var line in targets)
                {
                    if (!string.IsNullOrEmpty(line.ExchangeId)) affected.Add(line.ExchangeId);
                    if (!string.IsNullOrEmpty(line.Id))
                    {
                        hasStableTarget = true;
                        targetLineIds.Add(line.Id);
                    }
                }

                // UI 只持有可见行；先用稳定 Id 从内存、完整磁盘与归档段补回 ExchangeId，再按
                // exchange 扩展全部可见/hidden receipt 行。这个扩展、cleanup enqueue 和删除提交必须同锁。
                foreach (var source in new[] { _transcript, disk?.Lines, _archivedLines })
                {
                    if (source == null) continue;
                    foreach (var line in source)
                        if (line != null && !string.IsNullOrEmpty(line.Id) && targetLineIds.Contains(line.Id)
                            && !string.IsNullOrEmpty(line.ExchangeId)) affected.Add(line.ExchangeId);
                }
                if (affected.Count == 0 && targetLineIds.Count == 0) return false;

                var completeExchange = new List<Line>();
                foreach (var source in new[] { _transcript, disk?.Lines, _archivedLines, targets })
                {
                    if (source == null) continue;
                    foreach (var line in source)
                    {
                        if (line == null) continue;
                        bool match = (!string.IsNullOrEmpty(line.ExchangeId) && affected.Contains(line.ExchangeId))
                            || (!string.IsNullOrEmpty(line.Id) && targetLineIds.Contains(line.Id));
                        if (!match) continue;
                        if (!string.IsNullOrEmpty(line.Id) && completeExchange.Exists(x => x != null && x.Id == line.Id)) continue;
                        completeExchange.Add(line);
                    }
                }
                // 有段不可读时,无法证明目标轮不在其中;只放行在活跃文档或可读段里确凿
                // 定位到的轮,其余 fail-closed,绝不假删。
                if (AnyArchiveSegmentUnreadable())
                {
                    var located = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var source in new[] { _transcript, disk?.Lines, _archivedLines })
                    {
                        if (source == null) continue;
                        foreach (var line in source)
                            if (line != null && !string.IsNullOrEmpty(line.ExchangeId)) located.Add(line.ExchangeId);
                    }
                    foreach (string exchangeId in affected)
                        if (!located.Contains(exchangeId)) return false;
                }
                EnqueueLinkedMemoryRemovals(completeExchange);
                foreach (string exchangeId in affected) _pendingMemoryRemovalExchangeIds.Add(exchangeId);
                foreach (var line in completeExchange)
                    if (!string.IsNullOrEmpty(line.Id))
                    {
                        hasStableTarget = true;
                        _deletedLineIds.Add(line.Id);
                        sharedDeleted.Add(line.Id);
                    }
                foreach (string id in targetLineIds) { _deletedLineIds.Add(id); sharedDeleted.Add(id); }

                int removed = _transcript.RemoveAll(current => current != null
                    && ((!string.IsNullOrEmpty(current.ExchangeId) && affected.Contains(current.ExchangeId))
                        || (!string.IsNullOrEmpty(current.Id) && targetLineIds.Contains(current.Id))));
                removedAny = removed > 0;

                // 记下确证驻留在归档段中的目标轮:重写时这些轮必须在可读段中被定位,
                // 否则(段在读取与重写之间被锁住/损坏)不能报删除成功。
                var archiveResident = new HashSet<string>(StringComparer.Ordinal);
                foreach (var line in _archivedLines)
                    if (line != null && !string.IsNullOrEmpty(line.ExchangeId)
                        && affected.Contains(line.ExchangeId)) archiveResident.Add(line.ExchangeId);
                // SaveTranscript 在同一个可重入文件锁内把 pending cleanup 与整轮删除原子提交。
                // 写失败时磁盘仍保留原轮，pending 的内存/tombstone 也保留供 UI 重试。
                persisted = SaveTranscript();
                // 删除意图(PendingMemoryRefresh journal)已随活跃文档 durable 提交,此后才
                // 允许重写归档段;段重写失败即返回 false 不假删,意图保留供幂等重试。
                if (persisted) archiveRewritten = TryRewriteArchiveSegmentsRemoving(affected, targetLineIds, archiveResident);
            }
            // Stable Id 也可能已在第一次尝试中从内存移除、但保存/记忆撤回失败；仍须幂等重试。
            if (!hasStableTarget && !removedAny) return false;
            if (!persisted) return false;
            if (!archiveRewritten) return false;
            if (!RefreshMemoryForExchanges(affected) || !RemovePendingLinkedMemories()) return false;
            MarkMemoryRefreshCompleted(affected);
            if (SaveTranscript()) return true;
            foreach (string exchangeId in affected) _pendingMemoryRemovalExchangeIds.Add(exchangeId);
            return false;
        }

        /// <summary>把场景图绑定到一条仍在当前群聊活跃文档中的 NPC 发言。</summary>
        internal bool SetLineImage(string lineId, string imageFileName,
            out string previousFileName)
        {
            previousFileName = null;
            if (!_transcriptReliable || !WorldLifecycle.IsSameWorld(_worldGeneration)
                || !HasCurrentFileEpoch() || string.IsNullOrWhiteSpace(lineId)
                || !ChatImageReference.IsValid(imageFileName)) return false;
            string path = ConvPath();
            lock (GetFileLock(path))
            {
                if (_fileEpoch != CurrentFileEpoch(path)) return false;
                Line target = _transcript?.Find(line => line != null && !line.IsTaiwu
                    && string.Equals(line.Id, lineId, StringComparison.Ordinal));
                if (target == null) return false;
                previousFileName = target.ImageFileName;
                if (string.Equals(previousFileName, imageFileName, StringComparison.Ordinal)) return true;
                target.ImageFileName = imageFileName;
                if (SaveTranscript()) return true;
                target.ImageFileName = previousFileName;
                previousFileName = null;
                return false;
            }
        }

        private GroupDocument MakeDocument(List<Line> lines, long revision,
            bool? memoryProjectionPending = null)
        {
            var contextProjectionIds = new List<string>(_pendingContextProjectionExchangeIds);
            contextProjectionIds.Sort(StringComparer.Ordinal);
            bool projectionPending = memoryProjectionPending
                ?? contextProjectionIds.Count > 0;
            if (!projectionPending) contextProjectionIds.Clear();
            var doc = new GroupDocument
            {
                Version = CurrentGroupDocumentVersion,
                WorldId = JianghuYoulingPaths.CurrentWorldId,
                TaiwuId = _taiwuId,
                GroupId = _key,
                Revision = revision,
                Lines = lines ?? new List<Line>(),
                MemoryProjectionPending = projectionPending,
                PendingContextProjectionExchangeIds = contextProjectionIds,
                PendingMemoryRefreshExchangeIds = new List<string>(_pendingMemoryRemovalExchangeIds),
                ArchivedSegmentCount = _archivedSegmentCount,
                ArchiveSegmentIndex = 0,
            };
            foreach (var pair in _pendingLinkedMemoryRemovals)
                if (pair.Value != null && pair.Value.Count > 0)
                    doc.PendingLinkedMemoryRemovals.Add(new LinkedMemoryRemoval
                    {
                        NpcId = pair.Key,
                        MemoryIds = new List<string>(pair.Value),
                    });
            foreach (var m in _members) if (m != null) doc.Members.Add(new MemberRef { Id = m.Id, Name = m.Name });
            return doc;
        }

        private void PrunePendingContextProjectionsWithoutLines()
        {
            if (_pendingContextProjectionExchangeIds.Count == 0) return;
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (Line line in _transcript ?? new List<Line>())
                if (line != null && !string.IsNullOrWhiteSpace(line.ExchangeId))
                    present.Add(line.ExchangeId);
            _pendingContextProjectionExchangeIds.RemoveWhere(id => !present.Contains(id));
            _memoryProjectionPending = _pendingContextProjectionExchangeIds.Count > 0;
        }

        /// <summary>
        /// main/tmp/bak 都可能来自一次已完成或中断的原子写。逐个做结构、身份和成员语义校验，
        /// 选择 Revision 最高者（同 Revision 时 main 优先），再把恢复结果提升回 main。
        /// </summary>
        private static GroupDocument ReadDocumentIndexSnapshot(string path,
            uint expectedWorldId, int expectedTaiwuId)
        {
            lock (GetFileLock(path))
            {
                string[] candidates = { path, path + ".tmp", path + ".bak" };
                var valid = new List<RecoveredDocument>();
                RecoveredDocument main = null;
                for (int i = 0; i < candidates.Length; i++)
                {
                    try
                    {
                        if (!File.Exists(candidates[i])) continue;
                        GroupDocument document = ParseAndValidateDocument(
                            ReadTranscriptBytesChecked(candidates[i]), expectedWorldId,
                            expectedTaiwuId, null, null);
                        var recovered = new RecoveredDocument
                        {
                            Path = candidates[i],
                            Rank = candidates.Length - i,
                            Document = document,
                        };
                        valid.Add(recovered);
                        if (i == 0) main = recovered;
                    }
                    catch { }
                }

                RecoveredDocument best = main;
                if (main != null && main.Document.Version < CurrentGroupDocumentVersion)
                    foreach (RecoveredDocument candidate in valid)
                    {
                        if (candidate.Document.Version != CurrentGroupDocumentVersion
                            || candidate.Document.Revision <= main.Document.Revision) continue;
                        int copies = 0;
                        foreach (RecoveredDocument other in valid)
                            if (SameGroupDocument(candidate.Document, other.Document)) copies++;
                        if (copies >= 2 && (best == main
                            || candidate.Document.Revision > best.Document.Revision))
                            best = candidate;
                    }
                else if (main == null)
                    foreach (RecoveredDocument candidate in valid)
                    {
                        int copies = 0;
                        foreach (RecoveredDocument other in valid)
                            if (SameGroupDocument(candidate.Document, other.Document)) copies++;
                        if (copies < 2) continue;
                        if (best == null || candidate.Document.Revision > best.Document.Revision
                            || candidate.Document.Revision == best.Document.Revision
                                && candidate.Rank > best.Rank)
                            best = candidate;
                    }
                return best?.Document;
            }
        }

        private static GroupDocument ReadDocumentRecoverable(string path, bool preserveCorrupt,
            uint expectedWorldId = 0, int expectedTaiwuId = 0, string expectedGroupId = null,
            HashSet<int> expectedMembers = null)
        {
            bool ignored;
            return ReadDocumentRecoverable(path, preserveCorrupt, expectedWorldId, expectedTaiwuId,
                expectedGroupId, expectedMembers, out ignored);
        }

        private static GroupDocument ReadDocumentRecoverable(string path, bool preserveCorrupt,
            uint expectedWorldId, int expectedTaiwuId, string expectedGroupId,
            HashSet<int> expectedMembers, out bool reliable)
        {
            reliable = false;
            lock (GetFileLock(path))
            {
                string[] candidates = { path, path + ".tmp", path + ".bak" };
                RecoveredDocument best = null;
                RecoveredDocument main = null;
                var valid = new List<RecoveredDocument>();
                var validRaw = new List<byte[]>();
                var invalid = new List<string>();
                bool hadCandidates = false;
                for (int i = 0; i < candidates.Length; i++)
                {
                    string candidate = candidates[i];
                    if (!File.Exists(candidate)) continue;
                    hadCandidates = true;
                    try
                    {
                        byte[] raw = ReadTranscriptBytesChecked(candidate);
                        // 与某个已通过完整语义校验的候选逐字节相同 ⇒ 解析结果必然相同；
                        // 正常提交后 main/tmp/bak 三份同字节，只需解析一次，不放大主线程成本。
                        GroupDocument doc = null;
                        for (int v = 0; v < valid.Count; v++)
                            if (BytesEqual(raw, validRaw[v])) { doc = valid[v].Document; break; }
                        if (doc == null)
                            doc = ParseAndValidateDocument(raw, expectedWorldId, expectedTaiwuId,
                                expectedGroupId, expectedMembers);
                        int rank = candidates.Length - i; // main > tmp > bak on equal Revision
                        var recovered = new RecoveredDocument { Path = candidate, Rank = rank, Document = doc };
                        valid.Add(recovered);
                        validRaw.Add(raw);
                        if (i == 0) main = recovered;
                    }
                    catch (Exception ex)
                    {
                        invalid.Add(candidate);
                        Debug.LogWarning("[江湖有灵] 群聊候选文件无效 " + Path.GetFileName(candidate)
                            + ":" + ex.GetType().Name);
                    }
                }

                if (main != null)
                {
                    best = main;
                    // Legacy main files have no current integrity digest. A matching
                    // two-replica current-version quorum at a strictly newer revision is
                    // evidence of a completed migration whose main was later rolled back.
                    if (main.Document.Version < CurrentGroupDocumentVersion)
                        foreach (RecoveredDocument candidate in valid)
                        {
                            if (candidate.Document.Version != CurrentGroupDocumentVersion
                                || candidate.Document.Revision <= main.Document.Revision) continue;
                            int copies = 0;
                            foreach (RecoveredDocument other in valid)
                                if (SameGroupDocument(candidate.Document, other.Document)) copies++;
                            if (copies >= 2 && (best == main
                                || candidate.Document.Revision > best.Document.Revision)) best = candidate;
                        }
                }
                else
                    foreach (RecoveredDocument candidate in valid)
                    {
                        int copies = 0;
                        foreach (RecoveredDocument other in valid)
                            if (SameGroupDocument(candidate.Document, other.Document)) copies++;
                        if (copies < 2) continue;
                        if (best == null || candidate.Document.Revision > best.Document.Revision
                            || candidate.Document.Revision == best.Document.Revision && candidate.Rank > best.Rank)
                            best = candidate;
                    }

                if (best == null)
                {
                    reliable = !hadCandidates;
                    return null;
                }
                int matchingCopies = 0;
                foreach (RecoveredDocument candidate in valid)
                    if (SameGroupDocument(best.Document, candidate.Document)) matchingCopies++;
                if (preserveCorrupt)
                    foreach (string candidate in invalid)
                        if (!TryArchiveCorruptCandidate(candidate))
                        {
                            reliable = false;
                            return null;
                        }
                if (!string.Equals(best.Path, path, StringComparison.OrdinalIgnoreCase)
                    || best.Document.Version == CurrentGroupDocumentVersion && matchingCopies < 2)
                {
                    try
                    {
                        WriteDocumentAtomic(path, best.Document, ".recover-" + Guid.NewGuid().ToString("N") + ".tmp",
                            preserveVersion: true);
                    }
                    catch (Exception ex)
                    {
                        // 已验证的候选仍可供本次读取；保留原件，下次继续尝试提升。
                        Debug.LogWarning("[江湖有灵] 群聊恢复文件提升失败:" + ex.GetType().Name);
                        reliable = false;
                        return null;
                    }
                }
                reliable = true;
                return best.Document;
            }
        }

        private static GroupDocument ParseAndValidateDocument(string candidate, uint expectedWorldId,
            int expectedTaiwuId, string expectedGroupId, HashSet<int> expectedMembers,
            int expectedArchiveSegmentIndex = 0)
        {
            return ParseAndValidateDocument(ReadTranscriptBytesChecked(candidate), expectedWorldId,
                expectedTaiwuId, expectedGroupId, expectedMembers, expectedArchiveSegmentIndex);
        }

        // expectedArchiveSegmentIndex=0 校验活跃文档(拒收段文档);>=1 校验对应段号的归档段,
        // 段号取自文件名,借此保证"段 index 与文件名一致"。
        private static GroupDocument ParseAndValidateDocument(byte[] raw, uint expectedWorldId,
            int expectedTaiwuId, string expectedGroupId, HashSet<int> expectedMembers,
            int expectedArchiveSegmentIndex = 0)
        {
            var token = ReadStrictJsonToken(raw);
            RejectCaseInsensitiveDuplicateProperties(token);
            if (token.Type == JTokenType.Array)
            {
                if (expectedArchiveSegmentIndex > 0)
                    throw new JsonException("归档段不接受无身份的旧版数组");
                if (!string.IsNullOrWhiteSpace(expectedGroupId))
                    throw new JsonException("canonical 群聊文档不接受无身份的旧版数组");
                ValidateLineArray((JArray)token, 1);
                return new GroupDocument { Version = 1, Revision = 0,
                    Lines = token.ToObject<List<Line>>() ?? new List<Line>() };
            }
            if (token.Type != JTokenType.Object) throw new JsonException("群聊文档不是对象或旧版数组");
            var obj = (JObject)token;
            var linesToken = obj["Lines"] ?? obj["lines"];
            if (linesToken?.Type != JTokenType.Array) throw new JsonException("群聊文档缺少 Lines 数组");
            JToken versionToken = obj["Version"] ?? obj["version"];
            if (versionToken?.Type != JTokenType.Integer)
                throw new JsonException("群聊文档缺少显式整数 Version");
            int version = versionToken.Value<int>();
            if (version < 1 || version > CurrentGroupDocumentVersion)
                throw new JsonException("不支持的群聊文档版本 " + version);
            if (!string.IsNullOrWhiteSpace(expectedGroupId) && version < 3)
                throw new JsonException("canonical 群聊文档版本过旧，缺少强身份绑定");
            ValidateLineArray((JArray)linesToken, version);
            if (version >= 4) ValidateV4TransactionJournals(obj);
            if (version >= 6) ValidateV6MemoryProjectionBarrier(obj);
            if (version >= 7) ValidateV7ArchiveSegmentFields(obj);
            if (version >= 8) ValidateV8ContextProjectionQueue(obj);

            var doc = token.ToObject<GroupDocument>();
            if (doc == null) throw new JsonException("群聊文档反序列化为空");
            doc.Version = version;
            if (doc.Lines == null) doc.Lines = new List<Line>();
            if (doc.Members == null) doc.Members = new List<MemberRef>();
            if (doc.PendingMemoryRefreshExchangeIds == null) doc.PendingMemoryRefreshExchangeIds = new List<string>();
            if (doc.PendingLinkedMemoryRemovals == null) doc.PendingLinkedMemoryRemovals = new List<LinkedMemoryRemoval>();
            if (doc.PendingContextProjectionExchangeIds == null)
                doc.PendingContextProjectionExchangeIds = new List<string>();
            if (doc.Revision < 0) throw new JsonException("Revision 不能为负数");
            if (doc.ArchivedSegmentCount < 0 || doc.ArchiveSegmentIndex < 0)
                throw new JsonException("归档段计数不能为负数");
            if (expectedArchiveSegmentIndex > 0)
            {
                if (doc.ArchiveSegmentIndex != expectedArchiveSegmentIndex)
                    throw new JsonException("归档段 index 与文件名不一致");
                if (doc.ArchivedSegmentCount != 0)
                    throw new JsonException("归档段自身不得再声明归档段");
            }
            else if (doc.ArchiveSegmentIndex != 0)
                throw new JsonException("活跃群聊文档不接受归档段文档");

            if (version >= 3)
            {
                if (doc.WorldId == 0) throw new JsonException("v3 群聊文档缺少 WorldId");
                if (doc.TaiwuId <= 0) throw new JsonException("v3 群聊文档缺少 TaiwuId");
                if (string.IsNullOrWhiteSpace(doc.GroupId)) throw new JsonException("v3 群聊文档缺少 GroupId");
                if (doc.Revision <= 0) throw new JsonException("v3 群聊文档缺少有效 Revision");
                if (doc.Members.Count == 0) throw new JsonException("v3 群聊文档缺少成员");
            }
            if (expectedWorldId > 0 && doc.WorldId != 0 && doc.WorldId != expectedWorldId)
                throw new JsonException("WorldId 不匹配");
            if (expectedTaiwuId > 0 && doc.TaiwuId != 0 && doc.TaiwuId != expectedTaiwuId)
                throw new JsonException("TaiwuId 不匹配");
            if (!string.IsNullOrWhiteSpace(expectedGroupId) && !string.IsNullOrWhiteSpace(doc.GroupId)
                && !string.Equals(doc.GroupId, expectedGroupId, StringComparison.Ordinal))
                throw new JsonException("GroupId 不匹配");

            var actualMembers = new HashSet<int>();
            foreach (var member in doc.Members)
            {
                if (member == null || member.Id <= 0 || !actualMembers.Add(member.Id))
                    throw new JsonException("群聊成员列表无效或重复");
            }
            if (expectedMembers != null && actualMembers.Count > 0 && !actualMembers.SetEquals(expectedMembers))
                throw new JsonException("群聊成员集合不匹配");
            if (version >= 5)
            {
                string computed = ComputeGroupDocumentIntegrity(doc);
                if (!ValidGroupSha256(doc.IntegritySha256)
                    || !string.Equals(doc.IntegritySha256, computed, StringComparison.Ordinal))
                    throw new JsonException("群聊文档完整性摘要不匹配");
            }
            return doc;
        }

        private static bool DocumentNeedsMemoryProjection(GroupDocument document)
            => PendingContextProjectionIds(document).Count > 0;

        private static List<string> PendingContextProjectionIds(GroupDocument document)
        {
            var result = new List<string>();
            // v7 and earlier used a single boolean and cannot identify which failed exchange
            // still needs work. Per product decision, do not retroactively process those old
            // failures; only v8 per-exchange obligations are recoverable.
            if (document == null || document.Version < 8
                || document.PendingContextProjectionExchangeIds == null) return result;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in document.PendingContextProjectionExchangeIds)
                if (!string.IsNullOrWhiteSpace(id) && seen.Add(id)) result.Add(id);
            return result;
        }

        private void LoadPendingContextProjections(GroupDocument document)
        {
            _pendingContextProjectionExchangeIds.Clear();
            foreach (string id in PendingContextProjectionIds(document))
                _pendingContextProjectionExchangeIds.Add(id);
            _memoryProjectionPending = _pendingContextProjectionExchangeIds.Count > 0;
        }

        private void MergePendingContextProjections(GroupDocument document)
        {
            foreach (string id in PendingContextProjectionIds(document))
                _pendingContextProjectionExchangeIds.Add(id);
            _memoryProjectionPending = _pendingContextProjectionExchangeIds.Count > 0;
        }

        private static void RejectCaseInsensitiveDuplicateProperties(JToken token)
        {
            if (token is JObject obj)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JProperty property in obj.Properties())
                {
                    if (!names.Add(property.Name))
                        throw new JsonException("群聊文档含大小写碰撞字段:" + property.Name);
                    RejectCaseInsensitiveDuplicateProperties(property.Value);
                }
                return;
            }
            if (token is JArray array)
                foreach (JToken child in array) RejectCaseInsensitiveDuplicateProperties(child);
        }

        private static void ValidateV4TransactionJournals(JObject document)
        {
            JToken refresh = document?["PendingMemoryRefreshExchangeIds"]
                ?? document?["pendingMemoryRefreshExchangeIds"];
            JToken linked = document?["PendingLinkedMemoryRemovals"]
                ?? document?["pendingLinkedMemoryRemovals"];
            if (refresh?.Type != JTokenType.Array || linked?.Type != JTokenType.Array)
                throw new JsonException("v4 群聊文档缺少持久化记忆清理事务数组");

            var refreshIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (JToken item in (JArray)refresh)
            {
                string id = item?.Type == JTokenType.String ? item.Value<string>() : null;
                if (string.IsNullOrWhiteSpace(id) || !refreshIds.Add(id))
                    throw new JsonException("v4 群聊记忆刷新事务 ID 无效或重复");
            }

            var npcIds = new HashSet<int>();
            foreach (JToken item in (JArray)linked)
            {
                if (item?.Type != JTokenType.Object) throw new JsonException("v4 群聊关联记忆事务不是对象");
                var entry = (JObject)item;
                JToken npcToken = entry["NpcId"] ?? entry["npcId"];
                JToken memoryToken = entry["MemoryIds"] ?? entry["memoryIds"];
                int npcId = npcToken?.Type == JTokenType.Integer ? npcToken.Value<int>() : 0;
                if (npcId < 0 || !npcIds.Add(npcId) || memoryToken?.Type != JTokenType.Array
                    || !memoryToken.HasValues)
                    throw new JsonException("v4 群聊关联记忆事务身份或数组无效");
                var memoryIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (JToken memory in (JArray)memoryToken)
                {
                    string id = memory?.Type == JTokenType.String ? memory.Value<string>() : null;
                    if (string.IsNullOrWhiteSpace(id) || !memoryIds.Add(id))
                        throw new JsonException("v4 群聊关联记忆 ID 无效或重复");
                }
            }
        }

        private static void ValidateV6MemoryProjectionBarrier(JObject document)
        {
            JToken pending = document?["MemoryProjectionPending"]
                ?? document?["memoryProjectionPending"];
            if (pending?.Type != JTokenType.Boolean)
                throw new JsonException("v6 群聊文档缺少记忆投影提交屏障");
        }

        private static void ValidateV7ArchiveSegmentFields(JObject document)
        {
            JToken count = document?["ArchivedSegmentCount"] ?? document?["archivedSegmentCount"];
            JToken index = document?["ArchiveSegmentIndex"] ?? document?["archiveSegmentIndex"];
            if (count?.Type != JTokenType.Integer || index?.Type != JTokenType.Integer)
                throw new JsonException("v7 群聊文档缺少显式归档段字段");
        }

        private static void ValidateLineArray(JArray lines, int version)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in lines)
            {
                if (item?.Type != JTokenType.Object) throw new JsonException("群聊 Lines 含非对象条目");
                var line = (JObject)item;
                var text = line["Text"] ?? line["text"];
                if (text?.Type != JTokenType.String) throw new JsonException("群聊行缺少文本");
                if (version >= 3)
                {
                    string id = (line["Id"] ?? line["id"])?.Value<string>();
                    string exchangeId = (line["ExchangeId"] ?? line["exchangeId"])?.Value<string>();
                    if (string.IsNullOrWhiteSpace(id) || !ids.Add(id)) throw new JsonException("v3 群聊行 ID 缺失或重复");
                    if (string.IsNullOrWhiteSpace(exchangeId)) throw new JsonException("v3 群聊行缺少 ExchangeId");
                }
                if (version >= 4)
                {
                    JToken isTaiwuToken = line["IsTaiwu"] ?? line["isTaiwu"];
                    JToken dateToken = line["Date"] ?? line["date"];
                    JToken attemptToken = line["CommitAttemptId"] ?? line["commitAttemptId"];
                    JToken memoriesToken = line["MemoryIds"] ?? line["memoryIds"];
                    JToken actionsToken = line["Actions"] ?? line["actions"];
                    if (isTaiwuToken?.Type != JTokenType.Boolean || dateToken?.Type != JTokenType.Integer)
                        throw new JsonException("v4 群聊行缺少发言身份或游戏日期");
                    if (!isTaiwuToken.Value<bool>()
                        && (attemptToken?.Type != JTokenType.String || string.IsNullOrWhiteSpace(attemptToken.Value<string>())))
                        throw new JsonException("v4 NPC 群聊行缺少 CommitAttemptId");
                    if (memoriesToken == null || (memoriesToken.Type != JTokenType.Null && memoriesToken.Type != JTokenType.Array)
                        || actionsToken == null || (actionsToken.Type != JTokenType.Null && actionsToken.Type != JTokenType.Array))
                        throw new JsonException("v4 群聊行缺少 memory/action 事务字段");
                }
                JToken imageToken = line["ImageFileName"] ?? line["imageFileName"];
                if (imageToken != null && imageToken.Type != JTokenType.Null
                    && (imageToken.Type != JTokenType.String
                        || !ChatImageReference.IsValid(imageToken.Value<string>())))
                    throw new JsonException("群聊图片引用无效");
            }
        }

        private static void WriteDocumentAtomic(string path, GroupDocument document, string tempSuffix,
            bool preserveVersion = false)
        {
            string tmp = path + tempSuffix;
            string replicaPath = path + ".tmp";
            string promote = path + ".publish-" + Guid.NewGuid().ToString("N") + ".tmp";
            // preserveVersion=恢复提升专用:副本仲裁出的旧版本文档必须原样写回。强升当前
            // 版本会绕过 LoadTranscript 迁移路径的行归一化与投影屏障置位——v1-3 行缺合成
            // 身份会让新版本校验永久失败,v4/v5 则会静默丢掉"迁移时投影一次"的义务。
            if (!preserveVersion) document.Version = CurrentGroupDocumentVersion;
            document.IntegritySha256 = document.Version >= 5 ? ComputeGroupDocumentIntegrity(document) : null;
            if (document.Version >= 5 && !ValidGroupSha256(document.IntegritySha256))
                throw new IOException("群聊文档完整性摘要生成失败");
            string json = JsonConvert.SerializeObject(document, Formatting.Indented);
            byte[] payload = StrictUtf8.GetBytes(json);
            ConversationSessionIndexStore.Mutation indexMutation =
                IsActiveGroupDocument(path, document)
                    ? ConversationSessionIndexStore.BeginGroupMutation(document.TaiwuId)
                    : null;
            bool stagedVerified = false;
            try
            {
                WriteDurableUtf8(tmp, json);
                // 完整语义读回只需在 tmp 上做一次：其余副本写入的是同一份序列化字节，
                // 回读逐字节等于 payload 即与已验证的 tmp 语义一致，不必在主线程重复全文解析。
                var staged = ParseAndValidateDocument(tmp, document.WorldId, document.TaiwuId,
                    document.GroupId, MemberIds(document));
                if (!SameGroupDocument(staged, document))
                    throw new IOException("群聊文档 tmp 完整语义读回不一致");
                VerifyReplicaBytes(tmp, payload, "群聊文档 tmp 字节读回不一致");
                stagedVerified = true;
                if (!string.Equals(tmp, replicaPath, StringComparison.OrdinalIgnoreCase))
                {
                    WriteDurableUtf8(replicaPath, json);
                    VerifyReplicaBytes(replicaPath, payload, "群聊文档同 revision 冗余副本不一致");
                }
                WriteDurableUtf8(promote, json);
                // promotion 副本必须在顶替 main 之前验完：坏字节一旦经 File.Replace 发布，
                // 唯一权威提交点已被污染，事后校验只能报错而无法回退。
                VerifyReplicaBytes(promote, payload, "群聊文档 promotion 读回不一致");
                if (File.Exists(path)) File.Replace(promote, path, path + ".bak", true);
                else File.Move(promote, path);

                VerifyReplicaBytes(path, payload, "群聊文档 main 提交读回不一致");
                VerifyReplicaBytes(replicaPath, payload, "群聊文档提交后冗余副本不一致");
                WriteDurableUtf8(path + ".bak", json);
                VerifyReplicaBytes(path + ".bak", payload, "群聊文档备份副本不一致");
                if (QueueGroupConversationIndex(document, path, indexMutation))
                    indexMutation = null; // background queue owns the dirty-marker lease
            }
            catch
            {
                // Publication may already have succeeded and only the final backup
                // refresh/read-back failed. main plus the independently flushed replica
                // prove the exact committed document; surfacing false here would make
                // callers roll back or redispatch around an already published revision.
                try
                {
                    var committed = ParseAndValidateDocument(path, document.WorldId,
                        document.TaiwuId, document.GroupId, MemberIds(document));
                    var replica = ParseAndValidateDocument(replicaPath, document.WorldId,
                        document.TaiwuId, document.GroupId, MemberIds(document));
                    if (SameGroupDocument(committed, document)
                        && SameGroupDocument(replica, document))
                    {
                        if (QueueGroupConversationIndex(document, path, indexMutation))
                            indexMutation = null;
                        return;
                    }
                }
                catch { }
                if (!stagedVerified) try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
            finally { indexMutation?.Dispose(); }
        }

        private static bool IsActiveGroupDocument(string path, GroupDocument document)
        {
            if (document == null || document.TaiwuId <= 0 || document.ArchiveSegmentIndex != 0
                || string.IsNullOrWhiteSpace(document.GroupId) || string.IsNullOrWhiteSpace(path))
                return false;
            return string.Equals(Path.GetFileName(path), "Group_" + document.GroupId + ".json",
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool QueueGroupConversationIndex(GroupDocument document, string path,
            ConversationSessionIndexStore.Mutation mutation)
        {
            if (document == null || mutation == null) return false;
            var entry = new ConversationSessionIndexStore.GroupEntry
            {
                GroupId = document.GroupId,
                ArchivedSegmentCount = Math.Max(0, document.ArchivedSegmentCount),
            };
            var memberIds = new HashSet<int>();
            if (document.Members != null)
                foreach (MemberRef member in document.Members)
                    if (member != null && member.Id > 0 && memberIds.Add(member.Id))
                        entry.Members.Add(new ConversationSessionIndexStore.MemberEntry
                        {
                            Id = member.Id,
                            Name = member.Name,
                        });
            List<Line> active = VisibleLines(document.Lines);
            entry.HasActiveLines = active.Count > 0;
            if (entry.Members.Count == 0)
                foreach (Line line in active)
                    if (line != null && !line.IsTaiwu && line.SpeakerId >= 0
                        && memberIds.Add(line.SpeakerId))
                        entry.Members.Add(new ConversationSessionIndexStore.MemberEntry
                        {
                            Id = line.SpeakerId,
                            Name = line.Speaker,
                        });
            DateTime activity = LatestContentOrder(active);
            if (activity <= ContentOrderEpochUtc && document.ArchivedSegmentCount <= 0)
                activity = DocumentLastWriteUtc(path);
            entry.LastActivityUtcTicks = activity > ContentOrderEpochUtc ? activity.Ticks : 0;
            return ConversationSessionIndexStore.QueueUpsertGroup(mutation,
                document.WorldId, Path.GetDirectoryName(path), document.TaiwuId, entry);
        }

        /// <summary>
        /// 副本一致性按字节回读比对：与已通过完整语义校验的那份序列化字节逐字节相等，
        /// 证据强度等同再做一次全文解析校验，但不放大主线程的 JSON 解析成本。
        /// </summary>
        private static void VerifyReplicaBytes(string path, byte[] expected, string failure)
        {
            byte[] actual = File.ReadAllBytes(path);
            if (actual.Length != expected.Length) throw new IOException(failure);
            for (int i = 0; i < actual.Length; i++)
                if (actual[i] != expected[i]) throw new IOException(failure);
        }

        // —— 归档分段:活跃文档 + 不可变归档段(GroupArc_{key}_{n:D4}.json),群聊记录可无限增长 ——

        /// <summary>活跃文档超限时把最老的完整轮次封存进归档段。只在上下文投影屏障与来源记忆
        /// 清理事务全空后运行:被封存轮次的个人群聊上下文已 durable 确认,移动它们不改变任何记忆/动作
        /// 对账语义。任何一步失败都放弃本次归档,聊天功能不受影响,下一次屏障清空后再试。</summary>
        private void MaybeArchiveOldExchanges()
        {
            if (!_transcriptReliable || string.IsNullOrWhiteSpace(_key)) return;
            if (_memoryProjectionPending || _pendingMemoryRemovalExchangeIds.Count > 0
                || _pendingLinkedMemoryRemovals.Count > 0) return;
            if (_worldGeneration > 0 && !WorldLifecycle.IsSameWorld(_worldGeneration)) return;
            if (JianghuYoulingPaths.CurrentWorldId == 0) return;
            // 主副本长度低于阈值时直接跳过:归档判定不需要每次屏障提交都付一次
            // 全文档三副本读取+全文解析+全文序列化的固定成本。
            try
            {
                var info = new FileInfo(ConvPath());
                if (!info.Exists || info.Length <= ArchiveThresholdBytes) return;
            }
            catch { return; }
            // 迁移升级前留下的超大活跃文档需要多个段;每一趟独立崩溃安全。趟数每次调用
            // 有界,余量摊到后续屏障提交,避免超大旧文档首开在主线程单帧冻结数十秒。
            for (int pass = 0; pass < ArchiveMaxPassesPerCall; pass++)
                if (!TryArchiveOnePass()) break;
        }

        /// <summary>一趟归档:段先落盘并完整校验,活跃文档随后提交段边界。段写成功但活跃提交
        /// 失败/崩溃 ⇒ 段只是可被覆盖的未提交暂存,行仍完整留在活跃文档,无重复亦无丢失。</summary>
        private bool TryArchiveOnePass()
        {
            string path = ConvPath();
            try
            {
                lock (GetFileLock(path))
                {
                    if (_fileEpoch != CurrentFileEpoch(path)) return false;
                    bool reliable;
                    GroupDocument disk = ReadDocumentRecoverable(path, preserveCorrupt: true,
                        JianghuYoulingPaths.CurrentWorldId, _taiwuId, _key, ExpectedMemberIds(),
                        out reliable);
                    if (!reliable || disk?.Lines == null || disk.Lines.Count == 0
                        || disk.Members == null || disk.Members.Count == 0) return false;
                    // 屏障状态以磁盘为准:另一页签可能刚提交了新行或新的清理事务。
                    if (DocumentNeedsMemoryProjection(disk)
                        || (disk.PendingMemoryRefreshExchangeIds != null && disk.PendingMemoryRefreshExchangeIds.Count > 0)
                        || (disk.PendingLinkedMemoryRemovals != null && disk.PendingLinkedMemoryRemovals.Count > 0)) return false;
                    // 交换 journal 里任何未收尾的条目都指向仍可能需要恢复对账的轮次;把它们
                    // 封存出活跃文档会让恢复判定失明。journal 不可靠时放弃本趟。
                    HashSet<string> journalHeld = null;
                    if (_exchangeJournal != null)
                    {
                        var journalEntries = _exchangeJournal.Snapshot();
                        if (journalEntries == null) return false;
                        foreach (var entry in journalEntries)
                            if (entry != null && !string.IsNullOrWhiteSpace(entry.ExchangeId))
                                (journalHeld ?? (journalHeld = new HashSet<string>(StringComparer.Ordinal)))
                                    .Add(entry.ExchangeId);
                    }
                    byte[] payload = StrictUtf8.GetBytes(JsonConvert.SerializeObject(disk, Formatting.Indented));
                    if (payload.LongLength <= ArchiveThresholdBytes) return false;
                    // v1-4 旧行缺合成身份;段按当前版本校验,先归一化再封存。
                    NormalizeLines(disk.Lines);
                    var moved = SelectOldestExchangesForArchive(disk.Lines, payload.LongLength, journalHeld);
                    if (moved.Count == 0) return false;

                    int nextIndex = checked(disk.ArchivedSegmentCount + 1);
                    var segment = new GroupDocument
                    {
                        Version = CurrentGroupDocumentVersion,
                        WorldId = disk.WorldId,
                        TaiwuId = disk.TaiwuId,
                        GroupId = disk.GroupId,
                        Revision = 1,
                        Lines = moved,
                        MemoryProjectionPending = false,
                        ArchivedSegmentCount = 0,
                        ArchiveSegmentIndex = nextIndex,
                    };
                    foreach (MemberRef member in disk.Members)
                        segment.Members.Add(new MemberRef { Id = member.Id, Name = member.Name });
                    if (!WriteArchiveSegment(ArchiveSegmentPath(nextIndex), segment)) return false;

                    var movedIds = new HashSet<string>(StringComparer.Ordinal);
                    foreach (Line line in moved) if (!string.IsNullOrEmpty(line.Id)) movedIds.Add(line.Id);
                    disk.Lines.RemoveAll(line => line != null && !string.IsNullOrEmpty(line.Id)
                        && movedIds.Contains(line.Id));
                    disk.ArchivedSegmentCount = nextIndex;
                    disk.Revision = checked(disk.Revision + 1);
                    WriteDocumentAtomic(path, disk, ".tmp");
                    _transcript = disk.Lines;
                    _documentRevision = disk.Revision;
                    SyncArchivedSegmentCount(nextIndex);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[江湖有灵] 群聊归档本趟失败,已放弃(下次屏障清空后重试):" + ex.GetType().Name);
                return false;
            }
        }

        /// <summary>从最老轮次起选出整轮封存,直到活跃文档估计降至阈值以下或本段装满约一个
        /// 阈值。活跃文档至少保留 MinActiveVisibleLines 条可见行,绝不移动进行中的轮次;
        /// 遇到 journal 未收尾的轮次即停(段必须始终是历史的连续前缀,跳过会打乱回看顺序)。</summary>
        private List<Line> SelectOldestExchangesForArchive(List<Line> lines, long payloadBytes,
            HashSet<string> excludedExchanges)
        {
            var moved = new List<Line>();
            int visibleTotal = VisibleLines(lines).Count;
            long remaining = payloadBytes;
            long segmentBytes = 0;
            foreach (var pair in ExchangesInOrderOf(lines))
            {
                if (remaining <= ArchiveThresholdBytes || segmentBytes >= ArchiveThresholdBytes) break;
                if (!string.IsNullOrWhiteSpace(_currentExchangeId)
                    && string.Equals(pair.Key, _currentExchangeId, StringComparison.Ordinal)) break;
                if (excludedExchanges != null && excludedExchanges.Contains(pair.Key)) break;
                int exchangeVisible = VisibleLines(pair.Value).Count;
                if (visibleTotal - exchangeVisible < MinActiveVisibleLines) break;
                long exchangeBytes = 0;
                foreach (Line line in pair.Value) exchangeBytes += EstimateLineBytes(line);
                foreach (Line line in pair.Value) moved.Add(line);
                visibleTotal -= exchangeVisible;
                remaining -= exchangeBytes;
                segmentBytes += exchangeBytes;
            }
            return moved;
        }

        private static long EstimateLineBytes(Line line)
        {
            if (line == null) return 0;
            try { return StrictUtf8.GetByteCount(JsonConvert.SerializeObject(line, Formatting.Indented)) + 16; }
            catch { return 0; }
        }

        /// <summary>归档段使用 Core 的统一 current-commit 协议提交。main 是提交点，tmp 在
        /// 备份刷新失败时保留“精确当前版”证据；后续读取若看到 stale-bak/current-tmp 分歧会
        /// fail-closed，绝不会用删除前的旧段复活已删轮次。</summary>
        private static bool WriteArchiveSegment(string path, GroupDocument segment)
        {
            lock (GetFileLock(path))
            {
                try
                {
                    segment.Version = CurrentGroupDocumentVersion;
                    segment.IntegritySha256 = ComputeGroupDocumentIntegrity(segment);
                    if (!ValidGroupSha256(segment.IntegritySha256))
                        throw new IOException("群聊归档段完整性摘要生成失败");
                    string json = JsonConvert.SerializeObject(segment, Formatting.Indented);
                    HashSet<int> expectedMembers = MemberIds(segment);
                    Func<string, bool> validator = value => IsValidArchiveSegmentPayload(value,
                        segment.WorldId, segment.TaiwuId, segment.GroupId, expectedMembers,
                        segment.ArchiveSegmentIndex);
                    bool readable = DurableFileStore.TryReadRecoverableText(path,
                        MaxTranscriptDocumentBytes, validator, out _, out bool anyCandidate, out _);
                    if (anyCandidate && !readable) return false;
                    return DurableFileStore.TryWriteTextAtomic(path, json,
                        MaxTranscriptDocumentBytes, validator);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[江湖有灵] 群聊归档段写入失败:" + ex.GetType().Name);
                    return false;
                }
            }
        }

        private static bool IsValidArchiveSegmentPayload(string value, uint expectedWorldId,
            int expectedTaiwuId, string expectedGroupId, HashSet<int> expectedMembers,
            int expectedArchiveSegmentIndex)
        {
            try
            {
                byte[] payload = StrictUtf8.GetBytes(value ?? string.Empty);
                if (payload.Length <= 0 || payload.Length > MaxTranscriptDocumentBytes) return false;
                return ParseAndValidateDocument(payload, expectedWorldId, expectedTaiwuId,
                    expectedGroupId, expectedMembers, expectedArchiveSegmentIndex) != null;
            }
            catch { return false; }
        }

        private GroupDocument ReadArchiveSegment(int index)
            => ReadArchiveSegmentAt(ArchiveSegmentPath(index), index,
                JianghuYoulingPaths.CurrentWorldId, _taiwuId, _key, null);

        private static GroupDocument ReadArchiveSegmentIndexSnapshot(string path, int index,
            uint expectedWorldId, int expectedTaiwuId, string expectedGroupId)
        {
            lock (GetFileLock(path))
            {
                Func<string, bool> validator = value => IsValidArchiveSegmentPayload(value,
                    expectedWorldId, expectedTaiwuId, expectedGroupId, null, index);
                if (!DurableFileStore.TryReadRecoverableTextReadOnly(path,
                    MaxTranscriptDocumentBytes, validator, out string value, out _, out _))
                    return null;
                try
                {
                    return ParseAndValidateDocument(StrictUtf8.GetBytes(value), expectedWorldId,
                        expectedTaiwuId, expectedGroupId, null, index);
                }
                catch { return null; }
            }
        }

        /// <summary>段是不可变提交,main 权威、.bak 为冗余副本;两者都无法通过完整校验时返回
        /// null,由调用方决定占位呈现(渲染/导出)还是 fail-closed(删除)。</summary>
        private static GroupDocument ReadArchiveSegmentAt(string path, int index, uint expectedWorldId,
            int expectedTaiwuId, string expectedGroupId, HashSet<int> expectedMembers)
        {
            lock (GetFileLock(path))
            {
                Func<string, bool> validator = value => IsValidArchiveSegmentPayload(value,
                    expectedWorldId, expectedTaiwuId, expectedGroupId, expectedMembers, index);
                if (!DurableFileStore.TryReadRecoverableText(path, MaxTranscriptDocumentBytes,
                    validator, out string value, out bool anyCandidate, out _))
                {
                    if (anyCandidate)
                        Debug.LogWarning("[江湖有灵] 群聊归档段当前提交不可可靠判定 "
                            + Path.GetFileName(path));
                    return null;
                }
                try
                {
                    return ParseAndValidateDocument(StrictUtf8.GetBytes(value), expectedWorldId,
                        expectedTaiwuId, expectedGroupId, expectedMembers, index);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[江湖有灵] 群聊归档段候选无效 "
                        + Path.GetFileName(path) + ":" + ex.GetType().Name);
                    return null;
                }
            }
        }

        private string ArchiveSegmentPath(int index)
            => ArchiveSegmentPathFor(JianghuYoulingPaths.ChatLogs, _key, index);

        // "GroupArc_" 前缀刻意避开世界启动恢复扫描的 "Group_*.json*" 通配:段不携带事务
        // 日志与投影屏障,不参与 transcript 恢复,读者只认活跃文档声明的 1..N 段。
        private static string ArchiveSegmentPathFor(string directory, string groupId, int index)
            => Path.Combine(directory, "GroupArc_" + groupId + "_" + index.ToString("D4") + ".json");

        /// <summary>按活跃文档声明的段数把归档行读进只读缓存(旧→新)。个别段损坏时以一条
        /// 占位行呈现,绝不让整个群聊瘫痪;更高 index 的未提交暂存段一律忽略。</summary>
        private void EnsureArchiveLoaded()
        {
            if (_archivedLoaded) return;
            if (_archivedSegmentCount <= 0 || string.IsNullOrWhiteSpace(_key))
            {
                _archivedLines = new List<Line>();
                _archivedLoaded = true;
                return;
            }
            lock (GetFileLock(ConvPath()))
            {
                if (_archivedLoaded) return;
                var lines = new List<Line>();
                for (int index = 1; index <= _archivedSegmentCount; index++)
                {
                    GroupDocument segment = ReadArchiveSegment(index);
                    if (segment?.Lines == null)
                    {
                        lines.Add(MissingArchiveSegmentLine(index));
                        continue;
                    }
                    NormalizeLines(segment.Lines);
                    lines.AddRange(segment.Lines);
                }
                _archivedLines = lines;
                _archivedLoaded = true;
            }
        }

        // 不可读段的占位行:只进内存呈现,从不落盘;删除它会被 fail-closed 拒绝。
        private static Line MissingArchiveSegmentLine(int index)
            => new Line { Id = "arc-missing-" + index, ExchangeId = "arc-missing-" + index,
                SpeakerId = 0, Speaker = "系统", IsTaiwu = false,
                Text = "【第" + index + "卷群聊记录无法读取】", Date = 0, CreatedUtc = DateTime.UtcNow };

        private bool AnyArchiveSegmentUnreadable()
        {
            foreach (Line line in _archivedLines)
                if (line != null && !string.IsNullOrEmpty(line.Id)
                    && line.Id.StartsWith("arc-missing-", StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>收集 1..count 全部段内行 Id。任一段不可读即返回 false:调用方无法证明
        /// 某行未被封存时,必须拒绝合并写入,避免同一行在段与活跃文档中重复。</summary>
        private bool TryCollectArchivedLineIds(int segmentCount, out HashSet<string> lineIds)
        {
            lineIds = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 1; index <= segmentCount; index++)
            {
                GroupDocument segment = ReadArchiveSegment(index);
                if (segment?.Lines == null) return false;
                foreach (Line line in segment.Lines)
                    if (line != null && !string.IsNullOrEmpty(line.Id)) lineIds.Add(line.Id);
            }
            return true;
        }

        /// <summary>把整轮删除应用到承载这些轮次的归档段:段以 staged 原子重写整段实现删轮
        /// (段 Revision+1)。删除意图此刻已作为 PendingMemoryRefresh journal 持久化在活跃文档,
        /// 任何一步失败/崩溃都不会假删——段里仍在的行会在玩家重试删除时再次被移除。
        /// mustResolveExchanges:调用方已确证驻留在归档段里的目标轮。整轮只会驻留在唯一
        /// 一个段中;这些轮若没有在任何可读段中被定位,而又存在不可读段,则无法证明删除
        /// 已生效,必须返回 false(否则读取时段被锁住/损坏会造成"删除成功"的假象)。</summary>
        private bool TryRewriteArchiveSegmentsRemoving(HashSet<string> affectedExchanges, HashSet<string> lineIds,
            HashSet<string> mustResolveExchanges = null)
        {
            if (_archivedSegmentCount <= 0) return true;
            bool changed = false, allRewritten = true, anyUnreadable = false;
            var unresolved = mustResolveExchanges == null
                ? null : new HashSet<string>(mustResolveExchanges, StringComparer.Ordinal);
            for (int index = 1; index <= _archivedSegmentCount; index++)
            {
                GroupDocument segment = ReadArchiveSegment(index);
                if (segment?.Lines == null) { anyUnreadable = true; continue; }
                if (unresolved != null && unresolved.Count > 0)
                    foreach (Line line in segment.Lines)
                        if (line != null && !string.IsNullOrEmpty(line.ExchangeId)) unresolved.Remove(line.ExchangeId);
                int removed = segment.Lines.RemoveAll(line => line != null
                    && ((!string.IsNullOrEmpty(line.ExchangeId) && affectedExchanges.Contains(line.ExchangeId))
                        || (!string.IsNullOrEmpty(line.Id) && lineIds.Contains(line.Id))));
                if (removed <= 0) continue;
                segment.Revision = checked(segment.Revision + 1);
                if (!WriteArchiveSegment(ArchiveSegmentPath(index), segment)) { allRewritten = false; break; }
                changed = true;
            }
            if (anyUnreadable && unresolved != null && unresolved.Count > 0) allRewritten = false;
            if (changed)
            {
                _archivedLoaded = false;
                _archivedLines = new List<Line>();
            }
            return allRewritten;
        }

        /// <summary>磁盘声明的段数领先本页签时,先把已封存行从本页签内存剔除、再同步计数。
        /// 任何写路径都必须经此收敛:裸同步计数会绕过 SaveTranscript 的去重防线,让陈旧
        /// 页签把整段旧行重新合并回活跃文档(跨段/活跃永久重复并自我放大);段不可读时
        /// 无法证明哪些行已封存,整体 fail-closed。</summary>
        private bool ReconcileArchivedSegments(GroupDocument disk)
        {
            if (disk == null || disk.ArchivedSegmentCount <= _archivedSegmentCount) return true;
            if (!TryCollectArchivedLineIds(disk.ArchivedSegmentCount, out HashSet<string> archivedIds))
                return false;
            _transcript.RemoveAll(line => line != null && !string.IsNullOrEmpty(line.Id)
                && archivedIds.Contains(line.Id));
            SyncArchivedSegmentCount(disk.ArchivedSegmentCount);
            return true;
        }

        // 段数只随磁盘权威文档同步;变化即失效只读缓存,下次访问按新声明重读。
        private void SyncArchivedSegmentCount(int diskCount)
        {
            if (diskCount == _archivedSegmentCount) return;
            _archivedSegmentCount = diskCount;
            _archivedLoaded = false;
            _archivedLines = new List<Line>();
        }

        /// <summary>归档依赖投影屏障清空;屏障因某成员记忆库损坏等原因长期未清时,活跃文档
        /// 会一路涨向 16MB 硬防护并最终拒写。提前指名根因,给玩家留出处理时间。</summary>
        private static void WarnIfActiveDocumentOversized(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length <= MaxTranscriptDocumentBytes / 2) return;
                lock (OversizeWarnedPaths)
                    if (!OversizeWarnedPaths.Add(path)) return;
                Debug.LogWarning("[江湖有灵] 群聊活跃文档已达 " + info.Length + " 字节(安全上限 "
                    + MaxTranscriptDocumentBytes + ")仍未归档:通常因记忆投影屏障或删除事务长期未清"
                    + "(多为某成员记忆文件损坏,请回看此前日志中的记忆库警告并处理);"
                    + "若持续增长到上限,该群将无法再保存新消息。");
            }
            catch { }
        }

        /// <summary>清空提交成功后移除全部归档段(含 .bak/.stage 残留)。删除失败只记日志:
        /// 活跃文档已声明 ArchivedSegmentCount=0,残留文件只是可被覆盖的未提交暂存,无害。</summary>
        private void DeleteArchiveSegmentsBestEffort()
        {
            try
            {
                string directory = JianghuYoulingPaths.ChatLogs;
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
                foreach (string candidate in Directory.GetFiles(directory, "GroupArc_" + _key + "_*"))
                    try { File.Delete(candidate); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[江湖有灵] 清空后删除群聊归档段失败 "
                            + Path.GetFileName(candidate) + ":" + ex.GetType().Name);
                    }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[江湖有灵] 清空后扫描群聊归档段失败:" + ex.GetType().Name);
            }
        }

        private static JToken ReadStrictJsonToken(string path)
        {
            return ReadStrictJsonToken(ReadTranscriptBytesChecked(path));
        }

        private static JToken ReadStrictJsonToken(byte[] bytes)
        {
            if (bytes == null || bytes.Length <= 0 || bytes.Length > MaxTranscriptDocumentBytes)
                throw new InvalidDataException("群聊文档大小无效");
            string json = StrictUtf8.GetString(bytes);
            return JToken.Parse(json, new JsonLoadSettings
            {
                CommentHandling = CommentHandling.Ignore,
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
            });
        }

        private static byte[] ReadTranscriptBytesChecked(string path)
        {
            long expectedLength = new FileInfo(path).Length;
            if (expectedLength <= 0 || expectedLength > MaxTranscriptDocumentBytes)
                throw new InvalidDataException("群聊文档大小无效");
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.LongLength != expectedLength) throw new IOException("群聊文档读取期间发生变化");
            return bytes;
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i]) return false;
            return true;
        }

        private static void WriteDurableUtf8(string path, string content)
        {
            byte[] bytes = StrictUtf8.GetBytes(content ?? string.Empty);
            if (bytes.Length <= 0 || bytes.Length > MaxTranscriptDocumentBytes)
                throw new InvalidDataException("群聊文档大小无效");
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private static HashSet<int> MemberIds(GroupDocument document)
        {
            var result = new HashSet<int>();
            if (document?.Members != null)
                foreach (MemberRef member in document.Members)
                    if (member != null) result.Add(member.Id);
            return result;
        }

        private static bool SameGroupDocument(GroupDocument left, GroupDocument right)
        {
            if (left == null || right == null || left.Version != right.Version
                || left.WorldId != right.WorldId || left.TaiwuId != right.TaiwuId
                || left.Revision != right.Revision
                || !string.Equals(left.IntegritySha256, right.IntegritySha256, StringComparison.Ordinal)
                || !string.Equals(left.GroupId, right.GroupId, StringComparison.Ordinal)
                || !SameMembers(left.Members, right.Members)
                || !SameLines(left.Lines, right.Lines)
                || left.MemoryProjectionPending != right.MemoryProjectionPending
                || !SameStrings(left.PendingContextProjectionExchangeIds,
                    right.PendingContextProjectionExchangeIds)
                || left.ArchivedSegmentCount != right.ArchivedSegmentCount
                || left.ArchiveSegmentIndex != right.ArchiveSegmentIndex
                || !SameStrings(left.PendingMemoryRefreshExchangeIds, right.PendingMemoryRefreshExchangeIds)
                || !SameLinkedRemovals(left.PendingLinkedMemoryRemovals, right.PendingLinkedMemoryRemovals))
                return false;
            return true;
        }

        private static string ComputeGroupDocumentIntegrity(GroupDocument document)
        {
            if (document == null) return null;
            try
            {
                JObject value = JObject.FromObject(document);
                value.Remove("IntegritySha256");
                // The field did not exist when v5 integrity digests were produced.
                // Removing its CLR default preserves exact validation of old documents.
                if (document.Version < 6) value.Remove("MemoryProjectionPending");
                // 归档段字段自 v7 起才进入摘要;移除 CLR 默认值以精确校验现存 v6 及更早文档。
                if (document.Version < 7)
                {
                    value.Remove("ArchivedSegmentCount");
                    value.Remove("ArchiveSegmentIndex");
                }
                if (document.Version < 8)
                    value.Remove("PendingContextProjectionExchangeIds");
                byte[] digest;
                using (var sha = SHA256.Create())
                    digest = sha.ComputeHash(StrictUtf8.GetBytes(value.ToString(Formatting.None)));
                var result = new StringBuilder(64);
                foreach (byte b in digest) result.Append(b.ToString("x2"));
                return result.ToString();
            }
            catch { return null; }
        }

        private static bool ValidGroupSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64) return false;
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        private static bool SameMembers(List<MemberRef> left, List<MemberRef> right)
        {
            if (left == null || right == null || left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
                if (left[i] == null || right[i] == null || left[i].Id != right[i].Id
                    || !string.Equals(left[i].Name, right[i].Name, StringComparison.Ordinal)) return false;
            return true;
        }

        private static bool SameLines(List<Line> left, List<Line> right)
        {
            if (left == null || right == null || left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
            {
                Line a = left[i], b = right[i];
                if (a == null || b == null || a.SpeakerId != b.SpeakerId || a.IsTaiwu != b.IsTaiwu
                    || a.Date != b.Date || a.CreatedUtc.ToUniversalTime().Ticks != b.CreatedUtc.ToUniversalTime().Ticks
                    || !string.Equals(a.Id, b.Id, StringComparison.Ordinal)
                    || !string.Equals(a.ExchangeId, b.ExchangeId, StringComparison.Ordinal)
                    || !string.Equals(a.CommitAttemptId, b.CommitAttemptId, StringComparison.Ordinal)
                    || !string.Equals(a.Speaker, b.Speaker, StringComparison.Ordinal)
                    || !string.Equals(a.Text, b.Text, StringComparison.Ordinal)
                    || !string.Equals(a.LocationText, b.LocationText, StringComparison.Ordinal)
                    || !string.Equals(a.ContactMode, b.ContactMode, StringComparison.Ordinal)
                    || !string.Equals(a.ImageFileName, b.ImageFileName, StringComparison.Ordinal)
                    || !SameStrings(a.MemoryIds, b.MemoryIds)
                    || !SameStrings(a.Actions, b.Actions)
                    || !SameStrings(a.ToolResults, b.ToolResults)) return false;
            }
            return true;
        }

        private static bool SameLinkedRemovals(List<LinkedMemoryRemoval> left, List<LinkedMemoryRemoval> right)
        {
            if (left == null || right == null || left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
                if (left[i] == null || right[i] == null || left[i].NpcId != right[i].NpcId
                    || !SameStrings(left[i].MemoryIds, right[i].MemoryIds)) return false;
            return true;
        }

        private static bool SameStrings(List<string> left, List<string> right)
        {
            if (left == null || right == null) return left == null && right == null;
            if (left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private static bool TryArchiveCorruptCandidate(string candidate)
        {
            if (!File.Exists(candidate)) return true;
            try
            {
                string archive = candidate + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")
                    + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                File.Move(candidate, archive);
                return File.Exists(archive) && !File.Exists(candidate);
            }
            catch { return false; }
        }

        private static bool HasRecoverableDocument(string path)
            => File.Exists(path) || File.Exists(path + ".tmp") || File.Exists(path + ".bak");

        private static List<string> DiscoverDocumentPaths(string directory, string pattern)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string candidate in Directory.GetFiles(directory, pattern))
            {
                string path = null;
                if (candidate.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) path = candidate;
                else if (candidate.EndsWith(".json.tmp", StringComparison.OrdinalIgnoreCase)) path = candidate.Substring(0, candidate.Length - 4);
                else if (candidate.EndsWith(".json.bak", StringComparison.OrdinalIgnoreCase)) path = candidate.Substring(0, candidate.Length - 4);
                if (path != null) paths.Add(path);
            }
            return new List<string>(paths);
        }

        private static DateTime DocumentLastWriteUtc(string path)
        {
            DateTime latest = DateTime.MinValue;
            foreach (string candidate in new[] { path, path + ".tmp", path + ".bak" })
                if (File.Exists(candidate))
                {
                    DateTime stamp = File.GetLastWriteTimeUtc(candidate);
                    if (stamp > latest) latest = stamp;
                }
            return latest;
        }

        private static void ArchiveMigratedDocument(string path)
        {
            lock (GetFileLock(path))
            {
                foreach (string candidate in new[] { path, path + ".tmp", path + ".bak" })
                {
                    if (!File.Exists(candidate)) continue;
                    try
                    {
                        string archive = candidate + ".legacy-key.bak";
                        if (File.Exists(archive)) archive += "." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
                        File.Move(candidate, archive);
                    }
                    catch { }
                }
            }
        }

        private HashSet<int> ExpectedMemberIds()
        {
            var ids = new HashSet<int>();
            foreach (var member in _members) if (member != null && member.Id > 0) ids.Add(member.Id);
            return ids;
        }

        private static void CollectExchangeIds(IEnumerable<Line> lines, HashSet<string> destination)
        {
            if (lines == null || destination == null) return;
            foreach (var line in lines)
                if (line != null && !string.IsNullOrWhiteSpace(line.ExchangeId)) destination.Add(line.ExchangeId);
        }

        private static void CollectCandidateExchangeIds(string path, uint expectedWorldId, int expectedTaiwuId,
            string expectedGroupId, HashSet<int> expectedMembers, HashSet<string> destination)
        {
            lock (GetFileLock(path))
            {
                foreach (string candidate in new[] { path, path + ".tmp", path + ".bak" })
                {
                    if (!File.Exists(candidate)) continue;
                    try
                    {
                        var doc = ParseAndValidateDocument(candidate, expectedWorldId, expectedTaiwuId,
                            expectedGroupId, expectedMembers);
                        CollectExchangeIds(doc.Lines, destination);
                        if (doc.PendingMemoryRefreshExchangeIds != null)
                            foreach (string exchangeId in doc.PendingMemoryRefreshExchangeIds)
                                if (!string.IsNullOrWhiteSpace(exchangeId)) destination.Add(exchangeId);
                    }
                    catch { }
                }
            }
        }

        private void NormalizeTranscript() => NormalizeLines(_transcript);

        // v1-4 老行缺 Id/ExchangeId/CommitAttemptId,读回时补 legacy-* 合成身份;
        // 活跃文档与归档段的行走同一套归一化,段行读回后语义与活跃行完全一致。
        private void NormalizeLines(List<Line> lines)
        {
            string exchange = null;
            int seq = 0;
            foreach (var line in lines ?? new List<Line>())
            {
                if (line == null) { seq++; continue; }
                if (string.IsNullOrEmpty(line.Id)) line.Id = "legacy-l-" + seq.ToString("D6");
                if (line.IsTaiwu || string.IsNullOrEmpty(exchange)) exchange = "legacy-x-" + seq.ToString("D6");
                if (string.IsNullOrEmpty(line.ExchangeId)) line.ExchangeId = exchange;
                else exchange = line.ExchangeId;
                if (line.IsTaiwu) { line.SpeakerId = _taiwuId; if (string.IsNullOrWhiteSpace(line.Speaker)) line.Speaker = "太吾"; }
                else if (line.SpeakerId < 0)
                {
                    int found = 0;
                    foreach (var m in _members) if (m != null && m.Name == line.Speaker) { if (found != 0) { found = -1; break; } found = m.Id; }
                    if (found > 0) line.SpeakerId = found;
                }
                if (!line.IsTaiwu && string.IsNullOrWhiteSpace(line.CommitAttemptId))
                    line.CommitAttemptId = "legacy-a-" + StableShortHash(line.Id + "|" + line.ExchangeId);
                seq++;
            }
        }

        private List<Line> MergeLegacyDocuments(List<KeyValuePair<string, GroupDocument>> documents)
        {
            var merged = new List<Line>();
            var byId = new Dictionary<string, Line>(StringComparer.Ordinal);
            foreach (var pair in documents)
            {
                string sourceTag = StableShortHash(Path.GetFileName(pair.Key));
                string exchange = null;
                int seq = 0;
                foreach (var line in pair.Value.Lines ?? new List<Line>())
                {
                    if (line == null) { seq++; continue; }
                    if (string.IsNullOrWhiteSpace(line.Id)) line.Id = "legacy-l-" + sourceTag + "-" + seq.ToString("D6");
                    if (line.IsTaiwu || string.IsNullOrEmpty(exchange)) exchange = "legacy-x-" + sourceTag + "-" + seq.ToString("D6");
                    if (string.IsNullOrWhiteSpace(line.ExchangeId)) line.ExchangeId = exchange;
                    else exchange = line.ExchangeId;
                    if (line.IsTaiwu)
                    {
                        line.SpeakerId = _taiwuId;
                        if (string.IsNullOrWhiteSpace(line.Speaker)) line.Speaker = "太吾";
                    }
                    else if (line.SpeakerId < 0)
                    {
                        int found = 0;
                        foreach (var member in _members)
                            if (member != null && member.Name == line.Speaker)
                            {
                                if (found != 0) { found = -1; break; }
                                found = member.Id;
                            }
                        if (found > 0) line.SpeakerId = found;
                    }

                    if (byId.TryGetValue(line.Id, out var existing))
                    {
                        if (SameLogicalLine(existing, line)) { seq++; continue; }
                        line.Id = "legacy-l-" + sourceTag + "-collision-" + seq.ToString("D6");
                        while (byId.ContainsKey(line.Id)) line.Id += "x";
                    }
                    byId[line.Id] = line;
                    merged.Add(line);
                    seq++;
                }
            }
            return merged;
        }

        private static bool SameLogicalLine(Line left, Line right)
            => left != null && right != null && left.IsTaiwu == right.IsTaiwu
                && left.SpeakerId == right.SpeakerId
                && string.Equals(left.Speaker, right.Speaker, StringComparison.Ordinal)
                && string.Equals(left.Text, right.Text, StringComparison.Ordinal)
                && string.Equals(left.CommitAttemptId, right.CommitAttemptId, StringComparison.Ordinal)
                && string.Equals(left.ExchangeId, right.ExchangeId, StringComparison.Ordinal)
                && SameStrings(left.MemoryIds, right.MemoryIds)
                && SameStrings(left.Actions, right.Actions)
                && SameStrings(left.ToolResults, right.ToolResults);

        private static string StableShortHash(string value)
        {
            byte[] digest;
            using (var sha = SHA256.Create()) digest = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
            var sb = new StringBuilder(12);
            for (int i = 0; i < 6; i++) sb.Append(digest[i].ToString("x2"));
            return sb.ToString();
        }

        private static object GetFileLock(string path)
            => FileLocks.GetOrAdd(path, _ => new object());

        private static long CurrentFileEpoch(string path)
            => FileEpochs.TryGetValue(path, out long epoch) ? epoch : 0;

        private bool HasCurrentFileEpoch()
        {
            if (string.IsNullOrWhiteSpace(_key)) return false;
            string path = ConvPath();
            lock (GetFileLock(path)) return _fileEpoch == CurrentFileEpoch(path);
        }

        private bool FileEpochStillCurrent(long capturedEpoch)
        {
            if (string.IsNullOrWhiteSpace(_key)) return false;
            string path = ConvPath();
            lock (GetFileLock(path)) return capturedEpoch == CurrentFileEpoch(path);
        }

        private static long CurrentGroupEpoch(string journalPath)
        {
            if (string.IsNullOrWhiteSpace(journalPath)) return -1;
            lock (ActiveAttemptsGate)
                return GroupEpochsByPath.TryGetValue(journalPath, out long epoch) ? epoch : 0;
        }

        private static long AdvanceGroupEpoch(string journalPath)
        {
            if (string.IsNullOrWhiteSpace(journalPath)) return -1;
            lock (ActiveAttemptsGate)
            {
                long next = unchecked((GroupEpochsByPath.TryGetValue(journalPath, out long value) ? value : 0) + 1);
                GroupEpochsByPath[journalPath] = next;
                // Every former owner is fenced by the epoch before cleanup starts.  Removing
                // leases here lets the clearing tab recover their entries immediately.
                ActiveAttemptOwnersByPath.Remove(journalPath);
                return next;
            }
        }

        private bool HasCurrentGroupEpoch()
            => !string.IsNullOrWhiteSpace(_journalPath) && _groupEpoch == CurrentGroupEpoch(_journalPath);

        private bool RegisterOwnedAttempt(string attemptId)
        {
            if (string.IsNullOrWhiteSpace(attemptId) || string.IsNullOrWhiteSpace(_journalPath)) return false;
            lock (ActiveAttemptsGate)
            {
                long current = GroupEpochsByPath.TryGetValue(_journalPath, out long epoch) ? epoch : 0;
                if (_groupEpoch != current) return false;
                if (!ActiveAttemptOwnersByPath.TryGetValue(_journalPath, out var owners))
                {
                    owners = new Dictionary<string, string>(StringComparer.Ordinal);
                    ActiveAttemptOwnersByPath[_journalPath] = owners;
                }
                if (owners.TryGetValue(attemptId, out string owner) && owner != _ownerId) return false;
                owners[attemptId] = _ownerId;
                _ownedAttemptIds.Add(attemptId);
                return true;
            }
        }

        private void ReleaseOwnedAttempt(string attemptId)
        {
            if (string.IsNullOrWhiteSpace(attemptId)) return;
            lock (ActiveAttemptsGate)
            {
                if (!string.IsNullOrWhiteSpace(_journalPath)
                    && ActiveAttemptOwnersByPath.TryGetValue(_journalPath, out var owners)
                    && owners.TryGetValue(attemptId, out string owner) && owner == _ownerId)
                {
                    owners.Remove(attemptId);
                    if (owners.Count == 0) ActiveAttemptOwnersByPath.Remove(_journalPath);
                }
                _ownedAttemptIds.Remove(attemptId);
            }
        }

        private static bool IsAttemptActive(string journalPath, string attemptId)
        {
            if (string.IsNullOrWhiteSpace(journalPath) || string.IsNullOrWhiteSpace(attemptId)) return false;
            lock (ActiveAttemptsGate)
                return ActiveAttemptOwnersByPath.TryGetValue(journalPath, out var owners)
                    && owners.ContainsKey(attemptId);
        }

        private bool TurnStillCurrent(System.Threading.CancellationToken cancellationToken, long capturedEpoch)
            => !cancellationToken.IsCancellationRequested
                && WorldLifecycle.IsSameWorld(_worldGeneration)
                && FileEpochStillCurrent(capturedEpoch)
                && HasCurrentGroupEpoch();

        private static HashSet<string> DeletedLines(string path)
            => DeletedLineIdsByPath.GetOrAdd(path, _ => new HashSet<string>(StringComparer.Ordinal));

        // 只在 GetFileLock(path) 内读写。完成墓碑留到进程结束，阻止同组旧页签把已清除的
        // durable pending marker 再写回；exchange ID 为 GUID/稳定 legacy ID，不会复用。
        private static HashSet<string> CompletedMemoryRefreshIds(string path)
            => CompletedMemoryRefreshIdsByPath.GetOrAdd(path, _ => new HashSet<string>(StringComparer.Ordinal));

        private static HashSet<string> CompletedLinkedMemoryRemovalIds(string path)
            => CompletedLinkedMemoryRemovalIdsByPath.GetOrAdd(path, _ => new HashSet<string>(StringComparer.Ordinal));

        private static string NewId(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N");

        private Line NewLine(string exchangeId, int speakerId, string speaker, bool isTaiwu, string text, int date,
            string locationText = null, string contactMode = null)
            => new Line { Id = NewId("l"), ExchangeId = exchangeId, SpeakerId = speakerId, Speaker = speaker,
                IsTaiwu = isTaiwu, Text = text, Date = date, LocationText = PrimaryLocationText(locationText),
                ContactMode = string.IsNullOrWhiteSpace(contactMode) ? null : contactMode.Trim(), CreatedUtc = DateTime.UtcNow };

        private static string PrimaryLocationText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string text = value.Trim();
            int lineBreak = text.IndexOfAny(new[] { '\r', '\n' });
            return lineBreak < 0 ? text : text.Substring(0, lineBreak).Trim();
        }

        private int CurrentDate()
        {
            try
            {
                var basic = SingletonObject.getInstance<BasicGameData>();
                if (basic != null && basic.CurrDate >= 0) return basic.CurrDate;
            }
            catch { }
            foreach (var m in _members) if (m?.Snap != null && m.Snap.CurrentDate >= 0) return m.Snap.CurrentDate;
            return 0;
        }

        private void MergePendingMemoryRefresh(GroupDocument document)
        {
            if (document?.PendingMemoryRefreshExchangeIds == null) return;
            foreach (string exchangeId in document.PendingMemoryRefreshExchangeIds)
                if (!string.IsNullOrWhiteSpace(exchangeId)) _pendingMemoryRemovalExchangeIds.Add(exchangeId);
        }

        private void MergePendingLinkedMemoryRemovals(GroupDocument document, string path = null)
        {
            if (document?.PendingLinkedMemoryRemovals == null) return;
            HashSet<string> completed = null;
            if (!string.IsNullOrWhiteSpace(path)) completed = CompletedLinkedMemoryRemovalIds(path);
            foreach (var pending in document.PendingLinkedMemoryRemovals)
            {
                if (pending == null || pending.NpcId < 0 || pending.MemoryIds == null) continue;
                if (!_pendingLinkedMemoryRemovals.TryGetValue(pending.NpcId, out var ids))
                { ids = new HashSet<string>(StringComparer.Ordinal); _pendingLinkedMemoryRemovals[pending.NpcId] = ids; }
                foreach (string id in pending.MemoryIds)
                    if (!string.IsNullOrWhiteSpace(id) && (completed == null || !completed.Contains(pending.NpcId + ":" + id))) ids.Add(id);
            }
        }

        private void EnqueueLinkedMemoryRemovals(IEnumerable<Line> lines)
        {
            if (lines == null) return;
            foreach (var line in lines)
            {
                if (line == null || line.SpeakerId < 0 || line.MemoryIds == null) continue;
                if (!_pendingLinkedMemoryRemovals.TryGetValue(line.SpeakerId, out var ids))
                { ids = new HashSet<string>(StringComparer.Ordinal); _pendingLinkedMemoryRemovals[line.SpeakerId] = ids; }
                foreach (string id in line.MemoryIds) if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
            }
        }

        private bool RemovePendingLinkedMemories()
        {
            if (_pendingLinkedMemoryRemovals.Count == 0) return true;
            foreach (var pair in new List<KeyValuePair<int, HashSet<string>>>(_pendingLinkedMemoryRemovals))
            {
                if (pair.Key <= 0 || pair.Value == null || pair.Value.Count == 0) { _pendingLinkedMemoryRemovals.Remove(pair.Key); continue; }
                try
                {
                    var store = NpcMemoryStore.Load(JianghuYoulingPaths.Memories, _taiwuId.ToString(), pair.Key.ToString());
                    if (!store.LoadReliable) return false;
                    store.RemoveByIds(pair.Value);
                    if (!store.Save()) return false;
                    PortraitService.Invalidate(_taiwuId, pair.Key);
                    if (!PortraitStore.Delete(_taiwuId, pair.Key)) return false;
                    string path = ConvPath();
                    lock (GetFileLock(path))
                    {
                        var completed = CompletedLinkedMemoryRemovalIds(path);
                        foreach (string id in pair.Value) completed.Add(pair.Key + ":" + id);
                    }
                    _pendingLinkedMemoryRemovals.Remove(pair.Key);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[江湖有灵] 撤回群聊关联记忆失败 npc=" + pair.Key + ":" + ex.GetType().Name);
                    return false;
                }
            }
            return true;
        }

        /// <summary>重放聊天文档内的来源记忆事务日志。刷新是幂等的：先撤回三个兼容
        /// SourceId，再按当前 transcript 重建仍存在的 exchange。</summary>
        private void ReplayPendingMemoryRefresh()
        {
            if (_pendingMemoryRemovalExchangeIds.Count == 0 && _pendingLinkedMemoryRemovals.Count == 0) return;
            var pending = new List<string>(_pendingMemoryRemovalExchangeIds);
            // 删归档轮可能崩溃在"意图已落盘、段重写前";必须先补做段重写再撤回记忆。
            // 顺序反了会把段里仍可见轮次的记忆删光、又消费掉意图,被删轮就以"无记忆"
            // 状态在重启后静默复活。段重写失败则整体保留意图,下次载入幂等重试。
            if (_archivedSegmentCount > 0 && pending.Count > 0)
            {
                var pendingSet = new HashSet<string>(pending, StringComparer.Ordinal);
                // 与 DeleteLines 一致的 fail-closed：把 pending exchange 作为 mustResolve 传入。
                // 存在不可读段且仍有 pending 未能在任一可读段确认移除时，段重写返回 false，
                // 保留意图待段恢复后幂等重试，绝不 fail-open 删了记忆却删不掉段内行(记忆复活)。
                if (!TryRewriteArchiveSegmentsRemoving(pendingSet, new HashSet<string>(StringComparer.Ordinal), pendingSet))
                {
                    Debug.LogWarning("[江湖有灵] 群聊删除意图的归档段重写失败，保留待办下次重放");
                    return;
                }
            }
            if (!RefreshMemoryForExchanges(pending) || !RemovePendingLinkedMemories())
            {
                Debug.LogWarning("[江湖有灵] 群聊来源记忆待办重放失败，将保留到下次载入");
                return;
            }
            MarkMemoryRefreshCompleted(pending);
            if (!SaveTranscript())
            {
                foreach (string exchangeId in pending) _pendingMemoryRemovalExchangeIds.Add(exchangeId);
                Debug.LogWarning("[江湖有灵] 群聊来源记忆待办完成标记写盘失败，将幂等重放");
            }
        }

        private void MarkMemoryRefreshCompleted(IEnumerable<string> exchangeIds)
        {
            string path = ConvPath();
            lock (GetFileLock(path))
            {
                var completed = CompletedMemoryRefreshIds(path);
                foreach (string exchangeId in exchangeIds)
                {
                    if (string.IsNullOrWhiteSpace(exchangeId)) continue;
                    completed.Add(exchangeId);
                    _pendingMemoryRemovalExchangeIds.Remove(exchangeId);
                }
            }
        }

        /// <summary>清空前从每位成员的可靠记忆库反查本群 SourceId。即使群聊 main/tmp/bak
        /// 全损坏，也不能因 transcript 为空就把孤儿来源记忆误报为已清理。</summary>
        private bool CollectGroupMemoryExchangeIds(HashSet<string> destination)
        {
            if (destination == null) return false;
            foreach (var member in _members)
            {
                try
                {
                    var memory = NpcMemoryStore.Load(JianghuYoulingPaths.Memories,
                        _taiwuId.ToString(), member.Id.ToString());
                    if (!memory.LoadReliable)
                    {
                        Debug.LogWarning("[江湖有灵] 群聊清空前无法可靠扫描成员记忆 npc=" + member.Id);
                        return false;
                    }
                    foreach (var entry in memory.All)
                    {
                        if (entry == null) continue;
                        if (string.Equals(entry.SourceKind, "group", StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryGetGroupExchangeId(entry.SourceId, member.Id, out string exchangeId))
                                destination.Add(exchangeId);
                            continue;
                        }
                        if ((string.Equals(entry.SourceKind, "group_turn_action", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(entry.SourceKind, "group_turn_remember", StringComparison.OrdinalIgnoreCase))
                            && TryGetGroupTurnExchangeId(entry.SourceId, member.Id, out string turnExchangeId))
                        {
                            destination.Add(turnExchangeId);
                            if (!string.IsNullOrWhiteSpace(entry.Id))
                            {
                                if (!_pendingLinkedMemoryRemovals.TryGetValue(member.Id, out var linked))
                                { linked = new HashSet<string>(StringComparer.Ordinal); _pendingLinkedMemoryRemovals[member.Id] = linked; }
                                linked.Add(entry.Id);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[江湖有灵] 群聊清空前扫描成员记忆失败 npc=" + member.Id + ":" + ex.GetType().Name);
                    return false;
                }
            }
            return true;
        }

        private bool TryGetGroupExchangeId(string sourceId, int memberId, out string exchangeId)
        {
            exchangeId = null;
            if (string.IsNullOrWhiteSpace(sourceId)) return false;
            string suffix = ":" + memberId;
            if (!sourceId.EndsWith(suffix, StringComparison.Ordinal)) return false;
            foreach (string key in new[] { _key, PreviousGroupKey(_taiwuId, _members), LegacyGroupKey(_taiwuId, _members) })
            {
                string prefix = "group:" + key + ":";
                if (!sourceId.StartsWith(prefix, StringComparison.Ordinal) || sourceId.Length <= prefix.Length + suffix.Length) continue;
                exchangeId = sourceId.Substring(prefix.Length, sourceId.Length - prefix.Length - suffix.Length);
                return !string.IsNullOrWhiteSpace(exchangeId);
            }
            return false;
        }

        private bool TryGetGroupTurnExchangeId(string sourceId, int memberId, out string exchangeId)
        {
            exchangeId = null;
            if (string.IsNullOrWhiteSpace(sourceId)) return false;
            string prefix = "group-turn:" + _key + ":";
            if (!sourceId.StartsWith(prefix, StringComparison.Ordinal)) return false;
            foreach (string kind in new[] { "action", "remember" })
            {
                string suffix = ":" + memberId + ":" + kind;
                if (!sourceId.EndsWith(suffix, StringComparison.Ordinal)
                    || sourceId.Length <= prefix.Length + suffix.Length) continue;
                string body = sourceId.Substring(prefix.Length, sourceId.Length - prefix.Length - suffix.Length);
                int attemptMarker = body.IndexOf(":attempt:", StringComparison.Ordinal);
                exchangeId = attemptMarker > 0 ? body.Substring(0, attemptMarker) : body;
                return !string.IsNullOrWhiteSpace(exchangeId);
            }
            return false;
        }

        private bool RemoveMemoryForExchanges(IEnumerable<string> exchangeIds)
        {
            if (exchangeIds == null) return true;
            var ids = new List<string>(); foreach (var x in exchangeIds) if (!string.IsNullOrWhiteSpace(x) && !ids.Contains(x)) ids.Add(x);
            if (ids.Count == 0) return true;
            bool persisted = true;
            foreach (var m in _members)
            {
                try
                {
                    var ms = NpcMemoryStore.Load(JianghuYoulingPaths.Memories, _taiwuId.ToString(), m.Id.ToString());
                    var sources = new List<string>();
                    foreach (string exchangeId in ids) AddCompatibleMemorySourceIds(sources, m.Id, exchangeId);
                    ms.RemoveBySourceIds(sources);
                    // 即使本实例没看到旧条目也必须 Save：NpcMemoryStore 的 source epoch/tombstone
                    // 会阻止另一旧实例把刚清掉的来源重新写回。
                    if (ms.Save())
                    {
                        PortraitService.Invalidate(_taiwuId, m.Id);
                        if (!PortraitStore.Delete(_taiwuId, m.Id)) persisted = false;
                    }
                    else
                    {
                        persisted = false;
                        Debug.LogWarning("[江湖有灵] 撤回群聊记忆被并发清理/不可靠读盘拒绝 npc=" + m.Id);
                    }
                }
                catch (Exception ex)
                {
                    persisted = false;
                    Debug.LogWarning("[江湖有灵] 撤回群聊记忆失败 npc=" + m.Id + ":" + ex.GetType().Name);
                }
            }
            return persisted;
        }

        private bool RefreshMemoryForExchanges(IEnumerable<string> exchangeIds)
        {
            if (exchangeIds == null) return true;
            var set = new HashSet<string>(exchangeIds, StringComparer.Ordinal);
            if (set.Count == 0) return true;

            // 清理旧版本直接写入的 group 长期记忆，同时撤回对应的隐藏上下文。
            bool persisted = RemoveMemoryForExchanges(set);
            var affected = new List<string>(set);
            foreach (Member member in _members)
            {
                if (member == null || member.Id <= 0) continue;
                if (!TalkOrchestrator.SynchronizeGroupChatTranscripts(_taiwuId, member.Id,
                    member.Name, _key, affected, Array.Empty<TalkTurn>())) persisted = false;
            }
            if (!persisted) return false;

            // 若该轮仍存在（例如晚到工具回执补写），重新生成摘要；整轮删除则到此结束。
            string remaining = null;
            foreach (KeyValuePair<string, List<Line>> pair in ExchangesInOrder())
                if (set.Contains(pair.Key) && VisibleLines(pair.Value).Count > 0)
                { remaining = pair.Key; break; }
            return string.IsNullOrWhiteSpace(remaining) || ConsolidateMemoryOnly(remaining);
        }
        private void AddCompatibleMemorySourceIds(List<string> target, int memberId, string exchangeId)
        {
            AddUnique(target, "group:" + _key + ":" + exchangeId + ":" + memberId);
            AddUnique(target, "group:" + PreviousGroupKey(_taiwuId, _members) + ":" + exchangeId + ":" + memberId);
            AddUnique(target, "group:" + LegacyGroupKey(_taiwuId, _members) + ":" + exchangeId + ":" + memberId);
        }

        private static void AddUnique(List<string> target, string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !target.Contains(value)) target.Add(value);
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        // —— 离线持久化测试入口(先例:WindowsDictation.RunShortcutSequenceForTest;DevTest 直接
        //    编译本文件,以最小成员状态驱动真实的载入/存盘/归档/删轮/清空路径,不含 LLM/Unity 交互)——
        internal static GroupChatOrchestrator OpenForPersistenceTest(int taiwuId,
            IList<KeyValuePair<int, string>> roster, string persistedGroupId = null)
        {
            var orchestrator = new GroupChatOrchestrator
            {
                _worldGeneration = WorldLifecycle.Generation,
                _taiwuId = taiwuId,
            };
            if (roster != null)
                foreach (var member in roster)
                    orchestrator._members.Add(new Member { Id = member.Key, Name = member.Value });
            orchestrator._key = IsSafePersistedGroupId(persistedGroupId, taiwuId)
                ? persistedGroupId.Trim()
                : GroupKey(taiwuId, orchestrator._members);
            orchestrator._journalWorldId = JianghuYoulingPaths.CurrentWorldId;
            if (orchestrator._journalWorldId > 0)
            {
                orchestrator._journalPath = Path.Combine(JianghuYoulingPaths.ChatLogs,
                    "GroupTxn_" + orchestrator._key + ".json");
                orchestrator._exchangeJournal = new GroupExchangeJournal(orchestrator._journalPath,
                    orchestrator._journalWorldId, taiwuId, orchestrator._key);
                orchestrator._groupEpoch = CurrentGroupEpoch(orchestrator._journalPath);
            }
            if (!orchestrator.LoadTranscript()) return null;
            if (!HasRecoverableDocument(orchestrator.ConvPath()) && !orchestrator.SaveTranscript())
                return null;
            orchestrator.ReplayPreparedGroupExchanges();
            orchestrator.ReplayPendingMemoryRefresh();
            orchestrator.ConsolidateMemoryOnly();
            orchestrator.MaybeArchiveOldExchanges();
            return orchestrator;
        }

        internal bool AddMembersForPersistenceTest(IList<KeyValuePair<int, string>> additions)
        {
            var prepared = new List<Member>();
            if (additions != null)
                foreach (KeyValuePair<int, string> addition in additions)
                    prepared.Add(new Member { Id = addition.Key, Name = addition.Value });
            if (!TryCommitAddedMembers(prepared, out _)) return false;
            return ConsolidateAllMemoryForPersistenceTest();
        }

        internal bool ConsolidateAllMemoryForPersistenceTest()
        {
            while (true)
            {
                if (!TryLoadPendingMemoryProjection(out long revision)) return false;
                if (!_memoryProjectionPending) return true;
                string exchangeId = null;
                foreach (string pending in _pendingContextProjectionExchangeIds)
                { exchangeId = pending; break; }
                if (string.IsNullOrWhiteSpace(exchangeId)
                    || !TryCommitMemoryProjection(exchangeId, revision, _fileEpoch)) return false;
            }
        }

        private static void ValidateV8ContextProjectionQueue(JObject document)
        {
            JToken pending = document?["PendingContextProjectionExchangeIds"]
                ?? document?["pendingContextProjectionExchangeIds"];
            if (pending?.Type != JTokenType.Array)
                throw new JsonException("v8 群聊文档缺少上下文投影队列");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (JToken item in (JArray)pending)
            {
                string id = item?.Type == JTokenType.String ? item.Value<string>() : null;
                if (string.IsNullOrWhiteSpace(id) || !ids.Add(id))
                    throw new JsonException("v8 群聊上下文投影 ID 无效或重复");
            }
            JToken barrier = document?["MemoryProjectionPending"]
                ?? document?["memoryProjectionPending"];
            if (barrier?.Type != JTokenType.Boolean
                || barrier.Value<bool>() != (ids.Count > 0))
                throw new JsonException("v8 群聊上下文投影队列与屏障不一致");
        }

        internal bool AppendExchangeForPersistenceTest(string playerText,
            IList<KeyValuePair<int, string>> memberReplies, int date)
        {
            string exchangeId = NewId("x");
            var appended = new List<Line> { NewLine(exchangeId, _taiwuId, "太吾", true, playerText, date) };
            if (memberReplies != null)
                foreach (var reply in memberReplies)
                {
                    string name = "#" + reply.Key;
                    foreach (var member in _members)
                        if (member != null && member.Id == reply.Key) { name = member.Name; break; }
                    Line line = NewLine(exchangeId, reply.Key, name, false, reply.Value, date);
                    line.CommitAttemptId = NewId("a");
                    appended.Add(line);
                }
            foreach (Line line in appended) _transcript.Add(line);
            if (SaveTranscript(contentChanged: true, changedExchangeId: exchangeId)) return true;
            foreach (Line line in appended) _transcript.Remove(line);
            return false;
        }

        internal bool AppendResultOnlyExchangeForPersistenceTest(string playerText,
            int memberId, string toolResult, int date)
        {
            string exchangeId = NewId("x");
            string name = "#" + memberId;
            foreach (Member member in _members)
                if (member != null && member.Id == memberId) { name = member.Name; break; }
            var player = NewLine(exchangeId, _taiwuId, "太吾", true, playerText, date);
            var receipt = NewLine(exchangeId, memberId, name, false, string.Empty, date);
            receipt.CommitAttemptId = NewId("a");
            receipt.ToolResults = new List<string> { toolResult };
            _transcript.Add(player);
            _transcript.Add(receipt);
            if (SaveTranscript(contentChanged: true, changedExchangeId: exchangeId)) return true;
            _transcript.Remove(receipt);
            _transcript.Remove(player);
            return false;
        }

        internal string ResultContextForPersistenceTest(int memberId)
        {
            foreach (Member member in _members)
                if (member != null && member.Id == memberId) return BuildGroupInputFor(member);
            return null;
        }

        internal List<int> RoutedMemberIdsForTest(string playerInput)
        {
            var result = new List<int>();
            foreach (int index in RoutedIndices(playerInput, 1))
                if (index >= 0 && index < _members.Count && _members[index] != null)
                    result.Add(_members[index].Id);
            result.Sort();
            return result;
        }

        internal void ArchiveForPersistenceTest() => MaybeArchiveOldExchanges();
        internal int ArchivedSegmentCountForPersistenceTest => _archivedSegmentCount;
        internal bool TranscriptReliableForPersistenceTest => _transcriptReliable;
        internal string GroupKeyForPersistenceTest => _key;
        internal string ActiveDocumentPathForPersistenceTest => ConvPath();
        internal string ArchiveSegmentPathForPersistenceTest(int index) => ArchiveSegmentPath(index);

        /// <summary>按旧版真实磁盘形态(不含 v7 归档字段)生成带正确 v6 摘要的文档字节,
        /// 供离线回归验证 v7 读取端的完整性版本门控不破坏任何现存 v6 文档。</summary>
    }
}
