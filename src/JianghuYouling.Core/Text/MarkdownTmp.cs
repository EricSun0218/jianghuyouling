using System.Text;
using System.Text.RegularExpressions;

namespace JianghuYouling.Core.Text
{
    /// <summary>
    /// 把模型可能吐出的 Markdown「智能」归一成适合 NPC 说话、又能被 TextMeshPro 富文本渲染的文本:
    ///   · **加粗** / __加粗__ → &lt;b&gt;…&lt;/b&gt;,*斜体* / _斜体_ → &lt;i&gt;…&lt;/i&gt;
    ///   · # 标题 → 去掉井号只留文字(说话不需要大标题)
    ///   · - / * / + 列表项 → 行首「· 」;> 引用、``` 代码围栏、--- 分隔线 → 去掉标记只留内容
    ///   · `行内代码` → 去掉反引号;[文字](链接) → 只留文字
    /// 不动普通正文;TMP richText=true 下即可直接显示。纯逻辑、可单测。
    /// </summary>
    public static class MarkdownTmp
    {
        static readonly Regex Fence = new Regex(@"^\s*```.*$", RegexOptions.Compiled);
        static readonly Regex Header = new Regex(@"^\s{0,3}#{1,6}\s+(.*)$", RegexOptions.Compiled);
        static readonly Regex Hr = new Regex(@"^\s*([-*_])(\s*\1){2,}\s*$", RegexOptions.Compiled);
        static readonly Regex Quote = new Regex(@"^\s{0,3}>\s?", RegexOptions.Compiled);
        static readonly Regex UList = new Regex(@"^(\s*)[-*+]\s+(.*)$", RegexOptions.Compiled);

        static readonly Regex BoldA = new Regex(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
        static readonly Regex BoldB = new Regex(@"__(.+?)__", RegexOptions.Compiled);
        static readonly Regex ItalicA = new Regex(@"(?<!\*)\*(?!\s)(.+?)(?<!\s)\*(?!\*)", RegexOptions.Compiled);
        static readonly Regex ItalicB = new Regex(@"(?<![A-Za-z0-9_])_(?!\s)(.+?)(?<!\s)_(?![A-Za-z0-9_])", RegexOptions.Compiled);
        static readonly Regex Code = new Regex(@"`([^`]+?)`", RegexOptions.Compiled);
        static readonly Regex Link = new Regex(@"\[([^\]]+?)\]\([^)]*?\)", RegexOptions.Compiled);
        static readonly Regex ManyBlank = new Regex(@"\n{3,}", RegexOptions.Compiled);

        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace("\r\n", "\n").Replace("\r", "\n");

            // —— 块级:逐行处理(去围栏/标题/分隔线/引用,列表项转「· 」)——
            var lines = s.Split('\n');
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                string ln = lines[i];
                if (Fence.IsMatch(ln)) continue;            // ``` 围栏整行丢弃,只留里面的内容
                if (Hr.IsMatch(ln)) continue;               // --- / *** 分隔线整行丢弃
                Match h = Header.Match(ln);
                if (h.Success) ln = h.Groups[1].Value;      // # 标题 → 纯文字
                ln = Quote.Replace(ln, "");                 // > 引用 → 去标记
                Match u = UList.Match(ln);
                if (u.Success) ln = u.Groups[1].Value + "· " + u.Groups[2].Value;   // 无序列表 → 行首「· 」
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(ln);
            }
            s = sb.ToString();

            // —— 行内:加粗先于斜体(免 ** 被 * 抢匹配),再去行内代码与链接 ——
            s = BoldA.Replace(s, "<b>$1</b>");
            s = BoldB.Replace(s, "<b>$1</b>");
            s = ItalicA.Replace(s, "<i>$1</i>");
            s = ItalicB.Replace(s, "<i>$1</i>");
            s = Code.Replace(s, "$1");
            s = Link.Replace(s, "$1");

            s = ManyBlank.Replace(s, "\n\n");
            return s.Trim('\n');
        }
    }
}
