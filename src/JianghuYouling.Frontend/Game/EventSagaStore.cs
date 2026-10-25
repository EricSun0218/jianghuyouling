using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using JianghuYouling.Core.Tools;
using JianghuYouling.Core.Persistence;
using JianghuYouling.Core.Prompt;

namespace JianghuYouling
{
    public sealed class EventSagaGoal
    {
        public string Id;
        public string Summary;
        public string SuccessCriteria;
        public int MinimumSucceededSteps = 1;
        public int SucceededSteps;
        public int FailedSteps;
        public string Status = "active"; // active | achieved | failed | abandoned
        public int UpdatedDate;
    }

    public sealed class EventSagaOpenLoop
    {
        public string Id;
        public string Kind;
        public string Summary;
        public string Status = "open"; // open | resolved | blocked
        public List<int> ParticipantIds = new List<int>();
        public List<string> EvidenceIds = new List<string>();
        public int NextEligibleDate;
    }

    public sealed class EventSagaConstraint
    {
        public string Id;
        public string Kind;
        public string Value;
        public bool Active = true;
    }

    public sealed class EventSagaStepOutcome
    {
        public string OperationId;
        public string StepId;
        public string ToolName;
        public string Status; // prepared | succeeded | failed | unknown
        public string Code;
        public string Intent;
        public string Summary;
        public string Receipt;
        // True only when Receipt came from a structured backend mutation/query response.
        // Local cancellation/result strings must never enqueue an operation ACK.
        public bool BackendReceiptStored;
        public bool Retryable;
        public int ActorId;
        public int TargetId;
        // Names are frozen from the game-owned roster beside the numeric identity fields.
        // Durable story projection never recovers them from model/dispatch JSON.
        public string ActorName;
        public string TargetName;
        // Frozen only from the successful executor callback; StoryReceipt must match it exactly.
        // It is deliberately separate from both model/dispatch JSON and the receipt being checked.
        public string ProjectionAsset;
        // Populated only after a succeeded, persisted backend receipt and the matching typed
        // execution callback have both arrived.  Null means projection must use a non-factual fallback.
        public StoryProjectionReceipt StoryReceipt;
        public int WorldDate;
        public long UpdatedUtcTicks;
    }

    /// <summary>
    /// 连载的已知事实与章节历史。未完成任务、行动参数和文字补记进度不跨月保存。
    /// </summary>
    public sealed class EventSaga
    {
        [JsonIgnore]
        public bool LoadReliable = true;
        public int Version = 1;
        public long Revision;
        public uint WorldId;
        public int TaiwuId;
        public bool Active;
        public string Title;
        public string Outline;
        public string AreaName;
        public short AreaId = -1;
        public List<int> ProtagonistIds = new List<int>();
        public List<string> ProtagonistNames = new List<string>();
        public List<string> Chapters = new List<string>();
        public List<int> ChapterDates = new List<int>();
        public bool TaiwuInvolved;
        // Durable ids of recent single/group turns whose code-owned successful action rows
        // prove Taiwu actually acted (gifted/taught/wrote/told), rather than merely chatted.
        public List<string> TaiwuFameEvidenceIds = new List<string>();
        public int MonthsTotal;
        public int MonthsElapsed;
        public int StartDate;
        public int CompletedDate;

        public EventSagaGoal Goal;
        public List<EventSagaOpenLoop> OpenLoops = new List<EventSagaOpenLoop>();
        public List<EventSagaConstraint> Constraints = new List<EventSagaConstraint>();
        [JsonIgnore]
        public EventSagaStepOutcome LastOutcome;
        public List<EventSagaStepOutcome> OutcomeJournal = new List<EventSagaStepOutcome>();
        [JsonIgnore]
        public List<string> UnconfirmedOperationIds = new List<string>();
        [JsonIgnore]
        public bool RecordsIncomplete;
    }

    internal static class EventDispatchEnvelopeBinding
    {
        internal static string CreateStableKey(uint worldId, int taiwuId,
            int startDate, int worldDate, string stepId, string toolName,
            int actorId, int targetId, string argumentsJson)
            => "monthly-event|" + worldId + "|" + taiwuId
                + "|" + startDate + "|" + worldDate + "|" + (stepId ?? string.Empty)
                + "|" + (toolName ?? string.Empty) + "|" + actorId + "|" + targetId
                + "|" + (argumentsJson ?? string.Empty);

    }

    /// <summary>
    /// Keeps a durable saga roster aligned after the base game removes temporary/dead
    /// characters. The id/name arrays and every open-loop participant list must be pruned
    /// together; leaving only the old display names makes an invalid character look usable
    /// to the monthly agent.
    /// </summary>
    internal static class EventSagaRosterPolicy
    {
        internal static int RemoveUnavailable(EventSaga saga, ISet<int> unavailable)
        {
            if (saga == null || saga.ProtagonistIds == null || unavailable == null
                || unavailable.Count == 0) return 0;

            if (saga.ProtagonistNames == null)
                saga.ProtagonistNames = new List<string>();

            int removed = 0;
            for (int i = saga.ProtagonistIds.Count - 1; i >= 0; i--)
            {
                if (!unavailable.Contains(saga.ProtagonistIds[i])) continue;
                saga.ProtagonistIds.RemoveAt(i);
                if (i < saga.ProtagonistNames.Count)
                    saga.ProtagonistNames.RemoveAt(i);
                removed++;
            }

            if (saga.OpenLoops != null)
                foreach (EventSagaOpenLoop loop in saga.OpenLoops)
                    if (loop?.ParticipantIds != null)
                        loop.ParticipantIds.RemoveAll(id => unavailable.Contains(id));

            return removed;
        }
    }

    /// <summary>Single-writer story history using the shared atomic file store.</summary>
    public static class EventSagaStore
    {
        private static readonly object Io = new object();
        private const int MaxSagaBytes = 8 * 1024 * 1024;
        private const int MaxOutcomes = 256;
        private static string PathFor(int taiwuId)
            => Path.Combine(JianghuYoulingPaths.Events, "saga_story_" + taiwuId + ".json");

        private static EventSaga Empty(int taiwuId) => new EventSaga
        {
            WorldId = JianghuYoulingPaths.CurrentWorldId, TaiwuId = taiwuId,
        };

        public static EventSaga Load(int taiwuId)
        {
            lock (Io)
            {
                bool any = false;
                if (taiwuId > 0 && DurableFileStore.TryReadRecoverableTextReadOnly(
                    PathFor(taiwuId), MaxSagaBytes, text => IsValid(text, taiwuId),
                    out string json, out any, out _))
                {
                    EventSaga saga = JsonConvert.DeserializeObject<EventSaga>(json);
                    saga.LastOutcome = saga.OutcomeJournal.Count == 0 ? null
                        : saga.OutcomeJournal[saga.OutcomeJournal.Count - 1];
                    return saga;
                }
                EventSaga empty = Empty(taiwuId);
                empty.LoadReliable = !any;
                return empty;
            }
        }

        public static bool Save(int taiwuId, EventSaga saga)
        {
            if (taiwuId <= 0 || saga == null || !saga.LoadReliable
                || saga.TaiwuId != taiwuId || saga.WorldId != JianghuYoulingPaths.CurrentWorldId)
                return false;
            lock (Io)
            {
                EventSaga stored = Load(taiwuId);
                if (!stored.LoadReliable || stored.Revision > saga.Revision
                    || saga.Revision == long.MaxValue) return false;
                long previous = saga.Revision;
                saga.Revision++;
                if (saga.OutcomeJournal.Count > MaxOutcomes)
                    saga.OutcomeJournal.RemoveRange(0, saga.OutcomeJournal.Count - MaxOutcomes);
                bool saved = DurableFileStore.TryWriteTextAtomic(PathFor(taiwuId),
                    JsonConvert.SerializeObject(saga, Formatting.None), MaxSagaBytes,
                    text => IsValid(text, taiwuId));
                if (!saved) saga.Revision = previous;
                return saved;
            }
        }

        public static bool Clear(int taiwuId)
        {
            if (taiwuId <= 0) return false;
            lock (Io)
            {
                EventSaga stored = Load(taiwuId);
                if (!stored.LoadReliable) return false;
                EventSaga empty = Empty(taiwuId);
                empty.Revision = stored.Revision;
                return Save(taiwuId, empty);
            }
        }

        private static bool IsValid(string json, int taiwuId)
        {
            try
            {
                JObject document = JObject.Parse(json,
                    new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                var saga = document.ToObject<EventSaga>(JsonSerializer.Create(
                    new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error, MaxDepth = 64 }));
                if (saga == null || saga.Version != 1 || saga.Revision < 0
                    || saga.WorldId == 0 || saga.WorldId != JianghuYoulingPaths.CurrentWorldId
                    || saga.TaiwuId != taiwuId || saga.StartDate < 0 || saga.CompletedDate < 0
                    || saga.MonthsTotal < 0 || saga.MonthsTotal > 120
                    || saga.MonthsElapsed < 0 || saga.MonthsElapsed > saga.MonthsTotal
                    || saga.ProtagonistIds == null || saga.ProtagonistNames == null
                    || saga.ProtagonistIds.Count != saga.ProtagonistNames.Count
                    || saga.ProtagonistIds.Count > 128 || saga.OpenLoops == null
                    || saga.Constraints == null || saga.TaiwuFameEvidenceIds == null
                    || saga.OutcomeJournal == null || saga.OutcomeJournal.Count > MaxOutcomes
                    || saga.Chapters == null || saga.ChapterDates == null
                    || saga.Chapters.Count != saga.ChapterDates.Count
                    || saga.Chapters.Count != saga.MonthsElapsed
                    || saga.Active && (saga.AreaId < 0 || saga.MonthsTotal == 0
                        || saga.MonthsElapsed >= saga.MonthsTotal)) return false;
                foreach (int id in saga.ProtagonistIds) if (id <= 0) return false;
                foreach (int date in saga.ChapterDates) if (date < saga.StartDate) return false;
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (EventSagaStepOutcome outcome in saga.OutcomeJournal)
                {
                    if (outcome == null || !OperationId.IsValid(outcome.OperationId)
                        || !ids.Add(outcome.OperationId) || outcome.ActorId <= 0
                        || outcome.WorldDate < saga.StartDate || string.IsNullOrWhiteSpace(outcome.ToolName)
                        || (outcome.Status != "succeeded" && outcome.Status != "failed"
                            && outcome.Status != "rejected" && outcome.Status != "canceled"
                            && outcome.Status != "unknown")) return false;
                }
                foreach (EventSagaOpenLoop loop in saga.OpenLoops)
                    if (loop == null || loop.ParticipantIds == null || loop.EvidenceIds == null) return false;
                return true;
            }
            catch { return false; }
        }
    }
}
