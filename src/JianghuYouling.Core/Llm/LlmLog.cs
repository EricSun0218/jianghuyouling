using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Llm
{
    /// <summary>LLM 结构化性能日志：区分排队、TTFT、重试与总耗时，并保留滚动 JSONL。</summary>
    public static class LlmLog
    {
        public static Action<string> Sink;
        public static void Line(string s) { try { Sink?.Invoke(SecretRedactor.Redact(s)); } catch { } }

        private sealed class Stat
        {
            public int Calls, Fails, Retries;
            public long Prompt, Completion, Reasoning, Total, Cached, CacheMiss, CacheWrite;
            public long TotalMs, MaxMs, QueueMs;
            public readonly List<long> Latencies = new List<long>();
            public readonly List<long> Ttfts = new List<long>();
        }

        private static readonly Dictionary<string, Stat> _stats = new Dictionary<string, Stat>();
        private static readonly object _lock = new object();
        private static readonly object _jsonlIoLock = new object();
        private static long _metricsWriteFailures;
        private static int _metricsWriteWarningEmitted;
        private const int MaxSamplesPerTag = 512;
        private const long MaxJsonlBytes = 2 * 1024 * 1024;
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false, true);
        private static readonly string JsonlPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JianghuYouling", "llm_metrics.jsonl");

        public static long MetricsWriteFailureCount => Interlocked.Read(ref _metricsWriteFailures);
        public static bool MetricsPersistenceHealthy => MetricsWriteFailureCount == 0;

        public static void Record(string tag, string model, string mode, long ms, int prompt, int completion, bool ok, string err = null,
            int cached = 0, int cacheWrite = 0, long queueMs = 0, long ttftMs = 0,
            int retryCount = 0, string retryClass = null, int reasoning = 0, int total = 0, int cacheMiss = 0)
            => RecordWithTrace(tag, model, mode, ms, prompt, completion, ok, err, cached, cacheWrite,
                queueMs, ttftMs, retryCount, retryClass, reasoning, total, cacheMiss, null);

        public static void RecordWithTrace(string tag, string model, string mode, long ms, int prompt, int completion, bool ok, string err = null,
            int cached = 0, int cacheWrite = 0, long queueMs = 0, long ttftMs = 0,
            int retryCount = 0, string retryClass = null, int reasoning = 0, int total = 0, int cacheMiss = 0,
            LlmTraceContext trace = null)
        {
            tag = SecretRedactor.Redact(string.IsNullOrEmpty(tag) ? "?" : tag);
            model = SecretRedactor.Redact(model ?? "?");
            mode = SecretRedactor.Redact(mode ?? "?");
            retryClass = SecretRedactor.Redact(retryClass);
            err = SecretRedactor.Redact(err);
            Line("[江湖有灵][LLM] " + tag + " · " + mode + " · " + ms + "ms · tok " + prompt + "/" + completion
                 + (cached > 0 || cacheWrite > 0 ? (" · cache hit/write " + cached + "/" + cacheWrite
                    + (prompt > 0 ? (" (" + (cached * 100.0 / prompt).ToString("0.0") + "%)") : "")) : "")
                 + " · queue/ttft " + queueMs + "/" + ttftMs + "ms"
                 + (retryCount > 0 ? (" · retry " + retryCount + ":" + (retryClass ?? "unknown")) : "")
                 + " · " + (model ?? "?") + " · " + (ok ? "ok" : ("FAIL:" + Trunc(err, 80))));
            lock (_lock)
            {
                if (!_stats.TryGetValue(tag, out var s)) { s = new Stat(); _stats[tag] = s; }
                s.Calls++; s.TotalMs += ms; if (ms > s.MaxMs) s.MaxMs = ms;
                s.QueueMs += queueMs; s.Retries += retryCount;
                AddSample(s.Latencies, ms); if (ttftMs > 0) AddSample(s.Ttfts, ttftMs);
                s.Prompt += prompt; s.Completion += completion; s.Reasoning += reasoning; s.Total += total;
                s.Cached += cached; s.CacheMiss += cacheMiss; s.CacheWrite += cacheWrite; if (!ok) s.Fails++;
            }
            AppendJsonLine(tag, model, mode, ms, prompt, completion, ok, err, cached, cacheWrite,
                queueMs, ttftMs, retryCount, retryClass, reasoning, total, cacheMiss, trace);
        }

        public static string Summary()
        {
            lock (_lock)
            {
                var list = new List<KeyValuePair<string, Stat>>(_stats);
                if (list.Count == 0)
                {
                    long failures = MetricsWriteFailureCount;
                    return "[江湖有灵][LLM统计] 本次启动尚无大模型调用记录。"
                        + (failures > 0 ? (" 指标持久化已降级，写入失败 " + failures + " 次。") : string.Empty);
                }
                list.Sort((a, b) => b.Value.TotalMs.CompareTo(a.Value.TotalMs));
                var sb = new StringBuilder("[江湖有灵][LLM统计] 自启动以来各处大模型调用(按总耗时降序):\n");
                long allMs = 0; int allCalls = 0;
                foreach (var kv in list)
                {
                    var s = kv.Value; allMs += s.TotalMs; allCalls += s.Calls;
                    sb.Append("  ").Append(kv.Key).Append(": ").Append(s.Calls).Append("次, 共")
                      .Append((s.TotalMs / 1000.0).ToString("0.0")).Append("s, 均")
                      .Append(s.Calls > 0 ? s.TotalMs / s.Calls : 0).Append("ms, 峰")
                      .Append(s.MaxMs).Append("ms, p50 ").Append(Percentile(s.Latencies, 0.50)).Append("ms, p95 ")
                      .Append(Percentile(s.Latencies, 0.95)).Append("ms")
                      .Append(", queue均 ").Append(s.Calls > 0 ? s.QueueMs / s.Calls : 0).Append("ms");
                    if (s.Ttfts.Count > 0) sb.Append(", TTFT p50 ").Append(Percentile(s.Ttfts, 0.50))
                        .Append("ms/p95 ").Append(Percentile(s.Ttfts, 0.95)).Append("ms");
                    if (s.Retries > 0) sb.Append(", 重试").Append(s.Retries);
                    sb.Append(", tok ").Append(s.Prompt).Append("/").Append(s.Completion)
                      .Append(" reasoning/total ").Append(s.Reasoning).Append('/').Append(s.Total);
                    if (s.Cached > 0 || s.CacheWrite > 0) sb.Append(", cache hit/write ").Append(s.Cached).Append('/').Append(s.CacheWrite)
                        .Append(s.Prompt > 0 ? (" (hit " + (s.Cached * 100.0 / s.Prompt).ToString("0.0") + "%)") : "");
                    if (s.CacheMiss > 0) sb.Append(", cache miss ").Append(s.CacheMiss);
                    if (s.Fails > 0) sb.Append(", 失败").Append(s.Fails);
                    sb.Append('\n');
                }
                sb.Append("  —— 合计 ").Append(allCalls).Append(" 次, ").Append((allMs / 1000.0).ToString("0.0")).Append("s");
                long persistenceFailures = MetricsWriteFailureCount;
                if (persistenceFailures > 0)
                    sb.Append("\n  ⚠ 指标持久化已降级，写入失败 ").Append(persistenceFailures)
                        .Append(" 次；内存统计仍有效，JSONL 证据可能不完整。");
                return sb.ToString();
            }
        }

        public static void Reset() { lock (_lock) { _stats.Clear(); } }

        /// <summary>
        /// Records a non-LLM trajectory edge (for example, a tool execution receipt) without
        /// inflating model-call latency/token statistics.  Only bounded identifiers are accepted;
        /// tool arguments and result text are intentionally not part of this API.
        /// </summary>
        public static void RecordTrajectory(string tag, LlmTraceContext trace, string tool,
            string outcome, string operationId = null, string reason = null)
        {
            if (trace == null) return;
            var observed = trace.WithOperation(operationId, tool).WithObservedResult(tool, outcome);
            bool ok = !string.Equals(observed.Outcome, "failed", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(observed.Outcome, "rejected", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(observed.Outcome, "unknown", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(observed.Outcome, "not_executed", StringComparison.OrdinalIgnoreCase);
            // 失败轨迹的 error 字段必须非空：调用方给出具体原因时如实记录，
            // 未给出时至少落 outcome 标签，杜绝 ok=false 而 error 为空的哑失败记录。
            string err = ok ? null
                : (!string.IsNullOrWhiteSpace(reason) ? reason
                    : (!string.IsNullOrWhiteSpace(observed.Outcome) ? observed.Outcome : "unspecified_failure"));
            AppendJsonLine(SecretRedactor.Redact(tag ?? "trajectory"), "", "trajectory", 0,
                0, 0, ok, err, 0, 0, 0, 0, 0, null, 0, 0, 0, observed);
        }

        private static void AddSample(List<long> samples, long value)
        {
            if (samples.Count >= MaxSamplesPerTag) samples.RemoveAt(0);
            samples.Add(value);
        }

        private static long Percentile(List<long> samples, double percentile)
        {
            if (samples == null || samples.Count == 0) return 0;
            var sorted = new List<long>(samples); sorted.Sort();
            if (double.IsNaN(percentile) || percentile <= 0) percentile = 0.01;
            if (percentile > 1) percentile = 1;
            int index = (int)Math.Ceiling(sorted.Count * percentile) - 1;
            if (index < 0) index = 0; if (index >= sorted.Count) index = sorted.Count - 1;
            return sorted[index];
        }

        private static void AppendJsonLine(string tag, string model, string mode, long ms, int prompt, int completion,
            bool ok, string err, int cached, int cacheWrite, long queueMs, long ttftMs, int retryCount, string retryClass,
            int reasoning, int total, int cacheMiss, LlmTraceContext trace)
        {
            try
            {
                lock (_jsonlIoLock)
                {
                    string dir = Path.GetDirectoryName(JsonlPath);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    if (File.Exists(JsonlPath) && new FileInfo(JsonlPath).Length >= MaxJsonlBytes)
                    {
                        string previous = JsonlPath + ".1";
                        if (File.Exists(previous)) File.Delete(previous);
                        File.Move(JsonlPath, previous);
                    }
                    var line = BuildJsonLine(tag, model, mode, ms, prompt, completion, ok, err,
                        cached, cacheWrite, queueMs, ttftMs, retryCount, retryClass, reasoning, total,
                        cacheMiss, trace);
                    File.AppendAllText(JsonlPath, line.ToString(Formatting.None) + Environment.NewLine, Utf8NoBom);
                }
            }
            catch (Exception ex) { NoteMetricsWriteFailure(ex); }
        }

        internal static void NoteMetricsWriteFailure(Exception error)
        {
            Interlocked.Increment(ref _metricsWriteFailures);
            if (Interlocked.CompareExchange(ref _metricsWriteWarningEmitted, 1, 0) != 0) return;
            // Do not include the exception message or path: provider errors and local paths may
            // contain secrets.  The type is sufficient for a one-shot health signal.
            string kind = error == null ? "unknown" : error.GetType().Name;
            Line("[江湖有灵][LLM指标] JSONL 持久化不可用；本次启动仅保留内存统计。error_type=" + kind);
        }

        internal static void ResetMetricsWriteHealthForTests()
        {
            Interlocked.Exchange(ref _metricsWriteFailures, 0);
            Interlocked.Exchange(ref _metricsWriteWarningEmitted, 0);
        }

        internal static JObject BuildJsonLine(string tag, string model, string mode, long ms, int prompt,
            int completion, bool ok, string err, int cached, int cacheWrite, long queueMs, long ttftMs,
            int retryCount, string retryClass, int reasoning, int total, int cacheMiss, LlmTraceContext trace)
        {
            var line = new JObject
            {
                ["utc"] = DateTime.UtcNow.ToString("o"),
                ["tag"] = Trunc(SecretRedactor.Redact(tag ?? "?"), 160),
                ["model"] = Trunc(SecretRedactor.Redact(model ?? ""), 160),
                ["mode"] = Trunc(SecretRedactor.Redact(mode ?? ""), 48),
                ["elapsed_ms"] = ms, ["queue_ms"] = queueMs, ["ttft_ms"] = ttftMs,
                ["retry_count"] = retryCount,
                ["retry_class"] = Trunc(SecretRedactor.Redact(retryClass ?? ""), 96),
                ["prompt_tokens"] = prompt, ["completion_tokens"] = completion,
                ["reasoning_tokens"] = reasoning, ["total_tokens"] = total,
                ["cached_tokens"] = cached, ["cache_write_tokens"] = cacheWrite,
                ["cache_miss_tokens"] = cacheMiss, ["ok"] = ok,
                ["error"] = Trunc(SecretRedactor.Redact(err), 240),
            };
            AddTraceFields(line, trace);
            return line;
        }

        private static void AddTraceFields(JObject line, LlmTraceContext trace)
        {
            if (line == null || trace == null || trace.IsEmpty) return;
            AddOptional(line, "run_id", trace.RunId);
            AddOptional(line, "conversation_id", trace.ConversationId);
            AddOptional(line, "exchange_id", trace.ExchangeId);
            AddOptional(line, "attempt_id", trace.AttemptId);
            if (trace.Round.HasValue) line["round"] = trace.Round.Value;
            AddOptional(line, "operation_id", trace.OperationId);
            AddOptional(line, "tool", trace.Tool);
            AddOptional(line, "outcome", trace.Outcome);
        }

        private static void AddOptional(JObject line, string name, string value)
        {
            if (!string.IsNullOrEmpty(value)) line[name] = value;
        }

        private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n));
    }
}
