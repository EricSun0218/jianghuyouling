using System;
using System.Collections.Generic;

namespace JianghuYouling.Core.Behavior
{
    public sealed class RecipientSecretSelection
    {
        public int DisplayIndex { get; set; }
        public int SecretId { get; set; }
        public string Text { get; set; }
    }

    /// <summary>
    /// Recipient-scoped authority for a model-visible secret list. A display index is only
    /// meaningful inside the actor/recipient snapshot that produced it; freezing the exact
    /// secret id here prevents another recipient or a reordered later list from reusing it.
    /// </summary>
    public sealed class RecipientSecretSelectionLedger
    {
        private readonly Dictionary<string, Dictionary<int, RecipientSecretSelection>> _snapshots
            = new Dictionary<string, Dictionary<int, RecipientSecretSelection>>(
                StringComparer.Ordinal);

        private static string Key(int actorId, int recipientId)
            => actorId + ">" + recipientId;

        public bool Replace(
            int actorId,
            int recipientId,
            IEnumerable<RecipientSecretSelection> selections)
        {
            if (actorId <= 0 || recipientId <= 0 || actorId == recipientId)
                return false;
            var snapshot = new Dictionary<int, RecipientSecretSelection>();
            if (selections != null)
                foreach (RecipientSecretSelection selection in selections)
                {
                    if (selection == null || selection.DisplayIndex <= 0
                        || selection.SecretId < 0 || snapshot.ContainsKey(selection.DisplayIndex))
                        continue;
                    snapshot[selection.DisplayIndex] = new RecipientSecretSelection
                    {
                        DisplayIndex = selection.DisplayIndex,
                        SecretId = selection.SecretId,
                        Text = selection.Text ?? string.Empty,
                    };
                }
            _snapshots[Key(actorId, recipientId)] = snapshot;
            return true;
        }

        public bool TryFreeze(
            int actorId,
            int recipientId,
            int displayIndex,
            out RecipientSecretSelection selection)
        {
            selection = null;
            if (displayIndex <= 0
                || !_snapshots.TryGetValue(Key(actorId, recipientId), out var snapshot)
                || !snapshot.TryGetValue(displayIndex, out RecipientSecretSelection found))
                return false;
            selection = new RecipientSecretSelection
            {
                DisplayIndex = found.DisplayIndex,
                SecretId = found.SecretId,
                Text = found.Text,
            };
            return true;
        }

        public void Invalidate(int actorId, int recipientId)
            => _snapshots.Remove(Key(actorId, recipientId));

        public void Clear()
            => _snapshots.Clear();
    }
}
