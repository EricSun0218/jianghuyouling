using System;
using System.Collections.Generic;

namespace JianghuYouling.Core.Behavior
{
    public static class ConversationNavigationOrder
    {
        public static int Compare(bool pinnedA, long activityA, string titleA, string identityA,
            bool pinnedB, long activityB, string titleB, string identityB)
        {
            int order = pinnedB.CompareTo(pinnedA);
            if (order == 0) order = activityB.CompareTo(activityA);
            if (order == 0) order = string.Compare(titleA, titleB, StringComparison.Ordinal);
            return order != 0 ? order : string.Compare(identityA, identityB, StringComparison.Ordinal);
        }

        // Shared by the fast activity update and tests; never assumes that newest means first.
        public static void InsertSorted<T>(IList<T> sorted, T entry, Comparison<T> compare)
        {
            int first = 0, last = sorted.Count;
            while (first < last)
            {
                int middle = first + (last - first) / 2;
                if (compare(sorted[middle], entry) <= 0) first = middle + 1;
                else last = middle;
            }
            sorted.Insert(first, entry);
        }
    }
}
