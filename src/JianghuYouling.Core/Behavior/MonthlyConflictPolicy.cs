using System.Collections.Generic;

namespace JianghuYouling.Core.Behavior
{
    /// <summary>
    /// Coordinates the two independent monthly agent lanes without turning event prose
    /// generation into a global write lock. Only actors/targets that overlap the event
    /// roster wait for its authoritative mutations to reach the checkpoint.
    /// </summary>
    public static class MonthlyConflictPolicy
    {
        public static bool CanCompanionMutate(bool runEvent,
            bool eventMutationCheckpointReached, bool eventParticipantsKnown,
            bool eventIsolationIdentitiesReliable,
            ISet<int> eventParticipantIds, int actorId, IReadOnlyCollection<int> targetIds)
        {
            if (!runEvent || eventMutationCheckpointReached) return true;
            // UI completion is deliberately not an execution milestone. If a prepared
            // operation is still pending, a late backend receipt may mutate the world
            // after the visible event coroutine has returned. Keep the gate fail-closed
            // globally when its durable actor/target identities cannot be proven.
            if (!eventParticipantsKnown || !eventIsolationIdentitiesReliable) return false;
            if (actorId > 0 && eventParticipantIds != null && eventParticipantIds.Contains(actorId))
                return false;

            if (targetIds != null && eventParticipantIds != null)
            {
                foreach (int targetId in targetIds)
                    if (targetId > 0 && eventParticipantIds.Contains(targetId)) return false;
            }
            return true;
        }
    }
}
