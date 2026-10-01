using System;

namespace JianghuYouling.Core.Commission
{
    /// <summary>
    /// 委托进度的唯一比较口径。所有增量任务都用 long 做中间计算，再钳制回 int，
    /// 避免接近本体资源上限时溢出；完成条件始终是达到或超过目标。
    /// </summary>
    public static class CommissionProgressPolicy
    {
        public static int ProgressValue(string kind, int currentValue, int baselineValue)
        {
            long progress = string.Equals(kind, "deliver_resource",
                    StringComparison.Ordinal)
                ? Math.Max(0L, currentValue)
                : Math.Max(0L, (long)currentValue - Math.Max(0L, baselineValue));
            return progress > int.MaxValue ? int.MaxValue : (int)progress;
        }

        public static int TargetValue(string kind, int baselineValue, int amount)
        {
            if (string.Equals(kind, "deliver_resource",
                    StringComparison.Ordinal))
                return Math.Max(0, amount);
            long target = Math.Max(0L, baselineValue) + Math.Max(0L, amount);
            return target > int.MaxValue ? int.MaxValue : (int)target;
        }

        public static bool IsCompleted(string kind, int currentValue, int baselineValue,
            int amount)
            => amount > 0 && ProgressValue(kind, currentValue, baselineValue) >= amount;
    }
}
