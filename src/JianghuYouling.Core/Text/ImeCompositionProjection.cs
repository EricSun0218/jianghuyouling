using System;

namespace JianghuYouling.Core.Text
{
    /// <summary>把 TMP 尚未提交的输入法合成串投影进正文，供布局测量使用。</summary>
    public static class ImeCompositionProjection
    {
        /// <param name="committedText">TMP_InputField.text，不含合成串。</param>
        /// <param name="composition">当前输入法合成串。</param>
        /// <param name="visibleStringPosition">
        /// TMP_InputField.stringPosition；合成期间其 getter 已额外包含 composition.Length。
        /// </param>
        public static string ForMeasurement(string committedText, string composition, int visibleStringPosition)
        {
            string committed = committedText ?? string.Empty;
            string pending = composition ?? string.Empty;
            if (pending.Length == 0) return committed;

            int insertion = visibleStringPosition - pending.Length;
            insertion = Math.Max(0, Math.Min(committed.Length, insertion));
            return committed.Insert(insertion, pending);
        }
    }
}
