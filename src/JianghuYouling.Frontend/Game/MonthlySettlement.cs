using System;
using System.Collections.Generic;
using System.Threading;
using FrameWork;                 // ArgumentBox
using UnityEngine;
using JianghuYouling.Core.Behavior;
using JianghuYouling.Core.Memory;
using JianghuYouling.Effects;

namespace JianghuYouling
{
    /// <summary>
    /// 过月车道:订阅引擎月度推进事件(EEvents.OnAdvancingMonthStateChange,即引擎事件 6),
    /// 当推进状态回到 0(本月结算完成、世界稳定)且世界日期前进时,取出本太吾名下的意图队列消费。
    /// 注:普通前往/寻人由 goto_place 立即安排有期限行程；显式赴约及营救/保护/追杀等按各自权威目标落地，不再由此队列兑现；此处现仅落地【立场漂移(MoralityDelta)】。
    ///
    /// 纯静态稳妥做法:不需 MonoBehaviour,GEvent 持有委托即可。
    /// </summary>
    public static class MonthlySettlement
    {
        private static readonly object IntentInflightGate = new object();
        private static readonly HashSet<string> IntentInflight = new HashSet<string>(StringComparer.Ordinal);
        private static GEvent.Callback _handler;
        private static readonly MonthlyRunQueue DigestQueue = new MonthlyRunQueue();
        private static bool _inited;
        private static bool _worldBaselineReady;
        private static bool _loggedFirstEvent;   // 诊断:首次收到月度状态事件时打一行,确认事件真的到达本 mod
        private static bool _travelStopIssued;
        private static bool _monthAdvanceStopIssued;
        private const string NativeAdvanceStopReason =
            "因你在上一轮生成完成前又开始了下一次过月，上一轮尚未完成的江湖事件与主动行事已取消。";
        private const string TravelStopReason =
            "因太吾正在旅行，本月尚未完成的江湖事件与主动行事已停止；旅行结束后，下一次过月会正常恢复。";

        private static bool DigestWorldIsCurrent(int generation, uint worldId)
            => WorldLifecycle.IsSameWorld(generation) && worldId > 0
                && WorldLifecycle.WorldId == worldId;

        public static void Initialize()
        {
            if (_inited) return;
            _inited = true;
            // 以当前日期为基线,避免初始化时的状态事件触发一次空处理
            try
            {
                var bgd = SingletonObject.getInstance<BasicGameData>();
                DigestQueue.Reset(bgd != null ? bgd.CurrDate : -1);
                _worldBaselineReady = WorldLifecycle.HasWorldIdentity;
            }
            catch { DigestQueue.Reset(-1); _worldBaselineReady = false; }

            _handler = OnMonthStateChange;
            GEvent.Add(EEvents.OnAdvancingMonthStateChange, _handler);
            GEvent.Add(EEvents.OnSavingWorldStateChange, _handler);
            Debug.Log("[江湖有灵] 过月结算已挂载(基线日期=" + DigestQueue.LastClaimedDate + ")");
        }

        public static void Shutdown()
        {
            if (!_inited) return;
            try { if (_handler != null) GEvent.Remove(EEvents.OnAdvancingMonthStateChange, _handler); } catch { }
            try { if (_handler != null) GEvent.Remove(EEvents.OnSavingWorldStateChange, _handler); } catch { }
            _handler = null;
            _inited = false;
            _travelStopIssued = false;
            _monthAdvanceStopIssued = false;
        }

        public static void ResetForWorldReady()
        {
            _travelStopIssued = false;
            _monthAdvanceStopIssued = false;
            try
            {
                var bgd = SingletonObject.getInstance<BasicGameData>();
                DigestQueue.Reset(bgd != null ? bgd.CurrDate : -1);
                _worldBaselineReady = bgd != null && WorldLifecycle.HasWorldIdentity;
            }
            catch { DigestQueue.Reset(-1); _worldBaselineReady = false; }
            _loggedFirstEvent = false;
        }

        public static void SuspendForWorldChange()
        {
            DigestQueue.Reset(-1);
            _worldBaselineReady = false;
            _loggedFirstEvent = false;
            _travelStopIssued = false;
            _monthAdvanceStopIssued = false;
        }

        // The base game exposes this same frontend authority to disable reading, character
        // interaction and other location-bound actions while the Taiwu party is traveling.
        // Monthly agents must use it too: their backend mutations can otherwise interleave with
        // MapDomain.ContinueTravelWithDetectTravelingEvent and native month settlement.
        internal static bool IsTravelingForMonthlyWork()
        {
            try { return WorldMapModel.Traveling; }
            catch { return false; }
        }

        // The native backend performs month settlement as one consistency-sensitive transaction.
        // No agent mutation may overlap a non-zero advancing state, including a stale digest from
        // the preceding month that is still waiting for its model response.
        internal static bool IsAdvancingMonthForMonthlyWork()
        {
            try
            {
                var bgd = SingletonObject.getInstance<BasicGameData>();
                return bgd != null && bgd.AdvancingMonthState != 0;
            }
            catch { return false; }
        }

        internal static bool IsSavingWorldForMonthlyWork()
        {
            try
            {
                var bgd = SingletonObject.getInstance<BasicGameData>();
                return bgd == null || bgd.SavingWorld;
            }
            catch { return true; }
        }

        internal static System.Collections.IEnumerator WaitForNativeSave(int generation, uint worldId)
            => MonthlyNativeSaveBarrier.Wait(
                () => DigestWorldIsCurrent(generation, worldId),
                IsSavingWorldForMonthlyWork, IsAdvancingMonthForMonthlyWork,
                () => Debug.Log("[JHYL_MONTHLY_SAVE_DEFERRED] waiting_for_native_save=true"));

        private static string CurrentMonthlyStopReason()
        {
            if (IsAdvancingMonthForMonthlyWork()) return NativeAdvanceStopReason;
            if (IsTravelingForMonthlyWork()) return TravelStopReason;
            return null;
        }

        private static void RecordStoppedDigest(int taiwuId, int date, bool eventExpected,
            bool companionExpected, int generation, uint worldId, string reason)
        {
            if (taiwuId <= 0 || date < 0 || string.IsNullOrWhiteSpace(reason)
                || !DigestWorldIsCurrent(generation, worldId)) return;
            MonthlyChronicleProgressStore.Publish(date, taiwuId, eventExpected, true,
                companionExpected, true, 0, generation, worldId, reason);
            if (!EventLogStore.UpsertMonthlyStopReason(taiwuId, date, reason))
                Debug.LogWarning("[JHYL_MONTHLY_STOP_REASON_ARCHIVE_FAILED] date=" + date);
            else
                MonthlyChronicleNoticeStore.NotifyArchiveChanged(taiwuId);
            HistoryWindow.RefreshEventsIfOpen(taiwuId, date);
        }

        // ConfigHost calls this once per frame.  The transition latch prevents both log spam and
        // repeated cancellation, while entry-point guards below still reject work started after
        // the trip was already underway.
        internal static void ObserveTravelState()
        {
            if (!IsTravelingForMonthlyWork())
            {
                _travelStopIssued = false;
                return;
            }
            if (_travelStopIssued) return;
            _travelStopIssued = true;
            MonthlyEventGenerator.CancelForTravel();
            CompanionMonthlyActions.CancelForTravel();
            Debug.Log("[JHYL_MONTHLY_TRAVEL_STOP] 旅行中，已停止江湖事件与主动行事");
        }

        private static void OnMonthStateChange(ArgumentBox _)
        {
            try
            {
                var bgd = SingletonObject.getInstance<BasicGameData>();
                if (bgd == null) return;
                if (!_worldBaselineReady || !WorldLifecycle.HasWorldIdentity) return;
                if (!_loggedFirstEvent)
                {
                    _loggedFirstEvent = true;   // 只打一次:确认月度状态事件确实到达(排查「本月动向从没触发」)
                    Debug.Log("[江湖有灵] 首次收到月度状态事件(state=" + bgd.AdvancingMonthState + " date=" + bgd.CurrDate + " taiwu=" + bgd.TaiwuCharId + " 基线=" + DigestQueue.LastClaimedDate + ")");
                }
                if (bgd.AdvancingMonthState != 0)
                {
                    // A player can begin the next native month while the previous digest is still
                    // waiting on an LLM. Cancel both lanes before either can dispatch another
                    // mutation into WorldDomain.AdvanceMonth. The RPC layer carries a second,
                    // dispatch-time guard for callbacks that race this notification.
                    if (!_monthAdvanceStopIssued)
                    {
                        _monthAdvanceStopIssued = true;
                        bool interruptedDigest = DigestQueue.RunningDate >= 0;
                        MonthlyEventGenerator.CancelForMonthAdvance();
                        CompanionMonthlyActions.CancelForMonthAdvance();
                        if (interruptedDigest)
                            Debug.Log("[JHYL_MONTHLY_NATIVE_ADVANCE_STOP] digest_date="
                                + DigestQueue.RunningDate + " advancing_state=" + bgd.AdvancingMonthState
                                + " new_date=" + bgd.CurrDate);
                    }
                    return;
                }
                _monthAdvanceStopIssued = false;
                // AdvancingMonthState returns to zero while SavingWorld is still true.
                // The save-end notification will retry this date; do not consume it here.
                if (bgd.SavingWorld) return;
                int taiwuId = bgd.TaiwuCharId;
                int date = bgd.CurrDate;
                if (taiwuId <= 0) return;
                if (date <= DigestQueue.LastClaimedDate)
                {
                    TryStartNextDigest();
                    return;
                }
                if (DigestHost() == null)
                {
                    // Do not consume the date before a coroutine host exists. A repeated
                    // state notification can then safely retry instead of losing this month forever.
                    Debug.LogWarning("[JHYL_MONTHLY_DIGEST_DEFERRED] coroutine host unavailable date=" + date);
                    return;
                }
                ProcessQueue(taiwuId, date);
                RefreshArchivedCharacterStates(taiwuId);
                TriggerMonthlyDigest(taiwuId, date);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[江湖有灵] 过月处理异常: " + e.GetType().Name);
            }
        }

        // Consume a month before starting either lane. Record failures never reopen it.
        private static void TriggerMonthlyDigest(int taiwuId, int date)
        {
            if (DigestQueue.Claim(taiwuId, date)) TryStartNextDigest();
        }

        private static void TryStartNextDigest()
        {
            if (!_worldBaselineReady || !WorldLifecycle.HasWorldIdentity
                || IsAdvancingMonthForMonthlyWork() || IsSavingWorldForMonthlyWork()) return;
            var host = DigestHost();
            if (host == null || !DigestQueue.TryStart(out int taiwuId, out int date)) return;
            try { host.StartCoroutine(RunDigestOnce(taiwuId, date)); }
            catch (Exception e)
            {
                DigestQueue.Finish(date);
                Debug.LogWarning("[JHYL_MONTHLY_DIGEST_START_FAILED] date=" + date
                    + " error=" + e.GetType().Name + " retry=false");
            }
        }

        private static System.Collections.IEnumerator RunDigestOnce(int taiwuId, int date)
        {
            int generation = WorldLifecycle.Generation;
            uint worldId = WorldLifecycle.WorldId;
            try
            {
                yield return WaitForNativeSave(generation, worldId);
                if (!DigestWorldIsCurrent(generation, worldId)) yield break;
                MonthlyAgentRequestBudget.Begin(worldId, taiwuId, date);
                MonthlyChronicleNoticeStore.BeginGeneration(taiwuId, date);
                var stack = new Stack<System.Collections.IEnumerator>();
                stack.Push(RunDigest(taiwuId, date));
                try
                {
                    while (stack.Count > 0 && DigestWorldIsCurrent(generation, worldId))
                    {
                        var iterator = stack.Peek();
                        bool hasNext = false;
                        object next = null;
                        Exception failure = null;
                        try { hasNext = iterator.MoveNext(); if (hasNext) next = iterator.Current; }
                        catch (Exception e) { failure = e; }
                        if (failure != null)
                        {
                            Debug.LogWarning("[JHYL_MONTHLY_DIGEST_FAILED] date=" + date
                                + " error=" + failure.GetType().Name + " retry=false");
                            RecordStoppedDigest(taiwuId, date, AiEventStore.Load(), CompanionMonthlyStore.Load(),
                                generation, worldId, "本月生成遇到异常，未完成部分已结束；已执行的动作不会重试。");
                            MonthlyEventGenerator.CancelForMonthAdvance();
                            CompanionMonthlyActions.CancelForMonthAdvance();
                            break;
                        }
                        if (!hasNext) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
                        if (next is System.Collections.IEnumerator nested
                            && !(next is YieldInstruction) && !(next is CustomYieldInstruction))
                            stack.Push(nested);
                        else yield return next;
                    }
                }
                finally
                {
                    while (stack.Count > 0)
                        try { (stack.Pop() as IDisposable)?.Dispose(); } catch { }
                }
            }
            finally
            {
                if (DigestWorldIsCurrent(generation, worldId))
                {
                    MonthlyChronicleNoticeStore.EndGeneration(taiwuId, date);
                    DigestQueue.Finish(date);
                    TryStartNextDigest();
                }
            }
        }

        private static MonoBehaviour DigestHost()
        {
            MonoBehaviour host = TalkEntryHost.Instance;
            if (host == null) host = ConfigHost.Instance;
            return host;
        }

        private static System.Collections.IEnumerator RunDigest(int taiwuId, int date)
        {
            var host = DigestHost();
            if (host == null) yield break;
            int generation = WorldLifecycle.Generation;
            uint worldId = WorldLifecycle.WorldId;
            if (!WorldLifecycle.IsSameWorld(generation)) yield break;
            bool runEvent = AiEventStore.Load();
            bool runActions = CompanionMonthlyStore.Load();
            if (IsAdvancingMonthForMonthlyWork())
            {
                RecordStoppedDigest(taiwuId, date, runEvent, runActions,
                    generation, worldId, NativeAdvanceStopReason);
                Debug.Log("[JHYL_MONTHLY_NATIVE_ADVANCE_SKIP] date=" + date
                    + " event=false companion=false retry=false");
                yield break;
            }
            if (IsTravelingForMonthlyWork())
            {
                // Travel consumes this month's attempt; the next month starts fresh.
                RecordStoppedDigest(taiwuId, date, runEvent, runActions,
                    generation, worldId, TravelStopReason);
                Debug.Log("[JHYL_MONTHLY_TRAVEL_SKIP] date=" + date
                    + " event=false companion=false retry=false");
                yield break;
            }
            float digestStarted = Time.unscaledTime;
            string stopReason = null;
            string evText = null, evArea = null; int evHeard = 0; bool evDone = false;
            List<CompanionMonthlyResult> companionActions = null; bool companionDone = false;
            int companionCompleted = 0, companionTotal = 0;
            int archivedCompanionCount = 0;
            int archivedInterruptedCount = 0;
            var laneCoordinator = new MonthlyLaneCoordinator(runEvent);
            Func<int, IReadOnlyCollection<int>, bool> companionMutationGate = (actorId, targetIds) =>
                laneCoordinator.CanCompanionMutate(actorId, targetIds);
            Action observeStop = () =>
            {
                if (!string.IsNullOrWhiteSpace(stopReason)) return;
                stopReason = CurrentMonthlyStopReason();
                if (string.IsNullOrWhiteSpace(stopReason)) return;
                if (stopReason == NativeAdvanceStopReason)
                {
                    MonthlyEventGenerator.CancelForMonthAdvance();
                    CompanionMonthlyActions.CancelForMonthAdvance();
                }
                else
                {
                    MonthlyEventGenerator.CancelForTravel();
                    CompanionMonthlyActions.CancelForTravel();
                }
            };
            // JHYL_MONTHLY_DIGEST_INCREMENTAL: 任一真实生成项完成即首次弹出；同一批之后只
            // 更新现有内容，不抢回已被玩家关闭的窗口。切档检查位于所有异步回调之前。
            // 历史窗口是权威的进度视图；结算一开始就登记月份，不必等第一个 Agent
            // 完成后才让玩家看见“生成中”。自动打开窗口仍沿用下方 anyReady 门控。
            MonthlyChronicleProgressStore.Publish(date, taiwuId, runEvent, false,
                runActions, false, runActions ? 1 : 0, generation, worldId);
            HistoryWindow.RefreshEventsIfOpen(taiwuId, date);
            // 新档首月常需先建立参与者与画像，后台模型可能耗时较长。任务一启动就把
            // 本月卷宗交给统一进度窗口，明确显示“生成中”；最终正文仍只认后续权威
            // 回执与归档，绝不拿占位文字冒充已经发生的事件。
            MonthlyDigestPopup.ShowProgress(date, null, null, 0, null,
                taiwuId, null, runEvent, false, runActions, false,
                runActions ? 1 : 0, generation, worldId);
            Debug.Log("[JHYL_MONTHLY_DIGEST_VISIBLE_PENDING] date=" + date
                + " event=" + runEvent + " companion=" + runActions);
            Action publishProgress = () =>
            {
                if (!WorldLifecycle.IsSameWorld(generation) || WorldLifecycle.WorldId != worldId) return;
                bool hasCompanionResult = runActions && companionActions != null
                    && companionActions.Count > 0;
                // JHYL_MONTHLY_ARCHIVE_COALESCED: 进度 UI 仍逐人刷新，但纪事档案属于耐久
                // 大文件，不能每完成一人就在 Unity 主线程做整份读回、三副本原子提交。
                // 首个结果、明确中止和整批完成时归档。先归档再检查整月中止，防止取消吞掉回执。
                int currentCompanionCount = hasCompanionResult ? companionActions.Count : 0;
                int interruptedCount = hasCompanionResult
                    ? companionActions.FindAll(value => value != null && !string.IsNullOrWhiteSpace(value.StopReason)).Count : 0;
                bool shouldArchiveCompanions = currentCompanionCount > archivedCompanionCount
                    && (archivedCompanionCount == 0 || companionDone || interruptedCount > archivedInterruptedCount);
                if (shouldArchiveCompanions)
                {
                    bool archived = EventLogStore.UpsertMonthlyDigest(taiwuId, date, companionActions);
                    if (!archived)
                    {
                        const string warning = "记录未完整保存：本月同道结果已显示，但纪事归档写入失败；已执行的动作不会重试，也不会跨月补记。";
                        Debug.LogWarning("[江湖有灵] " + warning);
                        MonthlyChronicleProgressStore.WarnIncompleteRecords(taiwuId, date, warning);
                        HistoryWindow.RefreshEventsIfOpen(taiwuId, date);
                    }
                    else
                    {
                        archivedCompanionCount = currentCompanionCount;
                        archivedInterruptedCount = interruptedCount;
                        MonthlyChronicleNoticeStore.NotifyArchiveChanged(taiwuId);
                        HistoryWindow.RefreshEventsIfOpen(taiwuId, date);
                    }
                }
                observeStop();
                if (!string.IsNullOrWhiteSpace(stopReason)) return;
                int pending = companionDone ? 0 : Math.Max(0, companionTotal - companionCompleted);
                MonthlyChronicleProgressStore.Publish(date, taiwuId, runEvent, evDone,
                    runActions, companionDone, pending, generation, worldId);
                HistoryWindow.RefreshEventsIfOpen(taiwuId, date);
                // JHYL_MONTHLY_FIRST_LANE_COMPLETION_POPUP:任一分支结束即展示状态，包括空结果。
                bool eventLaneCompleted = runEvent && evDone;
                bool companionLaneCompleted = runActions && companionCompleted > 0;
                bool anyReady = eventLaneCompleted || companionLaneCompleted;
                if (!anyReady) return;
                MonthlyDigestPopup.ShowProgress(date, evText, evArea, evHeard, companionActions,
                    taiwuId, null, runEvent, evDone, runActions, companionDone, pending,
                    generation, worldId);
            };
            Action startCompanionPlanning = () =>
            {
                observeStop();
                if (!string.IsNullOrWhiteSpace(stopReason))
                {
                    companionDone = true;
                    return;
                }
                if (!laneCoordinator.TryStartCompanion(runActions)) return;
                if (!WorldLifecycle.IsSameWorld(generation)) { companionDone = true; return; }
                float companionStarted = Time.unscaledTime;
                try
                {
                    host.StartCoroutine(CompanionMonthlyActions.RunMonthly(taiwuId, date, actions =>
                    {
                        companionActions = actions;
                        companionDone = true;
                        companionCompleted = Math.Max(companionCompleted, companionTotal);
                        observeStop();
                        Debug.Log("[JHYL_MONTHLY_DIGEST_TIMING] companion_ms=" + ElapsedMs(companionStarted)
                            + " count=" + (actions == null ? 0 : actions.Count));
                        publishProgress();
                    }, companionMutationGate, (actions, completed, total) =>
                    {
                        companionActions = actions;
                        companionCompleted = Math.Max(0, completed);
                        companionTotal = Math.Max(companionCompleted, total);
                        publishProgress();
                    }));
                }
                catch (Exception e)
                {
                    companionDone = true;
                    Debug.LogWarning("[江湖有灵] 同道过月并行规划启动失败:" + e.GetType().Name);
                }
            };
            // JHYL_MONTHLY_CONFLICT_AWARE_PARALLELISM:同道的快照、记忆、关系查询和主模型规划
            // 与江湖事件从一开始就并行。事件名册解析后，不涉及同一行动者/目标的同道副作用
            // 也立即并行；只有人物集合重叠时才等事件的 mutation checkpoint，故事、见闻和归档
            // 不再充当全局写锁。
            if (runEvent && runActions)
            {
                Debug.Log("[JHYL_MONTHLY_DIGEST_TIMING] companion_planning_started_with_event=true"
                    + " companion_mutation_gate=participant_conflicts_only");
                startCompanionPlanning();
            }
            if (runEvent)   // 设置里可关:关掉则过月不生成 AI 江湖事件,但同道主动行事仍可独立运行。
            {
                float evStarted = Time.unscaledTime;
                yield return MonthlyEventGenerator.RunMonthly(taiwuId, date, (t, a, h) =>
                {
                    evText = t; evArea = a; evHeard = h; evDone = true;
                    observeStop();
                    MonthlyChronicleNoticeStore.NotifyArchiveChanged(taiwuId);
                    Debug.Log("[JHYL_MONTHLY_DIGEST_TIMING] event_ms=" + ElapsedMs(evStarted) + " hasText=" + !string.IsNullOrWhiteSpace(t));
                    publishProgress();
                }, false, () =>
                {
                    laneCoordinator.MarkMutationCheckpoint();
                    Debug.Log("[JHYL_MONTHLY_DIGEST_TIMING] event_mutation_checkpoint_ms=" + ElapsedMs(evStarted)
                        + " companion_planning_already_started=" + laneCoordinator.CompanionStarted
                        + " companion_mutation_gate=overlap_released");
                }, ids =>
                {
                    laneCoordinator.SetEventParticipants(ids);
                    int resolvedParticipantCount = laneCoordinator.EventParticipantCount;
                    Debug.Log("[JHYL_MONTHLY_DIGEST_TIMING] event_participants_resolved count="
                        + resolvedParticipantCount + " unrelated_companions_released=true");
                }, (ids, identitiesReliable) =>
                {
                    laneCoordinator.MarkMutationIsolationUnresolved(ids, identitiesReliable);
                    Debug.LogWarning("[JHYL_MONTHLY_DIGEST_TIMING] event_mutation_isolation_retained"
                        + " identitiesReliable=" + identitiesReliable
                        + " participantCount=" + laneCoordinator.EventParticipantCount);
                });
                observeStop();
                if (!evDone)
                {
                    evDone = true;
                    Debug.LogWarning("[江湖有灵] 过月事件协程结束但未回调；UI 分支收口，"
                        + "人物冲突门仍只接受权威 mutation checkpoint");
                    publishProgress();
                }
                startCompanionPlanning(); // 无事件/早退时 checkpoint 最迟在事件返回后触发。
            }
            else
            {
                evDone = true;
                Debug.Log("[JHYL_MONTHLY_DIGEST_TIMING] event_disabled");
                startCompanionPlanning();
            }
            if (runActions)
            {
                while (!companionDone && WorldLifecycle.IsSameWorld(generation))
                {
                    observeStop();
                    if (!string.IsNullOrWhiteSpace(stopReason))
                    {
                        companionDone = true;
                        break;
                    }
                    yield return null;
                }
                if (!companionDone)
                {
                    companionDone = true;
                    Debug.LogWarning("[江湖有灵] 同道过月协程随世界切换终止，本月按空结果收口");
                }
            }
            else { companionDone = true; Debug.Log("[JHYL_MONTHLY_DIGEST_TIMING] companion_disabled"); }
            if (!WorldLifecycle.IsSameWorld(generation)) yield break;
            if (!string.IsNullOrWhiteSpace(stopReason))
            {
                RecordStoppedDigest(taiwuId, date, runEvent, runActions,
                    generation, worldId, stopReason);
                string reasonCode = stopReason == NativeAdvanceStopReason
                    ? "native_month_advance" : "travel";
                Debug.Log("[JHYL_MONTHLY_STOPPED] date=" + date
                    + " reason=" + reasonCode + " retry=false");
            }
            Debug.Log("[JHYL_MONTHLY_DIGEST_TIMING] total_ms=" + ElapsedMs(digestStarted) + " eventDone=" + evDone + " companionDone=" + companionDone);
            publishProgress();
        }

        private static int ElapsedMs(float started)
            => Math.Max(0, (int)((Time.unscaledTime - started) * 1000f));

        private static void ProcessQueue(int taiwuId, int date)
        {
            var q = IntentQueue.Load(JianghuYoulingPaths.Intents, taiwuId.ToString());
            if (!q.LoadReliable)
            {
                Debug.LogWarning("[江湖有灵] 过月意图队列读取不可靠，拒绝消费，避免覆盖或重复副作用");
                return;
            }
            if (q.Count == 0) return;

            List<BehaviorIntent> items = q.Snapshot();
            foreach (var item in items)
                if (item != null && string.IsNullOrWhiteSpace(item.Id))
                {
                    Debug.LogWarning("[江湖有灵] 过月意图缺失 durable id，拒绝派发；IntentQueue.Load 修复未成功");
                    return;
                }

            // 普通前往/来寻经 goto_place 安排有期限行程，显式赴约及营救/保护/追杀按各自目标即时落地，不再由本地队列排队;
            // 本队列如今只余【立场漂移】(MoralityDelta)一类消费者。
            int drifted = 0;
            foreach (var it in items)
            {
                if (it == null) continue;
                if (it.MoralityDelta != 0)
                {
                    if (it.TaiwuId <= 0 || it.TaiwuId != taiwuId)
                    {
                        Debug.LogWarning("[江湖有灵] 过月意图原始太吾身份缺失或不匹配，保持队列不派发 id=" + (it.Id ?? "(空)"));
                        continue;
                    }
                    int npcM = it.NpcId, d = it.MoralityDelta;
                    string intentId = it.Id;
                    string operationId = IntentQueue.OperationIdFor(it);
                    lock (IntentInflightGate)
                    {
                        if (!IntentInflight.Add(operationId)) continue;
                    }
                    try { QueryThenDispatchIntent(q, intentId, operationId,
                        JianghuYoulingPaths.CurrentWorldId, it.TaiwuId, npcM, d, WorldLifecycle.Generation); }
                    catch (Exception e)
                    {
                        lock (IntentInflightGate) IntentInflight.Remove(operationId);
                        Debug.LogWarning("[江湖有灵] 过月立场漂移派发异常，意图仍在队列:" + operationId + " " + e.GetType().Name);
                        continue;
                    }
                    drifted++;
                }
            }
            Debug.Log("[江湖有灵] 过月结算 date=" + date + ": 立场漂移 " + drifted);
        }

        // JHYL_MONTHLY_INTENT_QUERY_FIRST: 持久意图恢复时绝不先重放 mutation。只有后端明确表示
        // operation_not_found，才用同一个稳定 operationId 首次派发；其余未知/取消均保留队列。
        private static void QueryThenDispatchIntent(IntentQueue queue, string intentId, string operationId,
            uint worldId, int taiwuId, int npcId, int delta, int generation)
        {
            EffectHandler.QueryOperation(worldId, taiwuId, operationId, outcome =>
            {
                if (!WorldLifecycle.IsSameWorld(generation))
                {
                    ReleaseIntentInflight(operationId);
                    return;
                }
                if (outcome != null && string.Equals(outcome.Code, "operation_not_found", StringComparison.OrdinalIgnoreCase))
                {
                    DispatchIntentMutation(queue, intentId, operationId, worldId, taiwuId, npcId, delta, generation);
                    return;
                }
                CompleteIntentFromOutcome(queue, intentId, operationId, worldId, taiwuId,
                    npcId, delta, outcome, "query");
            });
        }

        private static void DispatchIntentMutation(IntentQueue queue, string intentId, string operationId,
            uint worldId, int taiwuId, int npcId, int delta, int generation)
        {
            int completed = 0;
            Action<JianghuYouling.Core.Tools.ToolOutcome> finish = outcome =>
            {
                if (Interlocked.CompareExchange(ref completed, 1, 0) != 0) return;
                EffectHandler.ForgetOperationOutcomeObserver(operationId);
                if (!WorldLifecycle.IsSameWorld(generation))
                {
                    ReleaseIntentInflight(operationId);
                    return;
                }
                CompleteIntentFromOutcome(queue, intentId, operationId, worldId, taiwuId,
                    npcId, delta, outcome, "dispatch");
            };
            EffectHandler.ObserveOperationOutcome(operationId, finish);
            try
            {
                if (!EffectHandler.PrepareOperationIdentity(worldId, taiwuId, operationId))
                {
                    finish(JianghuYouling.Core.Tools.ToolOutcome.Failed(
                        "operation_identity_changed_before_dispatch", operationId, null,
                        "存档或太吾身份已变化，动作没有派发"));
                    return;
                }
                EffectHandler.ApplyChangeMorality(npcId, delta, (ok, message) =>
                {
                    // 正常 RPC 会先发布结构化 ToolOutcome；这里只兜底本地前置拒绝或兼容旧桥。
                    if (Volatile.Read(ref completed) != 0) return;
                    bool terminal = IsTerminalIntentOutcome(ok, message);
                    finish(new JianghuYouling.Core.Tools.ToolOutcome
                    {
                        OperationId = operationId,
                        Status = ok ? "succeeded" : (terminal ? "failed" : "unknown"),
                        Code = ok ? "OK" : (terminal ? "legacy_failed" : "legacy_unconfirmed"),
                        Retryable = !terminal,
                        Message = message,
                    });
                }, operationId);
            }
            catch (Exception e)
            {
                finish(JianghuYouling.Core.Tools.ToolOutcome.Unknown(operationId, "派发异常:" + e.GetType().Name));
            }
        }

        private static void CompleteIntentFromOutcome(IntentQueue queue, string intentId, string operationId,
            uint worldId, int taiwuId, int npcId, int delta,
            JianghuYouling.Core.Tools.ToolOutcome outcome, string source)
        {
            bool persisted = false;
            try
            {
                if (outcome != null && outcome.IsTerminal)
                {
                    persisted = queue.AcknowledgePersisted(intentId);
                    // Locally canceled/no-dispatch outcomes may intentionally consume the
                    // intent, but there is no backend receipt to ACK.  Only an authoritative
                    // structured receipt is allowed into the durable ACK outbox.
                    if (persisted && !string.IsNullOrWhiteSpace(outcome.Receipt))
                        EffectHandler.AcknowledgeOperation(worldId, taiwuId, operationId, receiptAck =>
                        {
                            if (!receiptAck) Debug.LogWarning("[江湖有灵] 过月意图终态 ACK 暂未完成:" + operationId);
                        });
                }
            }
            finally { ReleaseIntentInflight(operationId); }

            string status = outcome == null ? "unknown" : (outcome.Status ?? "unknown");
            string code = outcome == null ? "missing_outcome" : (outcome.Code ?? "");
            Debug.Log("[江湖有灵] 过月立场漂移 operationId=" + operationId + " npc=" + npcId + " delta=" + delta
                + " source=" + source + " status=" + status + " code=" + code + " intentAck=" + persisted);
            if (outcome == null || !outcome.IsTerminal)
                Debug.LogWarning("[江湖有灵] 过月意图回执未确认/已取消，保留队列且不更换 operationId:" + operationId);
            else if (!persisted)
                Debug.LogWarning("[江湖有灵] 过月意图终态存盘失败，保留稳定 operationId 供下次只查询恢复:" + operationId);
        }

        private static void ReleaseIntentInflight(string operationId)
        {
            lock (IntentInflightGate) IntentInflight.Remove(operationId);
        }

        private static bool IsTerminalIntentOutcome(bool success, string message)
        {
            if (success) return true;
            string text = (message ?? "").Trim();
            // bool 兼容桥拿不到结构化 status 时必须按“只有明确终态才 ACK”处理。空/解析失败/
            // 传输与生命周期取消都只是未确认；保留原 intent + operationId，之后仍走后端幂等账本。
            if (string.IsNullOrWhiteSpace(text) || text == "未完成") return false;
            if (text.StartsWith("UNKNOWN:", StringComparison.OrdinalIgnoreCase)) return false;
            string lower = text.ToLowerInvariant();
            if (lower.Contains("world_changed") || lower.Contains("world_inactive") || lower.Contains("canceled")
                || lower.Contains("cancelled") || lower.Contains("timeout") || text.Contains("已切换")
                || text.Contains("离开存档") || text.Contains("当前不在存档") || text.Contains("已取消")
                || text.Contains("结果解析失败") || text.Contains("回包为空") || text.Contains("回包解析失败")
                || text.Contains("派发失败") || text.Contains("回执未确认") || text.Contains("结果未知")) return false;
            // 其余 false 来自后端确定 failed/rejected（参数、前置条件、引擎拒绝），属于可 ACK 终态。
            return true;
        }

        /// <summary>
        /// 过月刷新人物档案状态：聊天、群聊、人设、记忆、画像和小说素材全部永久保留；
        /// 真死者只持久标记“已故”并剪掉尚未执行的未来意图。剧情阶段退场/临时移除者不误标死亡。
        /// </summary>
        private static void RefreshArchivedCharacterStates(int taiwuId)
        {
            int generation = WorldLifecycle.Generation;
            string tid = taiwuId.ToString();
            var known = new HashSet<int>();
            try
            {
                foreach (int id in NpcMemoryStore.EnumerateNpcIds(
                    JianghuYoulingPaths.Memories, tid))
                    if (id > 0) known.Add(id);
                if (ConversationSessionIndexStore.TryLoadSingles(taiwuId,
                    out List<ConversationSessionIndexStore.SingleEntry> singles))
                    foreach (ConversationSessionIndexStore.SingleEntry entry in singles)
                        if (entry != null && entry.NpcId > 0) known.Add(entry.NpcId);
                if (ConversationSessionIndexStore.TryLoadGroups(taiwuId,
                    out List<ConversationSessionIndexStore.GroupEntry> groups))
                    foreach (ConversationSessionIndexStore.GroupEntry group in groups)
                        if (group?.Members != null)
                            foreach (ConversationSessionIndexStore.MemberEntry member in group.Members)
                                if (member != null && member.Id > 0) known.Add(member.Id);
            }
            catch { return; }
            if (known.Count == 0) return;

            string csv = string.Join(",", new List<int>(known).ConvertAll(
                i => i.ToString()).ToArray());
            EffectHandler.QueryCharacterArchiveStates(csv, (ok, aliveCsv, deadCsv) =>
            {
                if (!ok || !WorldLifecycle.IsSameWorld(generation)) return;
                ReconcileArchivedCharacters(taiwuId, tid, aliveCsv, deadCsv, generation);
            });
        }

        private static List<int> ParseCharacterIds(string csv)
        {
            var result = new List<int>();
            var seen = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(csv)) return result;
            foreach (string part in csv.Split(','))
                if (int.TryParse(part.Trim(), out int id) && id > 0 && seen.Add(id))
                    result.Add(id);
            return result;
        }

        private static void ReconcileArchivedCharacters(int taiwuId, string tid,
            string aliveCsv, string deadCsv, int generation)
        {
            try
            {
                if (!WorldLifecycle.IsSameWorld(generation)) return;
                List<int> aliveIds = ParseCharacterIds(aliveCsv);
                List<int> deadIds = ParseCharacterIds(deadCsv);
                if (!ArchivedCharacterStatusStore.ApplyAuthoritative(taiwuId,
                    aliveIds, deadIds, out bool statusChanged))
                {
                    Debug.LogWarning("[江湖有灵] 已故人物档案状态未可靠写入，本轮保留原状态稍后重试");
                    return;
                }

                // 只剪掉死者尚未执行的未来行动；过去发生过的一切内容与视觉资料完整保留。
                IntentQueue q = null;
                try { q = IntentQueue.Load(JianghuYoulingPaths.Intents, tid); } catch { }
                int removedIntents = 0;
                foreach (int id in deadIds)
                    if (q != null) removedIntents += q.RemoveByNpc(id);

                bool intentsCommitted = q == null || removedIntents == 0;
                if (q != null && removedIntents > 0)
                {
                    try { intentsCommitted = q.Save(); }
                    catch { intentsCommitted = false; }
                }
                if (!intentsCommitted)
                    Debug.LogWarning("[江湖有灵] 已故人物未来意图未可靠提交，旧条目将在后续重试；本轮不宣称已清除 "
                        + removedIntents + " 条");
                // 恢复机会单独耐久化：不论过月、打开会话或主动消息调度哪条路径先发现死亡，
                // 都会重试到成功处理一次；此后玩家再隐藏就永不自动恢复。
                foreach (int id in deadIds)
                    ChatWindow.RevealNewSoulConversation(taiwuId, id);
                if (statusChanged) ChatWindow.NotifyCharacterArchiveStatusChanged(taiwuId);
                Debug.Log("[江湖有灵] 人物档案状态刷新:确认已故 " + deadIds.Count
                    + " 人，历史内容全部保留；清理未来意图 "
                    + (intentsCommitted ? removedIntents : 0) + " 条");
            }
            catch (Exception e) { Debug.LogWarning("[江湖有灵] 人物档案状态刷新异常: " + e.GetType().Name); }
        }
    }
}
