using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Llm
{
    /// <summary>
    /// 统一拆分各兼容端返回的正文与思考。除传统字符串外，还兼容 Gemini/代理常见的
    /// content/parts 数组（{ text, thought:true }）以及 type=reasoning/thought_summary 等块。
    /// 这里只依据协议字段分类，不用“Thought:”之类自然语言启发式，避免误删角色台词。
    /// </summary>
    public static class ResponseContentExtractor
    {
        public static void Extract(JToken token, out string visible, out string thinking, out string rawText)
        {
            var vis = new StringBuilder();
            var think = new StringBuilder();
            var raw = new StringBuilder();
            Append(token, false, true, vis, think, raw);
            visible = vis.ToString();
            thinking = think.ToString();
            rawText = raw.ToString();
        }

        /// <summary>
        /// 流式结构块提取。visible 保留内联 think 标签，交给 StreamingToolCollector 的跨 chunk
        /// 状态机继续处理；显式 thought 块仍直接分入 thinking。
        /// </summary>
        public static void ExtractForStreaming(JToken token, out string visible, out string thinking)
        {
            var vis = new StringBuilder();
            var think = new StringBuilder();
            var raw = new StringBuilder();
            Append(token, false, false, vis, think, raw);
            visible = vis.ToString();
            thinking = think.ToString();
        }

        /// <summary>读取 message/delta 上各家独立 reasoning 字段并归一成纯文本。</summary>
        public static string ExtractReasoningFields(JToken owner)
        {
            if (owner == null || owner.Type != JTokenType.Object) return null;
            var sb = new StringBuilder();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            AppendReasoning(owner["reasoning_content"], sb, seen);
            AppendReasoning(owner["reasoning"], sb, seen);
            AppendReasoning(owner["reasoning_details"], sb, seen);
            AppendReasoning(owner["thought_summary"], sb, seen);
            AppendReasoning(owner["thought_summaries"], sb, seen);
            return sb.Length == 0 ? null : sb.ToString();
        }

        /// <summary>
        /// DeepSeek 工具轮协议回灌值。只取 reasoning_content，且保留原始空白；
        /// reasoning_details / thought_summary / 内联 think 仅供展示，绝不能混入这个值。
        /// </summary>
        public static string ExtractReplayReasoningContent(JToken owner)
        {
            if (owner == null || owner.Type != JTokenType.Object) return null;
            var token = owner["reasoning_content"];
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.String) return token.Value<string>();
            return token.ToString(Newtonsoft.Json.Formatting.None);
        }

        public static string JoinReasoning(string a, string b)
        {
            a = (a ?? "").Trim();
            b = (b ?? "").Trim();
            if (a.Length == 0) return b.Length == 0 ? null : b;
            if (b.Length == 0) return a;
            return a + "\n" + b;
        }

        private static void AppendReasoning(JToken token, StringBuilder target, HashSet<string> seen)
        {
            if (token == null || token.Type == JTokenType.Null) return;
            var vis = new StringBuilder();
            var think = new StringBuilder();
            var raw = new StringBuilder();
            Append(token, true, true, vis, think, raw);
            string s = (think.ToString() + vis).Trim();
            if (s.Length == 0 || !seen.Add(s)) return;
            if (target.Length > 0) target.Append('\n');
            target.Append(s);
        }

        private static void Append(JToken token, bool forceThinking, bool splitInline,
            StringBuilder visible, StringBuilder thinking, StringBuilder raw)
        {
            if (token == null || token.Type == JTokenType.Null) return;
            if (token.Type == JTokenType.String || token.Type == JTokenType.Integer
                || token.Type == JTokenType.Float || token.Type == JTokenType.Boolean)
            {
                AppendText(token.ToString(), forceThinking, splitInline, visible, thinking, raw);
                return;
            }
            if (token.Type == JTokenType.Array)
            {
                foreach (var child in token.Children()) Append(child, forceThinking, splitInline, visible, thinking, raw);
                return;
            }
            var obj = token as JObject;
            if (obj == null) return;

            bool isThinking = forceThinking || IsThinkingPart(obj);
            var parts = obj["parts"] ?? obj["items"];
            if (parts != null && parts.Type != JTokenType.Null)
            {
                Append(parts, isThinking, splitInline, visible, thinking, raw);
                return;
            }

            // OpenAI content parts / Gemini parts / OpenRouter reasoning details.
            var text = obj["text"] ?? obj["output_text"] ?? obj["summary"];
            if (text != null && text.Type != JTokenType.Null)
            {
                Append(text, isThinking, splitInline, visible, thinking, raw);
                return;
            }

            // 部分代理把真正内容再包一层 content；不要把整个 JObject.ToString() 当正文。
            var content = obj["content"];
            if (content != null && content.Type != JTokenType.Null)
                Append(content, isThinking, splitInline, visible, thinking, raw);
        }

        private static void AppendText(string text, bool forceThinking, bool splitInline,
            StringBuilder visible, StringBuilder thinking, StringBuilder raw)
        {
            if (string.IsNullOrEmpty(text)) return;
            raw.Append(text);
            if (forceThinking)
            {
                StreamingToolCollector.SplitThink(text, out var v, out var t);
                thinking.Append(v).Append(t);
                return;
            }
            if (!splitInline) { visible.Append(text); return; }
            StreamingToolCollector.SplitThink(text, out var vis, out var think);
            visible.Append(vis);
            thinking.Append(think);
        }

        private static bool IsThinkingPart(JObject obj)
        {
            try
            {
                var thought = obj["thought"];
                if (thought != null && thought.Type != JTokenType.Null && thought.Value<bool>()) return true;
            }
            catch { }
            string type = (obj["type"]?.ToString() ?? obj["role"]?.ToString() ?? "").Trim().ToLowerInvariant();
            return type == "analysis" || type.Contains("thought") || type.Contains("thinking") || type.Contains("reasoning");
        }
    }
}
