using System.Security.Cryptography;
using System.Text;

namespace JianghuYouling.Core.Prompt
{
    /// <summary>世界书进阶解析(关键词词条 / 作者注 / 出话前指令):
    /// · 普通行 = 常驻背景设定(一直注入,与旧版完全兼容);
    /// · 以 @ 起头 = 按关键词触发的词条「@关键词1,关键词2:内容」——仅当某关键词出现在近期对话里才注入
    ///   (可放很多设定而不爆 token,要用到才上场,即 Lorebook);
    /// · 以 ! 起头 = 临场铁令「!内容」——注入到历史之后、出话之前(深度注入 / Post-History,遵循度最高,
    ///   适合「无论如何不能透露X」「始终用文言」这类硬约束)。
    /// 纯逻辑,无 Unity 依赖,可独立测试。</summary>
    public static class WorldBookFilter
    {
        /// <summary>
        /// 当前生效世界书的短内容指纹。它只随正文变化，用来让已有长对话明确知道
        /// “旧历史里的世界设定已经过期”；不包含月份、角色状态等动态内容。
        /// </summary>
        public static string ContentFingerprint(string effectiveWorldBook)
        {
            string normalized = (effectiveWorldBook ?? "")
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Trim();
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                var hex = new StringBuilder(16);
                for (int i = 0; i < 8; i++) hex.Append(hash[i].ToString("x2"));
                return "wb1-" + hex;
            }
        }

        public sealed class Result
        {
            public string StableBackground = "";  // 普通常驻行：稳定前缀，可参与 provider prompt cache
            public string TriggeredBackground = ""; // 本轮 @/@@ 命中内容：动态尾，不得夹在人物圣经前破坏缓存
            public string Background = "";        // 兼容旧调用：Stable + Triggered
            public string FinalInstruction = "";  // 注入到 tail(历史之后、出话之前)的临场铁令
        }

        public static Result Resolve(string worldBook, string recentText)
        {
            var r = new Result();
            if (string.IsNullOrWhiteSpace(worldBook)) return r;
            string hay = (recentText ?? "").ToLowerInvariant();
            var stable = new StringBuilder();
            var triggered = new StringBuilder();
            var fin = new StringBuilder();
            bool inBlock = false, blockOn = false;   // @@关键词 起的多行块:仅命中关键词时整块注入
            foreach (var raw in worldBook.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0)   // 空行:在生效范围内(基础段或命中的块)保留,作段落间隔
                {
                    if (inBlock) { if (blockOn) AppendLine(triggered, ""); }
                    else AppendLine(stable, "");
                    continue;
                }
                char c = line[0];
                if (line.Length >= 2 && (c == '@' || c == '＠') && (line[1] == '@' || line[1] == '＠'))   // @@/＠＠关键词 — 多行块起始
                {
                    string keys = line.Substring(2).Trim();
                    if (keys.Length == 0 || keys.Equals("end", System.StringComparison.OrdinalIgnoreCase) || keys == "结束")
                    { inBlock = false; blockOn = false; continue; }
                    inBlock = true;
                    blockOn = KeyHit(keys, hay);
                    continue;
                }
                if (c == '@' || c == '＠')   // @关键词:内容 — 单行词条(独立于块)
                {
                    string body = line.Substring(1);
                    int sep = IndexOfSep(body);
                    if (sep < 0) continue;
                    string keys = body.Substring(0, sep);
                    string content = body.Substring(sep + 1).Trim();
                    if (content.Length == 0) continue;
                    if (KeyHit(keys, hay)) AppendLine(triggered, content);
                }
                else if (c == '!' || c == '！')   // !内容 — 临场铁令(post-history),并结束当前块
                {
                    inBlock = false; blockOn = false;
                    string content = line.Substring(1).Trim();
                    if (content.Length > 0) { if (fin.Length > 0) fin.Append('\n'); fin.Append("· ").Append(content); }
                }
                else   // 普通行:在块内则随块去留,块外为常驻背景
                {
                    if (inBlock) { if (blockOn) AppendLine(triggered, line); }
                    else AppendLine(stable, line);
                }
            }
            r.StableBackground = stable.ToString().Trim();
            r.TriggeredBackground = triggered.ToString().Trim();
            r.Background = (r.StableBackground + (r.StableBackground.Length > 0 && r.TriggeredBackground.Length > 0 ? "\n" : "") + r.TriggeredBackground).Trim();
            r.FinalInstruction = fin.ToString().Trim();
            return r;
        }

        static void AppendLine(StringBuilder sb, string s)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(s);
        }

        /// <summary>合成生效世界书:玩家未自定义→默认文档;替换→只用玩家文本;追加→默认文档 + 玩家文本。
        /// 返回值仍含 @@/@/! 语法,交由 Resolve 按近期对话裁剪。</summary>
        public static string Compose(string custom, string mode)
        {
            bool has = !string.IsNullOrWhiteSpace(custom);
            if (!has) return DefaultWorldBook.Text;
            if (string.Equals(mode, "append", System.StringComparison.OrdinalIgnoreCase))
                return DefaultWorldBook.Text + "\n\n———(主公另定，补于默认之上)———\n" + custom.Trim();
            return custom.Trim();   // replace(默认)
        }

        // 关键词与内容的分隔:半/全角冒号、竖线
        static int IndexOfSep(string s)
        {
            int best = -1;
            char[] seps = { ':', '：', '|', '｜' };
            foreach (char sep in seps)
            {
                int i = s.IndexOf(sep);
                if (i >= 0 && (best < 0 || i < best)) best = i;
            }
            return best;
        }

        // 关键词以 逗号/顿号/空格/斜杠 分隔,大小写不敏感,子串命中即触发
        static bool KeyHit(string keys, string hayLower)
        {
            if (string.IsNullOrEmpty(hayLower) || string.IsNullOrWhiteSpace(keys)) return false;
            char[] kseps = { ',', '，', ' ', '、', '/', '／' };
            foreach (var k in keys.Split(kseps))
            {
                string kk = k.Trim().ToLowerInvariant();
                if (kk.Length > 0 && hayLower.Contains(kk)) return true;
            }
            return false;
        }
    }
}
