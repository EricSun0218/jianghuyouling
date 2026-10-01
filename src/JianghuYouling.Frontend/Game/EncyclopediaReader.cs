using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Game.Views.Encyclopedia;

namespace JianghuYouling
{
    /// <summary>
    /// 百晓册只读渐进披露：先返回真实章节目录，再按模型选择的稳定三级路径载入完整小章节。
    /// 不做向量检索，也不靠用户措辞与正文碰关键词；每个最终章节通常只有数百至数千字。
    /// </summary>
    public static class EncyclopediaReader
    {
        // NPC 只翻阅世界内见闻；灵儿可额外解释启程、交互、扩展与主页等玩家向内容。
        static readonly HashSet<string> GuideCategories = new HashSet<string>(StringComparer.Ordinal)
        {
            "启程", "交互", "扩展", "主页",
        };

        static bool EnsureInit()
        {
            // 第一次查询可能早于本体百科初始化。旧实现无论初始化是否成功都会永久置为
            // “已初始化”，一次时序失败会让本次游戏后续查询全部无载。现在只有真实表
            // 已装载才算成功；未就绪时下一次查询仍会重试。
            try
            {
                EncyclopediaContent ready = EncyclopediaContent.Instance;
                if (ready != null && ready.Count > 0) return true;
            }
            catch { }

            try
            {
                EncyclopediaDataManager.Instance.ReInitialize();
                EncyclopediaContent ready = EncyclopediaContent.Instance;
                return ready != null && ready.Count > 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// path 为空时列一级目录；填写一级目录时直接列出该类全部稳定三级章节路径；
        /// 填写返回的三级路径后载入整章。若三级标题在全书唯一，也允许直接填写该标题。
        /// </summary>
        public static string Browse(string path, bool includeGuideCategories = false,
            int maxChars = 8000)
        {
            if (!EnsureInit()) return "(百晓册尚未装载完成；稍后可再次翻阅。)";
            EncyclopediaContent content;
            try { content = EncyclopediaContent.Instance; }
            catch { return "(百晓册尚未装载完成；稍后可再次翻阅。)"; }
            if (content == null || content.Count <= 0)
                return "(百晓册尚未装载完成；稍后可再次翻阅。)";

            List<string> parts = ParsePath(path);
            if (parts.Count == 0)
                return RenderRootDirectory(content, includeGuideCategories, null);

            string root = ResolveRoot(content, parts[0], includeGuideCategories);
            if (root == null && parts.Count == 1)
            {
                string[] unique = ResolveUniqueThirdPath(content, parts[0], includeGuideCategories);
                if (unique != null) return RenderChapter(content, unique, maxChars);
            }
            if (root == null)
                return RenderRootDirectory(content, includeGuideCategories,
                    "没有这个一级章节「" + parts[0] + "」");

            if (parts.Count == 1)
                return RenderThirdLevelDirectory(content, root, null, null);

            string second = ResolveSecond(content, root, parts[1]);
            if (second == null)
                return RenderThirdLevelDirectory(content, root, null,
                    "「" + root + "」下没有二级章节「" + parts[1] + "」");
            if (parts.Count == 2)
                return RenderThirdLevelDirectory(content, root, second, null);

            string third = ResolveThird(content, root, second, parts[2]);
            if (third == null)
                return RenderThirdLevelDirectory(content, root, second,
                    "「" + root + "·" + second + "」下没有三级章节「" + parts[2] + "」");
            return RenderChapter(content, new[] { root, second, third }, maxChars);
        }

        static string RenderRootDirectory(EncyclopediaContent content, bool includeGuides,
            string warning)
        {
            var roots = new SortedSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < content.Count; i++)
            {
                EncyclopediaContentItem item = SafeGet(content, i);
                if (item == null || string.IsNullOrWhiteSpace(item.Title1)) continue;
                string title = item.Title1.Trim();
                if (!includeGuides && GuideCategories.Contains(title)) continue;
                roots.Add(title);
            }
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(warning)) sb.Append(warning).Append("。\n");
            sb.Append("【百晓册一级目录】\n");
            foreach (string title in roots) sb.Append("- ").Append(title).Append('\n');
            sb.Append("请按问题选择一个一级目录再次翻阅；不要把玩家整句话当作章节路径。");
            return sb.ToString().Trim();
        }

        static string RenderThirdLevelDirectory(EncyclopediaContent content, string root,
            string secondFilter, string warning)
        {
            var paths = new SortedSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < content.Count; i++)
            {
                EncyclopediaContentItem item = SafeGet(content, i);
                if (item == null || !Same(item.Title1, root)
                    || string.IsNullOrWhiteSpace(item.Title2)
                    || string.IsNullOrWhiteSpace(item.Title3)) continue;
                string second = item.Title2.Trim();
                if (!string.IsNullOrWhiteSpace(secondFilter) && !Same(second, secondFilter)) continue;
                paths.Add(root + "·" + second + "·" + item.Title3.Trim());
            }
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(warning)) sb.Append(warning).Append("。\n");
            sb.Append("【百晓册可展开章节】\n");
            foreach (string title in paths) sb.Append("- ").Append(title).Append('\n');
            sb.Append("请选择最符合问题的一条完整路径再次翻阅；下一次会载入该小章节的全部正文。");
            return sb.ToString().Trim();
        }

        static string RenderChapter(EncyclopediaContent content, string[] path, int maxChars)
        {
            if (maxChars < 1000) maxChars = 1000;
            var body = new StringBuilder();
            string lastFourth = null;
            string lastFifth = null;
            for (int i = 0; i < content.Count; i++)
            {
                EncyclopediaContentItem item = SafeGet(content, i);
                if (item == null || !Same(item.Title1, path[0]) || !Same(item.Title2, path[1])
                    || !Same(item.Title3, path[2]) || string.IsNullOrWhiteSpace(item.Content)) continue;
                string text = Strip(item.Content);
                if (string.IsNullOrWhiteSpace(text) || text == "{0}") continue;

                string fourth = CleanTitle(item.Title4);
                string fifth = CleanTitle(item.Title5);
                if (!string.IsNullOrEmpty(fourth) && !Same(lastFourth, fourth))
                {
                    if (body.Length > 0) body.Append("\n\n");
                    body.Append("【").Append(fourth).Append("】");
                    lastFourth = fourth;
                    lastFifth = null;
                }
                if (!string.IsNullOrEmpty(fifth) && !Same(lastFifth, fifth))
                {
                    if (body.Length > 0) body.Append("\n");
                    body.Append("〔").Append(fifth).Append("〕");
                    lastFifth = fifth;
                }
                if (body.Length > 0) body.Append('\n');
                body.Append(text);
                if (body.Length >= maxChars) break;
            }

            if (body.Length == 0)
                return "(章节「" + string.Join("·", path) + "」当前没有可读取的纯文字正文。)";
            bool truncated = body.Length > maxChars;
            string rendered = truncated ? body.ToString(0, maxChars).TrimEnd() + "…" : body.ToString().Trim();
            return "【百晓册章节：" + string.Join("·", path) + "】\n"
                + rendered
                + (truncated ? "\n（章节过长，已在安全上下文上限处截断。）" : "");
        }

        static List<string> ParsePath(string path)
        {
            var result = new List<string>(3);
            string raw = (path ?? string.Empty).Trim();
            if (raw.Length == 0) return result;
            string[] pieces = Regex.Split(raw, "\\s*(?:·|>|＞|/|／|→)\\s*");
            foreach (string piece in pieces)
            {
                string value = CleanTitle(piece);
                if (value.Length > 0) result.Add(value);
                if (result.Count == 3) break;
            }
            return result;
        }

        static string ResolveRoot(EncyclopediaContent content, string requested, bool includeGuides)
        {
            for (int i = 0; i < content.Count; i++)
            {
                EncyclopediaContentItem item = SafeGet(content, i);
                if (item == null || string.IsNullOrWhiteSpace(item.Title1)) continue;
                string title = item.Title1.Trim();
                if (!includeGuides && GuideCategories.Contains(title)) continue;
                if (Same(title, requested)) return title;
            }
            return null;
        }

        static string ResolveSecond(EncyclopediaContent content, string root, string requested)
        {
            for (int i = 0; i < content.Count; i++)
            {
                EncyclopediaContentItem item = SafeGet(content, i);
                if (item != null && Same(item.Title1, root) && Same(item.Title2, requested))
                    return item.Title2.Trim();
            }
            return null;
        }

        static string ResolveThird(EncyclopediaContent content, string root, string second,
            string requested)
        {
            for (int i = 0; i < content.Count; i++)
            {
                EncyclopediaContentItem item = SafeGet(content, i);
                if (item != null && Same(item.Title1, root) && Same(item.Title2, second)
                    && Same(item.Title3, requested)) return item.Title3.Trim();
            }
            return null;
        }

        static string[] ResolveUniqueThirdPath(EncyclopediaContent content, string requested,
            bool includeGuides)
        {
            string[] found = null;
            for (int i = 0; i < content.Count; i++)
            {
                EncyclopediaContentItem item = SafeGet(content, i);
                if (item == null || !Same(item.Title3, requested)
                    || string.IsNullOrWhiteSpace(item.Title1) || string.IsNullOrWhiteSpace(item.Title2)) continue;
                string root = item.Title1.Trim();
                if (!includeGuides && GuideCategories.Contains(root)) continue;
                var candidate = new[] { root, item.Title2.Trim(), item.Title3.Trim() };
                if (found == null) found = candidate;
                else if (!Same(found[0], candidate[0]) || !Same(found[1], candidate[1])) return null;
            }
            return found;
        }

        static EncyclopediaContentItem SafeGet(EncyclopediaContent content, int index)
        {
            try { return content[index]; }
            catch { return null; }
        }

        static bool Same(string left, string right)
            => string.Equals(CleanTitle(left), CleanTitle(right), StringComparison.OrdinalIgnoreCase);

        static string CleanTitle(string value)
            => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Trim('【', '】', '「', '」', '『', '』');

        static string Strip(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            string text = Regex.Replace(value, "<[^>]+>", string.Empty);
            return text.Replace("\\n", "\n").Trim();
        }
    }
}
