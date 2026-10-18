using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JianghuYouling.Core.Llm;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Telemetry
{
    /// <summary>One non-blocking upload per completed call. No metric files, retry queue, or replay.</summary>
    public static class AiTelemetry
    {
        public const string Endpoint = "https://stats.opengameagent.com/api/telemetry";
        private static readonly object Gate = new object();
        private static Reporter _reporter;

        public static void Start()
        {
            lock (Gate)
            {
                if (_reporter != null) return;
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "JianghuYouling", "Telemetry");
                _reporter = new Reporter(directory);
            }
        }

        public static void Stop()
        {
            lock (Gate) { _reporter?.Stop(); _reporter = null; }
        }

        internal static void Record(string model, bool success, int? inputTokens, int? outputTokens)
        {
            // This seam cannot receive prompts, URLs, trace IDs, errors or API keys.
            try { _reporter?.Record(model, success, inputTokens, outputTokens); }
            catch { /* Reporting can never fail or delay the game operation. */ }
        }

        internal static JObject CreateEvent(string clientId, string model, bool success, int? input, int? output, DateTime utc)
            => new JObject
            {
                ["schema"] = 1, ["client_id"] = clientId, ["event_id"] = Guid.NewGuid().ToString("D"),
                ["day"] = utc.ToUniversalTime().AddHours(8).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["model"] = TelemetryModelCatalog.Normalize(model), ["successful"] = success,
                ["input_tokens"] = input.HasValue ? new JValue(Math.Max(0, input.Value)) : JValue.CreateNull(),
                ["output_tokens"] = output.HasValue ? new JValue(Math.Max(0, output.Value)) : JValue.CreateNull()
            };

        internal sealed class Reporter
        {
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            private readonly Task<string> _identity;
            private readonly HttpClient _http;
            private int _inFlight;
            internal int InFlight => Volatile.Read(ref _inFlight);

            internal Reporter(string directory, HttpMessageHandler handler = null)
            {
                _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
                    { Timeout = TimeSpan.FromSeconds(10) };
                _identity = Task.Run(() =>
                {
                    try { return TelemetryIdentity.LoadOrCreate(directory); }
                    catch
                    {
                        LlmLog.Line("[江湖有灵][统计] 随机身份暂不可用，本次运行不上传统计；不影响对话。");
                        return null;
                    }
                });
            }

            internal void Record(string model, bool success, int? input, int? output)
            {
                if (_stop.IsCancellationRequested) return;
                // Bound simultaneous best-effort requests; never accumulate a backlog.
                if (Interlocked.Increment(ref _inFlight) > 16) { Interlocked.Decrement(ref _inFlight); return; }
                JObject value = CreateEvent(null, model, success, input, output, DateTime.UtcNow);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        string id = await _identity.ConfigureAwait(false);
                        if (id == null || _stop.IsCancellationRequested) return;
                        value["client_id"] = id;
                        using (var body = new StringContent(value.ToString(Formatting.None), Encoding.UTF8, "application/json"))
                        using (var response = await _http.PostAsync(Endpoint, body, _stop.Token).ConfigureAwait(false)) { }
                    }
                    catch { /* One attempt only: lost uploads are not retried or stored. */ }
                    finally { Interlocked.Decrement(ref _inFlight); }
                });
            }

            internal void Stop() { _stop.Cancel(); _http.Dispose(); }
        }
    }
}
