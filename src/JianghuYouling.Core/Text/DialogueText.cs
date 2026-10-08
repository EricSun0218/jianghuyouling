using System.Collections.Generic;
using System.Text;

namespace JianghuYouling.Core.Text
{
    /// <summary>The shared narration/dialogue boundary used by chat colors and speech selection.</summary>
    public static class DialogueText
    {
        readonly struct TextSpan
        {
            public readonly int Start, Length;
            public readonly bool IsDialogue;

            public TextSpan(int start, int length, bool isDialogue)
            {
                Start = start;
                Length = length;
                IsDialogue = isDialogue;
            }
        }

        // Preserve the chat formatter's exact quote rules, including nested and unfinished
        // dialogue across line breaks. This is presentation syntax, not a semantic guess.
        static IEnumerable<TextSpan> Spans(string text)
        {
            var closers = new Stack<char>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                bool openingAsciiQuote = ch == '"'
                    && (closers.Count == 0 || closers.Peek() != '"');
                if (ch == '「' || ch == '『' || ch == '“' || openingAsciiQuote)
                {
                    if (closers.Count == 0)
                    {
                        if (i > start) yield return new TextSpan(start, i - start, false);
                        start = i;
                    }
                    closers.Push(ch == '「' ? '」' : ch == '『' ? '』' : ch == '“' ? '”' : '"');
                    continue;
                }
                if (closers.Count == 0 || ch != closers.Peek()) continue;
                closers.Pop();
                if (closers.Count != 0) continue;
                yield return new TextSpan(start, i + 1 - start, true);
                start = i + 1;
            }
            if (start < text.Length)
                yield return new TextSpan(start, text.Length - start, closers.Count > 0);
        }

        public static string Colorize(string text, string narrationColor, string dialogueColor)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var output = new StringBuilder(text.Length + 96);
            output.Append("<color=").Append(narrationColor).Append('>');
            foreach (TextSpan span in Spans(text))
            {
                if (span.IsDialogue)
                    output.Append("</color><color=").Append(dialogueColor).Append('>');
                output.Append(text, span.Start, span.Length);
                if (span.IsDialogue)
                    output.Append("</color><color=").Append(narrationColor).Append('>');
            }
            output.Append("</color>");
            return output.ToString();
        }

        /// <summary>Keep the same quoted spans that receive the dialogue color, in their original order.</summary>
        public static string ExtractDialogue(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var output = new StringBuilder();
            foreach (TextSpan span in Spans(text))
            {
                if (!span.IsDialogue) continue;
                // Keep separate utterances separate after the intervening narration is removed.
                if (output.Length > 0) output.Append('\n');
                output.Append(text, span.Start, span.Length);
            }
            return output.ToString();
        }
    }
}
