using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace JianghuYouling.Core.Persistence
{
    /// <summary>Current-run event delivery data; never saved as a resumable checkpoint.</summary>
    public sealed class MonthlyEventDelivery
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
        public int ChapterNumber;
        public bool Finale;
        public int Heard;
    }

    public static class EventFanoutPolicy
    {
        public const int MaxRecipients = 512;

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

    }
}
