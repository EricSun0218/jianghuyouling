using System;

namespace JianghuYouling.Core.Commission
{
    /// <summary>
    /// 成功清巢按发生顺序记录难度；接单基线后的第一条记录决定这单奖励。
    /// 分页仅限制单个存档字符串的长度，不丢弃尚未领奖的历史。
    /// </summary>
    public static class OutlawNestProgressPolicy
    {
        public const string Kind = "clear_outlaw_nest";
        public const int PageSize = 4096;

        public static bool IsOutlawNest(int adventureCoreId, int nestAdventureId, int nestType)
            => adventureCoreId > 0 && adventureCoreId == nestAdventureId && nestType == 0;

        public static int RewardGradeForDifficulty(int difficulty)
            => difficulty >= 0 && difficulty <= 8 ? difficulty : -1;

        public static bool TryFirstCompletionGrade(int count, int baseline,
            Func<int, string> readPage, out int grade)
        {
            grade = -1;
            if (count < 0 || baseline < 0 || baseline >= count || readPage == null) return false;
            string page = readPage(baseline / PageSize);
            int offset = baseline % PageSize;
            if (page == null || offset >= page.Length) return false;
            grade = RewardGradeForDifficulty(page[offset] - '0');
            return grade >= 0;
        }
    }
}
