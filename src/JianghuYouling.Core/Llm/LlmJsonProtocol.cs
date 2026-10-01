using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Llm
{
    internal static class LlmJsonProtocol
    {
        public static bool TryParseObject(string text, int maxBytes, out JObject value, out string error)
        {
            value = null; error = null;
            text = text ?? "";
            if (Encoding.UTF8.GetByteCount(text) > maxBytes) { error = "JSON 响应超过字节上限"; return false; }
            try
            {
                using (var sr = new StringReader(text))
                using (var reader = new StrictJsonTextReader(sr)
                {
                    MaxDepth = LlmProtocolLimits.MaxJsonDepth,
                    DateParseHandling = DateParseHandling.None,
                    FloatParseHandling = FloatParseHandling.Decimal,
                })
                {
                    var token = JToken.ReadFrom(reader, new JsonLoadSettings
                    {
                        CommentHandling = CommentHandling.Ignore,
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                    });
                    if (!(token is JObject obj)) { error = "JSON 根节点不是 object"; return false; }
                    if (reader.Read()) { error = "JSON 根节点后有额外内容"; return false; }
                    value = obj;
                    return true;
                }
            }
            catch (Exception ex) { error = "JSON 无法解析:" + ex.GetType().Name; return false; }
        }
    }

    /// <summary>Newtonsoft 默认接受 JavaScript 注释；provider 协议只允许标准 JSON。</summary>
    internal sealed class StrictJsonTextReader : JsonTextReader
    {
        public StrictJsonTextReader(TextReader reader) : base(new StrictJsonLexicalReader(reader)) { }

        public override bool Read()
        {
            bool hasToken = base.Read();
            if (hasToken && TokenType == JsonToken.Comment)
                throw new JsonReaderException("JSON 不允许注释");
            return hasToken;
        }
    }

    /// <summary>
    /// Json.NET 默认接受 object/array 尾逗号。此 reader 在词法层跟踪字符串与转义，
    /// 只在字符串外遇到 `,}` / `,]` 时拒绝，因而不会用正则误伤字符串正文。
    /// </summary>
    internal sealed class StrictJsonLexicalReader : TextReader
    {
        private readonly TextReader _inner;
        private int _peeked = -2;
        private bool _inString;
        private bool _escaped;
        private char _lastSignificant;

        public StrictJsonLexicalReader(TextReader inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public override int Peek()
        {
            if (_peeked == -2) _peeked = _inner.Read();
            return _peeked;
        }

        public override int Read()
        {
            int value;
            if (_peeked != -2) { value = _peeked; _peeked = -2; }
            else value = _inner.Read();
            if (value >= 0) Inspect((char)value);
            return value;
        }

        public override int Read(char[] buffer, int index, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (index < 0 || count < 0 || index + count > buffer.Length) throw new ArgumentOutOfRangeException();
            int read = 0;
            while (read < count)
            {
                int value = Read();
                if (value < 0) break;
                buffer[index + read++] = (char)value;
            }
            return read;
        }

        private void Inspect(char c)
        {
            if (_inString)
            {
                if (_escaped) { _escaped = false; return; }
                if (c == '\\') { _escaped = true; return; }
                if (c == '"') { _inString = false; _lastSignificant = '"'; return; }
                if (c < 0x20) throw new JsonReaderException("JSON 字符串含未转义控制字符");
                return;
            }

            if (char.IsWhiteSpace(c)) return;
            if (c == '"') { _inString = true; _lastSignificant = '"'; return; }
            if (c == '\'') throw new JsonReaderException("JSON 只允许双引号字符串");
            if ((c == '}' || c == ']') && _lastSignificant == ',')
                throw new JsonReaderException("JSON 不允许尾逗号");
            _lastSignificant = c;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
