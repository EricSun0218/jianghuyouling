using System;
using System.Text;

namespace JianghuYouling.Core.Llm
{
    /// <summary>
    /// WHATWG SSE 事件组装器：同一事件的多行 data 以换行拼接，只在空行处派发。
    /// 注释、event/id/retry 字段不会被误送进 JSON 解析器。
    /// </summary>
    public sealed class SseEventReader
    {
        private readonly Action<string> _onData;
        private readonly StringBuilder _data = new StringBuilder();

        public string ProtocolError { get; private set; }

        public SseEventReader(Action<string> onData)
        {
            _onData = onData ?? throw new ArgumentNullException(nameof(onData));
        }

        public void FeedLine(string line)
        {
            if (ProtocolError != null) return;
            line = line ?? "";
            if (line.Length > LlmProtocolLimits.MaxSseLineChars)
            {
                ProtocolError = "SSE 单行超过上限";
                return;
            }
            if (line.Length == 0)
            {
                Dispatch();
                return;
            }
            if (line[0] == ':') return;
            int colon = line.IndexOf(':');
            string field = colon < 0 ? line : line.Substring(0, colon);
            string value = colon < 0 ? "" : line.Substring(colon + 1);
            if (value.StartsWith(" ", StringComparison.Ordinal)) value = value.Substring(1);
            if (!string.Equals(field, "data", StringComparison.Ordinal)) return;
            if (_data.Length + value.Length + 1 > LlmProtocolLimits.MaxSseEventChars)
            {
                ProtocolError = "SSE data 事件超过上限";
                return;
            }
            _data.Append(value).Append('\n');
        }

        /// <summary>EOF 不派发缺少空行终止的半个事件；调用方随后会因缺 finish_reason 而封闭失败。</summary>
        public void Complete()
        {
            if (_data.Length > 0 && ProtocolError == null)
                ProtocolError = "SSE 事件在空行终止前中断";
        }

        private void Dispatch()
        {
            if (_data.Length == 0) return;
            _data.Length--;
            string payload = _data.ToString();
            _data.Length = 0;
            _onData(payload);
        }
    }
}
