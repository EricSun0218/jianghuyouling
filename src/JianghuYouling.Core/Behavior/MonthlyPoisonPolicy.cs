namespace JianghuYouling.Core.Behavior
{
    /// <summary>
    /// Pure policy shared by the live backend and the offline harness.
    /// The game's poison values are clamped to 0..25000.
    /// </summary>
    public static class MonthlyPoisonPolicy
    {
        public const int Increment = 60;
        public const int MaxValue = 25000;

        public static bool TryGetNextValue(int current, out int next)
        {
            int normalized = current < 0 ? 0 : current;
            if (normalized >= MaxValue)
            {
                next = MaxValue;
                return false;
            }

            next = System.Math.Min(MaxValue, normalized + Increment);
            return next > normalized;
        }
    }
}
