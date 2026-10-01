using System;
using System.Text;

namespace JianghuYouling.Core.Text
{
    /// <summary>
    /// Applies a clipboard insertion as one linear-time document edit.  TMP_InputField's native
    /// Append(string) inserts and announces every character separately; that becomes quadratic
    /// for the 250k/500k character persona and world-book editors.
    /// </summary>
    public static class LongTextBulkEdit
    {
        public sealed class Result
        {
            public string Text { get; internal set; }
            public int Caret { get; internal set; }
            public int InsertedCharacters { get; internal set; }
            public bool Changed { get; internal set; }
        }

        public static Result Apply(string current, int anchor, int focus, string clipboard, int characterLimit)
        {
            current = current ?? string.Empty;
            clipboard = clipboard ?? string.Empty;
            anchor = Clamp(anchor, 0, current.Length);
            focus = Clamp(focus, 0, current.Length);
            int start = Math.Min(anchor, focus);
            int end = Math.Max(anchor, focus);
            int retainedLength = current.Length - (end - start);
            int capacity = characterLimit > 0 ? Math.Max(0, characterLimit - retainedLength) : int.MaxValue;

            string insertion = FilterClipboard(clipboard, capacity);
            if (insertion.Length == 0)
            {
                // Native TMP ignores a clipboard containing only rejected control characters;
                // it does not delete the current selection in that case.
                return new Result { Text = current, Caret = focus, Changed = false };
            }

            var combined = new StringBuilder(retainedLength + insertion.Length);
            if (start > 0) combined.Append(current, 0, start);
            combined.Append(insertion);
            if (end < current.Length) combined.Append(current, end, current.Length - end);
            return new Result
            {
                Text = combined.ToString(),
                Caret = start + insertion.Length,
                InsertedCharacters = insertion.Length,
                Changed = true,
            };
        }

        private static string FilterClipboard(string value, int capacity)
        {
            if (string.IsNullOrEmpty(value) || capacity <= 0) return string.Empty;
            int target = Math.Min(value.Length, capacity);
            var accepted = new StringBuilder(target);
            for (int i = 0; i < value.Length && accepted.Length < capacity; i++)
            {
                char c = value[i];
                if (c < ' ' && c != '\t' && c != '\r' && c != '\n') continue;
                if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    // A surrogate pair cannot be split.  Skip the whole pair when only one UTF-16
                    // slot remains, but keep scanning because a later BMP character may still fit.
                    if (accepted.Length + 2 > capacity) { i++; continue; }
                    accepted.Append(c).Append(value[++i]);
                    continue;
                }
                // Reject isolated surrogate halves rather than leave an invalid UTF-16 document.
                if (char.IsSurrogate(c)) continue;
                accepted.Append(c);
            }
            return accepted.ToString();
        }

        private static int Clamp(int value, int min, int max)
            => value < min ? min : value > max ? max : value;
    }
}
