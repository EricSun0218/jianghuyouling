using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JianghuYouling.Core.Persistence;
using JianghuYouling.Core.Prompt;

namespace JianghuYouling.DevTest
{
    internal static class ConversationHistoryPagingTests
    {
        internal static void Run()
        {
            TestLazyPagesAndSummary();
            TestRoundBoundariesAndFiltering();
            TestArchiveCursorAndFailure();
            TestFrontendContracts();
            Console.WriteLine("[PASS] 单聊分页：20条/完整轮次/延迟归档/跨页总结/去重/完整导出运行测试；滚动锚点与生命周期源码契约（非游戏内 E2E）");
        }

        static void TestLazyPagesAndSummary()
        {
            var live = Pairs("live", 25);
            var archived = Pairs("archive", 35);
            int opened = 0;
            var pager = new ConversationHistoryPager(live, "累计梗概", () =>
            {
                opened++;
                // Include a prefix copy left by compaction rollback / an archive-before-save crash.
                return archived.Concat(live.Take(2)).Reverse().GetEnumerator();
            });
            var newest = pager.ReadPreviousPage();
            Check(newest.Count == 20 && opened == 0
                && newest.Select(e => e.Turn.Id).SequenceEqual(live.Skip(30).Select(t => t.Id)),
                "opening renders only the latest 20 messages without touching cold storage");
            var second = pager.ReadPreviousPage();
            Check(second.Count == 20 && opened == 0, "second live page still does not open the archive");
            var chronological = new List<ConversationHistoryPager.Entry>();
            chronological.AddRange(second);
            chronological.AddRange(newest);
            int pages = 2;
            while (pager.HasMore)
            {
                var page = pager.ReadPreviousPage();
                Check(page.Count(e => e.Turn != null) <= 20, "normal paired pages contain at most 20 visible turns");
                chronological.InsertRange(0, page);
                Check(++pages < 20, "cursor terminates");
            }
            Check(opened == 1, "cold archive opens once per view");
            Check(chronological.Where(e => e.Turn != null).Select(e => e.Turn.Id)
                .SequenceEqual(archived.Concat(live).Select(t => t.Id)), "all pages have no gaps, duplicates or reordered turns");
            Check(chronological.Count(e => e.Summary == "累计梗概") == 1
                && chronological.FindIndex(e => e.Summary == "累计梗概") == archived.Count,
                "summary appears exactly once at the archived/live boundary");
            Check(pager.ReadPreviousPage().Count == 0, "exhausted cursors remain empty");

            opened = 0;
            pager = new ConversationHistoryPager(Pairs("exact", 10), "边界", () =>
            {
                opened++;
                return Enumerable.Empty<TalkTurn>().GetEnumerator();
            });
            Check(pager.ReadPreviousPage().Count == 20 && opened == 0,
                "an exactly-full live page does not pre-open cold storage");
            Check(pager.ReadPreviousPage().Single().Summary == "边界" && !pager.HasMore,
                "summary-only final page is returned without a phantom message");
            pager.Dispose();
            Check(!pager.HasMore, "disposed cursor cannot load stale pages");
            var repeated = Pairs("duplicate", 1);
            repeated.Add(Turn(repeated[0].Id, true, "重复副本"));
            pager = new ConversationHistoryPager(repeated, null, null);
            Check(pager.ReadPreviousPage().Count(e => e.Turn != null) == 2,
                "duplicate live IDs use the same first-copy rule as complete history");
            int visited = 0;
            IEnumerable<TalkTurn> LargeArchive()
            {
                for (int i = 99999; i >= 0; i--)
                {
                    visited++;
                    yield return Turn("large" + i, false, "历史消息");
                }
            }
            pager = new ConversationHistoryPager(null, "十万条历史", () => LargeArchive().GetEnumerator());
            Check(pager.ReadPreviousPage().Count(e => e.Turn != null) == 20 && visited == 21,
                "a large archive reads only one page plus boundary lookahead, not every message");
            pager.Dispose();
        }

        static void TestRoundBoundariesAndFiltering()
        {
            var turns = Pairs("pair", 2);
            for (int i = 0; i < 19; i++) turns.Add(Turn("proactive" + i, false, "主动来信"));
            var pager = new ConversationHistoryPager(turns, null, null);
            var page = pager.ReadPreviousPage();
            Check(page.Count == 21 && page[0].Turn.Id == "pair1p" && page[1].Turn.Id == "pair1n",
                "a player/reply pair crossing the 20-message cut is kept whole");
            turns[2].ExchangeId = turns[3].ExchangeId = null;
            pager = new ConversationHistoryPager(turns, null, null);
            Check(pager.ReadPreviousPage().Count == 21, "id-less player/reply pairing is also kept whole");

            var native = Enumerable.Range(0, 3).Select(i => new TalkTurn
            {
                Id = "native" + i, ExchangeId = "native-exchange", Date = 13,
                Kind = TalkTurnKinds.NativePlayerChoice, FromPlayer = true, Text = "原生选择"
            }).Concat(turns.Skip(4)).ToList();
            pager = new ConversationHistoryPager(native, null, null);
            Check(pager.ReadPreviousPage().Count(e => e.Turn != null) == 22,
                "multi-message native interaction is not split at the page boundary");

            var filtered = Pairs("visible", 11);
            filtered.Add(null);
            filtered.Add(Turn("empty", false, " "));
            filtered.Add(new TalkTurn { Id = "group", Kind = TalkTurnKinds.GroupChatTranscript, Text = "群聊不能混入单聊" });
            filtered.Add(new TalkTurn { Id = "action", Actions = new List<string> { "赠礼" } });
            pager = new ConversationHistoryPager(filtered, null, null);
            page = pager.ReadPreviousPage();
            Check(page.Count == 21 && page.Last().Turn.Id == "action"
                && page.All(e => e.Turn != null && e.Turn.Id != "group" && e.Turn.Id != "empty"),
                "hidden group/blank rows do not consume the page; action-only turns stay visible");
        }

        static void TestArchiveCursorAndFailure()
        {
            string directory = Path.Combine(Path.GetTempPath(), "JianghuYouling.PagingTest." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var turns = Pairs("cold", 16);
                turns[1].ImageFileName = "test.png";
                turns[1].Actions = new List<string> { "赠礼" };
                turns[1].ToolResults = new List<string> { "已收到" };
                var archive = ConversationColdArchiveStore.Load(directory, 81, 1001, 2002);
                string hash = ConversationColdArchiveStore.ComputeSourceHash(null, null, turns);
                Check(archive.Append(hash, null, null, "累计", turns), "cold source is durably saved");
                var cursor = archive.ReadTurnsNewestFirst(hash).GetEnumerator();
                Check(cursor.MoveNext() && cursor.Current.Id == turns.Last().Id, "archive cursor starts with newest turn");
                cursor.Current.Text = "仅修改读取副本";
                cursor.Dispose();
                var recovered = archive.RecoverTurns();
                Check(recovered.Last().Text == turns.Last().Text && recovered[1].Actions[0] == "赠礼"
                    && recovered[1].ToolResults[0] == "已收到" && recovered[1].ImageFileName == "test.png",
                    "cursor returns detached turns without damaging original text, receipts or image references");
                Check(archive.ReadTurnsNewestFirst(hash).Reverse().Select(t => t.Id)
                    .SequenceEqual(recovered.Select(t => t.Id)), "paged archive and complete export contain the same ordered transcript");
                var pager = new ConversationHistoryPager(null, "累计", () => archive.ReadTurnsNewestFirst(hash).GetEnumerator());
                var page = pager.ReadPreviousPage();
                Check(page.Count(e => e.Turn != null) == 20 && page.Last().Summary == "累计",
                    "manual summary of all turns opens at the latest archived page with summary at bottom");
                var later = Pairs("later", 3);
                string nextHash = ConversationColdArchiveStore.ComputeSourceHash("累计", hash, later);
                Check(archive.Append(nextHash, "累计", hash, "后来总结", later), "append a later background summary");
                Check(archive.ReadTurnsNewestFirst(hash).Reverse().Select(t => t.Id)
                    .SequenceEqual(turns.Select(t => t.Id)),
                    "a deferred old view excludes newer archive segments appended after its snapshot");
                Check(archive.ReadTurnsNewestFirst(nextHash).Count() == turns.Count + later.Count
                    && !archive.ReadTurnsNewestFirst(null).Any(),
                    "a new view sees the new committed prefix; a pre-summary view has no cold prefix");
                var missing = new ConversationHistoryPager(null, "累计",
                    () => archive.ReadTurnsNewestFirst(new string('a', 64)).GetEnumerator());
                Check(missing.ReadPreviousPage().Any(e => e.Notice != null),
                    "a missing committed archive boundary reports failure instead of displaying unrelated history");
            }
            finally { Directory.Delete(directory, true); }

            var failed = new ConversationHistoryPager(Pairs("safe", 1), "已存梗概",
                () => throw new IOException("unreadable"));
            var failurePage = failed.ReadPreviousPage();
            Check(failurePage.Count(e => e.Notice != null) == 1
                && failurePage.Count(e => e.Turn != null) == 2 && !failed.HasMore,
                "archive failure is visible, terminates loading, and preserves live records and summary");
        }

        static void TestFrontendContracts()
        {
            string ui = ReadRepoFile("src/JianghuYouling.Frontend/UI/ChatWindow.cs");
            Check(ui.Contains("_scroll.onValueChanged.AddListener(OnHistoryScrollChanged)")
                && ui.Contains("scrollGo.AddComponent<ChatHistoryScrollListener>().OnScrollUp = RequestOlderHistory"),
                "both dragging and an upward wheel gesture at an already-clamped top can request a page");
            Check(ui.Contains("_groupMode || _assistantMode || _historyLoading || _historyPager == null")
                && ui.Contains("_content.anchoredPosition.y > 48f"),
                "loading is single-chat-only, non-reentrant, and near the top");
            Check(ui.Contains("oldY + _content.rect.height - oldHeight")
                && ui.Contains("version == _historyViewVersion && scrollVersion == _historyScrollVersion"),
                "prepend compensates height and stale layout callbacks cannot override a newer scroll");
            Check(ui.Contains("newRows[i].SetSiblingIndex(i)")
                && ui.Contains("_curStartChild += newRows.Count - removed")
                && ui.Contains("_rounds.InsertRange(0, newRounds)"),
                "prepended rows keep chronological round order without stealing a streaming round's rows");
            int renderStart = ui.IndexOf("void RenderHistory()", StringComparison.Ordinal);
            int renderEnd = ui.IndexOf("GameObject AddDivider(", renderStart, StringComparison.Ordinal);
            string render = ui.Substring(renderStart, renderEnd - renderStart);
            Check(!render.Contains("ConversationHistoryLayout") && !render.Contains("TryGetCompleteHistory("),
                "opening single chat no longer uses a full transcript UI snapshot");
            Check(render.Contains("Task.Run(() => pager.ReadPreviousPage())")
                && render.Contains("pending.ContinueWith(task =>")
                && render.Contains("version != _historyViewVersion || _content == null"),
                "archive parsing is off the Unity thread and reset views cannot apply stale task results");
            string orchestrator = ReadRepoFile("src/JianghuYouling.Frontend/Talk/TalkOrchestrator.cs");
            Check(orchestrator.Contains("LoadReadOnly(directory, worldId, taiwuId, npcId)")
                && orchestrator.Contains("return archive.ReadTurnsNewestFirst(sourceHash).GetEnumerator()")
                && orchestrator.Contains("string sourceHash = conversation.SummarySourceHash;")
                && orchestrator.Contains("if (!sourceReliable) throw new IOException("),
                "deferred storage reads use captured world identity and the bounded clone cursor");
        }

        static List<TalkTurn> Pairs(string prefix, int count)
        {
            var result = new List<TalkTurn>();
            for (int i = 0; i < count; i++)
            {
                result.Add(Turn(prefix + i + "p", true, "问", prefix + i));
                result.Add(Turn(prefix + i + "n", false, "答", prefix + i));
            }
            return result;
        }

        static TalkTurn Turn(string id, bool player, string text, string exchange = null)
            => new TalkTurn { Id = id, ExchangeId = exchange ?? id, FromPlayer = player, Text = text, Date = 13 };

        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Conversation history paging: " + message);
        }

        static string ReadRepoFile(string path)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, path);
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
            }
            throw new FileNotFoundException(path);
        }
    }
}
