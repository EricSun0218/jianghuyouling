namespace JianghuYouling.Core.Behavior
{
    public static class MonthlyDangerActionPolicy
    {
        // Only these existing whole-month actions represent seeking out the target.
        // This flag is supplied by the monthly executor, never by model tool arguments.
        public static bool RequiresCoLocation(string operation, int monthlyOnlyPurity)
            => monthlyOnlyPurity != 1
                || (operation != "kill" && operation != "capture" && operation != "poison");
    }
}
