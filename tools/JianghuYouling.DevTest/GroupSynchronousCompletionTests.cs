using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Memory;
using JianghuYouling.Core.Prompt;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.DevTest
{
    // Drive the actual group orchestrator and durable group document. Unity scheduling,
    // NPC generation and the personal-store boundary are substituted. Successful compression and
    // the full-original fallback both enter the same personal conversation timeline.
    internal static class GroupSynchronousCompletionTests
    {
        internal static void Run()
        {
            RunCase("success", 1, true);
            RunCase("multi-round", 3, true);
            RunCase("fallback-original", 1, false);
            RunCase("interjection", 1, false);
            RunCase("store-failure", 1, true);
            Check(GroupContextCompressor.TryAcceptSummary(
                "甲说明发现旧信，乙约定明早陪同核对。", out string accepted)
                && accepted.Contains("乙约定"), "plain summary accepted");
            Check(!GroupContextCompressor.TryAcceptSummary(
                "{\"summary\":\"不需要 JSON\"}", out _), "structured wrapper rejected");
        }

        private static void Check(bool value, string label)
        {
            if (!value) throw new InvalidOperationException("Group completion: " + label);
        }

        private static void RunCase(string scenario, int rounds, bool modelSucceeds)
        {
            string priorRoot = JianghuYoulingPaths.Root;
            uint priorWorld = JianghuYoulingPaths.CurrentWorldId;
            int priorGeneration = WorldLifecycle.Generation;
            string directory = Path.Combine(Path.GetTempPath(),
                "JHYL_GroupCompletion_" + Guid.NewGuid().ToString("N"));
            var scheduler = new Scheduler();
            GroupChatOrchestrator orchestrator = null;
            SummaryServer summaryServer = null;
            try
            {
                Directory.CreateDirectory(directory);
                JianghuYoulingPaths.Root = directory;
                JianghuYoulingPaths.CurrentWorldId = 8123;
                UnityEngine.MonoBehaviour.CoroutineStarted = scheduler.Start;
                TalkEntryHost.Instance = new TalkEntryHost();
                summaryServer = modelSucceeds ? new SummaryServer() : null;
                string baseUrl = modelSucceeds
                    ? "http://127.0.0.1:" + summaryServer.Port + "/v1"
                    : "http://127.0.0.1:1/v1";
                LlmService.Client = new OpenAiCompatibleClient(
                    baseUrl, "/chat/completions", "", "offline-test");
                GroupRoundsStore.Rounds = rounds;
                TalkOrchestrator.ReplyForTest = id =>
                    id == 2001 ? "我在旧庙石像下找到半封信。" : "我明早陪甲去核对旧档。";
                var projected = new Dictionary<int, IList<TalkTurn>>();
                bool completed = false;
                bool generating = false;
                string error = null, progress = null;
                int shown = 0, started = 0;
                bool interjected = false;
                TalkOrchestrator.ProjectForTest = (id, turns) =>
                {
                    Check(!completed && generating, scenario + " projection precedes completion");
                    if (scenario == "store-failure" && id == 2002) return false;
                    projected[id] = turns;
                    return true;
                };
                orchestrator = GroupChatOrchestrator.OpenForPersistenceTest(1001,
                    new[]
                    {
                        new KeyValuePair<int, string>(2001, "甲"),
                        new KeyValuePair<int, string>(2002, "乙"),
                    });
                Check(orchestrator != null, scenario + " open");
                foreach (var member in orchestrator.Members)
                    member.Snap = new NpcSnapshot
                    { NpcId = member.Id, TaiwuId = 1001, CurrentDate = 123 };

                scheduler.Start(orchestrator.ProcessGroupTurn("明日一起去旧庙查信。",
                    text => progress = text,
                    name => { generating = true; started++; },
                    null,
                    (name, text, line) =>
                    {
                        generating = false;
                        shown++;
                        if (scenario == "interjection" && !interjected)
                        {
                            interjected = true;
                            Check(orchestrator.Interject("还要带上青玉簪。"),
                                "mid-turn interjection accepted");
                        }
                    },
                    () => completed = true,
                    text => error = text,
                    () => generating = false));
                scheduler.Until(() => !scheduler.HasWork);

                int expectedReplies = (scenario == "interjection" ? 2 : rounds) * 2;
                bool success = scenario != "store-failure";
                Check(started == expectedReplies && shown == expectedReplies,
                    scenario + " replies remain visible expected=" + expectedReplies
                    + " started=" + started + " shown=" + shown + " error=" + error);
                Check(completed == success, scenario + " correct completion outcome");
                Check((error == null) == success, scenario + " failure explicitly reported");
                Check(!generating, scenario + " spinner stopped after terminal outcome");
                Check(progress != null && progress.Contains("正在生成中")
                    && !progress.Contains("整理群聊记忆"),
                    scenario + " compression remains inside final generation phase");

                int expectedProjectedMembers = success ? 2 : 1;
                Check(projected.Count == expectedProjectedMembers,
                    scenario + " personal context stores reflect commit outcome");
                foreach (IList<TalkTurn> turns in projected.Values)
                {
                    Check(turns.Count == 1 && TalkTurnKinds.IsGroupChat(turns[0]),
                        scenario + " group turn kind");
                    string text = turns[0].Text;
                    if (modelSucceeds)
                    {
                        Check(text.Contains("【群聊摘要")
                            && text.Contains(SummaryServer.Summary)
                            && !text.Contains("太吾：明日一起去旧庙查信。"),
                            scenario + " one common compressed summary projected");
                    }
                    else
                    {
                        Check(text.Contains("【群聊实录（压缩失败，保留完整原文）")
                            && text.Contains("太吾：明日一起去旧庙查信。")
                            && text.Contains("甲：我在旧庙石像下找到半封信。")
                            && text.Contains("乙：我明早陪甲去核对旧档。"),
                            scenario + " failed compression preserves full attributed transcript");
                        Check(text.Contains("青玉簪") == (scenario == "interjection"),
                            scenario + " interjection fallback matches source");
                    }
                }

                string transcriptPath = Directory.GetFiles(
                    JianghuYoulingPaths.ChatLogs, "Group_*.json").Single();
                JObject document = JObject.Parse(File.ReadAllText(transcriptPath));
                Check(document["MemoryProjectionPending"].Value<bool>() == !success,
                    scenario + " durable pending retained only on store failure");
                Check(document["PendingContextProjectionExchangeIds"].Count()
                    == (success ? 0 : 1),
                    scenario + " durable per-exchange pending queue matches outcome");
                Check(document["Lines"].Count() == expectedReplies + (interjected ? 2 : 1),
                    scenario + " durable transcript remains complete");
                Console.WriteLine("[PASS] synchronous compressed group context: " + scenario);
            }
            finally
            {
                orchestrator?.Cancel();
                summaryServer?.Dispose();
                scheduler.Dispose();
                UnityEngine.MonoBehaviour.CoroutineStarted = null;
                TalkEntryHost.Instance = null;
                LlmService.Client = null;
                TalkOrchestrator.ReplyForTest = null;
                TalkOrchestrator.ProjectForTest = null;
                GroupRoundsStore.Rounds = 1;
                JianghuYoulingPaths.Root = priorRoot;
                JianghuYoulingPaths.CurrentWorldId = priorWorld;
                WorldLifecycle.Generation = priorGeneration;
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        private sealed class SummaryServer : IDisposable
        {
            internal const string Summary = "甲说明在旧庙石像下找到半封信，乙约定明早陪甲核对旧档。";
            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly Task _loop;
            internal int Port { get; }

            internal SummaryServer()
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _loop = Task.Run(() => Run());
            }

            private async Task Run()
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient connection;
                    try { connection = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                    catch { break; }
                    using (connection)
                    using (NetworkStream stream = connection.GetStream())
                    {
                        await ReadRequest(stream).ConfigureAwait(false);
                        string json = "{\"choices\":[{\"finish_reason\":\"stop\","
                            + "\"message\":{\"role\":\"assistant\",\"content\":"
                            + Newtonsoft.Json.JsonConvert.ToString(Summary)
                            + "}}],\"usage\":{\"prompt_tokens\":40,\"completion_tokens\":24}}";
                        byte[] payload = Encoding.UTF8.GetBytes(json);
                        byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n"
                            + "Content-Type: application/json\r\nContent-Length: " + payload.Length
                            + "\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(header, 0, header.Length).ConfigureAwait(false);
                        await stream.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);
                        await stream.FlushAsync().ConfigureAwait(false);
                    }
                }
            }

            private static async Task ReadRequest(NetworkStream stream)
            {
                var bytes = new List<byte>();
                var buffer = new byte[2048];
                int headerEnd = -1, contentLength = 0;
                while (headerEnd < 0)
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (read <= 0) return;
                    bytes.AddRange(buffer.Take(read));
                    string text = Encoding.ASCII.GetString(bytes.ToArray());
                    headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (headerEnd >= 0)
                    {
                        foreach (string line in text.Substring(0, headerEnd).Split(new[] { "\r\n" },
                            StringSplitOptions.None))
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                int.TryParse(line.Substring("Content-Length:".Length).Trim(), out contentLength);
                    }
                }
                int expected = headerEnd + 4 + contentLength;
                while (bytes.Count < expected)
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (read <= 0) break;
                    bytes.AddRange(buffer.Take(read));
                }
            }

            public void Dispose()
            {
                _cts.Cancel();
                try { _listener.Stop(); } catch { }
                try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
                _cts.Dispose();
            }
        }

        private sealed class Scheduler : IDisposable
        {
            private readonly List<Stack<IEnumerator>> _routines =
                new List<Stack<IEnumerator>>();
            internal bool HasWork => _routines.Count != 0;
            internal void Start(IEnumerator routine)
            {
                var stack = new Stack<IEnumerator>();
                stack.Push(routine);
                _routines.Add(stack);
            }
            internal void Until(Func<bool> condition)
            {
                var watch = Stopwatch.StartNew();
                while (!condition())
                {
                    Check(watch.Elapsed < TimeSpan.FromSeconds(30), "fixture deadline");
                    foreach (var stack in _routines.ToArray())
                    {
                        while (stack.Count > 0)
                        {
                            var top = stack.Peek();
                            if (!top.MoveNext())
                            {
                                (stack.Pop() as IDisposable)?.Dispose();
                                continue;
                            }
                            if (top.Current is IEnumerator child)
                            {
                                stack.Push(child);
                                continue;
                            }
                            break;
                        }
                        if (stack.Count == 0) _routines.Remove(stack);
                    }
                    Thread.Sleep(1);
                }
            }
            public void Dispose()
            {
                foreach (var stack in _routines)
                    while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
                _routines.Clear();
            }
        }
    }
}
