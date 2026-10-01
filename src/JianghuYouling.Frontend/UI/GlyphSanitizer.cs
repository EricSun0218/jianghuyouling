using System.Text;
using TMPro;
using UnityEngine;

namespace JianghuYouling
{
    /// <summary>把要显示的文本里游戏字体渲染不出的字符滤掉,避免显示成「口」(豆腐块/tofu)。
    /// 用游戏自己的 TMP 字体 HasCharacter(含 fallback)判定;字体未就绪时原样返回(宁可不滤,绝不误删整段)。
    /// 成对的 emoji(代理对/星补字符)整对剔除。空白/换行一律保留。</summary>
    public static class GlyphSanitizer
    {
        static TMP_FontAsset _font;

        /// <summary>由 UI 在拿到聊天字体时灌进来,免去自行查找。</summary>
        public static void SetFont(TMP_FontAsset f) { if (f != null) _font = f; }

        static TMP_FontAsset ResolveFont()
        {
            if (_font != null) return _font;
            try
            {
                foreach (var t in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
                    if (t != null && t.font != null) { _font = t.font; break; }
                if (_font == null)
                {
                    var all = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                    if (all != null && all.Length > 0) _font = all[0];
                }
            }
            catch { }
            return _font;
        }

        /// <summary>滤掉字体渲染不出的字符。字体不可用时原样返回(不冒误删整段的险)。</summary>
        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = StripInternalExecutionTags(s);
            // The game's GB2312 UI font does not contain these common semantic arrows.
            // Preserve their meaning instead of letting TMP emit one warning per layout rebuild.
            s = s.Replace("↔", "互换").Replace("→", "至").Replace("←", "自");
            // Older durable journals stored relation assets as protocol enums. Translate the complete
            // legacy result phrase at the display boundary; persistence remains verbatim and auditable.
            s = s.Replace("结成关系:befriend", "结成挚友之谊")
                .Replace("结成关系:best_friend", "结成挚友之谊")
                .Replace("结成关系:friend", "结成挚友之谊")
                .Replace("结成关系:swear_sibling", "结成结义之盟")
                .Replace("结成关系:sworn_sibling", "结成结义之盟")
                .Replace("结成关系:sworn", "结成结义之盟")
                .Replace("结成关系:take_disciple", "结成师徒之礼")
                .Replace("结成关系:apprentice", "结成师徒之礼")
                .Replace("结成关系:mentor", "结成师徒之礼")
                .Replace("结成关系:adored", "结成相恋之约")
                .Replace("结成关系:lover", "结成相恋之约")
                .Replace("结成关系:husband_or_wife", "结成夫妻之约")
                .Replace("结成关系:spouse", "结成夫妻之约")
                .Replace("解除了关系:befriend", "解除了挚友之谊")
                .Replace("解除了关系:best_friend", "解除了挚友之谊")
                .Replace("解除了关系:friend", "解除了挚友之谊")
                .Replace("解除了关系:swear_sibling", "解除了结义之盟")
                .Replace("解除了关系:sworn_sibling", "解除了结义之盟")
                .Replace("解除了关系:sworn", "解除了结义之盟")
                .Replace("解除了关系:take_disciple", "解除了师徒之礼")
                .Replace("解除了关系:apprentice", "解除了师徒之礼")
                .Replace("解除了关系:mentor", "解除了师徒之礼")
                .Replace("解除了关系:adored", "解除了相恋之约")
                .Replace("解除了关系:lover", "解除了相恋之约")
                .Replace("解除了关系:husband_or_wife", "解除了夫妻之约")
                .Replace("解除了关系:spouse", "解除了夫妻之约");
            var font = ResolveFont();
            if (font == null) return s;   // 字体没就绪:宁可不滤
            var sb = new StringBuilder(s.Length);
            bool changed = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\n' || c == '\r' || c == '\t' || c == ' ' || c == '　') { sb.Append(c); continue; }
                // 代理对(emoji/星补字符):游戏字体几乎必无 → 整对剔除
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
                    changed = true; continue;
                }
                if (char.IsLowSurrogate(c)) { changed = true; continue; }
                bool ok;
                try { ok = font.HasCharacter(c, true); } catch { ok = true; }
                if (ok) sb.Append(c); else changed = true;
            }
            return changed ? sb.ToString() : s;
        }

        /// <summary>移除只允许出现在模型上下文中的动作历史协议标记。</summary>
        internal static string StripInternalExecutionTags(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            const string markerPattern = @"\[\s*JHYL_(?:DONE|TOOL_RESULTS)\s*(?::[^\]\r\n]*)?\]";
            if (!System.Text.RegularExpressions.Regex.IsMatch(text, markerPattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return text;
            string visible = System.Text.RegularExpressions.Regex.Replace(text,
                markerPattern,
                string.Empty,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            visible = System.Text.RegularExpressions.Regex.Replace(visible,
                @"(?:\r?\n)[ \t]*(?:\r?\n[ \t]*){2,}", "\n\n");
            return visible.Trim();
        }
    }
}
