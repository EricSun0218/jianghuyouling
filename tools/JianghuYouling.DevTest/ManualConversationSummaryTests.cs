using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JianghuYouling.Core.Memory;
using JianghuYouling.Core.Persistence;
using JianghuYouling.Core.Prompt;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.DevTest
{
    internal static class ManualConversationSummaryTests
    {
        internal static void Run()
        {
            TestCumulativeMaintenanceAndArchive();
            TestSummaryDisplayOrder();
            TestFrontendSourceContracts();
            Console.WriteLine("[PASS] 手动总结：累计提示/严格解析/连续冷归档/梗概显示顺序运行测试，以及入口/提交/取消/UI 源码契约（非游戏内 E2E）");
        }

        private static void TestCumulativeMaintenanceAndArchive()
        {
            string directory = Path.Combine(Path.GetTempPath(), "JianghuYouling.SummaryTest." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var first = Turns("first", "上次压缩后原话甲", "角色回复乙");
                const string originalSummary = "太吾与柳青相识于村口。";
                const string firstSummary = "太吾与柳青相识于村口，约好下月重逢。";
                var firstPrompt = ConversationMaintenance.BuildMessages("柳青", originalSummary, first);
                Check(firstPrompt.Count == 2 && firstPrompt[1].Content.Contains(originalSummary)
                    && firstPrompt[1].Content.Contains(first[0].Text)
                    && firstPrompt[1].Content.Contains(first[1].Text), "prior summary and every new turn enter the shared prompt");
                Check(firstPrompt[0].Content.Contains("已有梗概") && firstPrompt[0].Content.Contains("滚动梗概"),
                    "shared maintenance requests a cumulative summary");
                string response = new JObject { ["summary"] = firstSummary, ["memories"] = new JArray() }.ToString();
                Check(ConversationMaintenance.TryParse(response, 21, MemoryFlush.BuildAllowedSourceLineIds(first),
                    out string parsedFirst, out List<MemoryEntry> firstMemories)
                    && parsedFirst == firstSummary && firstMemories.Count == 0,
                    "valid summary with no new long-term memory is accepted");

                var archive = ConversationColdArchiveStore.Load(directory, 71, 1001, 2002);
                string firstHash = ConversationColdArchiveStore.ComputeSourceHash(originalSummary, null, first);
                Check(archive.Append(firstHash, originalSummary, null, parsedFirst, first), "first summary archives original turns");
                var second = Turns("second", "千里传音新话丙", "传音回复丁");
                foreach (TalkTurn turn in second) turn.ContactMode = "千里传音";
                var secondPrompt = ConversationMaintenance.BuildMessages("柳青", parsedFirst, second);
                Check(secondPrompt[1].Content.Contains(firstSummary) && secondPrompt[1].Content.Contains(second[0].Text)
                    && secondPrompt[1].Content.Contains(second[1].Text)
                    && !secondPrompt[1].Content.Contains(first[0].Text)
                    && !secondPrompt[1].Content.Contains(first[1].Text),
                    "next summary carries prior rollup but does not resend already compressed raw turns");
                const string secondSummary = "太吾与柳青相识于村口，约好下月重逢，又经千里传音互报平安。";
                Check(ConversationMaintenance.TryParse(new JObject { ["summary"] = secondSummary, ["memories"] = new JArray() }.ToString(),
                    22, MemoryFlush.BuildAllowedSourceLineIds(second), out string parsedSecond, out _), "second cumulative summary parses");
                string secondHash = ConversationColdArchiveStore.ComputeSourceHash(parsedFirst, firstHash, second);
                Check(firstHash != secondHash && archive.Append(secondHash, parsedFirst, firstHash, parsedSecond, second),
                    "second segment chains to prior summary source hash");
                Check(archive.Append(secondHash, parsedFirst, firstHash, "重试生成不同措辞", second), "same-source retry is idempotent");
                var reopened = ConversationColdArchiveStore.LoadReadOnly(directory, 71, 1001, 2002);
                Check(reopened.LoadReliable && reopened.Snapshot().Count == 2
                    && reopened.Snapshot()[1].PriorSummary == parsedFirst
                    && reopened.Snapshot()[1].PriorSummarySourceHash == firstHash
                    && reopened.Snapshot()[1].Summary == parsedSecond, "two consecutive summaries survive reopen with cumulative lineage");
                Check(reopened.RecoverTurns().Select(t => t.Id).SequenceEqual(first.Concat(second).Select(t => t.Id))
                    && reopened.RecoverTurns().Last().ContactMode == "千里传音", "all original local and remote turns remain exportable");

                foreach (string invalid in new[] { "not JSON", "{\"summary\":\"\",\"memories\":[]}",
                    "{\"summary\":\"缺少记忆字段\"}", "{\"summary\":\"甲\",\"summary\":\"乙\",\"memories\":[]}" })
                    Check(!ConversationMaintenance.TryParse(invalid, 22, MemoryFlush.BuildAllowedSourceLineIds(second), out _, out _),
                        "malformed maintenance cannot become a summary");
                Check(!archive.Append(secondHash, parsedFirst, firstHash, parsedSecond, new List<TalkTurn>()),
                    "empty source cannot create an archive segment");
                var blocked = ConversationColdArchiveStore.Load(directory, 71, 1001, 2003);
                Directory.CreateDirectory(blocked.Path);
                Check(!blocked.Append(firstHash, originalSummary, null, parsedFirst, first), "archive I/O failure is reported");
                Check(first.Count == 2 && first[0].Text == "上次压缩后原话甲", "failed archive does not mutate source turns");
            }
            finally { Directory.Delete(directory, true); }
        }

        private static List<TalkTurn> Turns(string id, string player, string npc)
        {
            return new List<TalkTurn>
            {
                new TalkTurn { Id = id + "-player", ExchangeId = id, FromPlayer = true, Text = player, Date = 21 },
                new TalkTurn { Id = id + "-npc", ExchangeId = id, FromPlayer = false, Text = npc, Date = 21 },
            };
        }

        private static void TestSummaryDisplayOrder()
        {
            var archived = Turns("archived", "已压缩原文", "已压缩回话");
            var live = Turns("live", "未压缩新话", "未压缩新回话");
            var pager = new ConversationHistoryPager(live, "累计", () => archived.AsEnumerable().Reverse().GetEnumerator());
            var page = pager.ReadPreviousPage();
            Check(page.FindIndex(e => e.Summary == "累计") == 2 && page.Count == 5,
                "automatic summary appears after original messages and before newer live messages");
            pager = new ConversationHistoryPager(null, "手动累计", () => archived.AsEnumerable().Reverse().GetEnumerator());
            page = pager.ReadPreviousPage();
            Check(page.Last().Summary == "手动累计", "all-turn summary follows all archived original messages");
            pager = new ConversationHistoryPager(live, "旧梗概", () => live.AsEnumerable().Reverse().GetEnumerator());
            page = pager.ReadPreviousPage();
            Check(page.Count == 3 && page[0].Summary == "旧梗概", "uncommitted duplicate archive copies do not displace the summary");
            pager = new ConversationHistoryPager(null, "梗概", null);
            page = pager.ReadPreviousPage();
            Check(page.Count == 1 && page[0].Summary == "梗概", "summary-only history has no phantom turn");
        }

        // Unity's coroutine/UI surfaces are source-contract checks here, not runtime game tests.
        private static void TestFrontendSourceContracts()
        {
            string talk = ReadRepoFile("src/JianghuYouling.Frontend/Talk/TalkOrchestrator.cs");
            string manual = Section(talk, "internal static bool TrySummarizeConversation(", "private static void ScheduleCompactionAfterCommit(");
            Check(manual.Contains("int cut = conversation.Turns?.Count ?? 0;") && !manual.Contains("ComputeCompactCut("),
                "manual cut includes every currently uncompressed turn without the automatic threshold");
            Check(manual.IndexOf("if (cut == 0)", StringComparison.Ordinal) < manual.IndexOf("if (!LlmService.IsConfigured)", StringComparison.Ordinal),
                "empty history returns before configuration/request work");
            Check(manual.Contains("TryBeginCompaction(conversation, cancellationToken)")
                && manual.Contains("RunCompactionAfterCommit(conversation, npcName, currentDate, cut,")
                && !manual.Contains("SendAsync"), "manual entry shares the automatic single-flight lease and maintenance runner");
            string lease = Section(talk, "private static CompactionLease TryBeginCompaction(", "private static void EndCompaction(");
            Check(lease.Contains("ActiveCompactions.ContainsKey(c.CacheKey)") && lease.Contains("CreateLinkedTokenSource("),
                "same-conversation overlap is rejected and manual cancellation is linked");
            string runner = Section(talk, "private static IEnumerator RunCompactionAfterCommit(", "private static Conversation ReadConversationRecoverable(");
            Check(runner.Contains("ConversationMaintenance.BuildMessages(npcName, summaryAtStart, old)")
                && runner.Contains("CompactionPrefixStillCurrent(conversation, old, summaryAtStart, summarySourceHashAtStart)")
                && runner.Contains("ct.IsCancellationRequested") && runner.Contains("WorldLifecycle.IsSameWorld(worldGeneration)"),
                "shared runner protects cumulative source, cancellation and world changes");
            int archive = runner.IndexOf("archive.Append(compactionSourceHash", StringComparison.Ordinal);
            int removal = runner.IndexOf("conversation.Turns.RemoveRange(0, old.Count)", StringComparison.Ordinal);
            int save = runner.IndexOf("if (!SaveConv(", StringComparison.Ordinal);
            int success = runner.IndexOf("else conversationCommitted = true;", StringComparison.Ordinal);
            int callback = runner.IndexOf("onCompleted?.Invoke(conversationCommitted", StringComparison.Ordinal);
            Check(archive >= 0 && removal > archive && save > removal && success > save && callback > success,
                "archive then save precede the successful UI callback");
            Check(runner.Contains("conversation.Summary = summaryBeforeCommit;")
                && runner.Contains("conversation.SummarySourceHash = sourceHashBeforeCommit;")
                && runner.Contains("conversation.Turns = turnsBeforeCommit;")
                && runner.Contains("RollbackCompactionMemories("), "save failure restores raw turns, old rollup and extracted memories");
            Check(runner.IndexOf("try", StringComparison.Ordinal) < runner.IndexOf("yield return null;", StringComparison.Ordinal)
                && runner.Contains("finally") && runner.Contains("EndCompaction(conversation, lease);"),
                "lease cleanup covers cancellation even before the first request");

            string ui = ReadRepoFile("src/JianghuYouling.Frontend/UI/ChatWindow.cs");
            string start = Section(ui, "void SummarizeConversation()", "void FinishConversationSummary(");
            Check(start.Contains("_assistantMode || _groupMode || _busy") && !start.Contains("_remote")
                && start.Contains("NpcProactiveChatScheduler.IsGeneratingFor("), "only idle NPC single chat, including remote chat, can start");
            string finish = Section(ui, "void FinishConversationSummary(", "void OpenTaiwuDirectAction(");
            Check(finish.Contains("ReferenceEquals(_summaryCancellation, cancellation)")
                && finish.Contains("WorldLifecycle.IsSameWorld(generation)")
                && finish.Contains("if (committed)") && finish.Contains("NotifyPersistedHistoryChanged();")
                && !finish.Contains("BeginRound(") && !finish.Contains("FinalizeRound("),
                "current request refreshes durable history without creating an artificial exchange");
            string cancel = Section(ui, "public void Interrupt()", "CancelContactModeRefresh();");
            Check(cancel.Contains("cancellation.Cancel();") && cancel.Contains("_summaryWorldGeneration")
                && cancel.Contains("FinishConversationSummary(") && cancel.Contains("return;"),
                "summary interruption owns a separate cancel path and captured world generation");
            string refresh = Section(ui, "bool summaryVisible =", "bool personaVisible =");
            Check(refresh.Contains("!_assistantMode && !_groupMode") && !refresh.Contains("!_remote")
                && refresh.Contains("Navigation.Mode.Explicit") && refresh.Contains("navigation.selectOnLeft"),
                "button hides outside NPC single chat, supports remote chat and has focus navigation");
            string button = Section(ui, "_summarizeButtonGo = NewButton(", "GameObject exportLogs = NewButton(");
            Check(button.Contains("new Vector2(324, 3), new Vector2(448, -3)")
                && button.Contains("_summarizeButton.onClick.AddListener(SummarizeConversation)")
                && !ui.Contains("_taiwuActionButtons.Add(_summarizeButton)")
                && ui.IndexOf("_taiwuActionButtons.Add(writeBookButton)", StringComparison.Ordinal)
                    < ui.IndexOf("_summarizeButtonGo = NewButton(", StringComparison.Ordinal),
                "button follows the gift/teach/write buttons and is not subject to in-person action gating");

            string history = Section(ui, "void RenderHistory()", "GameObject AddDivider(");
            Check(history.Contains("TalkOrchestrator.CreateHistoryPager(_taiwuId, _npcId)")
                && history.Contains("ReadPreviousPage()") && history.Contains("var t = entry.Turn;")
                && history.Contains("AddBubble(BubbleKind.Player, t.Text)")
                && history.Contains("AddBubble(BubbleKind.Npc, t.Text)")
                && history.Contains("AddPersistedExecutionRows(t.Actions, t.ToolResults)")
                && history.Contains("AddPersistedRoundImage(cur, t.ImageFileName)"),
                "chat view replays archived and live original messages, actions and existing images after summary or reopen");
            Check(history.Contains("if (!string.IsNullOrWhiteSpace(entry.Summary))")
                && history.Contains("【对话总结 · 累计梗概】") && !history.Contains("TryGetCompleteHistory"),
                "summary comes from the paged boundary, not a complete transcript rebuild");
            Check(history.Contains("host.StartCoroutine(FinishInitialHistoryLayout("),
                "summary completion scrolls again after the rebuilt layout settles");
            string clear = Section(ui, "void ClearLog()", "void ScrollDown()");
            int detach = clear.IndexOf("child.SetParent(null, false)", StringComparison.Ordinal);
            Check(detach >= 0 && detach < clear.IndexOf("Object.Destroy(child.gameObject)", StringComparison.Ordinal),
                "same-frame rebuild excludes old pending-destroy rows from scroll height");
            Check(history.Contains("entry.Notice") && ReadRepoFile("src/JianghuYouling.Core/Persistence/ConversationHistoryPager.cs")
                .Contains("归档记录暂时读取失败"),
                "archive read failure is visible and does not hide the remaining live history");
            string liveHistory = Section(talk, "public static IReadOnlyList<TalkTurn> History(", "internal static bool AreTurnsLive(");
            Check(liveHistory.Contains("GetConv(taiwuId, npcId).Turns")
                && !liveHistory.Contains("TryGetCompleteHistory") && !liveHistory.Contains("RecoverTurns"),
                "display restoration does not expand model/live context or feed already compressed originals back to the model");
            string bar = Section(ui, "void AddRoundBar(Round r)", "bool LooksLikeError(");
            Check(bar.Contains("bool archived = IsArchivedRound(r);")
                && bar.Contains("if (!archived && !r.isAsst")
                && bar.Contains("if (!archived && !r.proactive") && bar.Contains("if (!archived)")
                && bar.Contains("GenRoundVoice(r)"),
                "archived messages retain playback without offering unsupported delete/retry/image writes");
            Check(Section(ui, "bool DeleteRound(Round r)", "void RetryRound(Round r)")
                    .Contains("if (IsArchivedRound(r))")
                && Section(ui, "void GenRoundImage(Round r)", "void BeginRoundImage(Round r,")
                    .Contains("if (IsArchivedRound(r))"),
                "old visible buttons cannot mutate a round compressed in the background after rendering");
        }

        private static string ReadRepoFile(string relative)
        {
            foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
                for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
                {
                    string path = Path.Combine(directory.FullName, relative);
                    if (File.Exists(path)) return File.ReadAllText(path, Encoding.UTF8);
                }
            throw new FileNotFoundException(relative);
        }

        private static string Section(string source, string start, string end)
        {
            int from = source.IndexOf(start, StringComparison.Ordinal);
            int to = from < 0 ? -1 : source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
            Check(from >= 0 && to > from, "source section: " + start);
            return source.Substring(from, to - from);
        }

        private static void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Manual summary: " + label);
        }
    }
}
