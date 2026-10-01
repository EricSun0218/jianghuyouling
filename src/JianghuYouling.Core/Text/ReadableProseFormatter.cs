using System;
using System.Text;

namespace JianghuYouling.Core.Text
{
    /// <summary>
    /// 给模型偶发返回的“超长单段”补上纯排版分段。只在正文完全没有换行且足够长时生效，
    /// 优先在句末断开，不改字词、标点或既有段落，因而不会覆盖玩家设定的文风内容。
    /// </summary>
    public static class ReadableProseFormatter
    {
        public const int MinimumSingleParagraphChars = 180;
        private const int TargetParagraphChars = 96;
        private const int MaximumParagraphChars = 180;
        private const int MinimumRemainderChars = 36;

        public static string EnsureParagraphs(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length < MinimumSingleParagraphChars
                || value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0)
                return value;

            var output = new StringBuilder(value.Length + 16);
            int paragraphStart = 0;
            int lastSentenceEnd = -1;
            int lastClauseEnd = -1;
            int inserted = 0;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (IsSentenceEnd(c)) lastSentenceEnd = IncludeClosingPunctuation(value, i);
                else if (IsClauseEnd(c)) lastClauseEnd = IncludeClosingPunctuation(value, i);

                int currentLength = i - paragraphStart + 1;
                int breakAt = -1;
                if (currentLength >= TargetParagraphChars && lastSentenceEnd >= paragraphStart + 40)
                    breakAt = lastSentenceEnd;
                else if (currentLength >= MaximumParagraphChars && lastClauseEnd >= paragraphStart + 64)
                    breakAt = lastClauseEnd;

                if (breakAt < paragraphStart || value.Length - breakAt - 1 < MinimumRemainderChars)
                    continue;

                output.Append(value, paragraphStart, breakAt - paragraphStart + 1).Append("\n\n");
                paragraphStart = breakAt + 1;
                i = paragraphStart - 1;
                lastSentenceEnd = -1;
                lastClauseEnd = -1;
                inserted++;
            }

            if (inserted == 0) return value;
            output.Append(value, paragraphStart, value.Length - paragraphStart);
            return output.ToString();
        }

        private static bool IsSentenceEnd(char c)
            => c == '。' || c == '！' || c == '？' || c == '!' || c == '?';

        private static bool IsClauseEnd(char c)
            => c == '；' || c == ';' || c == '，' || c == ',' || c == '：' || c == ':';

        private static int IncludeClosingPunctuation(string value, int index)
        {
            int i = index;
            while (i + 1 < value.Length && IsClosingPunctuation(value[i + 1])) i++;
            return i;
        }

        private static bool IsClosingPunctuation(char c)
            => c == '”' || c == '’' || c == '」' || c == '』' || c == '》'
                || c == '）' || c == ')' || c == '】' || c == ']';
    }
}
