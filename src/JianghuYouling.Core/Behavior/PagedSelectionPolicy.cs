using System;
using System.Collections.Generic;

namespace JianghuYouling.Core.Behavior
{
    /// <summary>分页多选的纯状态策略：本页全选不会改动其它页，且全局不超过席位上限。</summary>
    public static class PagedSelectionPolicy
    {
        public static void ToggleVisible(IList<bool> selection, IEnumerable<int> visibleIndices,
            int selectionLimit)
        {
            if (selection == null || visibleIndices == null) return;
            selectionLimit = Math.Max(0, Math.Min(selectionLimit, selection.Count));
            var visible = new List<int>();
            var seen = new HashSet<int>();
            foreach (int index in visibleIndices)
                if (index >= 0 && index < selection.Count && seen.Add(index)) visible.Add(index);
            if (visible.Count == 0) return;

            bool allVisibleSelected = true;
            int visibleSelected = 0;
            foreach (int index in visible)
            {
                if (selection[index]) visibleSelected++;
                else allVisibleSelected = false;
            }
            int selected = 0;
            for (int i = 0; i < selection.Count; i++) if (selection[i]) selected++;
            // 通常一页人数远大于席位上限，永远不可能“整页都选中”。第一次点击
            // 填满全局席位后，第二次点击应把本页已选项清掉，而不是因无余量而无响应。
            bool clearVisible = allVisibleSelected
                || visibleSelected > 0 && selected >= selectionLimit;
            if (clearVisible)
            {
                foreach (int index in visible) selection[index] = false;
                return;
            }

            foreach (int index in visible)
            {
                if (selected >= selectionLimit) break;
                if (selection[index]) continue;
                selection[index] = true;
                selected++;
            }
        }
    }
}
