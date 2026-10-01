using System;

namespace JianghuYouling.Core.Commission
{
    /// <summary>委托资源奖励的纯品级曲线。0=九品，8=一品。</summary>
    public static class CommissionRewardPolicy
    {
        public const int MinGrade = 0;
        public const int MaxGrade = 8;
        private static readonly int[] BaseAmounts =
        {
            600, 1000, 1700, 2800, 4500, 7000, 11000, 18000, 30000,
        };

        public static int BaseResourceAmount(int grade, int resourceType)
        {
            if (grade < MinGrade || grade > MaxGrade)
                throw new ArgumentOutOfRangeException(nameof(grade));
            if (resourceType < 0 || resourceType > 7)
                throw new ArgumentOutOfRangeException(nameof(resourceType));
            int amount = BaseAmounts[grade];
            if (resourceType == 6) return amount * 4; // 银钱
            if (resourceType == 7) return amount * 2; // 威望
            return amount;
        }
    }
}
