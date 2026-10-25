namespace JianghuYouling.Core.Behavior
{
    /// <summary>Session-only, at-most-once monthly scheduling. Only the latest waiting month survives.</summary>
    public sealed class MonthlyRunQueue
    {
        public int LastClaimedDate { get; private set; } = -1;
        public int RunningDate { get; private set; } = -1;
        private int _waitingDate = -1;
        private int _waitingTaiwuId;

        public void Reset(int currentDate)
        {
            LastClaimedDate = currentDate;
            RunningDate = -1;
            _waitingDate = -1;
            _waitingTaiwuId = 0;
        }

        public bool Claim(int taiwuId, int date)
        {
            if (taiwuId <= 0 || date < 0 || date <= LastClaimedDate) return false;
            LastClaimedDate = date;
            _waitingDate = date;
            _waitingTaiwuId = taiwuId;
            return true;
        }

        public bool TryStart(out int taiwuId, out int date)
        {
            taiwuId = 0;
            date = -1;
            if (RunningDate >= 0 || _waitingDate < 0) return false;
            date = RunningDate = _waitingDate;
            taiwuId = _waitingTaiwuId;
            _waitingDate = -1;
            return true;
        }

        public void Finish(int date)
        {
            if (RunningDate == date) RunningDate = -1;
        }
    }
}
