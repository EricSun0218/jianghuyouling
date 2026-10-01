using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JianghuYouling.Core.Llm
{
    /// <summary>所有外部错误、日志和 UI 错误出口共用的密钥脱敏器。</summary>
    public static class SecretRedactor
    {
        private static readonly Regex Bearer = new Regex("(?i)(\\bBearer\\s+)[^\\s,;\\\"']+", RegexOptions.Compiled);
        private static readonly Regex NamedSecret = new Regex(
            "(?i)((?:api[_-]?key|apikey|x-api-key|access[_-]?token|token|secret|password)\\s*[\\\"']?\\s*[:=]\\s*[\\\"']?)[^\\\"'\\s,;}]+",
            RegexOptions.Compiled);
        private static readonly Regex QuerySecret = new Regex(
            "(?i)([?&](?:key|api[_-]?key|access[_-]?token|token)=)[^&#\\s]+", RegexOptions.Compiled);
        private static readonly Regex SkLike = new Regex("(?i)\\bsk-[a-z0-9][a-z0-9_-]{7,}\\b", RegexOptions.Compiled);
        private static readonly Regex ToolArgsPayload = new Regex(
            "(?im)(工具明细#[^\\r\\n]*?\\bargs=)[^\\r\\n]*|((?:并发)?执行工具[^\\r\\n]*?\\bargs=)[^\\r\\n]*",
            RegexOptions.Compiled);
        private static readonly Regex ToolResultPayload = new Regex(
            "(?im)((?:并发)?工具结果[^\\r\\n]*?\\bresult=)[^\\r\\n]*|(durable dispatch gate 拦截[^\\r\\n]*?\\bresult=)[^\\r\\n]*",
            RegexOptions.Compiled);
        private static readonly Regex RejectedReplyPayload = new Regex(
            "(?im)(拦截(?:伪工具成功话术|太吾赠予/传授空口收下|空头动作承诺)[^:\\r\\n]*:)[^\\r\\n]*",
            RegexOptions.Compiled);

        public static string Redact(string value, params string[] exactSecrets)
        {
            if (string.IsNullOrEmpty(value)) return value;
            string result = value;
            if (exactSecrets != null)
            {
                foreach (string secret in exactSecrets)
                {
                    if (string.IsNullOrEmpty(secret) || secret.Length < 4) continue;
                    result = result.Replace(secret, "[REDACTED]");
                }
            }
            result = Bearer.Replace(result, "$1[REDACTED]");
            result = NamedSecret.Replace(result, "$1[REDACTED]");
            result = QuerySecret.Replace(result, "$1[REDACTED]");
            result = SkLike.Replace(result, "[REDACTED]");
            return result;
        }

        public static bool ContainsHeaderBreak(string value)
            => !string.IsNullOrEmpty(value) && (value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0);

        /// <summary>
        /// Sanitizes historical diagnostic lines before they can be exported or sent to a
        /// log-analysis model. Old releases could include complete tool arguments/results or
        /// rejected dialogue; their structural prefixes remain, payloads do not.
        /// </summary>
        public static string RedactDiagnosticPayloads(string value, params string[] exactSecrets)
        {
            string result = Redact(value, exactSecrets);
            if (string.IsNullOrEmpty(result)) return result;
            result = ToolArgsPayload.Replace(result, match =>
            {
                string prefix = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                return prefix + "[PRIVATE_PAYLOAD_REDACTED]";
            });
            result = ToolResultPayload.Replace(result, match =>
            {
                string prefix = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                return prefix + "[PRIVATE_PAYLOAD_REDACTED]";
            });
            return RejectedReplyPayload.Replace(result, "$1[PRIVATE_REPLY_REDACTED]");
        }
    }

    /// <summary>
    /// Redacts one exact credential before streaming callbacks observe it, even when the
    /// provider splits the credential across arbitrary SSE deltas. Characters that currently
    /// match a credential prefix are held back until they either complete (emit one redaction
    /// marker) or diverge (then become safe to emit). The KMP fallback keeps memory bounded by
    /// the credential length and avoids delaying ordinary text by a fixed multi-kilobyte tail.
    /// </summary>
    internal sealed class IncrementalSecretRedactor
    {
        private readonly string _secret;
        private readonly int[] _prefix;
        private readonly StringBuilder _pending = new StringBuilder();
        private readonly StringBuilder _outgoing = new StringBuilder();
        private int _matched;
        private bool _flushed;

        internal IncrementalSecretRedactor(string exactSecret, int minimumLength = 4)
        {
            _secret = !string.IsNullOrEmpty(exactSecret)
                && exactSecret.Length >= Math.Max(1, minimumLength)
                ? exactSecret : null;
            if (_secret == null) return;
            _prefix = new int[_secret.Length];
            for (int i = 1, j = 0; i < _secret.Length; i++)
            {
                while (j > 0 && _secret[i] != _secret[j]) j = _prefix[j - 1];
                if (_secret[i] == _secret[j]) j++;
                _prefix[i] = j;
            }
        }

        internal void Push(string value, Action<string> sink)
        {
            if (_flushed || string.IsNullOrEmpty(value)) return;
            if (_secret == null)
            {
                SafeEmit(SecretRedactor.Redact(value), sink);
                return;
            }

            foreach (char c in value)
            {
                while (_matched > 0 && c != _secret[_matched])
                {
                    int fallback = _prefix[_matched - 1];
                    int emitCount = _matched - fallback;
                    _outgoing.Append(_pending.ToString(0, emitCount));
                    _pending.Remove(0, emitCount);
                    _matched = fallback;
                }

                if (c == _secret[_matched])
                {
                    _pending.Append(c);
                    _matched++;
                    if (_matched == _secret.Length)
                    {
                        FlushOutgoing(sink);
                        SafeEmit("[REDACTED]", sink);
                        _pending.Length = 0;
                        _matched = 0;
                    }
                }
                else
                {
                    // _matched is zero here; otherwise the mismatch loop would have fallen
                    // back again until this character either matched or no prefix remained.
                    _outgoing.Append(c);
                }
            }
            FlushOutgoing(sink);
        }

        internal void Flush(Action<string> sink)
        {
            if (_flushed) return;
            _flushed = true;
            if (_pending.Length > 0)
            {
                _outgoing.Append(_pending);
                _pending.Length = 0;
                _matched = 0;
            }
            FlushOutgoing(sink);
        }

        private void FlushOutgoing(Action<string> sink)
        {
            if (_outgoing.Length == 0) return;
            string value = _outgoing.ToString();
            _outgoing.Length = 0;
            SafeEmit(SecretRedactor.Redact(value, _secret), sink);
        }

        private static void SafeEmit(string value, Action<string> sink)
        {
            if (string.IsNullOrEmpty(value) || sink == null) return;
            try { sink(value); } catch { }
        }
    }

    /// <summary>
    /// Streaming exact-text redaction for a bounded set of in-game private facts.
    /// Unlike applying <see cref="SecretRedactor.Redact(string, string[])"/> to each
    /// delta independently, this also catches a fact split across arbitrary SSE chunks.
    /// Longer values run first so an embedded shorter value cannot expose the remainder.
    /// </summary>
    public sealed class IncrementalExactTextRedactor
    {
        private readonly List<IncrementalSecretRedactor> _pipeline;
        private bool _flushed;

        public IncrementalExactTextRedactor(IEnumerable<string> exactTexts)
        {
            _pipeline = (exactTexts ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(value => value.Length)
                .Select(value => new IncrementalSecretRedactor(value, 1))
                .ToList();
        }

        public bool HasValues => _pipeline.Count > 0;

        public void Push(string value, Action<string> sink)
        {
            if (_flushed || string.IsNullOrEmpty(value)) return;
            PushAt(0, value, sink);
        }

        public void Flush(Action<string> sink)
        {
            if (_flushed) return;
            _flushed = true;
            for (int index = 0; index < _pipeline.Count; index++)
            {
                int next = index + 1;
                _pipeline[index].Flush(value => PushAt(next, value, sink));
            }
        }

        private void PushAt(int index, string value, Action<string> sink)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (index >= _pipeline.Count)
            {
                try { sink?.Invoke(value); } catch { }
                return;
            }
            int next = index + 1;
            _pipeline[index].Push(value, emitted => PushAt(next, emitted, sink));
        }
    }
}
