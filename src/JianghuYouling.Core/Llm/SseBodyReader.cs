using System;
using System.Text;

namespace JianghuYouling.Core.Llm
{
    /// <summary>
    /// 严格 HTTP/1.1 chunked/identity 响应体增量解码器。chunk size、data 后 CRLF、
    /// 0-chunk/trailer、UTF-8 与总字节上限任一异常都会显式进入 ProtocolError。
    /// </summary>
    public sealed class SseBodyReader
    {
        private enum St { Size, SizeLf, Data, DataCr, DataLf, Trailer, TrailerLf, Done, Error }

        private readonly bool _chunked;
        private readonly Action<string> _onLine;
        private readonly int _maxDecodedBytes;
        private readonly int _maxLineChars;
        private readonly Decoder _dec = new UTF8Encoding(false, true).GetDecoder();
        private readonly StringBuilder _line = new StringBuilder();
        private readonly StringBuilder _sizeHex = new StringBuilder();
        private readonly StringBuilder _trailerLine = new StringBuilder();
        private St _state;
        private int _remain;
        private bool _inExt;
        private int _chunkSizeLineBytes;
        private int _trailerBytes;
        private long _wireBytes;
        private bool _firstDecodedChar = true;
        private bool _payloadCr;
        private char[] _cbuf = new char[4096];

        public bool Done { get; private set; }
        public string ProtocolError { get; private set; }
        public long DecodedBytes { get; private set; }

        public SseBodyReader(bool chunked, Action<string> onLine, int maxDecodedBytes = LlmProtocolLimits.MaxResponseBytes,
            int maxLineChars = LlmProtocolLimits.MaxSseLineChars)
        {
            _chunked = chunked;
            _onLine = onLine ?? throw new ArgumentNullException(nameof(onLine));
            _maxDecodedBytes = Math.Max(1, maxDecodedBytes);
            _maxLineChars = Math.Max(1, maxLineChars);
            _state = chunked ? St.Size : St.Data;
        }

        public void Feed(byte[] buf, int offset, int count)
        {
            if (Done || ProtocolError != null || buf == null || count <= 0) return;
            if (offset < 0 || count < 0 || offset > buf.Length - count) { Fail("响应体切片越界"); return; }
            _wireBytes += count;
            if (_wireBytes > (long)_maxDecodedBytes * 8 + LlmProtocolLimits.MaxHttpHeaderBytes)
            { Fail("HTTP 响应体传输编码开销超过上限"); return; }
            if (!_chunked) { Decode(buf, offset, count); return; }

            int i = offset, end = offset + count;
            while (i < end && !Done && ProtocolError == null)
            {
                byte b;
                switch (_state)
                {
                    case St.Size:
                        b = buf[i++];
                        if (++_chunkSizeLineBytes > 128) { Fail("chunk size 行过长"); break; }
                        if (b == (byte)'\r') _state = St.SizeLf;
                        else if (b == (byte)'\n') Fail("chunk size 行必须以 CRLF 结束");
                        else if (b == (byte)';') _inExt = true;
                        else if (_inExt && (b < 0x20 || b > 0x7e)) Fail("chunk extension 含非法字节");
                        else if (!_inExt)
                        {
                            if (!IsHex(b) || _sizeHex.Length >= 16) Fail("chunk size 非法或过长");
                            else _sizeHex.Append((char)b);
                        }
                        break;
                    case St.SizeLf:
                        b = buf[i++];
                        if (b != (byte)'\n') { Fail("chunk size 缺少 LF"); break; }
                        if (!TryParseHex(_sizeHex.ToString(), out _remain)) { Fail("chunk size 非法"); break; }
                        _sizeHex.Length = 0; _inExt = false; _chunkSizeLineBytes = 0;
                        if (_remain == 0) _state = St.Trailer;
                        else if ((long)_remain + DecodedBytes > _maxDecodedBytes) Fail("流式响应体超过上限");
                        else _state = St.Data;
                        break;
                    case St.Data:
                        int take = Math.Min(_remain, end - i);
                        Decode(buf, i, take);
                        i += take; _remain -= take;
                        if (_remain == 0) _state = St.DataCr;
                        break;
                    case St.DataCr:
                        b = buf[i++];
                        if (b != (byte)'\r') Fail("chunk data 后缺少 CRLF");
                        else _state = St.DataLf;
                        break;
                    case St.DataLf:
                        b = buf[i++];
                        if (b != (byte)'\n') Fail("chunk data 后缺少 LF");
                        else _state = St.Size;
                        break;
                    case St.Trailer:
                        b = buf[i++];
                        if (++_trailerBytes > LlmProtocolLimits.MaxHttpHeaderBytes) { Fail("chunk trailer 总量过大"); break; }
                        if (b == (byte)'\r') _state = St.TrailerLf;
                        else if (b == (byte)'\n') Fail("chunk trailer 必须以 CRLF 结束");
                        else if (b < 0x20 || b > 0x7e) Fail("chunk trailer 含非法字节");
                        else if (_trailerLine.Length >= 8192) Fail("chunk trailer 过长");
                        else _trailerLine.Append((char)b);
                        break;
                    case St.TrailerLf:
                        b = buf[i++];
                        if (++_trailerBytes > LlmProtocolLimits.MaxHttpHeaderBytes) { Fail("chunk trailer 总量过大"); break; }
                        if (b != (byte)'\n') { Fail("chunk trailer 缺少 LF"); break; }
                        if (_trailerLine.Length == 0) { Done = true; _state = St.Done; }
                        else
                        {
                            if (!ValidTrailerLine(_trailerLine.ToString())) { Fail("chunk trailer 字段无效"); break; }
                            _trailerLine.Length = 0; _state = St.Trailer;
                        }
                        break;
                    default:
                        i = end;
                        break;
                }
            }
            if (Done && i < end && ProtocolError == null) Fail("0-chunk 后仍有额外响应体字节");
        }

        /// <summary>连接/Content-Length 已结束时调用。chunked 必须完整走到 0-chunk 与 trailer 空行。</summary>
        public void Flush()
        {
            if (ProtocolError != null) return;
            if (_chunked && !Done) { Fail("chunked 响应在 0-chunk/trailer 前中断"); return; }
            try
            {
                int n = _dec.GetChars(EmptyBytes, 0, 0, _cbuf, 0, true);
                EmitChars(n);
            }
            catch (DecoderFallbackException) { Fail("响应体含非法 UTF-8"); return; }
            if (_line.Length > 0)
            {
                string tail = _line.ToString();
                _line.Length = 0;
                _onLine(tail);
            }
            if (!_chunked) { Done = true; _state = St.Done; }
        }

        private static readonly byte[] EmptyBytes = new byte[0];

        private void Decode(byte[] buf, int offset, int count)
        {
            if (count <= 0 || ProtocolError != null) return;
            DecodedBytes += count;
            if (DecodedBytes > _maxDecodedBytes) { Fail("流式响应体超过上限"); return; }
            try
            {
                int maxChars = Encoding.UTF8.GetMaxCharCount(count);
                if (_cbuf.Length < maxChars) _cbuf = new char[maxChars];
                int n = _dec.GetChars(buf, offset, count, _cbuf, 0, false);
                EmitChars(n);
            }
            catch (DecoderFallbackException) { Fail("响应体含非法 UTF-8"); }
        }

        private void EmitChars(int n)
        {
            for (int k = 0; k < n && ProtocolError == null; k++)
            {
                char c = _cbuf[k];
                if (_firstDecodedChar)
                {
                    _firstDecodedChar = false;
                    if (c == '\uFEFF') continue;
                }
                if (_payloadCr)
                {
                    _payloadCr = false;
                    if (c == '\n') continue;
                }
                if (c == '\r') { EmitCurrentLine(); _payloadCr = true; }
                else if (c == '\n') EmitCurrentLine();
                else if (_line.Length >= _maxLineChars) { Fail("SSE 单行超过上限"); break; }
                else _line.Append(c);
            }
        }

        private void EmitCurrentLine()
        {
            string line = _line.ToString(); _line.Length = 0;
            _onLine(line);
        }

        private void Fail(string error)
        {
            ProtocolError = error;
            _state = St.Error;
        }

        private static bool IsHex(byte b)
            => (b >= (byte)'0' && b <= (byte)'9') || (b >= (byte)'a' && b <= (byte)'f') || (b >= (byte)'A' && b <= (byte)'F');

        private static bool TryParseHex(string text, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text)) return false;
            long parsed = 0;
            foreach (char c in text)
            {
                int digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c - 'A' + 10;
                parsed = parsed * 16 + digit;
                if (parsed > int.MaxValue) return false;
            }
            value = (int)parsed;
            return true;
        }

        private static bool ValidTrailerLine(string line)
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) return false;
            string name = line.Substring(0, colon);
            for (int i = 0; i < name.Length; i++)
                if (!(char.IsLetterOrDigit(name[i]) || "!#$%&'*+-.^_`|~".IndexOf(name[i]) >= 0)) return false;
            string value = line.Substring(colon + 1);
            for (int i = 0; i < value.Length; i++)
                if ((value[i] < 0x20 && value[i] != '\t') || value[i] == 0x7f) return false;
            return !name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("Host", StringComparison.OrdinalIgnoreCase);
        }
    }
}
