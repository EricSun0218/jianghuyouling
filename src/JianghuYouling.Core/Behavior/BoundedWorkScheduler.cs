using System;
using System.Collections.Generic;

namespace JianghuYouling.Core.Behavior
{
    /// <summary>
    /// Thread-safe admission state for a bounded set of child jobs. Admission, completion and
    /// queued retirement are explicit so cancellation cannot leave never-started work pending.
    /// </summary>
    public sealed class BoundedWorkScheduler
    {
        private readonly object _gate = new object();
        private readonly int _total;
        private readonly int _limit;
        private int _next;
        private int _active;
        private int _completed;
        private int _retired;
        private int _peakActive;

        public BoundedWorkScheduler(int total, int limit)
        {
            if (total < 0) throw new ArgumentOutOfRangeException(nameof(total));
            if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
            _total = total;
            _limit = limit;
        }

        public int Pending
        {
            get { lock (_gate) return Math.Max(0, _total - _completed - _retired); }
        }

        public int Active
        {
            get { lock (_gate) return _active; }
        }

        public int PeakActive
        {
            get { lock (_gate) return _peakActive; }
        }

        public int Completed
        {
            get { lock (_gate) return _completed; }
        }

        public IReadOnlyList<int> AdmitAvailable(bool stillCurrent, int maxAdmissions = int.MaxValue)
        {
            var admitted = new List<int>();
            if (!stillCurrent || maxAdmissions <= 0) return admitted;
            lock (_gate)
            {
                while (_active < _limit && _next < _total && admitted.Count < maxAdmissions)
                {
                    admitted.Add(_next++);
                    _active++;
                    if (_active > _peakActive) _peakActive = _active;
                }
            }
            return admitted;
        }

        public bool CompleteOne()
        {
            lock (_gate)
            {
                if (_active <= 0) return false;
                _active--;
                _completed++;
                return true;
            }
        }

        public int RetireQueued()
        {
            lock (_gate)
            {
                int retired = Math.Max(0, _total - _next);
                _next = _total;
                _retired += retired;
                return retired;
            }
        }
    }

    /// <summary>
    /// Production callback pump around <see cref="BoundedWorkScheduler"/>. The same code path
    /// owns initial admission, rolling refill and launch-failure slot release, which makes it
    /// possible to exercise the real dispatch ordering without Unity coroutines.
    /// </summary>
    public sealed class BoundedWorkDispatcher
    {
        private readonly BoundedWorkScheduler _scheduler;

        public BoundedWorkDispatcher(int total, int limit)
        {
            _scheduler = new BoundedWorkScheduler(total, limit);
        }

        public int Pending => _scheduler.Pending;
        public int Active => _scheduler.Active;
        public int PeakActive => _scheduler.PeakActive;
        public int Completed => _scheduler.Completed;

        public void Pump(bool stillCurrent, Action<int> start,
            Action<int, Exception> onLaunchFailure = null, int maxStarts = int.MaxValue)
        {
            if (start == null) throw new ArgumentNullException(nameof(start));
            if (maxStarts <= 0) return;
            foreach (int index in _scheduler.AdmitAvailable(stillCurrent, maxStarts))
            {
                try { start(index); }
                catch (Exception error)
                {
                    _scheduler.CompleteOne();
                    onLaunchFailure?.Invoke(index, error);
                }
            }
        }

        public bool CompleteOne() => _scheduler.CompleteOne();

        public int RetireQueued() => _scheduler.RetireQueued();
    }
}
