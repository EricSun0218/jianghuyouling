using System.Collections.Generic;
using System.Threading;

namespace JianghuYouling.Core.Behavior
{
    /// <summary>
    /// Thread-safe coordination state shared by the monthly event and companion lanes.
    /// Planning launches independently; only overlapping mutations wait for the event
    /// participant set and checkpoint.
    /// </summary>
    public sealed class MonthlyLaneCoordinator
    {
        private readonly object _gate = new object();
        private readonly bool _runEvent;
        private readonly HashSet<int> _eventParticipants = new HashSet<int>();
        private int _companionStarted;
        private bool _participantsKnown;
        private bool _mutationCheckpointReached;
        private bool _isolationIdentitiesReliable = true;

        public MonthlyLaneCoordinator(bool runEvent)
        {
            _runEvent = runEvent;
            _participantsKnown = !runEvent;
            _mutationCheckpointReached = !runEvent;
        }

        public bool CompanionStarted => Volatile.Read(ref _companionStarted) != 0;

        public bool TryStartCompanion(bool enabled)
            => enabled && Interlocked.Exchange(ref _companionStarted, 1) == 0;

        public void SetEventParticipants(IEnumerable<int> ids)
        {
            lock (_gate)
            {
                if (ids != null)
                    foreach (int id in ids)
                        if (id > 0) _eventParticipants.Add(id);
                _participantsKnown = true;
            }
        }

        public int EventParticipantCount
        {
            get { lock (_gate) return _eventParticipants.Count; }
        }

        public void MarkMutationCheckpoint()
        {
            lock (_gate)
            {
                _mutationCheckpointReached = true;
                _isolationIdentitiesReliable = true;
            }
        }

        public void MarkMutationIsolationUnresolved(IEnumerable<int> ids,
            bool identitiesReliable)
        {
            lock (_gate)
            {
                if (ids != null)
                    foreach (int id in ids)
                        if (id > 0) _eventParticipants.Add(id);
                _participantsKnown = _participantsKnown || identitiesReliable;
                _isolationIdentitiesReliable = identitiesReliable;
            }
        }

        public bool CanCompanionMutate(int actorId, IReadOnlyCollection<int> targetIds)
        {
            lock (_gate)
                return MonthlyConflictPolicy.CanCompanionMutate(_runEvent,
                    _mutationCheckpointReached, _participantsKnown,
                    _isolationIdentitiesReliable,
                    _eventParticipants, actorId, targetIds);
        }
    }
}
