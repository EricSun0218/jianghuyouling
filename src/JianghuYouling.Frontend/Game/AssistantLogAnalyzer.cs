using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using JianghuYouling.Core.Llm;

namespace JianghuYouling
{
    /// <summary>Ling-er local Player.log analyzer. Reads only the fixed Taiwu Unity log path.</summary>
    internal static class AssistantLogAnalyzer
    {
        // JHYL_ASSISTANT_LOG_ANALYZER
        private const int MaxReadBytes = 512 * 1024;
        private const int MaxReturnedChars = 6000;
        private const int MaxTopicChars = 512;
        private const int ContextRadius = 2;

        private static readonly Regex SplitLines = new Regex("\\r\\n|\\n|\\r", RegexOptions.Compiled);
        private static readonly Regex MultiBlank = new Regex("\\n{3,}", RegexOptions.Compiled);

        public static string Analyze(string topic, bool includePrevious)
        {
            if (!ConfiguredSecretSnapshot.TryLoad(out string[] exactSecrets, out _))
                return "【日志分析失败】受保护的接口凭据配置无法可靠读取，已拒绝读取或发送本机日志";
            topic = SecretRedactor.RedactDiagnosticPayloads((topic ?? "").Trim(), exactSecrets);
            if (topic.Length > MaxTopicChars) topic = topic.Substring(0, MaxTopicChars);
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData", "LocalLow", "Conchship", "The Scroll of Taiwu");
                var files = new List<string> { Path.Combine(dir, "Player.log") };
                if (includePrevious) files.Add(Path.Combine(dir, "Player-prev.log"));

                var header = new StringBuilder();
                var body = new StringBuilder();
                header.Append("【日志分析摘要】\n");
                header.Append("主题:").Append(string.IsNullOrWhiteSpace(topic) ? "未指定,按江湖有灵/异常/工具/语音等关键词扫描" : topic).Append('\n');
                header.Append("目录:游戏的 Unity Player.log 固定目录(不向模型发送本机用户路径)\n");
                header.Append("说明:只读取 Player.log / Player-prev.log 的尾部,返回前已脱敏密钥及旧版本可能记录的工具参数、结果正文和被拒回复。\n");

                int totalHits = 0;
                var categoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    var result = AnalyzeOneFile(file, topic, categoryCounts, exactSecrets);
                    totalHits += result.Hits;
                    header.Append("读取:").Append(result.Summary).Append('\n');
                    if (!string.IsNullOrWhiteSpace(result.Snippets))
                    {
                        body.Append("\n【").Append(System.IO.Path.GetFileName(file)).Append(" 相关片段】\n");
                        body.Append(result.Snippets.Trim()).Append('\n');
                    }
                }

                header.Append("命中相关片段:").Append(totalHits).Append('\n');
                AppendCategorySummary(header, categoryCounts);

                if (totalHits <= 0)
                {
                    body.Append("\n未在日志尾部找到明显相关的江湖有灵报错。若问题刚刚发生,让玩家保持游戏不关闭再试一次;若已经重启过游戏,勾上 include_previous 查看 Player-prev.log。\n");
                }

                return LimitReturned(header.ToString(), body.ToString());
            }
            catch (Exception e)
            {
                // Never put exception messages into the model-facing result: filesystem
                // exceptions commonly embed the local Windows user path.
                return "【日志分析失败】" + e.GetType().Name;
            }
        }

        private static FileResult AnalyzeOneFile(string path, string topic,
            Dictionary<string, int> categoryCounts, string[] exactSecrets)
        {
            if (!File.Exists(path))
                return new FileResult { Summary = System.IO.Path.GetFileName(path) + " 不存在", Hits = 0, Snippets = "" };

            string text;
            long length;
            DateTime lastWrite;
            try
            {
                var fi = new FileInfo(path);
                length = fi.Length;
                lastWrite = fi.LastWriteTime;
                text = ReadTail(path);
            }
            catch (Exception e)
            {
                return new FileResult { Summary = System.IO.Path.GetFileName(path) + " 读取失败:" + e.GetType().Name, Hits = 0, Snippets = "" };
            }

            text = RedactSecrets(text ?? "", exactSecrets);
            CountCategories(text, categoryCounts);

            var lines = SplitLines.Split(text);
            var needles = BuildNeedles(topic);
            var keep = new SortedSet<int>();
            for (int i = 0; i < lines.Length; i++)
            {
                if (!IsRelevant(lines[i], needles)) continue;
                for (int j = Math.Max(0, i - ContextRadius); j <= Math.Min(lines.Length - 1, i + ContextRadius); j++)
                    keep.Add(j);
            }

            var snippets = new StringBuilder();
            int prev = -10;
            int hits = 0;
            foreach (int i in keep)
            {
                string line = CleanLine(lines[i]);
                if (line.Length == 0) continue;
                if (prev >= 0 && i > prev + 1) snippets.Append("\n...\n");
                snippets.Append("L").Append(i + 1).Append(": ").Append(line).Append('\n');
                prev = i;
                hits++;
            }

            string name = System.IO.Path.GetFileName(path);
            string tailNote = length > MaxReadBytes ? ",仅读尾部约" + MaxReadBytes / 1024 + "KB" : "";
            return new FileResult
            {
                Summary = name + " 存在,size=" + FormatBytes(length) + ",mtime=" + lastWrite.ToString("yyyy-MM-dd HH:mm:ss") + tailNote,
                Hits = hits,
                Snippets = MultiBlank.Replace(snippets.ToString(), "\n\n").Trim()
            };
        }

        private static string ReadTail(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int count = (int)Math.Min(fs.Length, MaxReadBytes);
                if (fs.Length > count) fs.Seek(-count, SeekOrigin.End);
                byte[] buf = new byte[count];
                int read = fs.Read(buf, 0, count);
                string text = Encoding.UTF8.GetString(buf, 0, read);
                if (fs.Length > count)
                {
                    int firstNl = text.IndexOf('\n');
                    if (firstNl >= 0 && firstNl + 1 < text.Length) text = text.Substring(firstNl + 1);
                }
                return text;
            }
        }

        private static List<string> BuildNeedles(string topic)
        {
            var list = new List<string>
            {
                "[江湖有灵]",
                "JianghuYouling",
                "Exception",
                "Error",
                "Failed",
                "失败",
                "异常",
                "报错",
                "NullReferenceException",
                "HttpRequestException",
                "TaskCanceledException",
                "timeout",
                "超时",
                "工具明细",
                "执行工具"
            };

            string t = (topic ?? "").Trim();
            if (t.Length > 0) AddNeedle(list, t);
            foreach (var part in Regex.Split(t, "[\\s,，、。;；:：/\\\\\\|\\(\\)（）\\[\\]【】「」『』]+"))
                AddNeedle(list, part);

            string lower = t.ToLowerInvariant();
            if (ContainsAny(lower, "语音", "配音", "tts", "audio", "voice"))
                AddMany(list, "TTS", "配音", "语音", "audio", "voice", "MiniMax", "Qwen", "/audio/speech", "provider");
            if (ContainsAny(lower, "交换", "换物", "barter"))
                AddMany(list, "barter", "以物换物", "交换", "gift", "item", "赠物", "物品");
            if (ContainsAny(lower, "流式", "stream", "输出"))
                AddMany(list, "stream", "流式", "chunk", "SSE");
            if (ContainsAny(lower, "工具", "tool", "调用"))
                AddMany(list, "tool", "工具", "function_call", "tool_calls");
            if (ContainsAny(lower, "传功", "授艺", "功法", "teach", "skill"))
                AddMany(list, "teach", "传功", "授艺", "功法", "技能");
            if (ContainsAny(lower, "赴约", "来寻", "goto", "行程", "迁居"))
                AddMany(list, "goto_place", "赴约", "来寻", "行程", "目标", "迁居");
            if (ContainsAny(lower, "世界书", "人设", "设置", "配置"))
                AddMany(list, "世界书", "人设", "ConfigWindow", "settings", "配置", "设置");
            return list;
        }

        private static bool IsRelevant(string line, List<string> needles)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            foreach (var n in needles)
                if (!string.IsNullOrWhiteSpace(n) && line.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        private static void CountCategories(string text, Dictionary<string, int> counts)
        {
            AddCount(counts, "异常/错误", CountAny(text, "Exception", "Error", "Failed", "失败", "异常", "报错"));
            AddCount(counts, "工具调用", CountAny(text, "工具明细", "执行工具", "tool_calls", "function_call"));
            AddCount(counts, "交换/赠物", CountAny(text, "barter", "以物换物", "gift", "赠物"));
            AddCount(counts, "语音", CountAny(text, "TTS", "配音", "语音", "audio", "MiniMax", "Qwen"));
            AddCount(counts, "流式/接口", CountAny(text, "stream", "流式", "HttpRequestException", "timeout", "HTTP"));
        }

        private static void AppendCategorySummary(StringBuilder sb, Dictionary<string, int> counts)
        {
            sb.Append("分类计数:");
            bool any = false;
            foreach (var kv in counts)
            {
                if (kv.Value <= 0) continue;
                if (any) sb.Append(" / ");
                sb.Append(kv.Key).Append('=').Append(kv.Value);
                any = true;
            }
            if (!any) sb.Append("未见明显分类命中");
            sb.Append('\n');
        }

        private static int CountAny(string text, params string[] needles)
        {
            int total = 0;
            foreach (var n in needles)
            {
                int start = 0;
                while (true)
                {
                    int i = text.IndexOf(n, start, StringComparison.OrdinalIgnoreCase);
                    if (i < 0) break;
                    total++;
                    start = i + Math.Max(1, n.Length);
                }
            }
            return total;
        }

        private static void AddCount(Dictionary<string, int> counts, string key, int value)
        {
            if (value <= 0) return;
            int old;
            counts.TryGetValue(key, out old);
            counts[key] = old + value;
        }

        private static string RedactSecrets(string s, string[] exactSecrets)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            s = SecretRedactor.RedactDiagnosticPayloads(s, exactSecrets);
            s = Regex.Replace(s, "sk-proj-[A-Za-z0-9_\\-]{8,}", "sk-proj-***");
            s = Regex.Replace(s, "sk-[A-Za-z0-9_\\-]{12,}", "sk-***");
            s = Regex.Replace(s, "(?i)(Authorization\\s*[:=]\\s*Bearer\\s+)[^\\s\"']+", "$1***");
            s = Regex.Replace(s, "(?i)(Bearer\\s+)[A-Za-z0-9_\\-\\.=]{12,}", "$1***");
            s = Regex.Replace(s, "(?i)((?:api[_-]?key|apikey|x-api-key|token|secret|password)\\s*[\"']?\\s*[:=]\\s*[\"']?)[^\"'\\s,;}]+", "$1***");
            return s;
        }

        private static string LimitReturned(string header, string body)
        {
            string result = header + body;
            if (result.Length <= MaxReturnedChars) return result;
            string note = "\n【截断】相关片段过长,以下保留最近尾部内容。\n";
            int room = MaxReturnedChars - header.Length - note.Length;
            if (room < 1200) room = 1200;
            string tail = body.Length > room ? body.Substring(body.Length - room) : body;
            return header + note + tail;
        }

        private static string CleanLine(string line)
        {
            line = (line ?? "").Trim();
            if (line.Length > 500) line = line.Substring(0, 500) + "...";
            return line;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.0") + "MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("0.0") + "KB";
            return bytes + "B";
        }

        private static bool ContainsAny(string haystack, params string[] needles)
        {
            foreach (var n in needles)
                if (!string.IsNullOrEmpty(n) && haystack.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        private static void AddMany(List<string> list, params string[] items)
        {
            foreach (var item in items) AddNeedle(list, item);
        }

        private static void AddNeedle(List<string> list, string item)
        {
            item = (item ?? "").Trim();
            if (item.Length <= 0) return;
            foreach (var existing in list)
                if (string.Equals(existing, item, StringComparison.OrdinalIgnoreCase))
                    return;
            list.Add(item);
        }

        private sealed class FileResult
        {
            public string Summary;
            public int Hits;
            public string Snippets;
        }
    }
}
