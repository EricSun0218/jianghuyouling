using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace JianghuYouling.Core.Persistence
{
    /// <summary>
    /// Durable, idempotent projection checkpoint for one monthly event. Recipient selection is
    /// frozen before any memory write; CompletedRecipientIds is advanced after each recipient's
    /// source-keyed memory has committed. Projection/ACK is legal only after EventLog, every
    /// recipient and the final Heard value have committed.
    /// </summary>
    public sealed class EventFanoutCheckpoint
    {
        public string EventId;
        public int WorldDate;
        public short AreaId = -1;
        public string AreaName;
        public string StoryText;
        public string ProjectionText;
        public string Roster;
        public List<string> Actions = new List<string>();
        public List<string> OutcomeOperationIds = new List<string>();
        public List<int> RecipientIds = new List<int>();
        public List<int> CompletedRecipientIds = new List<int>();
        public int ChapterNumber;
        public bool Finale;
        public bool EventLogCommitted;
        public bool FanoutCompleted;
        public bool HeardCommitted;
        public bool ProjectionCommitted;
        public int Heard;
        public long UpdatedUtcTicks;
    }

    public static class EventFanoutPolicy
    {
        public const int MaxCheckpoints = 24;
        public const int MaxRecipients = 512;
        public const int MaxActions = 64;
        public const int MaxOutcomeOperations = 64;

        public static bool SelectRecipient(string eventId, int recipientId, double probability)
        {
            if (string.IsNullOrWhiteSpace(eventId) || recipientId <= 0 || double.IsNaN(probability)) return false;
            if (probability <= 0d) return false;
            if (probability >= 1d) return true;
            byte[] bytes = Encoding.UTF8.GetBytes(eventId + "|recipient|" + recipientId);
            byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(bytes);
            ulong value = 0;
            for (int i = 0; i < 8; i++) value = (value << 8) | hash[i];
            double unit = value / (double)ulong.MaxValue;
            return unit < probability;
        }

        public static bool MarkRecipientCommitted(EventFanoutCheckpoint checkpoint, int recipientId)
        {
            if (checkpoint == null || !checkpoint.EventLogCommitted || recipientId <= 0 || checkpoint.RecipientIds == null
                || !checkpoint.RecipientIds.Contains(recipientId)) return false;
            if (checkpoint.CompletedRecipientIds == null)
                checkpoint.CompletedRecipientIds = new List<int>();
            if (checkpoint.CompletedRecipientIds.Contains(recipientId)) return true;
            checkpoint.CompletedRecipientIds.Add(recipientId);
            checkpoint.CompletedRecipientIds.Sort();
            checkpoint.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
            return true;
        }

        public static List<int> PendingRecipients(EventFanoutCheckpoint checkpoint)
        {
            var pending = new List<int>();
            if (checkpoint?.RecipientIds == null) return pending;
            var completed = new HashSet<int>(checkpoint.CompletedRecipientIds ?? new List<int>());
            foreach (int id in checkpoint.RecipientIds)
                if (id > 0 && !completed.Contains(id)) pending.Add(id);
            pending.Sort();
            return pending;
        }

        public static bool AllRecipientsCommitted(EventFanoutCheckpoint checkpoint)
            => checkpoint != null && PendingRecipients(checkpoint).Count == 0;

        public static bool ReadyForProjection(EventFanoutCheckpoint checkpoint)
            => checkpoint != null && checkpoint.EventLogCommitted && checkpoint.FanoutCompleted
                && checkpoint.HeardCommitted && AllRecipientsCommitted(checkpoint)
                && checkpoint.Heard == (checkpoint.CompletedRecipientIds?.Count ?? 0);

        public static bool IsValid(EventFanoutCheckpoint checkpoint)
        {
            if (checkpoint == null || string.IsNullOrWhiteSpace(checkpoint.EventId)
                || checkpoint.EventId.Length > 256 || checkpoint.WorldDate < 0 || checkpoint.AreaId < -1
                || string.IsNullOrWhiteSpace(checkpoint.StoryText)
                || string.IsNullOrWhiteSpace(checkpoint.ProjectionText)
                || checkpoint.ChapterNumber < 0 || checkpoint.Heard < 0 || checkpoint.UpdatedUtcTicks < 0
                || checkpoint.Actions == null || checkpoint.Actions.Count > MaxActions
                || checkpoint.OutcomeOperationIds == null || checkpoint.OutcomeOperationIds.Count > MaxOutcomeOperations
                || checkpoint.RecipientIds == null || checkpoint.RecipientIds.Count > MaxRecipients
                || checkpoint.CompletedRecipientIds == null || checkpoint.CompletedRecipientIds.Count > MaxRecipients)
                return false;
            var recipients = new HashSet<int>();
            int previousRecipient = 0;
            foreach (int id in checkpoint.RecipientIds)
            {
                if (id <= previousRecipient || !recipients.Add(id)) return false;
                previousRecipient = id;
            }
            var completed = new HashSet<int>();
            int previousCompleted = 0;
            foreach (int id in checkpoint.CompletedRecipientIds)
            {
                if (id <= previousCompleted || !recipients.Contains(id) || !completed.Add(id)) return false;
                previousCompleted = id;
            }
            var operations = new HashSet<string>(StringComparer.Ordinal);
            string previousOperation = null;
            foreach (string operationId in checkpoint.OutcomeOperationIds)
            {
                if (!JianghuYouling.Core.Tools.OperationId.IsValid(operationId)
                    || previousOperation != null && string.CompareOrdinal(previousOperation, operationId) >= 0
                    || !operations.Add(operationId)) return false;
                previousOperation = operationId;
            }
            if (!checkpoint.EventLogCommitted && completed.Count > 0) return false;
            if (checkpoint.FanoutCompleted && completed.Count != recipients.Count) return false;
            if (checkpoint.FanoutCompleted && !checkpoint.EventLogCommitted) return false;
            if (!checkpoint.HeardCommitted && checkpoint.Heard != 0) return false;
            if (checkpoint.HeardCommitted && (!checkpoint.EventLogCommitted || !checkpoint.FanoutCompleted
                || checkpoint.Heard != completed.Count)) return false;
            if (checkpoint.ProjectionCommitted && !ReadyForProjection(checkpoint)) return false;
            return true;
        }
    }
}
