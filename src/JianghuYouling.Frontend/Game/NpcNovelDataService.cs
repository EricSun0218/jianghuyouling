using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Memory;
using JianghuYouling.Core.Persistence;
using JianghuYouling.Core.Prompt;
using JianghuYouling.Core.Security;
using Newtonsoft.Json;

namespace JianghuYouling
{
    /// <summary>人物小说的世界隔离素材快照、草稿和导出。</summary>
    public static class NpcNovelDataService
    {
        public sealed class Scope
        {
            internal uint WorldId;
            internal int Generation;
            internal int TaiwuId;
            internal int NpcId;
            internal string NpcName;
            internal string ChatLogs;
            internal string Memories;
            internal string Novels;
            internal string Exports;
            internal string SingleSummary;
            internal List<TalkTurn> SingleTurns;

            internal bool IsCurrent
                => WorldId > 0 && WorldLifecycle.WorldId == WorldId
                    && WorldLifecycle.IsSameWorld(Generation)
                    && CurrentTaiwuId() == TaiwuId;
        }

        public sealed class SourceResult
        {
            public bool Ok;
            public string Source;
            public bool Truncated;
            public int ChatLines;
            public int MemoryCount;
            public string Error;
        }

        public sealed class Draft
        {
            public uint WorldId;
            public int TaiwuId;
            public int NpcId;
            public string NpcName;
            public string Prompt;
            public string Content;
            public string GeneratedAt;
        }

        public sealed class ExportResult
        {
            public bool Ok;
            public string Path;
            public string Error;
        }

        private const int MaxDraftBytes = 4 * 1024 * 1024;
        private const int MaxDraftChars = 2 * 1024 * 1024;

        /// <summary>必须在 Unity 主线程调用；把易变会话复制成不可变快照后才交给后台线程。</summary>
        public static Scope Capture(int taiwuId, int npcId, string npcName)
        {
            int generation = WorldLifecycle.Generation;
            uint worldId = WorldLifecycle.WorldId;
            if (worldId == 0 || taiwuId <= 0 || npcId <= 0 || CurrentTaiwuId() != taiwuId)
                return null;
            IReadOnlyList<TalkTurn> history = TalkOrchestrator.History(taiwuId, npcId);
            var turns = history == null ? new List<TalkTurn>() : history.ToList();
            string summary = TalkOrchestrator.SummaryOf(taiwuId, npcId);
            var scope = new Scope
            {
                WorldId = worldId,
                Generation = generation,
                TaiwuId = taiwuId,
                NpcId = npcId,
                NpcName = ReadableName(npcName, npcId),
                ChatLogs = Path.GetFullPath(JianghuYoulingPaths.ChatLogs),
                Memories = Path.GetFullPath(JianghuYoulingPaths.Memories),
                Novels = Path.GetFullPath(JianghuYoulingPaths.Novels),
                Exports = Path.GetFullPath(JianghuYoulingPaths.Exports),
                SingleSummary = summary,
                SingleTurns = turns,
            };
            return scope.IsCurrent ? scope : null;
        }

        /// <summary>只包含当前 NPC 的单聊、其实际参与群聊和它自己的长期记忆。</summary>
        public static SourceResult BuildSource(Scope scope, int maxChars)
        {
            try
            {
                EnsureCurrent(scope);
                maxChars = Math.Max(3000, Math.Min(300000, maxChars));
                string[] secrets = ConfiguredSecretSnapshot.LoadOrThrow();
                var memoryStore = NpcMemoryStore.Load(scope.Memories,
                    scope.TaiwuId.ToString(), scope.NpcId.ToString());
                if (!memoryStore.LoadReliable)
                    return FailSource("人物记忆暂时无法可靠读取，请稍后重试");

                int singleBudget = Math.Max(2200, maxChars * 42 / 100);
                int memoryBudget = Math.Max(1400, maxChars * 28 / 100);
                int groupBudget = Math.Max(1400, maxChars - singleBudget - memoryBudget - 1200);
                var sb = new StringBuilder(Math.Min(maxChars, 65536));
                int chatLines = 0, memoryCount = 0;
                bool truncated = false;

                Append(sb, "【与" + scope.NpcName + "的单聊】\n", maxChars, ref truncated);
                if (!string.IsNullOrWhiteSpace(scope.SingleSummary))
                {
                    string summary = Clean(scope.SingleSummary, secrets);
                    Append(sb, "较早交谈梗概：" + Tail(summary, Math.Max(600, singleBudget / 3)) + "\n", maxChars, ref truncated);
                }
                List<string> singleLines = RecentSingleLines(scope.SingleTurns, scope.NpcName,
                    secrets, singleBudget, out bool singleTruncated);
                truncated |= singleTruncated;
                foreach (string line in singleLines)
                {
                    Append(sb, line, maxChars, ref truncated);
                    chatLines++;
                }
                if (singleLines.Count == 0 && string.IsNullOrWhiteSpace(scope.SingleSummary))
                    Append(sb, "（尚无单聊记录）\n", maxChars, ref truncated);

                Append(sb, "\n【" + scope.NpcName + "的长期记忆】\n", maxChars, ref truncated);
                List<MemoryEntry> memories = SelectMemories(memoryStore.All, memoryBudget,
                    secrets, out bool memoryTruncated);
                truncated |= memoryTruncated;
                foreach (MemoryEntry memory in memories)
                {
                    Append(sb, "[" + MemoryLabel(memory.Type) + "·第" + FormatDate(memory.WorldDate)
                        + "] " + Clean(memory.Content, secrets) + "\n", maxChars, ref truncated);
                    memoryCount++;
                }
                if (memories.Count == 0) Append(sb, "（尚无长期记忆）\n", maxChars, ref truncated);

                Append(sb, "\n【" + scope.NpcName + "实际参与过的群聊】\n", maxChars, ref truncated);
                List<string> groupLines = RecentGroupLines(scope, groupBudget, secrets,
                    out bool groupTruncated);
                truncated |= groupTruncated;
                foreach (string line in groupLines)
                {
                    Append(sb, line, maxChars, ref truncated);
                    chatLines++;
                }
                if (groupLines.Count == 0) Append(sb, "（尚无群聊记录）\n", maxChars, ref truncated);

                if (truncated)
                    Append(sb, "\n（素材较多，已保留较早梗概、重要记忆和较新的逐句记录。）\n",
                        maxChars, ref truncated);
                EnsureCurrent(scope);
                if (chatLines == 0 && memoryCount == 0 && string.IsNullOrWhiteSpace(scope.SingleSummary))
                    return FailSource("此人还没有可用于写作的聊天记录或记忆");
                return new SourceResult
                {
                    Ok = true,
                    Source = sb.ToString().Trim(),
                    Truncated = truncated,
                    ChatLines = chatLines,
                    MemoryCount = memoryCount,
                };
            }
            catch (OperationCanceledException) { return FailSource("存档已切换，本次写作已取消"); }
            catch (Exception ex) { return FailSource("素材读取失败（" + ex.GetType().Name + "）"); }
        }

        public static Draft LoadDraft(Scope scope)
        {
            try
            {
                EnsureCurrent(scope);
                string path = DraftPath(scope);
                if (!DurableFileStore.TryReadRecoverableText(path, MaxDraftBytes,
                        raw => IsValidDraft(raw, scope), out string raw, out _, out _)) return null;
                Draft draft = JsonConvert.DeserializeObject<Draft>(raw);
                return draft != null && draft.WorldId == scope.WorldId
                    && draft.TaiwuId == scope.TaiwuId && draft.NpcId == scope.NpcId ? draft : null;
            }
            catch { return null; }
        }

        public static bool SaveDraft(Scope scope, string prompt, string content)
        {
            try
            {
                EnsureCurrent(scope);
                string safeContent = CleanNovel(content, ConfiguredSecretSnapshot.LoadOrThrow());
                if (safeContent.Length == 0 || safeContent.Length > MaxDraftChars) return false;
                var draft = new Draft
                {
                    WorldId = scope.WorldId,
                    TaiwuId = scope.TaiwuId,
                    NpcId = scope.NpcId,
                    NpcName = scope.NpcName,
                    Prompt = (prompt ?? string.Empty).Trim(),
                    Content = safeContent,
                    GeneratedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                };
                string json = JsonConvert.SerializeObject(draft, Formatting.Indented);
                EnsureCurrent(scope);
                return DurableFileStore.TryWriteTextAtomic(DraftPath(scope), json,
                    MaxDraftBytes, raw => IsValidDraft(raw, scope));
            }
            catch { return false; }
        }

        public static ExportResult Export(Scope scope, string content)
        {
            try
            {
                EnsureCurrent(scope);
                string[] secrets = ConfiguredSecretSnapshot.LoadOrThrow();
                string safe = CleanNovel(content, secrets);
                if (safe.Length == 0) return FailExport("还没有可导出的小说正文");
                if (safe.Length > MaxDraftChars) return FailExport("小说正文过长，无法安全导出");
                Directory.CreateDirectory(scope.Exports);
                string stem = "人物小说_" + SafeFileName(scope.NpcName) + "_"
                    + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                string path = Path.Combine(scope.Exports, stem + ".txt");
                for (int suffix = 2; File.Exists(path); suffix++)
                    path = Path.Combine(scope.Exports, stem + "_" + suffix + ".txt");
                string exported = safe + Environment.NewLine;
                byte[] bytes = new UTF8Encoding(true).GetBytes(exported);
                if (bytes.Length > MaxDraftBytes) return FailExport("小说正文过长，无法安全导出");
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                    var info = new FileInfo(temporary);
                    if (!info.Exists || info.Length != bytes.LongLength)
                        throw new InvalidDataException("小说导出暂存文件回读不一致");
                    EnsureCurrent(scope);
                    File.Move(temporary, path);
                    return new ExportResult { Ok = true, Path = path };
                }
                catch
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                    throw;
                }
            }
            catch (OperationCanceledException) { return FailExport("存档已切换，本次导出已取消"); }
            catch (Exception ex) { return FailExport("小说导出失败（" + ex.GetType().Name + "）"); }
        }

        private static List<string> RecentSingleLines(IReadOnlyList<TalkTurn> turns,
            string npcName, string[] secrets, int budget, out bool truncated)
        {
            var reversed = new List<string>();
            int used = 0;
            truncated = false;
            if (turns != null) for (int i = turns.Count - 1; i >= 0; i--)
            {
                TalkTurn turn = turns[i];
                if (turn == null) continue;
                string line = RenderTurn(turn, TalkTurnKinds.ContextSpeaker(turn, npcName), secrets);
                if (line.Length == 0) continue;
                if (used + line.Length > budget) { truncated = true; break; }
                reversed.Add(line);
                used += line.Length;
            }
            reversed.Reverse();
            return reversed;
        }

        private static List<string> RecentGroupLines(Scope scope, int budget,
            string[] secrets, out bool truncated)
        {
            var selected = new List<string>();
            truncated = false;
            int used = 0;
            var loader = new GroupChatOrchestrator();
            GroupChatOrchestrator.MemberHistoryCursor cursor = null;
            while (used < budget)
            {
                GroupChatOrchestrator.MemberHistoryPage page = loader.LoadSessionsForMemberPage(
                    scope.TaiwuId, scope.NpcId, scope.NpcName, 1000, cursor,
                    scope.WorldId, scope.ChatLogs);
                if (page?.Sessions == null || page.Sessions.Count == 0)
                {
                    if (page?.EarlierCursor != null) truncated = true;
                    break;
                }
                var pageLines = new List<string>();
                for (int si = page.Sessions.Count - 1; si >= 0; si--)
                {
                    GroupChatOrchestrator.GroupSession session = page.Sessions[si];
                    var oneSession = new List<string>();
                    IReadOnlyList<GroupChatOrchestrator.Line> lines = session?.Lines;
                    if (lines != null) for (int li = lines.Count - 1; li >= 0; li--)
                    {
                        GroupChatOrchestrator.Line line = lines[li];
                        if (line == null) continue;
                        string who = line.IsTaiwu ? "太吾"
                            : (string.IsNullOrWhiteSpace(line.Speaker) ? "某位江湖人" : line.Speaker);
                        string rendered = RenderSpeechAndResults(line.Text, who,
                            line.IsTaiwu ? null : line.Actions,
                            line.IsTaiwu ? null : line.ToolResults, line.Date, secrets);
                        if (rendered.Length == 0) continue;
                        if (used + rendered.Length + 80 > budget)
                        { truncated = true; break; }
                        oneSession.Insert(0, rendered);
                        used += rendered.Length;
                    }
                    if (oneSession.Count > 0)
                    {
                        string header = "-- 群聊：" + Clean(session.Participants, secrets) + " --\n";
                        if (used + header.Length <= budget)
                        {
                            oneSession.Insert(0, header);
                            used += header.Length;
                        }
                        pageLines.InsertRange(0, oneSession);
                    }
                    if (truncated) break;
                }
                if (pageLines.Count > 0) selected.InsertRange(0, pageLines);
                if (truncated) break;
                cursor = page.EarlierCursor;
                if (cursor == null) break;
            }
            if (cursor != null) truncated = true;
            return selected;
        }

        private static List<MemoryEntry> SelectMemories(IReadOnlyList<MemoryEntry> source,
            int budget, string[] secrets, out bool truncated)
        {
            var chosen = new List<MemoryEntry>();
            truncated = false;
            if (source == null) return chosen;
            IEnumerable<MemoryEntry> ranked = source.Where(x => x != null && x.Valid
                    && !string.IsNullOrWhiteSpace(x.Content))
                .OrderByDescending(x => x.IsCore)
                .ThenByDescending(x => x.Importance)
                .ThenByDescending(x => x.WorldDate)
                .ThenBy(x => x.Id ?? string.Empty, StringComparer.Ordinal);
            int used = 0, eligible = 0;
            foreach (MemoryEntry memory in ranked)
            {
                eligible++;
                int cost = Clean(memory.Content, secrets).Length + 42;
                if (used + cost > budget) { truncated = true; continue; }
                chosen.Add(memory);
                used += cost;
            }
            if (chosen.Count < eligible) truncated = true;
            chosen.Sort((a, b) =>
            {
                int date = a.WorldDate.CompareTo(b.WorldDate);
                return date != 0 ? date : string.Compare(a.Id, b.Id, StringComparison.Ordinal);
            });
            return chosen;
        }

        private static string RenderTurn(TalkTurn turn, string who, string[] secrets)
            => RenderSpeechAndResults(turn.Text, who,
                turn.FromPlayer || TalkTurnKinds.IsNative(turn) ? null : turn.Actions,
                turn.FromPlayer || TalkTurnKinds.IsNative(turn) ? null : turn.ToolResults,
                turn.Date, secrets);

        private static string RenderSpeechAndResults(string text, string who,
            IList<string> actions, IList<string> results, int date, string[] secrets)
        {
            var sb = new StringBuilder();
            string visible = Clean(text, secrets);
            if (visible.Length > 0)
                sb.Append('[').Append(FormatDate(date)).Append("] ")
                    .Append(Clean(who, secrets)).Append("：").Append(visible).Append('\n');
            IList<string> source = results != null && results.Count > 0 ? results : actions;
            if (source != null)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (string raw in source)
                {
                    string value = Clean(raw, secrets);
                    if (value.Length == 0 || !seen.Add(value)) continue;
                    sb.Append("  已确认结果：").Append(value).Append('\n');
                }
            }
            return sb.ToString();
        }

        private static string Clean(string value, string[] secrets)
        {
            StreamingToolCollector.SplitThink(value ?? string.Empty, out string visible, out _);
            string safe = TalkOrchestrator.StripInternalExecutionTags(visible)
                .Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            return SecretRedactor.Redact(safe, secrets);
        }

        public static string CleanNovel(string value, string[] secrets)
        {
            StreamingToolCollector.SplitThink(value ?? string.Empty, out string visible, out _);
            string safe = TalkOrchestrator.StripInternalExecutionTags(visible)
                .Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            return SecretRedactor.Redact(safe, secrets);
        }

        private static string Tail(string value, int max)
            => string.IsNullOrEmpty(value) || value.Length <= max ? (value ?? string.Empty)
                : "……" + value.Substring(value.Length - max);

        private static void Append(StringBuilder sb, string value, int max, ref bool truncated)
        {
            if (string.IsNullOrEmpty(value) || sb.Length >= max) { if (!string.IsNullOrEmpty(value)) truncated = true; return; }
            int take = Math.Min(value.Length, max - sb.Length);
            sb.Append(value, 0, take);
            if (take < value.Length) truncated = true;
        }

        private static string MemoryLabel(MemoryType type)
        {
            switch (type)
            {
                case MemoryType.Favor: return "恩情";
                case MemoryType.Grudge: return "仇怨";
                case MemoryType.Promise: return "承诺";
                case MemoryType.Secret: return "秘闻";
                case MemoryType.Event: return "经历";
                default: return "印象";
            }
        }

        private static string FormatDate(long date)
            => date <= 0 ? "日期未详" : ((date / 12 + 1) + "年" + (date % 12 + 1) + "月");

        private static string ReadableName(string name, int npcId)
            => string.IsNullOrWhiteSpace(name) || TalkOrchestrator.IsUnresolvedNpcName(name)
                ? ("人物" + npcId) : name.Trim();

        private static string DraftPath(Scope scope)
            => Path.Combine(scope.Novels, "Novel_" + scope.TaiwuId + "_" + scope.NpcId + ".json");

        private static bool IsValidDraft(string raw, Scope scope)
        {
            try
            {
                if (raw == null || raw.Length == 0 || raw.Length > MaxDraftChars || raw.IndexOf('\0') >= 0) return false;
                Draft draft = JsonConvert.DeserializeObject<Draft>(raw);
                return draft != null && draft.WorldId == scope.WorldId
                    && draft.TaiwuId == scope.TaiwuId && draft.NpcId == scope.NpcId
                    && NpcNovelPromptBuilder.IsValidCustomPrompt(draft.Prompt)
                    && !string.IsNullOrWhiteSpace(draft.Content) && draft.Content.Length <= MaxDraftChars;
            }
            catch { return false; }
        }

        private static string SafeFileName(string value)
        {
            string safe = value ?? "人物";
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            safe = safe.Replace('\r', '_').Replace('\n', '_').Trim(' ', '.');
            if (safe.Length == 0) safe = "人物";
            return safe.Length <= 48 ? safe : safe.Substring(0, 48);
        }

        private static void EnsureCurrent(Scope scope)
        {
            if (scope == null || !scope.IsCurrent)
                throw new OperationCanceledException("存档已切换");
        }

        private static int CurrentTaiwuId()
        {
            try { return SingletonObject.getInstance<BasicGameData>().TaiwuCharId; }
            catch { return 0; }
        }

        private static SourceResult FailSource(string error)
            => new SourceResult { Ok = false, Error = error };

        private static ExportResult FailExport(string error)
            => new ExportResult { Ok = false, Error = error };
    }
}
