using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Telemetry;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.DevTest
{
    internal static class TelemetryTests
    {
        internal static async Task Run()
        {
            string directory = Path.Combine(Path.GetTempPath(), "JianghuYouling.TelemetryTest." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string id = TelemetryIdentity.LoadOrCreate(directory);
                Check(id == TelemetryIdentity.LoadOrCreate(directory), "identity survives another client instance");
                var value = AiTelemetry.CreateEvent(id, "custom-private-model", true, null, 0,
                    new DateTime(2026, 10, 18, 16, 0, 0, DateTimeKind.Utc));
                Check(value.Count == 8 && value.Value<string>("day") == "2026-10-19", "exact minimal event and UTC+8 day");
                Check(value.Value<string>("model") == "other" && !value.ToString().Contains("custom-private-model"), "custom name never leaves client");
                Check(value["input_tokens"].Type == JTokenType.Null && value.Value<int>("output_tokens") == 0, "unknown and known zero remain distinct");
                Check(TelemetryModelCatalog.Normalize("provider/deepseek-v4-pro") == "deepseek-v4-pro", "allowlisted model normalization");
                Check(TelemetryModelCatalog.Normalize("gemini-pro") == "gemini-pro", "canonical model remains canonical");

                Check(!LlmUsage.Parse(JObject.Parse("{}")).NormalizedInputTokens.HasValue, "missing input usage is unknown");
                var openai = LlmUsage.Parse(JObject.Parse("{\"prompt_tokens\":100,\"completion_tokens\":20,\"prompt_tokens_details\":{\"cached_tokens\":80},\"completion_tokens_details\":{\"reasoning_tokens\":15}}"));
                Check(openai.NormalizedInputTokens == 100 && openai.NormalizedOutputTokens == 20, "cache and reasoning subsets are not double counted");
                var anthropic = LlmUsage.Parse(JObject.Parse("{\"input_tokens\":10,\"output_tokens\":20,\"cache_read_input_tokens\":80,\"cache_creation_input_tokens\":5}"));
                Check(anthropic.NormalizedInputTokens == 95 && anthropic.NormalizedOutputTokens == 20, "separate Anthropic cache input is included once");
                var gemini = LlmUsage.Parse(JObject.Parse("{\"prompt_token_count\":100,\"candidates_token_count\":20,\"thoughts_token_count\":30}"));
                Check(gemini.NormalizedInputTokens == 100 && gemini.NormalizedOutputTokens == 50, "separate reasoning output is included once");

                var handler = new FakeHandler();
                var reporter = new AiTelemetry.Reporter(directory, handler);
                reporter.Record("gpt-5", true, 50, 10);
                await Drain(reporter);
                Check(handler.Calls == 1 && JObject.Parse(handler.LastBody).Value<string>("client_id") == id, "one asynchronous request per call");
                handler.Fail = true;
                reporter.Record("gpt-5", false, null, null);
                await Drain(reporter);
                await Task.Delay(100);
                Check(handler.Calls == 2, "failed upload is not retried");
                reporter.Stop();
                Check(Directory.GetFiles(directory).All(p => Path.GetFileName(p).StartsWith("client_id.json", StringComparison.Ordinal)), "no metric/outbox files are created");
                var second = new FakeHandler();
                var restarted = new AiTelemetry.Reporter(directory, second);
                await Task.Delay(100);
                Check(second.Calls == 0, "restart does not replay observations");
                restarted.Stop();
                Console.WriteLine("[PASS] 独立 AI 统计：稳定 ID、单次异步上传、不缓存不重传、字段白名单与 Token 口径");
            }
            finally { Directory.Delete(directory, true); }
        }

        private static async Task Drain(AiTelemetry.Reporter reporter)
        {
            for (int i = 0; i < 100 && reporter.InFlight > 0; i++) await Task.Delay(10);
            Check(reporter.InFlight == 0, "reporter completed without waiting on the game thread");
        }
        private static void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Telemetry: " + label);
        }
        private sealed class FakeHandler : HttpMessageHandler
        {
            internal int Calls;
            internal bool Fail;
            internal string LastBody;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Calls);
                LastBody = await request.Content.ReadAsStringAsync();
                Check(request.RequestUri.AbsoluteUri == AiTelemetry.Endpoint, "fixed reporting destination");
                if (Fail) throw new HttpRequestException("test failure");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
            }
        }
    }
}
