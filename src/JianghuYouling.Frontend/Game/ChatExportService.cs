using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Persistence;
using JianghuYouling.Core.Prompt;

namespace JianghuYouling
{
    /// <summary>把玩家可见聊天导出为 UTF-8 BOM Markdown；不包含 API key、system prompt、思考或工具参数。</summary>
    public static class ChatExportService
    {
        public sealed class Result
        {
            public bool Ok;
            public string Path;
            public int ConversationCount;
            public int SkippedCount;
            public string Error;
        }

        public sealed class ExportScope
        {
            internal uint WorldId;
            internal int Generation;
            internal string ChatLogs;
            internal string Exports;
            internal LlmMessage[] AssistantHistory;

            internal bool IsCurrent
                => WorldId > 0
                    && WorldLifecycle.WorldId == WorldId
                    && WorldLifecycle.IsSameWorld(Generation);
        }

        private static readonly Regex TmpTag = new Regex("<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex NumericNamePlaceholder = new Regex(
            @"(?:江湖人|NPC|角色)?#\d+", RegexOptions.Compiled);
        private static readonly object ExportGate = new object();
        private static readonly Encoding Utf8Bom = new UTF8Encoding(true);
        private const int MaxExportChars = 8 * 1024 * 1024;
        [ThreadStatic] private static ExportScope ActiveScope;

        private sealed class ExportLimitExceededException : Exception { }

        public static ExportScope CaptureScope(int taiwuId)
        {
            int generation = WorldLifecycle.Generation;
            uint worldId = WorldLifecycle.WorldId;
            if (worldId == 0 || taiwuId <= 0) return null;
            string chatLogs = JianghuYoulingPaths.ChatLogs;
            string exports = JianghuYoulingPaths.Exports;
            // This method is called on Unity's main thread before Task.Run. Capture only
            // the player-visible assistant fields so the worker never touches mutable
            // assistant state, current-world paths, or BasicGameData.
            LlmMessage[] assistantHistory = CaptureAssistantHistory(taiwuId);
            if (WorldLifecycle.WorldId != worldId
                || !WorldLifecycle.IsSameWorld(generation)) return null;
            return new ExportScope
            {
                WorldId = worldId,
                Generation = generation,
                ChatLogs = Path.GetFullPath(chatLogs),
                Exports = Path.GetFullPath(exports),
                AssistantHistory = assistantHistory,
            };
        }

        private static LlmMessage[] CaptureAssistantHistory(int taiwuId)
        {
            List<LlmMessage> source = AssistantOrchestrator.HistorySnapshot(taiwuId);
            if (source == null || source.Count == 0) return Array.Empty<LlmMessage>();
            var snapshot = new LlmMessage[source.Count];
            for (int i = 0; i < source.Count; i++)
            {
                LlmMessage message = source[i];
                snapshot[i] = message == null
                    ? null
                    : new LlmMessage(message.Role, message.Content);
            }
            return snapshot;
        }

        private static Result RunScoped(ExportScope scope, Func<Result> action)
        {
            try
            {
                EnsureScopeCurrent(scope);
                ExportScope previous = ActiveScope;
                ActiveScope = scope;
                try { return action(); }
                finally { ActiveScope = previous; }
            }
            catch (Exception ex) { return Fail(ex); }
        }

        private static ExportScope RequireScope()
        {
            ExportScope scope = ActiveScope;
            if (scope == null || scope.WorldId == 0
                || string.IsNullOrWhiteSpace(scope.ChatLogs)
                || string.IsNullOrWhiteSpace(scope.Exports))
                throw new InvalidOperationException("聊天导出缺少固定世界范围");
            return scope;
        }

        private static void EnsureScopeCurrent(ExportScope scope)
        {
            if (scope == null || !scope.IsCurrent)
                throw new OperationCanceledException("存档已切换，已取消本次聊天导出");
        }

        public static Result ExportSingle(ExportScope scope, int taiwuId,
            int npcId, string npcName)
            => RunScoped(scope, () => ExportSingleCore(taiwuId, npcId, npcName));

        private static Result ExportSingleCore(int taiwuId, int npcId, string npcName)
        {
            string[] exactSecrets = null;
            try
            {
                exactSecrets = ConfiguredSecretSnapshot.LoadOrThrow();
                string name = ReadableNpcName(npcName);
                var sb = Header("单聊", taiwuId, exactSecrets);
                AppendChecked(sb, "- 对话对象：", Clean(name, exactSecrets), "（NPC ", npcId.ToString(), "）\n\n");
                AppendSingle(sb, taiwuId, npcId, name, exactSecrets);
                return Write("单聊_" + SafeName(name, exactSecrets) + "_" + npcId, sb, 1, exactSecrets);
            }
            catch (Exception ex) { return Fail(ex); }
        }

        /// <summary>导出人物详情页当前语义：该 NPC 的完整单聊，以及实际参与过的全部群聊。</summary>
        public static Result ExportNpcHistory(ExportScope scope, int taiwuId,
            int npcId, string npcName)
            => RunScoped(scope, () => ExportNpcHistoryCore(taiwuId, npcId, npcName));

        private static Result ExportNpcHistoryCore(int taiwuId, int npcId, string npcName)
        {
            string[] exactSecrets = null;
            try
            {
                exactSecrets = ConfiguredSecretSnapshot.LoadOrThrow();
                string name = ReadableNpcName(npcName);
                var sb = Header("人物聊天记录", taiwuId, exactSecrets);
                AppendChecked(sb, "- 对话对象：", Clean(name, exactSecrets), "（NPC ", npcId.ToString(), "）\n\n");
                AppendChecked(sb, "# 单聊\n\n");
                AppendSingle(sb, taiwuId, npcId, name, exactSecrets);
                int count = 1;
                int groupBudget = Math.Max(0, MaxExportChars - sb.Length - 2048);
                List<GroupChatOrchestrator.GroupSession> groupSessions = LoadBoundedNpcGroupHistory(
                    taiwuId, npcId, name, groupBudget, out bool truncatedGroups);
                foreach (GroupChatOrchestrator.GroupSession session in groupSessions)
                {
                    int checkpoint = sb.Length;
                    try
                    {
                        AppendChecked(sb, "\n---\n\n# 群聊 · ",
                            Clean(session.Participants, exactSecrets), "\n\n");
                        AppendGroupLines(sb, session.Lines, exactSecrets);
                        count++;
                    }
                    catch (ExportLimitExceededException)
                    {
                        // 预算估算使用脱敏前字符数。若密钥替换或标题开销使真实输出越界，
                        // 回滚整场群聊并保留已经完成的较新记录，不能让整份人物导出失败。
                        sb.Length = checkpoint;
                        truncatedGroups = true;
                        break;
                    }
                }
                if (truncatedGroups)
                {
                    try
                    {
                        AppendChecked(sb, "\n（群聊记录超过单次导出上限；已保留预算内较新的记录，更早部分请在聊天记录窗口分页查看。）\n");
                    }
                    catch (ExportLimitExceededException) { /* 单聊已占满预算时仍导出完整单聊。 */ }
                }
                Result result = Write("人物聊天_" + SafeName(name, exactSecrets) + "_" + npcId,
                    sb, count, exactSecrets);
                result.SkippedCount = truncatedGroups ? 1 : 0;
                return result;
            }
            catch (Exception ex) { return Fail(ex); }
        }

        public static Result ExportGroup(ExportScope scope, int taiwuId,
            IReadOnlyList<GroupChatOrchestrator.Member> members,
            IReadOnlyList<GroupChatOrchestrator.Line> lines)
            => RunScoped(scope, () => ExportGroupCore(taiwuId, members, lines));

        private static Result ExportGroupCore(int taiwuId,
            IReadOnlyList<GroupChatOrchestrator.Member> members,
            IReadOnlyList<GroupChatOrchestrator.Line> lines)
        {
            string[] exactSecrets = null;
            try
            {
                exactSecrets = ConfiguredSecretSnapshot.LoadOrThrow();
                var names = new List<string>();
                if (members != null) foreach (var m in members)
                    if (m != null) names.Add(Clean(ReadableNpcName(m.Name), exactSecrets));
                var sb = Header("群聊", taiwuId, exactSecrets);
                AppendChecked(sb, "- 群聊成员：",
                    names.Count > 0 ? string.Join("、", names) : "（旧记录未保存完整成员表）", "\n\n");
                AppendGroupLines(sb, lines, exactSecrets);
                string stem = "群聊_" + SafeName(names.Count > 0 ? string.Join("_", names) : "小队", exactSecrets);
                return Write(stem, sb, 1, exactSecrets);
            }
            catch (Exception ex) { return Fail(ex); }
        }

        public static Result ExportAssistant(ExportScope scope, int taiwuId,
            IReadOnlyList<LlmMessage> history)
            => RunScoped(scope, () => ExportAssistantCore(taiwuId, history));

        private static Result ExportAssistantCore(int taiwuId,
            IReadOnlyList<LlmMessage> history)
        {
            string[] exactSecrets = null;
            try
            {
                exactSecrets = ConfiguredSecretSnapshot.LoadOrThrow();
                var sb = Header("灵儿助手", taiwuId, exactSecrets);
                if (history == null || history.Count == 0) AppendChecked(sb, "（暂无聊天记录）\n");
                else foreach (var m in history)
                    if (m != null && (m.Role == "user" || m.Role == "assistant") && !string.IsNullOrWhiteSpace(m.Content))
                        AppendSpeech(sb, m.Role == "user" ? "太吾" : "灵儿", m.Content, exactSecrets);
                return Write("灵儿助手", sb, 1, exactSecrets);
            }
            catch (Exception ex) { return Fail(ex); }
        }

        public static Result ExportAll(ExportScope scope, int taiwuId)
            => RunScoped(scope, () => ExportAllCore(taiwuId));

        private static Result ExportAllCore(int taiwuId)
        {
            string[] exactSecrets = null;
            try
            {
                ExportScope scope = RequireScope();
                exactSecrets = ConfiguredSecretSnapshot.LoadOrThrow();
                var sb = Header("全部聊天", taiwuId, exactSecrets);
                int count = 0, skipped = 0;
                var groupLoader = new GroupChatOrchestrator();
                if (!groupLoader.TryLoadAllSessionsForExport(
                    taiwuId, scope.WorldId, scope.ChatLogs,
                    out List<GroupChatOrchestrator.GroupSession> groupSessions))
                    throw new IOException("群聊记录扫描不完整，已取消导出");
                var groupNameHints = new Dictionary<int, string>();
                if (groupSessions != null)
                    foreach (GroupChatOrchestrator.GroupSession session in groupSessions)
                        if (session?.Members != null)
                            foreach (GroupChatOrchestrator.Member member in session.Members)
                                if (member != null && member.Id > 0
                                    && !TalkOrchestrator.IsUnresolvedNpcName(member.Name))
                                    groupNameHints[member.Id] = member.Name.Trim();
                if (!TalkOrchestrator.TryScanConversedPartnersForExport(
                    taiwuId, scope.WorldId, scope.ChatLogs, out List<TalkOrchestrator.ConversedPartner> partners))
                    throw new IOException("单聊记录扫描不完整，已取消导出");
                foreach (var p in partners)
                {
                    int checkpoint = sb.Length;
                    try
                    {
                        string name = p != null && groupNameHints.TryGetValue(p.NpcId, out string hint)
                            && TalkOrchestrator.IsUnresolvedNpcName(p.Name) ? hint : ReadableNpcName(p?.Name);
                        AppendChecked(sb, "\n---\n\n# 与 ", Clean(name, exactSecrets), " 的单聊\n\n");
                        AppendSingle(sb, taiwuId, p.NpcId, name, exactSecrets);
                        count++;
                    }
                    catch { sb.Length = checkpoint; skipped++; }
                }
                if (groupSessions != null) foreach (var session in groupSessions)
                {
                    int checkpoint = sb.Length;
                    try
                    {
                        AppendChecked(sb, "\n---\n\n# 群聊 · ",
                            Clean(ReadableGroupParticipants(session), exactSecrets), "\n\n");
                        AppendGroupLines(sb, session.Lines, exactSecrets);
                        count++;
                    }
                    catch { sb.Length = checkpoint; skipped++; }
                }
                IReadOnlyList<LlmMessage> ah = scope.AssistantHistory;
                if (ah != null && ah.Count > 0)
                {
                    int checkpoint = sb.Length;
                    try
                    {
                        AppendChecked(sb, "\n---\n\n# 灵儿助手\n\n");
                        foreach (var m in ah)
                            if (m != null && (m.Role == "user" || m.Role == "assistant") && !string.IsNullOrWhiteSpace(m.Content))
                                AppendSpeech(sb, m.Role == "user" ? "太吾" : "灵儿", m.Content, exactSecrets);
                        count++;
                    }
                    catch { sb.Length = checkpoint; skipped++; }
                }
                if (count == 0) AppendChecked(sb, "（暂无聊天记录）\n");
                var result = Write("全部聊天记录_太吾" + taiwuId, sb, count, exactSecrets);
                result.SkippedCount = skipped;
                return result;
            }
            catch (Exception ex) { return Fail(ex); }
        }

        private static string ReadableNpcName(string value)
            => TalkOrchestrator.IsUnresolvedNpcName(value) ? "某位江湖人" : value.Trim();

        private static string ReadableGroupParticipants(GroupChatOrchestrator.GroupSession session)
        {
            var names = new List<string>();
            if (session?.Members != null)
                foreach (GroupChatOrchestrator.Member member in session.Members)
                    if (member != null && member.Id > 0)
                        names.Add(ReadableNpcName(member.Name));
            if (names.Count > 0) return string.Join("、", names);
            string legacy = session?.Participants;
            return string.IsNullOrWhiteSpace(legacy) || NumericNamePlaceholder.IsMatch(legacy)
                ? "群聊成员" : legacy.Trim();
        }

        private static void AppendSingle(StringBuilder sb, int taiwuId, int npcId, string npcName,
            string[] exactSecrets)
        {
            ExportScope scope = RequireScope();
            if (!ConversationColdArchiveStore.TryCheckReadBudget(scope.ChatLogs,
                scope.WorldId, taiwuId, npcId, MaxExportChars,
                out bool archiveWithinBudget))
                throw new InvalidDataException("聊天冷归档无法安全检查");
            if (!archiveWithinBudget) throw new ExportLimitExceededException();
            string summary;
            List<JianghuYouling.Core.Prompt.TalkTurn> turns;
            bool includesArchive;
            if (!TalkOrchestrator.TryGetCompleteHistoryForExport(
                scope.WorldId, scope.ChatLogs, taiwuId, npcId,
                out summary, out turns, out includesArchive))
                throw new InvalidDataException("聊天记录损坏，已拒绝导出不完整记录");
            if (!string.IsNullOrWhiteSpace(summary))
                AppendChecked(sb, "## 此前交谈梗概（非逐字记录）\n\n",
                    Clean(summary, exactSecrets), "\n\n");
            if (includesArchive)
                AppendChecked(sb, "## 完整逐字记录（含压缩前冷归档恢复）\n\n");
            int lastDate = int.MinValue;
            if (turns != null) foreach (var turn in turns)
            {
                // 群聊原文在导出的群聊章节已有权威副本；个人上下文投影不重复导出。
                if (TalkTurnKinds.IsGroupChat(turn)) continue;
                bool hasText = turn != null && !string.IsNullOrWhiteSpace(turn.Text);
                bool hasExecution = turn != null && (HasToolResults(turn.ToolResults)
                    || (turn.Actions != null && turn.Actions.Any(x => !string.IsNullOrWhiteSpace(x))));
                bool hasImage = turn != null && ChatImageReference.IsValid(turn.ImageFileName);
                if (turn == null || (!hasText && !hasExecution && !hasImage)) continue;
                if (turn.Date != lastDate)
                {
                    lastDate = turn.Date;
                    AppendChecked(sb, "## ", FormatDate(turn.Date), "\n\n");
                }
                string speaker = TalkTurnKinds.ContextSpeaker(turn, npcName);
                if (hasText) AppendSpeech(sb, speaker, turn.Text, exactSecrets);
                else AppendChecked(sb, "**", Clean(speaker, exactSecrets), "的行动结果**\n\n");
                if (!turn.FromPlayer && !TalkTurnKinds.IsNative(turn))
                    AppendExecutionResults(sb, turn.Actions, turn.ToolResults, exactSecrets);
                if (hasImage) AppendChecked(sb, "![生成图片](Images/", turn.ImageFileName, ")\n\n");
            }
        }

        private static void AppendGroupLines(StringBuilder sb, IReadOnlyList<GroupChatOrchestrator.Line> lines,
            string[] exactSecrets)
        {
            if (lines == null || lines.Count == 0) { AppendChecked(sb, "（暂无聊天记录）\n"); return; }
            int lastDate = int.MinValue;
            foreach (var line in lines)
            {
                if (line == null || (string.IsNullOrWhiteSpace(line.Text)
                    && !HasToolResults(line.ToolResults)
                    && !ChatImageReference.IsValid(line.ImageFileName))) continue;
                if (line.Date != lastDate)
                {
                    lastDate = line.Date;
                    AppendChecked(sb, "## ", FormatDate(line.Date), "\n\n");
                }
                if (!string.IsNullOrWhiteSpace(line.Text))
                    AppendSpeech(sb, line.IsTaiwu ? "太吾" : (string.IsNullOrWhiteSpace(line.Speaker) ? "某人" : line.Speaker), line.Text, exactSecrets);
                else if (!line.IsTaiwu)
                    AppendChecked(sb, "**「", Clean(string.IsNullOrWhiteSpace(line.Speaker) ? "某人" : line.Speaker,
                        exactSecrets), "」的执行结果：**\n\n");
                if (!line.IsTaiwu) AppendExecutionResults(sb, line.Actions, line.ToolResults, exactSecrets);
                if (ChatImageReference.IsValid(line.ImageFileName))
                    AppendChecked(sb, "![生成图片](Images/", line.ImageFileName, ")\n\n");
            }
        }

        private static bool HasToolResults(IList<string> toolResults)
        {
            if (toolResults == null) return false;
            foreach (string result in toolResults)
                if (!string.IsNullOrWhiteSpace(result)) return true;
            return false;
        }

        /// <summary>只枚举该 NPC 实际参与的群聊，并按 8MiB 导出余量从最新向前有界读取。
        /// 与 UI 使用同一稳定游标，不会先把其他群聊或无限归档全部装入内存。</summary>
        private static List<GroupChatOrchestrator.GroupSession> LoadBoundedNpcGroupHistory(
            int taiwuId, int npcId, string npcName, int charBudget, out bool truncated)
        {
            var selected = new List<GroupChatOrchestrator.GroupSession>();
            ExportScope scope = RequireScope();
            truncated = false;
            if (charBudget <= 0) { truncated = true; return selected; }
            var loader = new GroupChatOrchestrator();
            GroupChatOrchestrator.MemberHistoryCursor cursor = null;
            while (charBudget > 0)
            {
                GroupChatOrchestrator.MemberHistoryPage page = loader.LoadSessionsForMemberPage(
                    taiwuId, npcId, npcName, 1000, cursor,
                    scope.WorldId, scope.ChatLogs);
                if (page == null || page.Sessions == null || page.Sessions.Count == 0)
                {
                    if (page?.EarlierCursor != null) truncated = true;
                    break;
                }

                var acceptedPage = new List<GroupChatOrchestrator.GroupSession>();
                bool pageTruncated = false;
                for (int si = page.Sessions.Count - 1; si >= 0 && !pageTruncated; si--)
                {
                    GroupChatOrchestrator.GroupSession session = page.Sessions[si];
                    int headerCost = 128 + (session?.Participants?.Length ?? 0);
                    if (headerCost > charBudget) { pageTruncated = true; break; }
                    var acceptedLines = new List<GroupChatOrchestrator.Line>();
                    IReadOnlyList<GroupChatOrchestrator.Line> lines = session?.Lines;
                    if (lines != null)
                        for (int li = lines.Count - 1; li >= 0; li--)
                        {
                            GroupChatOrchestrator.Line line = lines[li];
                            int lineCost;
                            try
                            {
                                lineCost = checked(96 + (line?.Speaker?.Length ?? 0)
                                    + (line?.Text?.Length ?? 0) + ExecutionResultsLength(line?.Actions, line?.ToolResults));
                            }
                            catch { lineCost = int.MaxValue; }
                            if (headerCost + lineCost > charBudget)
                            { pageTruncated = true; break; }
                            acceptedLines.Insert(0, line);
                            headerCost += lineCost;
                        }
                    if (acceptedLines.Count > 0)
                    {
                        charBudget -= headerCost;
                        acceptedPage.Insert(0, new GroupChatOrchestrator.GroupSession
                        {
                            Participants = session.Participants,
                            MemberIds = session.MemberIds == null ? new List<int>() : new List<int>(session.MemberIds),
                            Lines = acceptedLines,
                            IsCurrent = session.IsCurrent,
                        });
                    }
                    else pageTruncated = true;
                }
                if (acceptedPage.Count > 0) selected.InsertRange(0, acceptedPage);
                if (pageTruncated) { truncated = true; break; }
                cursor = page.EarlierCursor;
                if (cursor == null) break;
            }
            if (cursor != null) truncated = true;
            return selected;
        }

        private static void AppendSpeech(StringBuilder sb, string who, string text, string[] exactSecrets)
            => AppendChecked(sb, "**", Clean(who, exactSecrets), "：** ",
                Clean(text, exactSecrets), "\n\n");

        private static void AppendExecutionResults(StringBuilder sb, IList<string> actions,
            IList<string> toolResults, string[] exactSecrets)
        {
            IList<string> source = toolResults != null && toolResults.Count > 0 ? toolResults : actions;
            if (source == null || source.Count == 0) return;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in source)
            {
                string value = (raw ?? string.Empty).Trim();
                if (value.Length == 0 || !seen.Add(value)) continue;
                AppendChecked(sb, "> **执行结果：** ", Clean(value, exactSecrets), "\n\n");
            }
        }

        private static int ExecutionResultsLength(IList<string> actions, IList<string> toolResults)
        {
            IList<string> source = toolResults != null && toolResults.Count > 0 ? toolResults : actions;
            if (source == null) return 0;
            int total = 0;
            try
            {
                foreach (string value in source) total = checked(total + (value?.Length ?? 0) + 32);
                return total;
            }
            catch { return int.MaxValue; }
        }

        private static StringBuilder Header(string type, int taiwuId, string[] exactSecrets)
        {
            ExportScope scope = RequireScope();
            var sb = new StringBuilder();
            sb.Append("# 江湖有灵聊天记录 · ").Append(Clean(type, exactSecrets)).Append("\n\n")
              .Append("- 世界 ID：").Append(scope.WorldId).Append("\n")
              .Append("- 太吾 ID：").Append(taiwuId).Append("\n")
              .Append("- 导出时间：").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\n")
              .Append("- 说明：仅导出玩家可见对话；不含模型思考、系统提示词、工具参数、人设、世界书或密钥。\n\n");
            EnsureWithinLimit(sb);
            return sb;
        }

        private static string FormatDate(int date)
            => date < 0 ? "日期未知" : (date == 0 ? "旧记录未保存日期" : ("第 " + (date / 12 + 1) + " 年 · " + (date % 12 + 1) + " 月"));

        private static Result Write(string stem, StringBuilder content, int count, string[] exactSecrets)
        {
            ExportScope scope = RequireScope();
            lock (ExportGate)
            {
                EnsureScopeCurrent(scope);
                EnsureWithinLimit(content);
                Directory.CreateDirectory(scope.Exports);
                string prefix = SafeName(stem, exactSecrets) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                string path = Path.Combine(scope.Exports, prefix + ".md");
                for (int suffix = 2; File.Exists(path); suffix++)
                    path = Path.Combine(scope.Exports, prefix + "_" + suffix + ".md");

                // Final defense covers future header/metadata additions that accidentally
                // bypass field-level Clean without altering Markdown formatting.
                string safeContent = SecretRedactor.Redact(content?.ToString() ?? "", exactSecrets);
                if (safeContent.Length > MaxExportChars) throw new ExportLimitExceededException();
                byte[] expected = EncodeUtf8Bom(safeContent);
                string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        4096, FileOptions.WriteThrough))
                    {
                        stream.Write(expected, 0, expected.Length);
                        stream.Flush(true);
                    }

                    if (!FileMatchesExpectedBytes(tmp, expected))
                        throw new InvalidDataException("聊天导出临时文件回读不一致");
                    EnsureScopeCurrent(scope);
                    File.Move(tmp, path);
                    return new Result { Ok = true, Path = path, ConversationCount = count };
                }
                catch
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    throw;
                }
            }
        }

        private static bool FileMatchesExpectedBytes(string path, byte[] expected)
        {
            if (expected == null) return false;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != expected.LongLength) return false;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var buffer = new byte[81920];
                int offset = 0;
                while (offset < expected.Length)
                {
                    int read = stream.Read(buffer, 0, Math.Min(buffer.Length, expected.Length - offset));
                    if (read <= 0) return false;
                    for (int i = 0; i < read; i++)
                        if (buffer[i] != expected[offset + i]) return false;
                    offset += read;
                }
                return stream.ReadByte() < 0;
            }
        }

        private static byte[] EncodeUtf8Bom(string text)
        {
            string value = text ?? "";
            byte[] preamble = Utf8Bom.GetPreamble();
            int bodyLength = Utf8Bom.GetByteCount(value);
            var bytes = new byte[checked(preamble.Length + bodyLength)];
            Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
            Utf8Bom.GetBytes(value, 0, value.Length, bytes, preamble.Length);
            return bytes;
        }

        private static Result Fail(Exception ex)
            // Filesystem exception messages commonly contain the local Windows user path.
            // The UI only needs a stable category; never echo the message into chat.
            => ex is ExportLimitExceededException
                ? new Result { Ok = false, Error = "聊天记录过大；单次导出上限为 8 MiB 字符，请导出当前页或分批处理" }
                : new Result { Ok = false, Error = "聊天导出失败（" + (ex?.GetType().Name ?? "UnknownError") + "）" };

        private static string Clean(string s, string[] exactSecrets)
        {
            if ((s?.Length ?? 0) > MaxExportChars) throw new ExportLimitExceededException();
            StreamingToolCollector.SplitThink(s ?? "", out string visible, out _);
            string t = TmpTag.Replace(TalkOrchestrator.StripInternalExecutionTags(visible), "");
            t = t.Replace("\r\n", "\n").Trim();
            string safe = SecretRedactor.Redact(t, exactSecrets);
            if (safe.Length > MaxExportChars) throw new ExportLimitExceededException();
            return safe;
        }

        private static void AppendChecked(StringBuilder sb, params string[] values)
        {
            if (sb == null) throw new ArgumentNullException(nameof(sb));
            long additional = 0;
            if (values != null)
                foreach (string value in values) additional += value?.Length ?? 0;
            if (additional > MaxExportChars || sb.Length > MaxExportChars - additional)
                throw new ExportLimitExceededException();
            if (values != null) foreach (string value in values) sb.Append(value);
        }

        private static void EnsureWithinLimit(StringBuilder sb)
        {
            if (sb == null || sb.Length > MaxExportChars)
                throw new ExportLimitExceededException();
        }

        private static string SafeName(string s, string[] exactSecrets)
        {
            string t = Clean(s, exactSecrets);
            foreach (char c in Path.GetInvalidFileNameChars()) t = t.Replace(c, '_');
            t = t.Replace('\n', '_').Replace('\r', '_').Trim(' ', '.');
            if (t.Length == 0) t = "聊天记录";
            return t.Length <= 72 ? t : t.Substring(0, 72);
        }
    }
}
