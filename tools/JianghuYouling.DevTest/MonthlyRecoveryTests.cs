using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JianghuYouling.Core.Behavior;
using JianghuYouling.Core.Memory;
using JianghuYouling.Core.Persistence;

namespace JianghuYouling.DevTest
{
    internal static class MonthlyRecoveryTests
    {
        internal static void Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "JHYL_MonthlyRecovery_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                SaveBarrierWaitsWithoutConsumingWork();
                RumorSurvivesFullMemory(root);
                FailedMemoryRemainsPending(root);
                ArchiveRecoveryPreservesExistingData(root);
                SourceWiring();
            }
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

        private static void FailedMemoryRemainsPending(string root)
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
                    "locked memory leaves the recipient pending");
            Check(EventFanoutMemoryWriter.TryCommit(root, 1, 3, rumor, out failure),
                "retry succeeds after a transient lock is released");
            Check(NpcMemoryStore.Load(root, "1", "3").All.Count(e => e.SourceId == rumor.SourceId) == 1,
                "failed attempts do not duplicate the recovered rumor");
        }

        private static void ArchiveRecoveryPreservesExistingData(string root)
        {
            string oldRoot = JianghuYoulingPaths.Root;
            uint oldWorld = JianghuYoulingPaths.CurrentWorldId;
            try
            {
                JianghuYoulingPaths.Root = Path.Combine(root, "archive");
                JianghuYoulingPaths.CurrentWorldId = 71;
                var checkpoint = new EventFanoutCheckpoint
                {
                    EventId = "event-168", WorldDate = 168, AreaName = "fixture area",
                    StoryText = "frozen fact", ProjectionText = "frozen projection", Roster = "fixture roster",
                    Actions = new List<string> { "confirmed action" }, RecipientIds = new List<int> { 2, 3 },
                    EventLogCommitted = true,
                };
                Check(EventLogStore.EnsureFanoutEntry(1, checkpoint),
                    "missing archive row is reconstructed even when checkpoint says committed");
                var entry = EventLogStore.Load(1).Single();
                entry.Detail = "generated detail to preserve";
                entry.StopReason = "existing stop reason";
                entry.CompanionActions.Add(new CompanionMonthlyLogEntry { NpcId = 4, Summary = "companion fact" });
                Check(EventLogStore.Upsert(1, entry), "seed archive detail and companion result");
                string path = Path.Combine(JianghuYoulingPaths.Events, "events_1.json");
                string before = File.ReadAllText(path);
                Check(EventLogStore.EnsureFanoutEntry(1, checkpoint) && File.ReadAllText(path) == before,
                    "recovery never replaces existing detail, stop reason or companion results");

                var saga = new EventSaga { WorldId = 71, TaiwuId = 1, StartDate = 168 };
                saga.FanoutJournal.Add(checkpoint);
                Check(EventSagaStore.Save(1, saga), "persist synthetic pending fanout");
                Check(EventFanoutPolicy.MarkRecipientCommitted(checkpoint, 2) && EventSagaStore.Save(1, saga),
                    "persist first recipient checkpoint");
                var recoveredSaga = EventSagaStore.Load(1);
                var recovered = recoveredSaga.FanoutJournal.Single();
                Check(recoveredSaga.LoadReliable && EventFanoutPolicy.PendingRecipients(recovered).SequenceEqual(new[] { 3 }),
                    "restart resumes only the unfinished recipient");
                Check(!EventFanoutPolicy.ReadyForProjection(recovered),
                    "partial fanout cannot project or acknowledge the event");
                Check(EventFanoutPolicy.MarkRecipientCommitted(recovered, 3)
                    && EventSagaStore.Save(1, recoveredSaga), "remaining recipient checkpoint can advance");
                Check(EventLogStore.TryUpdateHeard(1, recovered.EventId, 2), "heard count can finish after archive restoration");
                recovered.Heard = 2;
                recovered.FanoutCompleted = true;
                recovered.HeardCommitted = true;
                Check(EventSagaStore.Save(1, recoveredSaga) && EventFanoutPolicy.ReadyForProjection(recovered),
                    "all durable stages release projection");
                entry = EventLogStore.Load(1).Single();
                Check(entry.Heard == 2 && entry.Detail == "generated detail to preserve"
                    && entry.StopReason == "existing stop reason" && entry.CompanionActions.Count == 1,
                    "heard recovery preserves the rest of the archive");
                before = File.ReadAllText(path);
                Check(EventLogStore.TryUpdateHeard(1, recovered.EventId, 2) && File.ReadAllText(path) == before,
                    "heard replay is idempotent");

                JianghuYoulingPaths.Root = Path.Combine(root, "damaged-archive");
                Directory.CreateDirectory(JianghuYoulingPaths.Events);
                path = Path.Combine(JianghuYoulingPaths.Events, "events_1.json");
                File.WriteAllText(path, "synthetic invalid archive");
                Check(!EventLogStore.EnsureFanoutEntry(1, checkpoint)
                    && File.ReadAllText(path) == "synthetic invalid archive",
                    "archive recovery refuses to overwrite unreadable history");
            }
            finally
            {
                JianghuYoulingPaths.Root = oldRoot;
                JianghuYoulingPaths.CurrentWorldId = oldWorld;
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
                && settlement.Contains("if (IsAdvancingMonthForMonthlyWork() || IsSavingWorldForMonthlyWork()) return;"),
                "saving defers both month handling and queued digest start");
            foreach (string file in new[] { "MonthlyEventGenerator.cs", "CompanionMonthlyActions.cs" })
            {
                string source = File.ReadAllText(Path.Combine(game, file));
                int wait = source.IndexOf("yield return MonthlySettlement.WaitForNativeSave", StringComparison.Ordinal);
                int budget = source.IndexOf("MonthlyAgentRequestBudget.Begin", StringComparison.Ordinal);
                Check(wait >= 0 && budget > wait, file + " waits before starting its request budget and queries");
            }
            string monthly = File.ReadAllText(Path.Combine(game, "MonthlyEventGenerator.cs"));
            Check(monthly.Contains("while ((pendingFanout = FindIncompleteEventFanout(saga)) != null)")
                && monthly.Contains("if (pendingFanout.WorldDate == date)"),
                "successful older fanout recovery continues the current month's event");
            Check(monthly.Contains("EventFanoutMemoryWriter.TryCommit")
                && monthly.Contains("EventLogStore.EnsureFanoutEntry")
                && monthly.Contains("[JHYL_EVENT_FANOUT_PENDING]")
                && monthly.Contains("if (memoryIncomplete)"),
                "production uses verified commits and keeps failures pending with stage diagnostics");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Monthly recovery: " + message);
            Console.WriteLine("[PASS] Monthly recovery: " + message);
        }
    }
}
