using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JianghuYouling.Core.Behavior;
using JianghuYouling.Core.Memory;
using JianghuYouling.Core.Persistence;
using JianghuYouling.Core.Tools;

namespace JianghuYouling.DevTest
{
    internal static class MonthlyOneShotTests
    {
        internal static void Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "JHYL_MonthlyOneShot_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                SaveBarrierWaitsWithoutConsumingWork();
                RumorSurvivesFullMemory(root);
                FailedMemoryIsReported(root);
                QueueDoesNotResume();
                StoryHistoryDoesNotResume(root);
                ReceiptCleanupNeverReplays(root);
                RecordWarningsDoNotCancelWork();
                InterruptedResultsKeepReasonsAndReceipts(root);
                SourceWiring();
            }
            finally { Directory.Delete(root, true); }
        }

        private static void RecordWarningsDoNotCancelWork()
        {
            uint previousWorld = JianghuYoulingPaths.CurrentWorldId;
            try
            {
                JianghuYoulingPaths.CurrentWorldId = 83;
                MonthlyChronicleProgressStore.Publish(180, 1, true, false, true, false,
                    2, WorldLifecycle.Generation, 83);
                MonthlyChronicleProgressStore.WarnIncompleteRecords(1, 180, "记录未完整保存");
                MonthlyChronicleProgressStore.Publish(180, 1, true, true, true, false,
                    1, WorldLifecycle.Generation, 83);
                Check(MonthlyChronicleProgressStore.TryGet(1, 180, out MonthlyChronicleProgress progress)
                    && progress.IsGenerating && progress.RecordWarning == "记录未完整保存"
                    && string.IsNullOrWhiteSpace(progress.StopReason),
                    "record failure stays visible without canceling remaining work or labeling it stopped");
            }
            finally
            {
                MonthlyChronicleProgressStore.ResetForWorldExit();
                JianghuYoulingPaths.CurrentWorldId = previousWorld;
            }
        }

        private static void InterruptedResultsKeepReasonsAndReceipts(string root)
        {
            string previousRoot = JianghuYoulingPaths.Root;
            uint previousWorld = JianghuYoulingPaths.CurrentWorldId;
            try
            {
                JianghuYoulingPaths.Root = Path.Combine(root, "interrupted");
                JianghuYoulingPaths.CurrentWorldId = 84;
                var reasons = new HashSet<string>();
                foreach (string code in new[] { "location_changed", "location_unavailable", "travel",
                    "native_month_advance", "conversation_cleared", "timeout", "operation_receipt_unknown", "child_exception" })
                    Check(reasons.Add(MonthlyInterruption.Describe(code)), "distinct interruption reason: " + code);

                var state = new CompanionMonthlyStopState();
                state.Stop("native_month_advance");
                state.Stop("completed");
                state.Stop("timeout");
                Check(state.Code == "native_month_advance", "first cancellation reason survives cleanup and late failures");
                var freshState = new CompanionMonthlyStopState();
                freshState.Stop("completed_with_interruptions");
                Check(freshState.Code == null, "new run and normal cleanup do not inherit an earlier cancellation");

                var empty = new CompanionMonthlyResult { NpcId = 4, Name = "fixture" };
                MonthlyInterruption.Stop(empty, MonthlyInterruption.Describe("location_changed"));
                Check(empty.Outcomes.Count == 0 && empty.ActionTools.Count == 0
                    && empty.Summary.Contains("尚无已确认"), "movement before execution stays an interrupted record, not a fabricated action");
                var partial = new CompanionMonthlyResult { NpcId = 5, Name = "partial fixture" };
                partial.Outcomes.Add("成功:写书");
                partial.ActionTools.Add("write_book");
                MonthlyInterruption.Stop(partial, MonthlyInterruption.Describe("native_month_advance"));
                Check(partial.Outcomes.Single() == "成功:写书" && partial.ActionTools.Single() == "write_book",
                    "cancellation retains confirmed receipts and action categories");
                Check(EventLogStore.UpsertMonthlyDigest(1, 190, new[] { empty, partial }), "interrupted companion records archive");
                string failure = MonthlyInterruption.ModelFailure("HTTP 传输异常:WebException", 180378, 180);
                Check(failure.Contains("180 秒") && failure.Contains("WebException"), "model failure shows wait duration and transport category");
                Check(!MonthlyInterruption.ModelFailure("private provider body and credential", 1, 180).Contains("credential"),
                    "private provider response is not copied into the user-facing reason");
                Check(EventLogStore.Upsert(1, new EventLogEntry
                {
                    EventId = "monthly-guaranteed:84:1:190", Date = 190,
                    Brief = "仅保留本地兜底见闻", GenerationFailureReason = failure,
                }), "event failure merges with companion-first archive");
                EventLogEntry reloaded = EventLogStore.Load(1).Single();
                Check(reloaded.GenerationFailureReason == failure && string.IsNullOrWhiteSpace(reloaded.StopReason),
                    "event-only failure does not claim the entire month was canceled");
                Check(reloaded.CompanionActions.Count == 2
                    && reloaded.CompanionActions[0].StopReason.Contains("另一地块")
                    && reloaded.CompanionActions[1].StopReason.Contains("下一次过月")
                    && reloaded.CompanionActions[1].Outcomes.Single() == "成功:写书",
                    "reload and event merge preserve per-person reasons and partial receipts");
                Check(EventLogStore.TryUpdateCompanionDetail(1, 190, 5, "confirmed facts only")
                    && EventLogStore.Load(1).Single().CompanionActions[1].StopReason == partial.StopReason,
                    "on-demand detail generation cannot erase the interruption reason");
                Check(EventLogStore.UpsertMonthlyDigest(1, 190, new[] { empty, partial })
                    && EventLogStore.Load(1).Single().GenerationFailureReason == failure,
                    "a later companion completion cannot erase the event-only failure");
            }
            finally
            {
                JianghuYoulingPaths.Root = previousRoot;
                JianghuYoulingPaths.CurrentWorldId = previousWorld;
            }
        }

        internal static void RunStoryHistory()
        {
            string root = Path.Combine(Path.GetTempPath(), "JHYL_StoryHistory_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { StoryHistoryDoesNotResume(root); }
            finally { Directory.Delete(root, true); }
        }

        private static void RumorSurvivesFullMemory(string root)
        {
            var memory = NpcMemoryStore.Load(root, "1", "2");
            for (int i = 0; i < 2000; i++)
                memory.Add(new MemoryEntry
                {
                    Content = "retained fact " + i, Importance = 9, WorldDate = 175,
                    SourceKind = "fixture", SourceId = "fact-" + i,
                });
            Check(memory.Save(), "seed full memory");
            var rumor = new MemoryEntry
            {
                Content = "past event receipt", Importance = 3, Type = MemoryType.Event,
                WorldDate = 168, SourceKind = EventFanoutMemoryWriter.SourceKind,
                SourceId = "event-168:2",
            };
            Check(EventFanoutMemoryWriter.TryCommit(root, 1, 2, rumor, out string failure),
                "full memory must not evict the rumor being committed: " + failure);
            var restored = NpcMemoryStore.Load(root, "1", "2");
            Check(restored.LoadReliable && restored.Count == 2000,
                "rumor commit retains the bounded memory capacity");
            Check(restored.All.Count(e => e.SourceId == rumor.SourceId) == 1,
                "rumor survives a reload exactly once");
            Check(!restored.All.Single(e => e.SourceId == rumor.SourceId).IsCore,
                "temporary retention does not promote a rumor to permanent core memory");
            string path = Path.Combine(root, "Memory_1_2.json");
            string before = File.ReadAllText(path);
            Check(EventFanoutMemoryWriter.TryCommit(root, 1, 2, rumor, out failure),
                "retry after memory commit before journal commit succeeds");
            Check(File.ReadAllText(path) == before, "retry does not rewrite or prune existing memory");
        }

        private static void SaveBarrierWaitsWithoutConsumingWork()
        {
            bool current = true, saving = true, advancing = false;
            int notices = 0, work = 0;
            var wait = MonthlyNativeSaveBarrier.Wait(() => current, () => saving,
                () => advancing, () => notices++);
            bool remainedPending = true;
            for (int frame = 0; frame < 900; frame++)
                if (!wait.MoveNext()) { remainedPending = false; work++; break; }
            Check(remainedPending && work == 0 && notices == 1,
                "a long native save neither dispatches work nor burns an RPC/retry budget");
            saving = false;
            Check(!wait.MoveNext(), "save-end releases the pending month");

            saving = false;
            wait = MonthlyNativeSaveBarrier.Wait(() => current, () => saving, () => advancing);
            Check(wait.MoveNext(), "month-state notification yields before dispatch");
            saving = true;
            Check(wait.MoveNext(), "save-start later in the same notification batch is observed");
            current = false;
            Check(!wait.MoveNext(), "world change exits save wait without starting old work");

            current = true;
            wait = MonthlyNativeSaveBarrier.Wait(() => current, () => saving, () => advancing);
            wait.MoveNext();
            advancing = true;
            Check(!wait.MoveNext(), "a new native month exits the wait for cancellation");
            advancing = false;
            saving = false;
            wait = MonthlyNativeSaveBarrier.Wait(() => current, () => saving, () => advancing);
            Check(wait.MoveNext() && !wait.MoveNext(), "no-save months proceed after one frame");
        }

        private static void FailedMemoryIsReported(string root)
        {
            string path = Path.Combine(root, "Memory_1_3.json");
            const string damaged = "{ incomplete synthetic test fixture";
            File.WriteAllText(path, damaged);
            var rumor = new MemoryEntry
            {
                Content = "receipt", Importance = 3, Type = MemoryType.Event, WorldDate = 168,
                SourceKind = EventFanoutMemoryWriter.SourceKind, SourceId = "event-168:3",
            };
            Check(!EventFanoutMemoryWriter.TryCommit(root, 1, 3, rumor, out string failure)
                && failure == "memory_unreadable", "unreadable memory is not reported as committed");
            Check(File.ReadAllText(path) == damaged, "unreadable memory is not overwritten with an empty store");

            // Only this generated fixture is repaired. Production never resets unreadable data.
            File.WriteAllText(path, "[]");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(!EventFanoutMemoryWriter.TryCommit(root, 1, 3, rumor, out failure),
                    "locked memory returns an explicit failure");
            Check(EventFanoutMemoryWriter.TryCommit(root, 1, 3, rumor, out failure),
                "an explicitly requested write can succeed once a lock is released");
            Check(NpcMemoryStore.Load(root, "1", "3").All.Count(e => e.SourceId == rumor.SourceId) == 1,
                "failed attempts do not duplicate the recovered rumor");
        }

        internal static void QueueDoesNotResume()
        {
            var queue = new MonthlyRunQueue();
            queue.Reset(12);
            Check(!queue.Claim(1, 12) && !queue.TryStart(out _, out _),
                "world load establishes a baseline without generating the loaded month");
            Check(queue.Claim(1, 13) && !queue.Claim(1, 13), "duplicate month notifications are consumed once");
            Check(queue.TryStart(out int taiwu, out int date) && taiwu == 1 && date == 13,
                "claimed month starts once");
            Check(queue.Claim(1, 14) && !queue.TryStart(out _, out _), "new month waits only for current callbacks");
            Check(queue.Claim(1, 15), "latest stable month replaces an obsolete waiting month");
            queue.Finish(13);
            Check(queue.TryStart(out _, out date) && date == 15, "intermediate cancelled months are never replayed");
            queue.Finish(13);
            Check(queue.RunningDate == 15, "late completion cannot release a newer month");
            queue.Finish(15);
            Check(!queue.Claim(1, 15), "empty, cancelled and record-failed results never reopen a consumed month");
            Check(queue.Claim(1, 16) && queue.TryStart(out _, out _), "a later month proceeds without a completion write");
            queue.Reset(16);
            Check(!queue.TryStart(out _, out _) && !queue.Claim(1, 16), "restart abandons all pending work");
            queue.Reset(10);
            Check(queue.Claim(1, 11), "loading an earlier native save establishes its own timeline");
        }

        private static void StoryHistoryDoesNotResume(string root)
        {
            string oldRoot = JianghuYoulingPaths.Root;
            uint oldWorld = JianghuYoulingPaths.CurrentWorldId;
            try
            {
                JianghuYoulingPaths.Root = Path.Combine(root, "history");
                JianghuYoulingPaths.CurrentWorldId = 71;
                var saga = EventSagaStore.Load(1);
                var outcome = new EventSagaStepOutcome
                {
                    OperationId = JianghuYouling.Core.Tools.OperationId.FromStableKey("observed-unknown"),
                    StepId = "m1-s1", ToolName = "event_gift", Status = "unknown", Retryable = true,
                    ActorId = 2, TargetId = 3, ActorName = "actor", TargetName = "target",
                    Summary = "unknown receipt", WorldDate = 168,
                };
                saga.OutcomeJournal.Add(outcome);
                saga.UnconfirmedOperationIds.Add(outcome.OperationId);
                Check(EventSagaStore.Save(1, saga), "observed facts can be saved without a story or fanout");
                var reloaded = EventSagaStore.Load(1);
                Check(reloaded.LoadReliable && reloaded.OutcomeJournal.Count == 1
                    && reloaded.OutcomeJournal[0].Status == "unknown", "unknown stays unknown in history");
                Check(reloaded.UnconfirmedOperationIds.Count == 0,
                    "history load does not recreate an execution or delivery queue");
                string path = Path.Combine(JianghuYoulingPaths.Events, "saga_story_1.json");
                string json = File.ReadAllText(path);
                Check(!json.Contains("UnconfirmedOperationIds") && !json.Contains("DispatchEnvelope")
                    && !json.Contains("FanoutJournal"), "persisted story history contains no replay envelope or repair queue");
                Check(EventSagaStore.Clear(1) && !EventSagaStore.Load(1).Active,
                    "an unknown previous action does not prevent closing the old story");

                var delivery = new MonthlyEventDelivery
                {
                    EventId = "event-168", WorldDate = 168, AreaName = "fixture area",
                    StoryText = "observed fact", ProjectionText = "observed projection",
                    Actions = new List<string> { "confirmed action" }, Heard = 1,
                };
                Check(EventLogStore.SaveMonthlyEvent(1, delivery), "partial memory delivery still archives known facts");
                var entry = EventLogStore.Load(1).Single();
                Check(entry.Heard == 1 && entry.Actions.Count == 1, "archive counts only successful memory writes");
                var companion = new CompanionMonthlyResult { NpcId = 4, Name = "fixture", Summary = "confirmed" };
                companion.Outcomes.Add("成功:赠物");
                companion.ActionTools.Add("gift_item");
                Check(EventLogStore.UpsertMonthlyDigest(1, 168, new[] { companion }),
                    "companion facts and action history can share the month archive");
                entry = EventLogStore.Load(1).Single();
                Check(entry.CompanionActions.Single().ActionTools.Single() == "gift_item",
                    "novelty history no longer needs an execution journal");

                string obsolete = Path.Combine(JianghuYoulingPaths.Events, "saga_2.json");
                File.WriteAllText(obsolete, "obsolete unfinished task fixture");
                Check(EventSagaStore.Load(2).LoadReliable && !EventSagaStore.Load(2).Active
                    && File.ReadAllText(obsolete) == "obsolete unfinished task fixture",
                    "obsolete task files are neither resumed nor deleted");

                string damaged = Path.Combine(JianghuYoulingPaths.Events, "saga_story_3.json");
                File.WriteAllText(damaged, "{ broken history");
                var unavailable = EventSagaStore.Load(3);
                Check(!unavailable.LoadReliable && unavailable.UnconfirmedOperationIds.Count == 0,
                    "unreadable history exposes no pending execution");
                Check(!EventSagaStore.Save(3, unavailable) && File.ReadAllText(damaged) == "{ broken history",
                    "history failure preserves the user's existing bytes");
                var foreign = new EventSaga { WorldId = 72, TaiwuId = 4 };
                Check(!EventSagaStore.Save(4, foreign), "history writes remain world-bound");
            }
            finally
            {
                JianghuYoulingPaths.Root = oldRoot;
                JianghuYoulingPaths.CurrentWorldId = oldWorld;
            }
        }

        private static void ReceiptCleanupNeverReplays(string root)
        {
            string previousRoot = JianghuYoulingPaths.Root;
            uint previousWorld = JianghuYoulingPaths.CurrentWorldId;
            int previousGeneration = WorldLifecycle.Generation;
            var previousHost = TalkEntryHost.Instance;
            var previousStarted = UnityEngine.MonoBehaviour.CoroutineStarted;
            try
            {
                JianghuYoulingPaths.Root = Path.Combine(root, "cleanup");
                JianghuYoulingPaths.CurrentWorldId = 81;
                WorldLifecycle.Generation++;
                TalkEntryHost.Instance = new TalkEntryHost();
                UnityEngine.MonoBehaviour.CoroutineStarted = routine =>
                {
                    int frames = 0;
                    while (routine.MoveNext())
                        if (++frames > 20) throw new InvalidOperationException("Unexpected cleanup wait");
                };
                string op = OperationId.FromStableKey("cleanup-only");
                Func<bool> current = () => true;
                MonthlyOperationReceipts.Register(op, current);
                Check(ReferenceEquals(MonthlyOperationReceipts.Take(op), current)
                    && MonthlyOperationReceipts.Take(op) == null, "monthly scope belongs only to its own dispatch");
                Check(MonthlyOperationReceipts.Remember(81, 100, op), "cleanup tracks only world, Taiwu and operation id");
                string path = Path.Combine(Path.GetDirectoryName(JianghuYoulingPaths.OperationAckOutboxFor(81)),
                    "monthly_receipt_cleanup.json");
                int queries = 0, acknowledgements = 0;
                ToolOutcome receipt = ToolOutcome.Unknown(op, "pending");
                JianghuYouling.Effects.EffectHandler.QueryOperationForTest = (world, taiwu, id, done) =>
                {
                    Check(world == 81 && taiwu == 100 && id == op, "cleanup preserves the original identity");
                    queries++;
                    done(receipt);
                };
                bool acknowledgeSaved = false;
                JianghuYouling.Rpc.OperationRpcClient.AcknowledgeForTest = (world, taiwu, id) =>
                {
                    acknowledgements++;
                    return acknowledgeSaved;
                };
                MonthlyOperationReceipts.CleanUp();
                Check(queries == 1 && acknowledgements == 0
                    && OperationAckOutboxStore.Load(path, 81).Entries.Count == 1,
                    "unknown receipt is only queried, never acknowledged or replayed");
                receipt = new ToolOutcome { OperationId = op, Status = "succeeded", Receipt = "backend receipt" };
                MonthlyOperationReceipts.CleanUp();
                Check(acknowledgements == 1 && OperationAckOutboxStore.Load(path, 81).Entries.Count == 1,
                    "failed ACK handoff retains cleanup responsibility without replaying actions");
                acknowledgeSaved = true;
                MonthlyOperationReceipts.CleanUp();
                Check(acknowledgements == 2 && OperationAckOutboxStore.Load(path, 81).Entries.Count == 0,
                    "terminal cleanup transfers to the existing ACK outbox independently of text records");
                Check(MonthlyOperationReceipts.Remember(81, 100, op), "seed a late callback");
                JianghuYouling.Effects.EffectHandler.QueryOperationForTest = (world, taiwu, id, done) =>
                {
                    WorldLifecycle.Generation++;
                    JianghuYoulingPaths.CurrentWorldId = 82;
                    done(receipt);
                };
                MonthlyOperationReceipts.CleanUp();
                Check(acknowledgements == 2 && OperationAckOutboxStore.Load(path, 81).Entries.Count == 1,
                    "a world switch cannot acknowledge or clear the previous world's late result");
            }
            finally
            {
                JianghuYouling.Effects.EffectHandler.QueryOperationForTest = null;
                JianghuYouling.Rpc.OperationRpcClient.AcknowledgeForTest = null;
                UnityEngine.MonoBehaviour.CoroutineStarted = previousStarted;
                TalkEntryHost.Instance = previousHost;
                WorldLifecycle.Generation = previousGeneration;
                JianghuYoulingPaths.CurrentWorldId = previousWorld;
                JianghuYoulingPaths.Root = previousRoot;
            }
        }

        private static void SourceWiring()
        {
            DirectoryInfo repo = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (repo != null && !Directory.Exists(Path.Combine(repo.FullName, "src", "JianghuYouling.Frontend")))
                repo = repo.Parent;
            if (repo == null) throw new InvalidOperationException("Cannot find repository for monthly source contracts");
            string game = Path.Combine(repo.FullName, "src", "JianghuYouling.Frontend", "Game");
            string settlement = File.ReadAllText(Path.Combine(game, "MonthlySettlement.cs"));
            Check(settlement.Contains("GEvent.Add(EEvents.OnSavingWorldStateChange, _handler)")
                && settlement.Contains("GEvent.Remove(EEvents.OnSavingWorldStateChange, _handler)"),
                "monthly scheduler subscribes and unsubscribes save-end wakeups");
            int saveGuard = settlement.IndexOf("if (bgd.SavingWorld) return;", StringComparison.Ordinal);
            int process = settlement.IndexOf("ProcessQueue(taiwuId, date);", StringComparison.Ordinal);
            Check(saveGuard >= 0 && process > saveGuard
                && settlement.Contains("IsAdvancingMonthForMonthlyWork() || IsSavingWorldForMonthlyWork()"),
                "native saving defers both month handling and queued work");
            foreach (string file in new[] { "MonthlyEventGenerator.cs", "CompanionMonthlyActions.cs" })
            {
                string source = File.ReadAllText(Path.Combine(game, file));
                int wait = source.IndexOf("yield return MonthlySettlement.WaitForNativeSave", StringComparison.Ordinal);
                int budget = source.IndexOf("MonthlyAgentRequestBudget.Begin", StringComparison.Ordinal);
                Check(wait >= 0 && budget > wait, file + " waits before starting budget and queries");
            }
            string monthly = File.ReadAllText(Path.Combine(game, "MonthlyEventGenerator.cs"));
            string companion = File.ReadAllText(Path.Combine(game, "CompanionMonthlyActions.cs"));
            string history = File.ReadAllText(Path.Combine(game, "..", "UI", "HistoryWindow.cs"));
            Check(companion.Contains("JHYL_COMPANION_INTERRUPTED") && companion.Contains("MonthlyInterruption.Stop(result")
                && companion.Contains("completed_with_interruptions"), "invalidated companion waits return a visible stopped result");
            Check(settlement.IndexOf("EventLogStore.UpsertMonthlyDigest", StringComparison.Ordinal)
                    < settlement.IndexOf("int pending = companionDone", StringComparison.Ordinal),
                "stopped companion results are archived before progress-only early returns");
            Check(history.Contains("Append(companion.StopReason)") && history.Contains("Append(entry.GenerationFailureReason)")
                && history.Contains("有中止项"), "history shows both per-person and event-only reasons without expanding details");
            Check(monthly.Contains("MonthlyInterruption.ModelFailure(error") && monthly.Contains("GenerationFailureReason = _eventFailureReason"),
                "model request failures remain visible through the event fallback archive");
            string cleanup = File.ReadAllText(Path.Combine(game, "MonthlyOperationReceipts.cs"));
            string rpc = File.ReadAllText(Path.Combine(game, "..", "Rpc", "OperationRpcClient.cs"));
            Check(!settlement.Contains("SaveDigestState") && !settlement.Contains("RunDigestWithRetry")
                && settlement.Contains("DigestQueue.Reset(bgd != null ? bgd.CurrDate : -1)")
                && settlement.Contains("DigestQueue.Finish(date)"), "scheduler has no disk-gated recovery or retry loop");
            Check(!companion.Contains("MutationJournal") && !companion.Contains("TryLoadProjectedCompanionBatch")
                && !monthly.Contains("ReconcileSagaPendingOperations")
                && !monthly.Contains("FindIncompleteEventFanout"), "superseded recovery implementations are removed");
            Check(!companion.Contains("MutationInflightByEpoch")
                && companion.Contains("if (_runCancellationEpoch != batchEpoch) return;")
                && companion.Contains("_runCancellationEpoch = 0;"),
                "ended batch receipts never block the new month or release its scheduling slot");
            Check(companion.Contains("WorldLifecycle.Generation + \":\" + batchEpoch")
                && monthly.Contains("argumentsJson) + \"|run=\" + _runningGeneration"),
                "reloading the same world and date cannot collide with late monthly callbacks");
            Check(companion.Contains("记录未完整保存") && monthly.Contains("记录未完整保存")
                && monthly.Contains("if (saved) delivery.Heard++")
                && monthly.Contains("EventLogStore.SaveMonthlyEvent"), "record failures warn without blocking subsequent recipients or archives");
            Check(monthly.Contains("JHYL_MONTHLY_OBSERVED_RESULTS")
                && monthly.Contains("StopReason = unconfirmed ? unknownReason : null")
                && monthly.IndexOf("if (observed.Count > 0)", StringComparison.Ordinal)
                    < monthly.IndexOf("string story = BuildGuaranteedAmbientEvent", StringComparison.Ordinal),
                "an unknown event action remains visible with earlier known results instead of an ambient fallback");
            Check(cleanup.Contains("EffectHandler.QueryOperation") && cleanup.Contains("AcknowledgeExistingReceipt")
                && !cleanup.Contains("ExecuteEventTool") && !cleanup.Contains("DispatchEnvelope"),
                "persisted cleanup may only query and acknowledge receipts");
            Check(rpc.Contains("state.MonthlyCurrent == null && !resent")
                && rpc.Contains("MonthlyOperationReceipts.Remember")
                && rpc.Contains("MonthlyOperationReceipts.Acknowledge"),
                "monthly RPC never resends mutations and cleanup is independent of text writes");
            Check(companion.Contains("HasAuthoritativeSucceededReceipt")
                && companion.Contains("outcome.OperationId != OperationId")
                && monthly.Contains("string.Equals(authoritativeReceipt.OperationId, operationId"),
                "authoritative operation identity still gates success facts");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Monthly one-shot: " + message);
            Console.WriteLine("[PASS] Monthly one-shot: " + message);
        }
    }
}
