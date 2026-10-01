using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.DevTest
{
    /// <summary>
    /// Fixed, network-free agent safety evals.  Results contain only scenario ids and booleans and
    /// can be archived by CI/game E2E without exposing prompts, replies, settings or credentials.
    /// </summary>
    internal static class AgentEvaluationTests
    {
        private sealed class ScenarioResult
        {
            public string Id;
            public bool Passed;
            public string FailureClass;
        }

        public static void Run()
        {
            Console.WriteLine("=== Agent offline golden scenarios / trace isolation ===");
            var results = new List<ScenarioResult>();
            Evaluate(results, "reasoning_visible_body_isolation", ReasoningVisibleBodyIsolation);
            Evaluate(results, "fragmented_tool_call_requires_complete_json", FragmentedToolCallCompleteness);
            Evaluate(results, "unknown_and_failure_never_imply_success", UnknownAndFailureNeverSucceed);
            Evaluate(results, "physical_presence_is_fail_closed", PhysicalPresenceFailClosed);
            Evaluate(results, "assistant_setting_requires_direct_authorization", AssistantSettingAuthorization);
            Evaluate(results, "trace_fields_are_bounded_and_redacted", TraceFieldsBoundedAndRedacted);
            Evaluate(results, "parallel_trace_contexts_do_not_cross", ParallelTraceContextsDoNotCross);

            string outputPath = WriteMachineResult(results);
            int failures = 0;
            foreach (ScenarioResult result in results)
            {
                Console.WriteLine("  " + result.Id + "=" + (result.Passed ? "PASS" : "FAIL"));
                if (!result.Passed) failures++;
            }
            Console.WriteLine("  machine_result=" + outputPath);
            if (failures != 0) throw new InvalidOperationException("agent offline golden failures=" + failures);
        }

        private static void Evaluate(List<ScenarioResult> results, string id, Action scenario)
        {
            var result = new ScenarioResult { Id = id };
            try { scenario(); result.Passed = true; }
            catch (Exception ex) { result.Passed = false; result.FailureClass = ex.GetType().Name; }
            results.Add(result);
        }

        private static void ReasoningVisibleBodyIsolation()
        {
            var visible = new StringBuilder();
            var thinking = new StringBuilder();
            var collector = new StreamingToolCollector
            {
                RequireFinishReason = true,
                RequireDoneSentinel = true,
                OnContent = piece => visible.Append(piece),
                OnThinking = piece => thinking.Append(piece),
            };
            Check(collector.Feed("{\"choices\":[{\"delta\":{\"reasoning_content\":\"private-plan\",\"content\":\"<think>inline-plan</think>visible-body\"},\"finish_reason\":null}]}"), "reasoning payload rejected");
            Check(collector.Feed("{\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}"), "finish payload rejected");
            Check(!collector.Feed("[DONE]"), "done must terminate stream");
            LlmToolResult result = collector.ToResult();
            Check(result.Ok, "collector failed");
            Check(result.Content == "visible-body", "reasoning leaked into final body");
            Check(visible.ToString() == "visible-body", "reasoning leaked into visible callback");
            Check((result.Reasoning ?? "").Contains("private-plan") && (result.Reasoning ?? "").Contains("inline-plan"), "reasoning channel lost");
            Check(!thinking.ToString().Contains("visible-body"), "body leaked into reasoning callback");
        }

        private static void FragmentedToolCallCompleteness()
        {
            var probe = ToolDef.Of("probe", "offline fixture",
                ToolDef.Obj(("value", ToolDef.Sel("fixture", "OK"), true)));
            var collector = new StreamingToolCollector
            {
                RequireFinishReason = true,
                RequireDoneSentinel = true,
                EnforceAllowedTools = true,
                AllowedTools = new List<ToolDef> { probe },
                ExpectedToolChoice = "auto",
            };
            Check(collector.Feed("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_fixture\",\"type\":\"function\",\"function\":{\"name\":\"probe\",\"arguments\":\"{\\\"value\\\":\\\"\"}}]},\"finish_reason\":null}]}"), "first tool fragment rejected");
            Check(collector.Feed("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"OK\\\"}\"}}]},\"finish_reason\":null}]}"), "second tool fragment rejected");
            Check(collector.Feed("{\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}"), "tool finish rejected");
            Check(!collector.Feed("[DONE]"), "done must terminate stream");
            LlmToolResult result = collector.ToResult();
            Check(result.Ok && result.ToolCalls != null && result.ToolCalls.Count == 1, "complete tool call was not recovered");
            Check(result.ToolCalls[0].Name == "probe" && result.ToolCalls[0].ArgumentsJson == "{\"value\":\"OK\"}", "tool fragments were corrupted");

            var truncated = new StreamingToolCollector
            {
                RequireFinishReason = true,
                RequireDoneSentinel = true,
                EnforceAllowedTools = true,
                AllowedTools = new List<ToolDef> { probe },
                ExpectedToolChoice = "auto",
            };
            truncated.Feed("{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_bad\",\"type\":\"function\",\"function\":{\"name\":\"probe\",\"arguments\":\"{\\\"value\\\":\"}}]},\"finish_reason\":null}]}" );
            truncated.Feed("{\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}" );
            truncated.Feed("[DONE]");
            Check(!truncated.ToResult().Ok, "truncated tool arguments were accepted");
        }

        private static void UnknownAndFailureNeverSucceed()
        {
            string operationId = OperationId.FromStableKey("offline-golden-operation");
            ToolOutcome unknown = ToolOutcome.Unknown(operationId, "fixture timeout");
            ToolOutcome failed = ToolOutcome.Failed("FIXTURE_REJECTED", operationId, null, "fixture failure");
            Check(!unknown.IsSucceeded && unknown.IsUnconfirmed && !unknown.IsTerminal, "unknown became success/terminal");
            Check(!failed.IsSucceeded && failed.IsTerminal && !failed.IsUnconfirmed, "failure became success/unconfirmed");
        }

        private static void PhysicalPresenceFailClosed()
        {
            var roster = new HashSet<int> { 11, 12 };
            var presence = new HashSet<int> { 11 };
            bool allowed = GroupPhysicalPresencePolicy.Allows(1, new[] { 12 }, roster, presence,
                out int rejected, out string reason);
            Check(!allowed && rejected == 12 && reason == "not_same_block_or_companion", "absent participant gained physical authority");
            Check(!GroupPhysicalPresencePolicy.Allows(1, new[] { 11 }, roster, null, out _, out string missing)
                && missing == "presence_context_missing", "missing authority did not fail closed");
        }

        private static void AssistantSettingAuthorization()
        {
            Check(AssistantToolPolicy.PlayerDirectlyAuthorized("toggle_stream", "关闭流式输出"), "direct setting command rejected");
            Check(!AssistantToolPolicy.PlayerDirectlyAuthorized("toggle_stream", "怎么关闭流式输出？"), "question authorized a mutation");
            Check(!AssistantToolPolicy.PlayerDirectlyAuthorized("toggle_stream", "不要关闭流式输出"), "negation authorized a mutation");
            Check(!AssistantToolPolicy.PlayerDirectlyAuthorized("set_persona", "替我修改人设"), "removed persona mutation was authorized");
        }

        private static void TraceFieldsBoundedAndRedacted()
        {
            string longConversation = new string('c', 300);
            var trace = new LlmTraceContext("run-sk-abcdefgh123\nnext", longConversation, "exchange-1",
                "attempt-1", 3, "operation-1", "probe", "tool_calls");
            JObject line = LlmLog.BuildJsonLine("tag token=sk-abcdefgh123", "model", "tool", 2, 3, 4,
                true, "Bearer sk-abcdefgh123", 0, 0, 0, 0, 0, null, 0, 7, 3, trace);
            string json = line.ToString(Formatting.None);
            Check(!json.Contains("sk-abcdefgh123"), "secret-shaped value reached JSONL");
            Check((line.Value<string>("run_id") ?? "").Length <= 96, "run id unbounded");
            Check((line.Value<string>("conversation_id") ?? "").Length == 96, "conversation id bound not applied");
            Check(line.Value<string>("exchange_id") == "exchange-1" && line.Value<int>("round") == 3,
                "trajectory coordinates lost association");
            Check(line.Value<string>("operation_id") == "operation-1"
                && line.Value<string>("tool") == "probe" && line.Value<string>("outcome") == "tool_calls",
                "operation/tool/outcome fields lost association");
        }

        private static void ParallelTraceContextsDoNotCross()
        {
            const int count = 64;
            var tasks = new Task<JObject>[count];
            for (int i = 0; i < count; i++)
            {
                int captured = i;
                tasks[i] = Task.Run(() => LlmLog.BuildJsonLine("parallel", "offline", "tool", 1, 0, 0,
                    true, null, 0, 0, 0, 0, 0, null, 0, 0, 0,
                    new LlmTraceContext("run-" + captured, "conversation-" + captured,
                        "exchange-" + captured, "attempt-" + captured, captured,
                        "operation-" + captured, "tool-" + captured, "completed")));
            }
            Task.WaitAll(tasks);
            for (int i = 0; i < count; i++)
            {
                JObject line = tasks[i].Result;
                Check(line.Value<string>("run_id") == "run-" + i, "run context crossed at " + i);
                Check(line.Value<string>("conversation_id") == "conversation-" + i, "conversation context crossed at " + i);
                Check(line.Value<string>("exchange_id") == "exchange-" + i && line.Value<int>("round") == i,
                    "exchange/round context crossed at " + i);
                Check(line.Value<string>("operation_id") == "operation-" + i
                    && line.Value<string>("tool") == "tool-" + i, "operation/tool context crossed at " + i);
            }
        }

        private static string WriteMachineResult(IList<ScenarioResult> results)
        {
            string root = FindWorkspaceRoot();
            string dir = Path.Combine(root, ".e2e-results");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "agent-evaluation-offline.json");
            var scenarios = new JArray();
            foreach (ScenarioResult result in results)
            {
                var item = new JObject { ["id"] = result.Id, ["passed"] = result.Passed };
                if (!string.IsNullOrEmpty(result.FailureClass)) item["failure_class"] = result.FailureClass;
                scenarios.Add(item);
            }
            var document = new JObject
            {
                ["schema_version"] = 1,
                ["suite"] = "agent-offline-golden",
                ["network_used"] = false,
                ["contains_prompts_or_secrets"] = false,
                ["passed"] = AllPassed(results),
                ["scenarios"] = scenarios,
            };
            string json = document.ToString(Formatting.Indented);
            Check(string.Equals(json, SecretRedactor.Redact(json), StringComparison.Ordinal), "machine result was not secret-free");
            File.WriteAllText(path, json + Environment.NewLine, new UTF8Encoding(false));
            return path;
        }

        private static bool AllPassed(IList<ScenarioResult> results)
        {
            foreach (ScenarioResult result in results) if (!result.Passed) return false;
            return true;
        }

        private static string FindWorkspaceRoot()
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppDomain.CurrentDomain.BaseDirectory };
            foreach (string start in starts)
            {
                var directory = new DirectoryInfo(start);
                for (int i = 0; directory != null && i < 8; i++, directory = directory.Parent)
                    if (Directory.Exists(Path.Combine(directory.FullName, "tools"))
                        && Directory.Exists(Path.Combine(directory.FullName, "src"))) return directory.FullName;
            }
            return Directory.GetCurrentDirectory();
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
