using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using GameData.Domains.Information;
using JianghuYouling.Core.Behavior;
using JianghuYouling.Core.Commission;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Text;
using JianghuYouling.Core.Memory;
using JianghuYouling.Core.Persistence;
using JianghuYouling.Core.Prompt;
using JianghuYouling.Core.Tools;
using JianghuYouling.Effects;
using JianghuYouling.Rpc;
using JianghuYouling.Shared;

namespace JianghuYouling
{
    /// <summary>
    /// 工具调用版对话编排(协程):读快照→画像→召回记忆→组 prompt+工具→agentic 循环
    /// (调用→执行工具→回灌→出最终回话),每步触发进度回调。效果全部经工具落地。
    /// </summary>
    public sealed class TalkOrchestrator
    {
        private const int CurrentConversationVersion = 6;
        public string LastCommittedExchangeId { get; private set; }
        public IReadOnlyList<string> LastTurnMemoryIds { get; private set; }
        public IReadOnlyList<string> LastTurnActions { get; private set; }
        public IReadOnlyList<string> LastTurnToolResults { get; private set; }
        public string LastNpcName { get; private set; }
        public int LastTurnDate { get; private set; }
        public string LastPlayerLocationText { get; private set; }
        public string LastNpcLocationText { get; private set; }
        public string LastContactMode { get; private set; }
        private sealed class Conversation
        {
            public int Version { get; set; } = CurrentConversationVersion;
            public uint WorldId { get; set; }
            public int TaiwuId { get; set; }
            public int NpcId { get; set; }
            public long Revision { get; set; }
            public string IntegritySha256 { get; set; }
            public string Summary { get; set; }
            // SHA-256 of the exact prior summary metadata + verbatim turns used by
            // the latest compaction. The matching source is recoverable from cold archive.
            public string SummarySourceHash { get; set; }
            public List<TalkTurn> Turns { get; set; } = new List<TalkTurn>();
            public string NpcName { get; set; }
            // 按轮删除已提交会话、但长期记忆/画像尚未确认清理时的 durable journal。
            public List<string> PendingMemoryRemovalIds { get; set; } = new List<string>();
            // 工具副作用在最终回话前就可能已落地。派发前先写此 journal；若后续 LLM/进程失败，
            // 同一句重试会读到并拒绝重复派发，而不是再次赠物/扣钱/改关系。
            public List<PendingActionTurn> PendingActionTurns { get; set; } = new List<PendingActionTurn>();
            [JsonIgnore] public int Epoch { get; set; }
            [JsonIgnore] public long ClearEpoch { get; set; }
            [JsonIgnore] public string CacheKey { get; set; }
            [JsonIgnore] public bool LoadReliable { get; set; } = true;
        }
        private sealed class PendingActionTurn
        {
            public string Id { get; set; }
            public string PlayerInput { get; set; }
            public int Date { get; set; }
            // 非空即证明该动作事务已经随这一轮父对话一起提交。后续玩家再次发送
            // 相同文字时绝不能承接它；只有崩溃在父提交之前的事务才按输入续跑。
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
            public string CommittedExchangeId { get; set; }
            public List<PendingToolDispatch> Operations { get; set; } = new List<PendingToolDispatch>();
        }
        private sealed class PendingToolDispatch
        {
            public string Signature { get; set; }
            public string Tool { get; set; }
            public string OperationId { get; set; }
            public string ArgumentsJson { get; set; }
            public string Status { get; set; }   // dispatching / pending / succeeded / failed / rejected / canceled / unknown
            public string Code { get; set; }
            public bool Retryable { get; set; }
            public bool RetryableKnown { get; set; }
            public string Receipt { get; set; }
            public string Message { get; set; }
            public string Result { get; set; }   // 给模型看的短业务结果，不参与状态判断
            public bool ReceiptAcknowledged { get; set; }
            public bool ReceiptAcknowledgementQueued { get; set; }
            // v3 及更早存档兼容：只读迁移来源；新链路只依据 Status/ToolOutcome。
            public string State { get; set; }
            public long UpdatedUtcTicks { get; set; }
        }
        private sealed class CompactionLease
        {
            public string Key;
            public long Token;
            public System.Threading.CancellationTokenSource Cancellation;
        }
        private static readonly object ConversationGate = new object();
        private static readonly object ConversationMigrationLockGate = new object();
        private static readonly Dictionary<string, object> ConversationMigrationLocks =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Conversation> _conversations = new Dictionary<string, Conversation>();
        private static readonly Dictionary<string, long> ConversationRevisions = new Dictionary<string, long>();
        private static readonly Dictionary<string, long> ConversationClearEpochs = new Dictionary<string, long>();
        private static readonly Dictionary<string, CompactionLease> ActiveCompactions = new Dictionary<string, CompactionLease>();
        // 同进程按轮删除墓碑：会话已成功提交、但记忆/画像清理失败时，UI 重试可识别为
        // 幂等续做；反之“目标不在 Turns”可能只是尚未提交的压缩，必须 fail-closed。
        private static readonly Dictionary<string, HashSet<string>> DeletedExchangeIdsByConversation = new Dictionary<string, HashSet<string>>();
        private static readonly HashSet<string> RecoveredGroupReconciliations = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> RecoveredConversationReconciliations = new HashSet<string>(StringComparer.Ordinal);
        private static int _scheduledWorldRecoveryGeneration = -1;
        private static uint _scheduledWorldRecoveryWorldId;
        private static long _nextCompactionToken;
        private static int _conversationEpoch;
        private const string CompactionMemorySourceKind = "single_compaction";
        private const string UnconfirmedActionReply = "此事回执未确认，我不能说已经办成，也不会重复动手；且待稍后以游戏实情为准。";
        private const int MaxConversationDocumentBytes = 16 * 1024 * 1024;
        private const int MaxPendingActionTurns = 128;
        private const int MaxPendingOperations = 256;
        private static readonly UTF8Encoding StrictConversationUtf8 = new UTF8Encoding(false, true);

        public static void ResetConversationCacheForWorldExit()
        {
            lock (ConversationGate)
            {
                unchecked { _conversationEpoch++; }
                _conversations.Clear();
                ConversationRevisions.Clear();
                ConversationClearEpochs.Clear();
                foreach (var lease in ActiveCompactions.Values)
                    try { lease?.Cancellation?.Cancel(); } catch { }
                ActiveCompactions.Clear();
                DeletedExchangeIdsByConversation.Clear();
                RecoveredGroupReconciliations.Clear();
                RecoveredConversationReconciliations.Clear();
                ConversationSessionIndexStore.ResetForWorldExit();
                _scheduledWorldRecoveryGeneration = -1;
                _scheduledWorldRecoveryWorldId = 0;
            }
        }

        /// <summary>一个聊过的对象(供历史查看窗「对话」页列出)。</summary>
        public sealed class ConversedPartner
        {
            public int NpcId;
            public string Name;
            public int LastDate;
            public int LastPlayerDate;
            public int Turns;
            public int PlayerConversations;
            public int RecentPlayerConversations;
            public int RecentActiveMonths;
            public DateTime LastActivityUtc;
        }

        /// <summary>
        /// 固定模板人物首次开聊后，把旧 id 下的单聊全文、压缩梗概、冷归档和未完成动作日志
        /// 一次性复制到永久副本。这里只准备目标资料，不清理源文件；身份映射耐久提交后再由
        /// FinalizeConversationIdentityMigration 清理，避免后续任一步失败时把玩家留在空白旧身份上。
        /// </summary>
        internal static bool MigrateConversationIdentity(int taiwuId, int oldNpcId, int newNpcId)
        {
            if (taiwuId <= 0 || oldNpcId < 0 || newNpcId < 0) return false;
            if (oldNpcId == newNpcId) return true;
            uint worldId = JianghuYoulingPaths.CurrentWorldId;
            if (worldId == 0) return false;
            try
            {
                lock (ConversationGate)
                {
                    Conversation source = GetConv(taiwuId, oldNpcId);
                    Conversation target = GetConv(taiwuId, newNpcId);
                    if (source == null || target == null || !source.LoadReliable || !target.LoadReliable)
                        return false;

                    var merged = CloneConversation(target);
                    merged.WorldId = worldId;
                    merged.TaiwuId = taiwuId;
                    merged.NpcId = newNpcId;
                    merged.CacheKey = ConversationKey(taiwuId, newNpcId);
                    merged.Epoch = _conversationEpoch;
                    merged.ClearEpoch = CurrentClearEpoch(merged.CacheKey);
                    if (string.IsNullOrWhiteSpace(merged.NpcName)) merged.NpcName = source.NpcName;
                    if (string.IsNullOrWhiteSpace(merged.Summary))
                    {
                        merged.Summary = source.Summary;
                        merged.SummarySourceHash = source.SummarySourceHash;
                    }

                    var seenTurns = new HashSet<string>(StringComparer.Ordinal);
                    var combinedTurns = new List<TalkTurn>();
                    Action<IEnumerable<TalkTurn>> appendTurns = turns =>
                    {
                        if (turns == null) return;
                        foreach (TalkTurn turn in turns)
                        {
                            if (turn == null) continue;
                            string key = !string.IsNullOrWhiteSpace(turn.Id) ? "id:" + turn.Id
                                : "legacy:" + (turn.ExchangeId ?? "") + ":" + turn.Date + ":"
                                    + (turn.FromPlayer ? "1" : "0") + ":" + (turn.Text ?? "");
                            if (seenTurns.Add(key)) combinedTurns.Add(CloneTurn(turn));
                        }
                    };
                    appendTurns(source.Turns);
                    appendTurns(target.Turns);
                    merged.Turns = combinedTurns;

                    var pendingMem = new HashSet<string>(StringComparer.Ordinal);
                    foreach (string id in source.PendingMemoryRemovalIds ?? new List<string>())
                        if (!string.IsNullOrWhiteSpace(id)) pendingMem.Add(id);
                    foreach (string id in target.PendingMemoryRemovalIds ?? new List<string>())
                        if (!string.IsNullOrWhiteSpace(id)) pendingMem.Add(id);
                    merged.PendingMemoryRemovalIds = new List<string>(pendingMem);

                    var pendingActions = ClonePendingActionTurns(source.PendingActionTurns);
                    var pendingIds = new HashSet<string>(StringComparer.Ordinal);
                    foreach (PendingActionTurn turn in pendingActions)
                        if (!string.IsNullOrWhiteSpace(turn?.Id)) pendingIds.Add(turn.Id);
                    foreach (PendingActionTurn turn in ClonePendingActionTurns(target.PendingActionTurns))
                        if (turn != null && (string.IsNullOrWhiteSpace(turn.Id) || pendingIds.Add(turn.Id)))
                            pendingActions.Add(turn);
                    merged.PendingActionTurns = pendingActions;

                    string targetKey = ConversationKey(taiwuId, newNpcId);
                    _conversations[targetKey] = merged;
                    CancelActiveCompactionLocked(targetKey);
                    if (!SaveConv(taiwuId, newNpcId, merged)) return false;
                }

                var sourceArchive = ConversationColdArchiveStore.Load(
                    JianghuYoulingPaths.ChatLogs, worldId, taiwuId, oldNpcId);
                var targetArchive = ConversationColdArchiveStore.Load(
                    JianghuYoulingPaths.ChatLogs, worldId, taiwuId, newNpcId);
                if (!sourceArchive.LoadReliable || !targetArchive.LoadReliable) return false;
                foreach (ConversationColdArchiveStore.Segment segment in sourceArchive.Snapshot())
                {
                    if (segment == null || segment.Turns == null || segment.Turns.Count == 0) continue;
                    if (!targetArchive.Append(segment.SourceHash, segment.PriorSummary,
                        segment.PriorSummarySourceHash, segment.Summary, segment.Turns)) return false;
                }
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[江湖有灵] 人物副本单聊迁移失败 " + oldNpcId + "→"
                    + newNpcId + ":" + e.GetType().Name);
                return false;
            }
        }

        internal static bool FinalizeConversationIdentityMigration(int taiwuId, int oldNpcId)
            => PurgeConversationStorage(taiwuId, oldNpcId, false);

        /// <summary>
        /// 旧版在姓名尚未查到时会把“江湖人#123 / NPC#123”写进 UI 或持久化。
        /// 这些只是技术占位符，不能当作人物真名继续传播。
        /// </summary>
        public static bool IsUnresolvedNpcName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            string name = value.Trim();
            if (name == "姓名载入中…" || name == "姓名读取失败" || name == "姓名暂不可用") return true;
            return HasNumericPlaceholderSuffix(name, "江湖人#")
                || HasNumericPlaceholderSuffix(name, "NPC#")
                || HasNumericPlaceholderSuffix(name, "角色#")
                || HasNumericPlaceholderSuffix(name, "#");
        }

        private static bool HasNumericPlaceholderSuffix(string value, string prefix)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(prefix)
                || !value.StartsWith(prefix, StringComparison.Ordinal)
                || value.Length <= prefix.Length) return false;
            for (int i = prefix.Length; i < value.Length; i++)
                if (value[i] < '0' || value[i] > '9') return false;
            return true;
        }

        private static string NormalizeNpcName(string value)
            => IsUnresolvedNpcName(value) ? null : value.Trim();

        private static string ConversationKey(int taiwuId, int npcId)
            => JianghuYoulingPaths.CurrentWorldId + ":" + taiwuId + ":" + npcId;

        private static string ConvPath(int taiwuId, int npcId) => Path.Combine(JianghuYoulingPaths.ChatLogs, "Chat_" + taiwuId + "_" + npcId + ".json");

        /// <summary>
        /// Serializes only the two-file archive/live migration for one transcript. Export takes
        /// the same narrow lock without taking ConversationGate, so parsing a large cold archive
        /// cannot block unrelated gameplay conversations on Unity's main thread.
        /// </summary>
        private static object GetConversationMigrationLock(string directory, uint worldId,
            int taiwuId, int npcId)
        {
            string normalized;
            try { normalized = Path.GetFullPath(directory ?? string.Empty); }
            catch { normalized = directory ?? string.Empty; }
            string key = normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + "|" + worldId + "|" + taiwuId + "|" + npcId;
            lock (ConversationMigrationLockGate)
            {
                if (!ConversationMigrationLocks.TryGetValue(key, out object value))
                    ConversationMigrationLocks[key] = value = new object();
                return value;
            }
        }

        private static Conversation GetConv(int taiwuId, int npcId)
        {
            lock (ConversationGate)
            {
                string key = ConversationKey(taiwuId, npcId);
                if (_conversations.TryGetValue(key, out var cached))
                {
                if (cached.LoadReliable) ReplayPendingMemoryCleanup(cached);
                return cached;
                }
                uint worldId = JianghuYoulingPaths.CurrentWorldId;
                var c = ReadConversationRecoverable(ConvPath(taiwuId, npcId), worldId, taiwuId, npcId) ?? new Conversation();
                if (c.Turns == null) c.Turns = new List<TalkTurn>();
                if (c.PendingMemoryRemovalIds == null) c.PendingMemoryRemovalIds = new List<string>();
                if (c.PendingActionTurns == null) c.PendingActionTurns = new List<PendingActionTurn>();
                c.Version = CurrentConversationVersion; c.WorldId = worldId; c.TaiwuId = taiwuId; c.NpcId = npcId;
                c.Epoch = _conversationEpoch; c.CacheKey = key;
                c.ClearEpoch = CurrentClearEpoch(key);
                ConversationRevisions.TryGetValue(key, out long knownRevision);
                if (c.Revision < knownRevision) c.Revision = knownRevision;
                ConversationRevisions[key] = c.Revision;
                _conversations[key] = c;
                if (c.LoadReliable) ReplayPendingMemoryCleanup(c);
                return c;
            }
        }

        private static bool SaveConv(int taiwuId, int npcId, Conversation c)
        {
            lock (ConversationGate)
            {
                string key = ConversationKey(taiwuId, npcId);
                if (c == null || !c.LoadReliable || c.Epoch != _conversationEpoch || c.CacheKey != key
                    || c.ClearEpoch != CurrentClearEpoch(key)
                    || !_conversations.TryGetValue(key, out var canonical) || !ReferenceEquals(canonical, c)) return false;
                var p = ConvPath(taiwuId, npcId); var tmp = p + ".tmp"; var promote = p + ".promote";
                var indexMutation = ConversationSessionIndexStore.BeginSingleMutation(taiwuId);
                long previous = c.Revision;
                long attemptedRevision = 0;
                string attemptedIntegrity = null;
                bool stagedVerified = false;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(p));
                    ConversationRevisions.TryGetValue(key, out long knownRevision);
                    c.Version = CurrentConversationVersion; c.WorldId = JianghuYoulingPaths.CurrentWorldId; c.TaiwuId = taiwuId; c.NpcId = npcId;
                    c.Revision = Math.Max(previous, knownRevision) + 1;
                    c.IntegritySha256 = ComputeConversationIntegrity(c);
                    if (!ValidConversationSha256(c.IntegritySha256))
                        throw new IOException("对话完整性摘要生成失败");
                    attemptedRevision = c.Revision;
                    attemptedIntegrity = c.IntegritySha256;
                    string json = JsonConvert.SerializeObject(c, Formatting.Indented);
                    byte[] payload = StrictConversationUtf8.GetBytes(json);
                    WriteConversationDurable(tmp, payload);
                    Conversation staged = ParseConversationFile(tmp, c.WorldId, taiwuId, npcId);
                    if (!SameConversationDocument(staged, c))
                        throw new IOException("对话 tmp 完整语义读回不一致");
                    stagedVerified = true;
                    // tmp remains the same-revision recovery replica; publish main from
                    // a separately flushed source so File.Replace cannot consume the only
                    // up-to-date backup.
                    // tmp 已通过严格语义读回；其余副本写的是同一字节串，逐字节核对
                    // 即继承同等校验强度，无需对每个副本重复全文解析。
                    WriteConversationDurable(promote, payload);
                    if (!ConversationReplicaBytesMatch(promote, payload))
                        throw new IOException("对话 promotion 字节读回不一致");
                    if (File.Exists(p)) File.Replace(promote, p, p + ".bak", true);
                    else File.Move(promote, p);
                    Conversation committed = ParseConversationFile(p, c.WorldId, taiwuId, npcId);
                    if (!SameConversationDocument(committed, c))
                        throw new IOException("对话 main 完整语义读回不一致");
                    if (!ConversationReplicaBytesMatch(tmp, payload))
                        throw new IOException("对话 tmp 冗余副本读回不一致");
                    WriteConversationDurable(p + ".bak", payload);
                    if (!ConversationReplicaBytesMatch(p + ".bak", payload))
                        throw new IOException("对话 bak 冗余副本读回不一致");
                    ConversationRevisions[key] = c.Revision;
                    if (QueueSingleConversationIndex(c, p, indexMutation))
                        indexMutation = null; // background queue now owns the dirty-marker lease
                    return true;
                }
                catch (Exception e)
                {
                    c.Revision = previous;
                    // A malformed/incomplete stage is not recovery evidence. Once strict
                    // semantic readback succeeded, preserve it: it is the only proof of
                    // the attempted higher revision if publication failed mid-commit.
                    if (!stagedVerified) try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    RestoreConversationFromDisk(c, p, taiwuId, npcId, key);
                    if (attemptedRevision > 0 && c.LoadReliable && c.Revision == attemptedRevision
                        && string.Equals(c.IntegritySha256, attemptedIntegrity, StringComparison.Ordinal))
                    {
                        ConversationRevisions[key] = c.Revision;
                        if (QueueSingleConversationIndex(c, p, indexMutation))
                            indexMutation = null;
                        return true;
                    }
                    Debug.LogWarning("[江湖有灵] 对话存盘失败: " + e.GetType().Name);
                    return false;
                }
                finally { indexMutation?.Dispose(); }
            }
        }

        private static bool QueueSingleConversationIndex(Conversation conversation, string path,
            ConversationSessionIndexStore.Mutation mutation)
        {
            if (conversation == null || mutation == null) return false;
            string directory = Path.GetDirectoryName(path);
            if (conversation.Turns == null || conversation.Turns.Count == 0)
                return ConversationSessionIndexStore.QueueRemoveSingle(mutation,
                    conversation.WorldId, directory, conversation.TaiwuId, conversation.NpcId);

            int lastDate = -1;
            for (int i = conversation.Turns.Count - 1; i >= 0; i--)
                if (conversation.Turns[i] != null)
                {
                    lastDate = conversation.Turns[i].Date;
                    break;
                }
            MeasureProactiveEngagement(conversation.Turns, out int lastPlayerDate, out int playerConversations,
                out int recentPlayerConversations, out int recentActiveMonths);
            long activityTicks = 0;
            try { activityTicks = File.GetLastWriteTimeUtc(path).Ticks; } catch { }
            return ConversationSessionIndexStore.QueueUpsertSingle(mutation,
                conversation.WorldId, directory, conversation.TaiwuId,
                new ConversationSessionIndexStore.SingleEntry
                {
                    NpcId = conversation.NpcId,
                    Name = NormalizeNpcName(conversation.NpcName),
                    LastDate = lastDate,
                    LastPlayerDate = lastPlayerDate,
                    Turns = conversation.Turns.Count,
                    PlayerConversations = playerConversations,
                    RecentPlayerConversations = recentPlayerConversations,
                    RecentActiveMonths = recentActiveMonths,
                    LastActivityUtcTicks = activityTicks,
                });
        }

        private static void MeasureProactiveEngagement(IList<TalkTurn> turns,
            out int lastPlayerDate, out int playerConversations, out int recentPlayerConversations,
            out int recentActiveMonths)
        {
            lastPlayerDate = 0;
            playerConversations = 0;
            recentPlayerConversations = 0;
            recentActiveMonths = 0;
            if (turns == null || turns.Count == 0) return;
            int latestDate = -1;
            var all = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < turns.Count; i++)
            {
                TalkTurn turn = turns[i];
                if (turn == null || !turn.FromPlayer || TalkTurnKinds.IsNative(turn)) continue;
                string key = !string.IsNullOrWhiteSpace(turn.ExchangeId) ? turn.ExchangeId
                    : !string.IsNullOrWhiteSpace(turn.Id) ? turn.Id : "legacy:" + i;
                all.Add(key);
                if (turn.Date > latestDate) latestDate = turn.Date;
            }
            lastPlayerDate = Math.Max(0, latestDate);
            playerConversations = all.Count;
            if (playerConversations == 0) return;
            int firstRecentDate = latestDate > 0 ? latestDate - 5 : int.MinValue;
            var recent = new HashSet<string>(StringComparer.Ordinal);
            var months = new HashSet<int>();
            for (int i = 0; i < turns.Count; i++)
            {
                TalkTurn turn = turns[i];
                if (turn == null || !turn.FromPlayer || TalkTurnKinds.IsNative(turn)
                    || latestDate > 0 && turn.Date < firstRecentDate) continue;
                string key = !string.IsNullOrWhiteSpace(turn.ExchangeId) ? turn.ExchangeId
                    : !string.IsNullOrWhiteSpace(turn.Id) ? turn.Id : "legacy:" + i;
                recent.Add(key);
                if (turn.Date > 0) months.Add(turn.Date);
            }
            recentPlayerConversations = recent.Count;
            recentActiveMonths = months.Count > 0 ? Math.Min(6, months.Count) : 1;
        }

        private static void ReplayPendingMemoryCleanup(Conversation conversation)
        {
            if (conversation?.PendingMemoryRemovalIds == null || conversation.PendingMemoryRemovalIds.Count == 0) return;
            var ids = DistinctMemoryIds(conversation.PendingMemoryRemovalIds);
            if (!RemoveMemoryAndPortrait(conversation.TaiwuId, conversation.NpcId, ids)) return;

            conversation.PendingMemoryRemovalIds.Clear();
            if (!SaveConv(conversation.TaiwuId, conversation.NpcId, conversation))
                Debug.LogWarning("[江湖有灵] 单聊删除待办已执行但完成标记写盘失败，将在下次载入幂等重放 npc=" + conversation.NpcId);
        }

        private static List<string> DistinctMemoryIds(IEnumerable<string> memoryIds)
        {
            var ids = new List<string>();
            if (memoryIds != null)
                foreach (string id in memoryIds)
                    if (!string.IsNullOrWhiteSpace(id) && !ids.Contains(id)) ids.Add(id);
            return ids;
        }

        private static bool RemoveMemoryAndPortrait(int taiwuId, int npcId, List<string> ids)
        {
            if (ids == null || ids.Count == 0) return true;
            try
            {
                var memory = NpcMemoryStore.Load(JianghuYoulingPaths.Memories, taiwuId.ToString(), npcId.ToString());
                memory.RemoveByIds(ids);
                if (!memory.Save()) return false;
                PortraitService.Invalidate(taiwuId, npcId);
                return PortraitStore.Delete(taiwuId, npcId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[江湖有灵] 按轮撤回长期记忆失败 npc=" + npcId + ":" + ex.GetType().Name);
                return false;
            }
        }

        private static void RestoreConversationFromDisk(Conversation target, string path, int taiwuId, int npcId, string key)
        {
            var disk = ReadConversationRecoverable(path, JianghuYoulingPaths.CurrentWorldId, taiwuId, npcId) ?? new Conversation();
            target.Version = CurrentConversationVersion; target.WorldId = JianghuYoulingPaths.CurrentWorldId;
            target.TaiwuId = taiwuId; target.NpcId = npcId; target.Revision = disk.Revision;
            target.IntegritySha256 = disk.IntegritySha256;
            target.Summary = disk.Summary; target.SummarySourceHash = disk.SummarySourceHash;
            target.NpcName = disk.NpcName;
            target.Turns = disk.Turns ?? new List<TalkTurn>();
            target.PendingMemoryRemovalIds = disk.PendingMemoryRemovalIds ?? new List<string>();
            target.PendingActionTurns = disk.PendingActionTurns ?? new List<PendingActionTurn>();
            target.LoadReliable = disk.LoadReliable;
            target.Epoch = _conversationEpoch; target.ClearEpoch = CurrentClearEpoch(key); target.CacheKey = key;
            ConversationRevisions[key] = target.Revision;
        }

        private static bool ConversationStillCurrent(Conversation c)
        {
            if (c == null) return false;
            lock (ConversationGate)
                return c.Epoch == _conversationEpoch && c.WorldId == JianghuYoulingPaths.CurrentWorldId
                    && c.ClearEpoch == CurrentClearEpoch(c.CacheKey)
                    && c.CacheKey == ConversationKey(c.TaiwuId, c.NpcId)
                    && _conversations.TryGetValue(c.CacheKey, out var canonical) && ReferenceEquals(canonical, c);
        }

        private static long CurrentClearEpoch(string key)
            => key != null && ConversationClearEpochs.TryGetValue(key, out long epoch) ? epoch : 0;

        // 仅在 ConversationGate 内调用。
        private static HashSet<string> DeletedExchangeIds(string key)
        {
            if (!DeletedExchangeIdsByConversation.TryGetValue(key, out var deleted))
            {
                deleted = new HashSet<string>(StringComparer.Ordinal);
                DeletedExchangeIdsByConversation[key] = deleted;
            }
            return deleted;
        }

        public static long CaptureConversationClearEpoch(int taiwuId, int npcId)
        {
            lock (ConversationGate) return CurrentClearEpoch(ConversationKey(taiwuId, npcId));
        }

        private static CompactionLease TryBeginCompaction(Conversation c)
        {
            if (!ConversationStillCurrent(c)) return null;
            lock (ConversationGate)
            {
                if (ActiveCompactions.ContainsKey(c.CacheKey)) return null;
                long token = unchecked(++_nextCompactionToken);
                if (token == 0) token = unchecked(++_nextCompactionToken);
                var lease = new CompactionLease
                {
                    Key = c.CacheKey,
                    Token = token,
                    Cancellation = new System.Threading.CancellationTokenSource(),
                };
                ActiveCompactions[c.CacheKey] = lease;
                return lease;
            }
        }

        private static void EndCompaction(Conversation c, CompactionLease lease)
        {
            if (lease == null) return;
            try
            {
                lock (ConversationGate)
                    if (ActiveCompactions.TryGetValue(lease.Key, out var current)
                        && ReferenceEquals(current, lease))
                        ActiveCompactions.Remove(lease.Key);
            }
            finally
            {
                try { lease.Cancellation?.Dispose(); } catch { }
            }
        }

        // 仅在 ConversationGate 内调用。移除 lease 前先取消网络请求，避免清空/删轮/切档后
        // 后台整理继续白耗额度；旧协程即使稍后醒来，也会被 canonical/epoch gate 拒绝提交。
        private static void CancelActiveCompactionLocked(string key)
        {
            if (string.IsNullOrEmpty(key) || !ActiveCompactions.TryGetValue(key, out var lease)) return;
            try { lease?.Cancellation?.Cancel(); } catch { }
            ActiveCompactions.Remove(key);
        }

        // 压缩先写长期记忆、后写会话，两份文件无法做真正的跨文件事务。用旧逐字段的稳定指纹
        // 作为来源键：会话提交失败或进程在两次写入之间退出时，下次重放会覆盖同一批来源，
        // 不会把相同压缩段再次追加成孤儿/重复记忆。
        private static string CompactionMemorySourcePrefix(Conversation c, IList<TalkTurn> turns)
        {
            unchecked
            {
                ulong hash = 1469598103934665603UL;
                Action<string> mix = value =>
                {
                    string s = value ?? "";
                    for (int i = 0; i < s.Length; i++) { hash ^= s[i]; hash *= 1099511628211UL; }
                    hash ^= 0xff; hash *= 1099511628211UL;
                };
                mix(c?.WorldId.ToString()); mix(c?.TaiwuId.ToString()); mix(c?.NpcId.ToString());
                if (turns != null)
                    foreach (var turn in turns)
                    {
                        if (turn == null) { mix("null"); continue; }
                        mix(turn.Id); mix(turn.ExchangeId); mix(turn.FromPlayer ? "p" : "n"); mix(turn.Kind);
                        mix(turn.Date.ToString()); mix(turn.Text);
                    }
                return "compact:" + hash.ToString("x16");
            }
        }

        private static MemoryEntry CloneCompactionMemory(MemoryEntry source)
        {
            if (source == null) return null;
            return new MemoryEntry
            {
                Id = source.Id, Content = source.Content, Type = source.Type, Keywords = source.Keywords,
                WorldDate = source.WorldDate, Importance = source.Importance, Valid = source.Valid,
                LastRecalled = source.LastRecalled, RecallCount = source.RecallCount,
                RecallMonths = source.RecallMonths, Core = source.Core,
                SourceKind = source.SourceKind, SourceId = source.SourceId,
                SourceLineIds = source.SourceLineIds == null ? null : new List<string>(source.SourceLineIds),
            };
        }

        private static bool RollbackCompactionMemories(int taiwuId, int npcId, IList<string> sourceIds,
            IList<MemoryEntry> priorEntries)
        {
            try
            {
                if ((sourceIds == null || sourceIds.Count == 0) && (priorEntries == null || priorEntries.Count == 0)) return true;
                var rollback = NpcMemoryStore.Load(JianghuYoulingPaths.Memories, taiwuId.ToString(), npcId.ToString());
                if (sourceIds != null && sourceIds.Count > 0) rollback.RemoveBySourceIds(sourceIds);
                if (priorEntries != null)
                    foreach (var prior in priorEntries)
                    {
                        bool merged;
                        if (rollback.AddOrMerge(CloneCompactionMemory(prior), out merged) == null) return false;
                    }
                return rollback.Save();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[江湖有灵] 压缩会话提交失败后的记忆撤回也失败，稳定来源键会在下次重放覆盖: " + ex.GetType().Name);
                return false;
            }
        }

        private static bool CompactionPrefixStillCurrent(Conversation conversation, IList<TalkTurn> old,
            string summaryAtStart, string summarySourceHashAtStart)
        {
            if (!ConversationStillCurrent(conversation)
                || !string.Equals(conversation.Summary, summaryAtStart, StringComparison.Ordinal)
                || !string.Equals(conversation.SummarySourceHash, summarySourceHashAtStart, StringComparison.Ordinal)
                || conversation.Turns == null || old == null || conversation.Turns.Count < old.Count) return false;
            for (int i = 0; i < old.Count; i++)
            {
                TalkTurn current = conversation.Turns[i];
                TalkTurn captured = old[i];
                if (ReferenceEquals(current, captured)) continue;
                if (current == null || captured == null
                    || current.FromPlayer != captured.FromPlayer || current.Date != captured.Date
                    || !string.Equals(current.Id, captured.Id, StringComparison.Ordinal)
                    || !string.Equals(current.ExchangeId, captured.ExchangeId, StringComparison.Ordinal)
                    || !string.Equals(current.Text, captured.Text, StringComparison.Ordinal)
                    || !string.Equals(current.Kind, captured.Kind, StringComparison.Ordinal)
                    || !string.Equals(current.LocationText, captured.LocationText, StringComparison.Ordinal)
                    || !string.Equals(current.ContactMode, captured.ContactMode, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        // JHYL_COMPACTION_OUT_OF_REPLY_CRITICAL_PATH：正文及动作父记录已经可靠提交后才调度。
        // 后台协程第一步就让出一帧，当前帧不构造提示也不启动网络请求，正文可先真正渲染。
        private static void ScheduleCompactionAfterCommit(Conversation conversation, string npcName, int currentDate,
            LlmTraceContext traceRoot)
        {
            if (!ConversationStillCurrent(conversation)) return;
            int cut = ComputeCompactCut(conversation.Turns, KeepRecentTokens, MinKeepTurns);
            if (cut <= 0) return;
            CompactionLease lease = TryBeginCompaction(conversation);
            if (lease == null) return;
            TalkEntryHost host = TalkEntryHost.Instance;
            if (host == null)
            {
                EndCompaction(conversation, lease);
                Debug.LogWarning("[JHYL_COMPACTION_BACKGROUND_SKIP] no_host npc=" + conversation.NpcId);
                return;
            }
            try
            {
                int generation = WorldLifecycle.Generation;
                host.StartCoroutine(RunCompactionAfterCommit(conversation, npcName, currentDate, cut,
                    generation, traceRoot, lease));
                Debug.Log("[JHYL_COMPACTION_BACKGROUND_SCHEDULED] npc=" + conversation.NpcId + " cut=" + cut);
            }
            catch (Exception ex)
            {
                EndCompaction(conversation, lease);
                Debug.LogWarning("[JHYL_COMPACTION_BACKGROUND_SKIP] start_failed=" + ex.GetType().Name
                    + " npc=" + conversation.NpcId);
            }
        }

        private static IEnumerator RunCompactionAfterCommit(Conversation conversation, string npcName, int currentDate,
            int cut, int worldGeneration, LlmTraceContext traceRoot, CompactionLease lease)
        {
            // JHYL_COMPACTION_DEFER_FIRST_FRAME：StartCoroutine 会同步执行至首个 yield；
            // 因此必须在任何整理计算/请求之前先归还当前 UI 帧。
            yield return null;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var ct = lease.Cancellation.Token;
                if (ct.IsCancellationRequested || !WorldLifecycle.IsSameWorld(worldGeneration)
                    || !ConversationStillCurrent(conversation) || conversation.Turns == null
                    || cut <= 0 || cut > conversation.Turns.Count) yield break;

                var old = conversation.Turns.GetRange(0, cut);
                string summaryAtStart = conversation.Summary;
                string summarySourceHashAtStart = conversation.SummarySourceHash;
                OpenAiCompatibleClient compactClient = LlmService.GetBackgroundClient();
                if (compactClient == null) yield break;

                var maintenanceTask = compactClient.SendAsyncWithTrace(
                    ConversationMaintenance.BuildMessages(npcName, summaryAtStart, old),
                    4096, 0.45, ct, 120, false, "对话整理", LlmReasoningPolicy.Off,
                    traceRoot.WithRound(0, "conversation-maintenance-background"));
                while (!maintenanceTask.IsCompleted)
                {
                    if (ct.IsCancellationRequested || !WorldLifecycle.IsSameWorld(worldGeneration)
                        || !ConversationStillCurrent(conversation)) yield break;
                    yield return null;
                }
                if (ct.IsCancellationRequested || !WorldLifecycle.IsSameWorld(worldGeneration)
                    || !CompactionPrefixStillCurrent(conversation, old, summaryAtStart, summarySourceHashAtStart))
                {
                    Debug.Log("[JHYL_COMPACTION_BACKGROUND_STALE] npc=" + conversation.NpcId);
                    yield break;
                }

                bool memoryFlushReady = false;
                List<MemoryEntry> stagedFlush = null;
                string compactedSummary = null;
                try
                {
                    var result = maintenanceTask.Result;
                    if (result != null && result.Ok)
                        memoryFlushReady = ConversationMaintenance.TryParse(result.Content, currentDate,
                            JianghuYouling.Core.Memory.MemoryFlush.BuildAllowedSourceLineIds(old),
                            out compactedSummary, out stagedFlush);
                }
                catch { memoryFlushReady = false; }

                bool memoryFlushSaved = false;
                var compactionSourceIds = new List<string>();
                var rollbackSourceIds = new List<string>();
                var priorCompactionEntries = new List<MemoryEntry>();
                if (!string.IsNullOrWhiteSpace(compactedSummary) && memoryFlushReady
                    && CompactionPrefixStillCurrent(conversation, old, summaryAtStart, summarySourceHashAtStart))
                {
                    try
                    {
                        // 后台请求等待期间，新一轮可能已经写入长期记忆；提交时必须重新 Load，
                        // 绝不能拿调度时的旧 _mem 快照覆盖后来新增的记忆。
                        var compactMemory = NpcMemoryStore.Load(JianghuYoulingPaths.Memories,
                            conversation.TaiwuId.ToString(), conversation.NpcId.ToString());
                        string sourcePrefix = CompactionMemorySourcePrefix(conversation, old);
                        var priorSourceIds = new List<string>();
                        foreach (var existing in compactMemory.All)
                            if (existing != null && existing.SourceKind == CompactionMemorySourceKind
                                && !string.IsNullOrWhiteSpace(existing.SourceId)
                                && existing.SourceId.StartsWith(sourcePrefix + ":", StringComparison.Ordinal))
                            {
                                priorSourceIds.Add(existing.SourceId);
                                rollbackSourceIds.Add(existing.SourceId);
                                priorCompactionEntries.Add(CloneCompactionMemory(existing));
                            }
                        if (priorSourceIds.Count > 0) compactMemory.RemoveBySourceIds(priorSourceIds);

                        for (int i = 0; stagedFlush != null && i < stagedFlush.Count; i++)
                        {
                            MemoryEntry entry = stagedFlush[i];
                            if (entry == null) continue;
                            entry.SourceKind = CompactionMemorySourceKind;
                            entry.SourceId = sourcePrefix + ":" + i;
                            compactionSourceIds.Add(entry.SourceId);
                            if (!rollbackSourceIds.Contains(entry.SourceId)) rollbackSourceIds.Add(entry.SourceId);
                            bool merged;
                            if (compactMemory.AddOrMerge(entry, out merged) == null)
                                throw new InvalidOperationException("压缩记忆来源版本已变化");
                        }
                        bool memoryChanged = priorSourceIds.Count > 0 || compactionSourceIds.Count > 0;
                        memoryFlushSaved = !memoryChanged || compactMemory.Save();
                        if (memoryFlushSaved && compactionSourceIds.Count > 0)
                            Debug.Log("[江湖有灵] 后台压缩记忆flush:幂等提交 " + compactionSourceIds.Count
                                + " 条 npc=" + conversation.NpcId);
                    }
                    catch { memoryFlushSaved = false; }
                }

                // JHYL_COMPACTION_COLD_ARCHIVE_BARRIER：仍先保存逐字原文，再删除 live turns。
                string compactionSourceHash = ConversationColdArchiveStore.ComputeSourceHash(
                    summaryAtStart, summarySourceHashAtStart, old);
                bool archiveSaved = false;
                bool memoryRollbackAttempted = false;
                bool conversationCommitFailed = false;
                bool conversationCommitted = false;
                if (!string.IsNullOrWhiteSpace(compactedSummary) && memoryFlushSaved
                    && !string.IsNullOrWhiteSpace(compactionSourceHash)
                    && CompactionPrefixStillCurrent(conversation, old, summaryAtStart, summarySourceHashAtStart))
                {
                    // Archive append and live-prefix removal form one logical transcript migration.
                    // Export takes the same gate, so it can observe either the complete pre-migration
                    // snapshot or the complete post-migration snapshot, never the gap between them.
                    lock (ConversationGate)
                    {
                        lock (GetConversationMigrationLock(JianghuYoulingPaths.ChatLogs,
                            conversation.WorldId, conversation.TaiwuId, conversation.NpcId))
                        {
                        if (CompactionPrefixStillCurrent(conversation, old, summaryAtStart, summarySourceHashAtStart))
                        {
                            try
                            {
                                var archive = ConversationColdArchiveStore.Load(JianghuYoulingPaths.ChatLogs,
                                    conversation.WorldId, conversation.TaiwuId, conversation.NpcId);
                                archiveSaved = archive.LoadReliable && archive.Append(compactionSourceHash,
                                    summaryAtStart, summarySourceHashAtStart, compactedSummary, old);
                            }
                            catch { archiveSaved = false; }
                            if (archiveSaved
                                && CompactionPrefixStillCurrent(conversation, old, summaryAtStart, summarySourceHashAtStart))
                            {
                                string summaryBeforeCommit = conversation.Summary;
                                string sourceHashBeforeCommit = conversation.SummarySourceHash;
                                var turnsBeforeCommit = new List<TalkTurn>(conversation.Turns);
                                conversation.Summary = compactedSummary;
                                conversation.SummarySourceHash = compactionSourceHash;
                                conversation.Turns.RemoveRange(0, old.Count);
                                // JHYL_COMPACTION_COMMIT_BARRIER：后台也必须原子提交梗概与删减；
                                // SaveConv 失败先恢复完整 live 原文，再在锁外撤回本次整理记忆。
                                if (!SaveConv(conversation.TaiwuId, conversation.NpcId, conversation))
                                {
                                    conversation.Summary = summaryBeforeCommit;
                                    conversation.SummarySourceHash = sourceHashBeforeCommit;
                                    conversation.Turns = turnsBeforeCommit;
                                    conversationCommitFailed = true;
                                }
                                else conversationCommitted = true;
                            }
                        }
                        }
                    }
                    if (!archiveSaved)
                    {
                        memoryRollbackAttempted = true;
                        bool rolledBack = RollbackCompactionMemories(conversation.TaiwuId, conversation.NpcId,
                            rollbackSourceIds, priorCompactionEntries);
                        Debug.LogWarning("[江湖有灵] 后台压缩逐字冷归档失败，保留原文；记忆撤回="
                            + rolledBack + " npc=" + conversation.NpcId);
                    }
                }

                if (conversationCommitFailed)
                {
                    bool rolledBack = RollbackCompactionMemories(conversation.TaiwuId, conversation.NpcId,
                        rollbackSourceIds, priorCompactionEntries);
                    Debug.LogWarning("[江湖有灵] 后台压缩会话提交失败，保留原文；记忆撤回="
                        + rolledBack + " npc=" + conversation.NpcId);
                    yield break;
                }
                if (conversationCommitted)
                {
                    Debug.Log("[JHYL_COMPACTION_BACKGROUND_COMMITTED] npc=" + conversation.NpcId
                        + " cut=" + old.Count + " elapsed_ms=" + elapsed.ElapsedMilliseconds);
                }
                else
                {
                    if (memoryFlushSaved && !archiveSaved && !memoryRollbackAttempted)
                        RollbackCompactionMemories(conversation.TaiwuId, conversation.NpcId,
                            rollbackSourceIds, priorCompactionEntries);
                    Debug.LogWarning("[JHYL_COMPACTION_BACKGROUND_RETAINED] npc=" + conversation.NpcId
                        + " cut=" + old.Count + " elapsed_ms=" + elapsed.ElapsedMilliseconds);
                }
            }
            finally { EndCompaction(conversation, lease); }
        }

        private static Conversation ReadConversationRecoverable(string path, uint worldId, int taiwuId, int npcId)
        {
            Conversation best = null; string bestCandidate = null;
            Conversation main = null;
            bool hadCandidates = false;
            var valid = new List<KeyValuePair<string, Conversation>>();
            var invalid = new List<string>();
            foreach (string candidate in new[] { path, path + ".tmp", path + ".bak" })
            {
                if (!File.Exists(candidate)) continue;
                hadCandidates = true;
                try
                {
                    var c = ParseConversationFile(candidate, worldId, taiwuId, npcId);
                    valid.Add(new KeyValuePair<string, Conversation>(candidate, c));
                    if (string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase)) main = c;
                }
                catch
                {
                    // Invalid candidates remain durable evidence until a quorum can replace them.
                    invalid.Add(candidate);
                }
            }
            if (main != null)
            {
                best = main;
                bestCandidate = path;
                // Do not let an integrity-free legacy main downgrade two matching,
                // strictly newer current-version replicas left by a completed migration.
                if (main.Version < CurrentConversationVersion)
                    foreach (var candidate in valid)
                    {
                        if (candidate.Value.Version != CurrentConversationVersion
                            || candidate.Value.Revision <= main.Revision) continue;
                        int copies = 0;
                        foreach (var other in valid)
                            if (SameConversationDocument(candidate.Value, other.Value)) copies++;
                        if (copies >= 2 && (best == main || candidate.Value.Revision > best.Revision))
                        { best = candidate.Value; bestCandidate = candidate.Key; }
                    }
            }
            else
            {
                foreach (var candidate in valid)
                {
                    int copies = 0;
                    foreach (var other in valid)
                        if (SameConversationDocument(candidate.Value, other.Value)) copies++;
                    if (copies < 2) continue;
                    if (best == null || candidate.Value.Revision > best.Revision)
                    { best = candidate.Value; bestCandidate = candidate.Key; }
                }
            }
            if (best == null && hadCandidates)
            {
                // fail-closed 必须出声:否则玩家只看到"动作日志已损坏"而日志毫无线索。
                Debug.LogWarning("[江湖有灵] 对话文档三副本均无法可靠读取,本会话封闭 file="
                    + Path.GetFileName(path) + " invalid=" + invalid.Count);
                return new Conversation { LoadReliable = false };
            }
            int matchingCopies = 0;
            if (best != null)
                foreach (var candidate in valid)
                    if (SameConversationDocument(best, candidate.Value)) matchingCopies++;
            if (best != null)
                foreach (string candidate in invalid)
                    if (!TryArchiveConversationCorruptCandidate(candidate))
                    {
                        Debug.LogWarning("[江湖有灵] 对话损坏副本归档失败,本会话封闭 file="
                            + Path.GetFileName(candidate));
                        return new Conversation { LoadReliable = false };
                    }
            if (best != null && (!string.Equals(bestCandidate, path, StringComparison.OrdinalIgnoreCase)
                || best.Version == CurrentConversationVersion && matchingCopies < 2))
            {
                // 将有效 tmp/bak 晋升回主文件；否则目录枚举只看 Chat_*.json 时，恢复一次后该会话会消失。
                try
                {
                    string recover = path + ".recover";
                    string json = JsonConvert.SerializeObject(best, Formatting.Indented);
                    WriteConversationDurable(path + ".tmp", json);
                    Conversation replica = ParseConversationFile(path + ".tmp", worldId, taiwuId, npcId);
                    if (!SameConversationDocument(replica, best)) throw new IOException("对话恢复冗余副本读回不一致");
                    WriteConversationDurable(recover, json);
                    Conversation staged = ParseConversationFile(recover, worldId, taiwuId, npcId);
                    if (!SameConversationDocument(staged, best)) throw new IOException("对话恢复副本读回不一致");
                    if (File.Exists(path)) File.Replace(recover, path, path + ".bak", true);
                    else File.Move(recover, path);
                    Conversation committed = ParseConversationFile(path, worldId, taiwuId, npcId);
                    if (!SameConversationDocument(committed, best)) throw new IOException("conversation main recovery mismatch");
                    WriteConversationDurable(path + ".bak", json);
                    Conversation backup = ParseConversationFile(path + ".bak", worldId, taiwuId, npcId);
                    if (!SameConversationDocument(backup, best)) throw new IOException("conversation backup recovery mismatch");
                    if (!SameConversationDocument(committed, best)) throw new IOException("对话恢复主文件读回不一致");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[江湖有灵] 对话恢复副本晋升失败,本会话封闭 file="
                        + Path.GetFileName(path) + ":" + ex.GetType().Name);
                    return new Conversation { LoadReliable = false };
                }
            }
            if (best != null) best.LoadReliable = true;
            return best;
        }

        /// <summary>
        /// Export-only arbitration. It validates the same main/tmp/bak candidates but never
        /// archives corrupt evidence or promotes/repairs a replica. A background export that
        /// outlives its world must be observationally read-only for the captured world.
        /// </summary>
        private static Conversation ReadConversationReadOnly(string path, uint worldId,
            int taiwuId, int npcId)
        {
            Conversation best = null;
            Conversation main = null;
            bool hadCandidates = false;
            var valid = new List<KeyValuePair<string, Conversation>>();
            foreach (string candidate in new[] { path, path + ".tmp", path + ".bak" })
            {
                if (!File.Exists(candidate)) continue;
                hadCandidates = true;
                try
                {
                    Conversation conversation = ParseConversationFile(
                        candidate, worldId, taiwuId, npcId);
                    valid.Add(new KeyValuePair<string, Conversation>(candidate, conversation));
                    if (string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase))
                        main = conversation;
                }
                catch { }
            }
            if (main != null)
            {
                best = main;
                if (main.Version < CurrentConversationVersion)
                    foreach (var candidate in valid)
                    {
                        if (candidate.Value.Version != CurrentConversationVersion
                            || candidate.Value.Revision <= main.Revision) continue;
                        int copies = 0;
                        foreach (var other in valid)
                            if (SameConversationDocument(candidate.Value, other.Value)) copies++;
                        if (copies >= 2 && (best == main
                            || candidate.Value.Revision > best.Revision))
                            best = candidate.Value;
                    }
            }
            else
                foreach (var candidate in valid)
                {
                    int copies = 0;
                    foreach (var other in valid)
                        if (SameConversationDocument(candidate.Value, other.Value)) copies++;
                    if (copies >= 2 && (best == null
                        || candidate.Value.Revision > best.Revision))
                        best = candidate.Value;
                }
            if (best == null)
                return hadCandidates ? new Conversation { LoadReliable = false } : null;
            best.LoadReliable = true;
            return best;
        }

        private static Conversation ParseConversationFile(string path, uint expectedWorldId,
            int expectedTaiwuId, int expectedNpcId)
        {
            long expectedLength = new FileInfo(path).Length;
            if (expectedLength <= 0 || expectedLength > MaxConversationDocumentBytes)
                throw new InvalidDataException("对话文档大小无效");
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.LongLength != expectedLength) throw new IOException("对话文档读取期间发生变化");
            string json = StrictConversationUtf8.GetString(bytes);
            var raw = JObject.Parse(json, new JsonLoadSettings
            {
                CommentHandling = CommentHandling.Ignore,
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
            });
            RejectConversationCaseInsensitiveDuplicates(raw);
            JToken versionToken = raw["Version"] ?? raw["version"];
            int version;
            if (versionToken == null)
            {
                // 0.29 及更早版本写盘的单聊文档没有 Version 字段(仅 Summary/Turns)。
                // 它们是真实玩家存档的主体,必须按 v1 语义接纳;拒绝会让所有旧相识的
                // 对话被 fail-closed 成"动作日志已损坏"。首次保存会升级为当前版本。
                version = 1;
            }
            else
            {
                if (versionToken.Type != JTokenType.Integer)
                    throw new JsonException("对话文档 Version 不是整数");
                version = versionToken.Value<int>();
                if (version < 1 || version > CurrentConversationVersion)
                    throw new JsonException("不支持的对话文档版本 " + version);
            }
            JToken turns = raw["Turns"] ?? raw["turns"];
            if (turns?.Type != JTokenType.Array) throw new JsonException("Turns 不是数组");

            if (version >= 3)
            {
                JToken world = raw["WorldId"] ?? raw["worldId"];
                JToken taiwu = raw["TaiwuId"] ?? raw["taiwuId"];
                JToken npc = raw["NpcId"] ?? raw["npcId"];
                JToken revision = raw["Revision"] ?? raw["revision"];
                JToken pendingMemory = raw["PendingMemoryRemovalIds"] ?? raw["pendingMemoryRemovalIds"];
                JToken pendingActions = raw["PendingActionTurns"] ?? raw["pendingActionTurns"];
                if (world?.Type != JTokenType.Integer || world.Value<uint>() == 0
                    || taiwu?.Type != JTokenType.Integer || taiwu.Value<int>() <= 0
                    || npc?.Type != JTokenType.Integer || npc.Value<int>() <= 0
                    || revision?.Type != JTokenType.Integer || revision.Value<long>() <= 0
                    || pendingMemory?.Type != JTokenType.Array || pendingActions?.Type != JTokenType.Array)
                    throw new JsonException("v3 对话文档缺少完整身份、revision 或持久化事务数组");
                var pendingMemoryIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (JToken item in (JArray)pendingMemory)
                {
                    string id = item?.Type == JTokenType.String ? item.Value<string>() : null;
                    if (string.IsNullOrWhiteSpace(id) || !pendingMemoryIds.Add(id))
                        throw new JsonException("v3 对话记忆清理 ID 无效或重复");
                }
                ValidatePendingActionArray((JArray)pendingActions);
            }

            var conversation = raw.ToObject<Conversation>();
            if (conversation == null || conversation.Turns == null) throw new JsonException("会话为空");
            conversation.Version = version;
            if (conversation.Revision < 0) throw new JsonException("对话 revision 不能为负数");
            if (conversation.WorldId != 0 && conversation.WorldId != expectedWorldId)
                throw new JsonException("WorldId 不匹配");
            if (conversation.TaiwuId != 0 && conversation.TaiwuId != expectedTaiwuId)
                throw new JsonException("TaiwuId 不匹配");
            if (conversation.NpcId != 0 && conversation.NpcId != expectedNpcId)
                throw new JsonException("NpcId 不匹配");
            if (conversation.PendingMemoryRemovalIds == null) conversation.PendingMemoryRemovalIds = new List<string>();
            if (conversation.PendingActionTurns == null) conversation.PendingActionTurns = new List<PendingActionTurn>();
            foreach (TalkTurn turn in conversation.Turns)
                if (turn != null && !string.IsNullOrWhiteSpace(turn.ImageFileName)
                    && !ChatImageReference.IsValid(turn.ImageFileName))
                    throw new JsonException("对话图片引用无效");
            if (version < 3 && conversation.PendingActionTurns.Count > 0)
                throw new JsonException("旧版对话文档不得携带未定义的子操作事务");
            if (version >= 6 && !string.IsNullOrEmpty(conversation.SummarySourceHash)
                && !ValidConversationSha256(conversation.SummarySourceHash))
                throw new JsonException("对话梗概 sourceHash 无效");
            if (version == CurrentConversationVersion)
            {
                string computed = ComputeConversationIntegrity(conversation);
                if (!ValidConversationSha256(conversation.IntegritySha256)
                    || !string.Equals(conversation.IntegritySha256, computed, StringComparison.Ordinal))
                    throw new JsonException("对话文档完整性摘要不匹配");
            }
            return conversation;
        }

        private static void RejectConversationCaseInsensitiveDuplicates(JToken token)
        {
            if (token is JObject obj)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JProperty property in obj.Properties())
                {
                    if (!names.Add(property.Name))
                        throw new JsonException("对话文档含大小写碰撞字段:" + property.Name);
                    RejectConversationCaseInsensitiveDuplicates(property.Value);
                }
                return;
            }
            if (token is JArray array)
                foreach (JToken child in array) RejectConversationCaseInsensitiveDuplicates(child);
        }

        private static void ValidatePendingActionArray(JArray pendingActions)
        {
            if (pendingActions == null || pendingActions.Count > MaxPendingActionTurns)
                throw new JsonException("对话子操作事务数量超过恢复上限");
            var turnIds = new HashSet<string>(StringComparer.Ordinal);
            var operationIds = new HashSet<string>(StringComparer.Ordinal);
            int operationCount = 0;
            foreach (JToken token in pendingActions)
            {
                if (token?.Type != JTokenType.Object) throw new JsonException("对话子操作事务不是对象");
                var turn = (JObject)token;
                string turnId = (turn["Id"] ?? turn["id"])?.Value<string>();
                JToken operations = turn["Operations"] ?? turn["operations"];
                JToken playerInputToken = turn["PlayerInput"] ?? turn["playerInput"];
                JToken dateToken = turn["Date"] ?? turn["date"];
                JToken committedExchangeToken = turn["CommittedExchangeId"] ?? turn["committedExchangeId"];
                if (string.IsNullOrWhiteSpace(turnId) || !turnIds.Add(turnId)
                    || playerInputToken?.Type != JTokenType.String
                    || dateToken?.Type != JTokenType.Integer || dateToken.Value<int>() < 0
                    || operations?.Type != JTokenType.Array)
                    throw new JsonException("对话子操作事务 ID 或 operations 无效");
                if (committedExchangeToken != null && committedExchangeToken.Type != JTokenType.Null
                    && (committedExchangeToken.Type != JTokenType.String
                        || !OperationRpcClient.IsValidOperationId(committedExchangeToken.Value<string>())))
                    throw new JsonException("对话子操作父 exchange 标识无效");
                foreach (JToken operationToken in (JArray)operations)
                {
                    if (++operationCount > MaxPendingOperations)
                        throw new JsonException("对话未决 operation 数量超过恢复上限");
                    if (operationToken?.Type != JTokenType.Object) throw new JsonException("对话子操作条目不是对象");
                    var operation = (JObject)operationToken;
                    string operationId = (operation["OperationId"] ?? operation["operationId"])?.Value<string>();
                    string signature = (operation["Signature"] ?? operation["signature"])?.Value<string>();
                    string tool = (operation["Tool"] ?? operation["tool"])?.Value<string>();
                    string argumentsJson = (operation["ArgumentsJson"] ?? operation["argumentsJson"])?.Value<string>();
                    string status = (operation["Status"] ?? operation["status"]
                        ?? operation["State"] ?? operation["state"])?.Value<string>();
                    if (!OperationRpcClient.IsValidOperationId(operationId) || !operationIds.Add(operationId)
                        || !OperationRpcClient.IsValidOperationId(signature)
                        || string.IsNullOrWhiteSpace(tool) || argumentsJson == null
                        || !string.Equals(signature,
                            OperationId.FromStableKey(tool + "|" + CanonicalJson(argumentsJson)),
                            StringComparison.Ordinal)
                        || !ValidPendingDispatchStatus(status)
                        || !HasConversationProperty(operation, "Retryable", JTokenType.Boolean)
                        || !HasConversationProperty(operation, "RetryableKnown", JTokenType.Boolean)
                        || !HasConversationProperty(operation, "ReceiptAcknowledged", JTokenType.Boolean)
                        || !HasConversationProperty(operation, "ReceiptAcknowledgementQueued", JTokenType.Boolean)
                        || !HasConversationProperty(operation, "UpdatedUtcTicks", JTokenType.Integer)
                        || (operation["UpdatedUtcTicks"] ?? operation["updatedUtcTicks"]).Value<long>() <= 0
                        || !HasNullableConversationString(operation, "Code")
                        || !HasNullableConversationString(operation, "Receipt")
                        || !HasNullableConversationString(operation, "Message")
                        || !HasNullableConversationString(operation, "Result")
                        || !HasNullableConversationString(operation, "State"))
                        throw new JsonException("对话子操作身份、状态无效或重复");
                    bool acknowledged = (operation["ReceiptAcknowledged"]
                        ?? operation["receiptAcknowledged"]).Value<bool>();
                    bool acknowledgementQueued = (operation["ReceiptAcknowledgementQueued"]
                        ?? operation["receiptAcknowledgementQueued"]).Value<bool>();
                    string receipt = (operation["Receipt"] ?? operation["receipt"])?.Value<string>();
                    if (acknowledged && !acknowledgementQueued
                        || acknowledgementQueued && string.IsNullOrWhiteSpace(receipt))
                        throw new JsonException("对话子操作 ACK 状态与回执不一致");
                    if (!string.IsNullOrWhiteSpace(receipt)
                        && !ReceiptSummaryMatchesOperationId(receipt, operationId))
                        throw new JsonException("对话子操作回执与 operation_id 不一致");
                }
            }
        }

        private static bool ValidPendingDispatchStatus(string status)
            => status == "dispatching" || status == "pending" || status == "succeeded"
                || status == "failed" || status == "rejected" || status == "canceled"
                || status == "unknown";

        private static bool ReceiptSummaryMatchesOperationId(string receipt, string operationId)
        {
            if (string.IsNullOrWhiteSpace(receipt)
                || !OperationRpcClient.IsValidOperationId(operationId)) return false;
            foreach (string segment in receipt.Split(';'))
            {
                int split = segment.IndexOf('=');
                if (split <= 0) continue;
                if (string.Equals(segment.Substring(0, split).Trim(), "operation_id", StringComparison.Ordinal))
                    return string.Equals(segment.Substring(split + 1).Trim(), operationId, StringComparison.Ordinal);
            }
            return false;
        }

        private static bool HasConversationProperty(JObject value, string name, JTokenType type)
        {
            JToken token = value[name] ?? value[char.ToLowerInvariant(name[0]) + name.Substring(1)];
            return token != null && token.Type == type;
        }

        private static bool HasNullableConversationString(JObject value, string name)
        {
            JProperty property = value.Property(name, StringComparison.Ordinal)
                ?? value.Property(char.ToLowerInvariant(name[0]) + name.Substring(1), StringComparison.Ordinal);
            return property != null && (property.Value.Type == JTokenType.String
                || property.Value.Type == JTokenType.Null);
        }

        private static void WriteConversationDurable(string path, string content)
            => WriteConversationDurable(path, StrictConversationUtf8.GetBytes(content ?? string.Empty));

        private static void WriteConversationDurable(string path, byte[] bytes)
        {
            if (bytes == null || bytes.Length <= 0 || bytes.Length > MaxConversationDocumentBytes)
                throw new InvalidDataException("对话文档大小无效");
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        /// <summary>仅限同一提交内使用：expected 必须是已通过严格语义读回校验的那份字节串，
        /// 副本逐字节一致即继承同等校验强度；文件缺失或读取异常向上抛出，按存盘失败处理。</summary>
        private static bool ConversationReplicaBytesMatch(string path, byte[] expected)
        {
            byte[] actual = File.ReadAllBytes(path);
            if (expected == null || actual.LongLength != expected.LongLength) return false;
            for (int i = 0; i < actual.Length; i++)
                if (actual[i] != expected[i]) return false;
            return true;
        }

        private static bool SameConversationDocument(Conversation left, Conversation right)
        {
            if (left == null || right == null) return false;
            return JToken.DeepEquals(JToken.FromObject(left), JToken.FromObject(right));
        }

        private static string ComputeConversationIntegrity(Conversation conversation)
        {
            if (conversation == null) return null;
            try
            {
                JObject value = JObject.FromObject(conversation);
                value.Remove("IntegritySha256");
                byte[] digest;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    digest = sha.ComputeHash(StrictConversationUtf8.GetBytes(value.ToString(Formatting.None)));
                var result = new StringBuilder(64);
                foreach (byte b in digest) result.Append(b.ToString("x2"));
                return result.ToString();
            }
            catch { return null; }
        }

        private static bool ValidConversationSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64) return false;
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        private static bool TryArchiveConversationCorruptCandidate(string candidate)
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

        // main 可能在崩溃窗口中只剩 .tmp/.bak；目录功能（历史、月度候选、全量导出）
        // 必须先发现这些可恢复会话，再由 GetConv 将最高 revision 晋升回主文件。
        private static List<int> EnumerateConversationNpcIds(string dir, int taiwuId)
        {
            var found = new HashSet<int>();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir) || taiwuId <= 0) return new List<int>();
            string prefix = "Chat_" + taiwuId + "_";
            foreach (string pattern in new[] { prefix + "*.json", prefix + "*.json.tmp", prefix + "*.json.bak" })
                foreach (string file in Directory.GetFiles(dir, pattern))
                {
                    string name = Path.GetFileName(file);
                    if (string.IsNullOrEmpty(name) || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    string tail = name.Substring(prefix.Length);
                    int jsonAt = tail.IndexOf(".json", StringComparison.OrdinalIgnoreCase);
                    if (jsonAt <= 0) continue;
                    if (int.TryParse(tail.Substring(0, jsonAt), out int npcId) && npcId >= 0 && npcId != taiwuId)
                        found.Add(npcId);
                }
            var result = new List<int>(found);
            result.Sort();
            return result;
        }

        /// <summary>
        /// Discovers pending single-chat mutation journals for every Taiwu identity in
        /// the active world. This is required after succession: no UI path may ever open
        /// an old Taiwu's Chat_* file again, but its exact operation ids still need
        /// query-only reconciliation and durable ACK.
        /// </summary>
        public static void RecoverWorldPendingConversations()
        {
            uint worldId = JianghuYoulingPaths.CurrentWorldId;
            string directory = JianghuYoulingPaths.ChatLogs;
            int generation = WorldLifecycle.Generation;
            if (worldId == 0 || string.IsNullOrWhiteSpace(directory)
                || !Directory.Exists(directory) || !WorldLifecycle.IsSameWorld(generation)) return;
            MonoBehaviour host = TalkEntryHost.Instance;
            if (host == null) host = ConfigHost.Instance;
            if (host == null) return;
            lock (ConversationGate)
            {
                if (_scheduledWorldRecoveryGeneration == generation
                    && _scheduledWorldRecoveryWorldId == worldId) return;
                _scheduledWorldRecoveryGeneration = generation;
                _scheduledWorldRecoveryWorldId = worldId;
            }
            try
            {
                host.StartCoroutine(RecoverWorldPendingConversationsCoroutine(worldId, directory, generation));
            }
            catch (Exception ex)
            {
                ResetScheduledConversationRecovery(worldId, generation);
                Debug.LogWarning("[江湖有灵] 启动单聊事务恢复失败:"
                    + ex.GetType().Name);
            }
        }

        private static IEnumerator RecoverWorldPendingConversationsCoroutine(uint worldId,
            string directory, int generation)
        {
            bool retryPending = false;
            int completedAttempts = 0;
            try
            {
                do
                {
                    if (completedAttempts > 0)
                        yield return new WaitForSecondsRealtime(
                            StartupRecoveryPolicy.DelaySeconds(completedAttempts));
                    retryPending = false;
                    IEnumerator<string> candidates = null;
                    Exception scanError;
                    if (!TryCreateConversationRecoveryEnumerator(directory, out candidates, out scanError))
                    {
                        Debug.LogWarning("[江湖有灵] 扫描单聊事务日志失败:"
                            + (scanError == null ? "Unknown" : scanError.GetType().Name));
                        retryPending = true;
                    }
                    else
                    {
                        try
                        {
                            while (WorldLifecycle.IsSameWorld(generation)
                                && JianghuYoulingPaths.CurrentWorldId == worldId)
                            {
                                bool hasNext;
                                string candidate;
                                if (!TryMoveNextConversationRecoveryCandidate(candidates,
                                    out hasNext, out candidate, out scanError))
                                {
                                    Debug.LogWarning("[江湖有灵] 扫描单聊事务日志失败:"
                                        + (scanError == null ? "Unknown" : scanError.GetType().Name));
                                    retryPending = true;
                                    break;
                                }
                                if (!hasNext) break;
                                if (!TryRecoverConversationCandidate(candidate, worldId, out string recoveryErrorType))
                                {
                                    retryPending = true;
                                    Debug.LogWarning("[江湖有灵] 单聊事务候选恢复失败 error_type="
                                        + recoveryErrorType);
                                }
                                // Enumeration and at most one strict conversation read are spread
                                // over frames, including worlds with a very large chat history.
                                yield return null;
                            }
                        }
                        finally { try { candidates.Dispose(); } catch { } }
                    }
                    completedAttempts++;
                } while (StartupRecoveryPolicy.ShouldRetry(retryPending, completedAttempts)
                    && WorldLifecycle.IsSameWorld(generation)
                    && JianghuYoulingPaths.CurrentWorldId == worldId);

                if (retryPending && WorldLifecycle.IsSameWorld(generation)
                    && JianghuYoulingPaths.CurrentWorldId == worldId)
                    Debug.LogWarning("[江湖有灵] 单聊事务启动恢复已达有界重试上限 attempts="
                        + completedAttempts);
            }
            finally { ResetScheduledConversationRecovery(worldId, generation); }
        }

        private static bool TryRecoverConversationCandidate(string candidate, uint worldId,
            out string errorType)
        {
            errorType = null;
            try
            {
                string path;
                int taiwuId, npcId;
                if (!TryNormalizeConversationRecoveryCandidate(candidate,
                        out path, out taiwuId, out npcId)
                    || !HasPotentialPendingConversation(path)) return true;
                Conversation conversation = GetConv(taiwuId, npcId);
                if (conversation == null || !conversation.LoadReliable)
                { errorType = "UnreliableConversationDocument"; return false; }
                if (conversation.WorldId != worldId || conversation.TaiwuId != taiwuId
                    || conversation.NpcId != npcId) return true;
                if (!ConversationHasRecoveryWork(conversation))
                    EvictRecoveredConversationIfIdle(conversation);
                else
                    BeginRecoveredConversationReconciliation(conversation);
                return true;
            }
            catch (Exception ex)
            {
                errorType = ex.GetType().Name;
                return false;
            }
        }

        private static bool TryCreateConversationRecoveryEnumerator(string directory,
            out IEnumerator<string> candidates, out Exception error)
        {
            candidates = null;
            error = null;
            try
            {
                candidates = Directory.EnumerateFiles(directory, "Chat_*.json*").GetEnumerator();
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                return false;
            }
        }

        private static bool TryMoveNextConversationRecoveryCandidate(IEnumerator<string> candidates,
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

        private static void ResetScheduledConversationRecovery(uint worldId, int generation)
        {
            lock (ConversationGate)
                if (_scheduledWorldRecoveryGeneration == generation
                    && _scheduledWorldRecoveryWorldId == worldId)
                {
                    _scheduledWorldRecoveryGeneration = -1;
                    _scheduledWorldRecoveryWorldId = 0;
                }
        }

        private static bool TryNormalizeConversationRecoveryCandidate(string candidate,
            out string path, out int taiwuId, out int npcId)
        {
            path = candidate;
            taiwuId = 0;
            npcId = 0;
            if (string.IsNullOrWhiteSpace(candidate)) return false;
            bool temporary = candidate.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
            bool backup = candidate.EndsWith(".bak", StringComparison.OrdinalIgnoreCase);
            if (temporary || backup) path = candidate.Substring(0, candidate.Length - 4);
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
            if ((temporary || backup) && File.Exists(path)) return false;
            if (backup && File.Exists(path + ".tmp")) return false;

            string name = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(name)
                || !name.StartsWith("Chat_", StringComparison.OrdinalIgnoreCase)) return false;
            string identity = name.Substring(5, name.Length - 10);
            int separator = identity.IndexOf('_');
            return separator > 0 && separator == identity.LastIndexOf('_')
                && int.TryParse(identity.Substring(0, separator), out taiwuId)
                && int.TryParse(identity.Substring(separator + 1), out npcId)
                && taiwuId > 0 && npcId >= 0 && taiwuId != npcId;
        }

        private static bool HasPotentialPendingConversation(string path)
        {
            foreach (string candidate in new[] { path, path + ".tmp", path + ".bak" })
            {
                if (!File.Exists(candidate)) continue;
                try
                {
                    long length = new FileInfo(candidate).Length;
                    if (length <= 0 || length > MaxConversationDocumentBytes) return true;
                    string json = StrictConversationUtf8.GetString(File.ReadAllBytes(candidate));
                    int key = json.IndexOf("\"PendingActionTurns\"", StringComparison.OrdinalIgnoreCase);
                    if (key < 0) continue;
                    int colon = json.IndexOf(':', key + 20);
                    if (colon < 0) return true;
                    int cursor = colon + 1;
                    while (cursor < json.Length && char.IsWhiteSpace(json[cursor])) cursor++;
                    if (cursor >= json.Length || json[cursor] != '[') return true;
                    cursor++;
                    while (cursor < json.Length && char.IsWhiteSpace(json[cursor])) cursor++;
                    if (cursor >= json.Length || json[cursor] != ']') return true;
                }
                catch { return true; }
            }
            return false;
        }

        private static bool ConversationHasRecoveryWork(Conversation conversation)
        {
            if (conversation?.PendingActionTurns == null) return false;
            foreach (PendingActionTurn turn in conversation.PendingActionTurns)
                if (turn?.Operations != null && turn.Operations.Count > 0) return true;
            return false;
        }

        private static void EvictRecoveredConversationIfIdle(Conversation conversation)
        {
            if (conversation == null) return;
            lock (ConversationGate)
            {
                if (!ConversationStillCurrent(conversation) || ConversationHasRecoveryWork(conversation)
                    || conversation.PendingMemoryRemovalIds?.Count > 0) return;
                _conversations.Remove(conversation.CacheKey);
                ConversationRevisions.Remove(conversation.CacheKey);
            }
        }

        private static void BeginRecoveredConversationReconciliation(Conversation conversation)
        {
            if (conversation == null) return;
            string recoveryKey = conversation.WorldId + ":" + conversation.TaiwuId + ":" + conversation.NpcId;
            var unresolved = new List<PendingToolDispatch>();
            var terminal = new List<PendingToolDispatch>();
            lock (ConversationGate)
            {
                if (!ConversationStillCurrent(conversation)
                    || !RecoveredConversationReconciliations.Add(recoveryKey)) return;
                if (conversation.PendingActionTurns != null)
                    foreach (PendingActionTurn turn in conversation.PendingActionTurns)
                    {
                        // Group-owned child transactions are reconciled exclusively by
                        // GroupExchangeJournal so a slower single-chat query can never
                        // overwrite a terminal group result with retryable unknown.
                        if (turn?.Operations == null || IsGroupOwnedPendingTurn(turn)) continue;
                        foreach (PendingToolDispatch dispatch in turn.Operations)
                        {
                            if (dispatch == null) continue;
                            if (OutcomeFromDispatch(dispatch)?.IsTerminal == true) terminal.Add(dispatch);
                            else if (OperationRpcClient.IsValidOperationId(dispatch.OperationId)) unresolved.Add(dispatch);
                        }
                    }
                if (unresolved.Count == 0) RecoveredConversationReconciliations.Remove(recoveryKey);
            }

            if (unresolved.Count == 0)
            {
                foreach (PendingToolDispatch dispatch in terminal)
                    AcknowledgeDurableDispatch(conversation, conversation.TaiwuId, conversation.NpcId, dispatch);
                return;
            }

            int remaining = unresolved.Count;
            foreach (PendingToolDispatch dispatch in unresolved)
            {
                Action<ToolOutcome> observe = outcome =>
                {
                    bool persisted = false;
                    lock (ConversationGate)
                    {
                        if (ConversationStillCurrent(conversation))
                            MergeRecoveredOutcome(dispatch, outcome);
                        remaining--;
                        if (remaining == 0)
                        {
                            persisted = ConversationStillCurrent(conversation)
                                && SaveConv(conversation.TaiwuId, conversation.NpcId, conversation);
                            RecoveredConversationReconciliations.Remove(recoveryKey);
                        }
                    }
                    if (!persisted) return;
                    foreach (PendingActionTurn turn in conversation.PendingActionTurns)
                        if (turn?.Operations != null && !IsGroupOwnedPendingTurn(turn))
                            foreach (PendingToolDispatch item in turn.Operations)
                                if (item != null && OutcomeFromDispatch(item)?.IsTerminal == true)
                                    AcknowledgeDurableDispatch(conversation, conversation.TaiwuId,
                                        conversation.NpcId, item);
                };
                try
                {
                    EffectHandler.QueryOperation(conversation.WorldId, conversation.TaiwuId,
                        dispatch.OperationId, observe);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[江湖有灵] 单聊事务回执查询失败 op=" + dispatch.OperationId
                        + ":" + ex.GetType().Name);
                    observe(null);
                }
            }
        }

        private static bool IsGroupOwnedPendingTurn(PendingActionTurn turn)
            => turn != null && !string.IsNullOrEmpty(turn.PlayerInput)
                && turn.PlayerInput.StartsWith("[JHYL_GROUP_TURN:", StringComparison.Ordinal);

        private static TalkTurn CloneTurn(TalkTurn turn)
        {
            if (turn == null) return null;
            return new TalkTurn
            {
                Id = turn.Id,
                ExchangeId = turn.ExchangeId,
                FromPlayer = turn.FromPlayer,
                Text = turn.Text,
                Date = turn.Date,
                LocationText = turn.LocationText,
                ContactMode = turn.ContactMode,
                Kind = turn.Kind,
                ImageFileName = turn.ImageFileName,
                MemoryIds = turn.MemoryIds == null ? null : new List<string>(turn.MemoryIds),
                Actions = turn.Actions == null ? null : new List<string>(turn.Actions),
                ToolResults = turn.ToolResults == null ? null : new List<string>(turn.ToolResults),
            };
        }

        /// <summary>Returns an immutable-by-convention deep snapshot; UI/export callers never enumerate live Turns.</summary>
        public static IReadOnlyList<TalkTurn> History(int taiwuId, int npcId)
        {
            lock (ConversationGate)
            {
                var source = GetConv(taiwuId, npcId).Turns;
                var snapshot = new List<TalkTurn>(source?.Count ?? 0);
                if (source != null) foreach (var turn in source) snapshot.Add(CloneTurn(turn));
                return snapshot;
            }
        }

        /// <summary>把已生成场景图绑定到一条已持久化 NPC 回话；只保存受限文件名。</summary>
        internal static bool SetTurnImage(int taiwuId, int npcId, string turnId,
            string imageFileName, out string previousFileName)
        {
            previousFileName = null;
            if (taiwuId <= 0 || npcId < 0 || string.IsNullOrWhiteSpace(turnId)
                || !ChatImageReference.IsValid(imageFileName)) return false;
            lock (ConversationGate)
            {
                Conversation conversation = GetConv(taiwuId, npcId);
                if (conversation == null || !conversation.LoadReliable) return false;
                TalkTurn target = conversation.Turns?.Find(turn => turn != null
                    && !turn.FromPlayer && string.Equals(turn.Id, turnId, StringComparison.Ordinal));
                if (target == null) return false;
                previousFileName = target.ImageFileName;
                if (string.Equals(previousFileName, imageFileName, StringComparison.Ordinal)) return true;
                CancelActiveCompactionLocked(conversation.CacheKey);
                target.ImageFileName = imageFileName;
                if (SaveConv(taiwuId, npcId, conversation)) return true;
                previousFileName = null;
                return false;
            }
        }

        /// <summary>
        /// 把原生游戏事件窗中已经实际发生的选择与文本批量并入该 NPC 的单聊实录。
        /// 捕获端按帧聚合后只写一次三副本会话文件；稳定 Id 令失败重试保持幂等。
        /// </summary>
        internal static bool AppendNativeInteractions(int taiwuId, int npcId, string npcName,
            IList<TalkTurn> incoming)
        {
            if (!WorldLifecycle.HasWorldIdentity || taiwuId <= 0 || npcId < 0 || taiwuId == npcId
                || incoming == null || incoming.Count == 0) return false;
            lock (ConversationGate)
            {
                Conversation conversation = GetConv(taiwuId, npcId);
                if (conversation == null || !conversation.LoadReliable) return false;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                if (conversation.Turns != null)
                    foreach (TalkTurn existing in conversation.Turns)
                        if (!string.IsNullOrWhiteSpace(existing?.Id)) seen.Add(existing.Id);

                int before = conversation.Turns?.Count ?? 0;
                if (conversation.Turns == null) conversation.Turns = new List<TalkTurn>();
                foreach (TalkTurn source in incoming)
                {
                    if (source == null || !TalkTurnKinds.IsNative(source)
                        || string.IsNullOrWhiteSpace(source.Id) || !seen.Add(source.Id)
                        || string.IsNullOrWhiteSpace(source.Text) || source.Text.Length > 262144
                        || string.IsNullOrWhiteSpace(source.ExchangeId)
                        || source.ExchangeId.Length > 128) continue;
                    TalkTurn turn = CloneTurn(source);
                    turn.Text = turn.Text.Trim();
                    turn.MemoryIds = null;
                    turn.Actions = null;
                    turn.ToolResults = null;
                    conversation.Turns.Add(turn);
                }
                if (conversation.Turns.Count == before) return true;
                string normalizedName = NormalizeNpcName(npcName);
                if (!string.IsNullOrWhiteSpace(normalizedName)) conversation.NpcName = normalizedName;
                return SaveConv(taiwuId, npcId, conversation);
            }
        }

        /// <summary>
        /// 群聊的权威原文投影到参与者自己的聊天时间线。它是“参加过的群聊记录”，
        /// 不是 NPC 私聊回复；稳定 Id 使崩溃恢复、重复打开与加人补历史都保持幂等。
        /// affectedExchangeIds 限定本次同步范围：仍存在的轮 upsert，已删除的轮从 live
        /// 单聊窗口撤回。更早已压缩进冷归档的记录保持归档不可变语义。
        /// </summary>
        internal static bool SynchronizeGroupChatTranscripts(int taiwuId, int npcId,
            string npcName, string groupId, IEnumerable<string> affectedExchangeIds,
            IList<TalkTurn> desired)
        {
            if (!WorldLifecycle.HasWorldIdentity || taiwuId <= 0 || npcId < 0 || taiwuId == npcId
                || string.IsNullOrWhiteSpace(groupId) || affectedExchangeIds == null) return false;
            var affectedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (string exchangeId in affectedExchangeIds)
                if (!string.IsNullOrWhiteSpace(exchangeId))
                    affectedIds.Add(GroupChatProjectionTurnId(taiwuId, npcId, groupId, exchangeId));
            if (affectedIds.Count == 0) return true;

            Conversation conversation;
            int newestDate = 0;
            bool added = false;
            lock (ConversationGate)
            {
                conversation = GetConv(taiwuId, npcId);
                if (conversation == null || !conversation.LoadReliable) return false;
                if (conversation.Turns == null) conversation.Turns = new List<TalkTurn>();
                var wanted = new Dictionary<string, TalkTurn>(StringComparer.Ordinal);
                foreach (TalkTurn source in desired ?? Array.Empty<TalkTurn>())
                {
                    if (source == null || !TalkTurnKinds.IsGroupChat(source)
                        || string.IsNullOrWhiteSpace(source.Id) || !affectedIds.Contains(source.Id)
                        || string.IsNullOrWhiteSpace(source.Text) || source.Text.Length > 262144) continue;
                    wanted[source.Id] = CloneTurn(source);
                    if (source.Date > newestDate) newestDate = source.Date;
                }
                HashSet<string> archivedIds = wanted.Count > 0
                    ? RecoverArchivedTurnIds(taiwuId, npcId, wanted.Keys)
                    : new HashSet<string>(StringComparer.Ordinal);
                if (archivedIds == null) return false;

                bool changed = false;
                for (int i = conversation.Turns.Count - 1; i >= 0; i--)
                {
                    TalkTurn existing = conversation.Turns[i];
                    if (existing == null || !TalkTurnKinds.IsGroupChat(existing)
                        || string.IsNullOrWhiteSpace(existing.Id) || !affectedIds.Contains(existing.Id)) continue;
                    if (!wanted.TryGetValue(existing.Id, out TalkTurn replacement))
                    {
                        conversation.Turns.RemoveAt(i);
                        changed = true;
                        continue;
                    }
                    if (!SameProjectedGroupTurn(existing, replacement))
                    {
                        conversation.Turns[i] = replacement;
                        changed = true;
                    }
                    wanted.Remove(existing.Id);
                }

                if (wanted.Count > 0)
                {
                    foreach (TalkTurn turn in wanted.Values)
                    {
                        if (archivedIds.Contains(turn.Id)) continue;
                        int insertAt = conversation.Turns.FindIndex(existing => existing != null
                            && existing.Date > 0 && turn.Date > 0 && existing.Date > turn.Date);
                        if (insertAt < 0) conversation.Turns.Add(turn);
                        else conversation.Turns.Insert(insertAt, turn);
                        changed = true;
                        added = true;
                    }
                }
                if (!changed) return true;
                string normalizedName = NormalizeNpcName(npcName);
                if (!string.IsNullOrWhiteSpace(normalizedName)) conversation.NpcName = normalizedName;
                if (!SaveConv(taiwuId, npcId, conversation)) return false;
            }

            if (added)
            {
                var trace = LlmTraceContext.NewRun("group-history:" + npcId,
                    OperationId.FromStableKey("group-history-exchange|" + groupId));
                ScheduleCompactionAfterCommit(conversation,
                    NormalizeNpcName(npcName) ?? ("NPC#" + npcId), newestDate, trace);
            }
            return true;
        }

        internal static string GroupChatProjectionTurnId(int taiwuId, int npcId,
            string groupId, string exchangeId)
            => "grp-" + OperationId.FromStableKey("group-chat-history|"
                + JianghuYoulingPaths.CurrentWorldId + "|" + taiwuId + "|" + npcId + "|"
                + (groupId ?? string.Empty) + "|" + (exchangeId ?? string.Empty));

        private static bool SameProjectedGroupTurn(TalkTurn left, TalkTurn right)
            => left != null && right != null && left.FromPlayer == right.FromPlayer
                && left.Date == right.Date
                && string.Equals(left.Id, right.Id, StringComparison.Ordinal)
                && string.Equals(left.ExchangeId, right.ExchangeId, StringComparison.Ordinal)
                && string.Equals(left.Text, right.Text, StringComparison.Ordinal)
                && string.Equals(left.Kind, right.Kind, StringComparison.Ordinal);

        private static HashSet<string> RecoverArchivedTurnIds(int taiwuId, int npcId,
            IEnumerable<string> candidates)
        {
            var wanted = new HashSet<string>(candidates ?? Array.Empty<string>(), StringComparer.Ordinal);
            var found = new HashSet<string>(StringComparer.Ordinal);
            if (wanted.Count == 0) return found;
            try
            {
                var archive = ConversationColdArchiveStore.Load(JianghuYoulingPaths.ChatLogs,
                    JianghuYoulingPaths.CurrentWorldId, taiwuId, npcId);
                if (!archive.LoadReliable) return null;
                foreach (TalkTurn turn in archive.RecoverTurns())
                    if (!string.IsNullOrWhiteSpace(turn?.Id) && wanted.Contains(turn.Id))
                        found.Add(turn.Id);
            }
            catch { return null; }
            return found;
        }

        /// <summary>
        /// Export/recovery path for the complete single-chat transcript. Archived
        /// compaction sources are placed before live turns and de-duplicated by turn Id.
        /// A damaged archive fails closed so an export cannot silently claim completeness.
        /// </summary>
        public static bool TryGetCompleteHistory(int taiwuId, int npcId,
            out List<TalkTurn> turns, out bool includesArchive)
        {
            turns = new List<TalkTurn>();
            includesArchive = false;
            try
            {
                // The compaction migration holds this same gate while it appends archived
                // turns and removes their live prefix. Taking one combined snapshot prevents
                // a successful export from silently omitting the prefix mid-migration.
                lock (ConversationGate)
                {
                    var archive = ConversationColdArchiveStore.Load(JianghuYoulingPaths.ChatLogs,
                        JianghuYoulingPaths.CurrentWorldId, taiwuId, npcId);
                    if (!archive.LoadReliable) return false;
                    var seenIds = new HashSet<string>(StringComparer.Ordinal);
                    foreach (TalkTurn archived in archive.RecoverTurns())
                    {
                        if (archived == null) continue;
                        if (!string.IsNullOrWhiteSpace(archived.Id) && !seenIds.Add(archived.Id)) continue;
                        turns.Add(CloneTurn(archived));
                        includesArchive = true;
                    }
                    var live = GetConv(taiwuId, npcId).Turns;
                    if (live != null)
                        foreach (TalkTurn current in live)
                        {
                            if (current == null) continue;
                            if (!string.IsNullOrWhiteSpace(current.Id) && !seenIds.Add(current.Id)) continue;
                            turns.Add(CloneTurn(current));
                        }
                }
                return true;
            }
            catch { turns.Clear(); includesArchive = false; return false; }
        }

        /// <summary>
        /// Background export path bound to an immutable world directory.  It deliberately
        /// bypasses the current-world cache so a save switch cannot redirect half of an export
        /// to another world's transcript.
        /// </summary>
        internal static bool TryGetCompleteHistoryForExport(uint worldId, string directory,
            int taiwuId, int npcId, out string summary, out List<TalkTurn> turns,
            out bool includesArchive)
        {
            summary = null;
            turns = new List<TalkTurn>();
            includesArchive = false;
            if (worldId == 0 || taiwuId <= 0 || npcId < 0
                || string.IsNullOrWhiteSpace(directory)) return false;
            try
            {
                lock (GetConversationMigrationLock(directory, worldId, taiwuId, npcId))
                {
                    var archive = ConversationColdArchiveStore.LoadReadOnly(
                        directory, worldId, taiwuId, npcId);
                    if (!archive.LoadReliable) return false;
                    var seenIds = new HashSet<string>(StringComparer.Ordinal);
                    foreach (TalkTurn archived in archive.RecoverTurns())
                    {
                        if (archived == null) continue;
                        if (!string.IsNullOrWhiteSpace(archived.Id)
                            && !seenIds.Add(archived.Id)) continue;
                        turns.Add(CloneTurn(archived));
                        includesArchive = true;
                    }

                    string path = Path.Combine(directory,
                        "Chat_" + taiwuId + "_" + npcId + ".json");
                    Conversation live = ReadConversationReadOnly(
                        path, worldId, taiwuId, npcId);
                    if (live != null && !live.LoadReliable) return false;
                    summary = live?.Summary;
                    if (live?.Turns != null)
                        foreach (TalkTurn current in live.Turns)
                        {
                            if (current == null) continue;
                            if (!string.IsNullOrWhiteSpace(current.Id)
                                && !seenIds.Add(current.Id)) continue;
                            turns.Add(CloneTurn(current));
                        }
                }
                return true;
            }
            catch
            {
                summary = null;
                turns.Clear();
                includesArchive = false;
                return false;
            }
        }

        public static string SummarySourceHashOf(int taiwuId, int npcId)
            => GetConv(taiwuId, npcId).SummarySourceHash;

        /// <summary>灵儿用:汇总太吾近来与各 NPC 的对话片段(最近活跃的 maxNpcs 人,各取末 maxLines 句)。
        /// 让灵儿知道太吾近来都和谁聊了什么,好在相关话题上主动搭话。无则返回空串。</summary>
        public static string RecentDialogueDigest(int taiwuId, int maxNpcs = 3, int maxLines = 4)
        {
            if (taiwuId <= 0) return "";
            var actives = new List<Conversation>();
            // JHYL_ASSISTANT_RECENT_DIALOGUE_DISK:灵儿不能只看本次运行的内存;重启/未打开的 NPC 对话也要从 ChatLogs 纳入近况。
            try
            {
                if (!TryLoadIndexedConversedPartners(taiwuId,
                    out List<ConversedPartner> partners)) return "";
                int limit = Math.Max(0, Math.Min(maxNpcs, partners.Count));
                for (int i = 0; i < limit; i++)
                    actives.Add(GetConv(taiwuId, partners[i].NpcId));
            }
            catch { }
            actives.RemoveAll(c => c == null || c.Turns == null || c.Turns.Count == 0);
            if (actives.Count == 0) return "";
            // 按最后一句的世界日期降序(近者在前)
            actives.Sort((a, b) =>
            {
                int da = a.Turns[a.Turns.Count - 1].Date, db = b.Turns[b.Turns.Count - 1].Date;
                return db.CompareTo(da);
            });
            var sb = new StringBuilder();
            int taken = 0;
            foreach (var c in actives)
            {
                if (taken >= maxNpcs) break;
                string nm = string.IsNullOrWhiteSpace(c.NpcName) ? "某人" : c.NpcName;
                var lines = new List<string>();
                int start = Math.Max(0, c.Turns.Count - maxLines);
                for (int i = start; i < c.Turns.Count; i++)
                {
                    var t = c.Turns[i];
                    if (t == null || string.IsNullOrWhiteSpace(t.Text)) continue;
                    string txt = t.Text.Length > 40 ? t.Text.Substring(0, 40) + "…" : t.Text;
                    lines.Add(TalkTurnKinds.ContextSpeaker(t, nm) + ":" + txt);
                }
                if (lines.Count == 0) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("· 太吾与「").Append(nm).Append("」近来:").Append(string.Join(" ", lines.ToArray()));
                taken++;
            }
            return sb.ToString();
        }

        /// <summary>过月事件用:太吾与某 NPC 近来对话的【实录摘要】(末 maxLines 句,各句截断)。无则空串。
        /// 据此把太吾在过月故事里的戏份写【实】——只能延展这些真发生过的对话/交集,绝不凭空编造。</summary>
        public static string RecentDialogueWith(int taiwuId, int npcId, int maxLines = 4)
        {
            if (taiwuId <= 0 || npcId < 0) return "";
            var conv = GetConv(taiwuId, npcId);
            var turns = conv != null ? conv.Turns : null;
            if (turns == null || turns.Count == 0) return "";
            string nm = (conv != null && !string.IsNullOrWhiteSpace(conv.NpcName)) ? conv.NpcName : "某人";
            var lines = new List<string>();
            int start = Math.Max(0, turns.Count - maxLines);
            for (int i = start; i < turns.Count; i++)
            {
                var t = turns[i];
                if (t == null || string.IsNullOrWhiteSpace(t.Text)) continue;
                string txt = t.Text.Length > 50 ? t.Text.Substring(0, 50) + "…" : t.Text;
                lines.Add(TalkTurnKinds.ContextSpeaker(t, nm) + ":" + txt);
            }
            return string.Join(" ", lines.ToArray());
        }

        /// <summary>该 NPC 与太吾最后一次"交流"的日期(无则 -1)。供近来对话动因和兼容诊断使用；不再决定过月同道候选资格。
        /// 只认太吾(玩家)真的开口的交流;NPC 无玩家输入的历史行不算一次玩家参与。</summary>
        public static int LastChatDate(int taiwuId, int npcId)
        {
            int marked = PlayerTalkMarkStore.LastDate(taiwuId, npcId);
            var turns = GetConv(taiwuId, npcId).Turns;
            if (turns == null || turns.Count == 0) return marked;
            for (int i = turns.Count - 1; i >= 0; i--) if (turns[i] != null && turns[i].FromPlayer) return Math.Max(marked, turns[i].Date);
            return marked;
        }

        public static List<int> ChattedPartnersForSettlementMonth(int taiwuId, int settlementDate)
        {
            var seen = new HashSet<int>();
            var ids = new List<int>();
            if (taiwuId <= 0) return ids;
            try
            {
                string dir = JianghuYoulingPaths.ChatLogs;
                if (Directory.Exists(dir))
                {
                    foreach (int npcId in EnumerateConversationNpcIds(dir, taiwuId))
                    {
                        int last = LastChatDate(taiwuId, npcId);
                        if (IsSettlementMonth(last, settlementDate) && seen.Add(npcId)) ids.Add(npcId);
                    }
                }
                var marks = PlayerTalkMarkStore.AllDates(taiwuId);
                foreach (var kv in marks)
                    if (kv.Key > 0 && kv.Key != taiwuId && IsSettlementMonth(kv.Value, settlementDate) && seen.Add(kv.Key))
                        ids.Add(kv.Key);
            }
            catch { }
            ids.Sort((a, b) => LastChatDate(taiwuId, b).CompareTo(LastChatDate(taiwuId, a)));
            return ids;
        }

        private static bool IsSettlementMonth(int last, int settlementDate)
        {
            return last >= 0 && (last == settlementDate || (settlementDate > 0 && last == settlementDate - 1));
        }
        /// <summary>太吾近来(withinMonths 个世界月内)聊过的 NPC id,最近优先。扫聊天记录目录(含往期会话,不止本次加载到内存的),
        /// 供同道主动行事与相关排查使用。</summary>
        public static List<int> RecentChatPartners(int taiwuId, int date, int withinMonths = 24, int cap = 12)
        {
            var scored = new List<KeyValuePair<int, int>>();   // (npcId, lastDate)
            if (taiwuId <= 0) return new List<int>();
            try
            {
                if (!TryLoadIndexedConversedPartners(taiwuId,
                    out List<ConversedPartner> partners)) return new List<int>();
                foreach (ConversedPartner partner in partners)
                {
                    int last = partner.LastDate;
                    if (last < 0) continue;
                    if (withinMonths > 0 && date >= 0 && (date - last) > withinMonths) continue;   // 太久没聊的不算(与加权衰减窗口一致)
                    scored.Add(new KeyValuePair<int, int>(partner.NpcId, last));
                }
            }
            catch { }
            scored.Sort((a, b) => b.Value.CompareTo(a.Value));   // 近者在前
            var ids = new List<int>();
            foreach (var kv in scored) { if (ids.Count >= cap) break; ids.Add(kv.Key); }
            return ids;
        }

        /// <summary>
        /// Returns recent-first player-authored ordinary chat lines across recent partners.
        /// Native interaction records and projected group transcripts are excluded because
        /// they are not verbatim free-form player speech suitable for style imitation.
        /// </summary>
        internal static List<string> RecentPlayerSpeechExamples(int taiwuId, int maxExamples)
        {
            var result = new List<string>();
            if (taiwuId <= 0 || maxExamples <= 0) return result;
            try
            {
                List<ConversedPartner> partners = ConversedPartners(taiwuId);
                int partnerCount = 0;
                foreach (ConversedPartner partner in partners)
                {
                    if (partner == null || partner.NpcId < 0 || partnerCount++ >= 8) break;
                    Conversation conversation = GetConv(taiwuId, partner.NpcId);
                    lock (ConversationGate)
                    {
                        if (conversation == null || !conversation.LoadReliable) continue;
                        AppendRecentPlayerSpeech(conversation.Turns, result, maxExamples);
                    }
                    if (result.Count >= maxExamples) break;
                }
            }
            catch { }
            return result;
        }

        private static void AppendRecentPlayerSpeech(IList<TalkTurn> turns,
            List<string> destination, int maxExamples)
        {
            if (turns == null || destination == null) return;
            for (int i = turns.Count - 1; i >= 0 && destination.Count < maxExamples; i--)
            {
                TalkTurn turn = turns[i];
                if (turn == null || !turn.FromPlayer || !string.IsNullOrEmpty(turn.Kind)
                    || string.IsNullOrWhiteSpace(turn.Text)) continue;
                destination.Add(turn.Text);
            }
        }

        /// <summary>列出太吾聊过的所有 NPC(扫聊天记录目录),含名、最后日期、轮数,最近优先。供历史查看窗的「对话」页。</summary>
        public static List<ConversedPartner> ConversedPartners(int taiwuId)
        {
            if (TryLoadIndexedConversedPartners(taiwuId, out List<ConversedPartner> indexed))
                return indexed;
            return new List<ConversedPartner>();
        }

        internal static List<ConversedPartner> ScanConversedPartnersForExport(int taiwuId)
            => ScanConversedPartnersForExport(taiwuId,
                JianghuYoulingPaths.CurrentWorldId, JianghuYoulingPaths.ChatLogs);

        internal static List<ConversedPartner> ScanConversedPartnersForExport(int taiwuId,
            uint worldId, string directory)
            => ScanConversedPartners(taiwuId, worldId, directory, -1);

        internal static bool TryScanConversedPartnersForExport(int taiwuId,
            uint worldId, string directory, out List<ConversedPartner> partners)
        {
            partners = ScanConversedPartners(taiwuId, worldId, directory, -1,
                out bool complete);
            return complete;
        }

        /// <summary>
        /// Fast path used by the sidebar.  It never scans transcripts on the Unity thread;
        /// a missing/dirty derived index is rebuilt by the caller in the background.
        /// </summary>
        internal static bool TryLoadIndexedConversedPartners(int taiwuId,
            out List<ConversedPartner> list)
        {
            list = new List<ConversedPartner>();
            if (taiwuId <= 0) return false;
            if (ConversationSessionIndexStore.TryLoadSingles(taiwuId,
                out List<ConversationSessionIndexStore.SingleEntry> indexed))
            {
                foreach (ConversationSessionIndexStore.SingleEntry entry in indexed)
                    list.Add(new ConversedPartner
                    {
                        NpcId = entry.NpcId,
                        Name = NormalizeNpcName(entry.Name),
                        LastDate = entry.LastDate,
                        LastPlayerDate = entry.LastPlayerDate,
                        Turns = entry.Turns,
                        PlayerConversations = entry.PlayerConversations,
                        RecentPlayerConversations = entry.RecentPlayerConversations,
                        RecentActiveMonths = entry.RecentActiveMonths,
                        LastActivityUtc = SafeUtcFromTicks(entry.LastActivityUtcTicks),
                    });
                list.Sort((a, b) =>
                {
                    int byActivity = b.LastActivityUtc.CompareTo(a.LastActivityUtc);
                    return byActivity != 0 ? byActivity : b.LastDate.CompareTo(a.LastDate);
                });
                return true;
            }
            return false;
        }

        internal static List<ConversationSessionIndexStore.SingleEntry>
            BuildConversedPartnerIndexSnapshot(int taiwuId, uint expectedWorldId,
            string directory, int expectedGeneration)
        {
            if (taiwuId <= 0 || expectedWorldId == 0 || string.IsNullOrWhiteSpace(directory)
                || JianghuYoulingPaths.CurrentWorldId != expectedWorldId
                || !WorldLifecycle.IsSameWorld(expectedGeneration)) return null;
            List<ConversedPartner> list = ScanConversedPartners(
                taiwuId, expectedWorldId, directory, expectedGeneration, out bool complete);
            if (!complete) return null;
            if (JianghuYoulingPaths.CurrentWorldId != expectedWorldId
                || !WorldLifecycle.IsSameWorld(expectedGeneration)) return null;
            return list.ConvertAll(x => new ConversationSessionIndexStore.SingleEntry
                {
                    NpcId = x.NpcId,
                    Name = NormalizeNpcName(x.Name),
                    LastDate = x.LastDate,
                    LastPlayerDate = x.LastPlayerDate,
                    Turns = x.Turns,
                    PlayerConversations = x.PlayerConversations,
                    RecentPlayerConversations = x.RecentPlayerConversations,
                    RecentActiveMonths = x.RecentActiveMonths,
                    LastActivityUtcTicks = x.LastActivityUtc.ToUniversalTime().Ticks,
                });
        }

        private static List<ConversedPartner> ScanConversedPartners(int taiwuId, uint worldId,
            string directory, int expectedGeneration)
            => ScanConversedPartners(taiwuId, worldId, directory, expectedGeneration, out _);

        private static List<ConversedPartner> ScanConversedPartners(int taiwuId, uint worldId,
            string directory, int expectedGeneration, out bool complete)
        {
            var list = new List<ConversedPartner>();
            complete = false;
            try
            {
                if (worldId == 0 || string.IsNullOrWhiteSpace(directory)
                    || !Directory.Exists(directory))
                {
                    complete = true;
                    return list;
                }
                foreach (int npcId in EnumerateConversationNpcIds(directory, taiwuId))
                {
                    if (expectedGeneration >= 0
                        && (JianghuYoulingPaths.CurrentWorldId != worldId
                            || !WorldLifecycle.IsSameWorld(expectedGeneration))) return list;
                    Conversation c;
                    string path = Path.Combine(directory,
                        "Chat_" + taiwuId + "_" + npcId + ".json");
                    // Migration is read-only and never holds ConversationGate while parsing a
                    // potentially large file.  The captured mutation epoch rejects the whole
                    // snapshot if SaveConv begins anywhere during this scan.
                    c = ReadConversationIndexSnapshot(path, worldId, taiwuId, npcId,
                        out DateTime activity);
                    // A discovered identity with no reliable document means the scan is
                    // incomplete (locked, corrupt, or mid-migration). Never publish the partial
                    // set as a clean derived index.
                    if (c == null || !c.LoadReliable) return list;
                    if (c.Turns == null || c.Turns.Count == 0) continue;
                    int last = -1;
                    for (int i = c.Turns.Count - 1; i >= 0; i--) if (c.Turns[i] != null) { last = c.Turns[i].Date; break; }
                    MeasureProactiveEngagement(c.Turns, out int lastPlayerDate, out int playerConversations,
                        out int recentPlayerConversations, out int recentActiveMonths);
                    list.Add(new ConversedPartner
                    {
                        NpcId = npcId,
                        // 留空让调用方从群聊成员表或游戏后端补名；绝不再把内部 id 当显示名。
                        Name = NormalizeNpcName(c.NpcName),
                        LastDate = last,
                        LastPlayerDate = lastPlayerDate,
                        Turns = c.Turns.Count,
                        PlayerConversations = playerConversations,
                        RecentPlayerConversations = recentPlayerConversations,
                        RecentActiveMonths = recentActiveMonths,
                        LastActivityUtc = activity
                    });
                }
                complete = true;
            }
            catch { complete = false; }
            list.Sort((a, b) =>
            {
                int byActivity = b.LastActivityUtc.CompareTo(a.LastActivityUtc);
                return byActivity != 0 ? byActivity : b.LastDate.CompareTo(a.LastDate);
            });
            return list;
        }

        private static Conversation ReadConversationIndexSnapshot(string path, uint worldId,
            int taiwuId, int npcId, out DateTime activityUtc)
        {
            activityUtc = DateTime.MinValue;
            Conversation main = null;
            Conversation best = null;
            var valid = new List<Conversation>();
            var activityByDocument = new Dictionary<Conversation, DateTime>();
            foreach (string candidate in new[] { path, path + ".tmp", path + ".bak" })
            {
                if (!File.Exists(candidate)) continue;
                try
                {
                    Conversation parsed = ParseConversationFile(
                        candidate, worldId, taiwuId, npcId);
                    valid.Add(parsed);
                    try { activityByDocument[parsed] = File.GetLastWriteTimeUtc(candidate); }
                    catch { activityByDocument[parsed] = DateTime.MinValue; }
                    if (string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase))
                        main = parsed;
                }
                catch { }
            }
            if (main != null)
            {
                best = main;
                if (main.Version < CurrentConversationVersion)
                    foreach (Conversation candidate in valid)
                    {
                        if (candidate.Version != CurrentConversationVersion
                            || candidate.Revision <= main.Revision) continue;
                        int copies = 0;
                        foreach (Conversation other in valid)
                            if (SameConversationDocument(candidate, other)) copies++;
                        if (copies >= 2 && (best == main || candidate.Revision > best.Revision))
                            best = candidate;
                    }
            }
            else
                foreach (Conversation candidate in valid)
                {
                    int copies = 0;
                    foreach (Conversation other in valid)
                        if (SameConversationDocument(candidate, other)) copies++;
                    if (copies >= 2 && (best == null || candidate.Revision > best.Revision))
                        best = candidate;
                }
            if (best != null)
            {
                best.LoadReliable = true;
                foreach (Conversation candidate in valid)
                    if (SameConversationDocument(candidate, best)
                        && activityByDocument.TryGetValue(candidate, out DateTime candidateActivity)
                        && candidateActivity > activityUtc)
                        activityUtc = candidateActivity;
            }
            return best;
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

        public static string SummaryOf(int taiwuId, int npcId) => GetConv(taiwuId, npcId).Summary;
        /// <summary>清空某 NPC 的聊天记录(会话 + 梗概 + 文件)。供对话框"清空记录"用。</summary>
        public static bool ClearConversation(int taiwuId, int npcId)
        {
            // 必须先作废飞行中的 seed/consolidate；否则删盘后旧 LLM 结果会把画像复活。
            PortraitService.Invalidate(taiwuId, npcId);
            bool chatCleared = PurgeConversationStorage(taiwuId, npcId, true);
            // 一并清除该 NPC 的长期记忆与蒸馏画像(对应记忆),彻底重置——否则"清空"后 NPC 仍记得旧事
            bool memoryCleared = false, portraitCleared = false;
            try { memoryCleared = JianghuYouling.Core.Memory.NpcMemoryStore.DeleteFile(JianghuYoulingPaths.Memories, taiwuId.ToString(), npcId.ToString()); } catch { }
            try { portraitCleared = PortraitStore.Delete(taiwuId, npcId); } catch { }
            return chatCleared && memoryCleared && portraitCleared;
        }

        /// <summary>统一清除会话主文件/临时文件/备份并提升 epoch；旧页签持有的 Conversation 随后不能再保存。</summary>
        public static bool PurgeConversationStorage(int taiwuId, int npcId, bool removeTalkMark)
        {
            bool ok = true;
            lock (ConversationGate)
            {
                uint worldId = JianghuYoulingPaths.CurrentWorldId;
                lock (GetConversationMigrationLock(JianghuYoulingPaths.ChatLogs,
                    worldId, taiwuId, npcId))
                {
                    ConversationSessionIndexStore.Mutation indexMutation =
                        ConversationSessionIndexStore.BeginSingleMutation(taiwuId);
                    try
                    {
                        string key = ConversationKey(taiwuId, npcId);
                        long clearEpoch = unchecked(CurrentClearEpoch(key) + 1);
                        ConversationClearEpochs[key] = clearEpoch;
                        CancelActiveCompactionLocked(key);
                        DeletedExchangeIdsByConversation.Remove(key);
                        ConversationRevisions.TryGetValue(key, out long revision);
                        revision++;
                        ConversationRevisions[key] = revision;
                        var empty = new Conversation
                        {
                            WorldId = worldId,
                            TaiwuId = taiwuId,
                            NpcId = npcId,
                            Revision = revision,
                            Epoch = _conversationEpoch,
                            ClearEpoch = clearEpoch,
                            CacheKey = key,
                        };
                        _conversations[key] = empty;
                        try
                        {
                            string path = ConvPath(taiwuId, npcId);
                            string parent = Path.GetDirectoryName(path);
                            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                                foreach (string candidate in Directory.GetFiles(parent,
                                    Path.GetFileName(path) + ".corrupt-*"))
                                    if (File.Exists(candidate)) File.Delete(candidate);
                            if (!DurableFileStore.TryDeleteAllArtifacts(path)) ok = false;
                            if (!ConversationColdArchiveStore.Delete(JianghuYoulingPaths.ChatLogs,
                                worldId, taiwuId, npcId)) ok = false;
                        }
                        catch (Exception e)
                        {
                            ok = false;
                            Debug.LogWarning("[江湖有灵] 清除对话文件失败:" + e.GetType().Name);
                        }
                        if (ok && ConversationSessionIndexStore.QueueRemoveSingle(indexMutation,
                            worldId, JianghuYoulingPaths.ChatLogs, taiwuId, npcId))
                            indexMutation = null;
                        else if (ok) ok = false;
                    }
                    finally
                    {
                        indexMutation?.Dispose();
                    }
                }
            }
            if (removeTalkMark) try { if (!PlayerTalkMarkStore.Remove(taiwuId, npcId)) ok = false; } catch { ok = false; }
            return ok;
        }

        /// <summary>丢弃最近一轮(玩家+回话)。供「重试」重新生成本轮,免历史重复。</summary>
        public static void DropLastExchange(int taiwuId, int npcId)
        {
            var c = GetConv(taiwuId, npcId);
            int n = c.Turns.Count;
            if (n >= 2) c.Turns.RemoveRange(n - 2, 2);
            else if (n == 1) c.Turns.RemoveAt(0);
            SaveConv(taiwuId, npcId, c);
        }

        /// <summary>按轮删除/重试:从会话里按【引用】抹掉指定的若干 turn(不依赖下标,删中间轮也稳),
        /// 并硬删除这些回话轮沉淀的长期记忆条目(memoryIds)。只删记忆,绝不回退任何已落地的游戏行为。</summary>
        public static bool DeleteExchange(int taiwuId, int npcId, IEnumerable<TalkTurn> turns, IEnumerable<string> memoryIds)
        {
            var targets = new List<TalkTurn>();
            if (turns != null) foreach (var turn in turns) if (turn != null) targets.Add(turn);
            var requestedExchangeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in targets)
                if (!string.IsNullOrWhiteSpace(target.ExchangeId)) requestedExchangeIds.Add(target.ExchangeId);
            var ids = DistinctMemoryIds(memoryIds);

            // 错误提示/调试回话没有落入会话或长期记忆，属于纯 UI 轮次，无须为了删除它改写空文件。
            if (targets.Count == 0 && ids.Count == 0) return true;

            if (targets.Count > 0)
            {
                lock (ConversationGate)
                {
                    var current = GetConv(taiwuId, npcId);
                    var edited = CloneConversation(current);
                    string key = ConversationKey(taiwuId, npcId);
                    var deletedExchanges = DeletedExchangeIds(key);
                    bool removed = false;
                    foreach (var target in targets)
                    {
                        int index = -1;
                        if (!string.IsNullOrEmpty(target.Id))
                            index = edited.Turns.FindIndex(t => t != null && t.Id == target.Id);
                        if (index < 0 && !string.IsNullOrEmpty(target.ExchangeId))
                            index = edited.Turns.FindIndex(t => t != null && t.ExchangeId == target.ExchangeId);
                        if (index < 0) index = edited.Turns.IndexOf(target);
                        // 旧版无 Id/ExchangeId 的记录在一次写失败后会从磁盘恢复成新对象；按内容兜底定位，
                        // 让保留在 UI 上的同一轮仍可再次点击删除，而不会误报成功。
                        if (index < 0 && string.IsNullOrEmpty(target.Id) && string.IsNullOrEmpty(target.ExchangeId))
                            index = edited.Turns.FindIndex(t => SameLegacyTurn(t, target));
                        if (index < 0) continue; // 上次可能已删会话、仅长期记忆失败；后续仍需重试记忆。

                        string exchangeId = edited.Turns[index]?.ExchangeId;
                        int count;
                        if (!string.IsNullOrEmpty(exchangeId))
                            count = edited.Turns.RemoveAll(t => t != null && t.ExchangeId == exchangeId);
                        else
                        {
                            edited.Turns.RemoveAt(index);
                            count = 1;
                        }
                        removed |= count > 0;
                    }
                    if (removed)
                    {
                        // Copy-on-write 立即替换 canonical：其它页签在旧 conv 上等待的画像、压缩或
                        // LLM 续程会因 ReferenceEquals 失败而作废，不能把已删轮 flush/RemoveRange/写回。
                        foreach (string memoryId in ids)
                            if (!edited.PendingMemoryRemovalIds.Contains(memoryId)) edited.PendingMemoryRemovalIds.Add(memoryId);
                        _conversations[key] = edited;
                        CancelActiveCompactionLocked(key);
                        if (!SaveConv(taiwuId, npcId, edited)) return false;
                        foreach (string exchangeId in requestedExchangeIds) deletedExchanges.Add(exchangeId);
                    }
                    else
                    {
                        // Turns 中找不到不代表“上次已经删过”：它也可能刚被压入尚未提交/不可逆
                        // 定位的 Summary。只有本进程成功提交过删除的 exchange tombstone 才算幂等重试。
                        if (requestedExchangeIds.Count == 0) return false;
                        foreach (string exchangeId in requestedExchangeIds)
                            if (!deletedExchanges.Contains(exchangeId)) return false;

                        // 兼容本进程早先已提交会话删除、但尚未带 durable journal 的重试。
                        bool pendingChanged = false;
                        foreach (string memoryId in ids)
                            if (!current.PendingMemoryRemovalIds.Contains(memoryId))
                            {
                                current.PendingMemoryRemovalIds.Add(memoryId);
                                pendingChanged = true;
                            }
                        if (pendingChanged && !SaveConv(taiwuId, npcId, current)) return false;
                    }
                }
            }

            if (ids.Count > 0)
            {
                if (!RemoveMemoryAndPortrait(taiwuId, npcId, ids)) return false;
                lock (ConversationGate)
                {
                    string key = ConversationKey(taiwuId, npcId);
                    if (!_conversations.TryGetValue(key, out var canonical)) return false;
                    int removedPending = canonical.PendingMemoryRemovalIds.RemoveAll(id => ids.Contains(id));
                    if (removedPending > 0 && !SaveConv(taiwuId, npcId, canonical)) return false;
                }
            }
            return true;
        }

        private static bool SameLegacyTurn(TalkTurn left, TalkTurn right)
            => left != null && right != null && left.FromPlayer == right.FromPlayer && left.Date == right.Date
                && string.Equals(left.Kind, right.Kind, StringComparison.Ordinal)
                && string.Equals(left.Text, right.Text, StringComparison.Ordinal);

        private static Conversation CloneConversation(Conversation source)
        {
            return new Conversation
            {
                Version = source.Version,
                WorldId = source.WorldId,
                TaiwuId = source.TaiwuId,
                NpcId = source.NpcId,
                Revision = source.Revision,
                IntegritySha256 = source.IntegritySha256,
                Summary = source.Summary,
                SummarySourceHash = source.SummarySourceHash,
                NpcName = source.NpcName,
                Turns = source.Turns == null ? new List<TalkTurn>() : new List<TalkTurn>(source.Turns),
                PendingMemoryRemovalIds = source.PendingMemoryRemovalIds == null
                    ? new List<string>() : new List<string>(source.PendingMemoryRemovalIds),
                PendingActionTurns = ClonePendingActionTurns(source.PendingActionTurns),
                Epoch = source.Epoch,
                ClearEpoch = source.ClearEpoch,
                CacheKey = source.CacheKey,
                LoadReliable = source.LoadReliable,
            };
        }

        private static List<PendingActionTurn> ClonePendingActionTurns(List<PendingActionTurn> source)
        {
            var copy = new List<PendingActionTurn>();
            if (source == null) return copy;
            foreach (var turn in source)
            {
                if (turn == null) continue;
                var cloned = new PendingActionTurn
                {
                    Id = turn.Id,
                    PlayerInput = turn.PlayerInput,
                    Date = turn.Date,
                    CommittedExchangeId = turn.CommittedExchangeId,
                };
                if (turn.Operations != null)
                    foreach (var op in turn.Operations)
                        if (op != null) cloned.Operations.Add(new PendingToolDispatch
                        {
                            Signature = op.Signature, Tool = op.Tool, OperationId = op.OperationId,
                            ArgumentsJson = op.ArgumentsJson, Status = op.Status, Code = op.Code,
                            Retryable = op.Retryable, RetryableKnown = op.RetryableKnown,
                            Receipt = op.Receipt, Message = op.Message,
                            Result = op.Result, ReceiptAcknowledged = op.ReceiptAcknowledged,
                            ReceiptAcknowledgementQueued = op.ReceiptAcknowledgementQueued,
                            State = op.State, UpdatedUtcTicks = op.UpdatedUtcTicks,
                        });
                copy.Add(cloned);
            }
            return copy;
        }

        private static PendingActionTurn FindPendingActionTurn(Conversation conv, string playerInput, int date)
        {
            if (conv?.PendingActionTurns == null) return null;
            for (int i = conv.PendingActionTurns.Count - 1; i >= 0; i--)
            {
                var turn = conv.PendingActionTurns[i];
                // A committed parent exchange owns this journal forever. It may remain
                // for query-only recovery, but a new identical player utterance must
                // receive a fresh operation scope.
                if (turn != null && !string.IsNullOrWhiteSpace(turn.CommittedExchangeId)) continue;
                if (turn != null && turn.Date == date
                    && string.Equals(turn.PlayerInput ?? "", playerInput ?? "", StringComparison.Ordinal)) return turn;
                if (turn != null && turn.Date != date
                    && string.Equals(turn.PlayerInput ?? "", playerInput ?? "", StringComparison.Ordinal)
                    && !CanPrunePendingActionTurn(turn)) return turn; // 跨月只承接仍未解歧/未 ACK 的动作。
            }
            return null;
        }

        private string CurrentPendingTurnInputKey()
            => GroupCtx == null ? (_currentPlayerInput ?? string.Empty)
                : GroupPendingTurnInputKey(GroupCtx.GroupId, GroupCtx.ExchangeId, GroupCtx.AttemptId, _currentPlayerInput);

        private static string GroupPendingTurnInputKey(string groupId, string exchangeId, string attemptId, string playerInput)
            => "[JHYL_GROUP_TURN:" + (groupId ?? string.Empty) + ":" + (exchangeId ?? string.Empty)
                + (string.IsNullOrWhiteSpace(attemptId) ? string.Empty : (":attempt:" + attemptId))
                + "]" + (playerInput ?? string.Empty);

        private string PendingActionNote()
        {
            var turn = _activePendingActionTurn;
            if (turn?.Operations == null || turn.Operations.Count == 0) return null;
            var sb = new StringBuilder("【未完成回合的真实派发日志】上一次处理同一句太吾输入时，以下动作已经派发。相同动作不得再次执行：");
            foreach (var op in turn.Operations)
            {
                if (op == null) continue;
                sb.Append("\n- ").Append(op.Tool ?? "?").Append("：").Append(DispatchStatus(op));
                if (!string.IsNullOrWhiteSpace(op.Result)) sb.Append("（").Append(ShortLog(op.Result, 120)).Append("）");
            }
            sb.Append("\n若状态 succeeded，就承接既成事实；failed 就据实说明未成；dispatching/unknown 只可说尚未确认，绝不可重发或假称成功。");
            return sb.ToString();
        }

        private static ReactionCommitState ReactionStateFromPendingTurn(PendingActionTurn turn)
        {
            if (turn?.Operations == null) return ReactionCommitState.NotAttempted;
            bool succeeded = false, failed = false;
            foreach (PendingToolDispatch operation in turn.Operations)
            {
                if (operation == null || !string.Equals(operation.Tool, "record_reaction", StringComparison.Ordinal)) continue;
                string status = DispatchStatus(operation);
                // Any unresolved reaction blocks every new reaction signature. Otherwise
                // a model could change one numeric argument and evade signature dedupe.
                if (status == "dispatching" || status == "pending" || status == "unknown")
                    return ReactionCommitState.Unknown;
                if (status == "succeeded") succeeded = true;
                else if (status == "failed" || status == "rejected" || status == "canceled") failed = true;
            }
            return succeeded ? ReactionCommitState.Succeeded
                : (failed ? ReactionCommitState.Failed : ReactionCommitState.NotAttempted);
        }

        private bool PrepareDurableToolDispatch(Conversation conv, NpcSnapshot snap, string tool, string argsJson,
            out PendingToolDispatch dispatch, out string priorResult)
        {
            dispatch = null;
            priorResult = null;
            if (conv == null || !conv.LoadReliable || snap == null)
            { priorResult = "(派发日志不可靠，为防止重复落地，动作未执行。)"; return false; }
            if (conv.PendingActionTurns == null) conv.PendingActionTurns = new List<PendingActionTurn>();
            if (_activePendingActionTurn == null)
            {
                string pendingInputKey = CurrentPendingTurnInputKey();
                _activePendingActionTurn = FindPendingActionTurn(conv, pendingInputKey, snap.CurrentDate);
                if (_activePendingActionTurn == null)
                {
                    // 旧终态可清；dispatching/pending/unknown 或尚未 ACK 的后端回执必须跨月保留，
                    // 否则一次回包丢失会在月界把防重凭据删掉。
                    conv.PendingActionTurns.RemoveAll(t => t == null || (snap.CurrentDate > 1
                        && t.Date < snap.CurrentDate - 1 && CanPrunePendingActionTurn(t)));
                    _activePendingActionTurn = new PendingActionTurn
                    {
                        Id = Guid.NewGuid().ToString("N"), PlayerInput = pendingInputKey, Date = snap.CurrentDate,
                    };
                    conv.PendingActionTurns.Add(_activePendingActionTurn);
                }
            }
            if (_activePendingActionTurn.Operations == null) _activePendingActionTurn.Operations = new List<PendingToolDispatch>();
            string canonicalArgs = CanonicalJson(argsJson);
            string signature = OperationId.FromStableKey((tool ?? "") + "|" + canonicalArgs);
            foreach (var existing in _activePendingActionTurn.Operations)
            {
                if (existing == null || existing.Signature != signature) continue;
                dispatch = existing;
                string existingStatus = DispatchStatus(existing);
                // A terminal failed reaction proves that no relationship mutation was
                // applied. Unlike an unknown receipt, it is therefore safe to create a
                // fresh operation for the mandatory per-turn reaction. Keep scanning in
                // case a later retry with the same signature already succeeded/pended.
                if (string.Equals(tool, "record_reaction", StringComparison.Ordinal)
                    && (existingStatus == "failed" || existingStatus == "rejected"
                        || existingStatus == "canceled"))
                {
                    dispatch = null;
                    continue;
                }
                if (existingStatus == "succeeded")
                {
                    priorResult = "(DUPLICATE_OPERATION_BLOCKED：同一句请求中的相同动作此前已权威成功，本次没有重复执行。此前结果："
                        + (existing.Result ?? "已完成") + ")";
                    bool restoredSecret = string.Equals(existing.Tool, "tell_secret", StringComparison.Ordinal)
                        && RestoreSuccessfulSecretDisclosureFromFrozenArguments(existing.ArgumentsJson, snap);
                    if (!restoredSecret && _landedThisTurn != null)
                        _landedThisTurn.Add("承接此前已完成的" + (existing.Tool ?? "动作"));
                }
                else if (existingStatus == "unknown" && existing.RetryableKnown && !existing.Retryable)
                    priorResult = "(DUPLICATE_OPERATION_BLOCKED：相同动作的权威回执已确定无法再恢复，不能判断成败，"
                        + "本次仍不会换 operationId 重发；请只说实情不可确认。此前记录："
                        + (existing.Result ?? existing.Message ?? "回执不可恢复") + ")";
                else if (existingStatus == "failed" || existingStatus == "rejected" || existingStatus == "canceled")
                    priorResult = "(DUPLICATE_OPERATION_BLOCKED：相同动作此前已确定失败，本次没有重复执行；请据实说明未成，别假称完成。此前结果："
                        + (existing.Result ?? "未完成") + ")";
                else
                    priorResult = "(JHYL_ACTION_UNCONFIRMED：相同动作此前已派发但回执仍为" + existingStatus
                        + "，本次没有重复执行。不得声称成功，也不得换 operationId 重发。)";
                return false;
            }

            dispatch = new PendingToolDispatch
            {
                Signature = signature, Tool = tool, OperationId = Guid.NewGuid().ToString("N"),
                ArgumentsJson = canonicalArgs, Status = "dispatching", State = "dispatching",
                Code = "prepared", Retryable = true, RetryableKnown = true, UpdatedUtcTicks = DateTime.UtcNow.Ticks,
            };
            _activePendingActionTurn.Operations.Add(dispatch);
            if (SaveConv(snap.TaiwuId, snap.NpcId, conv)) return true;
            _activePendingActionTurn = FindPendingActionTurn(conv, CurrentPendingTurnInputKey(), snap.CurrentDate);
            dispatch = null;
            priorResult = "(动作派发日志无法可靠落盘，出于防重复保护，本动作没有执行。请稍后检查磁盘权限再试，别假称完成。)";
            return false;
        }

        private bool CommitDurableToolDispatch(Conversation conv, NpcSnapshot snap, PendingToolDispatch dispatch,
            string result, ToolOutcome outcome)
        {
            if (dispatch == null || conv == null || snap == null) return true;
            outcome = outcome ?? ToolOutcome.Unknown(dispatch.OperationId, "执行器没有返回结构化回执");
            if (!string.IsNullOrWhiteSpace(outcome.OperationId)
                && !string.Equals(outcome.OperationId, dispatch.OperationId, StringComparison.Ordinal))
                outcome = ToolOutcome.Unknown(dispatch.OperationId, "回执 operation_id 与派发日志不一致，拒绝采信");
            dispatch.Status = string.IsNullOrWhiteSpace(outcome.Status) ? "unknown" : outcome.Status;
            dispatch.State = dispatch.Status; // 旧版本读取兼容；状态决策只使用 Status。
            dispatch.Code = outcome.Code;
            dispatch.Retryable = outcome.Retryable;
            dispatch.RetryableKnown = true;
            dispatch.Receipt = outcome.Receipt;
            dispatch.Message = outcome.Message;
            dispatch.Result = string.IsNullOrEmpty(result) ? "(无结果)" : (result.Length <= 800 ? result : result.Substring(0, 800));
            dispatch.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
            // JHYL_TERMINAL_SAVE_BEFORE_ACK：终态必须先可靠落盘，才允许压缩后端完整回执。
            if (SaveConv(snap.TaiwuId, snap.NpcId, conv))
            {
                if (outcome.IsTerminal) AcknowledgeDurableDispatch(conv, snap, dispatch);
                return true;
            }
            _activePendingActionTurn = FindPendingActionTurn(conv, CurrentPendingTurnInputKey(), snap.CurrentDate);
            return false;
        }

        private static string DispatchStatus(PendingToolDispatch dispatch)
        {
            if (dispatch == null) return "unknown";
            if (!string.IsNullOrWhiteSpace(dispatch.Status)) return dispatch.Status;
            return string.IsNullOrWhiteSpace(dispatch.State) ? "unknown" : dispatch.State;
        }

        private static bool CanPrunePendingActionTurn(PendingActionTurn turn)
        {
            if (turn?.Operations == null || turn.Operations.Count == 0) return true;
            foreach (var dispatch in turn.Operations)
            {
                if (dispatch == null) continue;
                bool terminal = OutcomeFromDispatch(dispatch)?.IsTerminal == true;
                if (!terminal) return false;
                if (!DispatchAcknowledgementDurable(dispatch)) return false;
            }
            return true;
        }

        private static ToolOutcome OutcomeFromDispatch(PendingToolDispatch dispatch)
        {
            if (dispatch == null) return null;
            return new ToolOutcome
            {
                Status = DispatchStatus(dispatch), Code = dispatch.Code,
                Retryable = DispatchStatus(dispatch) == "unknown" && !dispatch.RetryableKnown
                    ? true : dispatch.Retryable,
                OperationId = dispatch.OperationId,
                Receipt = dispatch.Receipt, Message = dispatch.Message ?? dispatch.Result,
            };
        }

        /// <summary>
        /// Query callbacks may race a live mutation callback or another recovery path.
        /// Once an exact operation has reached a terminal state, never let a later
        /// pending/unknown (or conflicting terminal) observation regress that evidence.
        /// </summary>
        private static bool MergeRecoveredOutcome(PendingToolDispatch dispatch, ToolOutcome outcome)
        {
            if (dispatch == null || outcome == null
                || !string.Equals(outcome.OperationId, dispatch.OperationId, StringComparison.Ordinal)
                || OutcomeFromDispatch(dispatch)?.IsTerminal == true) return false;
            if (string.Equals(outcome.Code, "operation_not_found", StringComparison.OrdinalIgnoreCase))
            {
                dispatch.Status = dispatch.State = "rejected";
                dispatch.Code = "operation_not_found_no_execution";
                dispatch.Retryable = false;
                dispatch.RetryableKnown = true;
                dispatch.Receipt = null;
                dispatch.Message = "后端权威确认没有此 operation_id，动作未执行";
            }
            else
            {
                dispatch.Status = dispatch.State = string.IsNullOrWhiteSpace(outcome.Status)
                    ? "unknown" : outcome.Status;
                dispatch.Code = outcome.Code;
                dispatch.Retryable = outcome.Retryable;
                dispatch.RetryableKnown = true;
                dispatch.Receipt = outcome.Receipt;
                dispatch.Message = outcome.Message;
            }
            // The ACK flags belong to the previously persisted receipt observation.
            // A merged query result must be durably saved before its own ACK can start.
            dispatch.ReceiptAcknowledged = false;
            dispatch.ReceiptAcknowledgementQueued = false;
            dispatch.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
            return true;
        }

        private static bool AcknowledgeDurableDispatch(Conversation conv, NpcSnapshot snap, PendingToolDispatch dispatch)
        {
            if (conv == null || snap == null) return false;
            return AcknowledgeDurableDispatch(conv, snap.TaiwuId, snap.NpcId, dispatch);
        }

        private static bool AcknowledgeDurableDispatch(Conversation conv, int taiwuId, int npcId,
            PendingToolDispatch dispatch)
        {
            if (dispatch == null || string.IsNullOrWhiteSpace(dispatch.Receipt)) return true;
            if (dispatch.ReceiptAcknowledged) return true;
            if (conv == null || conv.WorldId == 0 || taiwuId <= 0 || npcId < 0
                || conv.TaiwuId != taiwuId || conv.NpcId != npcId
                || !OperationRpcClient.IsValidOperationId(dispatch.OperationId)) return false;
            bool durable = EffectHandler.AcknowledgeOperation(conv.WorldId, conv.TaiwuId,
                dispatch.OperationId, acknowledged =>
            {
                if (!acknowledged || !ConversationStillCurrent(conv)) return; // 回执仍留在后端，下次加载继续 ACK。
                dispatch.ReceiptAcknowledged = true;
                dispatch.ReceiptAcknowledgementQueued = true;
                dispatch.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
                if (!SaveConv(taiwuId, npcId, conv)) dispatch.ReceiptAcknowledged = false;
            });
            if (!durable) return false;
            dispatch.ReceiptAcknowledgementQueued = true;
            dispatch.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
            SaveConv(taiwuId, npcId, conv);
            return true;
        }

        private static bool DispatchAcknowledgementDurable(PendingToolDispatch dispatch)
            => dispatch == null || string.IsNullOrWhiteSpace(dispatch.Receipt)
                || dispatch.ReceiptAcknowledged;

        private static bool EnsureTurnAcknowledgementsDurable(Conversation conv, int taiwuId, int npcId,
            PendingActionTurn turn)
        {
            if (turn?.Operations == null) return true;
            foreach (PendingToolDispatch dispatch in turn.Operations)
            {
                if (dispatch == null || OutcomeFromDispatch(dispatch)?.IsTerminal != true) continue;
                if (!AcknowledgeDurableDispatch(conv, taiwuId, npcId, dispatch)) return false;
            }
            return true;
        }

        /// <summary>
        /// 重启/回包丢失恢复只查询既有 operationId，绝不重发原 mutation。查询到终态后先把
        /// ToolOutcome 可靠写回会话，再 ACK 后端回执；pending/unknown/not_found 继续隔离。
        /// </summary>
        private IEnumerator ReconcileDurableToolDispatches(Conversation conv, NpcSnapshot snap,
            System.Threading.CancellationToken ct, Action<bool> onDone)
        {
            if (conv == null || !conv.LoadReliable) { onDone?.Invoke(false); yield break; }
            if (conv.PendingActionTurns == null || snap == null) { onDone?.Invoke(true); yield break; }
            var unresolved = new List<PendingToolDispatch>();
            var recovered = new Dictionary<PendingToolDispatch, ToolOutcome>();
            foreach (var turn in conv.PendingActionTurns)
            {
                if (turn?.Operations == null) continue;
                foreach (var dispatch in turn.Operations)
                {
                    if (dispatch == null) continue;
                    bool terminal = OutcomeFromDispatch(dispatch)?.IsTerminal == true;
                    if (terminal)
                    {
                        AcknowledgeDurableDispatch(conv, snap, dispatch);
                        continue;
                    }
                    if (!OperationRpcClient.IsValidOperationId(dispatch.OperationId)) continue; // 旧存档无 id，只保留 fail-closed 日志。
                    unresolved.Add(dispatch);
                }
            }
            if (unresolved.Count == 0) { onDone?.Invoke(true); yield break; }

            int pending = unresolved.Count;
            foreach (var dispatch in unresolved)
                EffectHandler.QueryOperation(conv.WorldId, conv.TaiwuId, dispatch.OperationId, outcome =>
                {
                    recovered[dispatch] = outcome;
                    pending--;
                });

            float deadline = Time.unscaledTime + RpcReceiptWaitSeconds;
            while (pending > 0 && Time.unscaledTime < deadline && TurnToolDispatchAllowed(ct)) yield return null;
            if (!TurnToolDispatchAllowed(ct) || !ConversationStillCurrent(conv)) { onDone?.Invoke(false); yield break; }

            bool changed = false;
            foreach (var pair in recovered)
            {
                var dispatch = pair.Key;
                var outcome = pair.Value;
                if (dispatch == null || outcome == null) continue;
                if (string.IsNullOrWhiteSpace(outcome.OperationId)
                    || !string.Equals(outcome.OperationId, dispatch.OperationId, StringComparison.Ordinal))
                    outcome = ToolOutcome.Unknown(dispatch.OperationId, "查询回执 operation_id 不匹配，继续隔离");
                // Backend mutations are prewritten before dispatch, so authoritative
                // not-found closes this operation as no-execution. The merge helper also
                // protects a terminal live callback that raced this recovery query.
                changed |= MergeRecoveredOutcome(dispatch, outcome);
            }
            if (changed && !SaveConv(snap.TaiwuId, snap.NpcId, conv)) { onDone?.Invoke(false); yield break; }
            if (changed)
                foreach (var pair in recovered)
                    if (OutcomeFromDispatch(pair.Key)?.IsTerminal == true)
                        AcknowledgeDurableDispatch(conv, snap, pair.Key);
            onDone?.Invoke(true);
        }

        private static string CanonicalJson(string raw)
        {
            try { return CanonicalizeToken(string.IsNullOrWhiteSpace(raw) ? new JObject() : JToken.Parse(raw)).ToString(Formatting.None); }
            catch { return (raw ?? "").Trim(); }
        }

        private static JToken CanonicalizeToken(JToken token)
        {
            if (token is JObject obj)
            {
                var props = new List<JProperty>(obj.Properties());
                props.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                var sorted = new JObject();
                foreach (var prop in props) sorted[prop.Name] = CanonicalizeToken(prop.Value);
                return sorted;
            }
            if (token is JArray arr)
            {
                var copy = new JArray();
                foreach (var item in arr) copy.Add(CanonicalizeToken(item));
                return copy;
            }
            return token?.DeepClone() ?? JValue.CreateNull();
        }

        const int KeepRecentTokens = 3200;  // 逐字窗口有界，避免同一 NPC 越聊首轮输入越大；更早内容由后台梗概+长期记忆承接
        const int MinKeepTurns = 12;        // 至少保留最近 6 个完整来回，短对话不因 token 窗口而过早压缩
        static int EstTokens(string s)
        {
            // JHYL_CJK_TOKEN_ESTIMATE:中文不能按 chars/4 估,否则会压缩过晚。
            if (string.IsNullOrEmpty(s)) return 0;
            int tokens = 0, asciiRun = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch <= 0x7f)
                {
                    if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch))
                    {
                        if (asciiRun > 0) { tokens += (asciiRun + 3) / 4; asciiRun = 0; }
                        if (!char.IsWhiteSpace(ch)) tokens++;
                    }
                    else asciiRun++;
                }
                else
                {
                    if (asciiRun > 0) { tokens += (asciiRun + 3) / 4; asciiRun = 0; }
                    tokens++;
                }
            }
            if (asciiRun > 0) tokens += (asciiRun + 3) / 4;
            return tokens;
        }
        /// <summary>从最新往回累计 token,保留 ≤ keepTokens(但至少 minKeep 轮)的近期逐字;返回需压缩的最旧若干轮数(cut)。0=无需压缩。</summary>
        static int ComputeCompactCut(IList<TalkTurn> turns, int keepTokens, int minKeep)
        {
            if (turns == null) return 0;
            int n = turns.Count, acc = 0;
            for (int i = n - 1; i >= 0; i--)
            {
                int t = EstTokens(turns[i] != null ? turns[i].Text : null);
                if (acc + t > keepTokens && (n - 1 - i) >= minKeep)
                {
                    int cut = i + 1;
                    // 一次玩家/NPC 来回必须原子进入冷归档，不能只压掉玩家问题却把对应回答
                    // 留在 live history。新版优先按 ExchangeId 对齐；旧记录缺 id 时按角色相邻关系兜底。
                    while (cut > 0 && cut < n)
                    {
                        TalkTurn left = turns[cut - 1], right = turns[cut];
                        bool sameExchange = left != null && right != null
                            && !string.IsNullOrWhiteSpace(left.ExchangeId)
                            && string.Equals(left.ExchangeId, right.ExchangeId, StringComparison.Ordinal);
                        bool legacySplit = left != null && right != null
                            && string.IsNullOrWhiteSpace(left.ExchangeId) && string.IsNullOrWhiteSpace(right.ExchangeId)
                            && left.FromPlayer && !right.FromPlayer;
                        if (!sameExchange && !legacySplit) break;
                        cut--;
                    }
                    return cut;
                }
                acc += t;
            }
            return 0;
        }

        /// <summary>难度(简单/均衡/困难),由配置或对话框设置;影响 NPC 被说动的难易(注入提示词)。</summary>
        public static string Difficulty = "均衡";
        public static int ReplyLength = 1;   // 回复篇幅:0简短/1适中/2详细/3不限
        /// <summary>太吾口吻(自由文本):主角说话的语气风格,只影响『代笔』替太吾拟话的口吻;空=默认。</summary>
        public static string TaiwuVoice = "";

        // OperationRpcClient 会在首包丢失后用同一 operation_id 查询回执并至多补发一次；
        // 最坏有界恢复约 12 秒。外层协程必须等到恢复窗口结束，不能沿用旧的 4 秒等待而
        // 把稍后到达的权威终态误报成超时。正常回包仍会立即结束，不增加常态延迟。
        private const float RpcReceiptWaitSeconds = 16f;

        // 本轮取消源:中断按钮经 ChatWindow 调 Cancel() 取消正在进行的 LLM 调用。
        private System.Threading.CancellationTokenSource _cts;
        private int _turnWorldGeneration;
        private int _turnWorldDate;
        public void Cancel() { try { _cts?.Cancel(); } catch { } }

        /// <summary>
        /// 仅供拥有本实例协程的 UI 在 StopCoroutine 前调用。协程已不会再读取 Dispatched，
        /// 因而必须主动释放本轮 observer；持久 pending journal 仍负责下次恢复查询。
        /// 群聊 worker 只做协作取消、不会调用本方法，以便 finally 正确判断是否已派发。
        /// </summary>
        public void AbandonCurrentTurnOperationObservers()
        {
            foreach (string operationId in new List<string>(_operationIdsThisTurn))
                EffectHandler.ForgetOperationOutcomeObserver(operationId);
            _operationIdsThisTurn.Clear();
        }

        /// <summary>
        /// Captures in-flight execution state when cancellation exits before normal LastTurn*
        /// projection. The group parent calls this from the worker finally, after ProcessTurn
        /// has stopped mutating the collections.
        /// </summary>
        internal void CaptureCurrentTurnState(out List<string> memoryIds, out List<string> actions,
            out List<string> toolResults)
        {
            memoryIds = NormalizeExecutionHistory(LastTurnMemoryIds ?? _memIdsThisTurn);
            actions = NormalizeExecutionHistory(LastTurnActions ?? _landedThisTurn);
            toolResults = NormalizeExecutionHistory(LastTurnToolResults ?? _toolResultsThisTurn);
        }

        // 本轮记忆库引用(供工具写记忆/读记忆),以及本轮累计的即时好感变动(供收尾播动画)
        private NpcMemoryStore _mem;
        private MemoryRecaller.RecallResult _recallResult;
        // 本轮经 remember 工具沉淀的记忆条目 id(实例每轮 new,无需清空):收尾时写进回话 turn.MemoryIds,供按轮删除/重试一并抹除
        private readonly List<string> _memIdsThisTurn = new List<string>();
        private readonly List<string> _operationIdsThisTurn = new List<string>();
        // 本轮已执行过的工具调用键(name|args):同名同参第二次短路,防重复动作 / 省冗余往返
        private readonly HashSet<string> _toolCallsSeen = new HashSet<string>();
        private readonly HashSet<string> _loadedActionSkills = new HashSet<string>(StringComparer.Ordinal);
        private ConversationToolRoute _toolRoute;
        private ToolContext _turnToolContext;
        private bool _npcInitiatedTurn;
        private PendingActionTurn _activePendingActionTurn;
        private string _currentPlayerInput;
        private int _favorDeltaThisTurn;
        private enum ReactionCommitState
        {
            NotAttempted,
            Attempting,
            Failed,
            Unknown,
            Succeeded,
        }

        private ReactionCommitState _reactionState;
        // 太吾自身随身物 + 武学技艺:仅"太吾送 NPC 物品/传功传艺"用到时才惰性拉取,缓存本轮,免每轮空跑拖慢
        private TaiwuHoldings _taiwuHoldings;

        // 「获得」提示延后:工具执行期间先攒着(_deferList 非空),待最终回话揭示完再统一显示在正文下方;
        // null = 直接显示(如调试暗号,无回话可等)。
        // 这两个字段 + Emit/Flush 是【实例】级(非 static):群聊并行时每个成员各持一个 orch 实例、各记各的落地/提示,
        // 互不串台(Emit 经异步 RPC 回调闭包捕获的也是各自的 this)。单聊单实例,语义与原静态完全一致。
        private sealed class NoticeItem { public string What; public string Label; public Action OnView; }
        private List<NoticeItem> _deferList;
        private List<string> _landedThisTurn;   // 本轮真落地的动作描述(供收尾写进 NPC 记忆,让它记得自己做过什么)
        private List<string> _toolResultsThisTurn; // 本轮已向玩家显示的绿色执行结果；含失败，不能混作已完成动作
        private List<string> _taiwuSecretsDisclosedThisTurn; // 成功告诉太吾的秘闻正文；收口时防止模型只打哑谜
        private List<string> _thirdPartySecretsDisclosedThisTurn; // 成功告诉第三方的秘闻正文；后续模型上下文与玩家正文都不得泄露给太吾
        private enum LocalToolSemantic { NotApplicable, Succeeded, Failed, Unconfirmed }
        // 非后端 operation-ledger 工具（目前是 remember/start_combat）的结构化终态。
        // 后端副作用始终以 ToolOutcome 为准；检索工具不使用本字段。
        private LocalToolSemantic _lastLocalToolSemantic;
        // 多页签:把「绿色落地提示」与「系统提示(流式降级等)」路由回发起本 turn 的那个页签;为空则落当前活动页。
        // 由 ChatWindow.RunTurn / GroupChatOrchestrator 建 orch 时各自挂上宿主页签的实例方法 → 后台页签生成时绿提示不再错落到当前看的那页。
        public Action<string, string, Action> GrantSink;
        public Action<string> SysSink;

        public sealed class PendingCombatRequest
        {
            public string OperationId;
            public uint WorldId;
            public int TaiwuId;
            public int NpcId;
            public string NpcName;
            public short CombatConfig;
            public string Mode;
            public string Initiator;
            public string Reason;
            public int WorldGeneration;
        }
        private PendingCombatRequest _pendingCombat;
        public PendingCombatRequest TakePendingCombat()
        {
            var request = _pendingCombat;
            _pendingCombat = null;
            return request;
        }
        public sealed class PendingGroomingRequest
        {
            public uint WorldId;
            public int TaiwuId;
            public int NpcId;
            public string NpcName;
            public int WorldGeneration;
        }
        private PendingGroomingRequest _pendingGrooming;
        public PendingGroomingRequest TakePendingGrooming()
        {
            var request = _pendingGrooming;
            _pendingGrooming = null;
            return request;
        }
        // actionSucceeded is deliberately explicit at every call site. Visible wording is
        // presentation, not an authority boundary: a natural failure such as “货架暂无现货”
        // must still be shown, but must never become Actions or long-term “我做过” memory
        // merely because it lacks a particular Chinese substring. Informational/debug
        // notices pass false because they are not completed actions.
        private void Emit(string what, string label, Action onView, bool actionSucceeded)
        {
            if (string.IsNullOrEmpty(what)) return;
            if (_toolResultsThisTurn != null) _toolResultsThisTurn.Add(what);
            // 只接收调用方从权威回执/明确前置结果传来的成功语义。绝不解析显示文案猜成败。
            if (_landedThisTurn != null && actionSucceeded)
            {
                _landedThisTurn.Add(what);
                // The parent group shares one authoritative relation revision. Any landed
                // mutation may change favor, alertness or a formal relation, so invalidate
                // the matrix once here instead of re-querying it before every unchanged
                // agent round.
                GroupCtx?.MarkRelationshipsDirty?.Invoke();
                // 群聊副作用先于最终 transcript 发生。动作一旦权威落地，立刻把提交屏障
                // 写进父群聊 journal；失败则在收尾前 fail-closed，绝不创建孤立动作记忆。
                PrepareGroupCommitBarrier();
            }
            if (_deferList != null) _deferList.Add(new NoticeItem { What = what, Label = label, OnView = onView });
            else (GrantSink ?? ChatWindow.AddGrantNotice)(what, label, onView);
        }

        private void RegisterTaiwuSecretDisclosure(string rawSecret)
        {
            string secret = SecretDisclosureProjection.NormalizeSecret(rawSecret);
            if (secret.Length == 0) return;
            if (_taiwuSecretsDisclosedThisTurn == null)
                _taiwuSecretsDisclosedThisTurn = new List<string>();
            if (!_taiwuSecretsDisclosedThisTurn.Contains(secret))
                _taiwuSecretsDisclosedThisTurn.Add(secret);
        }

        private void RegisterThirdPartySecretDisclosure(string rawSecret)
        {
            string secret = SecretDisclosureProjection.NormalizeSecret(rawSecret);
            if (secret.Length == 0) return;
            if (_thirdPartySecretsDisclosedThisTurn == null)
                _thirdPartySecretsDisclosedThisTurn = new List<string>();
            if (!_thirdPartySecretsDisclosedThisTurn.Contains(secret))
                _thirdPartySecretsDisclosedThisTurn.Add(secret);
        }

        private bool HasThirdPartyOnlySecretDisclosure()
        {
            return SecretDisclosureProjection.HasThirdPartyOnlySecrets(
                _thirdPartySecretsDisclosedThisTurn, _taiwuSecretsDisclosedThisTurn);
        }

        private static string BuildThirdPartySecretPrivacyReply(NpcSnapshot snap)
        {
            string name = string.IsNullOrWhiteSpace(snap?.Name) ? "对方" : snap.Name.Trim();
            return name + "压低声音，只把那桩隐秘说给了指定之人。转回身时，"
                + name + "没有向你复述其中内容。";
        }

        private bool RestoreSuccessfulSecretDisclosureFromFrozenArguments(string argsJson, NpcSnapshot snap)
        {
            if (snap == null) return false;
            try
            {
                JObject args = JObject.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
                if (!RecipientSecretDispatchEnvelope.TryRead(args, snap.NpcId, 0,
                    out RecipientSecretDispatch dispatch))
                    return false;
                bool toTaiwu = dispatch.RecipientId == snap.TaiwuId;
                string safeAction;
                if (toTaiwu)
                {
                    RegisterTaiwuSecretDisclosure(dispatch.SecretText);
                    safeAction = "向太吾吐露一桩秘闻";
                }
                else
                {
                    RegisterThirdPartySecretDisclosure(dispatch.SecretText);
                    safeAction = "向指定人物吐露一桩秘闻";
                }
                if (_landedThisTurn != null && !_landedThisTurn.Contains(safeAction))
                    _landedThisTurn.Add(safeAction);
                if (_toolResultsThisTurn != null && !_toolResultsThisTurn.Contains(safeAction))
                    _toolResultsThisTurn.Add(safeAction);
                return true;
            }
            catch { return false; }
        }

        private void RegisterRecoveredSecretDisclosures(PendingActionTurn turn, NpcSnapshot snap)
        {
            if (turn?.Operations == null || snap == null) return;
            foreach (PendingToolDispatch dispatch in turn.Operations)
                if (dispatch != null
                    && string.Equals(dispatch.Tool, "tell_secret", StringComparison.Ordinal)
                    && string.Equals(DispatchStatus(dispatch), "succeeded", StringComparison.Ordinal))
                    RestoreSuccessfulSecretDisclosureFromFrozenArguments(dispatch.ArgumentsJson, snap);
        }

        private static int TellSecretDisplayIndex(JObject args)
        {
            JToken token = args?["index"];
            if (token == null) return 1;
            try { return token.Value<int>(); }
            catch { return int.TryParse(token.ToString(), out int value) ? value : 0; }
        }

        private static SecretRef FindShareableSecret(NpcSnapshot snap, int displayIndex)
        {
            if (snap?.ShareableSecrets == null || displayIndex <= 0) return null;
            foreach (SecretRef secret in snap.ShareableSecrets)
                if (secret != null && secret.DisplayIndex == displayIndex) return secret;
            // Compatibility with snapshots created before DisplayIndex was persisted.
            if (displayIndex <= snap.ShareableSecrets.Count)
            {
                SecretRef positional = snap.ShareableSecrets[displayIndex - 1];
                if (positional != null && positional.DisplayIndex <= 0) return positional;
            }
            return null;
        }

        private bool TryReuseFrozenTellSecretArguments(int actorId, int recipientId, int displayIndex,
            out string frozenArgumentsJson)
        {
            frozenArgumentsJson = null;
            if (_activePendingActionTurn?.Operations == null) return false;
            foreach (PendingToolDispatch pending in _activePendingActionTurn.Operations)
            {
                if (pending == null
                    || !string.Equals(pending.Tool, "tell_secret", StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(pending.ArgumentsJson)) continue;
                try
                {
                    JObject persisted = JObject.Parse(pending.ArgumentsJson);
                    if (TellSecretDisplayIndex(persisted) != displayIndex) continue;
                    if (!RecipientSecretDispatchEnvelope.TryRead(persisted, actorId, recipientId, out _))
                        continue;
                    frozenArgumentsJson = pending.ArgumentsJson;
                    return true;
                }
                catch { }
            }
            return false;
        }

        private bool TryFreezeTellSecretDispatchArguments(string argsJson, NpcSnapshot snap,
            int recipientId, out string frozenArgumentsJson, out string error)
        {
            frozenArgumentsJson = null;
            error = null;
            if (snap == null || recipientId <= 0 || recipientId == snap.NpcId)
            {
                error = "秘闻接收者无效";
                return false;
            }
            JObject args;
            try { args = string.IsNullOrWhiteSpace(argsJson) ? new JObject() : JObject.Parse(argsJson); }
            catch
            {
                error = "工具参数不是有效 JSON";
                return false;
            }
            int displayIndex = TellSecretDisplayIndex(args);
            if (TryReuseFrozenTellSecretArguments(snap.NpcId, recipientId, displayIndex,
                out frozenArgumentsJson)) return true;
            SecretRef secret = FindShareableSecret(snap, displayIndex);
            if (secret == null)
            {
                error = "序号 " + displayIndex + " 不属于当前人物的可吐露秘闻清单";
                return false;
            }
            string secretText = SecretDisclosureProjection.NormalizeSecret(secret.Text);
            if (secretText.Length == 0)
            {
                error = "这桩秘闻的正文无法可靠读取";
                return false;
            }
            args["_actorId"] = snap.NpcId;
            args["_targetId"] = recipientId;
            args["secret_id"] = (int)secret.Id;
            args["_secret_text"] = secretText;
            if (!RecipientSecretDispatchEnvelope.TryRead(args, snap.NpcId, recipientId, out _))
            {
                error = "无法建立不可变的秘闻派发信封";
                return false;
            }
            frozenArgumentsJson = CanonicalJson(args.ToString(Formatting.None));
            return true;
        }

        private void FlushNotices()
        {
            var list = _deferList; _deferList = null;
            if (list == null) return;
            var sink = GrantSink ?? (Action<string, string, Action>)ChatWindow.AddGrantNotice;
            foreach (var n in list) sink(n.What, n.Label, n.OnView);
        }

        // 群聊用:成员回话气泡显示后,再把其本轮落地动作的绿提示显示在其下方(与单聊一致的绿色「获得/落地」提示)。
        public void FlushDeferredNoticesNow() => FlushNotices();
        // 群聊用:沉默成员(无回话气泡)→ 丢弃其挂起提示,免错位或被下一位覆盖。
        public void ClearDeferredNotices() { _deferList = null; }

        // 大工具结果截断:头部+尾部保留(尾部常含错误/清单结尾/汇总),中间略去,免单条 tool 结果撑爆上下文
        static string TruncateToolResult(string s, int maxChars = 4000)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= maxChars) return s;
            int head = (int)(maxChars * 0.7), tail = maxChars - head - 24;
            if (tail < 0) tail = 0;
            return s.Substring(0, head) + "\n…(中间略去 " + (s.Length - head - tail) + " 字)…\n" + s.Substring(s.Length - tail);
        }

        /// <summary>流式开关:开=边收边显(思考过程 + 工具调用 + 正文逐字);关=当前非流式无思考方案。默认开,可在设置页关。
        /// 用户在设置里重新打开时,顺带清掉"连续失败自动停用"状态,给流式再次机会。</summary>
        private static bool _streamingEnabled = true;
        public static bool StreamingEnabled
        {
            get => _streamingEnabled;
            set { _streamingEnabled = value; if (value) { StreamAutoDisabled = false; _streamFailStreak = 0; _streamNoticeShown = false; } }
        }
        // 熔断:某模型/端点连续 StreamFailLimit 轮流式失败 → 本会话自动改走非流式(不显示思考过程),免每轮白等一次流式超时。
        // 仅会话级,不动用户存档设置;用户重开流式或下次启动游戏会复位。
        public static bool StreamAutoDisabled;
        private static int _streamFailStreak;
        private static bool _streamNoticeShown;
        private const int StreamFailLimit = 2;

        // 实例方法(非 static):熔断计数仍用 static 会话级状态,但一次性的降级提示经本 orch 的 SysSink 落到发起页签(多页签下不错落)。
        private void NoteStreamTrouble()
        {
            if (++_streamFailStreak < StreamFailLimit || StreamAutoDisabled) return;
            StreamAutoDisabled = true;
            Debug.Log("[江湖有灵] 流式连续失败,本会话自动切为非流式(不显示思考过程)");
            if (!_streamNoticeShown)
            {
                _streamNoticeShown = true;
                try { (SysSink ?? ChatWindow.AddSysNotice)("(流式/思考显示连续异常,已自动切为非流式;如需重开可在设置里打开「流式」)"); } catch { }
            }
        }

        private bool TurnToolDispatchAllowed(System.Threading.CancellationToken ct)
            => !ct.IsCancellationRequested && WorldLifecycle.IsSameWorld(_turnWorldGeneration)
                && (GroupCtx == null || GroupCtx.DispatchStillValid == null || GroupCtx.DispatchStillValid());

        public bool Remote;   // 千里传音(远程对话):本轮去掉需当面/物理接触的工具,并提示 NPC 远隔相谈
        // 非空=太吾已通过聊天窗按钮在本轮模型调用前完成了一批真实行动；
        // 模型只负责接收权威回执并回应，不能把同项动作再执行一次。
        internal TaiwuDirectActionReceipt DirectTaiwuAction;

        // 非空=本轮是"群聊"里这位成员的发言:复用整套单聊流程(全套工具/记忆/落地照常),仅额外注入一段群聊框架提示。默认 null→单聊行为不变。
        public GroupContext GroupCtx;
        public sealed class GroupContext
        {
            public string SelfName;            // 本成员名
            public List<string> Others;        // 同频道其他人(不含太吾、不含自己)，不蕴含同地身份
            public int MemberId;
            public int DispatchOrder;
            public GroupActionCoordinator ActionCoordinator;
            // 仅表示本次群 roster（不含太吾）；不能据此推断同地，mutation 前仍须查权威“同块 ∪ 同道”。
            public HashSet<int> ParticipantIds;
            public string GroupId;
            public string ExchangeId;
            public string AttemptId;
            public List<string> CompanionNames;
            public List<string> OrdinaryChannelNames;
            public string RelationshipContext;
            public Func<long> RelationshipRevision;
            public Action MarkRelationshipsDirty;
            public long SeenRelationshipRevision;
            public string IdentityContext;
            // 群页可常驻数月；每名子 Agent 真正开始本轮前，用刚读取的实时快照重建
            // 太吾/当前发言者账本，不能沿用开窗时缓存的太吾性别等身份字段。
            public Func<NpcSnapshot, string> RefreshIdentityContext;
            public bool MustReply;
            // ProcessTurn 的 playerInput 在群聊中是给模型看的完整 transcript；行动授权、
            // 本轮意图和纯闲聊判断只能读取当前这一轮太吾/插话的原文，不能被历史行动词污染。
            public string CurrentTurnInput;
            // One player message may drive several autonomous group rounds. The mandatory
            // memory search still runs logically for every member reply, but the expensive
            // semantic selection is identical until Taiwu interjects with a new topic. The
            // parent owns this per-exchange cache and clears it on every new Taiwu message.
            public string MemoryRecallTopic;
            public Func<int, MemoryRecaller.RecallResult> GetCachedMemoryRecall;
            public Action<int, MemoryRecaller.RecallResult> StoreCachedMemoryRecall;
            // Returns null when admitted, otherwise a diagnostic.  The parent group owns one
            // shared instance so all members and automatic rounds consume the same emergency fuse.
            public Func<int, int, string> ReserveModelRequest;
            public Func<bool> DispatchStillValid;
            public Func<int, List<string>, List<string>, List<string>, bool> PrepareCommit;
            public string BuildNote()
            {
                string othersStr = (Others != null && Others.Count > 0) ? string.Join("、", Others.ToArray()) : "";
                var sb = new System.Text.StringBuilder("【群聊】此刻你");
                sb.Append(string.IsNullOrWhiteSpace(SelfName) ? "" : ("(" + SelfName + ")"));
                sb.Append("与太吾");
                if (othersStr.Length > 0) sb.Append("、").Append(othersStr);
                sb.Append(" 正在同一条共享群聊频道中交谈。**收到同一频道消息只代表能听见彼此发言，绝不自动证明大家同处一地。当前同道视作正与太吾同行；其他群成员只有代码以游戏权威数据确认同块时，才算能当面接触。** ");
                sb.Append("**当前同道（按规则视作与太吾同地）：")
                    .Append(CompanionNames != null && CompanionNames.Count > 0 ? string.Join("、", CompanionNames.ToArray()) : "无")
                    .Append("。其他普通频道成员：")
                    .Append(OrdinaryChannelNames != null && OrdinaryChannelNames.Count > 0 ? string.Join("、", OrdinaryChannelNames.ToArray()) : "无")
                    .Append("；普通频道成员并不因此获得同地身份。** ");
                sb.Append("你记忆里在别处、或往日认识的人若不在本频道，除非太吾自己提起，否则别向他们递话，也别伪装成他们正在参与。");
                sb.Append("上面那段是频道里各参与者方才所言——**每句开头都标了说话人(『太吾:』或『「某人」:』),你能分清是谁说的**。**太吾的话通常发给频道众人，不一定单独问你(除非明确点名);顺势接话即可，别把话都抢着包圆。** ");
                sb.Append("**你接话时未必是在回太吾，也可能是在接另一位参与者的话；若冲着某位参与者说，就先点出名字，让大家知道你在回谁。** ");
                sb.Append("据此自然接话:可应和、可反驳、可打趣、可追问、可把话头递给某人(直接喊名字也行),也可只冲其中一人说;长短随你性子,三两字附和也成,别硬凑长篇。");
                if (MustReply)
                    sb.Append("**本轮消息已经明确路由给你，你必须用自己的角色口吻给出一段可见回应；不可回『旁听』、纯省略号或沉默旁白。可以简短，但必须真正接住太吾或群中刚才的话。** ");
                else
                    sb.Append("**这是没有新玩家发言的续聊轮；只有被戳中心事或确有话说时才开口，无甚可说可只回『旁听』。** ");
                sb.Append("**你可以请求工具真办事，但群成员身份本身不授予当面行动资格。赠物、交换、传授、疗伤、动手、建立需接触的关系等物理动作，必须等代码确认相关人物是当前同块或当前同道；工具拒绝就据实改口，绝不可假称完成。** ");
                sb.Append("**工具对象可写太吾或频道参与者(").Append(othersStr.Length > 0 ? othersStr : "其他成员").Append(")，但必须明确填写真实姓名；邀请进频道不代表此人可被当面施为。**");
                sb.Append("\n**本轮已从游戏权威读取频道内所有人物两两关系（含太吾）：**\n")
                    .Append(string.IsNullOrWhiteSpace(RelationshipContext) ? "关系读取失败；本轮不得猜测任何人物关系。" : RelationshipContext.Trim())
                    .Append("\n上述关系只在本轮有效；关系行动后下一轮会重新读取。不得用旧记忆覆盖这份权威矩阵。 ");
                sb.Append("\n**本轮身份与性别账本（权威）：**\n")
                    .Append(string.IsNullOrWhiteSpace(IdentityContext)
                        ? "身份账本读取失败；不得猜测或互换任何人的性别、代词和行动者。"
                        : IdentityContext.Trim())
                    .Append("\n第一人称只指当前发言 NPC；『太吾/玩家』只指太吾；第三方姓名、代词及工具参数必须与账本逐项对应。 ");
                sb.Append("别复述别人原话、别旁白动作、别替别人说话。");
                if (!MustReply)
                    sb.Append("**要么堂堂正正说一句人话，要么只回『旁听』二字；切莫用省略号或『默默听着』『不语』『静静旁观』等旁白充数。**");
                return sb.ToString();
            }
        }

        private bool PrepareGroupCommitBarrier()
        {
            if (GroupCtx == null) return true;
            if (_cts == null || _cts.IsCancellationRequested
                || !WorldLifecycle.IsSameWorld(_turnWorldGeneration)
                || (GroupCtx.DispatchStillValid != null && !GroupCtx.DispatchStillValid())) return false;
            if (GroupCtx.PrepareCommit == null || GroupCtx.MemberId <= 0
                || string.IsNullOrWhiteSpace(GroupCtx.GroupId)
                || string.IsNullOrWhiteSpace(GroupCtx.ExchangeId)
                || string.IsNullOrWhiteSpace(GroupCtx.AttemptId)) return false;
            var actions = new List<string>();
            if (_landedThisTurn != null)
                foreach (string action in _landedThisTurn)
                    if (!string.IsNullOrWhiteSpace(action) && !actions.Contains(action)) actions.Add(action);
            try
            {
                return GroupCtx.PrepareCommit(GroupCtx.MemberId,
                    new List<string>(_memIdsThisTurn), actions, new List<string>(_operationIdsThisTurn));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[江湖有灵] 群聊提交屏障失败 npc=" + GroupCtx.MemberId + ":" + ex.GetType().Name);
                return false;
            }
        }

        private IEnumerator RecalculateRemoteFromAuthoritativePresence(NpcSnapshot snap,
            System.Threading.CancellationToken cancellationToken)
        {
            if (snap == null) { Remote = true; yield break; }
            bool resolved = false;
            bool remote = true;
            yield return ConversationContactModeResolver.Resolve(snap.TaiwuId, snap.NpcId,
                _turnWorldGeneration, cancellationToken,
                value => { remote = value; resolved = true; }, snap.IsCaptive,
                EwReflect.HasTargetCharacterContext(snap.NpcId));
            // Any inability to obtain current authoritative presence fails closed to the
            // remote tool surface.
            Remote = !resolved || remote;
        }

        private IEnumerator RefreshAuthoritativeConversationLocations(NpcSnapshot snap,
            System.Threading.CancellationToken cancellationToken)
        {
            if (snap == null || snap.IsDead) yield break;
            bool npcDone = false, taiwuDone = false;
            bool npcOk = false, taiwuOk = false;
            short npcArea = -1, npcBlock = -1, taiwuArea = -1, taiwuBlock = -1;
            try
            {
                EffectHandler.QueryCharLocation(snap.NpcId, (area, block, ok) =>
                {
                    npcArea = area; npcBlock = block; npcOk = ok; npcDone = true;
                });
                EffectHandler.QueryCharLocation(snap.TaiwuId, (area, block, ok) =>
                {
                    taiwuArea = area; taiwuBlock = block; taiwuOk = ok; taiwuDone = true;
                });
            }
            catch
            {
                yield break;
            }

            float deadline = Time.unscaledTime + 3f;
            while ((!npcDone || !taiwuDone) && Time.unscaledTime < deadline
                && !cancellationToken.IsCancellationRequested
                && WorldLifecycle.IsSameWorld(_turnWorldGeneration)) yield return null;
            if (cancellationToken.IsCancellationRequested
                || !WorldLifecycle.IsSameWorld(_turnWorldGeneration)) yield break;

            string authoritativeTaiwu = taiwuDone && taiwuOk
                ? NpcSnapshotReader.ResolveLocationText(
                    new GameData.Domains.Map.Location(taiwuArea, taiwuBlock)) : null;
            string authoritativeNpc = npcDone && npcOk
                ? NpcSnapshotReader.ResolveLocationText(
                    new GameData.Domains.Map.Location(npcArea, npcBlock)) : null;
            if (!string.IsNullOrWhiteSpace(authoritativeTaiwu))
                snap.TaiwuLocationText = authoritativeTaiwu;
            // 当面意味着 NPC 此刻就在太吾的真实现场。同道、被擒者或事件目标的
            // Character.Location 可能仍保留原地，不能把那个旧地址写成说话地点。
            if (!Remote && !string.IsNullOrWhiteSpace(authoritativeTaiwu))
                snap.LocationText = authoritativeTaiwu;
            else if (!string.IsNullOrWhiteSpace(authoritativeNpc))
                snap.LocationText = authoritativeNpc;
        }

        private static string PrimaryLocationText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string text = value.Trim();
            int lineBreak = text.IndexOfAny(new[] { '\r', '\n' });
            return lineBreak < 0 ? text : text.Substring(0, lineBreak).Trim();
        }

        private bool TryReserveGroupModelRequest(OpenAiCompatibleClient client,
            IList<LlmMessage> messages, IList<ToolDef> tools, int requestedMaxTokens,
            out string userError)
        {
            userError = null;
            if (GroupCtx?.ReserveModelRequest == null) return true;
            int output = requestedMaxTokens > 0 ? requestedMaxTokens
                : client != null && client.DefaultMaxTokens > 0 ? client.DefaultMaxTokens : 32768;
            int input = PromptBudgeter.Estimate(messages, tools);
            string diagnostic = GroupCtx.ReserveModelRequest(input, output);
            if (string.IsNullOrWhiteSpace(diagnostic)) return true;
            Debug.LogWarning("[JHYL_GROUP_COST_CIRCUIT] npc=" + GroupCtx.MemberId + " " + diagnostic);
            userError = "群聊本轮共享请求预算已达到异常保护上限，当前成员停止继续请求";
            return false;
        }

        private IEnumerator RefreshGroupRelationshipsForAgentRound(NpcSnapshot snap,
            System.Threading.CancellationToken cancellationToken, int agentRound, Action<bool> onDone)
        {
            if (GroupCtx == null) { onDone?.Invoke(true); yield break; }
            if (snap == null || snap.TaiwuId <= 0 || GroupCtx.ParticipantIds == null)
            { onDone?.Invoke(false); yield break; }
            long currentRevision = GroupCtx.RelationshipRevision == null
                ? -1L : GroupCtx.RelationshipRevision();
            if (currentRevision >= 0L
                && GroupCtx.SeenRelationshipRevision == currentRevision
                && !string.IsNullOrWhiteSpace(GroupCtx.RelationshipContext))
            {
                Debug.Log("[JHYL_GROUP_RELATION_AGENT_ROUND] cache_hit npc=" + snap.NpcId
                    + " round=" + agentRound + " revision=" + currentRevision);
                onDone?.Invoke(true);
                yield break;
            }

            var ids = new List<int> { snap.TaiwuId };
            foreach (int id in GroupCtx.ParticipantIds)
                if (id > 0 && !ids.Contains(id)) ids.Add(id);
            bool callbackAccepted = true;
            bool completed = false, succeeded = false;
            string context = null;
            try
            {
                EffectHandler.QueryRosterRelations(ids, true, (ok, value) =>
                {
                    if (!callbackAccepted) return;
                    succeeded = ok;
                    context = value;
                    completed = true;
                });
            }
            catch
            {
                callbackAccepted = false;
                onDone?.Invoke(false);
                yield break;
            }

            float deadline = Time.unscaledTime + 8f;
            while (!completed && Time.unscaledTime < deadline
                && TurnToolDispatchAllowed(cancellationToken)) yield return null;
            callbackAccepted = false;
            bool valid = completed && succeeded && TurnToolDispatchAllowed(cancellationToken);
            if (valid)
            {
                GroupCtx.RelationshipContext = string.IsNullOrWhiteSpace(context)
                    ? "群内各人之间均无显著关系" : context;
                GroupCtx.SeenRelationshipRevision = GroupCtx.RelationshipRevision == null
                    ? currentRevision : GroupCtx.RelationshipRevision();
                Debug.Log("[JHYL_GROUP_RELATION_AGENT_ROUND] npc=" + snap.NpcId
                    + " round=" + agentRound + " chars=" + ids.Count
                    + " revision=" + GroupCtx.SeenRelationshipRevision);
            }
            else
                Debug.LogWarning("[JHYL_GROUP_RELATION_AGENT_ROUND] failed npc=" + snap.NpcId
                    + " round=" + agentRound + " reason=" + (!completed ? "timeout_or_cancelled" : "backend_failed"));
            onDone?.Invoke(valid);
        }

        private static void InsertAfterStablePrefix(List<LlmMessage> messages,
            LlmMessage dynamicMessage)
        {
            if (messages == null || dynamicMessage == null) return;
            int boundary = -1;
            for (int i = 0; i < messages.Count; i++)
                if (messages[i] != null && messages[i].CacheBoundary) boundary = i;
            messages.Insert(Math.Min(messages.Count, Math.Max(0, boundary + 1)),
                dynamicMessage);
        }

        private bool TryLoadActionGuide(string skillId, out string guide)
            => _npcInitiatedTurn
                ? ConversationSkillCatalog.TryLoadForNpcInitiated(skillId, _turnToolContext, out guide)
                : ConversationSkillCatalog.TryLoad(skillId, _turnToolContext, out guide);

        private bool TryLoadActionGuideForAction(string toolName,
            out string skillId, out string guide)
            => _npcInitiatedTurn
                ? ConversationSkillCatalog.TryLoadForNpcInitiatedAction(toolName, _turnToolContext,
                    out skillId, out guide)
                : ConversationSkillCatalog.TryLoadForAction(toolName, _turnToolContext,
                    out skillId, out guide);

        public IEnumerator ProcessTurn(int npcId, string playerInput, Action<string> onReply, Action<string> onError,
            Action<string> onDelta = null, Action<string> onProgress = null, Action<int, int> onTokens = null,
            Action<string> onThinking = null, Action onResetReply = null, string retryAvoid = null,
            Action<bool> onContactMode = null, bool npcInitiated = false)
        {
            _cts = new System.Threading.CancellationTokenSource();
            LastCommittedExchangeId = null;
            LastTurnMemoryIds = null;
            LastTurnActions = null;
            LastTurnToolResults = null;
            LastNpcName = null;
            LastTurnDate = 0;
            LastPlayerLocationText = null;
            LastNpcLocationText = null;
            LastContactMode = null;
            string intentInput = DirectTaiwuAction != null
                ? "请对太吾刚才已经由代码完成的行动结果作出回应"
                : GroupCtx != null ? (GroupCtx.CurrentTurnInput ?? string.Empty)
                : npcInitiated
                    ? "最近的见闻经历、与太吾的关系、未完约定，以及此刻适合主动联系太吾的具体由头"
                    : (playerInput ?? string.Empty);
            // 主动来信没有玩家原话，仍须给事务日志一个每轮唯一键；该内部键绝不进入提示词或可见历史。
            _currentPlayerInput = npcInitiated
                ? "[JHYL_NPC_PROACTIVE:" + Guid.NewGuid().ToString("N") + "]"
                : intentInput;
            _activePendingActionTurn = null;
            _recallResult = null;
            var ct = _cts.Token;
            _turnWorldGeneration = WorldLifecycle.Generation;
            if (!WorldLifecycle.IsSameWorld(_turnWorldGeneration)) { onError?.Invoke("当前不在有效存档世界"); yield break; }
            _favorDeltaThisTurn = 0;
            _memIdsThisTurn.Clear();
            _operationIdsThisTurn.Clear();
            _toolCallsSeen.Clear();
            _loadedActionSkills.Clear();
            _npcInitiatedTurn = npcInitiated;
            _reactionState = ReactionCommitState.NotAttempted;
            _taiwuHoldings = null;
            _pendingCombat = null;
            _pendingGrooming = null;
            _deferList = null;   // 调试暗号等提前返回的路径:直接显示提示

            var client = LlmService.GetClient();
            if (client == null) { onError?.Invoke("未配置 LLM:请在本 mod 设置页或 llm.json 填入 baseUrl/apiKey/model"); yield break; }
            bool deepSeekFlashThinkingTools = client.UsesDeepSeekV4FlashThinkingToolProtocol;

            string travelStateText = null; bool travelStateDone = false;
            NpcSnapshot snap = null;
            yield return NpcSnapshotReader.Fetch(npcId, s => snap = s);
            if (!WorldLifecycle.IsSameWorld(_turnWorldGeneration)) { onError?.Invoke("已切换存档，本轮对话作废"); yield break; }
            if (snap == null || snap.TaiwuId <= 0) { onError?.Invoke("读取人物失败或无太吾"); yield break; }
            if (GroupCtx?.RefreshIdentityContext != null)
            {
                try { GroupCtx.IdentityContext = GroupCtx.RefreshIdentityContext(snap); }
                catch { GroupCtx.IdentityContext = null; }
            }
            LastNpcName = snap.Name;
            _turnWorldDate = snap.CurrentDate;
            if (snap.IsDead)
            {
                ArchivedCharacterStatusStore.ApplyAuthoritative(snap.TaiwuId, null,
                    new[] { snap.NpcId }, out _);
                ChatWindow.RevealNewSoulConversation(snap.TaiwuId, snap.NpcId);
                if (npcInitiated)
                {
                    onError?.Invoke("此人已故，灵魂不能主动发来消息");
                    yield break;
                }
                // 灵魂不再读取行程、位置或现场状态；也不套用当面/千里传音的物理联络模式。
                Remote = false;
                snap.LocationText = "灵魂状态（不受地块与行程限制）";
            }
            else
            {
                if (snap.PhysiologicalAge < 3) { onError?.Invoke("此人尚是襁褓婴孩,牙牙学语,还不能与你交谈。"); yield break; }   // 保留原判定阈值，只把年龄来源明确改为身龄
                // 活人每轮读取权威行程；死者绝不触碰该实时接口。
                EffectHandler.QueryTravelState(npcId,
                    text => { travelStateText = text; travelStateDone = true; });
                float travelStateDeadline = Time.unscaledTime + 1.5f;
                while (!travelStateDone && Time.unscaledTime < travelStateDeadline
                    && WorldLifecycle.IsSameWorld(_turnWorldGeneration)) yield return null;
                if (!string.IsNullOrWhiteSpace(travelStateText))
                    snap.LocationText = (snap.LocationText ?? "去向不明") + "\n当前行程状态:" + travelStateText.Trim();
                else
                    snap.LocationText = (snap.LocationText ?? "去向不明")
                        + "\n当前行程状态:本轮未能权威读取，不能仅凭旧对话断言某趟行程仍在执行";
                yield return RecalculateRemoteFromAuthoritativePresence(snap, ct);
                yield return RefreshAuthoritativeConversationLocations(snap, ct);
            }
            if (ct.IsCancellationRequested || !WorldLifecycle.IsSameWorld(_turnWorldGeneration))
            { onError?.Invoke("已中断"); yield break; }
            LastTurnDate = snap.CurrentDate;
            LastPlayerLocationText = PrimaryLocationText(snap.TaiwuLocationText);
            LastNpcLocationText = PrimaryLocationText(snap.LocationText);
            LastContactMode = snap.IsDead ? "灵魂状态" : Remote ? "千里传音" : "当面";
            try { onContactMode?.Invoke(Remote); } catch { }

            var toolContext = new ToolContext
            {
                InSect = !string.IsNullOrWhiteSpace(snap.SectName),
                IsMerchant = snap.IsMerchant,
                Remote = Remote,
                CanStartCombat = !Remote && GroupCtx == null && !snap.IsCaptive,
                CanOpenGrooming = !npcInitiated && !Remote && GroupCtx == null && !snap.IsCaptive,
                NpcInitiated = npcInitiated,
                ConversationOnly = snap.IsDead,
            };
            _toolRoute = GroupCtx == null
                ? ConversationToolRouter.Create(toolContext)
                : ConversationToolRouter.CreateGroup(toolContext);
            _turnToolContext = _toolRoute.Context;

            // 调试暗号由玩家在聊天框显式键入 /<暗号> 触发,LLM 永不经此路径。仅存活人物的
            // 当面普通单聊可执行；GroupCtx 守卫同时防止同一句暗号被每位群员重复落地。
            // /帮助 已在 ChatWindow 层拦截为玩家功能速览;此处只接住 /列表 等真正暗号。
            if (!npcInitiated && IsDebugCommandInput(intentInput))
            {
                if (GroupCtx == null && !snap.IsDead && !Remote)
                    HandleTestCommand(snap, intentInput, onReply);
                else
                    onReply?.Invoke("（调试暗号只在存活人物的当面普通单聊中执行。）");
                yield break;
            }

            var conv = GetConv(snap.TaiwuId, snap.NpcId);
            if (!conv.LoadReliable)
            {
                onError?.Invoke("对话动作日志已损坏，为防止旧动作重复落地，本轮已封闭停止");
                yield break;
            }
            string traceExchangeId = GroupCtx != null && !string.IsNullOrWhiteSpace(GroupCtx.ExchangeId)
                ? GroupCtx.ExchangeId : Guid.NewGuid().ToString("N");
            var traceRoot = new LlmTraceContext(
                GroupCtx != null ? traceExchangeId : Guid.NewGuid().ToString("N"),
                JianghuYoulingPaths.CurrentWorldId + ":" + snap.TaiwuId + ":" + snap.NpcId,
                traceExchangeId, GroupCtx != null ? GroupCtx.AttemptId : null);
            bool reconcileDone = snap.IsDead, reconcileOk = snap.IsDead;
            if (!snap.IsDead)
                yield return ReconcileDurableToolDispatches(conv, snap, ct,
                    ok => { reconcileOk = ok; reconcileDone = true; });
            if (!reconcileDone || !reconcileOk || !WorldLifecycle.IsSameWorld(_turnWorldGeneration))
            { onError?.Invoke("未能安全恢复上一轮动作回执，本轮未派发新动作"); yield break; }
            _activePendingActionTurn = snap.IsDead ? null
                : FindPendingActionTurn(conv, CurrentPendingTurnInputKey(), snap.CurrentDate);
            _reactionState = ReactionStateFromPendingTurn(_activePendingActionTurn);
            // JHYL_OPTIONAL_REACTION_NO_REPLY_GATE: record_reaction is an optional
            // conversational side effect.  An unresolved earlier reaction still blocks another
            // reaction mutation with different arguments, but it never blocks NPC prose or a
            // new gameplay action.  The pending journal remains available for later reconciliation.
            bool isFirst = conv.Turns.Count == 0 && string.IsNullOrEmpty(conv.Summary);

            string portrait = snap.IsDead
                ? (PortraitStore.GetPortrait(snap) ?? PortraitStore.BuildSimple(snap))
                : PortraitService.GetImmediateAndScheduleUpgrade(snap);
            if (!WorldLifecycle.IsSameWorld(_turnWorldGeneration)) { onError?.Invoke("已切换存档，本轮对话作废"); yield break; }
            if (GroupCtx == null && !ConversationStillCurrent(conv)) { onError?.Invoke("聊天记录已被清空，本轮作废"); yield break; }

            _mem = NpcMemoryStore.Load(JianghuYoulingPaths.Memories, snap.TaiwuId.ToString(), snap.NpcId.ToString());
            List<string> memory = null;
            bool memoryRecallCacheHit = false;
            if (GroupCtx?.GetCachedMemoryRecall != null)
            {
                MemoryRecaller.RecallResult cachedRecall = GroupCtx.GetCachedMemoryRecall(snap.NpcId);
                if (cachedRecall != null)
                {
                    memoryRecallCacheHit = true;
                    string retargetTopic = !string.IsNullOrWhiteSpace(GroupCtx.MemoryRecallTopic)
                        ? GroupCtx.MemoryRecallTopic : intentInput;
                    _recallResult = MemoryRecaller.RetargetDeterministically(
                        _mem.All, snap, retargetTopic);
                    memory = _recallResult.Lines ?? new List<string>();
                }
            }
            if (_recallResult == null)
            {
                string recallTopic = GroupCtx != null && !string.IsNullOrWhiteSpace(GroupCtx.MemoryRecallTopic)
                    ? GroupCtx.MemoryRecallTopic : intentInput;
                yield return MemoryRecaller.RecallDetailed(_mem.All, snap, recallTopic, r =>
                {
                    _recallResult = r;
                    memory = r?.Lines ?? new List<string>();
                }, ct);
                if (_recallResult != null && GroupCtx?.StoreCachedMemoryRecall != null)
                    GroupCtx.StoreCachedMemoryRecall(snap.NpcId, _recallResult);
            }
            // JHYL_MANDATORY_MEMORY_SEARCH_EVERY_TURN: memory search is an orchestrator
            // preflight, not an optional model decision. This preserves the faster and more
            // accurate tiered TopK/MMR/semantic recall path while guaranteeing that every
            // NPC reply (including each group member reply) consults its own long-term memory.
            // The player input already supplies the first search topic. recall_memory stays
            // in the stable scene schema for the whole loop; if the model asks the same query
            // again, the local per-turn dedupe below returns the existing result without a
            // second retrieval. Removing it here made providers call a prompt-mentioned but
            // hidden tool and surface a misleading "未授权工具" protocol failure.
            int mandatoryMemoryHits = _recallResult?.Lines?.Count ?? 0;
            Debug.Log("[江湖有灵] 第0轮·工具:recall_memory tc=local-recall");
            Debug.Log("[江湖有灵] 工具结果 name=recall_memory status="
                + (memoryRecallCacheHit ? "exchange-cache" : "retrieved") + " hits=" + mandatoryMemoryHits);
            LlmLog.RecordTrajectory(GroupCtx != null ? "群聊成员" : npcInitiated ? "NPC主动消息" : "单聊",
                traceRoot.WithRound(0, "mandatory-memory-search"), "recall_memory",
                memoryRecallCacheHit ? "exchange-cache" : "retrieved");
            if (ct.IsCancellationRequested) { onError?.Invoke("已中断"); yield break; }
            if (!WorldLifecycle.IsSameWorld(_turnWorldGeneration)) { onError?.Invoke("已切换存档，本轮对话作废"); yield break; }
            if (GroupCtx == null && !ConversationStillCurrent(conv)) { onError?.Invoke("聊天记录已被清空，本轮作废"); yield break; }

            // 旧对话整理不再阻塞本轮正文。成功提交玩家输入、NPC 回话和动作父记录后，
            // ScheduleCompactionAfterCommit 会在独立协程中整理记忆与梗概。

            // 预载常用动作的确切候选。赠物/传授只在当面需要；自修与读书不依赖太吾位置，
            // 所以千里传音场景也要读取。工具派发前还会再查一次完成度，避免长回复期间状态变化。
            if (!snap.IsDead)
            {
                if (!Remote)
                {
                    yield return NpcSnapshotReader.FetchGiftables(snap.NpcId, snap);
                    if (ct.IsCancellationRequested) { onError?.Invoke("已中断"); yield break; }
                    yield return NpcSnapshotReader.FetchLifeSkills(snap.NpcId, snap);
                    if (ct.IsCancellationRequested) { onError?.Invoke("已中断"); yield break; }
                }
                yield return NpcSnapshotReader.FetchSkills(snap.NpcId, snap);
                if (ct.IsCancellationRequested) { onError?.Invoke("已中断"); yield break; }
                yield return NpcSnapshotReader.FetchStudyProgress(snap.NpcId, snap,
                    () => TurnToolDispatchAllowed(ct));
                if (ct.IsCancellationRequested) { onError?.Invoke("已中断"); yield break; }
                if (ToolRegistry.IsAgentToolEnabled("use_item"))
                {
                    yield return NpcSnapshotReader.FetchUsableItems(snap.NpcId, snap,
                        () => TurnToolDispatchAllowed(ct));
                    if (ct.IsCancellationRequested) { onError?.Invoke("已中断"); yield break; }
                }
            }
            if (!WorldLifecycle.IsSameWorld(_turnWorldGeneration)) { onError?.Invoke("已切换存档，本轮对话作废"); yield break; }
            if (GroupCtx == null && !ConversationStillCurrent(conv)) { onError?.Invoke("聊天记录已被清空，本轮作废"); yield break; }
            if (!Remote)
            {
                Debug.Log("[江湖有灵] 预载 npc=" + snap.NpcId + ":可赠 " + snap.GiftableItems.Count
                    + " · 可传武学 " + snap.LearnableSkills.Count + " · 可传技艺 " + snap.LearnableLifeSkills.Count
                    + " · 尚未练满 " + snap.IncompleteTrainingSkillIds.Count + " · 尚未读完 " + snap.UnreadBookNames.Count);
            }

            var prof = PortraitStore.BuildProfile(snap);
            prof.Portrait = portrait;
            prof.CustomPersona = JianghuYouling.Core.Persona.PersonaStore.Load(JianghuYoulingPaths.Personas, snap.TaiwuId.ToString(), snap.NpcId.ToString());
            prof.CustomPersonaMode = JianghuYouling.Core.Persona.PersonaStore.LoadMode(JianghuYoulingPaths.Personas, snap.TaiwuId.ToString(), snap.NpcId.ToString());
            if (!string.IsNullOrWhiteSpace(prof.CustomPersona))
            {
                Debug.Log("[江湖有灵] 已载入自定义人设(" + prof.CustomPersona.Length + " 字)npc=" + snap.NpcId);
            }
            string externalExperience = null;
            yield return JianghuBrothelContextReader.Fetch(snap.NpcId,
                value => externalExperience = value);
            if (ct.IsCancellationRequested) { onError?.Invoke("已中断"); yield break; }
            if (!WorldLifecycle.IsSameWorld(_turnWorldGeneration))
            { onError?.Invoke("已切换存档，本轮对话作废"); yield break; }
            prof.ExternalExperienceText = externalExperience;

            // 每轮重新读取一次完整快照。短指纹会放到历史之后，令已有长会话明确淘汰旧世界设定；
            // 世界书正文仍只在稳定前缀出现，不在动态尾部重复，避免额外 token 和缓存失效。
            var worldBook = WorldBookStore.LoadSnapshot(snap.TaiwuId);
            string thinkingPrompt = ThinkingPromptRuleStore.LoadForPrompt();
            var tctx = new TalkContext
            {
                WorldState = BuildWorldState(snap),
                WorldBook = worldBook.EffectiveText,
                WorldBookFingerprint = worldBook.ContentFingerprint,
                Difficulty = Difficulty,
                ReplyLength = ReplyLength,
                CurrentMonth = snap.CurrentDate,
                NpcInitiated = npcInitiated,
                ThinkingPrompt = thinkingPrompt,
            };
            var msgs = TalkPromptBuilder.Build(prof, memory, conv.Turns, playerInput, isFirst, conv.Summary, tctx);
            string commissionContext = CommissionStore.PromptContext(snap.TaiwuId,
                snap.NpcId, snap.CurrentDate);
            if (!string.IsNullOrWhiteSpace(commissionContext))
                msgs.Insert(Math.Max(0, msgs.Count - 1), LlmMessage.System(commissionContext));
            if (DirectTaiwuAction != null)
                msgs.Insert(Math.Max(0, msgs.Count - 1),
                    LlmMessage.System(DirectTaiwuAction.PromptInstruction()));
            // #5 重试:玩家对上一回话不满,要求重讲 → 显式禁止复述原句,确保换说法(规避供应商按提示词命中的缓存/确定性导致逐字雷同)
            if (!string.IsNullOrWhiteSpace(retryAvoid))
            {
                string avoid = retryAvoid.Trim();
                if (avoid.Length > 220) avoid = avoid.Substring(0, 220) + "…";
                InsertAfterStablePrefix(msgs, LlmMessage.System("【重讲】玩家对你上一回的话不满意,要你换个说法重讲一遍。务必与下面这句明显不同——换措辞、换角度或换说法,别再雷同复述;仍只用角色自己的话,绝不要提及『重讲』『重试』或任何后台机制。上一回你说的是:「" + avoid + "」"));
            }
            if (Remote) InsertAfterStablePrefix(msgs, LlmMessage.System("【千里传音】此刻你与太吾并不在一处,而是借「千里传音」远隔传声相谈——只闻其声、不见其人。凡需当面、动手、过物的事(把东西交到太吾手上、当面传功授艺、为太吾疗伤、与他买卖换物、共处之事等)此刻都办不到;太吾若要你做这类事,你须如实说『你我远隔、待相见再说』,或应承日后相见再办,切莫假称已当面做成。言语、心意、消息、对你身边旁人的安排,照常可行。"));
            if (snap.IsCaptive) InsertAfterStablePrefix(msgs, LlmMessage.System("【身陷囹圄】此刻你是太吾的阶下囚——被擒后关在太吾据点的牢中,就在太吾面前,身上无兵刃、插翅难飞。你与太吾是当面相对。依你的性子应对:或宁死不屈、或破口大骂、或虚与委蛇、或摇尾乞怜、或伺机求生;尽可与太吾讨价还价(供出秘闻、归顺投效、以一身技艺/财物换条生路等),但你身在押中、行动受制,切莫假装能自由来去、当场拔腿就走或唤人来救。"));
            LlmMessage groupContextMessage = null;
            if (GroupCtx != null)
            {
                groupContextMessage = LlmMessage.System(GroupCtx.BuildNote());
                InsertAfterStablePrefix(msgs, groupContextMessage);
            }
            string pendingActionNote = PendingActionNote();
            if (!string.IsNullOrWhiteSpace(pendingActionNote))
                msgs.Insert(Math.Max(0, msgs.Count - 1), LlmMessage.System(pendingActionNote));
            msgs.Insert(Math.Max(0, msgs.Count - 1), LlmMessage.System(
                "【稳定工具表】本轮已提供当前现场可用的完整查询与行动能力，不需要也不能另行申请或补载工具。要改变游戏状态必须调用对应行动，并以游戏权威回执为准。"));
            msgs.Insert(Math.Max(0, msgs.Count - 1), LlmMessage.System(
                "【每轮长期记忆搜索】系统已按玩家本轮话题强制执行一次 recall_memory 分层搜索，结果已放入长期记忆上下文；这一步每轮都不会跳过。通常无需重复搜索；确需换一个更窄主题时仍可调用 recall_memory。"));
            // 当前权威现场对应的稳定完整工具表。它在整个 Agent 循环中不增删；
            // “动作已落地只补反应”等终态约束只在派发层执行，避免 provider 的历史意图、
            // 当前 schema 与本地校验集合发生漂移。
            var situationTools = new List<ToolDef>(_toolRoute.Tools ?? new List<ToolDef>());
            var tools = new List<ToolDef>(situationTools);
            int inputBudget = LlmService.InputBudgetFor(client);
            if (inputBudget <= 0)
            {
                onError?.Invoke("模型上下文窗口配置不足，无法安全容纳本轮输入与输出");
                yield break;
            }
            var budget = PromptBudgeter.Apply(msgs, tools, inputBudget);
            Debug.Log("[JHYL_PROMPT_BUDGET] before=" + budget.BeforeTokens + " after=" + budget.AfterTokens
                + " removed=" + budget.RemovedMessages + " dynamic_trim=" + budget.TrimmedDynamicSections
                + " tools=" + tools.Count + " stable_over=" + budget.StablePrefixExceedsBudget);
            if (budget.ExceedsBudget)
            {
                onError?.Invoke(budget.StablePrefixExceedsBudget
                    ? "世界书、人设与工具定义已超过模型上下文窗口，请增大上下文窗口或精简自定义内容"
                    : "本轮输入已超过模型上下文窗口，且无法在不破坏当前工具链的前提下安全裁剪");
                yield break;
            }
            _deferList = new List<NoticeItem>();   // 本轮开始攒「获得」提示,待回话揭示后再显示
            _landedThisTurn = new List<string>();   // 本轮开始攒"真落地的动作",收尾写进 NPC 记忆
            _toolResultsThisTurn = new List<string>(); // 与绿色提示同源，收尾后可重开回放并喂给后续 Agent
            _taiwuSecretsDisclosedThisTurn = new List<string>();
            _thirdPartySecretsDisclosedThisTurn = new List<string>();
            RegisterRecoveredSecretDisclosures(_activePendingActionTurn, snap);
            bool thirdPartySecretPrivacyClosure = HasThirdPartyOnlySecretDisclosure();
            if (thirdPartySecretPrivacyClosure)
                SecretDisclosureProjection.ScrubRequestMessagesInPlace(msgs,
                    _thirdPartySecretsDisclosedThisTurn, _taiwuSecretsDisclosedThisTurn);
            if (DirectTaiwuAction != null
                && !string.IsNullOrWhiteSpace(DirectTaiwuAction.ToolResultText))
                _toolResultsThisTurn.Add(DirectTaiwuAction.ToolResultText.Trim());

            // —— agentic 工具循环 ——
            string reply = thirdPartySecretPrivacyClosure
                ? BuildThirdPartySecretPrivacyReply(snap) : null;
            bool replyStreamed = false;   // 流式:终稿正文是否已逐字显示过(显示过则收尾不再打字机重放)
            int totalPrompt = 0, totalCompletion = 0;
            const int MaxRounds = 6;   // 常态上限；确定失败时可额外保留据失败原因改方案或说完的两轮。
            const int FailureRecoveryRounds = 2;
            const float ProgressMinDwell = 1.5f;   // 每条动态进度文字至少显示 1.5 秒(结果有常驻彩条反馈,无需停太久)
            int httpRetries = 0;
            int toolProtocolRetries = 0;
            bool toolProtocolProseFallbackUsed = false;
            int placeholderProseRetries = 0;
            // 不在入口层猜“上一句邀请 + 这一句专名”属于哪一种动作。除非当前输入和最近
            // 上下文都能高置信确认为纯寒暄，否则首轮就开启完整推理，由已获得完整历史、
            // 当前人物关系与全量场景工具的主模型判断是否需要查询或行动。
            bool definitePureSmallTalk = ToolCallHeuristics.IsDefinitelyPureSmallTalk(intentInput,
                GroupCtx == null ? conv.Turns : null);
            string nextToolChoice = "auto";
            bool pendingFailedActionProse = false;
            bool failureRecoveryBudgetAdded = false;
            string lastDefiniteFailureReceipt = null;
            // Tool schemas stay scene-stable for every network round. Closure modes are
            // enforced at dispatch time, never by removing valid tools from the schema;
            // providers that retain a recall/query intention therefore cannot produce the
            // misleading “本轮未授权工具” protocol error.
            bool proseClosureOnly = false;
            bool terminalReceiptClosureOnly = false;
            bool toolWorkflowActive = false;
            int roundLimit = thirdPartySecretPrivacyClosure ? 0 : MaxRounds;
            double previousCacheRatio = 0;
            bool stream = StreamingEnabled && !StreamAutoDisabled && onDelta != null
                && !thirdPartySecretPrivacyClosure;   // 第三方秘闻边界必须等最终安全投影后才允许进入可见正文
            string llmTag = GroupCtx != null ? "群聊成员" : npcInitiated ? "NPC主动消息" : "单聊";
            for (int round = 1; round <= roundLimit; round++)
            {
                if (GroupCtx != null)
                {
                    bool relationshipFresh = false;
                    yield return RefreshGroupRelationshipsForAgentRound(snap, ct, round,
                        ok => relationshipFresh = ok);
                    if (!relationshipFresh)
                    {
                        onError?.Invoke("未能可靠刷新群内所有人物的关系，本成员已停止；已完成动作仍会按真实回执保存");
                        yield break;
                    }
                    // Keep one stable message position, but refresh its authoritative relation
                    // payload before every actual model request. A relationship mutation from
                    // the previous tool round is therefore visible immediately in the next round.
                    groupContextMessage.Content = GroupCtx.BuildNote();
                }
                // 工具结果会在循环中增长；工具 schema 保持稳定，但仍须每次出网前重新预算，
                // 绝不能只检查首轮后让后续工具链悄悄突破 provider 上下文窗口。
                var contextPrune = AgentContextPruner.Apply(msgs, previousCacheRatio);
                if (contextPrune.ReclaimedCharacters > 0)
                    Debug.Log("[JHYL_AGENT_CONTEXT_PRUNE] mode=" + llmTag
                        + " compacted=" + contextPrune.CompactedResults
                        + " deduped=" + contextPrune.DeduplicatedResults
                        + " chars=" + contextPrune.ReclaimedCharacters);
                var roundBudget = PromptBudgeter.Apply(msgs, tools, inputBudget);
                if (roundBudget.ExceedsBudget)
                {
                    onError?.Invoke(roundBudget.StablePrefixExceedsBudget
                        ? "世界书、人设与工具定义已超过模型上下文窗口，请增大上下文窗口或精简自定义内容"
                        : "本轮工具链已超过模型上下文窗口，已停止以避免截断或伪造结果");
                    yield break;
                }
                onProgress?.Invoke("思忖中…");
                bool streamedThisRound = false;
                string toolChoice = nextToolChoice; nextToolChoice = "auto";
                // 单聊/群聊同样可能执行复杂多步动作，不能按入口一刀切关闭思考。
                // 纯闲聊使用 Low（V4 Pro 会真正关闭思考）；已经进入工具链、失败后重规划，
                // 或语义上不是确定寒暄时使用 Auto（V4 Pro 高推理）。不要另设低于用户
                // 配置的 completion 上限：推理 token 与正文共用该上限，硬截断会把完整
                // 工具调用变成 finish_reason=length，造成长时间等待后无正文。
                LlmReasoningPolicy roundReasoningPolicy = toolWorkflowActive || pendingFailedActionProse
                    || !definitePureSmallTalk
                    ? LlmReasoningPolicy.Auto : LlmReasoningPolicy.Low;
                Debug.Log("[JHYL_REASONING_POLICY] mode=" + llmTag + " round=" + round
                    + " policy=" + roundReasoningPolicy + " pure_smalltalk=" + definitePureSmallTalk
                    + " workflow=" + toolWorkflowActive + " closure=" + proseClosureOnly);
                int roundMaxTokens = 0;
                // 查询/动作链保持 auto，让模型拿到回执后可以直接提交正文。
                // record_reaction 只是模型可主动选择的附带变化，不会触发额外网络收口轮。
                string groupBudgetError;
                LlmToolResult tr;
                if (stream)
                {
                    // raw-socket 读续体在线程池线程触发回调,而 Unity UI 只能主线程动 → 增量入并发队列,由本协程在主线程抽干再喂 UI(否则后台直接动 UI 会抛异常被吞、看不到逐字)
                    var sq = new System.Collections.Concurrent.ConcurrentQueue<KeyValuePair<int, string>>();
                    // 人物持有秘闻不应导致整轮思考消失。这里只对实时思考回调做
                    // 跨 SSE 分片的精确正文遮蔽；模型内部仍能选择秘闻并调用工具，
                    // 真正成功告知太吾后，最终回话仍由权威投影完整显示该事实。
                    var privateThinkingTexts = new List<string>();
                    if (snap.ShareableSecrets != null)
                        foreach (SecretRef secret in snap.ShareableSecrets)
                        {
                            string exact = SecretDisclosureProjection.NormalizeSecret(secret?.Text);
                            if (exact.Length == 0) continue;
                            privateThinkingTexts.Add(exact);
                            // 初始提示为了省 token 只预载前 40 字；模型在思考中原样引用
                            // 该预览或去掉省略号时，也必须视作同一未公开事实遮住。
                            if (exact.Length > 40)
                            {
                                string preview = exact.Substring(0, 40);
                                privateThinkingTexts.Add(preview);
                                privateThinkingTexts.Add(preview + "…");
                            }
                        }
                    var thinkingPrivacyRedactor = new IncrementalExactTextRedactor(privateThinkingTexts);
                    Action<string> enqueueThinking = d =>
                    {
                        if (!string.IsNullOrEmpty(d))
                            sq.Enqueue(new KeyValuePair<int, string>(1, d));
                    };
                    // 工具轮可能先吐一段临时正文,随后才给 tool_calls;这段不是终稿,显示出来再清掉会像"写到一半删除重来"。
                    // 因此对带工具的会话只实时展示思考/工具进度,可见正文等本轮确认可收尾后再统一打字显示。
                    bool suppressVisibleDraft = tools != null && tools.Count > 0;
                    Action<string> onStreamContent = suppressVisibleDraft
                        ? (Action<string>)null
                        : (d => { if (!string.IsNullOrEmpty(d)) sq.Enqueue(new KeyValuePair<int, string>(0, d)); });
                    if (!TryReserveGroupModelRequest(client, msgs, tools, roundMaxTokens,
                        out groupBudgetError)) { onError?.Invoke(groupBudgetError); yield break; }
                    var stask = client.SendToolRoundStreamAsyncWithTrace(msgs, tools,
                        onStreamContent,   // 0=正文
                        d =>
                        {
                            if (thinkingPrivacyRedactor.HasValues)
                                thinkingPrivacyRedactor.Push(d, enqueueThinking);
                            else enqueueThinking(d);
                        },   // 1=思考
                        nm => { if (!string.IsNullOrEmpty(nm)) sq.Enqueue(new KeyValuePair<int, string>(2, nm)); }, // 2=工具名
                        toolChoice, roundMaxTokens, 0.8, ct, tag: llmTag,
                        reasoningPolicy: roundReasoningPolicy, trace: traceRoot.WithRound(round));
                    while (!stask.IsCompleted)
                    {
                        if (DrainStream(sq, onDelta, onThinking, onProgress)) streamedThisRound = true;
                        yield return null;
                    }
                    if (thinkingPrivacyRedactor.HasValues)
                        thinkingPrivacyRedactor.Flush(enqueueThinking);
                    if (DrainStream(sq, onDelta, onThinking, onProgress)) streamedThisRound = true;   // 收尾抽干残留
                    if (ct.IsCancellationRequested || !WorldLifecycle.IsSameWorld(_turnWorldGeneration)) { onError?.Invoke("已中断"); yield break; }
                    tr = stask.IsFaulted
                        ? new LlmToolResult { Ok = false, Error = stask.Exception?.GetBaseException()?.Message ?? "未知错误" }
                        : stask.Result;
                    if (tr != null && tr.Ok) _streamFailStreak = 0;   // 本轮流式顺利 → 清零失败计数
                }
                else
                {
                    if (!TryReserveGroupModelRequest(client, msgs, tools, roundMaxTokens,
                        out groupBudgetError)) { onError?.Invoke(groupBudgetError); yield break; }
                    var task = client.SendToolRoundAsyncWithTrace(msgs, tools, toolChoice, roundMaxTokens, 0.8, ct,
                        tag: llmTag, reasoningPolicy: roundReasoningPolicy, trace: traceRoot.WithRound(round));
                    yield return new WaitUntil(() => task.IsCompleted);
                    if (ct.IsCancellationRequested || !WorldLifecycle.IsSameWorld(_turnWorldGeneration)) { onError?.Invoke("已中断"); yield break; }
                    tr = task.IsFaulted
                        ? new LlmToolResult { Ok = false, Error = task.Exception?.GetBaseException()?.Message ?? "未知错误" }
                        : task.Result;
                }
                if (tr != null && tr.Canceled) { onError?.Invoke("已中断"); yield break; }

                // 流式失败(空闲超时/连接异常,非取消)→ 本轮立刻回退非流式重取一次(而不是再卡一次流式);
                // 已逐字显示过的临时正文先清掉。非流式走缓冲 HttpClient,对它无碍。
                if (stream && tr != null && !tr.Ok && !tr.Canceled)
                {
                    Debug.Log("[江湖有灵] 流式失败,回退非流式 " + LlmErrorLogMetadata(tr));
                    NoteStreamTrouble();   // 计一次流式失败,连续超限则本会话自动停用流式
                    // 同一对话后续工具轮立即改走非流式。stream 是循环外的局部快照，
                    // 仅更新全局熔断位不能阻止当前 turn 继续重复失败的流式请求。
                    stream = false;
                    if (streamedThisRound) { onResetReply?.Invoke(); streamedThisRound = false; }
                    onProgress?.Invoke("整理思绪中…");
                    // Preserve the same provider-neutral auto/none mode when stream falls back.
                    if (!TryReserveGroupModelRequest(client, msgs, tools, roundMaxTokens,
                        out groupBudgetError)) { onError?.Invoke(groupBudgetError); yield break; }
                    var taskNs = client.SendToolRoundAsyncWithTrace(msgs, tools, toolChoice, roundMaxTokens, 0.8, ct,
                        tag: llmTag, reasoningPolicy: roundReasoningPolicy,
                        trace: traceRoot.WithRound(round, "stream-fallback"));
                    yield return new WaitUntil(() => taskNs.IsCompleted);
                    if (ct.IsCancellationRequested || !WorldLifecycle.IsSameWorld(_turnWorldGeneration)) { onError?.Invoke("已中断"); yield break; }
                    if (!taskNs.IsFaulted && taskNs.Result != null) tr = taskNs.Result;
                }

                if (tr == null || !tr.Ok)
                {
                    Debug.Log("[江湖有灵] 工具调用失败 " + LlmErrorLogMetadata(tr));
                    // A provider may still emit an obsolete/read-only alias or a tool name
                    // carried over from earlier context. Keep the same stable scene schema,
                    // give it one bounded correction turn, and never expose an internal
                    // "authorization" failure as the NPC's final reply.
                    bool recoverableToolProtocol = ToolProtocolRecoveryPolicy.IsRecoverable(tr?.Error);
                    if (toolProtocolRetries < 1 && recoverableToolProtocol)
                    {
                        toolProtocolRetries++;
                        msgs.Add(LlmMessage.System(ToolProtocolRecoveryPolicy.BuildCorrectionPrompt(tr?.Error)));
                        nextToolChoice = toolChoice;
                        round--;
                        continue;
                    }
                    // 同一个 schema/工具名错误纠正一次仍失败时，不把内部错误直接显示给玩家，
                    // 也不继续烧多轮等体请求。保留“动作未执行”语义，强制无工具角色正文收尾。
                    if (recoverableToolProtocol && !toolProtocolProseFallbackUsed)
                    {
                        toolProtocolProseFallbackUsed = true;
                        proseClosureOnly = true;
                        msgs.Add(LlmMessage.System(
                            "刚才的工具调用参数再次无效，因此那项动作没有执行。现在不得再调用工具；"
                            + "请只用角色自己的口吻自然回应当前谈话。若刚才本想办事，就如实说尚未办成或换成纯言语回应，"
                            + "不要提工具、参数、系统、schema、报错或重试。"));
                        nextToolChoice = "none";
                        if (round >= roundLimit) roundLimit++;
                        continue;
                    }
                    // 底层非流式传输已经对瞬时断线重试一次。只有底层尚未重试时，
                    // 编排层才补一次服务重试，避免两层叠加成 3~6 个等体请求。
                    if (httpRetries < 1 && (tr == null || tr.RetryCount == 0)
                        && OpenAiCompatibleClient.IsRetryableServiceError(tr != null ? tr.Error : null))
                    {
                        // 这是同一逻辑轮的服务重试，保留相同的 auto/none 模式。
                        nextToolChoice = toolChoice;
                        httpRetries++; yield return new WaitForSecondsRealtime(0.6f * httpRetries); round--; continue;
                    }
                    onError?.Invoke("LLM 失败:" + (tr != null ? tr.Error : "null")); yield break;
                }

                // 兜底:某些端不认 stream=true、整段非 SSE 返回 → 流式拿到空壳。本轮回退非流式重取一次,免空回话。
                if (stream && !streamedThisRound && !tr.HasToolCalls && string.IsNullOrEmpty(tr.Content))
                {
                    NoteStreamTrouble();   // 空壳=该端不支持流式,计一次失败,连续超限则本会话自动停用流式
                    if (!TryReserveGroupModelRequest(client, msgs, tools, roundMaxTokens,
                        out groupBudgetError)) { onError?.Invoke(groupBudgetError); yield break; }
                    var task2 = client.SendToolRoundAsyncWithTrace(msgs, tools, toolChoice,
                        roundMaxTokens, 0.8, ct,
                        tag: llmTag, reasoningPolicy: roundReasoningPolicy,
                        trace: traceRoot.WithRound(round, "empty-stream-fallback"));
                    yield return new WaitUntil(() => task2.IsCompleted);
                    if (!task2.IsFaulted && task2.Result != null && task2.Result.Ok) tr = task2.Result;
                }

                if (GroupCtx == null && !ConversationStillCurrent(conv))
                { onError?.Invoke("聊天记录已被清空，本轮动作与回话作废"); yield break; }

                // Provider enum guesses must not change who actually initiated the current duel.
                // Normalize after the possible non-stream fallback so the exact response that will
                // be logged, replayed and executed shares one canonical set of arguments.
                if (tr.HasToolCalls)
                {
                    ConversationToolCallNormalizer.Normalize(tr.ToolCalls);
                    // The entry point does not determine complexity. A seemingly casual single/group-chat
                    // turn may discover a query or mutation tool and grow into a multi-step workflow.
                    toolWorkflowActive = true;
                }

                totalPrompt += tr.PromptTokens; totalCompletion += tr.CompletionTokens;
                previousCacheRatio = tr.PromptTokens > 0
                    ? (double)tr.CachedTokens / tr.PromptTokens : previousCacheRatio;
                onTokens?.Invoke(totalPrompt, totalCompletion);

                // 诊断:每轮模型到底调了哪些工具(空=纯说话没调工具),供排查"该调 teach/gift 却不调"
                if (tr.HasToolCalls)
                {
                    var _tn = new System.Text.StringBuilder();
                    foreach (var _c in tr.ToolCalls) { if (_tn.Length > 0) _tn.Append(','); _tn.Append(_c.Name); }
                    Debug.Log("[江湖有灵] 第" + round + "轮·工具:" + _tn + " tc=" + toolChoice);
                    // 生产 Player.log 只记录结构化元数据；参数可能含私密记忆、秘闻或玩家原话。
                    for (int _i = 0; _i < tr.ToolCalls.Count; _i++)
                    {
                        var _c = tr.ToolCalls[_i];
                        Debug.Log("[江湖有灵] 工具明细#" + _i + " " + ToolCallLogMetadata(_c));
                    }
                }
                else Debug.Log("[江湖有灵] 第" + round + "轮·纯说话(未调工具)tc=" + toolChoice + " len=" + (tr.Content == null ? 0 : tr.Content.Length));

                if (tr.HasToolCalls)
                {
                    // 提速:若本轮工具全是"动作/记录"类(结果模型无需再读)、且模型已给出正文 →
                    // 该正文即终稿,执行完工具直接收尾,省去再问一轮(凡含动作的对话都砍掉一次往返)。
                    // 检索类(query_*/recall)结果模型要读后再答,必须再问一轮。
                    bool anyRetrieval = false;
                    foreach (var c in tr.ToolCalls) if (IsRetrievalTool(c.Name)) { anyRetrieval = true; break; }
                    // 本轮是否真调过【动作类】工具(非检索、非纯记录 record_reaction)——哪怕异步落地一时回 -1(未即时进 _landedThisTurn),也算"已真办",
                    // 据此免 PromiseNudge 把"刚下毒/刚动手(回执 -1)"误判成空头承诺再逼一遍 → 杜绝双杀/双下毒。
                    bool roundActionFailed = false;
                    bool roundActionUnconfirmed = false;
                    // Target-sensitive prose must be written after the model has read the
                    // authoritative actor/target receipt. Accepting content from the same
                    // assistant message that merely proposed the tool allowed a third-party
                    // duel/favor narration to accompany an operation targeting current Taiwu.
                    bool canFinish = !anyRetrieval && !RequiresPostReceiptProse(tr.ToolCalls)
                        && !IsPlaceholderToolReply(tr.Content);
                    bool requiresLanding = ToolCallHeuristics.RequiresLandingConfirmation(tr.ToolCalls);
                    if (proseClosureOnly) requiresLanding = false;
                    int landedBeforeTools = _landedThisTurn == null ? 0 : _landedThisTurn.Count;

                    // 流式且本轮非终稿(还要再问一轮):此前逐字显示的正文不是最终回话 → 清掉,重回思考点
                    if (stream && streamedThisRound && !canFinish) { onResetReply?.Invoke(); streamedThisRound = false; }

                    // DeepSeek thinking 工具轮要求下一步原样回灌 reasoning_content；Gemini 的
                    // thought_signature 则已保存在每个 LlmToolCall.ExtraFields 中，一并由 MsgToJson 回灌。
                    msgs.Add(LlmMessage.WithToolCalls(tr.ToolCalls, tr.Content, tr.ReplayReasoningContent));
                    var calls = tr.ToolCalls;
                    if (proseClosureOnly || calls.Count <= 1 || !CanRunToolsInParallel(calls))
                    {
                        // JHYL_ACTION_TOOLS_SEQUENTIAL_BATCH:只有纯检索批次并发;含动作时顺序执行,让后续动作能承接前一动作真实结果。
                        foreach (var call in calls)
                        {
                            // TOOL_DISPATCH_CANCELLATION_GATE：群聊超时也必须在每个工具派发前生效，
                            // 不能只依赖单聊 ConversationStillCurrent。
                            if (!TurnToolDispatchAllowed(ct)) { onError?.Invoke("已中断，后续工具未执行"); yield break; }
                            if (GroupCtx == null && !ConversationStillCurrent(conv)) { onError?.Invoke("聊天记录已被清空，本轮作废"); yield break; }
                            if (proseClosureOnly)
                            {
                                const string closureResult = "未执行：上一轮工具结果已经给出，本阶段只需输出角色正文，不再执行任何工具。";
                                msgs.Add(LlmMessage.Tool(call?.Id, closureResult));
                                Debug.Log("[JHYL_STABLE_PROSE_CLOSURE] name=" + SafeLogToken(call?.Name, 64));
                                continue;
                            }
                            bool optionalReaction = string.Equals(call.Name, "record_reaction", StringComparison.Ordinal);
                            string dispatchArgumentsJson = call.ArgumentsJson;
                            // 群聊各成员的 LLM 可并行思考/检索；真实副作用由中央协调器按导演顺序提交。
                            // 这样后位成员不会在前位成员尚未落地时基于同一旧快照抢着改生命/关系/库存。
                            GroupDispatchGuard groupGuard = null;
                            bool groupClaimAcquired = false;
                            bool needsPresenceGuard = !optionalReaction && !IsRetrievalTool(call.Name)
                                && (GroupCtx != null || IsBackendDurableMutationTool(call.Name));
                            if (needsPresenceGuard)
                            {
                                while (GroupCtx != null && GroupCtx.ActionCoordinator != null
                                    && !GroupCtx.ActionCoordinator.CanDispatch(GroupCtx.DispatchOrder))
                                {
                                    if (!TurnToolDispatchAllowed(ct)) { onError?.Invoke("已中断，群聊动作未派发"); yield break; }
                                    yield return null;
                                }
                                yield return BuildGroupDispatchGuard(call.Name, call.ArgumentsJson, snap, ct, g => groupGuard = g);
                                if (groupGuard == null || !groupGuard.Allowed)
                                {
                                    string rejected = "(PHYSICAL_ACTION_REJECTED：" + (groupGuard?.Error ?? "无法验证动作的真实对象与当前位置")
                                        + "。本动作未执行；请据实改口，不得假称完成。)";
                                    Debug.LogWarning("[江湖有灵][动作现场校验] " + rejected);
                                    msgs.Add(LlmMessage.Tool(call.Id, rejected));
                                    roundActionFailed = true;
                                    if (string.IsNullOrWhiteSpace(lastDefiniteFailureReceipt))
                                        lastDefiniteFailureReceipt = rejected;
                                    LlmLog.RecordTrajectory(llmTag, traceRoot.WithRound(round), call?.Name, "rejected");
                                    continue;
                                }
                                if (string.Equals(call.Name, "tell_secret", StringComparison.Ordinal)
                                    && !TryFreezeTellSecretDispatchArguments(call.ArgumentsJson, snap,
                                        groupGuard.CanonicalSecretRecipientId,
                                        out dispatchArgumentsJson, out string freezeError))
                                {
                                    string rejected = "(未执行：" + (freezeError ?? "无法冻结秘闻接收者与正文")
                                        + "。请重新调用 query_npc_secrets(to=同一接收者)，再照抄原始序号；本次没有吐露任何秘闻。)";
                                    msgs.Add(LlmMessage.Tool(call.Id, rejected));
                                    roundActionFailed = true;
                                    if (string.IsNullOrWhiteSpace(lastDefiniteFailureReceipt))
                                        lastDefiniteFailureReceipt = rejected;
                                    LlmLog.RecordTrajectory(llmTag, traceRoot.WithRound(round), call?.Name, "rejected");
                                    continue;
                                }
                                if (GroupCtx != null && GroupCtx.ActionCoordinator != null
                                    && !GroupCtx.ActionCoordinator.TryClaimResolved(GroupCtx.MemberId, GroupCtx.SelfName,
                                    groupGuard.Footprint, out string conflict))
                                {
                                    string rejected = "(RESOURCE_CONFLICT：" + conflict + "。本动作未执行；请据实改口，不得假称完成。)";
                                    Debug.LogWarning("[江湖有灵][群聊权威仲裁] " + rejected);
                                    msgs.Add(LlmMessage.Tool(call.Id, rejected));
                                    roundActionFailed = true;
                                    if (string.IsNullOrWhiteSpace(lastDefiniteFailureReceipt))
                                        lastDefiniteFailureReceipt = rejected;
                                    LlmLog.RecordTrajectory(llmTag, traceRoot.WithRound(round), call?.Name, "rejected");
                                    continue;
                                }
                                groupClaimAcquired = GroupCtx != null && GroupCtx.ActionCoordinator != null;
                            }
                            float shownAt = Time.unscaledTime;
                            onProgress?.Invoke(ProgressLabel(call.Name));
                            string result = null;
                            ToolOutcome toolOutcome = null;
                            LocalToolSemantic localToolSemantic = LocalToolSemantic.NotApplicable;
                            PendingToolDispatch durableDispatch = null;
                            bool durableMutation = IsBackendDurableMutationTool(call.Name);
                            bool parentPrepared = !durableMutation || GroupCtx == null || PrepareGroupCommitBarrier();
                            bool shouldDispatch = parentPrepared && (!durableMutation
                                || PrepareDurableToolDispatch(conv, snap, call.Name, dispatchArgumentsJson, out durableDispatch, out result));
                            if (!parentPrepared)
                                result = "(群聊父事务日志无法在动作前可靠落盘，本动作没有派发。)";
                            if (durableMutation && durableDispatch != null)
                            {
                                if (!_operationIdsThisTurn.Contains(durableDispatch.OperationId))
                                    _operationIdsThisTurn.Add(durableDispatch.OperationId);
                                if (GroupCtx != null && !PrepareGroupCommitBarrier())
                                {
                                    shouldDispatch = false;
                                    result = "(群聊父事务未能可靠绑定动作 operation_id，本动作没有派发。)";
                                    CommitDurableToolDispatch(conv, snap, durableDispatch, result,
                                        ToolOutcome.Failed("group_parent_barrier_failed", durableDispatch.OperationId,
                                            null, "mutation was not dispatched"));
                                }
                                else if (shouldDispatch
                                    && !OperationRpcClient.RegisterOperationIdentityExpectation(
                                        durableDispatch.OperationId, conv.WorldId, conv.TaiwuId))
                                {
                                    shouldDispatch = false;
                                    result = "(动作准备后存档或太吾身份已经变化，本动作没有派发。)";
                                    CommitDurableToolDispatch(conv, snap, durableDispatch, result,
                                        ToolOutcome.Failed("operation_identity_changed_before_dispatch",
                                            durableDispatch.OperationId, null, "mutation was not dispatched"));
                                }
                                else if (shouldDispatch && groupGuard != null && groupGuard.PhysicalEndpoints.Count > 0
                                    && !OperationRpcClient.RegisterGroupPhysicalEnvelope(durableDispatch.OperationId,
                                        groupGuard.PhysicalActorId, snap.TaiwuId, groupGuard.PhysicalEndpoints))
                                {
                                    shouldDispatch = false;
                                    result = "(动作的权威现场复核信封无法建立，本动作没有派发。)";
                                    CommitDurableToolDispatch(conv, snap, durableDispatch, result,
                                        ToolOutcome.Failed("physical_envelope_failed", durableDispatch.OperationId,
                                            null, "mutation was not dispatched"));
                                }
                            }
                            if (shouldDispatch)
                            {
                                string stableOperationId = durableMutation ? durableDispatch.OperationId : null;
                                if (durableMutation)
                                    EffectHandler.ObserveOperationOutcome(stableOperationId, outcome => toolOutcome = outcome);
                                Debug.Log("[江湖有灵] 执行工具 " + ToolCallLogMetadata(call));
                                yield return ExecuteTool(call.Name, dispatchArgumentsJson, snap, conv, prof, ct,
                                    r => result = r, stableOperationId);
                                localToolSemantic = _lastLocalToolSemantic;
                                if (durableMutation && !EffectHandler.WasOperationDispatched(stableOperationId))
                                    OperationRpcClient.DiscardPreparedOperation(stableOperationId);
                                if (durableMutation && toolOutcome == null && EffectHandler.WasOperationDispatched(stableOperationId))
                                {
                                    float outcomeDeadline = Time.unscaledTime + RpcReceiptWaitSeconds;
                                    while (toolOutcome == null && Time.unscaledTime < outcomeDeadline && TurnToolDispatchAllowed(ct)) yield return null;
                                }
                                if (durableMutation && toolOutcome == null)
                                {
                                    // 未触及底层 RPC = 参数/目标/资格等本地前置失败；已触及但仍无回执 = UNKNOWN。
                                    toolOutcome = EffectHandler.WasOperationDispatched(stableOperationId)
                                        ? ToolOutcome.Unknown(stableOperationId, "副作用回执未能在恢复窗口内取得")
                                        : ToolOutcome.Failed("local_precondition", stableOperationId, null,
                                            string.IsNullOrWhiteSpace(result) ? "动作未通过本地前置校验" : result);
                                }
                                if (durableMutation) EffectHandler.ForgetOperationOutcomeObserver(stableOperationId);
                                if (durableMutation && !CommitDurableToolDispatch(conv, snap, durableDispatch, result, toolOutcome))
                                {
                                    result = "(JHYL_ACTION_UNCONFIRMED：动作已经派发，但终态日志未能可靠落盘；不得声称成功，也不得自动重试。请以游戏实情为准。)";
                                    toolOutcome = ToolOutcome.Unknown(stableOperationId, "终态日志未能可靠落盘");
                                }
                            }
                            else Debug.LogWarning("[江湖有灵] durable dispatch gate 拦截 name="
                                + SafeLogToken(call?.Name, 64) + " " + ToolResultLogMetadata(result, "gated"));
                            if (!TurnToolDispatchAllowed(ct))
                            {
                                Debug.LogWarning("[江湖有灵] 工具派发后本轮被取消，停止后续工具；已派发动作不推定成败 name=" + (call?.Name ?? "null"));
                                onError?.Invoke("已中断，已派发动作的回执未确认");
                                yield break;
                            }
                            if (GroupCtx == null && !ConversationStillCurrent(conv)) { onError?.Invoke("聊天记录已被清空，本轮作废"); yield break; }
                            string observedStatus = durableMutation
                                ? (toolOutcome?.Status ?? "unknown")
                                : (IsRetrievalTool(call?.Name) ? "retrieved"
                                    : (localToolSemantic == LocalToolSemantic.Failed ? "failed"
                                        : (localToolSemantic == LocalToolSemantic.Unconfirmed ? "unknown" : "completed")));
                            Debug.Log("[江湖有灵] 工具结果 name=" + SafeLogToken(call?.Name, 64)
                                + " " + ToolResultLogMetadata(result, observedStatus));
                            bool knownFailureForGuide = !optionalReaction && (durableMutation
                                ? toolOutcome != null && !toolOutcome.IsUnconfirmed && !toolOutcome.IsSucceeded
                                : !IsRetrievalTool(call.Name) && localToolSemantic == LocalToolSemantic.Failed);
                            string resultForModel = result ?? "(工具未返回结果,不要假称已完成)";
                            if (knownFailureForGuide
                                && TryLoadActionGuideForAction(call.Name,
                                    out string recoverySkillId, out string recoveryGuide)
                                && _loadedActionSkills.Add(recoverySkillId))
                            {
                                // 明确失败已经多占一轮恢复预算；直接随回执附上对应做法，免模型再花
                                // 一次网络轮读取技能。UNKNOWN 不走这里，避免诱导重发可能已落地的动作。
                                resultForModel += "\n\n" + recoveryGuide;
                                Debug.Log("[JHYL_ACTION_GUIDE_AUTO_RECOVERY] npc=" + snap.NpcId
                                    + " skill=" + recoverySkillId + " action=" + SafeLogToken(call.Name, 64));
                            }
                            msgs.Add(LlmMessage.Tool(call.Id, TruncateToolResult(resultForModel)));
                            if (HasThirdPartyOnlySecretDisclosure())
                            {
                                thirdPartySecretPrivacyClosure = true;
                                // query_npc_secrets 的权威回执与稳定预载都可能包含完整正文。
                                // 动作成功后立刻从所有可序列化字段清走，防止未来控制流改动时
                                // 把原文再次送进无状态请求，由模型转述或改写给太吾。
                                SecretDisclosureProjection.ScrubRequestMessagesInPlace(msgs,
                                    _thirdPartySecretsDisclosedThisTurn,
                                    _taiwuSecretsDisclosedThisTurn);
                            }
                            if (!optionalReaction && durableMutation)
                            {
                                toolOutcome = toolOutcome ?? OutcomeFromDispatch(durableDispatch);
                                // 父事务/现场/身份门在真正派发前拒绝时没有 operation 回执，但这是
                                // “确定未发生”而非 UNKNOWN。把它归为明确失败，Agent 才能据原因换方案。
                                if (toolOutcome == null && !shouldDispatch)
                                    toolOutcome = ToolOutcome.Failed("dispatch_gate_rejected",
                                        durableDispatch?.OperationId, null,
                                        string.IsNullOrWhiteSpace(result) ? "动作在派发前被拒绝" : result);
                                if (toolOutcome == null || toolOutcome.IsUnconfirmed)
                                { roundActionUnconfirmed = true; roundActionFailed = true; }
                                else if (!toolOutcome.IsSucceeded)
                                {
                                    roundActionFailed = true;
                                    if (string.IsNullOrWhiteSpace(lastDefiniteFailureReceipt))
                                        lastDefiniteFailureReceipt = result;
                                }
                            }
                            else if (!optionalReaction && !IsRetrievalTool(call.Name)
                                && localToolSemantic == LocalToolSemantic.Unconfirmed)
                            { roundActionUnconfirmed = true; roundActionFailed = true; }
                            else if (!optionalReaction && !IsRetrievalTool(call.Name)
                                && localToolSemantic == LocalToolSemantic.Failed)
                            {
                                roundActionFailed = true;
                                if (string.IsNullOrWhiteSpace(lastDefiniteFailureReceipt))
                                    lastDefiniteFailureReceipt = result;
                            }
                            bool definiteToolFailure = durableMutation
                                ? toolOutcome != null && !toolOutcome.IsUnconfirmed && !toolOutcome.IsSucceeded
                                : !IsRetrievalTool(call.Name) && localToolSemantic == LocalToolSemantic.Failed;
                            if (definiteToolFailure && groupClaimAcquired)
                            {
                                // 只释放“权威确定未发生”的占用。UNKNOWN 仍保留，防止后位成员
                                // 在前一副作用可能已落地时重复操作同一生命、关系或库存。
                                GroupCtx.ActionCoordinator.ReleaseResolved(GroupCtx.MemberId, groupGuard?.Footprint);
                                groupClaimAcquired = false;
                            }
                            string trajectoryOutcome = durableMutation
                                ? (toolOutcome?.Status ?? "unknown")
                                : (IsRetrievalTool(call.Name) ? "retrieved"
                                    : (localToolSemantic == LocalToolSemantic.Failed ? "failed"
                                        : (localToolSemantic == LocalToolSemantic.Unconfirmed ? "unknown" : "completed")));
                            LlmLog.RecordTrajectory(llmTag, traceRoot.WithRound(round), call?.Name,
                                trajectoryOutcome, durableDispatch?.OperationId,
                                durableMutation && toolOutcome != null && !toolOutcome.IsSucceeded
                                    ? toolOutcome.Code : null);
                            float minDwell = IsRetrievalTool(call.Name) ? 1.2f : ProgressMinDwell;
                            float dwell = Time.unscaledTime - shownAt;
                            if (dwell < minDwell) yield return new WaitForSecondsRealtime(minDwell - dwell);
                        }
                    }
                    else
                    {
                        // 多工具并发:Unity 协程在主线程协作式交错、后端域线程串行落地 → 安全无竞态;
                        // 把"逐个动作各停 ~3 秒"的串行等待塌缩为一并进行(如一次赠多样财物/传多门武学),大幅提速。
                        var host2 = TalkEntryHost.Instance;
                        int nTool = calls.Count;
                        var results = new string[nTool];
                        var doneFlags = new bool[nTool];
                        onProgress?.Invoke("一并操办中…");
                        float shownAt = Time.unscaledTime;
                        for (int i = 0; i < nTool; i++)
                        {
                            if (!TurnToolDispatchAllowed(ct)) { onError?.Invoke("已中断，后续工具未执行"); yield break; }
                            if (GroupCtx == null && !ConversationStillCurrent(conv)) { onError?.Invoke("聊天记录已被清空，本轮作废"); yield break; }
                            int idx = i; var call = calls[i];
                            Debug.Log("[江湖有灵] 并发执行工具 " + ToolCallLogMetadata(call));
                            if (host2 != null)
                                host2.StartCoroutine(ExecuteTool(call.Name, call.ArgumentsJson, snap, conv, prof, ct,
                                    r => { results[idx] = r; doneFlags[idx] = true; }));
                            else
                                yield return ExecuteTool(call.Name, call.ArgumentsJson, snap, conv, prof, ct,
                                    r => { results[idx] = r; doneFlags[idx] = true; });
                        }
                        float waitDl = Time.unscaledTime + RpcReceiptWaitSeconds;   // 覆盖 OperationRpcClient 的有界回执恢复窗；正常回包仍即时结束
                        bool allDone = false;
                        while (!allDone && Time.unscaledTime < waitDl)
                        {
                            if (!TurnToolDispatchAllowed(ct)) { onError?.Invoke("已中断，工具回执未确认"); yield break; }
                            allDone = true;
                            for (int i = 0; i < nTool; i++) if (!doneFlags[i]) { allDone = false; break; }
                            if (!allDone) yield return null;
                        }
                        if (!TurnToolDispatchAllowed(ct)) { onError?.Invoke("已中断，工具回执未确认"); yield break; }
                        // 结果按工具调用原顺序回填(保持 tool_call_id 配对)
                        for (int i = 0; i < nTool; i++)
                        {
                            var rr = results[i] ?? "(检索超时,未取到结果)";
                            Debug.Log("[江湖有灵] 并发工具结果 name=" + SafeLogToken(calls[i]?.Name, 64)
                                + " " + ToolResultLogMetadata(rr, results[i] == null ? "timeout" : "retrieved"));
                            msgs.Add(LlmMessage.Tool(calls[i].Id, TruncateToolResult(rr)));
                            LlmLog.RecordTrajectory(llmTag, traceRoot.WithRound(round), calls[i]?.Name,
                                results[i] == null ? "timeout" : "retrieved");
                        }
                        float dwell = Time.unscaledTime - shownAt;
                        if (dwell < ProgressMinDwell) yield return new WaitForSecondsRealtime(ProgressMinDwell - dwell);
                    }
                    if (requiresLanding && (_landedThisTurn == null || _landedThisTurn.Count <= landedBeforeTools) && !roundActionFailed)
                    {
                        // JHYL_REQUIRE_LANDING_CONFIRMATION
                        Debug.Log("[江湖有灵] 物品动作未见落地回执,拒绝直接收尾:" + ToolNamesForLog(calls));
                        msgs.Add(LlmMessage.System("(上一轮包含会转移物品/交易的动作工具,但没有任何落地回执;不能声称已经完成。请重新调用对应工具真实落地,或如实说明未完成。)"));
                        roundActionFailed = true;
                        roundActionUnconfirmed = true;
                    }
                    if (thirdPartySecretPrivacyClosure && !roundActionUnconfirmed)
                    {
                        // 当前模型轮是在第三方吐露成功之前生成的，已经见过秘闻原文，
                        // 因而它的任何正文（包括貌似不完整的改写）都不可信。以代码正文
                        // 收口并停止后续出网，既保留动作结果，也不给模型第二次转述机会。
                        reply = BuildThirdPartySecretPrivacyReply(snap);
                        replyStreamed = false;
                        break;
                    }
                    if (proseClosureOnly)
                    {
                        // Some compatible providers ignore tool_choice=none and emit the same
                        // invitation/action tool again. The tool calls above were deliberately
                        // answered as not executed; never give such a provider another network
                        // round or an invitation can appear to loop forever.
                        bool trustedClosure = !IsPlaceholderToolReply(tr.Content)
                            && IsTrustedLandedActionReply(tr.Content);
                        reply = trustedClosure ? tr.Content
                            : (terminalReceiptClosureOnly
                                && _landedThisTurn != null && _landedThisTurn.Count > 0
                                    ? DeterministicSucceededActionReply(_landedThisTurn)
                                    : "（" + (string.IsNullOrWhiteSpace(snap?.Name) ? "对方" : snap.Name)
                                        + (terminalReceiptClosureOnly
                                            ? "已把话说定，不再重复。"
                                            : "没有继续执行刚才的动作。") + "）");
                        replyStreamed = trustedClosure && streamedThisRound;
                        Debug.Log("[JHYL_PROSE_CLOSURE_HARD_STOP] repeated_tools="
                            + ToolNamesForLog(calls));
                        break;
                    }
                    if (roundActionFailed)
                    {
                        // UNKNOWN 仍是硬边界：既不能重试副作用，也不能让自由正文猜成败。
                        // 但“偷窃败露/物品不存在/条件不足”等确定失败已经有权威终态，必须把
                        // 失败原因回喂给角色继续说完；否则玩家只见工具提示、没有任何 NPC 正文。
                        if (stream && streamedThisRound) { onResetReply?.Invoke(); streamedThisRound = false; }
                        if (roundActionUnconfirmed)
                        {
                            onError?.Invoke("本轮游戏动作仍有权威回执尚未确认；正文未提交，也不会重试该动作。");
                            yield break;
                        }
                        pendingFailedActionProse = true;
                        proseClosureOnly = false;
                        terminalReceiptClosureOnly = false;
                        if (!failureRecoveryBudgetAdded)
                        {
                            roundLimit += FailureRecoveryRounds;
                            failureRecoveryBudgetAdded = true;
                        }
                        msgs.Add(LlmMessage.System(
                            "上一动作已有权威的明确失败结果。不得重复同名同参动作，不得改口声称成功；"
                            + "请根据具体失败原因继续思考：可以改用当前现场中可行的其它动作或参数，"
                            + "也可以直接以角色口吻据实回应。后续每个动作仍以工具回执为准。"));
                        // 同一 assistant 消息里的正文写在工具结果产生之前，不可能真正知道
                        // “偷窃败露”等失败原因；即使字面看似安全，也不能当作失败后的反应。
                        // 恢复相同现场的完整稳定工具表，让下一轮真正依据失败原因换方案。
                        nextToolChoice = "auto";
                        continue;
                    }
                    if (RequiresTerminalPostReceiptClosure(calls))
                    {
                        // 赴约、入队/关系邀约等承诺动作已经取得权威回执后，只需要让
                        // 模型读回执写一句收尾。下一轮强制无工具，避免模型把同一邀约
                        // 当作仍待执行而反复提交（不同参数也不得绕过去）。
                        proseClosureOnly = true;
                        terminalReceiptClosureOnly = true;
                        nextToolChoice = "none";
                        if (round >= roundLimit) roundLimit++;
                        msgs.Add(LlmMessage.System(
                            "上一项邀约、关系或承诺动作已经得到权威结果。现在只用角色口吻写一次自然收尾；"
                            + "不得再次调用任何工具，不得重新邀约、改地点、改关系或重复提交动作。"));
                        continue;
                    }
                    bool reactionOnlyBatch = calls.Count > 0;
                    foreach (var call in calls)
                        if (call == null || !string.Equals(call.Name, "record_reaction", StringComparison.Ordinal))
                        { reactionOnlyBatch = false; break; }
                    if (reactionOnlyBatch && IsPlaceholderToolReply(tr.Content))
                    {
                        // Tool-only providers commonly return “……” or no content beside the
                        // optional reaction call. Give exactly one prose-only round; success or
                        // failure of the reaction itself has no bearing on the visible answer.
                        if (placeholderProseRetries < 1)
                        {
                            placeholderProseRetries++;
                            proseClosureOnly = true;
                            if (round >= roundLimit) roundLimit++;
                            nextToolChoice = deepSeekFlashThinkingTools ? "auto" : "none";
                            continue;
                        }
                        reply = "（" + (string.IsNullOrWhiteSpace(snap?.Name) ? "对方" : snap.Name)
                            + "沉默片刻，没有作答。）";
                        replyStreamed = false;
                        break;
                    }
                    if (canFinish)
                    {
                        bool hasLandedAction = _landedThisTurn != null && _landedThisTurn.Count > 0;
                        bool keepModelProse = !hasLandedAction || IsTrustedLandedActionReply(tr.Content);
                        reply = keepModelProse
                            ? tr.Content
                            : DeterministicSucceededActionReply(_landedThisTurn);
                        // 工具轮正文为避免先展示未确认草稿而没有逐字推送；可信正文会在收尾时
                        // 原样交给 UI。只有空白或泄漏内部过程的不可信正文才改用确定性回执。
                        replyStreamed = keepModelProse && streamedThisRound;
                        break;
                    }
                    // 失败改口这一轮会推翻此前逐字显示的话 → 清屏重写
                    if (stream && streamedThisRound) { onResetReply?.Invoke(); streamedThisRound = false; }
                    continue;
                }

                reply = tr.Content;
                if (IsPlaceholderToolReply(reply))
                {
                    if (placeholderProseRetries < 1)
                    {
                        placeholderProseRetries++;
                        if (stream && streamedThisRound) { onResetReply?.Invoke(); streamedThisRound = false; }
                        proseClosureOnly = true;
                        if (round >= roundLimit) roundLimit++;
                        msgs.Add(LlmMessage.System("现在只输出一段有实际内容的角色正文；不要只写省略号或其它纯标点，也不要再调用工具。"));
                        nextToolChoice = "none";
                        continue;
                    }
                    reply = "（" + (string.IsNullOrWhiteSpace(snap?.Name) ? "对方" : snap.Name)
                        + "沉默片刻，没有作答。）";
                    streamedThisRound = false;
                }

                // 这轮没调工具、只回了话：正文直接作为终稿。真实游戏状态只会由正式
                // tool_call 与后端回执改变，不再根据正文关键词推断、重试或替换回复。
                if (_landedThisTurn != null && _landedThisTurn.Count > 0)
                {
                    // JHYL_LANDED_ACTION_PREFERS_MODEL_PROSE:动作已按权威回执落地,而这一收尾轮的正文
                    // (reply=tr.Content)是模型【拿着工具结果】写就的角色正文。此前无条件用
                    // DeterministicSucceededActionReply 顶掉它,导致"教功法/赠书成功却只回一句机械兜底"
                    // (实机:望霞八步 write_book 落地后玩家看到'我已照真实游戏结果办下…')。现改为:模型给了
                    // 可用正文(非空、且未泄漏 [JHYL_DONE]/tool_calls 等内部标签或伪成功话术)就照常显示;
                    // 只有正文缺失或泄漏内部协议时才退回确定性回执；这里只处理【确已落地】的情形。
                    if (IsTrustedLandedActionReply(reply))
                    {
                        replyStreamed = streamedThisRound;
                    }
                    else
                    {
                        if (stream && streamedThisRound) onResetReply?.Invoke();
                        reply = DeterministicSucceededActionReply(_landedThisTurn);
                        replyStreamed = false;
                    }
                }
                else replyStreamed = streamedThisRound;
                break;
            }
            string unsanitizedReply = reply;
            reply = TalkPromptBuilder.StripLeakedHistoricalContext(
                StripInternalExecutionTags(reply));
            if (replyStreamed && !string.Equals(unsanitizedReply, reply, StringComparison.Ordinal))
            {
                onResetReply?.Invoke();
                replyStreamed = false;
            }
            if (string.IsNullOrWhiteSpace(reply))
            {
                reply = SelectLoopExhaustionReply(reply, pendingFailedActionProse,
                    lastDefiniteFailureReceipt, _landedThisTurn, snap?.Name);
                replyStreamed = false;
            }
            if (!WorldLifecycle.IsSameWorld(_turnWorldGeneration)) { onError?.Invoke("已切换存档，本轮对话作废"); yield break; }
            reply = SecretDisclosureProjection.HideThirdPartyOnly(
                reply, _thirdPartySecretsDisclosedThisTurn, _taiwuSecretsDisclosedThisTurn,
                out bool thirdPartySecretHidden);
            reply = SecretDisclosureProjection.EnsureVisible(
                reply, _taiwuSecretsDisclosedThisTurn, out bool secretDisclosureAppended);
            if ((thirdPartySecretHidden || secretDisclosureAppended) && replyStreamed)
            {
                onResetReply?.Invoke();
                replyStreamed = false;
            }
            reply = ReadableProseFormatter.EnsureParagraphs(MarkdownTmp.Normalize(reply));
            // DeepSeek Pro 偶尔把详细/不限回复压成一个超长单段；在持久化前只补排版分段，
            // 保证当场显示、历史回放与导出看到的是同一份可读正文。

            if (GroupCtx == null && !ConversationStillCurrent(conv)) { onError?.Invoke("聊天记录已被清空，本轮作废"); yield break; }

            // 即使本轮只有失败/拒绝工具、没有新记忆，也要先建立 durable barrier；父群聊
            // 只有提交对应成员行后才可清掉 terminal PendingActionTurn。
            if (GroupCtx != null && !PrepareGroupCommitBarrier())
            {
                onError?.Invoke("群聊提交日志无法可靠落盘，本轮不再展示");
                yield break;
            }

            // 本轮真落地的动作(去重):① 随回话 turn 存进历史 → 每次都随聊天记录一并喂给模型(它据此记得自己做过,不靠模糊召回);
            // ② 同时写进长期记忆,供对话被压缩/远期之后仍记得。在写回话 turn 之前做,使记忆 id 挂进该轮 MemoryIds(按轮删除/重试连带抹)。
            List<string> didThisTurn = null;
            if (_landedThisTurn != null && _landedThisTurn.Count > 0)
            {
                didThisTurn = new List<string>(); foreach (var a in _landedThisTurn) if (!didThisTurn.Contains(a)) didThisTurn.Add(a);
                string did = string.Join("、", didThisTurn);
                bool actionMemoryBarrierFailed = false;
                if (_mem != null) try
                {
                    var me = new MemoryEntry { Content = "我方才与太吾相处时,亲手做了:" + did + "。", Type = MemoryType.Event, Keywords = "我做过的事," + did, Importance = 4, WorldDate = snap.CurrentDate };
                    if (GroupCtx != null && !string.IsNullOrWhiteSpace(GroupCtx.ExchangeId))
                    {
                        me.SourceKind = "group_turn_action";
                        me.SourceId = "group-turn:" + GroupCtx.GroupId + ":" + GroupCtx.ExchangeId
                            + ":attempt:" + GroupCtx.AttemptId + ":" + snap.NpcId + ":action";
                    }
                    var eff = _mem.AddOrMerge(me, out bool merged);
                    string newMemoryId = eff != null && (GroupCtx != null || !merged) ? eff.Id : null;
                    if (!string.IsNullOrEmpty(newMemoryId) && !_memIdsThisTurn.Contains(newMemoryId))
                        _memIdsThisTurn.Add(newMemoryId);
                    if (GroupCtx != null && !PrepareGroupCommitBarrier())
                    {
                        if (!merged && !string.IsNullOrEmpty(newMemoryId)) _mem.RemoveByIds(new[] { newMemoryId });
                        _memIdsThisTurn.Remove(newMemoryId);
                        actionMemoryBarrierFailed = true;
                    }
                    if (!actionMemoryBarrierFailed)
                    {
                        _mem.Prune(snap.CurrentDate);
                        if (_mem.Save())
                        {
                            // ID 已在 durable prepare 前加入，父 transcript 提交后再完成事务。
                        }
                        else
                        {
                            if (!merged && !string.IsNullOrEmpty(newMemoryId))
                            {
                                _mem.RemoveByIds(new[] { newMemoryId });
                                _memIdsThisTurn.Remove(newMemoryId);
                                PrepareGroupCommitBarrier();
                            }
                            Debug.LogWarning("[江湖有灵] 本轮动作记忆未能可靠保存 npc=" + snap.NpcId);
                        }
                    }
                }
                catch
                {
                    actionMemoryBarrierFailed = GroupCtx != null;
                    Debug.LogWarning("[江湖有灵] 本轮动作记忆写入异常 npc=" + snap.NpcId);
                }
                if (actionMemoryBarrierFailed)
                {
                    onError?.Invoke("群聊动作记忆提交日志无法可靠落盘，本轮不再展示");
                    yield break;
                }
                Debug.Log("[江湖有灵] 记下本轮所做:" + did + " npc=" + snap.NpcId);
            }
            LastTurnMemoryIds = _memIdsThisTurn.Count > 0 ? new List<string>(_memIdsThisTurn) : null;
            LastTurnActions = didThisTurn != null ? new List<string>(didThisTurn) : null;
            List<string> toolResultsThisTurn = NormalizeExecutionHistory(_toolResultsThisTurn);
            LastTurnToolResults = toolResultsThisTurn != null ? new List<string>(toolResultsThisTurn) : null;
            if (GroupCtx != null && !PrepareGroupCommitBarrier())
            {
                onError?.Invoke("群聊最终提交日志无法可靠落盘，本轮不再展示");
                yield break;
            }

            // 历史 + 存盘
            string authoritativeNpcName = NormalizeNpcName(snap.Name);
            if (authoritativeNpcName != null)
                conv.NpcName = authoritativeNpcName;   // 记下对方名,供灵儿"近来都和谁聊了什么"汇总
            // 群聊(GroupCtx!=null):本轮【不】写进该 NPC 的单聊会话——群聊记录另由 GroupChatOrchestrator 的群 transcript 持久化,
            // 不污染单聊窗口的显示。NPC 的长期记忆仍在上面照常写入,所以它依旧"记得"群里发生的事,只是不把群聊记录混进单聊。
            if (GroupCtx == null)
            {
                string exchangeId = Guid.NewGuid().ToString("N");
                // NPC 主动来信没有一条伪造的“太吾发言”；只保存 NPC 自己这一行。
                // RenderHistory 会把无玩家前项的回话自然识别为主动捎话轮。
                if (!npcInitiated)
                    conv.Turns.Add(new TalkTurn { Id = Guid.NewGuid().ToString("N"), ExchangeId = exchangeId,
                        FromPlayer = true, Text = playerInput, Date = snap.CurrentDate,
                        LocationText = LastPlayerLocationText, ContactMode = LastContactMode });
                conv.Turns.Add(new TalkTurn { Id = Guid.NewGuid().ToString("N"), ExchangeId = exchangeId, FromPlayer = false, Text = reply, Date = snap.CurrentDate,
                    LocationText = LastNpcLocationText, ContactMode = LastContactMode,
                    MemoryIds = _memIdsThisTurn.Count > 0 ? new List<string>(_memIdsThisTurn) : null, Actions = didThisTurn,
                    ToolResults = toolResultsThisTurn });   // 成功动作与可见执行结果分栏持久化；失败不得混入 Actions
                // Mark the parent commit in the same atomic conversation write. A crash
                // before this Save leaves the marker empty and an explicit retry may
                // safely reuse the prepared operations; after commit, identical text is
                // always a new player turn and must never inherit this journal.
                if (_activePendingActionTurn != null)
                    _activePendingActionTurn.CommittedExchangeId = exchangeId;
                EnsureTurnAcknowledgementsDurable(conv, snap.TaiwuId, snap.NpcId, _activePendingActionTurn);
                if (PendingTurnIsTerminal(_activePendingActionTurn)) conv.PendingActionTurns?.Remove(_activePendingActionTurn);
                if (!SaveConv(snap.TaiwuId, snap.NpcId, conv)) { onError?.Invoke("聊天记录已被清空或存盘失败，本轮不再展示"); yield break; }
                LastCommittedExchangeId = exchangeId;
                if (!npcInitiated)
                    PlayerTalkMarkStore.Mark(snap.TaiwuId, snap.NpcId, snap.CurrentDate);
            }
            // 群聊不能在父 transcript 提交前清 terminal 动作日志。父编排器提交成员行后调用
            // FinalizeRecoveredGroupCommit；若中途崩溃，group exchange journal 会在重启时补做。

            // 单聊的父记录已在上面 SaveConv 成功；群聊必须等 GroupChatOrchestrator 把
            // 对应成员行写入父 transcript 后，再由父编排器显式提交召回统计。
            if (GroupCtx == null) CommitRecallStatistics(snap.NpcId);

            // 效果已落地 → 打字机揭示正文(流式已逐字显示过则跳过,免重放)
            if (!replyStreamed && onDelta != null && reply.Length > 0)
            {
                int shown = 0;
                int step = Math.Max(3, reply.Length / 16);   // 按长度自适应,整段约 0.3s 内揭示完(此前 2 字/帧,长句要 1s+ 纯属白等)
                while (shown < reply.Length)
                {
                    if (GroupCtx == null && !ConversationStillCurrent(conv)) { onError?.Invoke("聊天记录已被清空，本轮作废"); yield break; }
                    int take = Math.Min(step, reply.Length - shown);
                    onDelta(reply.Substring(shown, take));
                    shown += take;
                    yield return null;
                }
            }
            if (GroupCtx == null && !ConversationStillCurrent(conv)) { onError?.Invoke("聊天记录已被清空，本轮作废"); yield break; }
            if (GroupCtx == null) FlushNotices();   // 单聊:正文已揭示→把「获得/落地」绿提示显示在其下方。群聊:留到该成员回话气泡显示后,由群聊编排器 flush(否则绿提示会跑到回话上方)
            onReply?.Invoke(reply);

            // 正文已经可靠存盘并完整交给界面后，才启动旧记录整理。后台任务不会阻塞本轮回复；
            // 群聊使用独立 transcript 与记忆提交协议，不走此处的单聊压缩。
            if (GroupCtx == null) ScheduleCompactionAfterCommit(conv, snap.Name, snap.CurrentDate, traceRoot);

            if (_favorDeltaThisTurn != 0 && TalkEntryHost.Instance != null)
                TalkEntryHost.Instance.StartCoroutine(TalkEntryInjector.AnimateFavorRefresh(snap.NpcId, snap.Favor));
        }

        /// <summary>
        /// 仅供已经成功提交父聊天记录的调用方使用。幂等；统计保存失败不撤销已提交正文。
        /// 群聊父编排器不得在 SaveTranscript 成功前调用。
        /// </summary>
        public bool CommitRecallStatistics(int npcId)
        {
            if (_recallResult == null || !_recallResult.Dirty) return true;
            bool saved = _recallResult.TryPersist(_mem);
            if (!saved) Debug.LogWarning("[江湖有灵] 记忆召回统计未能持久化 npc=" + npcId);
            return saved;
        }

        private static bool PendingTurnIsTerminal(PendingActionTurn turn)
        {
            if (turn == null) return false;
            if (turn.Operations == null || turn.Operations.Count == 0) return true;
            foreach (var op in turn.Operations)
                if (op != null && (OutcomeFromDispatch(op)?.IsTerminal != true
                    || !DispatchAcknowledgementDurable(op))) return false;
            return true;
        }

        /// <summary>
        /// 父群 transcript 已提交对应成员行后，才可清除此成员的 terminal 动作日志。
        /// group exchange journal 重启恢复也调用同一路径；目标不存在视为已幂等完成。
        /// </summary>
        public static bool FinalizeRecoveredGroupCommit(int taiwuId, int npcId,
            string groupId, string exchangeId, string attemptId, string playerInput, int worldDate)
        {
            if (JianghuYoulingPaths.CurrentWorldId == 0 || taiwuId <= 0 || npcId < 0) return false;
            lock (ConversationGate)
            {
                var conversation = GetConv(taiwuId, npcId);
                if (!conversation.LoadReliable) return false;
                var pending = FindPendingActionTurn(conversation,
                    GroupPendingTurnInputKey(groupId, exchangeId, attemptId, playerInput), worldDate);
                if (pending == null) return true;
                if (!EnsureTurnAcknowledgementsDurable(conversation, taiwuId, npcId, pending)) return false;
                if (!PendingTurnIsTerminal(pending)) return false;
                conversation.PendingActionTurns.Remove(pending);
                if (SaveConv(taiwuId, npcId, conversation)) return true;
                // SaveConv 已从磁盘恢复 canonical；保留 journal，稍后重试。
                return false;
            }
        }

        /// <summary>Read-only parent/child recovery probe. It never dispatches a mutation.</summary>
        public static string RecoveredGroupAttemptState(int taiwuId, int npcId, string groupId,
            string exchangeId, string attemptId, string playerInput, int worldDate,
            IEnumerable<string> expectedOperationIds)
        {
            if (taiwuId <= 0 || npcId < 0) return "unresolved";
            lock (ConversationGate)
            {
                Conversation conversation = GetConv(taiwuId, npcId);
                if (!conversation.LoadReliable) return "unresolved";
                PendingActionTurn pending = FindPendingActionTurn(conversation,
                    GroupPendingTurnInputKey(groupId, exchangeId, attemptId, playerInput), worldDate);
                if (pending == null) return "not_dispatched";
                var expected = new HashSet<string>(StringComparer.Ordinal);
                if (expectedOperationIds != null)
                    foreach (string id in expectedOperationIds)
                        if (OperationRpcClient.IsValidOperationId(id)) expected.Add(id);
                var actual = new HashSet<string>(StringComparer.Ordinal);
                if (pending.Operations != null)
                    foreach (PendingToolDispatch dispatch in pending.Operations)
                        if (dispatch != null && OperationRpcClient.IsValidOperationId(dispatch.OperationId))
                            actual.Add(dispatch.OperationId);
                // A crash may happen after the child pending turn durably appends the
                // next operation but before the parent barrier is rewritten with that
                // id. The exact group/exchange/attempt key makes this child turn the
                // authority for such extras; every id already recorded by the parent
                // must still be present.
                if (expected.Count > 0 && !expected.IsSubsetOf(actual)) return "unresolved";
                if (pending.Operations == null || pending.Operations.Count == 0) return "not_dispatched";
                foreach (PendingToolDispatch dispatch in pending.Operations)
                    if (dispatch != null && OutcomeFromDispatch(dispatch)?.IsTerminal != true) return "unresolved";
                return "terminal";
            }
        }

        public static void BeginRecoveredGroupAttemptReconciliation(int taiwuId, int npcId, string groupId,
            string exchangeId, string attemptId, string playerInput, int worldDate,
            IEnumerable<string> expectedOperationIds, Action completed)
        {
            string key = taiwuId + ":" + npcId + ":" + (groupId ?? "") + ":" + (exchangeId ?? "")
                + ":" + (attemptId ?? "");
            Conversation conversation;
            PendingActionTurn pendingTurn;
            var unresolved = new List<PendingToolDispatch>();
            lock (ConversationGate)
            {
                if (!RecoveredGroupReconciliations.Add(key)) return;
                conversation = GetConv(taiwuId, npcId);
                if (!conversation.LoadReliable)
                {
                    RecoveredGroupReconciliations.Remove(key);
                    return;
                }
                pendingTurn = FindPendingActionTurn(conversation,
                    GroupPendingTurnInputKey(groupId, exchangeId, attemptId, playerInput), worldDate);
                var expected = new HashSet<string>(StringComparer.Ordinal);
                if (expectedOperationIds != null)
                    foreach (string id in expectedOperationIds)
                        if (OperationRpcClient.IsValidOperationId(id)) expected.Add(id);
                var actual = new HashSet<string>(StringComparer.Ordinal);
                if (pendingTurn?.Operations != null)
                    foreach (PendingToolDispatch dispatch in pendingTurn.Operations)
                        if (dispatch != null && OperationRpcClient.IsValidOperationId(dispatch.OperationId))
                            actual.Add(dispatch.OperationId);
                if (expected.Count > 0 && !expected.IsSubsetOf(actual))
                {
                    RecoveredGroupReconciliations.Remove(key);
                    return;
                }
                if (pendingTurn?.Operations != null)
                    foreach (PendingToolDispatch dispatch in pendingTurn.Operations)
                        if (dispatch != null && OutcomeFromDispatch(dispatch)?.IsTerminal != true
                            && OperationRpcClient.IsValidOperationId(dispatch.OperationId)) unresolved.Add(dispatch);
                if (unresolved.Count == 0)
                {
                    RecoveredGroupReconciliations.Remove(key);
                    try { completed?.Invoke(); } catch { }
                    return;
                }
            }

            int remaining = unresolved.Count;
            foreach (PendingToolDispatch dispatch in unresolved)
            {
                Action<ToolOutcome> observe = outcome =>
                {
                    bool finish = false;
                    lock (ConversationGate)
                    {
                        if (ConversationStillCurrent(conversation))
                            MergeRecoveredOutcome(dispatch, outcome);
                        remaining--;
                        if (remaining == 0)
                        {
                            bool persisted = SaveConv(taiwuId, npcId, conversation);
                            RecoveredGroupReconciliations.Remove(key);
                            // Do not recursively re-query a backend pending/retryable-unknown
                            // receipt with no backoff. Keep the parent journal intact and let
                            // the next activation/recovery trigger probe again. Only terminal
                            // child evidence may advance the parent transaction.
                            finish = persisted && pendingTurn?.Operations != null;
                            if (finish)
                                foreach (PendingToolDispatch item in pendingTurn.Operations)
                                    if (item != null && OutcomeFromDispatch(item)?.IsTerminal != true)
                                    {
                                        finish = false;
                                        break;
                                    }
                        }
                    }
                    if (finish) try { completed?.Invoke(); } catch { }
                };
                try
                {
                    EffectHandler.QueryOperation(conversation.WorldId, conversation.TaiwuId,
                        dispatch.OperationId, observe);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[江湖有灵] 群聊子事务回执查询失败 op=" + dispatch.OperationId
                        + ":" + ex.GetType().Name);
                    observe(null);
                }
            }
        }

        /// <summary>"代笔":据与该 NPC 的聊天记录,替太吾拟一句接下来要说的话(单聊)。不发起任何动作,纯文本建议。</summary>
        public static IEnumerator SuggestPlayerLine(int taiwuId, int npcId, string npcName,
            Action<string> onResult, Action<string> onError,
            System.Threading.CancellationToken cancellationToken = default,
            int expectedWorldGeneration = -1)
        {
            if (expectedWorldGeneration >= 0 && !WorldLifecycle.IsSameWorld(expectedWorldGeneration)) yield break;
            if (cancellationToken.IsCancellationRequested) yield break;
            var client = LlmService.GetBackgroundClient();   // 代笔=纯文本建议、不调任何工具 → 走后台模型(留空=同主模型),更快
            if (client == null) { onError?.Invoke("未配置接口"); yield break; }
            if (taiwuId <= 0 || npcId < 0) { onError?.Invoke("无效对象"); yield break; }
            var conv = GetConv(taiwuId, npcId);
            string name = string.IsNullOrEmpty(npcName) ? "对方" : npcName;
            var sb = new StringBuilder();
            sb.Append("你在替玩家『太吾』构思下一句要对『").Append(name).Append("』说的话。\n");
            if (conv.Turns.Count > 0)
            {
                sb.Append("你们近来的对话:\n");
                int start = Math.Max(0, conv.Turns.Count - 10);
                for (int i = start; i < conv.Turns.Count; i++)
                {
                    var t = conv.Turns[i];
                    if (t == null || string.IsNullOrEmpty(t.Text)) continue;
                    sb.Append(TalkTurnKinds.ContextSpeaker(t, name)).Append(':').Append(t.Text).Append('\n');
                }
            }
            else sb.Append("(还没开口,这会是开场白)\n");
            int ghostwriteLength = GhostwriteLengthStore.Load();
            int ghostwriteMaxChars = GhostwriteLengthStore.MaxChars(ghostwriteLength);
            var localSpeech = new List<string>();
            AppendRecentPlayerSpeech(conv.Turns, localSpeech, 12);
            GhostwriteImitationProfile learning = GhostwriteLearningContext.Build(taiwuId, localSpeech);
            sb.Append("\n替太吾拟一段此刻自然会说的话:贴合方才语境与对方为人,像真江湖客口吻,能自然推进交谈。")
                .Append(GhostwriteLengthStore.PromptDirective(ghostwriteLength))
                .Append("。只输出这段发言本身,不要引号、不要旁白、不要解释、不要写「太吾:」。**快速直觉给出即可,无需长篇推敲。**");
            if (learning != null) sb.Append(learning.PromptDirective(ghostwriteMaxChars));
            if (!string.IsNullOrWhiteSpace(TaiwuVoice)) sb.Append("\n太吾平日说话的口吻:").Append(TaiwuVoice.Trim()).Append("。务必照此口吻遣词。");
            string system = "你是玩家的对话参谋,只产出玩家这一句要说的话;不必长篇思考,直觉拟出即可。";
            if (learning?.SampleCount > 0)
                system += "\n" + GhostwriteImitationProfileBuilder.HistoricalDataBoundary;
            var msgs = new List<LlmMessage> { LlmMessage.System(system) };
            GhostwriteLearningContext.AddHistoricalSamples(msgs, learning);
            msgs.Add(new LlmMessage("user", sb.ToString()));
            // thinking-only 兼容模型需要先完成推理，但一句代笔不应继承主对话的
            // 32768/服务端无限输出；8K 同时给推理留余量并约束异常生成。
            var task = client.SendAsync(msgs, 8192, 0.8, cancellationToken, 45, false, "代笔",
                LlmReasoningPolicy.Off);
            yield return new WaitUntil(() => task.IsCompleted || cancellationToken.IsCancellationRequested
                || (expectedWorldGeneration >= 0 && !WorldLifecycle.IsSameWorld(expectedWorldGeneration)));
            if (cancellationToken.IsCancellationRequested
                || (expectedWorldGeneration >= 0 && !WorldLifecycle.IsSameWorld(expectedWorldGeneration))) yield break;
            LlmResult r = null;
            try { r = task.Result; } catch (Exception e) { Debug.Log("[江湖有灵] 代笔异常:" + e.GetType().Name); onError?.Invoke(e.GetType().Name); yield break; }
            if (r == null || !r.Ok || string.IsNullOrWhiteSpace(r.Content))
            {
                Debug.Log("[江湖有灵] 代笔失败:ok=" + (r != null && r.Ok)
                    + " error_chars=" + (r?.Error?.Length ?? 0)
                    + " retry=" + SafeLogToken(r?.RetryClass, 64)
                    + " contentLen=" + (r?.Content?.Length ?? -1)
                    + " reasoningLen=" + (r?.Reasoning?.Length ?? -1));
                onError?.Invoke(r != null && r.Ok ? "模型只思考未出话(可调大模型输出上限或换非推理模型)" : (r?.Error ?? "生成失败")); yield break;
            }
            onResult?.Invoke(CleanSuggestion(r.Content, ghostwriteMaxChars));
        }

        /// <summary>把一句 NPC 的话(如过月主动捎来的)追加进与太吾的对话历史(作为 NPC 一方的发言),持久化。</summary>
        public static void AppendNpcLine(int taiwuId, int npcId, string text, int date)
            => AppendNpcLineIfCurrent(taiwuId, npcId, text, date, -1, -1);

        /// <summary>仅在指定世界 generation 仍有效时追加，阻止 DontDestroy 协程跨存档写入。</summary>
        public static bool AppendNpcLineIfCurrent(int taiwuId, int npcId, string text, int date, int expectedGeneration)
            => AppendNpcLineIfCurrent(taiwuId, npcId, text, date, expectedGeneration, -1);

        /// <summary>
        /// 同时校验同世界内的会话清空代次。月度任务在开始时捕获 token；用户随后清空后，
        /// 旧任务即使仍在运行也不得把旧结果追加回新会话。
        /// </summary>
        public static bool AppendNpcLineIfCurrent(int taiwuId, int npcId, string text, int date,
            int expectedGeneration, long expectedClearEpoch, string npcName = null)
            => AppendNpcLineIfCurrent(taiwuId, npcId, text, date, expectedGeneration, expectedClearEpoch,
                null, null, npcName);

        /// <summary>
        /// 追加外部生成的 NPC 轮次，并把“已确认成功动作”与“完整工具结果”分别持久化。
        /// 供过月同道投影使用；两类记录分开是为了让后续 Agent 不把失败当作既成事实。
        /// </summary>
        public static bool AppendNpcLineIfCurrent(int taiwuId, int npcId, string text, int date,
            int expectedGeneration, long expectedClearEpoch, IList<string> actions,
            IList<string> toolResults, string npcName = null)
            => AppendNpcLineWithStableIdIfCurrent(taiwuId, npcId, text, date,
                expectedGeneration, expectedClearEpoch, actions, toolResults, null, npcName);

        /// <summary>
        /// 追加带稳定行号的外部权威结果。稳定行号让调用方可以在瞬时磁盘失败后安全重试，
        /// 已经成功落盘的同一结果只会被识别为完成，不会重复形成第二条聊天记录。
        /// </summary>
        internal static bool AppendNpcLineWithStableIdIfCurrent(int taiwuId, int npcId, string text, int date,
            int expectedGeneration, long expectedClearEpoch, IList<string> actions,
            IList<string> toolResults, string stableTurnId, string npcName = null)
        {
            lock (ConversationGate)
            {
                if (taiwuId <= 0 || npcId < 0 || string.IsNullOrWhiteSpace(text)) return false;
                if (expectedGeneration >= 0 && !WorldLifecycle.IsSameWorld(expectedGeneration)) return false;
                if (expectedClearEpoch >= 0 && CaptureConversationClearEpoch(taiwuId, npcId) != expectedClearEpoch) return false;
                var conv = GetConv(taiwuId, npcId);
                if (expectedGeneration >= 0 && !WorldLifecycle.IsSameWorld(expectedGeneration)) return false;
                if (expectedClearEpoch >= 0
                    && (CaptureConversationClearEpoch(taiwuId, npcId) != expectedClearEpoch || conv.ClearEpoch != expectedClearEpoch)) return false;

                string normalizedName = NormalizeNpcName(npcName);
                bool nameChanged = normalizedName != null
                    && IsUnresolvedNpcName(conv.NpcName);
                if (nameChanged) conv.NpcName = normalizedName;

                string stable = string.IsNullOrWhiteSpace(stableTurnId) ? null : stableTurnId.Trim();
                if (stable != null)
                    foreach (TalkTurn existing in conv.Turns)
                        if (existing != null && string.Equals(existing.Id, stable, StringComparison.Ordinal))
                            return !nameChanged || SaveConv(taiwuId, npcId, conv);

                string turnId = stable ?? Guid.NewGuid().ToString("N");
                conv.Turns.Add(new TalkTurn
                {
                    Id = turnId,
                    ExchangeId = stable ?? Guid.NewGuid().ToString("N"),
                    FromPlayer = false,
                    Text = text.Trim(),
                    Date = date,
                    Actions = NormalizeExecutionHistory(actions),
                    ToolResults = NormalizeExecutionHistory(toolResults),
                });
                if (expectedClearEpoch >= 0 && CaptureConversationClearEpoch(taiwuId, npcId) != expectedClearEpoch) return false;
                return SaveConv(taiwuId, npcId, conv);
            }
        }

        /// <summary>
        /// 给已经存在的旧会话补写真名。只修现有内存/磁盘记录，不会因为浏览群聊成员而
        /// 为尚未单聊的人创建空 Chat 文件。
        /// </summary>
        public static bool TryUpdateExistingConversationNameIfCurrent(int taiwuId, int npcId,
            string npcName, int expectedGeneration)
        {
            string normalizedName = NormalizeNpcName(npcName);
            if (normalizedName == null) return true;
            lock (ConversationGate)
            {
                if (taiwuId <= 0 || npcId < 0) return false;
                if (expectedGeneration >= 0 && !WorldLifecycle.IsSameWorld(expectedGeneration)) return false;
                string key = ConversationKey(taiwuId, npcId);
                if (!_conversations.ContainsKey(key) && !File.Exists(ConvPath(taiwuId, npcId))) return true;
                Conversation conv = GetConv(taiwuId, npcId);
                if (expectedGeneration >= 0 && !WorldLifecycle.IsSameWorld(expectedGeneration)) return false;
                if (string.Equals(NormalizeNpcName(conv.NpcName), normalizedName, StringComparison.Ordinal))
                    return true;
                // 月度 journal 或旧群聊名单里的姓名可能早于玩家改名；这里只补缺失值，
                // 已有真名由下一次游戏权威快照正常刷新，不能被历史记录倒灌覆盖。
                if (!IsUnresolvedNpcName(conv.NpcName)) return true;
                conv.NpcName = normalizedName;
                return SaveConv(taiwuId, npcId, conv);
            }
        }

        internal static bool RemoveExactNpcLineIfCurrent(int taiwuId, int npcId, string text, int date)
        {
            lock (ConversationGate)
            {
                if (taiwuId <= 0 || npcId < 0 || string.IsNullOrWhiteSpace(text)) return false;
                var conv = GetConv(taiwuId, npcId);
                string expected = text.Trim();
                for (int i = conv.Turns.Count - 1; i >= 0; i--)
                {
                    TalkTurn turn = conv.Turns[i];
                    if (turn == null || turn.FromPlayer || TalkTurnKinds.IsNative(turn) || turn.Date != date
                        || !string.Equals((turn.Text ?? "").Trim(), expected, StringComparison.Ordinal)) continue;
                    conv.Turns.RemoveAt(i);
                    return SaveConv(taiwuId, npcId, conv);
                }
                return true;
            }
        }

        private static List<string> NormalizeExecutionHistory(IEnumerable<string> source)
        {
            if (source == null) return null;
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in source)
            {
                string value = (raw ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (value.Length == 0 || !seen.Add(value)) continue;
                if (value.Length > 2048) value = value.Substring(0, 2048) + "…";
                result.Add(value);
                if (result.Count >= 64) break;
            }
            return result.Count == 0 ? null : result;
        }

        /// <summary>清洗"代笔"结果:剥引号、剥误加的"太吾:"前缀，并按当前代笔篇幅限制为一条消息。</summary>
        public static string CleanSuggestion(string s)
            => CleanSuggestion(s, GhostwriteLengthStore.MaxChars(GhostwriteLengthStore.Load()));

        public static string CleanSuggestion(string s, int maxChars)
        {
            s = (s ?? "").Trim();
            if (s.StartsWith("太吾:")) s = s.Substring(3);
            else if (s.StartsWith("太吾：")) s = s.Substring(3);
            s = s.Trim().Trim('「', '」', '“', '”', '"', '\'', '『', '』').Trim();
            int lineBreak = s.IndexOfAny(new[] { '\r', '\n' });
            if (lineBreak >= 0) s = s.Substring(0, lineBreak).Trim();
            maxChars = Math.Max(1, Math.Min(4096, maxChars));
            if (s.Length > maxChars)
            {
                int end = -1;
                for (int i = maxChars - 1; i >= maxChars / 2; i--)
                    if ("。！？!?；;".IndexOf(s[i]) >= 0) { end = i + 1; break; }
                if (end > 0)
                {
                    s = s.Substring(0, end).TrimEnd();
                }
                else
                {
                    int contentChars = Math.Max(0, maxChars - 1);
                    if (contentChars > 0 && contentChars < s.Length
                        && char.IsHighSurrogate(s[contentChars - 1]))
                        contentChars--;
                    s = s.Substring(0, contentChars).TrimEnd() + "…";
                }
            }
            return s;
        }

        // 主线程抽干流式增量队列,喂 UI;返回本次是否有"正文"增量(用于判定终稿是否已逐字显示)。
        private static bool DrainStream(System.Collections.Concurrent.ConcurrentQueue<KeyValuePair<int, string>> q,
            Action<string> onDelta, Action<string> onThinking, Action<string> onProgress)
        {
            bool anyContent = false;
            KeyValuePair<int, string> it;
            while (q.TryDequeue(out it))
            {
                if (it.Key == 0) { anyContent = true; onDelta?.Invoke(it.Value); }
                else if (it.Key == 1) onThinking?.Invoke(it.Value);
                else if (it.Key == 2) onProgress?.Invoke(ProgressLabel(it.Value));
            }
            return anyContent;
        }

        private static string ShortLog(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = SecretRedactor.Redact(s).Replace("\r", "\\r").Replace("\n", "\\n");
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        private static string ToolCallLogMetadata(LlmToolCall call)
        {
            string args = call?.ArgumentsJson ?? "";
            var keys = new List<string>();
            JObject parsed = null;
            try { if (args.Length <= 1024 * 1024) parsed = JObject.Parse(args); } catch { parsed = null; }
            if (parsed != null)
            {
                foreach (JProperty property in parsed.Properties())
                {
                    keys.Add(SafeLogToken(property.Name, 48));
                    if (keys.Count >= 24) break;
                }
            }
            int bytes;
            try { bytes = Encoding.UTF8.GetByteCount(args); }
            catch { bytes = -1; }
            return "name=" + SafeLogToken(call?.Name, 64)
                + " id_len=" + (call?.Id?.Length ?? 0)
                + " arg_bytes=" + bytes
                + " arg_keys=" + (keys.Count == 0 ? "(none_or_invalid)" : string.Join(",", keys.ToArray()));
        }

        private static string ToolResultLogMetadata(string result, string status)
        {
            return "status=" + SafeLogToken(status, 24) + " chars=" + (result?.Length ?? 0);
        }

        private static string ReplyLogMetadata(string reply)
            => "reply_chars=" + (reply?.Length ?? 0);

        private static string LlmErrorLogMetadata(LlmToolResult result)
            => "ok=" + (result != null && result.Ok)
                + " error_chars=" + (result?.Error?.Length ?? 0)
                + " retry=" + SafeLogToken(result?.RetryClass, 64);

        private static string SafeLogToken(string value, int max)
        {
            if (string.IsNullOrWhiteSpace(value)) return "null";
            value = SecretRedactor.Redact(value).Trim();
            var safe = new StringBuilder(Math.Min(value.Length, Math.Max(1, max)));
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') safe.Append(c);
                else safe.Append('_');
                if (safe.Length >= max) break;
            }
            return safe.Length == 0 ? "invalid" : safe.ToString();
        }

        private static string ToolNamesForLog(System.Collections.Generic.IList<LlmToolCall> calls)
        {
            if (calls == null || calls.Count == 0) return "(none)";
            var sb = new StringBuilder();
            foreach (var c in calls)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(c == null ? "null" : (c.Name ?? "null"));
            }
            return sb.ToString();
        }

        // 检索类工具:结果模型要读后再答 → 必须再问一轮。其余动作/记录类工具的结果模型无需再读。
        private static bool IsRetrievalTool(string name)
            => name == "recall_memory"
                || name == ConversationSkillCatalog.ConsultToolName
                || (name != null && name.StartsWith("query_", StringComparison.Ordinal));

        private static bool HasTool(IList<ToolDef> tools, string name)
        {
            if (tools == null || string.IsNullOrWhiteSpace(name)) return false;
            foreach (ToolDef tool in tools)
                if (tool != null && string.Equals(tool.Name, name, StringComparison.Ordinal)) return true;
            return false;
        }

        // 只有真正进入后端 operation ledger 的副作用才写 durable dispatch。start_combat 只是等待
        // 玩家 UI 确认的提案；remember 是本地暂存记忆。
        private static bool IsBackendDurableMutationTool(string name)
        {
            switch (name)
            {
                case "record_reaction": case "gift": case "barter": case "steal":
                case "teach": case "write_book": case "set_relation": case "spend_night":
                case "dissolve_relation": case "matchmake": case "relate_npc": case "set_enmity":
                case "kill": case "capture": case "poison": case "heal": case "detox":
                case "regulate_breath": case "tell_secret":
                case "taiwu_tell_secret": case "trade": case "sect_support":
                case "change_equipment": case "flip_practice":
                case "train_skill": case "read_book": case "use_item":
                case "adjust_mood": case "adjust_fame":
                case "adjust_third_party_favor": case "add_feature": case "goto_place":
                case "change_caravan_favor": case "taiwu_give_item": case "taiwu_teach":
                case "taiwu_write_book":
                    return true;
                default:
                    return false;
            }
        }

        // 第三方解析失败 → 喂给模型的【准确】措辞:归因到「识别/身份」而非编造游戏条件,索要完整真名,明令勿假称成功。
        // reason 来自后端 ResolveChar:ambiguous(近旁同名数人)/ resolve_error(读关系异常,不得断言不认识)/ 其它=not_found。
        // hostile=true 用「动手」措辞(杀/绑/下毒/结仇),false 用「结缘/相与」措辞(缔结关系)。
        private static string ResolveFailMsg(string tgt, string reason, bool hostile)
        {
            string verb = hostile ? "对其动手" : "与之相与";
            if (reason == "ambiguous")
                return "(你身边叫「" + tgt + "」的不止一人,认不准你说的是哪位——请原样使用 query_current_block 名单中的【#人物编号】唯一定位;在分辨清楚前不可贸然行事,别假称已" + (hostile ? "动手" : "做") + "。)";
            if (reason == "resolve_error")
                return "(一时想不起「" + tgt + "」是谁,没能确认——可能名字不准,也可能确不相识。可先调 query_current_block 看此刻你和太吾身边都有谁、照其【完整真名或 #人物编号】重填重试,别假称已" + verb + "。)";
            return "(没能认出「" + tgt + "」是谁——你既不认得叫这名字的人,此刻你和太吾身边也没有这么一位。可先调 query_current_block 看身边都有谁、用确切【完整真名或 #人物编号】重试;实在认不出就如实把「不识此人」告诉太吾、就此作罢,切莫假称已" + verb + "。)";
        }

        private static bool IsTaiwuToken(string t)
        {
            t = (t ?? "").Trim();
            return t == "太吾" || t == "太吾传人" || t == "太吾本人"
                || t == "你" || t == "你自己" || t == "阁下" || t == "少侠" || t == "玩家"
                || t.Equals("taiwu", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSelfToken(string t)
        {
            t = (t ?? "").Trim();
            return t == "我" || t == "自己" || t == "我自己" || t == "本人" || t == "在下" || t == "此身" || t == "本座" || t == "本姑娘" || t == "老夫" || t == "贫道" || t == "贫僧";
        }

        private IEnumerator ResolveToolPerson(int speakerId, int taiwuId, string raw, int defaultId, string defaultName, string speakerName, bool allowBlock, Action<int, string, string> done)
        {
            string t = (raw ?? "").Trim();
            if (t.Length == 0)
            {
                done?.Invoke(defaultId, defaultName, null);
                yield break;
            }
            if (IsTaiwuToken(t))
            {
                done?.Invoke(taiwuId, "太吾", null);
                yield break;
            }
            if (IsSelfToken(t))
            {
                done?.Invoke(speakerId, "你", null);
                yield break;
            }
            if (ToolCallHeuristics.IsCurrentSpeakerName(t, speakerName))
            {
                // JHYL_CURRENT_SPEAKER_NAME_ALIAS: model may fill the current NPC's full name instead of "我".
                done?.Invoke(speakerId, string.IsNullOrWhiteSpace(speakerName) ? "你" : speakerName.Trim(), null);
                yield break;
            }

            int[] rid = { int.MinValue }; string[] reason = { null };
            EffectHandler.ResolveChar(speakerId, t, allowBlock, (c, r) => { rid[0] = c; reason[0] = r; });
            float dl = Time.unscaledTime + RpcReceiptWaitSeconds;
            while (rid[0] == int.MinValue && Time.unscaledTime < dl) yield return null;
            if (rid[0] == int.MinValue) done?.Invoke(0, t, "resolve_error");
            else done?.Invoke(rid[0], t, reason[0]);
        }

        private sealed class GroupDispatchGuard
        {
            public bool Allowed;
            public string Error;
            public int PhysicalActorId;
            public int CanonicalSecretRecipientId;
            public GroupActionFootprint Footprint = new GroupActionFootprint();
            public List<int> PhysicalEndpoints = new List<int>();
        }

        /// <summary>
        /// Resolve model strings to canonical game character ids before a group mutation.
        /// A group invitation is only a messaging capability: physical endpoints must also
        /// be in the current authoritative same-block or companion set.  Companion identity
        /// is intentionally treated as travelling with Taiwu, per the game's 同道 semantics.
        /// </summary>
        private IEnumerator BuildGroupDispatchGuard(string toolName, string argumentsJson, NpcSnapshot snap,
            System.Threading.CancellationToken ct, Action<GroupDispatchGuard> done)
        {
            var guard = new GroupDispatchGuard { Allowed = true };
            if (IsRetrievalTool(toolName) || toolName == "remember")
            { done?.Invoke(guard); yield break; }
            bool groupMode = GroupCtx != null;
            if (snap == null || (groupMode && (GroupCtx.ParticipantIds == null || GroupCtx.MemberId <= 0)))
            { guard.Allowed = false; guard.Error = "动作现场上下文缺失"; done?.Invoke(guard); yield break; }

            JObject args;
            try { args = string.IsNullOrWhiteSpace(argumentsJson) ? new JObject() : JObject.Parse(argumentsJson); }
            catch
            { guard.Allowed = false; guard.Error = "工具参数不是有效 JSON"; done?.Invoke(guard); yield break; }
            Func<string, string> value = key => args[key]?.ToString();
            var physicalEndpoints = new List<int>();
            Action<int> physical = id => { if (id > 0 && !physicalEndpoints.Contains(id)) physicalEndpoints.Add(id); };
            int npc = snap.NpcId, taiwu = snap.TaiwuId;
            guard.PhysicalActorId = npc;
            int first = 0, second = 0;
            string firstReason = null, secondReason = null;

            switch (toolName ?? string.Empty)
            {
                case "record_reaction":
                    guard.Footprint.AddRelation(npc, taiwu);
                    break;
                case "adjust_mood":
                case "adjust_fame":
                    yield return ResolveToolPerson(npc, taiwu, value("target"), 0,
                        value("target"), snap.Name, true,
                        (id, label, reason) => { first = id; firstReason = reason; });
                    if (first <= 0)
                    {
                        guard.Allowed = false;
                        guard.Error = "无法权威解析心情或名望变化对象";
                        break;
                    }
                    guard.Footprint.AddCharacterState(first,
                        toolName == "adjust_mood" ? "mood" : "fame");
                    break;
                case "gift":
                    yield return ResolveToolPerson(npc, taiwu, value("target"), taiwu, "太吾", snap.Name, true,
                        (id, label, reason) => { first = id; firstReason = reason; });
                    if (first <= 0) { guard.Allowed = false; guard.Error = ResolveFailMsg(value("target"), firstReason, false); break; }
                    physical(npc); physical(first);
                    guard.Footprint.AddInventory(npc, value("name")).AddInventory(first, value("name"));
                    break;
                case "barter":
                    yield return ResolveToolPerson(npc, taiwu, value("person_a"), npc, "你", snap.Name, true,
                        (id, label, reason) => { first = id; firstReason = reason; });
                    yield return ResolveToolPerson(npc, taiwu, value("person_b"), taiwu, "太吾", snap.Name, true,
                        (id, label, reason) => { second = id; secondReason = reason; });
                    if (first <= 0 || second <= 0) { guard.Allowed = false; guard.Error = "无法权威解析交换双方"; break; }
                    guard.PhysicalActorId = first;
                    physical(first); physical(second);
                    guard.Footprint.AddInventory(first, value("item_a")).AddInventory(second, value("item_b"));
                    break;
                case "steal":
                    yield return ResolveToolPerson(npc, taiwu, value("thief"), npc, "你", snap.Name, true,
                        (id, label, reason) => { first = id; firstReason = reason; });
                    yield return ResolveToolPerson(npc, taiwu, value("victim"), taiwu, "太吾", snap.Name, true,
                        (id, label, reason) => { second = id; secondReason = reason; });
                    if (first <= 0 || second <= 0) { guard.Allowed = false; guard.Error = "无法权威解析偷窃双方"; break; }
                    guard.PhysicalActorId = first;
                    physical(first); physical(second);
                    guard.Footprint.AddInventory(first, value("item")).AddInventory(second, value("item"));
                    break;
                case "teach":
                case "write_book":
                    yield return ResolveToolPerson(npc, taiwu, value("target"), toolName == "write_book" ? taiwu : 0,
                        toolName == "write_book" ? "太吾" : value("target"), snap.Name, true,
                        (id, label, reason) => { first = id; firstReason = reason; });
                    if (first <= 0) { guard.Allowed = false; guard.Error = "无法权威解析传授对象"; break; }
                    physical(npc); physical(first);
                    if (toolName == "write_book") guard.Footprint.AddInventory(first, value("name"));
                    else guard.Footprint.AddCharacterState(first, "skill:" + value("name"));
                    break;
                case "set_relation":
                    // Social relationship decisions are intentionally available to the
                    // remote-dialogue surface; they are durable but not physical transfers.
                    guard.Footprint.AddRelation(npc, taiwu); break;
                case "spend_night":
                    physical(npc); physical(taiwu); guard.Footprint.AddRelation(npc, taiwu); break;
                case "dissolve_relation":
                case "relate_npc":
                case "set_enmity":
                    yield return ResolveToolPerson(npc, taiwu, value("target"), toolName == "dissolve_relation" ? taiwu : 0,
                        toolName == "dissolve_relation" ? "太吾" : value("target"), snap.Name, true,
                        (id, label, reason) => { first = id; firstReason = reason; });
                    if (first <= 0) { guard.Allowed = false; guard.Error = "无法权威解析关系对象"; break; }
                    guard.Footprint.AddRelation(npc, first); break;
                case "matchmake":
                    yield return ResolveToolPerson(npc, taiwu, value("partner"), 0, value("partner"), snap.Name, true,
                        (id, label, reason) => { first = id; firstReason = reason; });
                    if (first <= 0) { guard.Allowed = false; guard.Error = "无法权威解析做媒对象"; break; }
                    guard.Footprint.AddRelation(npc, first); break;
                case "kill":
                case "capture":
                case "poison":
                {
                    yield return ResolveToolPerson(npc, taiwu, value("target"), 0,
                        value("target"), snap.Name, true,
                        (id, label, reason) => { first = id; firstReason = reason; });
                    if (first <= 0) { guard.Allowed = false; guard.Error = "无法权威解析动作对象"; break; }
                    bool targetIsTaiwu = first == taiwu;
                    if (!HighRiskActionAuthorization.IsAuthorized(toolName, targetIsTaiwu,
                        out string authorizationError))
                    {
                        guard.Allowed = false;
                        guard.Error = authorizationError;
                        break;
                    }
                    physical(npc); physical(first); guard.Footprint.AddLife(first); break;
                }
                case "heal":
                case "detox":
                case "regulate_breath":
                {
                    yield return ResolveToolPerson(npc, taiwu, value("target"), taiwu,
                        "太吾", snap.Name, true,
                        (id, label, reason) => { first = id; firstReason = reason; });
                    if (first <= 0) { guard.Allowed = false; guard.Error = "无法权威解析动作对象"; break; }
                    physical(npc); physical(first); guard.Footprint.AddLife(first); break;
                }
                case "trade":
                    physical(npc); physical(taiwu);
                    guard.Footprint.AddInventory(npc, value("item")).AddInventory(taiwu, value("item"));
                    break;
                case "taiwu_give_item":
                case "taiwu_write_book":
                    physical(npc); physical(taiwu);
                    guard.Footprint.AddInventory(taiwu, value("name")).AddInventory(npc, value("name"));
                    break;
                case "taiwu_teach":
                    physical(npc); physical(taiwu); guard.Footprint.AddCharacterState(npc, "skill:" + value("name")); break;
                case "flip_practice":
                    yield return ResolveToolPerson(npc, taiwu, value("person"), npc, "你", snap.Name, true,
                        (id, label, reason) => { first = id; firstReason = reason; });
                    if (first <= 0) { guard.Allowed = false; guard.Error = "无法权威解析练功对象"; break; }
                    // 自改练法也必须使用 actor+Taiwu 双端点。单端点信封无法证明
                    // 这笔“同道现场动作”仍绑定当前太吾与当前同行场景。
                    physical(npc); physical(first == npc ? taiwu : first);
                    guard.Footprint.AddCharacterState(first, "skill:" + value("skill"));
                    break;
                case "train_skill":
                {
                    yield return RefreshNpcStudyCandidates(npc, snap,
                        () => TurnToolDispatchAllowed(ct));
                    if (!snap.StudyProgressLoaded)
                    {
                        guard.Allowed = false;
                        guard.Error = "无法读取当前修炼进度，未执行修炼";
                        break;
                    }
                    string requested = (value("skill") ?? value("name") ?? string.Empty).Trim();
                    LearnableSkill learned = FindCombatSkill(snap, requested);
                    if (requested.Length == 0 || learned == null
                        || !snap.IncompleteTrainingSkillIds.Contains(learned.TemplateId))
                    {
                        guard.Allowed = false;
                        guard.Error = "所选武学并非当前尚未练满的已会武学；实时可练候选："
                            + TrainableSkillsList(snap);
                        break;
                    }
                    guard.Footprint.AddCharacterState(npc, "skill:" + learned.Name);
                    break;
                }
                case "read_book":
                {
                    yield return NpcSnapshotReader.FetchStudyProgress(npc, snap,
                        () => TurnToolDispatchAllowed(ct));
                    if (!snap.StudyProgressLoaded)
                    {
                        guard.Allowed = false;
                        guard.Error = "无法读取当前阅读进度，未执行读书";
                        break;
                    }
                    string requested = (value("book") ?? value("name") ?? string.Empty).Trim();
                    string canonicalBook = FindUnreadBookName(snap, requested);
                    if (requested.Length == 0 || canonicalBook == null)
                    {
                        guard.Allowed = false;
                        guard.Error = "所选书籍不在当前尚未读完的背包书籍中；实时可读候选："
                            + UnreadBooksList(snap);
                        break;
                    }
                    guard.Footprint.AddInventory(npc, canonicalBook)
                        .AddCharacterState(npc, "read_book:" + canonicalBook);
                    break;
                }
                case "use_item":
                {
                    yield return NpcSnapshotReader.FetchUsableItems(npc, snap,
                        () => TurnToolDispatchAllowed(ct));
                    if (!snap.UsableItemsLoaded)
                    {
                        guard.Allowed = false;
                        guard.Error = "无法读取当前可用物品，未执行使用";
                        break;
                    }
                    string requested = (value("item") ?? value("name") ?? string.Empty).Trim();
                    string canonicalItem = FindUsableItemName(snap, requested);
                    if (requested.Length == 0 || canonicalItem == null)
                    {
                        guard.Allowed = false;
                        guard.Error = "所选物品不在当前可自行使用候选中；实时可用候选："
                            + UsableItemsList(snap);
                        break;
                    }
                    guard.Footprint.AddInventory(npc, canonicalItem)
                        .AddCharacterState(npc, "use_item:" + canonicalItem);
                    break;
                }
                case "adjust_third_party_favor":
                    yield return ResolveToolPerson(npc, taiwu, value("target"), 0, value("target"), snap.Name, true,
                        (id, label, reason) => { first = id; firstReason = reason; });
                    if (first <= 0) { guard.Allowed = false; guard.Error = "无法权威解析观感对象"; break; }
                    guard.Footprint.AddRelation(npc, first); break;
                case "tell_secret":
                    yield return ResolveToolPerson(npc, taiwu, value("to"), taiwu, "太吾", snap.Name, true,
                        (id, label, reason) => { first = id; firstReason = reason; });
                    if (first <= 0 || (groupMode && first != taiwu && !GroupCtx.ParticipantIds.Contains(first)))
                    { guard.Allowed = false; guard.Error = "秘闻接收者不在本群聊频道"; break; }
                    guard.CanonicalSecretRecipientId = first;
                    guard.Footprint.AddCharacterState(first, "secret:" + value("index")); break;
                case "taiwu_tell_secret":
                    guard.Footprint.AddCharacterState(npc, "secret:" + value("index")); break;
                case "change_equipment":
                    physical(npc); physical(taiwu); guard.Footprint.AddInventory(npc, value("item")); break;
                case "add_feature":
                    guard.Footprint.AddCharacterState(npc, "feature:" + value("feature")); break;
                case "goto_place":
                    guard.Footprint.AddCharacterState(npc, "intent:goto"); break;
                case "sect_support":
                    guard.Footprint.AddCharacterState(npc, "sect_support"); break;
                case "change_caravan_favor":
                    physical(npc); physical(taiwu); guard.Footprint.AddRelation(npc, taiwu); break;
                case "start_combat":
                    physical(npc); physical(taiwu); guard.Footprint.AddCombat(); break;
                default:
                    // New mutation tools must declare their canonical footprint explicitly.
                    // Failing closed prevents a future tool from silently bypassing presence
                    // and cross-agent conflict arbitration.
                    guard.Allowed = false;
                    guard.Error = "unknown_group_mutation_tool:" + (toolName ?? "null");
                    break;
            }

            guard.PhysicalEndpoints = new List<int>(physicalEndpoints);
            if (!guard.Allowed || physicalEndpoints.Count == 0)
            { done?.Invoke(guard); yield break; }
            if (!TurnToolDispatchAllowed(ct))
            { guard.Allowed = false; guard.Error = "群聊已失效"; done?.Invoke(guard); yield break; }

            bool presenceDone = false, presenceReliable = false;
            List<int> presentIds = null;
            EffectHandler.QueryTaiwuScenePresence(taiwu, physicalEndpoints,
                (ok, ids) => { presenceReliable = ok; presentIds = ids; presenceDone = true; });
            float deadline = Time.unscaledTime + RpcReceiptWaitSeconds;
            while (!presenceDone && Time.unscaledTime < deadline && TurnToolDispatchAllowed(ct)) yield return null;
            if (!presenceDone || !presenceReliable || !TurnToolDispatchAllowed(ct))
            { guard.Allowed = false; guard.Error = "无法取得当前同块、同道或在押人物的权威现场快照"; done?.Invoke(guard); yield break; }

            var authoritativePresence = new HashSet<int>();
            if (presentIds != null) foreach (int id in presentIds) if (id > 0) authoritativePresence.Add(id);
            ISet<int> authorizedParticipants = groupMode
                ? GroupCtx.ParticipantIds
                : authoritativePresence;
            if (!GroupPhysicalPresencePolicy.Allows(taiwu, physicalEndpoints, authorizedParticipants,
                authoritativePresence, out int rejectedId, out string reasonCode))
            {
                guard.Allowed = false;
                guard.Error = "人物#" + rejectedId + "不具备本次当面行动资格（" + reasonCode + "）";
            }
            done?.Invoke(guard);
        }

        // 动作回执三态:1=权威成功 / 0=权威失败 / -1=未知。未知不能伪装成成功，也不能自动重试副作用。
        private static string ActMsg(int code, string okMsg, string failMsg)
            => code == 1 ? okMsg : (code == 0 ? failMsg
                : "(JHYL_ACTION_UNCONFIRMED:动作回执未确认,不得声称已成,也不得重试同一动作；只可说尚待实情落定。)");

        private static string DeterministicFailedActionReply(IList<string> landedActions, string failureReceipt = null)
        {
            bool partialSuccess = landedActions != null && landedActions.Count > 0;
            string reason = FailureReasonForReply(failureReceipt);
            if (!string.IsNullOrWhiteSpace(reason))
                return partialSuccess
                    ? "有些事已按真实结果办下，但另有动作未成：" + reason + "。"
                    : "此事未能办成：" + reason + "。";
            return partialSuccess
                ? "有些事已按真实结果办下，但另有动作未能办成；成与不成只以方才的游戏结果为准，我不拿空话混说。"
                : "此事未能办成；我不能把未成之事说成已经做下。";
        }

        private static string FailureReasonForReply(string receipt)
        {
            string text = (receipt ?? string.Empty).Trim().Trim('(', ')');
            if (text.Length == 0) return null;
            int colon = text.IndexOfAny(new[] { ':', '：' });
            if (colon >= 0 && colon + 1 < text.Length) text = text.Substring(colon + 1).Trim();
            string[] instructionCuts = { "。别假称", "。不得假称", "。请据实", "。先 query_", "。用角色", "；请据实" };
            foreach (string cut in instructionCuts)
            {
                int at = text.IndexOf(cut, StringComparison.Ordinal);
                if (at > 0) text = text.Substring(0, at).Trim();
            }
            if (text.EndsWith("。", StringComparison.Ordinal)) text = text.Substring(0, text.Length - 1);
            if (text.Length > 120) text = text.Substring(0, 120).TrimEnd() + "……";
            return text.Length > 0 ? text : null;
        }

        private static string DeterministicSucceededActionReply(IList<string> landedActions)
        {
            var visible = new List<string>();
            if (landedActions != null)
                foreach (string raw in landedActions)
                {
                    string safe = MemoryTrustPolicy.SanitizeForPromptData(raw, 240);
                    if (!string.IsNullOrWhiteSpace(safe) && !visible.Contains(safe)) visible.Add(safe);
                    if (visible.Count >= 8) break;
                }
            return visible.Count == 0
                ? "此事已按真实游戏回执办下；具体结果只以游戏状态为准。"
                : "我已照真实游戏结果办下：" + string.Join("、", visible.ToArray()) + "。";
        }

        private static string SelectLoopExhaustionReply(string currentReply,
            bool pendingFailedActionProse, string failureReceipt, IList<string> landedActions,
            string npcName)
        {
            bool hasLandedAction = landedActions != null && landedActions.Count > 0;
            bool trustedCurrent = IsTrustedLandedActionReply(currentReply)
                && !IsPlaceholderToolReply(currentReply);
            if (hasLandedAction)
                return trustedCurrent ? currentReply : DeterministicSucceededActionReply(landedActions);
            if (pendingFailedActionProse)
                return DeterministicFailedActionReply(landedActions, failureReceipt);

            if (trustedCurrent) return currentReply;
            return "（" + (string.IsNullOrWhiteSpace(npcName) ? "对方" : npcName)
                + "沉默片刻，终究没有把方才的话说完。）";
        }

        private static bool CanRunToolsInParallel(System.Collections.Generic.List<LlmToolCall> calls)
        {
            if (calls == null || calls.Count <= 1) return true;
            foreach (var c in calls)
                if (c == null || !IsRetrievalTool(c.Name))
                    return false;
            return true;
        }

        private static readonly System.Collections.Generic.HashSet<string> PostReceiptProseTools =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
            {
                // These tools bind a concrete actor/target whose canonical identity comes from
                // local/backend resolution. Their natural-language result must be generated only
                // after the model has read that receipt.
                "start_combat", "adjust_mood", "adjust_fame", "adjust_third_party_favor", "set_enmity", "relate_npc",
                "dissolve_relation", "matchmake", "kill", "capture", "poison", "heal",
                "detox", "regulate_breath",
                "gift", "barter", "steal", "teach", "write_book", "tell_secret",
                "taiwu_tell_secret", "taiwu_give_item", "taiwu_teach", "taiwu_write_book",
                "set_relation", "spend_night", "trade", "sect_support", "change_appearance",
                "change_equipment", "flip_practice", "add_feature", "goto_place",
                "train_skill", "read_book", "use_item",
                "change_caravan_favor", "offer_commission"
            };

        private static bool RequiresPostReceiptProse(System.Collections.Generic.List<LlmToolCall> calls)
        {
            if (calls == null) return false;
            foreach (var call in calls)
                if (call != null && PostReceiptProseTools.Contains(call.Name ?? string.Empty)) return true;
            return false;
        }

        private static readonly System.Collections.Generic.HashSet<string>
            TerminalPostReceiptClosureTools = new System.Collections.Generic.HashSet<string>(
                StringComparer.Ordinal)
            {
                "goto_place", "set_relation", "start_combat", "spend_night",
                "change_appearance", "offer_commission"
            };

        private static bool RequiresTerminalPostReceiptClosure(
            System.Collections.Generic.List<LlmToolCall> calls)
        {
            if (calls == null) return false;
            foreach (LlmToolCall call in calls)
                if (call != null && TerminalPostReceiptClosureTools.Contains(
                        call.Name ?? string.Empty)) return true;
            return false;
        }

        private static bool IsPlaceholderToolReply(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            string value = text.Trim();
            foreach (char c in value)
                if (!char.IsWhiteSpace(c) && c != '.' && c != '。' && c != '…' && c != '·'
                    && c != '—' && c != '-' && c != '～' && c != '~'
                    && c != '「' && c != '」' && c != '“' && c != '”' && c != '‘' && c != '’')
                    return false;
            return true;
        }

        // Internal execution-history markers are model-only context. A compatible provider may
        // echo them verbatim; never persist, display, or export those protocol records as dialogue.
        internal static string StripInternalExecutionTags(string text)
            => GlyphSanitizer.StripInternalExecutionTags(text);

        // 已有权威成功回执时，只拦截内部协议、工具过程和模型自述；动作是否成功仍完全
        // 由调用方先确认的 authoritative landedActions 决定。
        private static readonly string[] LandedReplyInternalCues = {
            "JHYL_", "tool_calls", "tool_call", "tool_result", "operation_id", "record_reaction", "query_", "action",
            "工具回执", "系统回执", "动作回执", "调用工具", "调了工具", "工具调用", "工具执行",
            "落地动作", "授权的工具", "不在授权", "真实游戏", "执行成功", "操作成功",
            "status=", "retryable", "reasoning", "token", "```", "JSON", "API", "LLM", "C#",
            "作为AI", "作为 AI", "语言模型", "系统提示", "提示词", "思考过程", "技术过程",
            "代码层", "调用记录", "报错信息", "错误码"
        };

        private static bool IsTrustedLandedActionReply(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string value = text.Trim();
            foreach (string cue in LandedReplyInternalCues)
                if (value.IndexOf(cue, StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
            return true;
        }

        // 进度行文字:模型在调哪个工具,那行显示什么
        private static string ProgressLabel(string tool)
        {
            switch (tool)
            {
                case "recall_memory": return "回想旧事中…";
                case "consult_action_guide": return "斟酌行事中…";
                case "offer_commission": return "写下委托中…";
                case "query_npc_status": return "审视己身中…";
                case "query_health_status": return "察看伤势气息中…";
                case "query_npc_relationships": return "细数人脉中…";
                case "query_npc_history": return "翻检往事中…";
                case "query_person_items":
                case "query_npc_items": return "清点随身物中…";
                case "query_npc_skills": return "默忆所学中…";
                case "query_npc_build": return "内观运功中…";
                case "query_npc_secrets": return "回想秘辛中…";
                case "query_taiwu_build": return "打量你的武学中…";
                case "query_world_progress": return "察看世道中…";
                case "query_merchant_goods": return "清点货物中…";
                case "start_combat": return "约定交手中…";
                case "query_person": return "打听此人中…";
                case "gift": return "取物相赠中…";
                case "barter": return "以物相易中…";
                case "steal": return "屏息探取中…";
                case "teach": return "倾囊相授中…";
                case "trade": return "与你交易中…";
                case "set_relation": case "dissolve_relation": case "matchmake": case "relate_npc": return "斟酌情分中…";
                case "write_book": return "凝神回忆中…";
                case "poison": return "暗下毒手中…";
                case "heal": return "运功疗伤中…";
                case "detox": return "施术驱毒中…";
                case "regulate_breath": return "调理内息中…";
                case "query_lore": return "翻阅百晓册中…";
                case "query_place": return "回想江湖地理中…";
                case "query_sect_lore": return "追想本门渊源中…";
                case "tell_secret": return "压低声音中…";
                case "kill": return "杀机暗起中…";
                case "capture": return "出手擒拿中…";
                case "change_appearance": return "准备本体梳妆互动中…";
                case "add_feature": return "心性微动中…";
                case "goto_place": return "盘算行程中…";
                case "change_caravan_favor": return "掂量这桩买卖中…";
                case "query_taiwu_items": return "打量太吾行囊中…";
                case "query_taiwu_skills": return "默想太吾武学中…";
                case "taiwu_give_item": return "拜领馈赠中…";
                case "taiwu_teach": return "拜领所授中…";
                case "taiwu_write_book": return "拜领书册中…";
                case "flip_practice": return "斟酌练法中…";
                case "train_skill": return "潜心修炼中…";
                case "read_book": return "闭卷通篇中…";
                case "use_item": return "取物自用中…";
                default: return "思忖中…";
            }
        }

        // 当下世道(年月季节 + 相枢品级),供世界观提示
        // 物品/装备品阶后缀「(X品)」(资源不显);供查询据实展示品阶。
        private static string ItemGradeSuffix(GameData.Domains.Item.ItemKey key)
        {
            if (key.ItemType == (sbyte)12) return "";
            try
            {
                sbyte g = GameData.Domains.Item.ItemTemplateHelper.GetGrade(key.ItemType, key.TemplateId);
                string n = GradeShortName(g);
                return string.IsNullOrEmpty(n) ? "" : ("(" + n + ")");
            }
            catch { return ""; }
        }
        private static string GradeShortName(sbyte g)
        {
            switch (g) { case 0: return "九品"; case 1: return "八品"; case 2: return "七品"; case 3: return "六品"; case 4: return "五品"; case 5: return "四品"; case 6: return "三品"; case 7: return "二品"; case 8: return "一品"; default: return ""; }
        }

        private static string BuildWorldState(NpcSnapshot snap)
        {
            int d = snap != null ? snap.CurrentDate : 0;
            if (d < 0) d = 0;
            int year = d / 12 + 1, zeroBasedMonth = d % 12, month = zeroBasedMonth + 1;
            // 与游戏 Season 配置和 NpcSnapshotReader.FetchTaiwuAndTime 保持一致：0/10/11 为冬，1..3 春，4..6 夏，7..9 秋。
            string season = (zeroBasedMonth == 0 || zeroBasedMonth >= 10) ? "冬"
                : zeroBasedMonth <= 3 ? "春" : zeroBasedMonth <= 6 ? "夏" : "秋";
            int xl = 0;
            try { xl = SingletonObject.getInstance<BasicGameData>().XiangshuProgress / 2; } catch { }
            return WorldLore.CurrentState(year, month, season, xl, null);
        }

        // 主线(相枢之劫,世道公知)+ 门派主线(本门中人知晓)→ 简短可读文。main=主线阶段枚举(0-29),sect=门派主线状态。
        private static string StoryStatusText(int main, int sect)
        {
            var sb = new StringBuilder();
            if (main >= 0)
            {
                string phase;
                if (main < 12) phase = "相枢之劫尚未显于世,江湖暂还安稳";
                else if (main == 12) phase = "相枢化身初现于世,人心惶惶";
                else if (main < 19) phase = "相枢之祸已蔓延,徐有容与古墓之事在江湖中流传";
                else if (main <= 25) phase = "诸路相枢化身正被逐一讨伐(已至第 " + (main - 18) + " 尊化身一线)";
                else phase = "相枢之劫已近终局,燃尘心魔之说甚嚣尘上";
                sb.Append("【世道·相枢之劫】").Append(phase).Append("。");
            }
            if (sect >= 0)
            {
                string ss = sect == 0 ? "尚未发端" : sect == 1 ? "正于本门中酝酿、进行" : sect == 2 ? "已了结,得了善果" : sect == 3 ? "已了结,落得恶果" : "不明";
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("【你本门的门派主线】").Append(ss).Append("(此乃本门中人知晓之事,可据此应对)。");
            }
            return sb.ToString();
        }

        // 是否指太吾村家园(present 太吾村是动态地点,不在静态地名表里;排除"过去的太吾村/梦中"等历史副本)
        private static bool IsHomeVillage(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.Contains("过去") || s.Contains("曾经") || s.Contains("梦")) return false;
            return s == "太吾村" || s.Contains("太吾家") || s == "家" || s == "老家" || s == "回家" || s == "咱村" || s == "村里";
        }

        // 「前往太吾所在地/身边」这类相对指代(无静态地名,后端取太吾当前坐标)。须含「太吾」且不含 村/家(那走家园分支)。
        private static bool IsFollowTaiwu(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.Contains("村") || s.Contains("家")) return false;   // 太吾村/太吾家 → 家园分支
            if (s == "太吾" || s == "太吾身边" || s == "太吾身旁" || s == "太吾所在" || s == "太吾所在地") return true;
            if (!s.Contains("太吾")) return false;
            foreach (var k in new[] { "所在", "身边", "身旁", "那里", "那儿", "处", "找", "寻", "投奔", "随", "跟", "去寻", "去找" })
                if (s.Contains(k)) return true;
            return false;
        }

        // ========== 工具分发:把模型的一次工具调用落地,onResult 回传给模型的结果文本 ==========
        // 把一串 charId 解析成姓名串(供 query_current_block / query_area_people / query_org_members)
        private IEnumerator PeopleNames(List<int> ids, int cap, Action<string> onNames)
        {
            if (ids == null || ids.Count == 0) { onNames(""); yield break; }
            if (ids.Count > cap) ids = ids.GetRange(0, cap);
            List<string> names = null; bool done = false;
            EffectHandler.QueryCharNames(ids, n => { names = n; done = true; });
            float dl = Time.unscaledTime + 1.8f; while (!done && Time.unscaledTime < dl) yield return null;
            var keep = new List<string>();
            for (int i = 0; i < ids.Count; i++)
            {
                string name = names != null && i < names.Count ? names[i] : null;
                if (string.IsNullOrWhiteSpace(name)) name = "姓名暂未取到";
                // JHYL_PERSON_REFERENCE_ID：显示名/法号会变化，同名也可能同时在场。
                // 把稳定实体编号一并交给模型，后续 query_person 与动作目标可原样使用 #id，
                // 避免“名单里明明有人，按名字却认不出/认错人”。
                keep.Add(name.Trim() + "(#" + ids[i] + ")");
            }
            onNames(string.Join("、", keep));
        }

        // 所有即时对话/暗号疗伤共用同一权威预查。健康目标不会派发 durable mutation，
        // 因而在 Agent 回执里属于“未执行”而不是真实失败；恢复日志重放仍直接走后端幂等检查。
        private static void ApplyHealAfterAuthoritativePreflight(int healerId, int targetId,
            Action<bool, string> onDone, string stableOperationId = null)
        {
            EffectHandler.QueryHealPreflight(targetId, state =>
            {
                if (state == null)
                {
                    onDone?.Invoke(false, "未执行：未能读取目标的权威健康与伤势状态");
                    return;
                }
                if (!state.TargetAlive)
                {
                    onDone?.Invoke(false, "未执行：行动者或目标已不在江湖");
                    return;
                }
                if (!state.TargetNeedsHealing)
                {
                    onDone?.Invoke(false, "未执行：目标当前气血" + state.TargetHealth + "/"
                        + state.TargetLeftMaxHealth + "、伤势标记" + state.TargetInjuryMarks + "，无需疗伤");
                    return;
                }
                EffectHandler.ApplyHeal(healerId, targetId, onDone, stableOperationId);
            });
        }

        private static void ApplyMedicalCareAfterAuthoritativePreflight(string tool,
            int healerId, int targetId, Action<bool, string> onDone,
            string stableOperationId = null)
        {
            if (healerId <= 0 || targetId <= 0 || healerId == targetId)
            {
                onDone?.Invoke(false, "未执行：驱毒或调息只能由当前 NPC 为另一名人物施行");
                return;
            }
            EffectHandler.QueryNpcHealthStatus(targetId, state =>
            {
                if (state == null)
                {
                    onDone?.Invoke(false, "未执行：未能读取目标的权威伤势、内息与中毒状态");
                    return;
                }
                if (string.Equals(tool, "detox", StringComparison.Ordinal))
                {
                    bool poisoned = false;
                    if (state.Poisons != null)
                        foreach (int value in state.Poisons) if (value > 0) { poisoned = true; break; }
                    if (!poisoned)
                    {
                        onDone?.Invoke(false, "未执行：目标当前并未中毒，无需驱毒");
                        return;
                    }
                    EffectHandler.ApplyDetox(healerId, targetId, onDone, stableOperationId);
                    return;
                }
                if (state.QiDisorderShow <= 0)
                {
                    onDone?.Invoke(false, "未执行：目标当前内息顺畅，无需调息");
                    return;
                }
                EffectHandler.ApplyRegulateBreath(healerId, targetId, onDone,
                    stableOperationId);
            });
        }

        private IEnumerator ExecuteTool(string name, string argsJson, NpcSnapshot snap, Conversation conv, NpcProfileForPrompt prof,
            System.Threading.CancellationToken ct, Action<string> onResult, string stableOperationId = null)
        {
            _lastLocalToolSemantic = LocalToolSemantic.NotApplicable;
            if (!TurnToolDispatchAllowed(ct))
            {
                _lastLocalToolSemantic = LocalToolSemantic.Unconfirmed;
                onResult?.Invoke("(本轮已取消或存档世界已切换，工具未派发；不得声称动作已完成。)");
                yield break;
            }
            if (snap?.IsDead == true)
            {
                _lastLocalToolSemantic = LocalToolSemantic.Unconfirmed;
                onResult?.Invoke("(灵魂状态禁止所有查询与行动，工具没有执行。只能继续交谈。)");
                yield break;
            }
            // 同轮去重:同名同参的调用第二次直接短路(防重复动作 / 省冗余往返);不同参数=不同调用,放行
            if (!_toolCallsSeen.Add((name ?? "") + "|" + CanonicalJson(argsJson)))
            {
                onResult?.Invoke("(本轮你已用相同参数调用过「" + name + "」,结果同上——请据上次结果继续,勿重复调用。)");
                yield break;
            }
            JObject a;
            try { a = string.IsNullOrWhiteSpace(argsJson) ? new JObject() : JObject.Parse(argsJson); } catch { a = new JObject(); }
            string S(string k) => a[k]?.ToString();
            int I(string k, int def = 0) { var t = a[k]; if (t == null) return def; try { return t.Value<int>(); } catch { return int.TryParse(t.ToString(), out int v) ? v : def; } }
            bool B(string k) { var t = a[k]; if (t == null) return false; try { return t.Value<bool>(); } catch { var s = (t.ToString() ?? "").ToLowerInvariant(); return s == "true" || s == "1" || s == "是"; } }

            int npc = snap.NpcId, taiwu = snap.TaiwuId;
            // JHYL_REFRESH_NPC_SKILLS_BEFORE_SKILL_TOOLS:功法/技艺可能刚被太吾教会或被事件改变,技能工具执行前必须刷新当前所学。
            if (name == "teach" || name == "query_npc_skills" || name == "write_book" || name == "flip_practice")
                yield return RefreshNpcSkills(npc, snap);
            // 全实时(不缓存,因会耗损):物品 gift/query_npc_items/change_equipment 走 EffectHandler 按名实时解析;商人货架 query_merchant_goods/trade 走 QueryLiveMerchantGoods 每次实时读(库存随卖随变)

            switch (name)
            {
                // —— 检索 ——
                case "recall_memory":
                    onResult(RecallMemory(S("topic"))); yield break;
                case "consult_action_guide":
                    {
                        string skillId = (S("skill") ?? string.Empty).Trim();
                        if (TryLoadActionGuide(skillId, out string guide))
                        {
                            _loadedActionSkills.Add(skillId);
                            Debug.Log("[JHYL_ACTION_GUIDE_LOADED] npc=" + npc + " skill=" + skillId
                                + " group=" + (GroupCtx != null));
                            onResult(guide);
                        }
                        else
                        {
                            onResult("(当前真实现场没有「" + skillId
                                + "」行动技能；它不会因此获得新工具。请只使用本轮实际提供的工具，或据实回应。)");
                        }
                        yield break;
                    }
                case "query_npc_skills":
                    {
                        // 武学/技艺合一:统一成一份名单,不分类型(你不必分清,teach/write_book 会按名自动判定)
                        var all = NamesOf(snap.LearnableSkills, s => s.Name);
                        all.AddRange(NamesOf(snap.LearnableLifeSkills, s => s.Name));
                        if (all.Count == 0) { onResult("你目前没有可传授给太吾的本事(或太吾已会)。"); yield break; }
                        string kw = (S("keyword") ?? "").Trim();
                        if (kw.Length > 0)
                        {
                            var hit = all.FindAll(n => NameHit(n, kw));
                            onResult(hit.Count > 0
                                ? ("你会的(含「" + kw + "」):" + string.Join("、", hit) + "。报这名即可(【教/传太吾→write_book 回忆成秘籍】;当面亲授在场第三方→teach),不必分武学还是技艺。")
                                : ("你不会「" + kw + "」。你会的:" + string.Join("、", all) + "。挑个确切名(【教/传太吾→write_book】;传在场第三方→teach)。"));
                        }
                        else
                            onResult("你会的武学技艺(不分类,报确切名即可):" + string.Join("、", all)
                                + "\n(【要教/传给太吾:用 write_book 回忆成秘籍交他自研】;当面亲授在场第三方:用 teach;身上正好有那本书:gift。做哪样你自定。)");
                        yield break;
                    }
                case "query_npc_build":
                    {
                        string bt = null;
                        yield return ReadCharacterBuild(npc, "你", x => bt = x);
                        onResult(bt ?? "(你当前功法搭配此刻看不真切;若太吾问起,就如实说自己一时说不准,别凭空编配招。)");
                        yield break;
                    }
                case "query_npc_items":
                    {
                        // 全实时:物品会随送/换而变,故每次直读后端 NPC【当前】持有,绝不用快照缓存
                        List<string> niNames = null; int niTotal = -1;
                        EffectHandler.QueryNpcItems(npc, S("keyword"), (ns, tot) => { niNames = ns; niTotal = tot; });
                        float niDl = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (niTotal < 0 && Time.unscaledTime < niDl) yield return null;
                        if (niTotal <= 0) { onResult("你身上没有可赠予的财物。"); yield break; }
                        string niKw = (S("keyword") ?? "").Trim();
                        if (niKw.Length > 0)
                            onResult(niNames != null && niNames.Count > 0
                                ? ("你随身确有,含「" + niKw + "」:" + string.Join("、", niNames))
                                : ("你随身并无「" + niKw + "」。你确有之物共 " + niTotal + " 样,换个词或不填 keyword 查全部再挑。"));
                        else
                            onResult("你的实时随身持有(含背包与当前穿戴,共 " + niTotal + " 样):"
                                + (niNames != null && niNames.Count > 0 ? string.Join("、", niNames) : "(无)")
                                + "。注意：当前穿戴仅在太吾本轮明确索要那件装备时才可用 gift；你不能为了自主送礼自行卸下装备。");
                        yield break;
                    }
                case "query_person_items":
                    {
                        string person = (S("person") ?? "").Trim();
                        int pid = 0; string pname = null, prs = null;
                        yield return ResolveToolPerson(npc, taiwu, person, npc, "你", snap.Name, true, (id, label, reason) => { pid = id; pname = label; prs = reason; });
                        if (pid <= 0)
                        {
                            onResult(ResolveFailMsg(string.IsNullOrEmpty(person) ? "此人" : person, prs, false));
                            yield break;
                        }
                        List<string> piNames = null; int piTotal = -1;
                        EffectHandler.QueryNpcItems(pid, S("keyword"), (ns, tot) => { piNames = ns; piTotal = tot; });
                        float piDl = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (piTotal < 0 && Time.unscaledTime < piDl) yield return null;
                        string who = string.IsNullOrWhiteSpace(pname) ? ("角色#" + pid) : pname;
                        if (piTotal <= 0) { onResult(who + "身上没有可交换的财物。"); yield break; }
                        string piKw = (S("keyword") ?? "").Trim();
                        if (piKw.Length > 0)
                            onResult(piNames != null && piNames.Count > 0
                                ? (who + "随身确有,含「" + piKw + "」:" + string.Join("、", piNames))
                                : (who + "随身并无「" + piKw + "」。其确有之物共 " + piTotal + " 样,换个词或不填 keyword 查全部再挑。"));
                        else
                            onResult(who + "的随身之物(可赠/可换/可装备,共 " + piTotal + " 样):" + (piNames != null && piNames.Count > 0 ? string.Join("、", piNames) : "(无)"));
                        yield break;
                    }
                case "query_taiwu_items":
                    yield return NpcSnapshotReader.FetchTaiwuHoldings(taiwu, h => _taiwuHoldings = h);
                    {
                        // 资财概览(银钱 + 资源储备):据实让 NPC 知道太吾家底,供议价/受赠/评估。银钱单独 GM 读一次(只在本工具读,不拖慢赠物/传功路径)
                        int[] money = { -1 }; bool md = false;
                        EffectHandler.QueryTaiwuMoney(taiwu, m => { money[0] = m; md = true; });
                        float mdl = Time.unscaledTime + RpcReceiptWaitSeconds; while (!md && Time.unscaledTime < mdl) yield return null;
                        var wealth = new StringBuilder();
                        if (money[0] >= 0) wealth.Append("银钱 ").Append(money[0]).Append(" 文");
                        if (_taiwuHoldings?.Resources != null)
                            foreach (var r in _taiwuHoldings.Resources)
                            { if (wealth.Length > 0) wealth.Append("、"); wealth.Append(r.Key).Append(' ').Append(r.Value); }
                        string wealthLine = wealth.Length > 0 ? ("\n太吾的资财:" + wealth + "。") : "";

                        var twItems = _taiwuHoldings?.Items;
                        var names = NamesOf(twItems, FormatGiftableItem);   // 物品名带品阶/数量
                        // 太吾的奇书(LegendaryBook,不在背包,寻常枚举会漏)——单列告知,标明乃异宝(非寻常可赠)
                        string booksLine = "";
                        {
                            List<string> tb = null; bool tbd = false;
                            EffectHandler.QueryLegendaryBooks(taiwu, x => { tb = x; tbd = true; });
                            float tbl = Time.unscaledTime + RpcReceiptWaitSeconds; while (!tbd && Time.unscaledTime < tbl) yield return null;
                            if (tb != null && tb.Count > 0) booksLine = "\n太吾另持奇书:" + string.Join("、", tb) + "(奇书乃异宝,非寻常可赠之物)。";
                        }
                        if (names.Count == 0)
                        {
                            onResult((string.IsNullOrEmpty(wealthLine) ? "太吾此刻身上没有可让与你的财物。" : ("太吾身上没有可让与你的单品物什。" + wealthLine)) + booksLine);
                            yield break;
                        }
                        string kw = (S("keyword") ?? "").Trim();
                        if (kw.Length > 0)
                        {
                            var matched = new List<string>();
                            if (twItems != null)
                                foreach (var g in twItems)
                                    if (GiftableItemHit(g, kw)) matched.Add(FormatGiftableItem(g));
                            onResult(matched.Count > 0
                                ? ("太吾随身确有,含「" + kw + "」:" + string.Join("、", matched) + wealthLine + booksLine)
                                : ("太吾随身并无「" + kw + "」。太吾确有之物共 " + names.Count + " 样,换个词或不填 keyword 查全部再挑。" + wealthLine + booksLine));
                        }
                        else
                            onResult("太吾随身、可赠你的物什(他要送你时按名收,共 " + names.Count + " 样):" + string.Join("、", names) + wealthLine + booksLine);
                    }
                    yield break;
                case "query_taiwu_skills":
                    yield return RefreshTaiwuHoldings(taiwu);
                    {
                        // 武学/技艺合一:统一名单,不分类型(taiwu_teach/taiwu_write_book 会按名自动判定)
                        var all = NamesOf(_taiwuHoldings?.CombatSkills, x => x.Name);
                        all.AddRange(NamesOf(_taiwuHoldings?.LifeSkills, x => x.Name));
                        if (all.Count == 0) { onResult("太吾此刻没有可传授于你的本事。"); yield break; }
                        string kw = (S("keyword") ?? "").Trim();
                        if (kw.Length > 0)
                        {
                            var hit = all.FindAll(n => NameHit(n, kw));
                            onResult(hit.Count > 0
                                ? ("太吾会的(含「" + kw + "」):" + string.Join("、", hit) + "。亲授用 taiwu_teach,成书相赠用 taiwu_write_book。")
                                : ("太吾不会「" + kw + "」。他能传你的:" + string.Join("、", all) + "。"));
                        }
                        else
                            onResult("太吾会、可传你或可回忆成书给你的本事(武学技艺不分,亲授用 taiwu_teach,成书用 taiwu_write_book):" + string.Join("、", all));
                    }
                    yield break;
                case "query_npc_secrets":
                    {
                        string receiver = (S("to") ?? string.Empty).Trim();
                        int targetId = 0;
                        string targetLabel = null;
                        string resolveReason = null;
                        yield return ResolveToolPerson(npc, taiwu, receiver, taiwu, "太吾",
                            snap.Name, true, (id, label, reason) =>
                            {
                                targetId = id;
                                targetLabel = id == taiwu ? "太吾" : label;
                                resolveReason = reason;
                            });
                        if (targetId <= 0 || targetId == npc)
                        {
                            onResult("无法可靠识别秘闻接收者「" + receiver + "」："
                                + (resolveReason ?? "只能查询太吾、自己认识或当前在场的具名人物")
                                + "。不要猜序号。");
                            yield break;
                        }
                        if (string.IsNullOrWhiteSpace(targetLabel)) targetLabel = "人物#" + targetId;
                        List<SecretRef> eligible = null; string eligibilityReason = null;
                        yield return NpcSnapshotReader.FetchDisclosableSecrets(npc, targetId, snap.CurrentDate,
                            (items, reason) => { eligible = items; eligibilityReason = reason; },
                            () => TurnToolDispatchAllowed(ct));
                        if (eligible == null || eligible.Count == 0)
                        {
                            onResult("你目前没有能真正告诉「" + targetLabel + "」的秘闻："
                                + (eligibilityReason ?? "对方已经知情或秘闻已经公开")
                                + "。应保密、换接收者或换别的行为，不要调用 tell_secret。" );
                            yield break;
                        }
                        var secretText = new StringBuilder("你确知且「" + targetLabel
                            + "」尚未知的秘闻（tell_secret.to 必须仍填此人，index 照抄原始序号）：\n");
                        foreach (SecretRef secret in eligible)
                            secretText.Append(secret.DisplayIndex).Append(". ")
                                .Append(secret.Text ?? "(一桩秘闻)").Append('\n');
                        onResult(secretText.ToString());
                        yield break;
                    }
                case "query_npc_status":
                    onResult(BuildStatusText(snap, prof)); yield break;
                case "query_health_status":
                    {
                        NpcHealthStatus health = null; bool healthDone = false;
                        EffectHandler.QueryNpcHealthStatus(npc,
                            value => { health = value; healthDone = true; });
                        float healthDeadline = Time.unscaledTime + EffectHandler.ReadOnlyQueryWaitSeconds;
                        while (!healthDone && Time.unscaledTime < healthDeadline) yield return null;
                        if (!healthDone || health == null)
                        {
                            onResult("(未能可靠读取你此刻的伤势、气息与中毒状态；不要凭空声称健康或中毒。)");
                            yield break;
                        }
                        onResult("你此刻的身体实况：气血" + health.Health + "/"
                            + health.LeftMaxHealth + "；伤势标记" + health.InjuryMarks
                            + "；气息（内息紊乱）" + health.QiDisorderShow + "，阶段「"
                            + (health.QiDisorderLevel ?? "未知") + "」；中毒："
                            + (health.PoisonSummary ?? "无中毒") + "。以上均为游戏当前权威值。" );
                        yield break;
                    }
                case "query_place":
                    {
                        string pkw = (S("keyword") ?? "").Trim();
                        List<string> places = null;
                        EffectHandler.QueryPlaces(pkw, ps => places = ps);
                        if (places == null || places.Count == 0)
                            onResult(pkw.Length > 0
                                ? ("江湖里没找到叫「" + pkw + "」的地方(或非现世可往之地);换个说法、或留空看有哪些地方可去,别硬填不存在的地名。)")
                                : "(一时想不起有哪些地名。)");
                        else
                            onResult((pkw.Length > 0 ? ("含「" + pkw + "」的江湖地名:") : "江湖主要地名(可前往):") + string.Join("、", places)
                                + "。要前往就把上面的确切名【原样】填给 goto_place。");
                        yield break;
                    }
                case "query_lore":
                    {
                        string path = S("path");
                        string lore = null;
                        try { lore = EncyclopediaReader.Browse(path, false); } catch { }
                        onResult(string.IsNullOrWhiteSpace(lore)
                            ? "(百晓册当前无法读取；不要杜撰，稍后可以再翻阅。)"
                            : ("百晓册载(若返回的是目录，请选择最贴切的完整路径再次调用；若返回正文，请用自己的口吻转述，勿照搬):\n" + lore));
                        yield break;
                    }
                case "query_sect_lore":
                    {
                        if (snap.OrgTemplateId <= 0 || string.IsNullOrWhiteSpace(snap.SectName))
                        { onResult("(你并无门派归属、或只是市井散人,没有门派典故可述——如实相告即可。)"); yield break; }
                        var sl = new StringBuilder();
                        sl.Append("你身属【").Append(snap.SectName).Append("】");
                        if (!string.IsNullOrWhiteSpace(snap.SectDesc)) sl.Append("。门派简介:").Append(snap.SectDesc);
                        if (!string.IsNullOrWhiteSpace(snap.SectExtra)) sl.Append("\n门风理念:").Append(snap.SectExtra);
                        if (!string.IsNullOrWhiteSpace(snap.SectVow)) sl.Append("\n入派誓约:").Append(snap.SectVow);
                        if (!string.IsNullOrWhiteSpace(snap.SectStory)) sl.Append("\n本门主线渊源:").Append(snap.SectStory);
                        sl.Append("\n(以上是你身为本门中人本就知晓的门派底细,请用你自己的口吻、本门人的立场道来,勿照搬。)");
                        onResult(sl.ToString()); yield break;
                    }
                case "query_taiwu_build":
                    { string bt = null; yield return ReadCharacterBuild(snap.TaiwuId, "太吾", x => bt = x); onResult(bt ?? "(太吾的武学搭配此刻看不真切;你可凭平日所见与功法心得,给个大方向上的建议。)"); yield break; }
                case "query_npc_relationships":
                    {
                        string requestedRelationName = (S("name") ?? "").Trim();
                        if (requestedRelationName.Length > 0)
                        {
                            // JHYL_QUERY_RELATIONSHIP_NAMED_TARGET：schema 的可选 name 必须真查这一对，
                            // 不能无论填谁都只返回整张关系网。
                            int relationTarget = 0; string relationTargetName = null, relationResolveReason = null;
                            yield return ResolveToolPerson(npc, taiwu, requestedRelationName, 0,
                                requestedRelationName, snap.Name, true,
                                (id, label, reason) =>
                                {
                                    relationTarget = id;
                                    relationTargetName = label;
                                    relationResolveReason = reason;
                                });
                            if (relationTarget <= 0)
                            {
                                onResult(ResolveFailMsg(requestedRelationName, relationResolveReason, false));
                                yield break;
                            }
                            if (relationTarget == npc)
                            {
                                onResult("(不能查询自己与自己的关系；name 请填太吾或另一名可识别人物。)");
                                yield break;
                            }
                            string namedRelation = null; int namedFavor = 0; bool namedRelationDone = false;
                            EffectHandler.QueryPersonRel(npc, relationTarget,
                                (rel, favor) => { namedRelation = rel; namedFavor = favor; namedRelationDone = true; });
                            float namedDeadline = Time.unscaledTime + RpcReceiptWaitSeconds;
                            while (!namedRelationDone && Time.unscaledTime < namedDeadline) yield return null;
                            if (!namedRelationDone)
                            {
                                onResult("(未能可靠读取你与「" + requestedRelationName + "」的当前关系，请稍后重查；不要把超时当作无关系。)");
                                yield break;
                            }
                            string namedLabel = string.IsNullOrWhiteSpace(relationTargetName)
                                ? requestedRelationName : relationTargetName;
                            onResult("你与" + namedLabel + "的当前真实关系:"
                                + (string.IsNullOrWhiteSpace(namedRelation) ? "无显著关系" : namedRelation)
                                + "；你对其好感=" + namedFavor + "。");
                            yield break;
                        }

                        string[] rels = { null }; bool rd = false;
                        string taiwuRelation = null; int taiwuFavor = 0; bool taiwuRelationDone = false;
                        EffectHandler.QueryNpcRelations(npc, x => { rels[0] = x; rd = true; });
                        // 与太吾的关系也每次从后端重读，不能复用开聊时画像里的旧值。
                        EffectHandler.QueryPersonRel(npc, taiwu,
                            (rel, favor) => { taiwuRelation = rel; taiwuFavor = favor; taiwuRelationDone = true; });
                        float rdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while ((!rd || !taiwuRelationDone) && Time.unscaledTime < rdl) yield return null;
                        string rtxt = rels[0];
                        // 关系网 + 与太吾的实时关系一并给；都空才退提示。
                        var rb = new StringBuilder();
                        if (!string.IsNullOrWhiteSpace(rtxt)) rb.Append("你的关系网:\n").Append(rtxt);
                        if (taiwuRelationDone)
                        {
                            if (rb.Length > 0) rb.Append('\n');
                            rb.Append("与太吾:")
                                .Append(string.IsNullOrWhiteSpace(taiwuRelation) ? "无显著关系" : taiwuRelation)
                                .Append("；你对太吾好感=").Append(taiwuFavor);
                        }
                        onResult(rb.Length > 0 ? rb.ToString() : "(你眼下没有什么特别的至亲故旧或宿敌;泛泛之交不必细数。)");
                        yield break;
                    }
                case "query_npc_history":
                    {
                        var lr = snap.LifeRecords;
                        if (lr == null || lr.Count == 0) { onResult("(你这一生还没什么可记的大事。)"); yield break; }
                        string history = null;
                        yield return LifeExperienceSummaryService.BuildForQuery(taiwu, snap, ct,
                            () => TurnToolDispatchAllowed(ct), value => history = value);
                        onResult(history ?? "(生平记录暂未能可靠整理，请稍后重查。)");
                        yield break;
                    }
                case "query_world_progress":
                    {
                        string ws = BuildWorldState(snap);
                        // 追加:主线(相枢之劫,世道公知)+ 本门门派主线(本门中人知晓)
                        int[] st = { int.MinValue, int.MinValue }; bool sd = false;
                        EffectHandler.QueryStoryStatus(snap.OrgTemplateId, (main, sect) => { st[0] = main; st[1] = sect; sd = true; });
                        float sdl = Time.unscaledTime + RpcReceiptWaitSeconds; while (!sd && Time.unscaledTime < sdl) yield return null;
                        string extra = StoryStatusText(st[0], st[1]);
                        onResult(string.IsNullOrEmpty(extra) ? ws : (ws + "\n" + extra));
                        yield break;
                    }
                case "query_person":
                    {
                        string pn = (S("name") ?? "").Trim();
                        if (pn.Length == 0) { onResult("(没说要打听谁——填那人姓名。)"); yield break; }
                        if (pn == "太吾" || pn.Equals("taiwu", StringComparison.OrdinalIgnoreCase)) { onResult("(眼前这位正是太吾,不必向旁人打听。)"); yield break; }
                        int[] tid = { int.MinValue };
                        EffectHandler.ResolveChar(npc, pn, true, true,
                            (c, reason) => tid[0] = c);   // 只读打听允许全局唯一严格全名；后续物理动作仍按实时现场独立校验。
                        float prdl = Time.unscaledTime + RpcReceiptWaitSeconds; while (tid[0] == int.MinValue && Time.unscaledTime < prdl) yield return null;
                        if (tid[0] <= 0 || tid[0] == npc)
                        { onResult("(按严格全名遍寻当前存活人物，仍没有「" + pn + "」这么一位——许是名字不准。别凭空编造此人其事。)"); yield break; }
                        string brief = null; yield return NpcSnapshotReader.FetchPersonBrief(tid[0], b => brief = b);
                        string prel = null; int pfav = 0; bool gotRel = false;
                        EffectHandler.QueryPersonRel(npc, tid[0], (r, f) => { prel = r; pfav = f; gotRel = true; });
                        float prdl2 = Time.unscaledTime + RpcReceiptWaitSeconds; while (!gotRel && Time.unscaledTime < prdl2) yield return null;
                        bool presenceDone = false, presentWithSpeaker = false;
                        if (Remote)
                        {
                            EffectHandler.QueryActorBlockChars(npc, (ok, area, block, values) =>
                            {
                                presentWithSpeaker = ok && values != null && values.Contains(tid[0]);
                                presenceDone = true;
                            });
                        }
                        else
                        {
                            EffectHandler.QueryTaiwuScenePresence(taiwu, new[] { tid[0] },
                                (ok, values) =>
                                {
                                    presentWithSpeaker = ok && values != null && values.Contains(tid[0]);
                                    presenceDone = true;
                                });
                        }
                        float presenceDeadline = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (!presenceDone && Time.unscaledTime < presenceDeadline) yield return null;
                        string favWord = pfav >= 6000 ? "情谊深厚" : pfav >= 2000 ? "交情不错" : pfav >= 200 ? "尚算相得" : pfav <= -6000 ? "深恶痛绝" : pfav <= -2000 ? "颇为嫌恶" : pfav <= -200 ? "有些疏冷" : "泛泛之交";
                        var sbp = new StringBuilder();
                        sbp.Append("你打听到的「").Append(pn).Append("」:").Append(string.IsNullOrWhiteSpace(brief) ? "识得此人,一时却想不起细节" : brief);
                        if (!string.IsNullOrWhiteSpace(prel)) sbp.Append(" 你与他:").Append(prel).Append("。");
                        sbp.Append(" 你对他:").Append(favWord).Append("。");
                        if (presenceDone)
                            sbp.Append(presentWithSpeaker
                                ? " 实时现场核验：此人正在你身边，可继续按具体动作条件处理。"
                                : " 实时现场核验：此人不在你身边；需要当面的物理动作不能隔空执行。");
                        sbp.Append("(据此如实回应太吾,别添你并不知道的事。)");
                        onResult(sbp.ToString());
                        yield break;
                    }
                case "query_current_block":
                    {
                        if (Remote)
                        {
                            List<int> actorIds = null, taiwuIds = null, remoteCompanions = null;
                            bool actorDone = false, taiwuDone = false, companionsDone = false;
                            EffectHandler.QueryActorBlockChars(npc, (ok, area, block, values) =>
                            {
                                actorIds = ok ? values : null;
                                actorDone = true;
                            });
                            EffectHandler.QuerySameBlockChars(taiwu,
                                x => { taiwuIds = x; taiwuDone = true; }, true);
                            NpcSnapshotReader.FetchGroupMembers(taiwu,
                                x => { remoteCompanions = x; companionsDone = true; });
                            float remoteDeadline = Time.unscaledTime + 2.5f;
                            while ((!actorDone || !taiwuDone || !companionsDone)
                                && Time.unscaledTime < remoteDeadline) yield return null;
                            if (!actorDone || !taiwuDone || !companionsDone || actorIds == null)
                            {
                                onResult("(千里传音两端的现场名单暂未完整取得，不能据此断言某人不在场；请稍后重查。)");
                                yield break;
                            }

                            var actorSet = new HashSet<int>();
                            foreach (int id in actorIds)
                                if (id > 0 && id != npc && id != taiwu) actorSet.Add(id);
                            var taiwuSet = new HashSet<int>();
                            if (taiwuIds != null)
                                foreach (int id in taiwuIds)
                                    if (id > 0 && id != npc && id != taiwu) taiwuSet.Add(id);
                            if (remoteCompanions != null)
                                foreach (int id in remoteCompanions)
                                    if (id > 0 && id != npc && id != taiwu) taiwuSet.Add(id);

                            var actorList = new List<int>(actorSet); actorList.Sort();
                            var taiwuList = new List<int>(taiwuSet); taiwuList.Sort();
                            string actorNames = null, taiwuNames = null;
                            yield return PeopleNames(actorList, actorList.Count, s => actorNames = s);
                            yield return PeopleNames(taiwuList, taiwuList.Count, s => taiwuNames = s);
                            onResult("当前是千里传音，两个现场必须分开判断。你身边："
                                + (string.IsNullOrWhiteSpace(actorNames) ? "没有查到其他可交互人物" : actorNames)
                                + "。太吾身边（含当前同道）："
                                + (string.IsNullOrWhiteSpace(taiwuNames) ? "没有查到其他可交互人物" : taiwuNames)
                                + "。姓名后的 #编号可直接用于后续人物查询；远隔双方不能据此执行当面动作。");
                            yield break;
                        }
                        List<int> ids = null; List<int> companions = null; bool d = false, td = false;
                        EffectHandler.QuerySameBlockChars(taiwu, x => { ids = x; d = true; }, true);
                        NpcSnapshotReader.FetchGroupMembers(taiwu, x => { companions = x; td = true; });
                        float dl = Time.unscaledTime + 1.8f; while ((!d || !td) && Time.unscaledTime < dl) yield return null;
                        if (!d || !td)
                        {
                            onResult("(现场名单暂未完整取得，不能据此断言无人、某人不在场或同道不在同行；请稍后重新查询。任何真实动作仍须以执行时后端现场复核为准。)");
                            yield break;
                        }
                        var merged = new HashSet<int>();
                        if (ids != null) foreach (int id in ids) if (id > 0 && id != taiwu && id != npc) merged.Add(id);
                        if (companions != null) foreach (int id in companions) if (id > 0 && id != taiwu && id != npc) merged.Add(id);
                        ids = new List<int>(merged); ids.Sort();
                        string ns = null; yield return PeopleNames(ids, ids.Count, s => ns = s);
                        onResult(string.IsNullOrWhiteSpace(ns)
                            ? "(此刻同处一地、且按同行规则视作同处的同道中，没有查到其他可交互人物。)"
                            : ("此刻与你/太吾同处一地的全部可交互人物（含仇敌、陌生人和当前同道）有:" + ns
                                + "。姓名后的 #编号可直接用于 query_person 与后续动作，同名时必须使用编号；名单只证明实时在场，不代表动作已经完成。")); yield break;
                    }
                case "query_area_people":
                    {
                        string place = (S("place") ?? "").Trim();
                        if (place.Length == 0) { onResult("(要打听哪个地方?填实有地名。)"); yield break; }
                        short ar = -1, bl = 0; string rn = place; bool okr = false; try { okr = EffectHandler.ResolveAreaId(place, out ar, out bl, out rn); } catch { }
                        if (!okr) { onResult("(认不出地名「" + place + "」,换江湖中实有地名。)"); yield break; }
                        List<int> ids = null; bool d = false;
                        EffectHandler.QueryAreaChars(ar, taiwu, 25, x => { ids = x; d = true; });
                        float dl = Time.unscaledTime + 2f; while (!d && Time.unscaledTime < dl) yield return null;
                        string ns = null; yield return PeopleNames(ids, 20, s => ns = s);
                        onResult(string.IsNullOrWhiteSpace(ns) ? ("(" + rn + "一带此刻没打听到什么人。)") : (rn + "一带此刻有:" + ns + "。")); yield break;
                    }
                case "query_org_members":
                    {
                        string sect = (S("sect") ?? "").Trim();
                        int orgTpl = sect.Length == 0 ? snap.OrgTemplateId : NpcSnapshotReader.ResolveOrgId(sect);
                        if (orgTpl <= 0) { onResult("(认不出门派「" + sect + "」,或你不属任何门派。)"); yield break; }
                        List<int> ids = null; bool d = false;
                        EffectHandler.QueryOrgMembers(orgTpl, -1, 25, taiwu, x => { ids = x; d = true; });
                        float dl = Time.unscaledTime + 2f; while (!d && Time.unscaledTime < dl) yield return null;
                        string ns = null; yield return PeopleNames(ids, 20, s => ns = s);
                        onResult(string.IsNullOrWhiteSpace(ns) ? "(没打听到该门派现有谁。)" : ("该门派现有:" + ns + "。")); yield break;
                    }
                case "query_merchant_goods":
                    {
                        int[] money = { int.MinValue };
                        EffectHandler.QueryTaiwuMoney(taiwu, m => money[0] = m);
                        List<GiftableItem> liveGoods = null;
                        yield return QueryLiveMerchantGoods(snap, g => liveGoods = g);   // 实时读货架(库存随卖随变,绝不缓存)
                        float mdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (money[0] == int.MinValue && Time.unscaledTime < mdl) yield return null;
                        // 货名带库存数量(>1 才缀×N;单件不缀),让商人能据实告诉太吾还剩几件
                        var goods = new List<string>();
                        if (liveGoods != null) foreach (var g in liveGoods)
                        {
                            int bv = 0; try { bv = global::GameData.Domains.Item.ItemTemplateHelper.GetBaseValue(g.Key.ItemType, g.Key.TemplateId); } catch { }
                            string gnm = g.Count > 1 ? (g.Name + "×" + g.Count) : g.Name;
                            goods.Add(bv > 0 ? (gnm + "〔基准价约" + bv + "文/件〕") : gnm);   // 补基准价,商人据此报价、不再凭空乱开
                        }
                        var mg = new StringBuilder();
                        if (goods.Count == 0) mg.Append("你货架上暂无现货可售(货是按时令/集市补的;此刻无存货就如实告诉太吾,切勿拿你随身的私物当货卖)。");
                        else { bool sr; int ht; mg.Append(SearchList(goods, S("keyword"), "你(商人)可售的货物", "你货里确有,含", out sr, out ht)); }
                        if (money[0] >= 0) mg.Append("\n太吾现有银钱:").Append(money[0]).Append(" 两 —— 报价别超过他付得起的;他买不起就直说、莫成交。");
                        onResult(mg.ToString());
                        yield break;
                    }

                // —— 收尾反应 ——
                case "offer_commission":
                    {
                        if (!CommissionProposalPolicy.TryParseArguments(a,
                                out CommissionProposal proposal, out string proposalError))
                        {
                            _lastLocalToolSemantic = LocalToolSemantic.Failed;
                            onResult("(委托未发布：" + proposalError + "。请按工具 schema 修正参数，"
                                + "或直接继续交谈；别假称委托已经进入列表。)");
                            yield break;
                        }
                        if (proposal.Kind == CommissionProposalPolicy.KillNpc)
                        {
                            int targetId = 0;
                            string targetLabel = proposal.TargetNpcName;
                            string targetReason = null;
                            yield return ResolveToolPerson(npc, taiwu, proposal.TargetNpcName,
                                0, proposal.TargetNpcName, snap.Name, true,
                                (id, label, reason) =>
                                {
                                    targetId = id; targetLabel = label; targetReason = reason;
                                });
                            targetId = CharacterProxyIdentityService.ResolveKnown(taiwu, targetId);
                            if (targetId <= 0 || targetId == taiwu || targetId == npc)
                            {
                                _lastLocalToolSemantic = LocalToolSemantic.Failed;
                                onResult("(委托未发布：无法把击杀目标解析为另一名仍有效的人物。"
                                    + (string.IsNullOrWhiteSpace(targetReason) ? string.Empty
                                        : "原因=" + targetReason + "。")
                                    + "请换用完整真名或 #人物编号，别假称任务已经写入。)");
                                yield break;
                            }
                            NpcSnapshot targetSnapshot = null;
                            yield return NpcSnapshotReader.Fetch(targetId,
                                value => targetSnapshot = value);
                            proposal.TargetNpcId = targetId;
                            proposal.TargetNpcName = targetSnapshot != null
                                && !string.IsNullOrWhiteSpace(targetSnapshot.Name)
                                    ? targetSnapshot.Name : targetLabel;
                        }
                        CommissionSnapshot commissionSnapshot = null;
                        EffectHandler.QueryCommissionSnapshot(taiwu, npc,
                            proposal.TargetNpcId,
                            value => commissionSnapshot = value);
                        float commissionDeadline = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (commissionSnapshot == null && Time.unscaledTime < commissionDeadline)
                            yield return null;
                        if (commissionSnapshot == null || !commissionSnapshot.Ok)
                        {
                            _lastLocalToolSemantic = LocalToolSemantic.Failed;
                            onResult("(委托未发布：无法读取太吾当前的权威任务基线。稍后再试，"
                                + "别假称任务已经写入。)");
                            yield break;
                        }
                        if (proposal.Kind == CommissionProposalPolicy.KillNpc)
                        {
                            if (!commissionSnapshot.TargetValid
                                || commissionSnapshot.TargetNpcId != proposal.TargetNpcId
                                || commissionSnapshot.TargetConsummate < 0
                                || commissionSnapshot.TargetRewardGrade < 0)
                            {
                                _lastLocalToolSemantic = LocalToolSemantic.Failed;
                                onResult("(委托未发布：所选击杀目标当前并非有效的存活人物。"
                                    + "请换一个目标，别假称任务已经写入。)");
                                yield break;
                            }
                            proposal.TargetConsummate = commissionSnapshot.TargetConsummate;
                            proposal.RewardGrade = commissionSnapshot.TargetRewardGrade;
                        }
                        int baseline = 0;
                        switch (proposal.Kind)
                        {
                            case CommissionProposalPolicy.CollectResource:
                                baseline = commissionSnapshot.Resources[proposal.ResourceType]; break;
                            case CommissionProposalPolicy.EarnMoney:
                                baseline = commissionSnapshot.Resources[6]; break;
                            case CommissionProposalPolicy.GainPrestige:
                                baseline = commissionSnapshot.Resources[7]; break;
                            case CommissionProposalPolicy.IncreaseFavor:
                                baseline = commissionSnapshot.Favor; break;
                        }
                        int objectiveCap = proposal.Kind == CommissionProposalPolicy.IncreaseFavor
                            ? short.MaxValue : 999999999;
                        if (proposal.Kind != CommissionProposalPolicy.DeliverResource
                            && baseline > objectiveCap - proposal.Amount)
                        {
                            _lastLocalToolSemantic = LocalToolSemantic.Failed;
                            onResult("(委托未发布：这项数值已经接近本体上限，无法形成可完成的目标。"
                                + "请换一种委托，别假称任务已经写入。)");
                            yield break;
                        }
                        if (!CommissionStore.TryIssue(taiwu, npc, snap.Name, snap.CurrentDate,
                                proposal, baseline, out CommissionRecord issued,
                                out string issueError))
                        {
                            _lastLocalToolSemantic = LocalToolSemantic.Failed;
                            onResult("(委托未发布：" + (issueError ?? "本地状态写入失败")
                                + "。别在正文中声称列表里已经出现任务。)");
                            yield break;
                        }
                        _lastLocalToolSemantic = LocalToolSemantic.Succeeded;
                        AssistantWidget.NotifyCommissionChanged();
                        CommissionWindow.RefreshIfOpen();
                        Emit("向太吾发布委托：" + issued.Objective + "；奖励为"
                            + issued.RewardLabel, null, null, true);
                        onResult("(真实委托已写入右侧【委】列表：" + issued.Objective
                            + "；完成后随机获得" + issued.RewardLabel + "。请用角色口吻自然复述 request，"
                            + "不得指定具体奖品，也不得提前声称已完成或已发奖。)");
                        yield break;
                    }
                case "record_reaction":
                    {
                        if (_reactionState == ReactionCommitState.Succeeded
                            || _reactionState == ReactionCommitState.Attempting
                            || _reactionState == ReactionCommitState.Unknown)
                        {
                            EffectHandler.CompleteObservedOperation(ToolOutcome.Failed(
                                "duplicate_reaction", stableOperationId, null,
                                "one reaction mutation is allowed per NPC turn"));
                            onResult(_reactionState == ReactionCommitState.Unknown
                                ? "(本轮即时反应回执尚未确认；为防重复调整好感或戒心，本次未重新派发。)"
                                : "(本轮即时反应已经记录或正在记录；重复调用未执行，也不会再次叠加好感或戒心。)");
                            yield break;
                        }
                        _reactionState = ReactionCommitState.Attempting;
                        int sat = Clamp(I("satisfaction"), -100, 100);
                        int mood = Clamp(I("mood"), -100, 100);
                        int mor = Clamp(I("morality_shift"), -25, 25);
                        int alert = JianghuYouling.Core.Influence.ConversationReactionPolicy.ResolveAlertnessShift(
                            sat, I("alertness_shift"), snap.CreatingType == 1);
                        int favorDelta = sat == 0 ? 0 : Clamp((int)Math.Round(sat * 80.0), -4000, 4000);
                        int happinessDelta = mood == 0 ? 0 : Clamp((int)Math.Round(mood * 0.25), -25, 25);
                        if (favorDelta == 0 && happinessDelta == 0 && mor == 0 && alert == 0)
                        {
                            EffectHandler.CompleteObservedOperation(ToolOutcome.Succeeded(stableOperationId, null, "本轮无数值变化"));
                            _reactionState = ReactionCommitState.Succeeded;
                            onResult("(本轮你对太吾的心绪如常，无数值反应需要落地；任何第三方观感均未因此改变。)"); yield break;
                        }

                        ToolOutcome reaction = null;
                        EffectHandler.ApplyReaction(npc, taiwu, favorDelta, happinessDelta, mor, alert,
                            r => reaction = r, stableOperationId);
                        float reactionDeadline = Time.unscaledTime + 16f;
                        while (reaction == null && Time.unscaledTime < reactionDeadline && TurnToolDispatchAllowed(ct)) yield return null;
                        if (!TurnToolDispatchAllowed(ct))
                        {
                            _reactionState = ReactionCommitState.Unknown;
                            onResult("(JHYL_ACTION_UNCONFIRMED：本轮已取消，反应回执未确认；不得假称已落地或重复派发。)"); yield break;
                        }
                        if (reaction == null || reaction.IsUnconfirmed
                            || string.Equals(reaction.Status, "unknown", StringComparison.Ordinal))
                        {
                            _reactionState = ReactionCommitState.Unknown;
                            onResult("(JHYL_ACTION_UNCONFIRMED：反应已派发但权威回执暂不可得；不得声称数值已经改变，也不得重复派发。operation_id="
                                + (reaction?.OperationId ?? "unknown") + ")");
                            yield break;
                        }
                        if (!reaction.IsSucceeded)
                        {
                            _reactionState = ReactionCommitState.Failed;
                            onResult("(反应未能落地：" + (reaction.Code ?? "failed") + " " + (reaction.Message ?? "")
                                + "。本次已确定没有成功，可安全生成一笔新的反应重试；别假称已经改变。)");
                            yield break;
                        }
                        _reactionState = ReactionCommitState.Succeeded;
                        _favorDeltaThisTurn += favorDelta;
                        onResult("(你对太吾本人的即时反应已权威落地；这不代表任何第三方观感改变。operation_id="
                            + (reaction.OperationId ?? "") + ")");
                        yield break;
                    }
                case "remember":
                    {
                        string content = S("content");
                        if (string.IsNullOrWhiteSpace(content) || _mem == null)
                        {
                            _lastLocalToolSemantic = LocalToolSemantic.Failed;
                            onResult("(长期记忆没有写入：内容为空或记忆存储当前不可用)");
                            yield break;
                        }
                        if (!string.IsNullOrWhiteSpace(content) && _mem != null)
                        {
                            var me = MemoryTrustPolicy.SanitizeModelMemory(new MemoryEntry
                            {
                                Content = content, Type = MapType(S("type")), Keywords = S("keywords"),
                                Importance = I("importance", 3), WorldDate = snap.CurrentDate,
                            });
                            if (GroupCtx != null && !string.IsNullOrWhiteSpace(GroupCtx.ExchangeId))
                            {
                                me.SourceKind = "group_turn_remember";
                                me.SourceId = "group-turn:" + GroupCtx.GroupId + ":" + GroupCtx.ExchangeId
                                    + ":attempt:" + GroupCtx.AttemptId + ":" + snap.NpcId + ":remember";
                            }
                            var eff = _mem.AddOrMerge(me, out bool memMerged);   // 去重:与现有近似则合并到那条
                            string newMemoryId = eff != null && (GroupCtx != null || !memMerged) ? eff.Id : null;
                            if (!string.IsNullOrEmpty(newMemoryId) && !_memIdsThisTurn.Contains(newMemoryId))
                                _memIdsThisTurn.Add(newMemoryId);
                            if (GroupCtx != null && !PrepareGroupCommitBarrier())
                            {
                                if (!memMerged && !string.IsNullOrEmpty(newMemoryId)) _mem.RemoveByIds(new[] { newMemoryId });
                                _memIdsThisTurn.Remove(newMemoryId);
                                _lastLocalToolSemantic = LocalToolSemantic.Failed;
                                onResult("(群聊记忆提交日志写入失败：本条没有落盘)");
                                yield break;
                            }
                            _mem.Prune(snap.CurrentDate);
                            bool saved = false;
                            try { saved = _mem.Save(); } catch { }
                            if (!saved)
                            {
                                if (!memMerged && !string.IsNullOrEmpty(newMemoryId))
                                {
                                    _mem.RemoveByIds(new[] { newMemoryId });
                                    _memIdsThisTurn.Remove(newMemoryId);
                                    PrepareGroupCommitBarrier();
                                }
                                _lastLocalToolSemantic = LocalToolSemantic.Failed;
                                onResult("(长期记忆写入失败：记录已被清空或文件不可可靠读取)");
                                yield break;
                            }
                        }
                        _lastLocalToolSemantic = LocalToolSemantic.Succeeded;
                        onResult("(已记入长期记忆)"); yield break;
                    }

                case "start_combat":
                    {
                        if (Remote || GroupCtx != null || snap.IsCaptive)
                        {
                            _lastLocalToolSemantic = LocalToolSemantic.Failed;
                            onResult("(约战未排定：只有与当前角色当面单聊、且对方并非被囚禁时才能进入原生战斗。请如实说明此刻不能开战，别假称已经交手。)");
                            yield break;
                        }
                        if (_pendingCombat != null)
                        {
                            _lastLocalToolSemantic = LocalToolSemantic.Succeeded;
                            onResult("(本轮已经排定一次交手，勿重复调用；先把最后一句话说完，等太吾确认。)");
                            yield break;
                        }
                        string opponent = (S("opponent") ?? "").Trim().ToLowerInvariant();
                        if (opponent != "taiwu" && opponent != "太吾" && opponent != "玩家")
                        {
                            _lastLocalToolSemantic = LocalToolSemantic.Failed;
                            onResult("(约战未排定：本工具的交手双方固定为你与当前太吾，不能用于你与第三方的决斗。请据实改口，且不要弹出与太吾交手的确认。)");
                            yield break;
                        }
                        string mode = (S("mode") ?? "").Trim().ToLowerInvariant();
                        if (mode == "切磋" || mode == "spar") mode = "play";
                        else if (mode == "相搏" || mode == "fight") mode = "beat";
                        else if (mode == "死斗" || mode == "death") mode = "die";
                        if (mode != "play" && mode != "beat" && mode != "die")
                        {
                            _lastLocalToolSemantic = LocalToolSemantic.Failed;
                            onResult("(约战未排定：mode 只能是 play、beat 或 die。改用其中一个重试，别假称已经开战。)");
                            yield break;
                        }
                        string initiator = (S("initiator") ?? "").Trim().ToLowerInvariant();
                        if (initiator == "太吾" || initiator == "player") initiator = "taiwu";
                        else if (initiator == "我" || initiator == "你" || initiator == "角色") initiator = "npc";
                        if (initiator != "taiwu" && initiator != "npc")
                        {
                            _lastLocalToolSemantic = LocalToolSemantic.Failed;
                            onResult("(约战未排定：initiator 只能是 taiwu 或 npc。改正后重试。)");
                            yield break;
                        }
                        short config = mode == "play" ? (short)0 : (mode == "beat" ? (short)1 : (short)2);
                        string reason = (S("reason") ?? "").Trim(); if (reason.Length > 160) reason = reason.Substring(0, 160) + "…";
                        _pendingCombat = new PendingCombatRequest
                        {
                            OperationId = OperationId.New(),
                            WorldId = WorldLifecycle.WorldId,
                            TaiwuId = taiwu,
                            NpcId = npc,
                            NpcName = snap.Name,
                            CombatConfig = config,
                            Mode = mode,
                            Initiator = initiator,
                            Reason = reason,
                            WorldGeneration = WorldLifecycle.Generation,
                        };
                        string zh = mode == "play" ? "切磋" : (mode == "beat" ? "相搏" : "生死斗");
                        if (_landedThisTurn != null) _landedThisTurn.Add("与太吾约定立刻" + zh + "（待太吾确认入场）");
                        _lastLocalToolSemantic = LocalToolSemantic.Succeeded;
                        onResult("(已与太吾排定立刻" + zh + "；此刻尚未开战。请先用角色口吻说完最后一句，随后界面会让太吾明确确认，点击后才进入游戏原生战斗。不要编造胜负或说已经打完。)");
                        yield break;
                    }

                // —— 馈赠/教学 ——
                case "gift":
                    {
                        string type = (S("type") ?? "").Trim().ToLowerInvariant();
                        if (type != "item" && type != "resources" && type != "silver")
                        {
                            onResult("(赠予类型无效：type 只能是 item、resources 或 silver。别假称已经赠出。)");
                            yield break;
                        }
                        int gRid = 0; string gWho = (S("target") ?? "").Trim();
                        if (gWho.Length > 0)
                        {
                            int[] rid = { int.MinValue };
                            EffectHandler.ResolveChar(npc, gWho, c => rid[0] = c);
                            float rdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                            while (rid[0] == int.MinValue && Time.unscaledTime < rdl) yield return null;
                            gRid = rid[0] == int.MinValue ? 0 : rid[0];
                            if (gRid == taiwu) { gRid = 0; gWho = null; }
                            else if (gRid <= 0 || gRid == npc) { onResult("(没找到「" + gWho + "」这个人(须你相识/同队/太吾这次提及),或那是你自己——赠不成。要赠太吾就省略 target。)"); yield break; }
                        }
                        else gWho = null;
                        if (type == "silver") { string[] sMsg = { null }; yield return GiveSilverAndNotice(npc, taiwu, Math.Max(1, I("amount", 1)), m => sMsg[0] = m, gRid, gWho, stableOperationId); onResult(sMsg[0] ?? "(赠银钱结果未确认,别假称已给。)"); yield break; }
                        else
                        {
                            int amt = (type == "resources") ? Math.Max(1, I("amount", 1)) : 1;   // 物品一次一件;资源成批——后端已按实有封顶,前端不再截 20
                            string[] giftMsg = { null };
                            string requestedItem = S("name");
                            // 主动来信没有玩家本轮明确索要装备的表达，绝不能自行卸装相赠。
                            bool allowEquipped = !_npcInitiatedTurn && B("allow_equipped");
                            yield return GiveItemAndNotice(snap, requestedItem, amt,
                                m => giftMsg[0] = m, gRid, gWho, stableOperationId,
                                allowEquipped);
                            onResult(giftMsg[0] ?? "(已尝试把财物相赠,详见对话下方提示)");
                        }
                        yield break;
                    }
                case "barter":
                    {
                        string aItem = ItemNameMatcher.StripCountSuffix((S("item_a") ?? "").Trim());
                        string bItem = ItemNameMatcher.StripCountSuffix((S("item_b") ?? "").Trim());
                        if (aItem.Length == 0 || bItem.Length == 0)
                        {
                            onResult("(没换成:未说清双方要交换哪两样东西。先 query_person_items 查双方真实随身物,再把清单里的确切名原样填入 barter。别假称已换。)");
                            yield break;
                        }
                        int aId = 0, bId = 0; string aName = null, bName = null, aReason = null, bReason = null;
                        yield return ResolveToolPerson(npc, taiwu, S("person_a"), npc, "你", snap.Name, true, (id, label, reason) => { aId = id; aName = label; aReason = reason; });
                        if (aId <= 0)
                        {
                            onResult(ResolveFailMsg(S("person_a"), aReason, false));
                            yield break;
                        }
                        yield return ResolveToolPerson(npc, taiwu, S("person_b"), taiwu, "太吾", snap.Name, true, (id, label, reason) => { bId = id; bName = label; bReason = reason; });
                        if (bId <= 0)
                        {
                            onResult(ResolveFailMsg(S("person_b"), bReason, false));
                            yield break;
                        }
                        if (_npcInitiatedTurn && (aId == taiwu || bId == taiwu))
                        {
                            onResult("(未执行：太吾本轮没有先行同意这笔交换。你可以在主动来信中提出交换条件，等太吾回应后再成交；与合规第三方的自主交换不受影响。)");
                            yield break;
                        }
                        if (aId == bId)
                        {
                            onResult("(没换成:交换双方是同一人。以物换物必须是两个不同的人,别假称已换。)");
                            yield break;
                        }
                        int wantA = Math.Max(1, I("amount_a", 1));
                        int wantB = Math.Max(1, I("amount_b", 1));
                        int[] code = { -1 }, gotA = { 0 }, gotB = { 0 };
                        string[] msg = { null }, realA = { null }, realB = { null };
                        Debug.Log("[江湖有灵] barter 请求 actor_a=" + aId + " actor_b=" + bId
                            + " amount_a=" + wantA + " amount_b=" + wantB
                            + " item_a_chars=" + aItem.Length + " item_b_chars=" + bItem.Length);
                        EffectHandler.ApplyBarter(aId, bId, aItem, wantA, bItem, wantB, (ok, aa, ba, an, bn, m) =>
                        {
                            code[0] = ok ? 1 : 0; gotA[0] = aa; gotB[0] = ba; realA[0] = an; realB[0] = bn; msg[0] = m;
                            Debug.Log("[江湖有灵] barter 回执 ok=" + ok + " a_amount=" + aa + " b_amount=" + ba
                                + " result_chars=" + (m?.Length ?? 0));
                            if (ok)
                            {
                                string la = string.IsNullOrWhiteSpace(an) ? aItem : an;
                                string lb = string.IsNullOrWhiteSpace(bn) ? bItem : bn;
                                Emit("以物换物:" + (aName ?? "甲方") + "给出" + la + (aa > 1 ? "×" + aa : "") + "；" + (bName ?? "乙方") + "给出" + lb + (ba > 1 ? "×" + ba : ""), null, null, true);
                            }
                            else Emit("以物换物未成:" + (m ?? "条件不符"), null, null, false);
                        }, stableOperationId);
                        float bdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (code[0] == -1 && Time.unscaledTime < bdl) yield return null;
                        if (code[0] == -1) Debug.Log("[江湖有灵] barter 回执超时 actor_a=" + aId
                            + " actor_b=" + bId + " item_a_chars=" + aItem.Length + " item_b_chars=" + bItem.Length);
                        string ra = string.IsNullOrWhiteSpace(realA[0]) ? aItem : realA[0];
                        string rb = string.IsNullOrWhiteSpace(realB[0]) ? bItem : realB[0];
                        onResult(ActMsg(code[0],
                            "(已以物换物:" + (aName ?? "甲方") + "给出「" + ra + "」" + (gotA[0] > 1 ? ("×" + gotA[0]) : "") + "," + (bName ?? "乙方") + "给出「" + rb + "」" + (gotB[0] > 1 ? ("×" + gotB[0]) : "") + "。可据实收尾。)",
                            "(没换成:" + (msg[0] ?? "双方持有物不符") + "。先 query_person_items 查双方真实随身物,从清单原样重填;别假称已换。)"));
                        yield break;
                    }
                case "steal":
                    {
                        string item = ItemNameMatcher.StripCountSuffix((S("item") ?? "").Trim());
                        if (item.Length == 0)
                        {
                            onResult("(没偷成:未说清要偷哪样东西。先 query_person_items 查被偷者真实随身物,再把清单里的确切名原样填入 steal。别假称已偷。)");
                            yield break;
                        }
                        int thiefId = npc, victimId = 0; string thiefName = snap.Name, victimName = null, thiefReason = null, victimReason = null;
                        string requestedThief = (S("thief") ?? "").Trim();
                        if (requestedThief.Length > 0)
                        {
                            int resolvedThief = 0; string resolvedName = null;
                            yield return ResolveToolPerson(npc, taiwu, requestedThief, npc, "你", snap.Name, true,
                                (id, label, reason) => { resolvedThief = id; resolvedName = label; thiefReason = reason; });
                            if (resolvedThief <= 0) { onResult(ResolveFailMsg(requestedThief, thiefReason, true)); yield break; }
                            if (resolvedThief != npc)
                            {
                                onResult("(没偷成:对话偷窃只能由当前 NPC 自己发起；太吾不能借语言命令直接偷 NPC，也不能指定第三方代偷。别假称已偷。)");
                                yield break;
                            }
                            thiefName = string.IsNullOrWhiteSpace(resolvedName) ? snap.Name : resolvedName;
                        }
                        yield return ResolveToolPerson(npc, taiwu, S("victim"), taiwu, "太吾", snap.Name, true, (id, label, reason) => { victimId = id; victimName = label; victimReason = reason; });
                        if (victimId <= 0) { onResult(ResolveFailMsg(S("victim"), victimReason, true)); yield break; }
                        if (thiefId == victimId)
                        {
                            onResult("(没偷成:偷窃双方是同一人。别假称已偷。)");
                            yield break;
                        }
                        int want = Math.Max(1, I("amount", 1));
                        int[] sr = { -1 }, got = { 0 }, chance = { 0 }; bool[] detected = { false };
                        string[] msg = { null }, real = { null };
                        Debug.Log("[江湖有灵] steal 请求 thief=" + thiefId + " victim=" + victimId
                            + " amount=" + want + " item_chars=" + item.Length);
                        EffectHandler.ApplySteal(thiefId, victimId, item, want, (ok, amount, name2, m, det, ch) =>
                        {
                            sr[0] = ok ? 1 : 0; got[0] = amount; real[0] = name2; msg[0] = m; detected[0] = det; chance[0] = ch;
                            if (ok) Emit((thiefName ?? "有人") + "从" + (victimName ?? "对方") + "处盗得" + (name2 ?? item) + (amount > 1 ? "×" + amount : ""), null, null, true);
                            else Emit("偷窃未成:" + (m ?? "被发现"), null, null, false);
                        }, stableOperationId);
                        float sdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (sr[0] == -1 && Time.unscaledTime < sdl) yield return null;
                        string rname = string.IsNullOrWhiteSpace(real[0]) ? item : real[0];
                        onResult(ActMsg(sr[0],
                            "(已偷得:" + (thiefName ?? "偷者") + "从" + (victimName ?? "被偷者") + "处得「" + rname + "」" + (got[0] > 1 ? ("×" + got[0]) : "") + "。据实收尾。)",
                            "(没偷成:" + (msg[0] ?? "对方没有此物或偷窃败露") + (detected[0] ? ",已被发现且好感下降" : "") + (chance[0] > 0 ? ",本次机会约" + chance[0] + "%" : "") + "。别假称已偷到。)"));
                        yield break;
                    }
                case "teach":
                    {
                        // 武学/技艺合一接口:AI 不必分清(它常分不清)。按 name 在两清单各查一遍,自动判定该传哪种;
                        // type 仅作"两边同名"时的倾向参考。target 省略=传太吾;填第三方姓名=传给那人。
                        string nm = (S("name") ?? "").Trim();
                        string hint = (S("type") ?? "").ToLowerInvariant();
                        int tRid = 0; string tWho = (S("target") ?? "").Trim();
                        if (tWho.Length > 0)
                        {
                            int[] rid = { int.MinValue };
                            EffectHandler.ResolveChar(npc, tWho, c => rid[0] = c);
                            float rdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                            while (rid[0] == int.MinValue && Time.unscaledTime < rdl) yield return null;
                            tRid = rid[0] == int.MinValue ? 0 : rid[0];
                            if (tRid == taiwu) { tRid = 0; tWho = null; }
                            else if (tRid <= 0 || tRid == npc) { onResult("(没找到「" + tWho + "」这个人,或那是你自己——传不成。要传太吾就省略 target。)"); yield break; }
                        }
                        else tWho = null;
                        bool inCombat = false, inLife = false;
                        if (nm.Length > 0 && snap != null)
                        {
                            if (snap.LearnableSkills != null) foreach (var sk in snap.LearnableSkills) if (sk != null && (sk.Name == nm || NameHit(sk.Name, nm))) { inCombat = true; break; }
                            if (snap.LearnableLifeSkills != null) foreach (var sk in snap.LearnableLifeSkills) if (sk != null && (sk.Name == nm || NameHit(sk.Name, nm))) { inLife = true; break; }
                        }
                        bool teachLife;
                        if (inLife && !inCombat) teachLife = true;        // 只在技艺清单 → 传技艺
                        else if (inCombat && !inLife) teachLife = false;  // 只在武学清单 → 传武学
                        else teachLife = (hint == "life");                // 两边同名 或 都没命中 → 依 hint
                        // teach 只用于当面亲授【第三方】。传/教太吾一律走 write_book(回忆成秘籍交他自研)——此处不再代劳,
                        // 而是回一条明确失败回执；编排以该后端 ToolOutcome 再转一轮，
                        // 让模型改调 write_book，别拿“我传你了”敷衍收场。
                        if (tRid == 0)
                        {
                            onResult("(没传成:teach 只用于把武学/技艺当面亲授【在场的第三方某人】——要教/传给太吾本人,请改调 write_book 把这门『" + (nm.Length > 0 ? nm : (teachLife ? "技艺" : "武学")) + "』回忆成秘籍交他自研。)"); yield break;
                        }
                        string[] teachMsg = { null };
                        if (teachLife) { yield return TeachLifeAndNotice(snap, nm, m => teachMsg[0] = m, tRid, tWho, stableOperationId); onResult(teachMsg[0] ?? "(已尝试传授技艺,详见对话下方提示)"); yield break; }
                        yield return TeachAndNotice(snap, nm, m => teachMsg[0] = m, tRid, tWho, stableOperationId);
                        onResult(teachMsg[0] ?? "(已尝试传授武学,详见对话下方提示)"); yield break;
                    }
                case "write_book":
                    {
                        // 武学/技艺合一:按书名在你的武学/技艺两清单自动判定该回忆哪种。target 省略=赠太吾;填第三方=赠那人。
                        string wbName = (S("name") ?? "").Trim();
                        string wbHint = (S("type") ?? "").ToLowerInvariant();
                        int wRid = 0; string wWho = (S("target") ?? "").Trim();
                        if (wWho.Length > 0)
                        {
                            int[] rid = { int.MinValue };
                            EffectHandler.ResolveChar(npc, wWho, c => rid[0] = c);
                            float rdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                            while (rid[0] == int.MinValue && Time.unscaledTime < rdl) yield return null;
                            wRid = rid[0] == int.MinValue ? 0 : rid[0];
                            if (wRid == taiwu) { wRid = 0; wWho = null; }
                            else if (wRid == npc)
                            {
                                onResult("(未执行：write_book 表示当前人物写书，不能把书赠给自己。"
                                    + "若是太吾亲自写书相赠，请改用 taiwu_write_book；不要根据玩家句式静默反转行动方向。)");
                                yield break;
                            }
                            else if (wRid <= 0) { onResult("(没找到「" + wWho + "」这个人——赠不成。要赠太吾就省略 target。)"); yield break; }
                        }
                        else wWho = null;
                        bool wbC = false, wbL = false;
                        if (wbName.Length > 0 && snap != null)
                        {
                            if (snap.LearnableSkills != null) foreach (var sk in snap.LearnableSkills) if (sk != null && (sk.Name == wbName || NameHit(sk.Name, wbName))) { wbC = true; break; }
                            if (snap.LearnableLifeSkills != null) foreach (var sk in snap.LearnableLifeSkills) if (sk != null && (sk.Name == wbName || NameHit(sk.Name, wbName))) { wbL = true; break; }
                        }
                        string kind = (wbL && !wbC) ? "life" : (wbC && !wbL) ? "combat" : (wbHint == "life" ? "life" : "combat");
                        string[] wbMsg = { null }; bool[] wbOk = { false };
                        yield return WriteBookAndNotice(snap, kind, wbName, (m, ok) => { wbMsg[0] = m; wbOk[0] = ok; }, wRid, wWho, stableOperationId);
                        onResult(wbMsg[0] ?? "(已尝试回忆成册相赠,详见对话下方提示)"); yield break;
                    }

                // —— 关系 ——
                case "set_relation":
                    {
                        string act = (S("action") ?? "").ToLowerInvariant();
                        if (_npcInitiatedTurn && act != "lover" && act != "recognize")
                        {
                            onResult("(未执行：太吾本轮没有先行同意。NPC 主动联系时只能单方面表达爱慕或归心；结交、结义、师徒、义亲、成婚和加入/离开队伍都须等太吾本人回应。)");
                            yield break;
                        }
                        int[] sr = { -1 }; string[] sm = { null }; string okZh = "落地此番情谊";
                        switch (act)
                        {
                            case "befriend": okZh = "与太吾结为挚友"; EffectHandler.ApplyRelation(npc, taiwu, "best_friend", (ok, m) => { sr[0] = ok ? 1 : 0; sm[0] = m; Emit(ok ? "与太吾结为挚友" : ("未能结交:" + (m ?? "")), null, null, ok); }, stableOperationId); break;
                            case "swear_sibling": case "sworn_sibling": okZh = "与太吾义结金兰"; EffectHandler.ApplyRelation(npc, taiwu, "sworn_sibling", (ok, m) => { sr[0] = ok ? 1 : 0; sm[0] = m; Emit(ok ? "与太吾义结金兰" : ("未能结义:" + (m ?? "")), null, null, ok); }, stableOperationId); break;
                            case "mentor": case "apprentice": okZh = "拜太吾为师"; EffectHandler.ApplyRelation(npc, taiwu, "apprentice", (ok, m) => { sr[0] = ok ? 1 : 0; sm[0] = m; Emit(ok ? "拜太吾为师" : ("未能拜师:" + (m ?? "")), null, null, ok); }, stableOperationId); break;
                            case "take_disciple": okZh = "收太吾为徒"; EffectHandler.ApplyRelation(npc, taiwu, "take_disciple", (ok, m) => { sr[0] = ok ? 1 : 0; sm[0] = m; Emit(ok ? "收太吾为徒" : ("未能收徒:" + (m ?? "")), null, null, ok); }, stableOperationId); break;
                            case "adoptive_parent": okZh = "认太吾为义父母"; EffectHandler.ApplyRelation(npc, taiwu, "adoptive_parent", (ok, m) => { sr[0] = ok ? 1 : 0; sm[0] = m; Emit(ok ? (m ?? "认太吾为义父母") : ("未能认亲:" + (m ?? "")), null, null, ok); }, stableOperationId); break;
                            case "adoptive_child": okZh = "收太吾为义子女"; EffectHandler.ApplyRelation(npc, taiwu, "adoptive_child", (ok, m) => { sr[0] = ok ? 1 : 0; sm[0] = m; Emit(ok ? (m ?? "收太吾为义子女") : ("未能收养:" + (m ?? "")), null, null, ok); }, stableOperationId); break;
                            case "lover": okZh = "向太吾表达爱慕"; EffectHandler.ApplyRelation(npc, taiwu, "lover", (ok, m) => { sr[0] = ok ? 1 : 0; sm[0] = m; Emit(ok ? (m ?? "已向太吾表达爱慕") : ("未能表达爱慕:" + (m ?? "")), null, null, ok); }, stableOperationId); break;
                            case "spouse": okZh = "与太吾结为夫妻"; EffectHandler.ApplyRelation(npc, taiwu, "spouse", (ok, m) => { sr[0] = ok ? 1 : 0; sm[0] = m; Emit(ok ? "与太吾结为夫妻" : ("未能成婚:" + (m ?? "")), null, null, ok); }, stableOperationId); break;
                            case "recognize": okZh = "归心太吾"; EffectHandler.ApplyRecognizeTaiwu(npc, (ok, m) => { sr[0] = ok ? 1 : 0; sm[0] = m; Emit(ok ? "已归心太吾" : ("未能归心:" + (m ?? "")), null, null, ok); }, stableOperationId); break;
                            case "follow": okZh = "加入太吾的队伍"; EffectHandler.ApplyFollow(npc, true, (ok, m) => { sr[0] = ok ? 1 : 0; sm[0] = m; Emit(ok ? "加入了太吾的队伍" : ("未能入队:" + (m ?? "")), null, null, ok); }, stableOperationId); break;
                            case "leave": okZh = "离开太吾的队伍"; EffectHandler.ApplyFollow(npc, false, (ok, m) => { sr[0] = ok ? 1 : 0; sm[0] = m; Emit(ok ? "离开了太吾的队伍" : ("未能离队:" + (m ?? "")), null, null, ok); }, stableOperationId); break;
                            default: onResult("(关系动作无法识别)"); yield break;
                        }
                        float sdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (sr[0] == -1 && Time.unscaledTime < sdl) yield return null;
                        if (sr[0] == 1)
                            onResult("(" + ((act == "lover" || act == "adoptive_parent" || act == "adoptive_child") && !string.IsNullOrWhiteSpace(sm[0])
                                ? sm[0] : "已" + okZh)
                                + ",照人设在回话里自然收尾即可;切勿向太吾解释系统/工具/动作名。)");
                        else if (sr[0] == 0)
                            onResult("(没能" + okZh + ":" + (sm[0] ?? "条件不合——多半是已是此关系、或有血亲不可缔结、或对方另有配偶") + "。用角色自己的话就事论事告诉太吾,别假称已成;绝不要提及工具、系统、action 或调用细节。)");
                        else
                            onResult(ActMsg(-1, null, null));
                        yield break;
                    }
                case "spend_night":
                    {
                        int[] sn = { -1 }; string[] snm = { null };
                        EffectHandler.ApplySpendNight(taiwu, npc, (ok, m) => { sn[0] = ok ? 1 : 0; snm[0] = m; Emit(ok ? "与太吾共度春宵" : ("未能共度春宵:" + (m ?? "")), null, null, ok); }, stableOperationId);
                        float sndl = Time.unscaledTime + RpcReceiptWaitSeconds; while (sn[0] == -1 && Time.unscaledTime < sndl) yield return null;
                        onResult(ActMsg(sn[0], "(已与太吾共度春宵,可在回话里含蓄收尾。)",
                            "(未能共度春宵:" + (snm[0] ?? "条件不合——多半是既非恋人夫妻、彼此也未到两情相悦、或未成年") + "。把缘由含蓄告知太吾,别假称已成。)"));
                        yield break;
                    }
                case "dissolve_relation":
                    {
                        string rel = (S("relation") ?? "").ToLowerInvariant();
                        if (rel == "befriend") rel = "friend";   // 容错:模型常沿用 relate_npc 的 befriend 命名
                        if (rel == "swear_sibling") rel = "sworn";
                        if (rel == "enemy" || rel == "feud" || rel == "仇" || rel == "仇敌")
                        { onResult("(『放下仇怨/化解仇敌』不走本工具——改用 set_enmity、action=reconcile、target 填对方姓名。)"); yield break; }
                        if (rel != "friend" && rel != "sworn" && rel != "mentor"
                            && rel != "adoptive_parent" && rel != "adoptive_child"
                            && rel != "lover" && rel != "spouse")
                        { onResult("(relation 只能填 friend/sworn/mentor/adoptive_parent/adoptive_child/lover/spouse 之一；义亲按你看对方的辈分填写，放下仇怨用 set_enmity reconcile。)"); yield break; }
                        string to = S("target");
                        int[] dr = { -1 }; string[] drm = { null };
                        Action<bool, string> cb = (ok, m) => { dr[0] = ok ? 1 : 0; drm[0] = m; Emit(ok ? "已断绝此段关系" : ("未能解除:" + (m ?? "")), null, null, ok); };
                        if (string.IsNullOrWhiteSpace(to) || to == "太吾")
                            EffectHandler.ApplyDissolveRelation(npc, taiwu, rel, taiwu, cb, stableOperationId);
                        else
                        {
                            int[] dcid = { int.MinValue }; string[] drsn = { null };
                            EffectHandler.ResolveChar(npc, to, false, (c, rsn) => { dcid[0] = c; drsn[0] = rsn; });
                            float ddl = Time.unscaledTime + RpcReceiptWaitSeconds;
                            while (dcid[0] == int.MinValue && Time.unscaledTime < ddl) yield return null;
                            if (dcid[0] <= 0 || dcid[0] == npc) { onResult(ResolveFailMsg(to, drsn[0], false)); yield break; }
                            EffectHandler.ApplyDissolveRelation(npc, taiwu, rel, dcid[0], cb, stableOperationId);
                        }
                        float drdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (dr[0] == -1 && Time.unscaledTime < drdl) yield return null;
                        onResult(ActMsg(dr[0], "(已断绝此段关系,详见对话下方提示)", "(没能解除:" + (drm[0] ?? "你与对方本无此关系") + "。别假称已断。)"));
                        yield break;
                    }
                case "matchmake":
                    {
                        int[] mm = { -1 }; string[] mmm = { null };
                        ResolveAndMatchmake(npc, taiwu, S("partner"), (ok, m) => { mm[0] = ok ? 1 : 0; mmm[0] = m; }, stableOperationId);
                        float mmdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (mm[0] == -1 && Time.unscaledTime < mmdl) yield return null;
                        onResult(ActMsg(mm[0], "(已为你撮合成一段姻缘,详见对话下方提示)", "(没能做成这桩媒:" + (mmm[0] ?? "对方不合适、或已有婚配、或太吾不认识此人") + "。别假称已成。)"));
                        yield break;
                    }
                case "relate_npc":
                    {
                        string act = (S("action") ?? "").ToLowerInvariant();
                        if (act != "befriend" && act != "sworn" && act != "mentor"
                            && act != "adoptive_parent" && act != "adoptive_child"
                            && act != "lover" && act != "spouse")
                        { onResult("(action 只能填 befriend/sworn/mentor/adoptive_parent/adoptive_child/lover/spouse。)"); yield break; }
                        string tgt = (S("target") ?? "").Trim();
                        if (tgt.Length == 0) { onResult("(没说与谁结这段关系——填那人确切姓名。)"); yield break; }
                        if (tgt == "太吾" || tgt.Equals("taiwu", StringComparison.OrdinalIgnoreCase)) { onResult("(与太吾本人结这些请用 set_relation,不是本工具。)"); yield break; }
                        int[] cid = { int.MinValue }; string[] crsn2 = { null };
                        EffectHandler.ResolveChar(npc, tgt, true, (c, rsn) => { cid[0] = c; crsn2[0] = rsn; });   // 同处一地的人也可缔结(两陌路人亦能结义/缔婚,引擎不要求先相识)
                        float rdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (cid[0] == int.MinValue && Time.unscaledTime < rdl) yield return null;
                        if (cid[0] <= 0 || cid[0] == npc || cid[0] == taiwu)
                        { onResult(ResolveFailMsg(tgt, crsn2[0], false)); yield break; }
                        string zh = act == "befriend" ? "结为挚友" : act == "sworn" ? "义结金兰"
                            : act == "mentor" ? "收为徒弟" : act == "adoptive_parent" ? "认作义父母"
                            : act == "adoptive_child" ? "收为义子女" : act == "lover" ? "表达爱慕" : "结为夫妻";
                        int[] rr = { -1 }; string[] rmsg = { null };
                        EffectHandler.ApplyRelateNpc(npc, cid[0], act, (ok, m) => { rr[0] = ok ? 1 : 0; rmsg[0] = m; }, stableOperationId);
                        float rdl2 = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (rr[0] == -1 && Time.unscaledTime < rdl2) yield return null;
                        string landed = (act == "lover" || act == "adoptive_parent" || act == "adoptive_child")
                            && !string.IsNullOrWhiteSpace(rmsg[0])
                            ? rmsg[0] : "与" + tgt + zh;
                        if (rr[0] == 1) Emit(landed, null, null, true);
                        onResult(ActMsg(rr[0], "(" + landed + ",详见对话下方提示)", "(未能与" + tgt + zh + ":" + (rmsg[0] ?? "条件不合或已是此关系") + "。别假称已结。)"));
                        yield break;
                    }
                case "set_enmity":
                    {
                        string act = (S("action") ?? "").ToLowerInvariant();
                        string tgt = (S("target") ?? "太吾").Trim();
                        if (act != "feud" && act != "reconcile") { onResult("(action 只能填 feud=结仇 或 reconcile=放下旧怨。)"); yield break; }
                        bool makeEnemy = act == "feud";
                        int enemyCid;
                        if (tgt.Length == 0 || tgt == "太吾" || tgt.Equals("taiwu", StringComparison.OrdinalIgnoreCase)) { enemyCid = taiwu; tgt = "太吾"; }
                        else
                        {
                            int[] ecid = { int.MinValue }; string[] ersn = { null };
                            EffectHandler.ResolveChar(npc, tgt, makeEnemy, (c, rsn) => { ecid[0] = c; ersn[0] = rsn; });   // 结仇=敌对动作放开同块兜底;化解只在已有关系里找
                            float edl = Time.unscaledTime + RpcReceiptWaitSeconds;
                            while (ecid[0] == int.MinValue && Time.unscaledTime < edl) yield return null;
                            if (ecid[0] <= 0 || ecid[0] == npc)
                            { onResult(ResolveFailMsg(tgt, ersn[0], makeEnemy)); yield break; }
                            enemyCid = ecid[0];
                        }
                        int[] er = { -1 }; string[] em = { null };
                        EffectHandler.ApplyEnmity(npc, enemyCid, makeEnemy, (ok, m) => { er[0] = ok ? 1 : 0; em[0] = m; Emit(ok ? ("与" + tgt + (makeEnemy ? "结下仇怨" : "化解了仇怨")) : ("未能" + (makeEnemy ? "结仇" : "化解") + ":" + (m ?? "")), null, null, ok); }, stableOperationId);
                        float erdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (er[0] == -1 && Time.unscaledTime < erdl) yield return null;
                        onResult(ActMsg(er[0], makeEnemy ? ("(已与" + tgt + "结下仇怨,详见对话下方提示)") : ("(已放下与" + tgt + "的旧怨,详见对话下方提示)"),
                            "(没能" + (makeEnemy ? "结仇" : "化解") + ":" + (em[0] ?? "对方角色或已失效") + "。别假称已做。)"));
                        yield break;
                    }
                case "kill":
                    {
                        string tgt = (S("target") ?? "").Trim();
                        if (tgt.Length == 0) { onResult("(要取谁的性命?填上姓名。)"); yield break; }
                        if (tgt == "太吾" || tgt.Equals("taiwu", StringComparison.OrdinalIgnoreCase)) { onResult("(不可加害太吾。)"); yield break; }
                        int[] kc = { int.MinValue }; string[] krsn = { null };
                        EffectHandler.ResolveChar(npc, tgt, true, (c, rsn) => { kc[0] = c; krsn[0] = rsn; });   // 敌对动作:放开同块陌生人兜底
                        float kdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (kc[0] == int.MinValue && Time.unscaledTime < kdl) yield return null;
                        if (kc[0] <= 0 || kc[0] == npc) { onResult(ResolveFailMsg(tgt, krsn[0], true)); yield break; }
                        int[] kr = { -1 }; string[] km = { null };
                        EffectHandler.ApplyKillDetailed(npc, kc[0], (ok, m, lootName) =>
                        {
                            kr[0] = ok ? 1 : 0; km[0] = m;
                            Emit(ok ? ("取了" + tgt + "的性命"
                                    + (string.IsNullOrWhiteSpace(lootName) ? "" : ("，并夺得「" + lootName + "」")))
                                : ("未能下手:" + (m ?? "")), null, null, ok);
                        }, stableOperationId);
                        float krdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (kr[0] == -1 && Time.unscaledTime < krdl) yield return null;
                        onResult(ActMsg(kr[0], "(已取" + tgt + "性命,详见对话下方提示。)", "(没能下手:" + (km[0] ?? "或你精纯不及对方/对方已失效") + "。别假称已做。)"));
                        yield break;
                    }
                case "capture":
                    {
                        string tgt = (S("target") ?? "").Trim();
                        if (tgt.Length == 0) { onResult("(要擒拿谁?填上姓名。)"); yield break; }
                        if (tgt == "太吾" || tgt.Equals("taiwu", StringComparison.OrdinalIgnoreCase)) { onResult("(不可擒拿太吾。)"); yield break; }
                        int[] cc = { int.MinValue }; string[] crsn = { null };
                        EffectHandler.ResolveChar(npc, tgt, true, (c, rsn) => { cc[0] = c; crsn[0] = rsn; });   // 敌对动作:放开同块陌生人兜底
                        float cdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (cc[0] == int.MinValue && Time.unscaledTime < cdl) yield return null;
                        if (cc[0] <= 0 || cc[0] == npc) { onResult(ResolveFailMsg(tgt, crsn[0], true)); yield break; }
                        int[] cr = { -1 }; string[] cm = { null };
                        EffectHandler.ApplyCapture(npc, cc[0], (ok, m) => { cr[0] = ok ? 1 : 0; cm[0] = m; Emit(ok ? ("将" + tgt + "绑缚擒下") : ("未能擒拿:" + (m ?? "")), null, null, ok); }, stableOperationId);
                        float crdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (cr[0] == -1 && Time.unscaledTime < crdl) yield return null;
                        onResult(ActMsg(cr[0], "(已将" + tgt + "擒下,详见对话下方提示。)", "(没能擒拿:" + (cm[0] ?? "或你精纯不及对方/无绳子/对方已失效") + "。别假称已做。)"));
                        yield break;
                    }
                case "poison":
                    {
                        string tgt = (S("target") ?? "").Trim();
                        if (tgt.Length == 0) { onResult("(要对谁下毒?填上姓名。)"); yield break; }
                        int[] pc = { 0 }; string[] prsn = { null }, plabel = { null };
                        yield return ResolveToolPerson(npc, taiwu, tgt, 0, tgt, snap.Name, true, (id, label, reason) => { pc[0] = id; plabel[0] = label; prsn[0] = reason; });
                        if (pc[0] <= 0 || pc[0] == npc) { onResult(ResolveFailMsg(tgt, prsn[0], true)); yield break; }
                        int[] pr = { -1 }; string[] pm = { null };
                        string pwho = string.IsNullOrWhiteSpace(plabel[0]) ? tgt : plabel[0];
                        EffectHandler.ApplyPoison(npc, pc[0], (ok, m) => { pr[0] = ok ? 1 : 0; pm[0] = m; Emit(ok ? ("对" + pwho + "暗下了" + (m ?? "毒")) : ("未能下毒:" + (m ?? "")), null, null, ok); }, stableOperationId);
                        float prdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (pr[0] == -1 && Time.unscaledTime < prdl) yield return null;
                        onResult(ActMsg(pr[0], "(已对" + pwho + "下" + (pm[0] ?? "毒") + ",详见对话下方提示。)", "(没能下毒:" + (pm[0] ?? "或你手边没有毒药/对方已失效") + "。别假称已做。)"));
                        yield break;
                    }
                case "heal":
                    {
                        string tgt = (S("target") ?? "").Trim();
                        int healTarget; string healWho;
                        if (tgt.Length == 0 || tgt == "太吾" || tgt.Equals("taiwu", StringComparison.OrdinalIgnoreCase)) { healTarget = taiwu; healWho = "太吾"; }
                        else
                        {
                            int[] hc = { int.MinValue }; string[] hrsn = { null };
                            EffectHandler.ResolveChar(npc, tgt, true, (c, rsn) => { hc[0] = c; hrsn[0] = rsn; });   // 疗伤是善举:同处一地的伤者也能救
                            float hdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                            while (hc[0] == int.MinValue && Time.unscaledTime < hdl) yield return null;
                            if (hc[0] <= 0) { onResult(ResolveFailMsg(tgt, hrsn[0], false)); yield break; }
                            healTarget = hc[0]; healWho = tgt;
                        }
                        int[] hr = { -1 }; string[] hm = { null };
                        ApplyHealAfterAuthoritativePreflight(npc, healTarget, (ok, m) => { hr[0] = ok ? 1 : 0; hm[0] = m; Emit(ok ? ("为" + healWho + "疗了伤") : (m ?? "未执行：未能确认可治疗状态"), null, null, ok); }, stableOperationId);
                        float hrdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (hr[0] == -1 && Time.unscaledTime < hrdl) yield return null;
                        if (hr[0] == 0 && !string.IsNullOrWhiteSpace(hm[0])
                            && hm[0].StartsWith("未执行：", StringComparison.Ordinal))
                        {
                            onResult("(" + hm[0] + "。这不是已经发生的失败行动，请据此换方案。)");
                            yield break;
                        }
                        onResult(ActMsg(hr[0], "(已为" + healWho + "疗伤,详见对话下方提示。)", "(没能疗伤:" + (hm[0] ?? "对方已失效") + "。别假称已做。)"));
                        yield break;
                    }
                case "detox":
                case "regulate_breath":
                    {
                        string tgt = (S("target") ?? "").Trim();
                        int target = 0; string who = null, reason = null;
                        yield return ResolveToolPerson(npc, taiwu, tgt, taiwu, "太吾",
                            snap.Name, true, (id, label, why) =>
                            { target = id; who = id == taiwu ? "太吾" : label; reason = why; });
                        if (target <= 0)
                        { onResult(ResolveFailMsg(tgt, reason, false)); yield break; }
                        if (target == npc)
                        {
                            onResult("(未执行：本工具只能由你为另一名人物施行，不能给自己"
                                + (name == "detox" ? "驱毒" : "调息") + "。)");
                            yield break;
                        }
                        if (string.IsNullOrWhiteSpace(who)) who = "人物#" + target;
                        int[] state = { -1 }; string[] message = { null };
                        ApplyMedicalCareAfterAuthoritativePreflight(name, npc, target,
                            (ok, m) =>
                            {
                                state[0] = ok ? 1 : 0; message[0] = m;
                                string action = name == "detox" ? "驱毒" : "调息";
                                Emit(ok ? ("为" + who + "完成" + action
                                        + (string.IsNullOrWhiteSpace(m) ? "" : "：" + m))
                                    : (m ?? ("未执行：未能为" + who + action)),
                                    null, null, ok);
                            }, stableOperationId);
                        float deadline = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (state[0] == -1 && Time.unscaledTime < deadline) yield return null;
                        if (state[0] == 0 && !string.IsNullOrWhiteSpace(message[0])
                            && message[0].StartsWith("未执行：", StringComparison.Ordinal))
                        {
                            onResult("(" + message[0] + "。请据实回应或另想办法。)");
                            yield break;
                        }
                        string verb = name == "detox" ? "驱毒" : "调息";
                        onResult(ActMsg(state[0], "(已为" + who + verb + "，详见回执。)",
                            "(没能为" + who + verb + ":" + (message[0] ?? "引擎拒绝")
                            + "。别假称已做。)"));
                        yield break;
                    }
                case "tell_secret":
                    {
                        RecipientSecretDispatch frozenSecret = null;
                        bool hasFrozenDispatch = !string.IsNullOrWhiteSpace(stableOperationId)
                            && RecipientSecretDispatchEnvelope.TryRead(a, npc, 0, out frozenSecret);
                        if (!string.IsNullOrWhiteSpace(stableOperationId) && !hasFrozenDispatch)
                        {
                            onResult("(未执行：秘闻派发日志缺少不可变的接收者、秘闻编号或正文；"
                                + "为避免把序号重映射成别人的秘密，本次没有吐露。)");
                            yield break;
                        }
                        int recipientId;
                        SecretInformationId secretId;
                        string secretText;
                        if (hasFrozenDispatch)
                        {
                            recipientId = frozenSecret.RecipientId;
                            secretId = (SecretInformationId)frozenSecret.SecretId;
                            secretText = SecretDisclosureProjection.NormalizeSecret(frozenSecret.SecretText);
                        }
                        else
                        {
                            string requestedRecipient = (S("to") ?? string.Empty).Trim();
                            string recipientReason = null;
                            recipientId = 0;
                            yield return ResolveToolPerson(npc, taiwu, requestedRecipient, taiwu, "太吾",
                                snap.Name, true, (id, label, reason) =>
                                {
                                    recipientId = id;
                                    recipientReason = reason;
                                });
                            if (recipientId <= 0 || recipientId == npc)
                            {
                                onResult("(未执行：无法可靠识别秘闻接收者「" + requestedRecipient + "」："
                                    + (recipientReason ?? "接收者无效") + "。)");
                                yield break;
                            }
                            int displayIndex = I("index", 1);
                            SecretRef selected = FindShareableSecret(snap, displayIndex);
                            if (selected == null)
                            {
                                onResult("(序号 " + displayIndex
                                    + " 不属于当前可吐露秘闻清单。先调 query_npc_secrets(to=同一接收者)"
                                    + " 看真实序号再原样照填，切勿猜。)");
                                yield break;
                            }
                            secretId = selected.Id;
                            secretText = SecretDisclosureProjection.NormalizeSecret(selected.Text);
                        }
                        if (secretText.Length == 0)
                        {
                            onResult("(未执行：这桩秘闻的正文无法可靠读取，本次没有吐露。)");
                            yield break;
                        }
                        bool toTaiwu = recipientId == taiwu;
                        bool can = false, checkedRecipient = false;
                        string preflightReason = null;
                        EffectHandler.QueryCanDiscloseSecret(secretId, npc, recipientId,
                            (ok, reason) => { can = ok; preflightReason = reason; checkedRecipient = true; });
                        float preflightDeadline = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (!checkedRecipient && Time.unscaledTime < preflightDeadline) yield return null;
                        if (!checkedRecipient || !can)
                        {
                            onResult("(未执行：" + (preflightReason ?? "接收者已经知情、秘闻已经公开或预查超时")
                                + "。先 query_npc_secrets(to=同一接收者) 换一条候选，"
                                + "不要把它写成已发生的失败。)");
                            yield break;
                        }
                        int[] ts = { -1 }; string[] tsm = { null };
                        if (toTaiwu)
                        {
                            EffectHandler.ApplyShareSecret(taiwu, secretId, (ok, m) =>
                            {
                                ts[0] = ok ? 1 : 0;
                                tsm[0] = m;
                                if (!ok) return;
                                RegisterTaiwuSecretDisclosure(secretText);
                                Emit("秘闻：「" + secretText + "」", "查看秘闻",
                                    CharacterMenuLink.OpenSecretPage, true);
                            }, stableOperationId);
                        }
                        else
                            EffectHandler.ApplyDiscloseSecret(secretId, npc, recipientId,
                                (ok, m) =>
                                {
                                    ts[0] = ok ? 1 : 0;
                                    tsm[0] = m;
                                    if (!ok) return;
                                    RegisterThirdPartySecretDisclosure(secretText);
                                    Emit("向指定人物吐露一桩秘闻", null, null, true);
                                }, stableOperationId);
                        float tsdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (ts[0] == -1 && Time.unscaledTime < tsdl) yield return null;
                        string success = toTaiwu
                            ? "(已向太吾明确说出秘闻：「"
                                + MemoryTrustPolicy.SanitizeForPromptData(secretText, 2000)
                                + "」。接下来的角色正文必须把这桩具体事实自然说清，不能只说有件事、你懂的、改日再说或让太吾自己查看秘闻。)"
                            : "(已向指定人物吐露一桩秘闻；本次没有向太吾吐露，"
                                + "不要为了向太吾交代结果而把秘闻正文再次泄露给他。)";
                        onResult(ActMsg(ts[0], success, "(没能吐露此秘:" + (tsm[0] ?? "对方或已知情、或此秘已广为人知、或你威望不足以散布此秘") + "。别假称已说。)"));
                        yield break;
                    }
                case "query_taiwu_secrets":
                    {
                        List<SecretRef> tss = null;
                        string eligibilityReason = null;
                        yield return NpcSnapshotReader.FetchDisclosableSecrets(taiwu, npc, snap.CurrentDate,
                            (items, reason) => { tss = items; eligibilityReason = reason; },
                            () => TurnToolDispatchAllowed(ct));
                        var qs = new StringBuilder();
                        if (tss == null || tss.Count == 0) qs.Append("太吾此刻并无可告知于你的秘闻：")
                            .Append(eligibilityReason ?? "你已经知情或秘闻已经公开").Append("。别假称他要告诉你什么。");
                        else { qs.Append("太吾确知、且你尚未知的秘闻(序号供 taiwu_tell_secret 用):\n"); foreach (SecretRef secret in tss) qs.Append(secret.DisplayIndex).Append(". ").Append(secret.Text ?? "(一桩秘闻)").Append("\n"); }
                        onResult(qs.ToString()); yield break;
                    }
                case "taiwu_tell_secret":
                    {
                        int idx = I("index", 1);
                        List<SecretRef> tss = null;
                        yield return NpcSnapshotReader.FetchShareableSecrets(taiwu, snap.CurrentDate, x => tss = x);
                        if (tss == null || tss.Count == 0) { onResult("(太吾并无可告知你的秘闻,别假称受教。)"); yield break; }
                        if (idx < 1 || idx > tss.Count) { onResult("(序号 " + idx + " 不对:太吾可讲的秘闻共 " + tss.Count + " 条,只能填 1~" + tss.Count + "。先 query_taiwu_secrets 看真实序号。)"); yield break; }
                        var sref2 = tss[idx - 1];
                        bool canReceive = false, receiveChecked = false; string receiveReason = null;
                        EffectHandler.QueryCanDiscloseSecret(sref2.Id, taiwu, npc,
                            (ok, reason) => { canReceive = ok; receiveReason = reason; receiveChecked = true; });
                        float receiveCheckDeadline = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (!receiveChecked && Time.unscaledTime < receiveCheckDeadline) yield return null;
                        if (!receiveChecked || !canReceive)
                        {
                            onResult("(未执行：" + (receiveReason ?? "你已经知情、秘闻已经公开或预查超时")
                                + "。先 query_taiwu_secrets 换一条候选，不要假称已经听到。)");
                            yield break;
                        }
                        int[] rs = { -1 }; string[] rm = { null };
                        EffectHandler.ApplyReceiveSecret(npc, sref2.Id, (ok, m) => { rs[0] = ok ? 1 : 0; rm[0] = m; if (ok) Emit("听太吾说了一桩秘闻", "查看秘闻", CharacterMenuLink.OpenSecretPage, true); }, stableOperationId);
                        float rdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (rs[0] == -1 && Time.unscaledTime < rdl) yield return null;
                        if (rs[0] == 1) onResult("(你已听太吾道出此秘,记下了,可在回话里收尾。)");
                        else onResult("(没能记下:" + (rm[0] ?? "你恐已知情、或此秘已广为人知") + "。别假称已知。)");
                        yield break;
                    }

                // —— 其它动作 ——
                case "trade":
                    {
                        string[] tradeMsg = { null };
                        yield return TradeAndNotice(snap, S("item"), Math.Max(1, I("amount", 1)), Math.Max(0, I("price")), m => tradeMsg[0] = m, stableOperationId);
                        onResult(tradeMsg[0] ?? "(已尝试与太吾交易,详见对话下方提示)"); yield break;
                    }
                case "sect_support":
                    {
                        int[] ss = { -1 }; string[] ssm = { null };
                        EffectHandler.ApplySectSupport(npc, (ok, m) => { ss[0] = ok ? 1 : 0; ssm[0] = m; Emit(ok ? "在门派内为太吾表态支持" : ("未能为太吾表态:" + (m ?? "")), null, null, ok); }, stableOperationId);
                        float ssdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (ss[0] == -1 && Time.unscaledTime < ssdl) yield return null;
                        onResult(ActMsg(ss[0], "(已在门派为太吾表态,详见对话下方提示)", "(没能为太吾表态:" + (ssm[0] ?? "你并无门派身份、或正在囚中、或门派成员数据缺失") + "。别假称已做。)"));
                        yield break;
                    }
                case "change_appearance":
                    {
                        if (Remote || GroupCtx != null || snap.IsCaptive || _npcInitiatedTurn
                            || _turnToolContext == null || !_turnToolContext.CanOpenGrooming)
                        {
                            _lastLocalToolSemantic = LocalToolSemantic.Failed;
                            onResult("(未排定梳头修面：只有太吾主动提出、与当前角色当面单聊且对方未被囚禁时，才能进入本体互动。不要说外貌已经改变。)");
                            yield break;
                        }
                        if (_pendingGrooming != null)
                        {
                            _lastLocalToolSemantic = LocalToolSemantic.Succeeded;
                            onResult("(本轮已经排定一次本体梳头修面入口，勿重复调用；外貌尚未改变，须等太吾点击并完成本体互动。)");
                            yield break;
                        }
                        _pendingGrooming = new PendingGroomingRequest
                        {
                            WorldId = WorldLifecycle.WorldId,
                            TaiwuId = taiwu,
                            NpcId = npc,
                            NpcName = snap.Name,
                            WorldGeneration = WorldLifecycle.Generation,
                        };
                        _lastLocalToolSemantic = LocalToolSemantic.Succeeded;
                        onResult("(已为太吾准备游戏本体的“为NPC梳头修面”入口；外貌此刻尚未改变。请说明太吾须点击“梳头修面”，再在原生界面选择具体装扮；该入口不经过好感、互动可见性、当月次数、资源或时间前置限制。)");
                        yield break;
                    }
                case "change_equipment":
                    {
                        string act = (S("action") ?? "").Trim().ToLowerInvariant();
                        if (act != "on" && act != "off")
                        {
                            onResult("(换装动作无效：action 只能是 on 或 off。别假称已经换装。)");
                            yield break;
                        }
                        int[] ce = { -1 }; string[] cem = { null };
                        if (act == "off")
                        {
                            string part;
                            if (!TryStrictEquipmentPart(S("part"), out part))
                            {
                                onResult("(卸装部位无效：part 只能是 weapon、armor、accessory 或 carrier。别假称已经卸下。)");
                                yield break;
                            }
                            EffectHandler.ApplyEquipTakeOff(npc, part, (ok, m) => { ce[0] = ok ? 1 : 0; cem[0] = m; Emit(ok ? ("卸下了" + PartZh(part)) : ("未能卸下" + PartZh(part) + ":" + (m ?? "")), null, null, ok); }, stableOperationId);
                            float cedl = Time.unscaledTime + RpcReceiptWaitSeconds;
                            while (ce[0] == -1 && Time.unscaledTime < cedl) yield return null;
                            onResult(ActMsg(ce[0], "(已卸下" + PartZh(part) + ",详见对话下方提示)", "(没能卸下" + PartZh(part) + ":" + (cem[0] ?? "该处本无装备") + "。别假称已卸。)"));
                        }
                        else
                        {
                            string item = (S("item") ?? "").Trim();
                            if (item.Length == 0) { onResult("(没说要换上哪件——先 query_npc_items 看你有什么可换上的。)"); yield break; }
                            EffectHandler.ApplyEquipByName(npc, item, (ok, m) => { ce[0] = ok ? 1 : 0; cem[0] = m; Emit(ok ? ("换上了「" + item + "」") : ("「" + item + "」未能换上" + (string.IsNullOrWhiteSpace(m) ? "" : ":" + m)), null, null, ok); }, stableOperationId);
                            float cedl = Time.unscaledTime + RpcReceiptWaitSeconds;
                            while (ce[0] == -1 && Time.unscaledTime < cedl) yield return null;
                            onResult(ActMsg(ce[0], "(已换上「" + item + "」,详见对话下方提示)", "(没能换上「" + item + "」:" + (cem[0] ?? "你身上并无此装备、或与装备槽不符") + "。以 query_npc_items 实时清单为准挑确切名,别假称已换。)"));
                        }
                        yield break;
                    }
                case "use_item":
                    {
                        string item = (S("item") ?? S("name") ?? string.Empty).Trim();
                        if (item.Length == 0)
                        {
                            onResult("(没说要使用哪件物品。只能从本轮预载的可自行使用候选中选择确切名称。)");
                            yield break;
                        }
                        int[] state = { -1 }, amount = { 0 }, before = { 0 }, after = { 0 };
                        string[] realName = { null }, kind = { null }, message = { null };
                        EffectHandler.ApplyNpcUseItem(npc, item,
                            (ok, name2, type, usedAmount, beforeCount, afterCount, why) =>
                            {
                                state[0] = ok ? 1 : 0; realName[0] = name2; kind[0] = type;
                                amount[0] = usedAmount; before[0] = beforeCount; after[0] = afterCount;
                                message[0] = why;
                                Emit(ok
                                    ? ("已使用「" + (name2 ?? item) + "」" + (usedAmount > 1 ? ("×" + usedAmount) : ""))
                                    : ("未能使用「" + item + "」:" + (why ?? "条件不合")),
                                    null, null, ok);
                            }, stableOperationId);
                        float deadline = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (state[0] == -1 && Time.unscaledTime < deadline) yield return null;
                        onResult(ActMsg(state[0],
                            "(已使用" + (string.IsNullOrWhiteSpace(kind[0]) ? "物品" : kind[0])
                                + "「" + (realName[0] ?? item) + "」"
                                + (amount[0] > 1 ? ("×" + amount[0]) : "")
                                + "，背包数量 " + before[0] + "→" + after[0] + "。)",
                            "(没能使用「" + item + "」:" + (message[0]
                                ?? "不在实时可用候选、数量不足、主属性不足或服食栏位已满")
                                + "。别假称已经使用。)"));
                        yield break;
                    }
                case "flip_practice":
                    {
                        string skill = (S("skill") ?? S("name") ?? "").Trim();
                        if (skill.Length == 0) { onResult("(没说要改哪门功法的正逆练。先 query_npc_skills/query_taiwu_skills 查确切名。)"); yield break; }
                        int targetId = 0; string targetName = null, targetReason = null;
                        yield return ResolveToolPerson(npc, taiwu, S("person"), npc, "你", snap.Name, true, (id, label, reason) => { targetId = id; targetName = label; targetReason = reason; });
                        if (targetId <= 0) { onResult(ResolveFailMsg(S("person"), targetReason, false)); yield break; }
                        int[] fr = { -1 }, flipped = { 0 }; string[] fm = { null }, realSkill = { null }, before = { null }, after = { null };
                        EffectHandler.ApplyFlipPractice(targetId, skill, (ok, name2, b, aft, cnt, m) =>
                        {
                            fr[0] = ok ? 1 : 0; realSkill[0] = name2; before[0] = b; after[0] = aft; flipped[0] = cnt; fm[0] = m;
                            Emit(ok ? ((targetName ?? "此人") + "将「" + (name2 ?? skill) + "」由" + (b ?? "原练法") + "改作" + (aft ?? "新练法")) : ("未能改正逆练:" + (m ?? "")), null, null, ok);
                        }, stableOperationId);
                        float fdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (fr[0] == -1 && Time.unscaledTime < fdl) yield return null;
                        onResult(ActMsg(fr[0],
                            "(已改变" + (targetName ?? "此人") + "「" + (realSkill[0] ?? skill) + "」的正逆练:" + (before[0] ?? "原练法") + "→" + (after[0] ?? "新练法") + ",共改 " + flipped[0] + " 页。)",
                            "(没能改变正逆练:" + (fm[0] ?? "未学会/未突破/没读对应反页") + "。这改的是角色已会功法,不是改秘籍;别假称已改。)"));
                        yield break;
                    }
                case "train_skill":
                    {
                        string skill = (S("skill") ?? S("name") ?? "").Trim();
                        if (skill.Length == 0)
                        {
                            onResult("(没说要修炼哪门武学。只能从本轮预载的尚未练满候选中选择确切名称。)");
                            yield break;
                        }
                        int[] state = { -1 }, before = { 0 }, after = { 0 };
                        string[] realName = { null }, direction = { null }, message = { null };
                        EffectHandler.ApplyNpcTrainSkill(npc, skill,
                            (ok, name2, beforePages, afterPages, dir, why) =>
                            {
                                state[0] = ok ? 1 : 0;
                                realName[0] = name2;
                                before[0] = beforePages;
                                after[0] = afterPages;
                                direction[0] = dir;
                                message[0] = why;
                                Emit(ok
                                    ? ("已将「" + (name2 ?? skill) + "」修炼完整，研读与突破均已完成")
                                    : ("未能修炼「" + skill + "」:" + (why ?? "条件不合")),
                                    null, null, ok);
                            }, stableOperationId);
                        float deadline = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (state[0] == -1 && Time.unscaledTime < deadline) yield return null;
                        onResult(ActMsg(state[0],
                            "(已把「" + (realName[0] ?? skill) + "」修炼完整：已读 "
                                + before[0] + "→" + after[0] + " 页，突破练法="
                                + (direction[0] ?? "原练法") + "。)",
                            "(没能修炼「" + skill + "」:" + (message[0]
                                ?? "你并未习得这门武学") + "。本工具只作用于当前 NPC 自己，别假称已完成。)"));
                        yield break;
                    }
                case "read_book":
                    {
                        string book = (S("book") ?? S("name") ?? "").Trim();
                        if (book.Length == 0)
                        {
                            onResult("(没说要读哪本书。只能从本轮预载的尚未读完候选中选择确切书名。)");
                            yield break;
                        }
                        int[] state = { -1 }, before = { 0 }, after = { 0 };
                        string[] realName = { null }, kind = { null }, message = { null };
                        EffectHandler.ApplyNpcReadBook(npc, book,
                            (ok, name2, type, beforePages, afterPages, why) =>
                            {
                                state[0] = ok ? 1 : 0;
                                realName[0] = name2;
                                kind[0] = type;
                                before[0] = beforePages;
                                after[0] = afterPages;
                                message[0] = why;
                                Emit(ok
                                    ? ("已把「" + (name2 ?? book) + "」全部读完")
                                    : ("未能读完「" + book + "」:" + (why ?? "背包中没有此书")),
                                    null, null, ok);
                            }, stableOperationId);
                        float deadline = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (state[0] == -1 && Time.unscaledTime < deadline) yield return null;
                        onResult(ActMsg(state[0],
                            "(已读完" + (kind[0] ?? "") + "书「" + (realName[0] ?? book)
                                + "」：进度 " + before[0] + "→" + after[0] + " 页。)",
                            "(没能读完「" + book + "」:" + (message[0]
                                ?? "你背包中没有这本真实书籍") + "。本工具只作用于当前 NPC 自己，别假称已读完。)"));
                        yield break;
                    }
                case "adjust_mood":
                case "adjust_fame":
                    {
                        bool mood = name == "adjust_mood";
                        string rawTarget = (S("target") ?? "").Trim();
                        string reason = (S("reason") ?? "").Trim();
                        int delta = I("delta", 0);
                        if (rawTarget.Length == 0 || reason.Length < 2)
                        {
                            onResult("(未执行：必须写明状态实际变化的人和造成变化的具体原因。)");
                            yield break;
                        }
                        if (mood
                            ? delta == 0 || delta < -30 || delta > 30
                            : delta == 0 || delta < -12 || delta > 12 || delta % 3 != 0)
                        {
                            onResult(mood
                                ? "(未执行：心情变化必须是 -30..30 的非零整数。)"
                                : "(未执行：名望变化只能是 ±3、±6、±9 或 ±12。)");
                            yield break;
                        }
                        int targetId = 0; string targetName = null, targetReason = null;
                        yield return ResolveToolPerson(npc, taiwu, rawTarget, 0, rawTarget,
                            snap.Name, true,
                            (id, label, why) =>
                            {
                                targetId = id;
                                targetName = label;
                                targetReason = why;
                            });
                        if (targetId <= 0)
                        {
                            onResult(ResolveFailMsg(rawTarget, targetReason, false));
                            yield break;
                        }
                        int[] state = { -1 }; string[] message = { null };
                        Action<bool, string> completed = (ok, why) =>
                        {
                            state[0] = ok ? 1 : 0;
                            message[0] = why;
                            string label = targetName ?? rawTarget;
                            Emit(ok
                                    ? (label + (mood ? "的心情" : "的名望")
                                        + (delta > 0 ? "+" : "") + delta + "：" + reason)
                                    : ((mood ? "心情" : "名望") + "变化失败："
                                        + (why ?? "引擎拒绝")),
                                null, null, ok);
                        };
                        if (mood)
                            EffectHandler.ApplyCharacterHappiness(targetId, delta, completed,
                                stableOperationId);
                        else EffectHandler.ApplyCharacterFame(targetId, delta, completed,
                            stableOperationId);
                        float deadline = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (state[0] == -1 && Time.unscaledTime < deadline) yield return null;
                        onResult(ActMsg(state[0],
                            "(已改变" + (targetName ?? rawTarget) + "的"
                                + (mood ? "心情" : "名望") + (delta > 0 ? "+" : "")
                                + delta + "，原因：" + reason + "。)",
                            "(没能改变" + (targetName ?? rawTarget) + "的"
                                + (mood ? "心情" : "名望") + "："
                                + (message[0] ?? "后端结果未确认") + "。别假称已经改变。)"));
                        yield break;
                    }
                case "adjust_third_party_favor":
                    {
                        string tgt = S("target"); int delta = Clamp(I("delta"), -3000, 3000);
                        if (string.IsNullOrWhiteSpace(tgt)) { onResult("(没说是对谁的观感——填上那人确切姓名再调。)"); yield break; }
                        if (delta == 0)
                        { EffectHandler.CompleteObservedOperation(ToolOutcome.Succeeded(stableOperationId, null, "观感无变")); onResult("(delta 为 0,观感无变。)"); yield break; }
                        int thirdId = 0; string thirdLabel = null, thirdReason = null;
                        yield return ResolveToolPerson(npc, taiwu, tgt, 0, tgt, snap.Name, false,
                            (id, label, reason) => { thirdId = id; thirdLabel = label; thirdReason = reason; });
                        if (thirdId <= 0 || thirdId == npc)
                        { onResult(ResolveFailMsg(tgt, thirdReason, false)); yield break; }
                        if (thirdId == taiwu)
                        {
                            onResult("(本工具只改变你对第三方的观感；目标解析成了太吾本人，本次没有执行。对太吾的即时反应只用 record_reaction。)");
                            yield break;
                        }
                        int[] favorState = { -1 }; string[] favorMessage = { null };
                        EffectHandler.ApplyThirdPartyFavor(npc, thirdId, delta, (ok, message) =>
                        {
                            favorState[0] = ok ? 1 : 0;
                            favorMessage[0] = message;
                            Emit(ok
                                ? ("对" + (thirdLabel ?? tgt) + "的观感" + (delta > 0 ? "转好了" : "转恶了"))
                                : ("对" + (thirdLabel ?? tgt) + "的观感未能改变:" + (message ?? "原因不明")),
                                null, null, ok);
                        }, stableOperationId);
                        float favorDeadline = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (favorState[0] == -1 && Time.unscaledTime < favorDeadline) yield return null;
                        onResult(ActMsg(favorState[0],
                            "(已改变你对" + (thirdLabel ?? tgt) + "的观感。)",
                            "(没能改变你对" + (thirdLabel ?? tgt) + "的观感:" + (favorMessage[0] ?? "原因不明") + "。别假称已改变。)"));
                        yield break;
                    }
                case "add_feature":
                    {
                        string fname = S("feature");
                        if (string.IsNullOrWhiteSpace(fname)) { onResult("(未指明何种特性)"); yield break; }
                        int[] fr = { -1 }; string[] fm = { null };
                        EffectHandler.ApplyAddFeature(npc, fname, (ok, m) =>
                        { fr[0] = ok ? 1 : 0; fm[0] = m; Emit(ok ? ("性情新生:" + m) : ("未能生出特性「" + fname + "」(" + (m ?? "") + ")"), null, null, ok); }, stableOperationId);
                        float fdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (fr[0] == -1 && Time.unscaledTime < fdl) yield return null;
                        if (fr[0] == 1) onResult("(你心性新生「" + (fm[0] ?? fname) + "」,可在回话里自然流露。)");
                        else if (fr[0] == 0) onResult("(没能生出特性「" + fname + "」:" + (fm[0] ?? "该词不在良性特性表,或你已具备、或性别不合") + "。换一个契合人设、且确在太吾特性表中的具体品性词再试,或就此作罢——别假称已变。)");
                        else onResult(ActMsg(-1, null, null));
                        yield break;
                    }
                case "goto_place":
                    {
                        // 统一行程工具：普通前往/寻人走有期限的 NpcTravelTarget；仅显式固定地点赴太吾之约走 254。
                        string place = (S("place") ?? "").Trim();
                        string purpose = (S("purpose") ?? "").Trim();   // 前往(默认)/赴约/营救/保护/投奔门派/追杀/迁居
                        bool toTaiwu = string.IsNullOrEmpty(place) || IsFollowTaiwu(place) || place.Contains("太吾");

                        // —— 赴约：只允许“与太吾在固定地点会面”。动态寻人不是预约，避免人物移动后永远守着旧坐标。——
                        if (purpose == "赴约")
                        {
                            if (snap != null && snap.Favor < 10000)
                            { onResult("(你与太吾交情尚不足以郑重立下远行之约，别空口答应。)"); yield break; }
                            if (string.IsNullOrWhiteSpace(place) || IsFollowTaiwu(place))
                            { onResult("(赴约须说定一个固定地点；『太吾所在地』会变化，不能拿来立固定之约。请换成确切地名或太吾村。)"); yield break; }
                            short appointmentArea = -1, appointmentBlock = 0;
                            string appointmentName = place;
                            bool appointmentHome = IsHomeVillage(place);
                            if (!appointmentHome && !EffectHandler.ResolveAreaId(place, out appointmentArea,
                                    out appointmentBlock, out appointmentName))
                            { onResult("(认不出约定地点『" + place + "』；赴约只能填写江湖中确有的固定地点。)"); yield break; }
                            int[] appointmentState = { -1 }; string[] appointmentMessage = { null };
                            EffectHandler.ApplyAppointmentWithTaiwu(npc, appointmentArea, appointmentBlock, (ok, m) =>
                            {
                                appointmentState[0] = ok ? 1 : 0;
                                appointmentMessage[0] = m;
                                Emit(ok
                                    ? ("已与太吾约定在" + appointmentName + "相见；抵达后会等太吾前来交谈")
                                    : ("未能立下赴约:" + (m ?? "")), null, null, ok);
                            }, appointmentHome, stableOperationId);
                            float appointmentDeadline = Time.unscaledTime + RpcReceiptWaitSeconds;
                            while (appointmentState[0] == -1 && Time.unscaledTime < appointmentDeadline) yield return null;
                            onResult(ActMsg(appointmentState[0],
                                "(你已与太吾说定在" + appointmentName + "相见；你会动身赴约，抵达后等太吾前来交谈。)",
                                "(没能立下赴约:" + (appointmentMessage[0] ?? "此约未成") + "。别假称已经约好。)"));
                            yield break;
                        }

                        // —— 投奔门派 / 迁居:太吾门下走 joinsect;别的门派按名 force-change;迁居仅太吾村 ——
                        if (purpose == "投奔门派" || purpose == "迁居")
                        {
                            bool toTaiwuSect = toTaiwu || IsHomeVillage(place) || place.Contains("太吾门") || place.Contains("太吾村");
                            if (purpose == "迁居" && !(toTaiwu || IsHomeVillage(place)))
                            { onResult("(迁居我只能迁去太吾村、投入太吾门下;别处的家一时安不了,别应了空欢喜。)"); yield break; }
                            if (toTaiwuSect || purpose == "迁居")   // 投奔/迁入太吾门下
                            {
                                int[] jt = { -1 }; string[] jtm = { null };
                                EffectHandler.ApplyJoinTaiwuSect(npc, (ok, m) =>
                                { jt[0] = ok ? 1 : 0; jtm[0] = m; if (ok) Emit("已决意归入太吾门下", null, null, true); }, stableOperationId);
                                float jtdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                                while (jt[0] == -1 && Time.unscaledTime < jtdl) yield return null;
                                onResult(ActMsg(jt[0], "(你已决意归入太吾门下,不日便去投效)",
                                    "(未能归入太吾门下:" + (jtm[0] ?? "条件不合") + "。别假称已投。)")); yield break;
                            }
                            // 投奔【别的门派】:按门派/据点名 force-change,名字认不出即当场回绝
                            int[] jo = { -1 }; string[] jom = { null };
                            EffectHandler.ApplyChangeOrgByName(npc, place, (ok, m) => { jo[0] = ok ? 1 : 0; jom[0] = m; Emit(ok ? ("已决意投奔" + place) : ("未能投奔" + place + ":" + (m ?? "")), null, null, ok); }, stableOperationId);
                            float jdl = Time.unscaledTime + RpcReceiptWaitSeconds; while (jo[0] == -1 && Time.unscaledTime < jdl) yield return null;
                            onResult(ActMsg(jo[0], "(你已决意投奔" + place + ",不日便去投效)", "(" + (jom[0] ?? "认不出这个门派,投不成") + ";别假称已投。)")); yield break;
                        }

                        // —— 追杀:太吾→271;第三方→274 GetRevenge(须对其确有仇怨)——
                        if (purpose == "追杀")
                        {
                            if (toTaiwu)
                            {
                                int[] hg = { -1 }; string[] hgm = { null };
                                EffectHandler.ApplyAddGoal(npc, 271, "taiwu", 0, (ok, m) => { hg[0] = ok ? 1 : 0; hgm[0] = m; Emit(ok ? "已起意追杀太吾(过月起行)" : ("未能起意追杀:" + (m ?? "")), null, null, ok); }, stableOperationId);
                                float hdl = Time.unscaledTime + RpcReceiptWaitSeconds; while (hg[0] == -1 && Time.unscaledTime < hdl) yield return null;
                                onResult(ActMsg(hg[0], "(你已起意,过些时日便去寻太吾索命)", "(没能起意追杀:" + (hgm[0] ?? "此刻时机未到") + "。别假称要动手。)")); yield break;
                            }
                            // 追杀【第三方】:具体动机由人物与本轮因果决定，仇怨不是固定资格门。
                            int[] tid = { int.MinValue };
                            EffectHandler.ResolveChar(npc, place, c => tid[0] = c);
                            float tdl = Time.unscaledTime + RpcReceiptWaitSeconds; while (tid[0] == int.MinValue && Time.unscaledTime < tdl) yield return null;
                            int tcid = tid[0] == int.MinValue ? 0 : tid[0];
                            if (tcid <= 0 || tcid == npc) { onResult("(遍寻不见你说的『" + place + "』这人,无从寻仇——换个你确实相识的确切姓名。)"); yield break; }
                            int[] hg2 = { -1 }; string[] hgm2 = { null };
                            EffectHandler.ApplyAddGoal(npc, 274, "char", tcid, (ok, m) => { hg2[0] = ok ? 1 : 0; hgm2[0] = m; Emit(ok ? ("已起意寻仇" + place + "(过月起行)") : ("未能起意寻仇:" + (m ?? "")), null, null, ok); }, stableOperationId);
                            float hdl2 = Time.unscaledTime + RpcReceiptWaitSeconds; while (hg2[0] == -1 && Time.unscaledTime < hdl2) yield return null;
                            onResult(ActMsg(hg2[0], "(你已起意,过些时日便去寻" + place + "报此宿怨)", "(没能起意寻仇:" + (hgm2[0] ?? "此刻不便") + "。别假称。)")); yield break;
                        }

                        // —— 营救(263)/ 保护(262):目标可太吾或具名第三方 ——
                        if (purpose == "营救" || purpose == "保护")
                        {
                            int tpl = purpose == "营救" ? 263 : 262;
                            string verb = purpose == "营救" ? "营救" : "护卫";
                            if (toTaiwu)
                            {
                                int[] rg = { -1 }; string[] rgm = { null };
                                EffectHandler.ApplyAddGoal(npc, tpl, "taiwu", 0, (ok, m) => { rg[0] = ok ? 1 : 0; rgm[0] = m; Emit(ok ? ("已决意" + verb + "太吾(过月起行)") : ("未能应下:" + (m ?? "")), null, null, ok); }, stableOperationId);
                                float rdl = Time.unscaledTime + RpcReceiptWaitSeconds; while (rg[0] == -1 && Time.unscaledTime < rdl) yield return null;
                                onResult(ActMsg(rg[0], "(你已决意," + (purpose == "营救" ? "过些时日便去搭救太吾" : "日后必护太吾周全") + ")", "(没能应下:" + (rgm[0] ?? "此刻不便") + "。别假称。)")); yield break;
                            }
                            // 具名第三方:具体动机由人物与本轮因果决定，不用固定好感阈值代替判断。
                            int[] rid = { int.MinValue };
                            EffectHandler.ResolveChar(npc, place, c => rid[0] = c);
                            float rrdl = Time.unscaledTime + RpcReceiptWaitSeconds; while (rid[0] == int.MinValue && Time.unscaledTime < rrdl) yield return null;
                            int rcid = rid[0] == int.MinValue ? 0 : rid[0];
                            if (rcid <= 0 || rcid == npc) { onResult("(遍寻不见你说的『" + place + "』这人,无从" + (purpose == "营救" ? "相救" : "护卫") + "——换个你确实相识的确切姓名。)"); yield break; }
                            int[] rg2 = { -1 }; string[] rgm2 = { null };
                            EffectHandler.ApplyAddGoal(npc, tpl, "char", rcid, (ok, m) => { rg2[0] = ok ? 1 : 0; rgm2[0] = m; Emit(ok ? ("已决意" + verb + place + "(过月起行)") : ("未能应下:" + (m ?? "")), null, null, ok); }, stableOperationId);
                            float rdl2 = Time.unscaledTime + RpcReceiptWaitSeconds; while (rg2[0] == -1 && Time.unscaledTime < rdl2) yield return null;
                            onResult(ActMsg(rg2[0], "(你已决意去" + verb + place + ";只是你若与他交情不深,未必真能成行)", "(没能应下:" + (rgm2[0] ?? "此刻不便") + "。别假称。)")); yield break;
                        }

                        // —— 默认『前往』/来寻某人：使用有期限的固定/动态行程，不创建太吾预约，也不套用预约好感门槛。——
                        short aId = -1, bId = 0; string realName = place; bool home = false; bool follow = false; int destChar = 0;
                        if (string.IsNullOrWhiteSpace(place) || IsFollowTaiwu(place)) { follow = true; realName = "太吾身边"; }
                        else if (IsHomeVillage(place)) { home = true; realName = "太吾村"; }
                        else if (EffectHandler.ResolveAreaId(place, out aId, out bId, out realName)) { }   // 地名解析成功
                        else
                        {
                            // 不是地名 → 试作『来寻某人』：后端按人物 id 逐月追踪，不冻结此刻坐标。
                            int[] pid = { int.MinValue };
                            EffectHandler.ResolveChar(npc, place, c => pid[0] = c);
                            float pdl = Time.unscaledTime + RpcReceiptWaitSeconds; while (pid[0] == int.MinValue && Time.unscaledTime < pdl) yield return null;
                            int pcid = pid[0] == int.MinValue ? 0 : pid[0];
                            if (pcid <= 0 || pcid == npc)
                            { Debug.Log("[江湖有灵] goto_place 无法解析 place_chars=" + place.Length + "(非地名/太吾/相识之人)"); onResult("(遍寻不见「" + place + "」这地方、也不识你说的这个人——换个江湖实有的地名、你确实相识的人名,或填『太吾所在地』来寻太吾。)"); yield break; }
                            destChar = pcid; realName = place;
                        }
                        Debug.Log("[江湖有灵] goto_place 解析 follow=" + follow + " home=" + home
                            + " area=" + aId + " block=" + bId + " destChar=" + destChar + " place_chars=" + place.Length);
                        int[] gp = { -1 }; string[] gpm = { null };
                        EffectHandler.ApplyGotoPlace(npc, aId, bId, (ok, m) =>
                            { gp[0] = ok ? 1 : 0; gpm[0] = m; Debug.Log("[江湖有灵] goto_place 后端回执 ok=" + ok + " result_chars=" + (m?.Length ?? 0)); Emit(ok ? ("已安排前往" + realName + "的行程(逐月成行，抵达或到期即结束)") : ("未能安排前往" + realName + ":" + (m ?? "")), null, null, ok); }, home, follow, destChar, stableOperationId);
                        float gpdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (gp[0] == -1 && Time.unscaledTime < gpdl) yield return null;
                        onResult(ActMsg(gp[0], "(你已应下,过些时日便往" + realName + "走一趟)", "(没能应下前往" + realName + ":" + (gpm[0] ?? "此行暂时无法成行") + "。别假称要去。)"));
                        yield break;
                    }
                case "change_caravan_favor":
                    {
                        int delta = Clamp(I("delta"), -100, 100);
                        if (delta == 0)
                        { EffectHandler.CompleteObservedOperation(ToolOutcome.Succeeded(stableOperationId, null, "商队观感无变")); onResult("(观感无变)"); yield break; }
                        int[] mf = { -1 }; string[] mfm = { null };
                        EffectHandler.ApplyMerchantFavor(npc, delta, (ok, m) =>
                            { mf[0] = ok ? 1 : 0; mfm[0] = m; Emit(ok ? ("商队对太吾的观感" + (delta > 0 ? "增进了" : "转淡了") + "(现 " + m + "/100)") : ("商队好感未变:" + (m ?? "")), null, null, ok); }, stableOperationId);
                        float mfdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (mf[0] == -1 && Time.unscaledTime < mfdl) yield return null;
                        onResult(ActMsg(mf[0], "(你所在商队对太吾的观感已" + (delta > 0 ? "更好" : "更冷") + "了几分)", "(商队观感没能变:" + (mfm[0] ?? "你并非商人、或不属任何商队") + "。别假称已变。)"));
                        yield break;
                    }
                case "taiwu_give_item":
                    {
                        string qn = ItemNameMatcher.StripCountSuffix((S("name") ?? "").Trim());   // 容错:剥尾部「×N」数量后缀(与后端对齐)
                        // 银钱/盘缠:不在物品清单里,直接走资源槽6(Money)。太吾给 NPC 些银两。
                        if (qn == "银钱" || qn == "银两" || qn == "盘缠" || qn == "钱" || qn == "money" || qn == "silver")
                        {
                            int amtM = Math.Max(1, I("amount", 1));
                            int[] tgm = { int.MinValue };
                            EffectHandler.ApplyTaiwuGiveItem(taiwu, npc, new GameData.Domains.Item.ItemKey((sbyte)12, (byte)0, (short)6, 0), amtM, actual =>
                                { tgm[0] = actual; Emit(actual > 0 ? ("收下太吾相赠银钱 " + actual + " 文") : "太吾欲赠银钱却未能给出", null, null, actual > 0); }, stableOperationId);
                            float mdl2 = Time.unscaledTime + RpcReceiptWaitSeconds; while (tgm[0] == int.MinValue && Time.unscaledTime < mdl2) yield return null;
                            onResult(tgm[0] > 0 ? ("(你已收下太吾的 " + tgm[0] + " 文银钱,可道谢收尾。)")
                                : tgm[0] == int.MinValue ? ActMsg(-1, null, null)
                                : "(没能收下银钱——太吾此刻银钱不足。别假称已收。)");
                            yield break;
                        }
                        yield return NpcSnapshotReader.FetchTaiwuHoldings(taiwu, h => _taiwuHoldings = h);
                        var list = _taiwuHoldings?.Items;
                        if (list == null || list.Count == 0) { onResult("(太吾身上并无可赠你之物)"); yield break; }
                        GiftableItem pick = null;
                        if (qn.Length == 0) pick = list[0];
                        else
                        {
                            foreach (var g in list) if (g != null && g.Name == qn) { pick = g; break; }
                            // 模糊命中取最长/最具体者,避免短名吞掉长名
                            if (pick == null) foreach (var g in list) if (g != null && NameHit(g.Name, qn) && (pick == null || (g.Name ?? "").Length > (pick.Name ?? "").Length)) pick = g;
                        }
                        if (pick == null) { onResult("(太吾身上没有「" + qn + "」,收不下)"); yield break; }
                        int amt = Math.Max(1, I("amount", 1));
                        int[] tg = { int.MinValue };
                        EffectHandler.ApplyTaiwuGiveItem(taiwu, npc, pick.Key, amt, actual =>
                            { tg[0] = actual; Emit(actual > 0 ? ("收下太吾相赠:" + pick.Name + (actual > 1 ? " ×" + actual : "")) : ("太吾欲赠「" + pick.Name + "」却未能到手"), null, null, actual > 0); }, stableOperationId);
                        float tgdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (tg[0] == int.MinValue && Time.unscaledTime < tgdl) yield return null;
                        if (tg[0] > 0) onResult("(你已收下太吾的" + pick.Name + ",可在回话里道谢收尾。)");
                        else if (tg[0] == int.MinValue) onResult(ActMsg(-1, null, null));
                        else onResult("(没能收下太吾的「" + pick.Name + "」——他此刻或已无此物、或你背包已满。别假称已收下。)");
                        yield break;
                    }
                case "taiwu_teach":
                    {
                        string nm = (S("name") ?? "").Trim();
                        // JHYL_REFRESH_TAIWU_SKILLS_BEFORE_TEACH:太吾可能刚学/换过功法,不能复用旧缓存先判"不会"。
                        yield return RefreshTaiwuHoldings(taiwu);
                        var cpool = _taiwuHoldings?.CombatSkills; var lpool = _taiwuHoldings?.LifeSkills;
                        bool hasC = cpool != null && cpool.Count > 0, hasL = lpool != null && lpool.Count > 0;
                        if (!hasC && !hasL) { onResult("(太吾并无可传你的本事)"); yield break; }
                        // 武学/技艺合一:按名字在太吾的武学/技艺两池自动判定该学哪种(你不必分清)
                        string kind = "combat"; LearnableSkill pk = null;
                        if (nm.Length == 0) { pk = hasC ? cpool[0] : lpool[0]; kind = hasC ? "combat" : "life"; }
                        else
                        {
                            if (hasC) foreach (var s2 in cpool) if (s2 != null && (s2.Name == nm || NameHit(s2.Name, nm))) { pk = s2; kind = "combat"; break; }
                            if (pk == null && hasL) foreach (var s2 in lpool) if (s2 != null && (s2.Name == nm || NameHit(s2.Name, nm))) { pk = s2; kind = "life"; break; }
                        }
                        if (pk == null) { onResult("(太吾当前所学里找不到「" + nm + "」;先 query_taiwu_skills 查确切名再试,别假称已学。)"); yield break; }
                        int[] tt = { -1 };
                        string[] ttMsg = { null };
                        EffectHandler.ApplyTaiwuTeach(taiwu, npc, kind, pk.TemplateId, (ok, msg) =>
                        {
                            tt[0] = ok ? 1 : 0;
                            ttMsg[0] = msg;
                            string why = !string.IsNullOrWhiteSpace(msg) && msg != "ok" ? msg : "对方或已会";
                            Emit(ok ? ("承太吾亲授:" + pk.Name) : ("太吾欲授「" + pk.Name + "」未成:" + why), null, null, ok);
                        }, stableOperationId);
                        float ttdl = Time.unscaledTime + RpcReceiptWaitSeconds;
                        while (tt[0] == -1 && Time.unscaledTime < ttdl) yield return null;
                        string failWhy = !string.IsNullOrWhiteSpace(ttMsg[0]) && ttMsg[0] != "ok" ? ttMsg[0] : ("多半你已通晓此" + (kind == "life" ? "技艺" : "武学"));
                        onResult(ActMsg(tt[0], "(你已拜领太吾所授" + pk.Name + ",可在回话里道谢收尾。)", "(没能学成太吾的「" + pk.Name + "」:" + failWhy + "。别假称已学。)"));
                        yield break;
                    }
                case "taiwu_write_book":
                    {
                        yield return TaiwuWriteBookForNpc(taiwu, npc,
                            (S("name") ?? "").Trim(), stableOperationId, onResult);
                        yield break;
                    }

                default:
                    onResult("(没有这样的本事)"); yield break;
            }
        }

        private IEnumerator RefreshTaiwuHoldings(int taiwu)
        {
            _taiwuHoldings = null;
            yield return NpcSnapshotReader.FetchTaiwuHoldings(taiwu, h => _taiwuHoldings = h);
        }

        private IEnumerator TaiwuWriteBookForNpc(int taiwu, int npc, string requestedName,
            string stableOperationId, Action<string> onResult)
        {
            string name = (requestedName ?? string.Empty).Trim();
            yield return RefreshTaiwuHoldings(taiwu);
            var combat = _taiwuHoldings?.CombatSkills;
            var life = _taiwuHoldings?.LifeSkills;
            bool hasCombat = combat != null && combat.Count > 0;
            bool hasLife = life != null && life.Count > 0;
            if (!hasCombat && !hasLife)
            {
                onResult?.Invoke("(太吾并无可回忆成书赠你的本事)");
                yield break;
            }

            string kind = "combat";
            LearnableSkill picked = null;
            if (name.Length == 0)
            {
                picked = hasCombat ? combat[0] : life[0];
                kind = hasCombat ? "combat" : "life";
            }
            else
            {
                if (hasCombat)
                    foreach (LearnableSkill skill in combat)
                        if (skill != null && (skill.Name == name || NameHit(skill.Name, name)))
                        { picked = skill; kind = "combat"; break; }
                if (picked == null && hasLife)
                    foreach (LearnableSkill skill in life)
                        if (skill != null && (skill.Name == name || NameHit(skill.Name, name)))
                        { picked = skill; kind = "life"; break; }
            }
            if (picked == null)
            {
                onResult?.Invoke("(太吾当前所学里找不到「" + name
                    + "」;先 query_taiwu_skills 查确切名再试,别假称已收书。)");
                yield break;
            }

            int[] state = { -1 };
            int[] lost = { 0 };
            string[] message = { null };
            EffectHandler.ApplyWriteBook(taiwu, taiwu, kind, picked.TemplateId,
                (ok, bookName, lostPages) =>
                {
                    state[0] = ok ? 1 : 0;
                    message[0] = bookName;
                    lost[0] = lostPages;
                    string actual = string.IsNullOrWhiteSpace(bookName) ? picked.Name : bookName;
                    Emit(ok ? ("收下太吾回忆《" + actual + "》相赠"
                            + (lostPages > 0 ? "(残" + lostPages + "页)" : ""))
                        : ("太吾回忆「" + picked.Name + "」成书未成:" + actual),
                        null, null, ok);
                }, npc, stableOperationId);
            float deadline = Time.unscaledTime + RpcReceiptWaitSeconds;
            while (state[0] == -1 && Time.unscaledTime < deadline) yield return null;
            string finalBookName = string.IsNullOrWhiteSpace(message[0]) ? picked.Name : message[0];
            string pageNote = lost[0] > 0
                ? ("其中有 " + lost[0] + " 页字句残缺,仍可研习,但须慢慢参详补全")
                : "书页完整";
            onResult?.Invoke(ActMsg(state[0],
                "(你已收下太吾回忆成册赠你的《" + finalBookName + "》," + pageNote
                    + ";可在回话里道谢收尾。)",
                "(没能收下太吾写成的「" + picked.Name + "」:"
                    + (message[0] ?? "原因不明") + "。别假称已收书。)"));
        }

        private IEnumerator RefreshNpcSkills(int npc, NpcSnapshot snap)
        {
            if (snap == null) yield break;
            snap.LearnableSkills.Clear();
            snap.LearnableLifeSkills.Clear();
            yield return NpcSnapshotReader.FetchSkills(npc, snap);
            yield return NpcSnapshotReader.FetchLifeSkills(npc, snap);
        }

        private IEnumerator RefreshNpcStudyCandidates(int npc, NpcSnapshot snap,
            Func<bool> stillCurrent = null)
        {
            if (snap == null) yield break;
            snap.LearnableSkills.Clear();
            yield return NpcSnapshotReader.FetchSkills(npc, snap, stillCurrent, false);
            if (stillCurrent != null && !stillCurrent()) yield break;
            yield return NpcSnapshotReader.FetchStudyProgress(npc, snap, stillCurrent);
        }

        private string RecallMemory(string topic)
        {
            if (_mem == null) return "(你想不起什么。)";
            var all = _mem.All;
            if (all == null || all.Count == 0) return "(你与太吾尚无值得记起的过往。)";
            var sb = new StringBuilder("你回想起的相关记忆:\n");
            string t = (topic ?? "").Trim();
            var topicTokens = MemoryRanker.Tokenize(t);
            var ranked = new List<MemoryEntry>();
            if (t.Length == 0)
            {
                ranked.AddRange(MemoryRanker.TopK(all, t, _turnWorldDate, Math.Min(12, all.Count)));
            }
            else
            {
                // 精确命中必须先从完整记忆库选出，不能因 TopK 候选池有界而被大量高重要度
                // 无关记忆挤掉；精确结果内部仍复用统一 ranker 排序和去重。
                var exact = new List<MemoryEntry>();
                foreach (var memory in all)
                    if (memory != null && memory.Valid && !string.IsNullOrWhiteSpace(memory.Content)
                        && ((memory.Content ?? string.Empty).IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0
                            || (memory.Keywords ?? string.Empty).IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0))
                        exact.Add(memory);
                ranked.AddRange(MemoryRanker.TopK(exact, t, _turnWorldDate, Math.Min(12, exact.Count)));

                // 精确子串不足时只补充达到词元相关阈值的近似结果，绝不补“近期随便几条”。
                if (ranked.Count < 12)
                {
                    var relevant = new List<MemoryEntry>();
                    foreach (var memory in all)
                        if (!ranked.Contains(memory) && MemoryTopicRelevant(memory, t, topicTokens))
                            relevant.Add(memory);
                    foreach (var memory in MemoryRanker.TopK(relevant, t, _turnWorldDate,
                        Math.Min(12 - ranked.Count, relevant.Count)))
                    {
                        ranked.Add(memory);
                        if (ranked.Count >= 12) break;
                    }
                }
            }
            int n = 0;
            foreach (var m in ranked)
            {
                if (m == null || string.IsNullOrWhiteSpace(m.Content)) continue;
                // 空话题表示“泛泛回想”，可返回统一排序后的要事；窄话题则必须至少有
                // 可解释的词元重合。精确子串没命中时允许近似召回，但绝不回退到无关旧记忆。
                if (t.Length > 0 && !MemoryTopicRelevant(m, t, topicTokens)) continue;
                sb.Append("- ").Append(m.Content.Trim()).Append('\n');
                if (++n >= 12) break;
            }
            return n == 0 ? "(你想不起与此相关的事。)" : sb.ToString().Trim();
        }

        private static bool MemoryTopicRelevant(MemoryEntry memory, string topic, HashSet<string> topicTokens)
        {
            if (memory == null || string.IsNullOrWhiteSpace(topic)) return false;
            string content = memory.Content ?? string.Empty;
            string keywords = memory.Keywords ?? string.Empty;
            if (content.IndexOf(topic, StringComparison.OrdinalIgnoreCase) >= 0
                || keywords.IndexOf(topic, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (topicTokens == null || topicTokens.Count == 0) return false;
            HashSet<string> contentTokens = MemoryRanker.Tokenize(content);
            HashSet<string> keywordTokens = MemoryRanker.Tokenize(keywords);
            int meaningful = 0, contentOverlap = 0, keywordOverlap = 0;
            foreach (string token in topicTokens)
            {
                if (RecallGenericTopicTokens.Contains(token)) continue;
                meaningful++;
                if (keywordTokens.Contains(token)) keywordOverlap++;
                if (contentTokens.Contains(token)) contentOverlap++;
            }
            if (meaningful == 0) return false;

            // Keywords 是记忆写入时的显式召回索引，一个有意义的命中即可；仅命中正文时，
            // 则要求话题至少四分之一被覆盖，避免凭一个泛化二元词误召回。
            return keywordOverlap > 0 || (double)contentOverlap / meaningful >= 0.25;
        }

        private static readonly HashSet<string> RecallGenericTopicTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "太吾", "上次", "之前", "以前", "那次", "那回", "过去", "旧事", "记得", "回忆", "曾经", "当时"
        };

        // 六维主属性标签,顺序与引擎 CurMainAttributes[0..5] 一致:膂力/灵敏/根骨/体质/真气(=内力)/悟性
        private static readonly string[] MainAttrLabels = { "膂力", "灵敏", "根骨", "体质", "真气(内力)", "悟性" };

        private static string BuildStatusText(NpcSnapshot snap, NpcProfileForPrompt prof)
        {
            var sb = new StringBuilder("你当下的状况:");
            if (!string.IsNullOrWhiteSpace(prof.StatusText)) sb.Append(prof.StatusText).Append(';');
            sb.Append("与太吾关系=").Append(string.IsNullOrWhiteSpace(prof.Relation) ? "无特殊" : prof.Relation);
            sb.Append(",好感=").Append(prof.FavorLevel ?? "?");
            if (!string.IsNullOrWhiteSpace(prof.PersonalitiesText)) sb.Append(";赋性=").Append(prof.PersonalitiesText);
            // 六维主属性(含真气/内力);Cur=当前、Max=资质上限。attr 读取失败则跳过,不臆造。
            if (snap != null && snap.CurMainAttr != null && snap.CurMainAttr.Length >= 6)
            {
                sb.Append(";主属性(当前/上限):");
                var parts = new List<string>(6);
                for (int i = 0; i < 6; i++)
                {
                    int cur = snap.CurMainAttr[i];
                    int max = (snap.MaxMainAttr != null && snap.MaxMainAttr.Length >= 6) ? snap.MaxMainAttr[i] : cur;
                    parts.Add(MainAttrLabels[i] + cur + "/" + max);
                }
                sb.Append(string.Join(" ", parts));
                sb.Append("(真气即内力,数值越高内力越雄厚)");
            }
            if (snap != null && snap.ConsummateLevel >= 0)
                sb.Append(";你的武学精纯=").Append((int)snap.ConsummateLevel)
                  .Append("/18(欲杀/绑一个人,须你的精纯【不低于】对方方能得手，相等亦可;需要比较人选时可用 query_person 看对方精纯；直接行动时执行层也会自动核实，低于就据回执换目标)");
            return sb.ToString();
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        // ===================== 以下为名称解析/落地辅助 + 调试暗号(沿用) =====================

        private static void ResolveAndApplyEnmity(int npcId, int taiwuId, string targetText, bool makeEnemy)
        {
            string t = (targetText ?? "").Trim();
            if (t.Length == 0) return;
            if (t == "太吾" || t.Equals("taiwu", StringComparison.OrdinalIgnoreCase))
            { EffectHandler.ApplyEnmity(npcId, taiwuId, makeEnemy); return; }
            EffectHandler.ResolveChar(npcId, t, cid => { if (cid > 0 && cid != npcId) EffectHandler.ApplyEnmity(npcId, cid, makeEnemy); });
        }

        private static string NormName(string s)
        {
            return ItemNameMatcher.Normalize(s);
        }

        private static bool NameHit(string candidate, string query)
        {
            return ItemNameMatcher.IsHit(candidate, query);
        }

        private static string FormatGiftableItem(GiftableItem item)
        {
            if (item == null) return "";
            string name = (item.Name ?? "") + ItemGradeSuffix(item.Key);
            return item.Count > 1 ? (name + "×" + item.Count) : name;
        }

        private static bool GiftableItemHit(GiftableItem item, string query)
        {
            if (item == null) return false;
            if (string.IsNullOrWhiteSpace(query)) return true;
            return NameHit(item.Name, query) || ItemCategoryHit(item.Key, query);
        }

        private static bool ItemCategoryHit(GameData.Domains.Item.ItemKey key, string query)
        {
            string q = ItemNameMatcher.StripItemQueryNoise(ItemNameMatcher.Normalize(query));
            if (string.IsNullOrEmpty(q)) return false;
            sbyte it = key.ItemType; short tpl = key.TemplateId;
            bool isResource = false; try { isResource = GameData.Domains.Item.ItemTemplateHelper.IsMiscResource(it, tpl); } catch { }
            sbyte resType = isResource ? (sbyte)tpl : (sbyte)(-1);
            bool isEquip = it >= 0 && it <= 4;
            bool isFood = it == 7 || it == 9 || (isResource && resType == 0);
            bool isMedicine = it == 8 || (isResource && resType == 5);
            bool isPoison = false; try { isPoison = GameData.Domains.Item.ItemTemplateHelper.GetMedicineItemPoisonType(it, tpl) >= 0; } catch { }
            bool isMaterial = it == 5 || (isResource && resType >= 1 && resType <= 5);
            bool isTool = it == 6;
            bool isBook = it == 10;
            bool isOther = it == 11 || (it == 12 && !isResource);

            if (q.Contains("资源")) return isResource;
            if (q.Contains("食物") || q.Contains("食材") || q.Contains("吃食") || q.Contains("酒") || q.Contains("茶")) return isFood;
            if (q.Contains("药毒")) return isMedicine || isPoison;
            if (q.Contains("毒药") || q == "毒") return isPoison;
            if (q.Contains("药")) return isMedicine;
            if (q.Contains("装备") || q.Contains("武具") || q.Contains("兵器") || q.Contains("衣甲") || q.Contains("佩饰") || q.Contains("衣着")) return isEquip;
            if (q.Contains("书籍") || q == "书" || q.Contains("秘籍") || q.Contains("书本")) return isBook;
            if (q.Contains("工具")) return isTool;
            if (q.Contains("材料") || q.Contains("木料") || q.Contains("金石") || q.Contains("玉石") || q.Contains("布料") || q.Contains("药材") || q.Contains("矿")) return isMaterial;
            if (q.Contains("其他") || q.Contains("杂物") || q.Contains("虫")) return isOther;
            return false;
        }

        // 查询用:把名字列表按 keyword 精确搜(NameHit 模糊匹配);keyword 空=全部。
        // header=有命中/全部时的引导语前缀;emptyMsg=列表本身为空时的话;miss 由调用方据 all 自行补全。
        private static string SearchList(List<string> all, string keyword, string headerAll, string headerHit, out bool searched, out int hits)
        {
            searched = false; hits = all == null ? 0 : all.Count;
            if (all == null || all.Count == 0) return null;
            string kw = (keyword ?? "").Trim();
            if (kw.Length == 0) return headerAll + "(共 " + all.Count + " 样):" + string.Join("、", all);
            searched = true;
            var matched = new List<string>();
            foreach (var n in all) if (NameHit(n, kw)) matched.Add(n);
            hits = matched.Count;
            if (matched.Count > 0) return headerHit + "「" + kw + "」:" + string.Join("、", matched);
            // 没命中:把全部回给模型,让它换个确切名;别让它以为啥都没有
            return "没找到「" + kw + "」。你确有的全部是:" + string.Join("、", all);
        }

        private static List<string> NamesOf<T>(List<T> list, Func<T, string> sel)
        {
            var r = new List<string>();
            if (list != null) foreach (var x in list) { var n = x == null ? null : sel(x); if (!string.IsNullOrWhiteSpace(n)) r.Add(n); }
            return r;
        }

        private IEnumerator GiveItemAndNotice(NpcSnapshot snap, string name, int count, Action<string> onModelResult = null,
            int recipientId = 0, string recipientName = null, string stableOperationId = null,
            bool allowEquipped = false)
        {
            string n = (name ?? "").Trim();
            if (n.Length == 0)
            { onModelResult?.Invoke("(没说要送哪样——先 query_npc_items 看你眼下有什么,再挑确切名原样照填。)"); yield break; }
            int want = count < 1 ? 1 : count;
            string who = string.IsNullOrEmpty(recipientName) ? "太吾" : recipientName;
            // 全实时:只把物名交后端,按 NPC【当前】持有实时解析+按实有封顶(物品随送随变,绝不依赖快照)
            int[] got = { int.MinValue }; string[] failMsg = { null };
            EffectHandler.ApplyGiveItemByName(snap.NpcId, snap.TaiwuId, n, want,
                (amt, msg) => { got[0] = amt; failMsg[0] = msg; }, recipientId,
                allowEquipped, stableOperationId);
            float dl = Time.unscaledTime + RpcReceiptWaitSeconds;
            while (got[0] == int.MinValue && Time.unscaledTime < dl) yield return null;
            if (got[0] == int.MinValue)
            {
                onModelResult?.Invoke(ActMsg(-1, null, null));
            }
            else if (got[0] > 0)
            {
                int g = got[0];
                string label = (g > 1 ? (n + " ×" + g) : n) + (recipientId > 0 ? "(→" + who + ")" : "");
                if (recipientId > 0) Emit(label, null, null, true);
                else Emit(label, "查看持有", CharacterMenuLink.OpenHoldingsPage, true);
                onModelResult?.Invoke("(已把「" + n + "」" + (g > 1 ? ("×" + g) : "") + "赠予" + who + ",可在回话里收尾。)");
            }
            else
            {
                Emit("「" + n + "」未能相赠", null, null, false);
                onModelResult?.Invoke(string.IsNullOrWhiteSpace(failMsg[0])
                    ? "(没送成:你此刻并无「" + n + "」。回上方【随身能直接给/传/吐露的】清单里挑一个确切名重试,或如实相告——别假称已送;那清单里也没有的,就用 query_npc_items 细查。)"
                    : ("(没送成:" + failMsg[0] + ")"));
            }
        }

        private IEnumerator GiveSilverAndNotice(int npcId, int taiwuId, int requested, Action<string> onModelResult = null,
            int recipientId = 0, string recipientName = null, string stableOperationId = null)
        {
            int[] actual = { int.MinValue }; string[] failMsg = { null };
            EffectHandler.ApplyGiveSilver(npcId, taiwuId, requested,
                (amt, msg) => { actual[0] = amt; failMsg[0] = msg; }, recipientId, stableOperationId);
            float dl = Time.unscaledTime + RpcReceiptWaitSeconds;
            while (actual[0] == int.MinValue && Time.unscaledTime < dl) yield return null;
            string who = recipientId > 0 ? (recipientName ?? "他人") : "太吾";
            if (actual[0] > 0)
            {
                if (recipientId > 0) Emit("银钱 " + actual[0] + "(赠" + who + ")", null, null, true);
                else Emit("银钱 " + actual[0], "查看持有", CharacterMenuLink.OpenHoldingsPage, true);
                onModelResult?.Invoke("(已赠/借银钱 " + actual[0] + " 给" + who + ",可收尾。)");
            }
            else if (actual[0] == int.MinValue)   // 超时未确认
                onModelResult?.Invoke("(赠银钱结果未确认,别假称已给。)");
            else   // 0 = 真没给成(最常见:NPC 自己没钱)——据实回喂,杜绝谎报成功
                onModelResult?.Invoke("(没给成:" + (!string.IsNullOrEmpty(failMsg[0]) && failMsg[0] != "ok" ? failMsg[0] : "你囊中羞涩,身无余钱") + "。别假称已赠。)");
        }

        private IEnumerator TeachAndNotice(NpcSnapshot snap, string name, Action<string> onModelResult = null,
            int recipientId = 0, string recipientName = null, string stableOperationId = null)
        {
            if (snap == null || snap.LearnableSkills == null || snap.LearnableSkills.Count == 0)
            { onModelResult?.Invoke("(你并无可传授的武学。若你想传的其实是『技艺』,改用 type=life 再试。)"); yield break; }
            string n = (name ?? "").Trim();
            if (n.Length == 0) { onModelResult?.Invoke("(未指明要传哪门武学,先 query_npc_skills 查清再原样照填。)"); yield break; }
            string who = string.IsNullOrEmpty(recipientName) ? "太吾" : recipientName;
            LearnableSkill pick = null;
            foreach (var sk in snap.LearnableSkills) if (sk != null && sk.Name == n) { pick = sk; break; }
            if (pick == null) foreach (var sk in snap.LearnableSkills) if (sk != null && NameHit(sk.Name, n)) { pick = sk; break; }
            if (pick == null)
            {
                Emit("武学「" + n + "」未能传授(非其所习,或对方已会)", null, null, false);
                onModelResult?.Invoke("(没传成:「" + n + "」非你所习。你可传的武学有:" + CombatSkillsList(snap)
                    + "。从中挑确切名原样照填重试;若「" + n + "」其实是一门『技艺』(非武学),改用 type=life 再试一次。)");
                yield break;
            }
            int[] res = { -1 }; string[] resMsg = { null };
            EffectHandler.ApplyTeachSkillId(snap.NpcId, snap.TaiwuId, pick.TemplateId,
                (ok, msg) => { res[0] = ok ? 1 : 0; resMsg[0] = msg; }, recipientId, stableOperationId);
            float dl = Time.unscaledTime + RpcReceiptWaitSeconds;
            while (res[0] == -1 && Time.unscaledTime < dl) yield return null;
            if (res[0] == 1) { if (recipientId > 0) Emit(pick.Name + "(→" + who + ")", null, null, true); else Emit(pick.Name, "查看武学", CharacterMenuLink.OpenSkillPage, true); onModelResult?.Invoke("(已把武学「" + pick.Name + "」传授" + who + ",可收尾。)"); }
            else if (res[0] == 0)
            {
                string why = !string.IsNullOrEmpty(resMsg[0]) && resMsg[0] != "ok" ? resMsg[0] : (who + "恐已会此武学");
                Emit("武学「" + pick.Name + "」未能传授(" + why + ")", null, null, false);
                onModelResult?.Invoke("(没传成:" + why + "。" + (recipientId > 0 ? "" : "若是已会就换一门、或作罢") + "——别假称已传。)");
            }
            else onModelResult?.Invoke("(传授结果未确认,别假称已传。)");
        }

        // 传技艺(生活技能)→ 后端 teachlife,成功后跳"技艺"页(任务11:跳转技艺页面)
        private IEnumerator TeachLifeAndNotice(NpcSnapshot snap, string name, Action<string> onModelResult = null,
            int recipientId = 0, string recipientName = null, string stableOperationId = null)
        {
            if (snap == null || snap.LearnableLifeSkills == null || snap.LearnableLifeSkills.Count == 0)
            { Emit("你并无可传授的技艺", null, null, false); onModelResult?.Invoke("(你并无可传授的技艺。若你想传的其实是『武学』,改用 type=combat 再试。)"); yield break; }
            string n = (name ?? "").Trim();
            string who = string.IsNullOrEmpty(recipientName) ? "太吾" : recipientName;
            LearnableSkill pick = null;
            if (n.Length == 0) pick = snap.LearnableLifeSkills[0];
            else
            {
                foreach (var sk in snap.LearnableLifeSkills) if (sk != null && sk.Name == n) { pick = sk; break; }
                if (pick == null) foreach (var sk in snap.LearnableLifeSkills) if (sk != null && NameHit(sk.Name, n)) { pick = sk; break; }
            }
            if (pick == null)
            {
                Emit("技艺「" + n + "」未能传授(非其所通,或对方已会)", null, null, false);
                onModelResult?.Invoke("(没传成:「" + n + "」非你所通的技艺。你可传的技艺有:" + LifeSkillsList(snap)
                    + "。从中挑确切名原样照填重试;若「" + n + "」其实是一门『武学』,改用 type=combat 再试一次。)");
                yield break;
            }
            int[] res = { -1 }; string[] resMsg = { null };
            EffectHandler.ApplyTeachLifeSkill(snap.NpcId, snap.TaiwuId, pick.TemplateId,
                (ok, msg) => { res[0] = ok ? 1 : 0; resMsg[0] = msg; }, recipientId, stableOperationId);
            float dl = Time.unscaledTime + RpcReceiptWaitSeconds;
            while (res[0] == -1 && Time.unscaledTime < dl) yield return null;
            if (res[0] == 1) { if (recipientId > 0) Emit(pick.Name + "(→" + who + ")", null, null, true); else Emit(pick.Name, "查看技艺", CharacterMenuLink.OpenLifeSkillPage, true); onModelResult?.Invoke("(已把技艺「" + pick.Name + "」传授" + who + ",可收尾。)"); }
            else if (res[0] == 0)
            {
                string why = !string.IsNullOrEmpty(resMsg[0]) && resMsg[0] != "ok" ? resMsg[0] : (who + "恐已通此技艺");
                Emit("技艺「" + pick.Name + "」未能传授(" + why + ")", null, null, false);
                onModelResult?.Invoke("(没传成:" + why + "。" + (recipientId > 0 ? "" : "若是已通就换一门、或作罢") + "——别假称已传。)");
            }
            else onModelResult?.Invoke("(传授结果未确认,别假称已传。)");
        }

        // 回忆赠书:NPC 凭记忆把它会的一门武学/技艺回忆成完整的书,虚空成册赠太吾。候选取"会而太吾尚无"的清单(同 teach),
        // 名字解析失败回喂候选让模型自纠;onModelResult(回执文本, 是否成功)。
        private IEnumerator WriteBookAndNotice(NpcSnapshot snap, string kind, string name,
            Action<string, bool> onModelResult = null, int recipientId = 0, string recipientName = null,
            string stableOperationId = null)
        {
            bool life = kind == "life";
            var pool = life ? snap?.LearnableLifeSkills : snap?.LearnableSkills;
            if (pool == null || pool.Count == 0)
            { onModelResult?.Invoke(life ? "(你没有可回忆成册的技艺。若想回忆的其实是武学,改用 type=combat 再试。)" : "(你没有可回忆成册的武学。若想回忆的其实是技艺,改用 type=life 再试。)", false); yield break; }
            string n = (name ?? "").Trim();
            string who = string.IsNullOrEmpty(recipientName) ? "太吾" : recipientName;
            LearnableSkill pick = null;
            if (n.Length == 0) pick = pool[0];
            else { foreach (var s in pool) if (s != null && s.Name == n) { pick = s; break; }
                   if (pick == null) foreach (var s in pool) if (s != null && NameHit(s.Name, n)) { pick = s; break; } }
            if (pick == null)
            {
                Emit((life ? "技艺「" : "武学「") + n + "」未能回忆成书(非你所习,或对方已会)", null, null, false);
                onModelResult?.Invoke("(没回忆成书:「" + n + "」非你所习。你可回忆成册的" + (life ? ("技艺有:" + LifeSkillsList(snap)) : ("武学有:" + CombatSkillsList(snap)))
                    + "。从中挑确切名原样照填;若把 type 填反了,换 " + (life ? "combat" : "life") + " 再试。)", false);
                yield break;
            }
            int[] res = { -1 }; string[] bn = { null }; int[] lostP = { 0 };
            EffectHandler.ApplyWriteBook(snap.NpcId, snap.TaiwuId, life ? "life" : "combat", pick.TemplateId,
                (ok, msg, lost) => { res[0] = ok ? 1 : 0; bn[0] = msg; lostP[0] = lost; }, recipientId, stableOperationId);
            float dl = Time.unscaledTime + RpcReceiptWaitSeconds;
            while (res[0] == -1 && Time.unscaledTime < dl) yield return null;
            if (res[0] == 1)
            {
                string bookNm = bn[0] ?? pick.Name;
                if (recipientId > 0) Emit("回忆《" + bookNm + "》赠" + who + (lostP[0] > 0 ? "(残" + lostP[0] + "页)" : ""), null, null, true);
                else Emit("回忆《" + bookNm + "》相赠" + (lostP[0] > 0 ? "(残" + lostP[0] + "页)" : ""), "查看持有", CharacterMenuLink.OpenHoldingsPage, true);
                    // NPC 凭记忆回忆成书,自己清楚有没有残页——把残页数告知模型,让它在回话里如实体现
                    string pageNote = lostP[0] > 0
                    ? ("。你是凭记忆回忆成书的,这一部你只记得大半,有 " + lostP[0] + " 页字句记不真切、写得残缺漫漶(并非整页空缺,仍可研习、只是要慢慢参详补全)——你心里清楚这几页残了,可在回话里如实提一句(如『此谱我只回忆得出大半,有几页字句记不真、残缺了,你将就着参详』)")
                    : "。这一部你记得齐全、回忆无残,可如实言明是完好的";
                onModelResult?.Invoke("(已回忆《" + bookNm + "》成册赠予" + who + pageNote + ",可在回话里收尾。)", true);
            }
            else if (res[0] == 0)
            {
                Emit((life ? "技艺「" : "武学「") + pick.Name + "」未能回忆成书(" + (bn[0] ?? "") + ")", null, null, false);
                onModelResult?.Invoke("(没回忆成书:" + (bn[0] ?? "原因不明") + "。别假称已赠。)", false);
            }
            else onModelResult?.Invoke("(回忆成书结果未知,别假称已赠。)", false);
        }

        // 商人随身货物名清单(供"卖了不存在的货"时把真实货名喂回模型,让它自纠);赠物落空也复用此清单
        // 可传授武学/技艺名清单(传授落空时把真实可传名喂回模型,让它挑确切名或换 type 重试)
        private static string CombatSkillsList(NpcSnapshot snap)
        {
            if (snap == null || snap.LearnableSkills == null || snap.LearnableSkills.Count == 0) return "(无)";
            var names = new List<string>();
            foreach (var s in snap.LearnableSkills) if (s != null && !string.IsNullOrWhiteSpace(s.Name)) names.Add(s.Name);
            return names.Count == 0 ? "(无)" : string.Join("、", names);
        }
        private static LearnableSkill FindCombatSkill(NpcSnapshot snap, string requested)
        {
            if (snap?.LearnableSkills == null || string.IsNullOrWhiteSpace(requested)) return null;
            string query = requested.Trim();
            foreach (LearnableSkill skill in snap.LearnableSkills)
                if (skill != null && string.Equals(skill.Name, query, StringComparison.Ordinal)) return skill;
            foreach (LearnableSkill skill in snap.LearnableSkills)
                if (skill != null && NameHit(skill.Name, query)) return skill;
            return null;
        }
        private static string TrainableSkillsList(NpcSnapshot snap)
        {
            if (snap == null || !snap.StudyProgressLoaded
                || snap.LearnableSkills == null || snap.IncompleteTrainingSkillIds == null) return "(读取失败)";
            var names = new List<string>();
            foreach (LearnableSkill skill in snap.LearnableSkills)
                if (skill != null && !string.IsNullOrWhiteSpace(skill.Name)
                    && snap.IncompleteTrainingSkillIds.Contains(skill.TemplateId)) names.Add(skill.Name);
            return names.Count == 0 ? "(无)" : string.Join("、", names);
        }
        private static string FindUnreadBookName(NpcSnapshot snap, string requested)
        {
            if (snap == null || !snap.StudyProgressLoaded || snap.UnreadBookNames == null) return null;
            string query = JianghuYouling.Shared.ItemNameMatcher.StripCountSuffix(
                (requested ?? string.Empty).Trim());
            foreach (string candidate in snap.UnreadBookNames)
                if (!string.IsNullOrWhiteSpace(candidate)
                    && JianghuYouling.Shared.ItemNameMatcher.IsHit(candidate, query)) return candidate.Trim();
            return null;
        }
        private static string UnreadBooksList(NpcSnapshot snap)
        {
            if (snap == null || !snap.StudyProgressLoaded || snap.UnreadBookNames == null) return "(读取失败)";
            var names = new List<string>();
            foreach (string name in snap.UnreadBookNames)
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
            return names.Count == 0 ? "(无)" : string.Join("、", names);
        }
        private static string FindUsableItemName(NpcSnapshot snap, string requested)
        {
            if (snap == null || !snap.UsableItemsLoaded || snap.UsableItemNames == null) return null;
            string query = JianghuYouling.Shared.ItemNameMatcher.StripCountSuffix(
                (requested ?? string.Empty).Trim());
            foreach (string candidate in snap.UsableItemNames)
                if (!string.IsNullOrWhiteSpace(candidate)
                    && JianghuYouling.Shared.ItemNameMatcher.IsHit(candidate, query)) return candidate.Trim();
            return null;
        }
        private static string UsableItemsList(NpcSnapshot snap)
        {
            if (snap == null || !snap.UsableItemsLoaded || snap.UsableItemNames == null) return "(读取失败)";
            var names = new List<string>();
            foreach (string name in snap.UsableItemNames)
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
            return names.Count == 0 ? "(无)" : string.Join("、", names);
        }
        private static string LifeSkillsList(NpcSnapshot snap)
        {
            if (snap == null || snap.LearnableLifeSkills == null || snap.LearnableLifeSkills.Count == 0) return "(无)";
            var names = new List<string>();
            foreach (var s in snap.LearnableLifeSkills) if (s != null && !string.IsNullOrWhiteSpace(s.Name)) names.Add(s.Name);
            return names.Count == 0 ? "(无)" : string.Join("、", names);
        }

        // 商人售货:在商人随身货物里按名定位,后端转移物品 + 太吾按议价付银钱(NPC-driven 主动售货)。
        // onModelResult:把成败回喂给模型——尤其"货里没这名字"时列出真实货名,让它别再编造、用确切名重试。
        // 实时读商人当前真·货架(店铺/行商/现生成),解析成 name+key;库存随卖随变,故每次实时查、绝不缓存。
        private static IEnumerator QueryLiveMerchantGoods(NpcSnapshot snap, Action<List<GiftableItem>> onResult)
        {
            var fresh = new List<GiftableItem>();
            if (snap == null || !snap.IsMerchant) { onResult(fresh); yield break; }
            bool done = false; MerchantGoodsQueryResult query = null;
            EffectHandler.QueryMerchantGoodsWithSource(snap.NpcId, snap.MerchantTemplateId,
                value => { query = value; done = true; });
            float dl = Time.unscaledTime + RpcReceiptWaitSeconds;
            while (!done && Time.unscaledTime < dl) yield return null;
            List<KeyValuePair<GameData.Domains.Item.ItemKey, int>> live = query?.Goods;
            if (live != null)
            {
                var seen = new HashSet<string>();
                foreach (var kv in live)
                {
                    var k = kv.Key;
                    string nm; try { nm = StripTags(GameData.Domains.Item.ItemTemplateHelper.GetName(k.ItemType, k.TemplateId)); } catch { continue; }
                    if (!string.IsNullOrWhiteSpace(nm) && seen.Add(nm)) fresh.Add(new GiftableItem
                    {
                        Name = nm, Key = k, Count = kv.Value,
                        MerchantOwnerType = query?.OwnerType ?? -1,
                        MerchantOwnerId = query?.OwnerId ?? -1
                    });
                }
            }
            onResult(fresh);
        }

        private IEnumerator TradeAndNotice(NpcSnapshot snap, string name, int amount, int price,
            Action<string> onModelResult, string stableOperationId = null)
        {
            List<GiftableItem> sellable = null;
            yield return QueryLiveMerchantGoods(snap, g => sellable = g);   // 实时读货架(现卖现查,库存随卖随变)
            if (sellable == null || sellable.Count == 0)
            { Emit("货架暂无现货", null, null, false); onModelResult?.Invoke("(没卖成:你货架上暂无现货可售。如实告诉太吾即可——切勿拿你随身私物充货、也别报价、别假称已卖。)"); yield break; }
            string q = (name ?? "").Trim();
            q = System.Text.RegularExpressions.Regex.Replace(q, @"[×xX]\s*\d+\s*$", "").Trim();   // 容错:模型若把"蜀锦×3"整串当货名填,剥掉尾部数量
            GiftableItem pick = null;
            if (q.Length == 0) pick = sellable[0];
            else
            {
                foreach (var g in sellable) if (g != null && g.Name == q) { pick = g; break; }
                if (pick == null) foreach (var g in sellable) if (g != null && NameHit(g.Name, q)) { pick = g; break; }
            }
            if (pick == null)
            {
                Emit("你货里没有「" + q + "」,无从成交", null, null, false);
                onModelResult?.Invoke("(没卖成:你货里并没有「" + q + "」这件东西。你真正可售的货物只有:" + string.Join("、", NamesOf(sellable, g => g.Name))
                    + "。务必从中挑确切的货名原样照填重试,或如实告诉太吾你没有他要的货——切勿编造不存在的货名,别假称已卖。)");
                yield break;
            }
            int[] st = { -1 }, amt = { 0 }, pay = { 0 }; string[] em = { null };
            EffectHandler.ApplyTrade(snap.NpcId, snap.TaiwuId, pick.Key, amount, price, snap.MerchantTemplateId,
                pick.MerchantOwnerType, pick.MerchantOwnerId,
                (ok, a, p, m) => { st[0] = ok ? 1 : 0; amt[0] = a; pay[0] = p; em[0] = m; }, stableOperationId);
            float dl = Time.unscaledTime + RpcReceiptWaitSeconds;
            while (st[0] == -1 && Time.unscaledTime < dl) yield return null;
            if (st[0] == 1 && amt[0] > 0)
            {
                Emit(pick.Name + (amt[0] > 1 ? " ×" + amt[0] : "") + "(付银钱 " + pay[0] + ")", "查看持有", CharacterMenuLink.OpenHoldingsPage, true);
                onModelResult?.Invoke("(成交:卖出「" + pick.Name + "」×" + amt[0] + ",得银 " + pay[0] + "。可在回话里收尾。)");
            }
            else if (st[0] == -1)
            {
                onModelResult?.Invoke(ActMsg(-1, null, null));
            }
            else
            {
                Emit("交易未成:" + (em[0] ?? "(太吾银钱不足或货已无)"), null, null, false);
                onModelResult?.Invoke("(交易未成:" + (em[0] ?? "太吾银钱不足或货已无") + "。如实告知太吾,别假称已卖。)");
            }
        }

        // 读太吾当前武学装配(主修内功 + 攻击/身法/防御/辅助),映射 Config.CombatSkill 名;供 NPC 给搭配建议前查实。
        private static IEnumerator ReadTaiwuBuild(int taiwuId, Action<string> onText)
        {
            yield return ReadCharacterBuild(taiwuId, "太吾", onText);
        }

        // 读任意角色当前运功 + 武学装配。太吾和 NPC 复用同一条路,避免"太吾能查、NPC 自查不能查"。
        private static IEnumerator ReadCharacterBuild(int charId, string ownerLabel, Action<string> onText)
        {
            if (charId <= 0) { onText(null); yield break; }
            string who = string.IsNullOrWhiteSpace(ownerLabel) ? "此人" : ownerLabel.Trim();
            GameData.Domains.Character.CombatSkillEquipment eq = default(GameData.Domains.Character.CombatSkillEquipment);
            bool ok = false, done = false;
            GameData.Domains.CombatSkill.CombatSkillDomainMethod.AsyncCall.GetCombatSkillEquipment(null, charId, (offset, pool) =>
            {
                try { GameData.Serializer.Serializer.Deserialize(pool, offset, ref eq); ok = true; } catch { } finally { done = true; }
            });
            float dl = Time.unscaledTime + RpcReceiptWaitSeconds;
            while (!done && Time.unscaledTime < dl) yield return null;
            if (!ok) { onText(null); yield break; }
            var sb = new StringBuilder(who + "当前功法搭配:");
            bool hasCombatLine = false;

            short[] looping = { -2 }; bool loopingDone = false;
            EffectHandler.QueryLoopingNeigong(charId, x => { looping[0] = x; loopingDone = true; });
            float ldl = Time.unscaledTime + 3f;
            while (!loopingDone && Time.unscaledTime < ldl) yield return null;
            if (loopingDone)
            {
                sb.Append("\n正在运功:");
                if (looping[0] >= 0) { sb.Append(CombatSkillName(looping[0])); hasCombatLine = true; }
                else sb.Append("无");
            }

            string[] labels = { "主修内功", "攻击", "身法", "防御", "辅助" };
            for (sbyte ty = 0; ty < 5; ty++)
            {
                var ids = new List<short>();
                try { eq.GetValidSkills(ty, ids); } catch { }
                if (ids.Count == 0) continue;
                var names = new List<string>();
                foreach (var id in ids)
                {
                    if (id < 0) continue;
                    string nm = CombatSkillName(id);
                    if (!string.IsNullOrWhiteSpace(nm)) names.Add(nm);
                }
                if (names.Count > 0) { hasCombatLine = true; sb.Append('\n').Append(labels[ty]).Append(':').Append(string.Join("、", names)); }
            }
            if (!hasCombatLine) sb.Append("\n装配功法:无");

            // 所穿装备(兵器/衣甲/佩饰):GetEquipmentKeys → ItemTemplateHelper.GetName
            List<GameData.Domains.Item.ItemKey> eqKeys = null; bool eqDone = false;
            GameData.Domains.Character.CharacterDomainMethod.AsyncCall.GetEquipmentKeys(null, charId, (offset, pool) =>
            {
                try { GameData.Serializer.Serializer.Deserialize(pool, offset, ref eqKeys); } catch { } finally { eqDone = true; }
            });
            float dl2 = Time.unscaledTime + 3f;
            while (!eqDone && Time.unscaledTime < dl2) yield return null;
            if (eqKeys != null && eqKeys.Count > 0)
            {
                var enames = new List<string>();
                var seen2 = new HashSet<string>();
                foreach (var key in eqKeys)
                {
                    if (!key.IsValid()) continue;
                    string nm; try { nm = StripTags(GameData.Domains.Item.ItemTemplateHelper.GetName(key.ItemType, key.TemplateId)); } catch { continue; }
                    if (!string.IsNullOrWhiteSpace(nm) && seen2.Add(nm)) enames.Add(nm);
                }
                if (enames.Count > 0) sb.Append("\n所穿装备:").Append(string.Join("、", enames));
            }
            onText(sb.ToString());
        }

        private static string CombatSkillName(short id)
        {
            try
            {
                var nm = Config.CombatSkill.Instance[id].Name;
                if (!string.IsNullOrWhiteSpace(nm)) return StripTags(nm);
            }
            catch { }
            return "#" + id;
        }

        private static string StripTags(string s) =>
            string.IsNullOrEmpty(s) ? s : System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "").Trim();

        private void EquipNamedItem(NpcSnapshot snap, string name)
        {
            if (snap == null) return;
            string n = (name ?? "").Trim();
            if (n.Length == 0) return;
            // 全实时:按名交后端实时解析 NPC【当前】持有 + 定槽校验(装备随穿随脱,绝不依赖快照)
            EffectHandler.ApplyEquipByName(snap.NpcId, n, (ok, msg) =>
                Emit(ok ? ("换上了「" + n + "」") : ("「" + n + "」未能换上" + (string.IsNullOrWhiteSpace(msg) ? "" : ":" + msg)), null, null, ok));
        }

        private static string PartZh(string p)
        {
            switch (p) { case "clothing": return "衣物"; case "armor": return "护具"; case "accessory": return "佩饰"; case "carrier": return "代步"; default: return "兵器"; }
        }

        private void ResolveAndDiscloseSecret(int npcId, int taiwuId, string targetText, SecretRef sref,
            Action<bool, string> onDone = null, string stableOperationId = null)
        {
            if (sref == null) { onDone?.Invoke(false, "无此秘闻"); return; }
            string t = (targetText ?? "").Trim();
            if (t.Length == 0) { onDone?.Invoke(false, "没说传给谁"); return; }
            void PreflightAndApply(int targetId)
            {
                EffectHandler.QueryCanDiscloseSecret(sref.Id, npcId, targetId, (can, reason) =>
                {
                    if (!can) { onDone?.Invoke(false, reason ?? "这桩秘闻当前不可向对方传播"); return; }
                    EffectHandler.ApplyDiscloseSecret(sref.Id, npcId, targetId, onDone, stableOperationId);
                });
            }
            if (t == "太吾" || t.Equals("taiwu", StringComparison.OrdinalIgnoreCase))
            { PreflightAndApply(taiwuId); return; }
            EffectHandler.ResolveChar(npcId, t, cid =>
            {
                if (cid > 0 && cid != npcId) PreflightAndApply(cid);
                else { Emit("未找到「" + t + "」,无从传告", null, null, false); onDone?.Invoke(false, "找不到「" + t + "」这个人——只能传给你认识的人或太吾"); }
            });
        }

        private void ResolveAndMatchmake(int npcId, int taiwuId, string targetText,
            Action<bool, string> onDone = null, string stableOperationId = null)
        {
            string t = (targetText ?? "").Trim();
            if (t.Length == 0) { onDone?.Invoke(false, "没说与谁做媒"); return; }
            // 先在【这位 NPC 自己的关系网 + 同处一地】里找对象(引擎不要求太吾认识此人);找不到再退回太吾关系网。
            EffectHandler.ResolveChar(npcId, t, true, (cid1, rsn1) =>
            {
                void Do(int cid)
                {
                    if (cid <= 0 || cid == npcId) { Emit("未找到「" + t + "」,无从说媒", null, null, false); onDone?.Invoke(false, "找不到「" + t + "」这个人——撮合的对象须是他相识、或同在一处之人"); return; }
                    EffectHandler.ApplyMatchmake(npcId, cid, (ok, msg) => { Emit(ok ? "喜结连理" : ("未能成婚:" + (msg ?? "")), null, null, ok); onDone?.Invoke(ok, msg); }, stableOperationId);
                }
                if (cid1 > 0 && cid1 != npcId) Do(cid1);
                else EffectHandler.ResolveChar(taiwuId, t, cid2 => Do(cid2));
            });
        }

        private static void ResolveAndThirdPartyFavor(int npcId, string targetText, int delta)
        {
            string t = (targetText ?? "").Trim();
            if (t.Length == 0 || delta == 0) return;
            EffectHandler.ResolveChar(npcId, t, cid => { if (cid > 0 && cid != npcId) EffectHandler.ApplyThirdPartyFavor(npcId, cid, delta); });
        }

        private static void ResolveAndRelease(int npcId, string targetText)
        {
            string t = (targetText ?? "").Trim();
            if (t.Length == 0) return;
            EffectHandler.ResolveChar(npcId, t, cid => { if (cid > 0 && cid != npcId) EffectHandler.ApplyRelease(npcId, cid, (ok, msg) => { }); });
        }

        // —— 调试暗号 ——
        private static bool IsDebugCommandInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return false;
            char first = input.TrimStart()[0];
            return first == '/' || first == '／';
        }

        private bool HandleTestCommand(NpcSnapshot snap, string input, Action<string> onReply)
        {
            // 玩家键入的 /<暗号> 绕过主对话模型，只作用于本机单机存档；/查生平的较早记录
            // 仍按正式接口使用后台模型维护事实摘要。/帮助 会列出完整测试面。
            // 这不是 LLM 可达的动作面,与"模型不得擅自改游戏状态"的信任
            // 边界无关,故 Debug/Release 均保留,供作者实机测试难以自然触发的功能。
            if (string.IsNullOrWhiteSpace(input)) return false;
            string s = input.Trim();
            if (s[0] == '／') s = "/" + s.Substring(1);
            if (s.Length < 1 || s[0] != '/') return false;
            var parts = s.Substring(1).Replace('　', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) { onReply?.Invoke(TestHelp()); return true; }

            int npc = snap.NpcId, taiwu = snap.TaiwuId;
            string act = parts[0];
            string a1 = parts.Length > 1 ? parts[1] : null;
            string a2 = parts.Length > 2 ? parts[2] : null;
            string a3 = parts.Length > 3 ? parts[3] : null;
            Action<bool, string> log = (ok, m) => Debug.Log("[江湖有灵][暗号] " + ok + " " + m);
            string note;
            switch (act)
            {
                case "help": case "帮助": case "列表": case "?": onReply?.Invoke(TestHelp()); return true;
                case "工具列表": case "全部工具":
                    {
                        var names = new List<string>(DebuggableToolNames());
                        names.Sort(StringComparer.Ordinal);
                        onReply?.Invoke("【正式工具调试入口 · " + names.Count + " 项】\n"
                            + string.Join("\n", names.ToArray())
                            + "\n\n用法:/工具 <上列名称> [JSON参数]。该入口与 AI 对话复用同一执行器。");
                        return true;
                    }
                case "工具": case "执行工具":
                    {
                        if (a1 == null)
                        {
                            onReply?.Invoke("用法:/工具 <正式工具名> [JSON参数]；输入 /工具列表 查看全部");
                            return true;
                        }
                        var allowed = DebuggableToolNames();
                        if (!allowed.Contains(a1))
                        {
                            onReply?.Invoke("没有正式工具「" + a1 + "」。输入 /工具列表 查看全部。");
                            return true;
                        }
                        JObject toolArgs = new JObject();
                        int nameAt = s.IndexOf(a1, StringComparison.Ordinal);
                        string rawArgs = nameAt < 0 ? "" : s.Substring(nameAt + a1.Length).Trim();
                        if (!string.IsNullOrWhiteSpace(rawArgs))
                        {
                            try { toolArgs = JObject.Parse(rawArgs); }
                            catch (Exception ex)
                            {
                                onReply?.Invoke("JSON 参数无效:" + ex.GetType().Name
                                    + "。示例:/工具 query_person {\"name\":\"人物名\"}");
                                return true;
                            }
                        }
                        return QueueDebugTool(snap, a1, toolArgs, onReply);
                    }
                case "llmstat": case "性能": case "llm":   // 大模型调用性能统计:各处调用数/总耗时/均耗时/峰值/token,看哪里慢/调用多
                    {
                        if (a1 == "reset" || a1 == "清零") { JianghuYouling.Core.Llm.LlmLog.Reset(); onReply?.Invoke("(已清零大模型性能统计)"); return true; }
                        string sum = JianghuYouling.Core.Llm.LlmLog.Summary();
                        Debug.Log(sum); onReply?.Invoke(sum); return true;
                    }
                // —— 与 ToolRegistry 一一对应的只读查询暗号。全部复用真实工具执行器，
                // 防止调试面另写一套查询逻辑后与正式工具渐行渐远。——
                case "查记忆": case "回想记忆": return QueueDebugTool(snap, "recall_memory", new JObject { ["topic"] = a1 ?? "" }, onReply);
                case "查状态": return QueueDebugTool(snap, "query_npc_status", new JObject(), onReply);
                case "查身体": case "查健康": return QueueDebugTool(snap, "query_health_status", new JObject(), onReply);
                case "查关系": return QueueDebugTool(snap, "query_npc_relationships", new JObject { ["name"] = a1 ?? "" }, onReply);
                case "查生平": return QueueDebugTool(snap, "query_npc_history", new JObject(), onReply);
                case "查物品": return QueueDebugTool(snap, "query_npc_items", new JObject { ["keyword"] = a1 ?? "" }, onReply);
                case "查人物物品": case "查他人物品":
                    if (a1 == null) { onReply?.Invoke("用法:/查人物物品 <人名> [关键词]"); return true; }
                    return QueueDebugTool(snap, "query_person_items", new JObject { ["person"] = a1, ["keyword"] = a2 ?? "" }, onReply);
                case "查人物":
                    if (a1 == null) { onReply?.Invoke("用法:/查人物 <人名>"); return true; }
                    return QueueDebugTool(snap, "query_person", new JObject { ["name"] = a1 }, onReply);
                case "查同地": case "查当前地块": return QueueDebugTool(snap, "query_current_block", new JObject(), onReply);
                case "查地区人物":
                    if (a1 == null) { onReply?.Invoke("用法:/查地区人物 <地名>"); return true; }
                    return QueueDebugTool(snap, "query_area_people", new JObject { ["place"] = a1 }, onReply);
                case "查门派成员": return QueueDebugTool(snap, "query_org_members", new JObject { ["sect"] = a1 ?? "" }, onReply);
                case "查功法": case "查武学": return QueueDebugTool(snap, "query_npc_skills", new JObject { ["keyword"] = a1 ?? "" }, onReply);
                case "查配招": return QueueDebugTool(snap, "query_npc_build", new JObject(), onReply);
                case "查秘闻": return QueueDebugTool(snap, "query_npc_secrets", new JObject(), onReply);
                case "查太吾配招": return QueueDebugTool(snap, "query_taiwu_build", new JObject(), onReply);
                case "查太吾物品": return QueueDebugTool(snap, "query_taiwu_items", new JObject { ["keyword"] = a1 ?? "" }, onReply);
                case "查太吾功法": case "查太吾武学": return QueueDebugTool(snap, "query_taiwu_skills", new JObject { ["keyword"] = a1 ?? "" }, onReply);
                case "世界进度": case "世道": return QueueDebugTool(snap, "query_world_progress", new JObject(), onReply);
                case "查百科":
                    return QueueDebugTool(snap, "query_lore", new JObject { ["path"] = a1 ?? "" }, onReply);
                case "查地点": return QueueDebugTool(snap, "query_place", new JObject { ["keyword"] = a1 ?? "" }, onReply);
                case "查门派设定": return QueueDebugTool(snap, "query_sect_lore", new JObject(), onReply);
                case "查太吾秘闻": return QueueDebugTool(snap, "query_taiwu_secrets", new JObject(), onReply);
                case "查行动指南": case "行动指南":
                    if (a1 == null) { onReply?.Invoke("用法:/行动指南 <技能id>（例如 secrets、combat、items）"); return true; }
                    return QueueDebugTool(snap, "consult_action_guide", new JObject { ["skill"] = a1 }, onReply);
                case "反应":
                    return QueueDebugTool(snap, "record_reaction", new JObject
                    {
                        ["satisfaction"] = Num(a1, 0), ["mood"] = Num(a2, 0), ["alertness_shift"] = Num(a3, 0)
                    }, onReply);
                case "记住": case "写记忆":
                    if (a1 == null) { onReply?.Invoke("用法:/记住 <内容> [恩情|仇怨|承诺|秘密|印象|事件]"); return true; }
                    return QueueDebugTool(snap, "remember", new JObject { ["content"] = a1, ["type"] = a2 ?? "事件", ["importance"] = 4 }, onReply);
                case "换物": case "以物换物":
                    if (a1 == null || a2 == null) { onReply?.Invoke("用法:/换物 <NPC给出的物品> <太吾给出的物品> [数量]"); return true; }
                    return QueueDebugTool(snap, "barter", new JObject
                    {
                        ["item_a"] = a1, ["amount_a"] = Num(a3, 1), ["person_b"] = "太吾",
                        ["item_b"] = a2, ["amount_b"] = Num(a3, 1)
                    }, onReply);
                case "偷窃": case "偷":
                    if (a1 == null || a2 == null) { onReply?.Invoke("用法:/偷窃 <被偷者> <物品名> [数量]（偷窃者固定为当前NPC）"); return true; }
                    return QueueDebugTool(snap, "steal", new JObject { ["victim"] = a1, ["item"] = a2, ["amount"] = Num(a3, 1) }, onReply);
                case "约战":
                    {
                        if (a1 != null && a1 != "切磋" && a1 != "相搏" && a1 != "死斗")
                        { onReply?.Invoke("用法:/约战 [切磋|相搏|死斗] [NPC|太吾]；不填类型时安全默认为切磋。"); return true; }
                        if (a2 != null && a2 != "NPC" && a2 != "npc" && a2 != "太吾")
                        { onReply?.Invoke("发起者只能填 NPC 或太吾。"); return true; }
                        string mode = a1 == "相搏" ? "beat" : a1 == "死斗" ? "die" : "play";
                        string initiator = a2 == "太吾" ? "taiwu" : "npc";
                        return QueueDebugTool(snap, "start_combat", new JObject { ["opponent"] = "taiwu", ["mode"] = mode, ["initiator"] = initiator, ["reason"] = "调试暗号" }, onReply);
                    }
                case "正逆练": case "翻练法":
                    if (a1 == null) { onReply?.Invoke("用法:/正逆练 <功法名> [人物名,默认当前NPC]"); return true; }
                    return QueueDebugTool(snap, "flip_practice", new JObject { ["skill"] = a1, ["person"] = a2 ?? "" }, onReply);
                case "驱毒":
                    return QueueDebugTool(snap, "detox", new JObject { ["target"] = a1 ?? "太吾" }, onReply);
                case "调息":
                    return QueueDebugTool(snap, "regulate_breath", new JObject { ["target"] = a1 ?? "太吾" }, onReply);
                case "练功":
                    if (a1 == null) { onReply?.Invoke("用法:/练功 <当前NPC已会且未练满的武学名>"); return true; }
                    return QueueDebugTool(snap, "train_skill", new JObject { ["skill"] = a1 }, onReply);
                case "读书":
                    if (a1 == null) { onReply?.Invoke("用法:/读书 <当前NPC持有且未读完的书名>"); return true; }
                    return QueueDebugTool(snap, "read_book", new JObject { ["book"] = a1 }, onReply);
                case "太吾写书":
                    if (a1 == null) { onReply?.Invoke("用法:/太吾写书 <太吾会的武学或技艺名> [life=技艺]"); return true; }
                    return QueueDebugTool(snap, "taiwu_write_book", new JObject
                    {
                        ["name"] = a1, ["type"] = (a2 == "life" || a2 == "技艺") ? "life" : "combat"
                    }, onReply);
                case "好感": EffectHandler.ApplyFavor(npc, taiwu, Num(a1, 1500)); note = "好感 +" + Num(a1, 1500); break;
                case "降好感": EffectHandler.ApplyFavor(npc, taiwu, -Num(a1, 1500)); note = "好感 -" + Num(a1, 1500); break;
                case "心情": EffectHandler.ApplyHappiness(npc, Num(a1, 20)); note = "心情 " + Num(a1, 20); break;
                case "挚友": EffectHandler.ApplyRelation(npc, taiwu, "best_friend", log); note = "结挚友"; break;
                case "结义": EffectHandler.ApplyRelation(npc, taiwu, "sworn_sibling", log); note = "结义"; break;
                case "师徒": EffectHandler.ApplyRelation(npc, taiwu, "mentor", log); note = "拜师"; break;
                case "认可": EffectHandler.ApplyRecognizeTaiwu(npc, log); note = "认可太吾"; break;
                case "投奔":
                    if (a1 != null && a1 != "太吾" && a1 != "太吾村" && a1 != "太吾门派")
                        EffectHandler.ApplyChangeOrgByName(npc, a1, (ok, m) => Emit(ok ? ("已投奔" + a1) : ("未能投奔:" + (m ?? "认不出该门派")), null, null, ok));
                    else EffectHandler.ApplyJoinTaiwuSect(npc);
                    note = "投奔 " + (a1 ?? "太吾门派"); break;
                case "门派支持": case "支持": EffectHandler.ApplySectSupport(npc); note = "门派表态支持太吾"; break;
                case "立场": EffectHandler.ApplyChangeMorality(npc, Num(a1, 20), log); note = "立场漂移 " + Num(a1, 20); break;
                case "秘闻":
                    if (snap.ShareableSecrets != null && snap.ShareableSecrets.Count > 0)
                    { EffectHandler.ApplyShareSecret(taiwu, snap.ShareableSecrets[0].Id); Emit("一条秘闻", "查看秘闻", CharacterMenuLink.OpenSecretPage, true); note = "吐露秘闻给太吾"; }
                    else note = "该 NPC 无可吐露秘闻";
                    break;
                case "赠银钱": case "借钱":
                    { int amt = Num(a1, 5000); TalkEntryHost.Instance?.StartCoroutine(GiveSilverAndNotice(npc, taiwu, amt)); note = "赠/借银钱 " + amt; }
                    break;
                case "赠物":
                    // 全实时:不再靠快照判有无,直接交后端按名实时解析(没有则自带提示)
                    { TalkEntryHost.Instance?.StartCoroutine(GiveItemAndNotice(snap, a1, Num(a2, 1))); note = "赠物 " + (a1 ?? "(需物名)") + " ×" + Num(a2, 1); }
                    break;
                case "恋人": EffectHandler.ApplyRelation(npc, taiwu, "lover", log); note = "与太吾结恋人"; break;
                case "夫妻": EffectHandler.ApplyRelation(npc, taiwu, "spouse", log); note = "与太吾结夫妻"; break;
                case "离间": ResolveAndApplyEnmity(npc, taiwu, a1 ?? "太吾", true); note = "离间结仇 → " + (a1 ?? "太吾"); break;
                case "说和": ResolveAndApplyEnmity(npc, taiwu, a1 ?? "太吾", false); note = "说和化解 → " + (a1 ?? "太吾"); break;
                case "追随": case "入队": EffectHandler.ApplyFollow(npc, true); note = "入队"; break;
                case "离去": EffectHandler.ApplyFollow(npc, false); note = "解除追随"; break;
                case "戒备": EffectHandler.ApplyChangeAlertness(npc, Num(a1, -5000), log); note = "对太吾戒备 " + Num(a1, -5000); break;
                case "传功":
                    if (snap.LearnableSkills != null && snap.LearnableSkills.Count > 0)
                    { string nm = a1 ?? snap.LearnableSkills[0].Name; TalkEntryHost.Instance?.StartCoroutine(WriteBookAndNotice(snap, "combat", nm, (m, ok) => Debug.Log("[江湖有灵][暗号] 传功回忆:" + m))); note = "回忆武学给太吾 → " + nm; }
                    else note = "该 NPC 无可传武学";
                    break;
                case "放人": if (a1 != null) ResolveAndRelease(npc, a1); note = "放走 " + (a1 ?? "(需写名)"); break;
                case "做媒": case "保媒": if (a1 != null) ResolveAndMatchmake(npc, taiwu, a1); note = "做媒 → " + (a1 ?? "(需写名)"); break;
                case "第三方好感": if (a1 != null) ResolveAndThirdPartyFavor(npc, a1, Num(a2, 2000)); note = "对第三方 " + (a1 ?? "?") + " 好感 " + Num(a2, 2000); break;
                case "传秘闻":
                    if (a1 == null) note = "需写对象名(/传秘闻 太吾 或 名)";
                    else if (snap.ShareableSecrets != null && snap.ShareableSecrets.Count > 0)
                    { ResolveAndDiscloseSecret(npc, taiwu, a1, snap.ShareableSecrets[0]); note = "传秘闻 → " + a1; }
                    else note = "该 NPC 无可分享秘闻";
                    break;
                case "讲秘闻": case "告知秘闻":
                    TalkEntryHost.Instance?.StartCoroutine(DebugTaiwuTellSecret(npc, taiwu, snap.CurrentDate));
                    note = "太吾把一桩秘闻讲给 NPC"; break;
                case "换上": if (a1 != null) { EquipNamedItem(snap, a1); note = "换上 " + a1; } else note = "需写物品名:/换上 <物品名>"; break;
                case "卸下": EffectHandler.ApplyEquipTakeOff(npc, NormPart(a1)); note = "卸下 " + NormPart(a1); break;
                case "保护":
                    if (a1 != null && a1 != "太吾") EffectHandler.ResolveChar(npc, a1, cid => { if (cid > 0 && cid != npc) EffectHandler.ApplyAddGoal(npc, 262, "char", cid, (ok, m) => Emit(ok ? ("已起意护卫" + a1) : ("未能:" + (m ?? "")), null, null, ok)); else Emit("未找到「" + a1 + "」", null, null, false); });
                    else InjectGoal(npc, 262);
                    note = "护卫 " + (a1 ?? "太吾"); break;
                case "营救":
                    if (a1 != null && a1 != "太吾") EffectHandler.ResolveChar(npc, a1, cid => { if (cid > 0 && cid != npc) EffectHandler.ApplyAddGoal(npc, 263, "char", cid, (ok, m) => Emit(ok ? ("已起意营救" + a1) : ("未能:" + (m ?? "")), null, null, ok)); else Emit("未找到「" + a1 + "」", null, null, false); });
                    else InjectGoal(npc, 263);
                    note = "营救 " + (a1 ?? "太吾"); break;
                case "追杀":   // 无名=追太吾(271);带名=追杀第三方(274 复仇,须实机有仇怨方生效)
                    if (a1 != null && a1 != "太吾") EffectHandler.ResolveChar(npc, a1, cid => { if (cid > 0 && cid != npc) EffectHandler.ApplyAddGoal(npc, 274, "char", cid, (ok, m) => Emit(ok ? ("已起意追杀" + a1) : ("未能:" + (m ?? "")), null, null, ok)); else Emit("未找到「" + a1 + "」", null, null, false); });
                    else InjectGoal(npc, 271);
                    note = "追杀 " + (a1 ?? "太吾"); break;
                case "解除关系": case "断绝":
                    {
                        string rel = MapDissolveRel(a1);
                        if (rel == null) { note = "用法:/解除关系 挚友|结义|师徒|恋人|夫妻 [对象名,默认太吾]"; break; }
                        string who = string.IsNullOrEmpty(a2) ? "太吾" : a2;
                        Action<bool, string> cb = (ok, m) => Debug.Log("[江湖有灵][暗号] dissolve " + ok + " " + m);
                        if (who == "太吾") EffectHandler.ApplyDissolveRelation(npc, taiwu, rel, taiwu, cb);
                        else EffectHandler.ResolveChar(npc, who, cid => { if (cid > 0 && cid != npc) EffectHandler.ApplyDissolveRelation(npc, taiwu, rel, cid, cb); });
                        note = "解除关系 " + a1 + " → " + who;
                    }
                    break;
                case "教技艺": case "传技艺":
                    if (snap.LearnableLifeSkills != null && snap.LearnableLifeSkills.Count > 0)
                    { string nm = a1 ?? snap.LearnableLifeSkills[0].Name; TalkEntryHost.Instance?.StartCoroutine(WriteBookAndNotice(snap, "life", nm, (m, ok) => Debug.Log("[江湖有灵][暗号] 传技艺回忆:" + m))); note = "回忆技艺给太吾 → " + nm; }
                    else note = "该 NPC 无可传技艺";
                    break;
                // —— 今日新增功能的调试暗号(不便自然触发的,直接试)——
                case "买卖": case "交易":
                    if (a1 == null) { note = "用法:/买卖 <物品名> [价,默认0=白送]"; break; }
                    { TalkEntryHost.Instance?.StartCoroutine(TradeAndNotice(snap, a1, 1, Num(a2, 0), m => Debug.Log("[江湖有灵][暗号] 买卖:" + m))); note = "买卖 " + a1 + " 价" + Num(a2, 0); }
                    break;
                case "回忆": case "默写": case "写书":
                    if (a1 == null) { note = "用法:/回忆 <武学/技艺名> [life=技艺,默认武学]"; break; }
                    { string knd = (a2 == "life" || a2 == "技艺") ? "life" : "combat"; TalkEntryHost.Instance?.StartCoroutine(WriteBookAndNotice(snap, knd, a1, (m, ok) => Debug.Log("[江湖有灵][暗号] 回忆:" + m))); note = "回忆 " + a1; }
                    break;
                case "名望": case "名誉":
                    {
                        int dl = Num(a1, 5);
                        EffectHandler.ApplyTaiwuFame(taiwu, dl, ok => Debug.Log("[江湖有灵][暗号] 名望" + (dl > 0 ? "+" : "") + dl + " " + (ok ? "成" : "败")));
                        note = "太吾名望 " + (dl > 0 ? "+" : "") + dl + "(用法 /名望 [±值,默认+5];稍后看人物面板名望变化。过月连载里太吾卷入会自动增减,这里是手动测后端通道)";
                    }
                    break;
                case "牵线": case "撮合npc":
                    if (a1 == null) { note = "用法:/牵线 <人名> [挚友|结义|师徒|恋人|夫妻,默认结义]"; break; }
                    {
                        string ract = a2 == "挚友" ? "befriend" : a2 == "师徒" ? "mentor" : a2 == "恋人" ? "lover" : a2 == "夫妻" ? "spouse" : "sworn";
                        EffectHandler.ResolveChar(npc, a1, true, (cid, _) =>
                        {
                            if (cid > 0 && cid != npc) EffectHandler.ApplyRelateNpc(npc, cid, ract, (ok, m) => Emit(ok ? ("与" + a1 + "缔结关系") : ("未能与" + a1 + "缔结:" + (m ?? "")), null, null, ok));
                            else Emit("未找到「" + a1 + "」(须你/太吾认识或同队)", null, null, false);
                        });
                        note = "牵线 " + a1 + " " + (a2 ?? "结义");
                    }
                    break;
                case "下毒":
                    if (a1 == null) { note = "用法:/下毒 <人名>(需 NPC 身上有毒药)"; break; }
                    EffectHandler.ResolveChar(npc, a1, true, (cid, _) =>
                    {
                        if (cid > 0 && cid != npc) EffectHandler.ApplyPoison(npc, cid, (ok, m) => Emit(ok ? ("对" + a1 + "下了" + (m ?? "毒")) : ("未能下毒:" + (m ?? "")), null, null, ok));
                        else Emit("未找到「" + a1 + "」", null, null, false);
                    });
                    note = "下毒 → " + a1; break;
                case "疗伤":
                    if (string.IsNullOrEmpty(a1) || a1 == "太吾")
                        ApplyHealAfterAuthoritativePreflight(npc, taiwu, (ok, m) => Emit(ok ? "为太吾疗了伤" : (m ?? "未执行：未能确认可治疗状态"), null, null, ok));
                    else
                        EffectHandler.ResolveChar(npc, a1, true, (cid, _) => { if (cid > 0) ApplyHealAfterAuthoritativePreflight(npc, cid, (ok, m) => Emit(ok ? ("为" + a1 + "疗了伤") : (m ?? "未执行：未能确认可治疗状态"), null, null, ok)); else Emit("未找到「" + a1 + "」", null, null, false); });
                    note = "疗伤 → " + (string.IsNullOrEmpty(a1) ? "太吾" : a1); break;
                case "送给": case "转赠":
                    if (a1 == null || a2 == null) { note = "用法:/送给 <人名> <物名>(NPC 把随身物赠给第三方)"; break; }
                    EffectHandler.ResolveChar(npc, a1, true, (cid, _) =>
                    {
                        if (cid > 0 && cid != npc) TalkEntryHost.Instance?.StartCoroutine(GiveItemAndNotice(snap, a2, 1, null, cid, a1));
                        else Emit("未找到「" + a1 + "」", null, null, false);
                    });
                    note = "送给 " + a1 + " " + a2; break;
                case "传给":
                    if (a1 == null || a2 == null) { note = "用法:/传给 <人名> <武学或技艺名>(NPC 传给第三方)"; break; }
                    EffectHandler.ResolveChar(npc, a1, true, (cid, _) =>
                    {
                        if (cid > 0 && cid != npc)
                        {
                            bool isLife = false;
                            if (snap.LearnableLifeSkills != null) foreach (var sk in snap.LearnableLifeSkills) if (sk != null && (sk.Name == a2 || NameHit(sk.Name, a2))) { isLife = true; break; }
                            TalkEntryHost.Instance?.StartCoroutine(isLife ? TeachLifeAndNotice(snap, a2, null, cid, a1) : TeachAndNotice(snap, a2, null, cid, a1));
                        }
                        else Emit("未找到「" + a1 + "」", null, null, false);
                    });
                    note = "传给 " + a1 + " " + a2; break;
                case "杀": case "杀人":
                    if (a1 == null) { note = "用法:/杀 <人名>(需精纯不低于对方，相等可成)"; break; }
                    EffectHandler.ResolveChar(npc, a1, true, (cid, _) =>
                    {
                        if (cid > 0 && cid != npc) EffectHandler.ApplyKillDetailed(npc, cid, (ok, m, lootName) =>
                        {
                            Emit(ok ? ("取了" + a1 + "性命"
                                    + (string.IsNullOrWhiteSpace(lootName) ? "" : ("，并夺得「" + lootName + "」")))
                                : ("未能下手:" + (m ?? "")), null, null, ok);
                        });
                        else Emit("未找到「" + a1 + "」", null, null, false);
                    });
                    note = "杀 → " + a1; break;
                case "绑": case "擒拿": case "绑架":
                    if (a1 == null) { note = "用法:/绑 <人名>(需精纯不低于对方，相等可成，并带绳子)"; break; }
                    EffectHandler.ResolveChar(npc, a1, true, (cid, _) => { if (cid > 0 && cid != npc) EffectHandler.ApplyCapture(npc, cid, (ok, m) => Emit(ok ? ("将" + a1 + "绑下") : ("未能擒拿:" + (m ?? "")), null, null, ok)); else Emit("未找到「" + a1 + "」", null, null, false); });
                    note = "绑 → " + a1; break;
                case "货架": case "查货":
                    EffectHandler.QueryMerchantGoods(npc, snap.MerchantTemplateId, goods =>
                        Emit(goods != null && goods.Count > 0 ? ("货架现有 " + goods.Count + " 种货") : "货架空(或此人非商人)", null, null, false));
                    note = "查货架"; break;
                case "收物":
                    if (a1 == null) { note = "用法:/收物 <太吾身上的物品名>"; break; }
                    TalkEntryHost.Instance?.StartCoroutine(DebugTaiwuGive(npc, taiwu, a1)); note = "太吾赠物:" + a1; break;
                case "受教": case "学艺":
                    if (a1 == null) { note = "用法:/受教 <太吾会的武学/技艺名> [life=技艺]"; break; }
                    { string knd = (a2 == "life" || a2 == "技艺") ? "life" : "combat"; TalkEntryHost.Instance?.StartCoroutine(DebugTaiwuTeach(npc, taiwu, knd, a1)); note = "向太吾学:" + a1; }
                    break;
                case "过月消息": case "月度消息": case "过月行为": case "同道行事":
                    {
                        int tw = taiwu;
                        TalkEntryHost.Instance?.StartCoroutine(CompanionMonthlyActions.RunMonthly(tw, snap.CurrentDate, actions =>
                        {
                            if (actions != null && actions.Count > 0) MonthlyDigestPopup.ShowAll(null, null, 0, actions, tw, null);
                            else Debug.Log("[江湖有灵][暗号] 同道主动行事:本轮无人行动");
                        }));
                    }
                    note = "（触发同道主动行事:从全部当前同道中稳定抽选,按记忆、关系和处境真正做事,稍候弹本月动向详情）"; break;
                case "商队好感":
                    EffectHandler.ApplyMerchantFavor(npc, Num(a1, 20), (ok, m) => Emit(ok ? ("商队好感(现 " + m + "/100)") : ("非商人或失败:" + (m ?? "")), null, null, ok));
                    note = "商队好感 " + Num(a1, 20); break;
                case "前往": case "去":
                    if (a1 == null) { note = "用法:/前往 <地名>"; break; }
                    {
                        short aId = -1, bId = 0; string rn = a1; bool home = IsHomeVillage(a1);
                        if (home) rn = "太吾村";
                        if (home || EffectHandler.ResolveAreaId(a1, out aId, out bId, out rn))
                        {
                            Debug.Log("[江湖有灵] /前往 解析「" + a1 + "」→ home=" + home + " area=" + aId + " block=" + bId + " name=" + rn);
                            EffectHandler.ApplyGotoPlace(npc, aId, bId, (ok, m) => { Debug.Log("[江湖有灵] /前往 后端回执 ok=" + ok + " msg=" + (m ?? "")); Emit(ok ? ("已安排前往" + rn + "的逐月行程") : ("未能前往:" + (m ?? "")), null, null, ok); }, home);
                            note = "过月前往 " + rn;
                        }
                        else { Debug.Log("[江湖有灵] /前往 认不出地名「" + a1 + "」(不在 Config.MapArea,且非太吾村家园)"); note = "认不出地名「" + a1 + "」"; }
                    }
                    break;
                case "加特性": case "特性":
                    if (a1 == null) { note = "用法:/加特性 <良性特性名,如 勇敢>"; break; }
                    EffectHandler.ApplyAddFeature(npc, a1, (ok, m) => Emit(ok ? ("性情新生:" + m) : ("未能添加特性:" + (m ?? "")), null, null, ok));
                    note = "加特性 " + a1; break;
                // —— 2026-06-26 新增功能调试暗号 ——
                case "收徒": EffectHandler.ApplyRelation(npc, taiwu, "apprentice", log); note = "太吾收NPC为徒(太吾=师父)"; break;
                case "拜师": EffectHandler.ApplyRelation(npc, taiwu, "take_disciple", log); note = "太吾拜NPC为师(NPC=师父)"; break;
                case "结仇": ResolveAndApplyEnmity(npc, taiwu, a1 ?? "太吾", true); note = "NPC与太吾结仇" + (a1 != null ? " → " + a1 : ""); break;
                case "春宵":
                    EffectHandler.ApplySpendNight(taiwu, npc, (ok, m) => Emit(ok ? "与太吾共度春宵" : ("未能春宵:" + (m ?? "")), null, null, ok));
                    note = "春宵一刻(需恋人/夫妻或情意深厚+成年)"; break;
                case "太吾送资源":
                    {
                        if (a1 == null) { note = "用法:/太吾送资源 <食材|木料|金石|玉石|布料|药材> [数,默认10]"; break; }
                        int rt = a1.Contains("食") ? 0 : (a1.Contains("木") ? 1 : (a1.Contains("金") || a1.Contains("矿") ? 2 : (a1.Contains("玉") ? 3 : (a1.Contains("布") ? 4 : (a1.Contains("药") ? 5 : -1)))));
                        if (rt < 0) { note = "认不出资源「" + a1 + "」(食材/木料/金石/玉石/布料/药材)"; break; }
                        EffectHandler.ApplyTaiwuGiveItem(taiwu, npc, new GameData.Domains.Item.ItemKey((sbyte)12, (byte)0, (short)rt, 0), Num(a2, 10),
                            actual => Emit(actual > 0 ? ("收下太吾的" + a1 + " ×" + actual) : "太吾此资源不足", null, null, actual > 0));
                        note = "太吾送资源 " + a1 + " ×" + Num(a2, 10);
                    }
                    break;
                case "太吾送银钱": case "送银钱":
                    EffectHandler.ApplyTaiwuGiveItem(taiwu, npc, new GameData.Domains.Item.ItemKey((sbyte)12, (byte)0, (short)6, 0), Num(a1, 1000),
                        actual => Emit(actual > 0 ? ("收下太吾银钱 " + actual + " 文") : "太吾银钱不足", null, null, actual > 0));
                    note = "太吾送银钱 " + Num(a1, 1000); break;
                case "刷新关系": case "刷新":
                    NativeUiRefresh.AfterRelationChange(npc); note = "促原生互动菜单按当前关系重评(见日志命中数)"; break;
                case "剧情": case "主线":
                    EffectHandler.QueryStoryStatus(snap.OrgTemplateId, (m, sct) =>
                    { string t = StoryStatusText(m, sct); onReply?.Invoke("【主线/门派剧情】\n" + (string.IsNullOrEmpty(t) ? "(暂无可知剧情进度)" : t)); });
                    return true;
                case "查特性": case "秉性":
                    onReply?.Invoke("【该 NPC 的秉性特性】\n" + ((snap.Features != null && snap.Features.Count > 0) ? string.Join("、", snap.Features.ToArray()) : "(空)")); return true;
                case "近况": case "近来":
                    { string dg = RecentDialogueDigest(taiwu); onReply?.Invoke("【太吾近来与各 NPC 的对话(供灵儿感知)】\n" + (string.IsNullOrEmpty(dg) ? "(暂无)" : dg)); }
                    return true;
                // —— 2026-06-27 新增功能调试暗号 ——
                case "事件": case "过月事件": case "江湖事件":
                    {
                        int tw = taiwu;
                        TalkEntryHost.Instance?.StartCoroutine(MonthlyEventGenerator.RunMonthly(tw, snap.CurrentDate, (text, area, heard) =>
                        {
                            if (!string.IsNullOrWhiteSpace(text)) MonthlyDigestPopup.ShowAll(text, area, heard, null, tw, null);
                            else Debug.Log("[江湖有灵][暗号] 过月事件:本轮未生成(在场名册不足或模型未产出)");
                        }));
                    }
                    note = "（触发过月 AI 江湖事件:世界演算就近找在场之人落地一桩事,稍候弹「真实发生」提示,并可在挂件「纪事」钮的江湖见闻录回看)"; break;
                case "连载": case "大事": case "连续剧":
                    {
                        int tw = taiwu;
                        // #18 强制开一桩跨月连载大事(或推进已在演的);多触发几次可看它逐月推进直至终回
                        TalkEntryHost.Instance?.StartCoroutine(MonthlyEventGenerator.RunMonthly(tw, snap.CurrentDate, (text, area, heard) =>
                        {
                            if (!string.IsNullOrWhiteSpace(text)) MonthlyDigestPopup.ShowAll(text, area, heard, null, tw, null);
                            else Debug.Log("[江湖有灵][暗号] 连载事件:本轮未生成(在场名册不足或模型未产出)");
                        }, true));
                    }
                    note = "（强制开/推进一桩『连载』跨月大事:本回弹出后,再多触发几次『/连载』或正常过月,可看同一桩大事逐月推进到终回。班底与地点全程固定)"; break;
                case "奇书":
                    EffectHandler.QueryLegendaryBooks(npc, bn =>
                        EffectHandler.QueryLegendaryBooks(taiwu, tn =>
                            onReply?.Invoke("【奇书查验(物品查询是否认得奇书)】\n本 NPC 持有:" + ((bn != null && bn.Count > 0) ? string.Join("、", bn) : "(无)") +
                                            "\n太吾持有:" + ((tn != null && tn.Count > 0) ? string.Join("、", tn) : "(无)") +
                                            "\n(若此处列出而平常 query_npc_items / query_taiwu_items 也列出,即奇书已被认得)")));
                    return true;
                default: onReply?.Invoke("未知暗号「/" + act + "」\n" + TestHelp()); return true;
            }
            onReply?.Invoke("【暗号已触发】" + note + "\n(绕过 LLM 直接落地;以游戏内人物面板为准)");
            return true;
        }

        private bool QueueDebugTool(NpcSnapshot snap, string toolName, JObject args, Action<string> onReply)
        {
            var host = TalkEntryHost.Instance;
            if (host == null)
            {
                onReply?.Invoke("调试工具宿主不可用");
                return true;
            }
            host.StartCoroutine(RunDebugTool(snap, toolName, args, onReply));
            return true;
        }

        // JHYL_DEBUG_TOOL_REGISTRY_PARITY: 调试总入口直接从“商人+门派+本地单聊”的
        // ToolRegistry 超集派生，不维护第二份易漏的手写工具名单。新增正式工具后会自动出现在
        // /工具列表，并由 /工具 走相同 ExecuteTool；场景/参数/后端仍会按真实状态如实成败。
        private static HashSet<string> DebuggableToolNames()
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            var tools = ToolRegistry.BuildConversationTools(new ToolContext
            {
                IsMerchant = true,
                InSect = true,
                Remote = false,
                CanStartCombat = true,
                CanOpenGrooming = true,
            });
            foreach (ToolDef tool in tools)
                if (tool != null && !string.IsNullOrWhiteSpace(tool.Name)) result.Add(tool.Name);
            return result;
        }

        private IEnumerator RunDebugTool(NpcSnapshot snap, string toolName, JObject args, Action<string> onReply)
        {
            if (snap == null || !WorldLifecycle.IsSameWorld(_turnWorldGeneration))
            {
                onReply?.Invoke("【调试未执行】存档世界已经变化");
                yield break;
            }
            var conv = GetConv(snap.TaiwuId, snap.NpcId);
            if (conv == null || !conv.LoadReliable)
            {
                onReply?.Invoke("【调试未执行】对话事务记录不可可靠读取");
                yield break;
            }
            _mem = NpcMemoryStore.Load(JianghuYoulingPaths.Memories, snap.TaiwuId.ToString(), snap.NpcId.ToString());
            _landedThisTurn = new List<string>();
            _toolCallsSeen.Clear();
            _reactionState = ReactionCommitState.NotAttempted;
            var profile = PortraitStore.BuildProfile(snap);
            string result = null;
            yield return ExecuteTool(toolName, args?.ToString(Formatting.None) ?? "{}", snap, conv, profile,
                _cts != null ? _cts.Token : System.Threading.CancellationToken.None, r => result = r,
                null);
            onReply?.Invoke("【调试结果】\n" + (string.IsNullOrWhiteSpace(result) ? "(无返回)" : result));
        }

        private static void InjectGoal(int npc, int tpl)
            => EffectHandler.ApplyAddGoal(npc, tpl, "taiwu", 0, (ok, m) => Debug.Log("[江湖有灵][暗号] goal " + tpl + " " + ok + " " + m));

        // 调试:太吾赠物给 NPC(取太吾随身物按名,NPC 收下)——测 taiwu_give_item
        private IEnumerator DebugTaiwuGive(int npc, int taiwu, string name)
        {
            TaiwuHoldings h = null;
            yield return NpcSnapshotReader.FetchTaiwuHoldings(taiwu, x => h = x);
            var list = h?.Items;
            if (list == null || list.Count == 0) { Emit("太吾身上无物可赠", null, null, false); yield break; }
            GiftableItem pick = null;
            foreach (var g in list) if (g != null && (g.Name == name || NameHit(g.Name, name))) { pick = g; break; }
            if (pick == null) { Emit("太吾没有「" + name + "」", null, null, false); yield break; }
            EffectHandler.ApplyTaiwuGiveItem(taiwu, npc, pick.Key, 1, actual => Emit(actual > 0 ? ("NPC 收下太吾的" + pick.Name) : "未能收下", null, null, actual > 0));
        }

        // 调试:太吾把会的武学/技艺传给 NPC(NPC 拜领)——测 taiwu_teach
        private IEnumerator DebugTaiwuTeach(int npc, int taiwu, string kind, string name)
        {
            TaiwuHoldings h = null;
            yield return NpcSnapshotReader.FetchTaiwuHoldings(taiwu, x => h = x);
            var pool = kind == "life" ? h?.LifeSkills : h?.CombatSkills;
            if (pool == null || pool.Count == 0) { Emit("太吾无可传的" + (kind == "life" ? "技艺" : "武学"), null, null, false); yield break; }
            LearnableSkill pk = null;
            foreach (var s2 in pool) if (s2 != null && (s2.Name == name || NameHit(s2.Name, name))) { pk = s2; break; }
            if (pk == null) { Emit("太吾不会「" + name + "」", null, null, false); yield break; }
            EffectHandler.ApplyTaiwuTeach(taiwu, npc, kind, pk.TemplateId, (ok, msg) => Emit(ok ? ("NPC 学得太吾的" + pk.Name) : ("未学成:" + (msg ?? "原因不明")), null, null, ok));
        }

        // 调试:太吾把自己知道的一桩秘闻讲给 NPC(NPC 收到)——测 taiwu_tell_secret
        private IEnumerator DebugTaiwuTellSecret(int npc, int taiwu, int curDate)
        {
            List<SecretRef> tss = null;
            yield return NpcSnapshotReader.FetchShareableSecrets(taiwu, curDate, x => tss = x);
            if (tss == null || tss.Count == 0) { Emit("太吾无可告知的秘闻", null, null, false); yield break; }
            EffectHandler.ApplyReceiveSecret(npc, tss[0].Id, (ok, m) => Emit(ok ? "NPC 听太吾说了一桩秘闻" : ("未能告知:" + (m ?? "")), null, null, ok));
        }

        private static int Num(string s, int def) => int.TryParse(s, out int v) ? v : def;

        // 调试暗号:中文/英文关系词 → dissolve rel
        private static string MapDissolveRel(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Trim();
            if (s.Contains("挚友") || s == "friend") return "friend";
            if (s.Contains("结义") || s.Contains("义兄") || s.Contains("义姐") || s == "sworn") return "sworn";
            if (s.Contains("师") || s == "mentor") return "mentor";
            if (s.Contains("恋") || s == "lover") return "lover";
            if (s.Contains("夫") || s.Contains("妻") || s == "spouse") return "spouse";
            return null;
        }

        private static string NormPart(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return "weapon";
            p = p.Trim().ToLowerInvariant();
            if (p.Contains("cloth") || p.Contains("衣") || p.Contains("袍")) return "clothing";
            if (p.Contains("armor") || p.Contains("甲") || p.Contains("护")) return "armor";
            if (p.Contains("access") || p.Contains("饰") || p.Contains("佩")) return "accessory";
            if (p.Contains("carrier") || p.Contains("坐骑") || p.Contains("代步") || p.Contains("牲畜")) return "carrier";
            return "weapon";
        }

        // 正式工具调用必须严格接受 schema 中的四种部位。NormPart 仅保留给人工调试暗号的中文别名，
        // 避免模型的缺参/拼写错误被静默降级为“卸下兵器”。
        private static bool TryStrictEquipmentPart(string value, out string part)
        {
            part = (value ?? "").Trim().ToLowerInvariant();
            return part == "weapon" || part == "armor" || part == "accessory" || part == "carrier";
        }

        // JHYL_TEST_HELP_SHARED:调试暗号总表。/列表 与 /帮助 在所有构建(含部署进游戏的 Release)
        // 都会显示本表；执行仅限存活人物的当面普通单聊，玩家键入触发且 LLM 不可达。
        internal static string TestHelp()
        {
            return "【调试暗号】 /<类型> [参数] —— 绕过主对话模型直接查询或落地,以人物面板为准\n" +
                "仅存活人物的当面普通单聊可执行；助手、群聊、灵魂与千里传音只显示帮助而不执行\n" +
                "输入 /列表 或 /帮助 随时重看本表（全角斜杠也可）\n" +
                "  /工具列表（自动列出全部正式工具）   /工具 正式工具名 {JSON参数}（与 AI 对话复用同一执行器）\n" +
                "\n" +
                "── 真实查询（复用正式对话工具）──\n" +
                "  /查记忆 [关键词]   /查状态   /查身体   /查关系 [人名]   /查生平(旧经历摘要使用后台模型)\n" +
                "  /查物品 [关键词]   /查人物物品 人名 [关键词]   /查人物 人名\n" +
                "  /查同地   /查地区人物 地名   /查门派成员 [门派名]\n" +
                "  /查功法 [关键词]   /查配招   /查秘闻\n" +
                "  /查太吾物品 [关键词]   /查太吾功法 [关键词]   /查太吾配招   /查太吾秘闻\n" +
                "  /查百科 [章节路径/一级类目]   /查地点 [关键词]   /查门派设定   /货架\n" +
                "\n" +
                "── 数值 / 好感 ──\n" +
                "  /好感 [数]   /降好感 [数]   /心情 [±数]   /名望 [±值,默认+5]\n" +
                "  /戒备 [±数]   /立场 [数]   /第三方好感 名 [数]   /商队好感 [数]\n" +
                "  /反应 [满意度] [心情] [戒备变化]   /记住 内容 [类型]\n" +
                "\n" +
                "── 与太吾结关系 ──\n" +
                "  /挚友   /结义   /恋人   /夫妻\n" +
                "  /收徒 (太吾收NPC为徒)   /拜师 (太吾拜NPC为师)\n" +
                "  /认可   /投奔   /门派支持   /入队   /离去\n" +
                "  /结仇 [名,默认太吾]   /离间 [名]   /说和 [名]\n" +
                "  /春宵 (需恋人/夫妻或情意深厚,且双方成年)\n" +
                "  /解除关系 挚友|结义|师徒|恋人|夫妻 [名,默认太吾]\n" +
                "\n" +
                "── NPC 互相牵线 ──\n" +
                "  /牵线 人名 [挚友|结义|师徒|恋人|夫妻,默认结义]   /做媒 名\n" +
                "\n" +
                "── 馈赠 / 传授(NPC→太吾)──\n" +
                "  /赠银钱 [数]   /赠物 名 [数]   /传功 [名]   /教技艺 [名]\n" +
                "  /秘闻   /传秘闻 名   /换物 NPC物品 太吾物品 [数量]\n" +
                "\n" +
                "── 受惠 / 受教(太吾→NPC)──\n" +
                "  /收物 太吾物名   /受教 太吾武学名 [life]\n" +
                "  /太吾写书 太吾武学名 [life]   /太吾送资源 名 [数]   /太吾送银钱 [数]   /讲秘闻\n" +
                "\n" +
                "── NPC 对第三方动作 ──\n" +
                "  /送给 人名 物名(转赠)   /传给 人名 武学或技艺名\n" +
                "  /偷窃 人名 物品 [数量]   /下毒 人名 (需带毒药)   /疗伤 [人名,默认太吾]\n" +
                "  /驱毒 [人名,默认太吾]   /调息 [人名,默认太吾]   /杀 人名   /绑 人名\n" +
                "\n" +
                "── 容貌 / 装备 ──\n" +
                "  梳头修面请在当面单聊中提出，角色同意后进入本体互动   /换上 名   /卸下 [部位]   /正逆练 功法 [人物]\n" +
                "  /练功 武学名（当前NPC本人）   /读书 书名（当前NPC背包）\n" +
                "\n" +
                "── 行动 / 过月目标(普通前往/寻人是有期限行程；固定地点赴约/营救/保护/投奔/追杀由 goto_place 分别安排)──\n" +
                "  /约战 [切磋|相搏|死斗] [NPC|太吾]   /前往 地名   /放人 名\n" +
                "  /保护 [人名]   /营救 [人名]   /追杀 [人名]   /投奔 [门派名]  (无名=对太吾;带名=对第三方/别门派,须实机满足条件方生效)\n" +
                "\n" +
                "── 特性 / 商队 / 买卖 ──\n" +
                "  /加特性 名   /查特性   /买卖 物名 [价]   /货架   /回忆 武学名 [life]\n" +
                "\n" +
                "── 消息 / 过月 / 查询(直接显示)──\n" +
                "  /过月行为(从全部当前同道抽选主动行事)   /近况(灵儿感知)\n" +
                "  /行动指南 技能id（调试技能指南加载）\n" +
                "  /事件(强触发过月 AI 江湖事件)   /连载(强开/推进跨月大事)   /奇书\n" +
                "  /世界进度   /剧情(主线门派)   /刷新关系(原生菜单重评)\n" +
                "  /性能 [清零]（大模型调用统计）";
        }

        private static MemoryType MapType(string t)
        {
            if (string.IsNullOrEmpty(t)) return MemoryType.Impression;
            switch (t)
            {
                case "恩情": return MemoryType.Favor;
                case "仇怨": return MemoryType.Grudge;
                case "承诺": return MemoryType.Promise;
                case "秘密": return MemoryType.Secret;
                case "事件": return MemoryType.Event;
                default: return MemoryType.Impression;
            }
        }
    }
}
