using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using JianghuYouling.Core.Llm;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.DevTest
{
    internal static class ToolArgumentProjectionTests
    {
        private static void Check(bool condition, string label)
        {
            if (!condition) throw new Exception("Tool argument projection failed: " + label);
            Console.WriteLine("  PASS " + label);
        }

        private static List<ToolDef> ProbeTools() => new List<ToolDef>
        {
            ToolDef.Of("bounded", "隔离测试动作，不接入游戏。", ToolDef.Obj(
                ("mode", ToolDef.Sel("模式", "a", "b"), true),
                ("count", ToolDef.Int("次数", 1, 3), true)))
        };

        private static LlmToolResult Parse(string arguments, IList<ToolDef> tools, bool streaming)
        {
            var call = new JObject
            {
                ["id"] = "projection-test", ["type"] = "function",
                ["function"] = new JObject { ["name"] = "bounded", ["arguments"] = arguments },
                ["extra_content"] = new JObject { ["google"] = new JObject { ["thought_signature"] = "opaque-test" } }
            };
            if (!streaming)
            {
                var response = new JObject { ["choices"] = new JArray(new JObject
                {
                    ["finish_reason"] = "tool_calls",
                    ["message"] = new JObject { ["content"] = "", ["tool_calls"] = new JArray(call) }
                }) };
                var parse = typeof(OpenAiCompatibleClient).GetMethod("ParseToolValidatedWithSchema",
                    BindingFlags.Static | BindingFlags.NonPublic);
                return (LlmToolResult)parse.Invoke(null, new object[] { response.ToString(Formatting.None), 200, false, tools, "auto" });
            }
            var collector = new StreamingToolCollector
            {
                AllowedTools = tools, EnforceAllowedTools = true,
                RequireFinishReason = true, RequireDoneSentinel = true
            };
            call["index"] = 0;
            int split = arguments.Length / 2;
            call["function"]["arguments"] = arguments.Substring(0, split);
            collector.Feed(new JObject { ["choices"] = new JArray(new JObject
            {
                ["delta"] = new JObject { ["tool_calls"] = new JArray(call) }
            }) }.ToString(Formatting.None));
            collector.Feed(new JObject { ["choices"] = new JArray(new JObject
            {
                ["finish_reason"] = "tool_calls",
                ["delta"] = new JObject { ["tool_calls"] = new JArray(new JObject
                {
                    ["index"] = 0, ["function"] = new JObject { ["arguments"] = arguments.Substring(split) }
                }) }
            }) }.ToString(Formatting.None));
            collector.Feed("[DONE]");
            LlmToolResult result = collector.ToResult();
            Check(result.Ok == collector.ToResult().Ok, "stream finalization stays idempotent");
            return result;
        }

        private static void CheckDispatch(LlmToolResult result, string label)
        {
            Check(result != null && result.Ok && result.ToolCalls.Count == 1, label + " accepted");
            LlmToolCall call = result.ToolCalls[0];
            JObject args = JObject.Parse(call.ArgumentsJson);
            Check(args.Count == 2 && args.Value<string>("mode") == "a" && args.Value<int>("count") == 2,
                label + " executor receives only declared valid fields");
            Check(call.Id == "projection-test" && call.ExtraFields?["extra_content"]?["google"]?["thought_signature"]?.ToString() == "opaque-test",
                label + " retains call identity and provider metadata");
        }

        public static void RunOffline()
        {
            var tools = ProbeTools();
            string valid = "{ \"mode\": \"a\", \"count\": 2 }";
            Check(ToolArgumentsValidator.ValidateCall(tools, "bounded", valid, out string unchanged, out _)
                && unchanged == valid, "no-extra argument bytes stay unchanged");
            foreach (bool streaming in new[] { false, true })
            {
                string label = streaming ? "stream" : "nonstream";
                CheckDispatch(Parse("{\"mode\":\"a\",\"count\":2,\"reason\":\"解释\",\"unused\":{\"nested\":[1,2]}}", tools, streaming), label);
                foreach (string bad in new[]
                {
                    "{\"mode\":\"a\",\"reason\":\"missing count\"}",
                    "{\"mode\":\"c\",\"count\":2,\"reason\":\"invalid enum\"}",
                    "{\"mode\":\"a\",\"count\":{},\"reason\":\"bad type\"}",
                    "{\"mode\":\"a\",\"count\":4,\"reason\":\"out of range\"}",
                    "{\"mode\":\"a\",\"count\":2,\"reason\":1,\"reason\":2}"
                }) Check(!Parse(bad, tools, streaming).Ok, label + " invalid declared args or duplicate JSON still fail");
            }
            var child = ToolDef.Obj(("count", ToolDef.Int("", 1, 3), true));
            var schema = ToolDef.Obj(("child", child, true),
                ("rows", new JObject { ["type"] = "array", ["items"] = child.DeepClone(), ["minItems"] = 1 }, true));
            var nested = new List<ToolDef> { ToolDef.Of("nested", "", schema) };
            Check(ToolArgumentsValidator.ValidateCall(nested, "nested",
                "{\"child\":{\"count\":2,\"reason\":true},\"rows\":[{\"count\":1,\"note\":\"ignored\"}],\"extra\":1}", out string clean, out _)
                && clean == "{\"child\":{\"count\":2},\"rows\":[{\"count\":1}]}", "nested object and array extras removed");
            Check(!ToolArgumentsValidator.ValidateCall(nested, "nested",
                "{\"child\":{\"count\":2},\"rows\":[{\"count\":0,\"reason\":1}]}", out _, out _), "nested bounds still enforced");
            var declared = new List<ToolDef> { ToolDef.Of("declared", "", ToolDef.Obj(("reason", ToolDef.Str(), true))) };
            Check(ToolArgumentsValidator.ValidateCall(declared, "declared", "{\"reason\":\"保留\"}", out clean, out _)
                && clean.Contains("保留"), "declared reason preserved");
            var empty = new List<ToolDef> { ToolDef.Of("empty", "", ToolDef.Obj()) };
            Check(ToolArgumentsValidator.ValidateCall(empty, "empty", "{\"reason\":\"多传\"}", out clean, out _)
                && clean == "{}", "no-argument tools ignore extras");
            var open = ToolDef.Obj(); open["additionalProperties"] = true;
            var extensions = new List<ToolDef> { ToolDef.Of("extensions", "", open) };
            Check(ToolArgumentsValidator.ValidateCall(extensions, "extensions", "{\"reason\":1}", out clean, out _)
                && clean == "{\"reason\":1}", "explicit free-form extensions preserved");
            open["additionalProperties"] = child;
            Check(ToolArgumentsValidator.ValidateCall(extensions, "extensions", "{\"dynamic\":{\"count\":1,\"extra\":2}}", out clean, out _)
                && clean == "{\"dynamic\":{\"count\":1}}", "typed extensions recursively cleaned");
            Check(!ToolArgumentsValidator.ValidateCall(extensions, "extensions", "{\"dynamic\":{\"count\":0}}", out _, out _),
                "typed extension constraints still enforced");
            schema.Remove("additionalProperties");
            Check(ToolArgumentsValidator.ValidateCall(nested, "nested",
                "{\"child\":{\"count\":2},\"rows\":[{\"count\":1}],\"extra\":1}", out clean, out _) && !clean.Contains("extra"),
                "omitted additionalProperties uses declared fields only");
        }

        public static async Task<int> RunLive(OpenAiCompatibleClient client)
        {
            // Ask the real model to produce a deliberately over-complete argument fixture.
            // Replay exactly that JSON through both production response parsers; no game RPC.
            LlmResult fixture = await client.SendAsync(new List<LlmMessage>
            {
                LlmMessage.System("你在执行隔离的 JSON 参数容错测试。只输出 JSON，不要 Markdown。"),
                LlmMessage.User("输出一个对象：mode 固定为 a，count 固定为数字 2；额外包含 reason 字符串，解释为何学习；另含 unused 对象，其中 note 为任意简短文字。")
            }, 512, 0.0, timeoutSec: 120, jsonObject: true, tag: "额外工具参数真实模型回归");
            Check(fixture != null && fixture.Ok, "real model produced argument fixture");
            JObject raw = JObject.Parse(fixture.Content);
            Check(raw["reason"]?.Type == JTokenType.String && raw["unused"] is JObject,
                "real model fixture contains undeclared reason and object");
            var tools = ProbeTools();
            CheckDispatch(Parse(fixture.Content, tools, false), "real fixture nonstream replay");
            CheckDispatch(Parse(fixture.Content, tools, true), "real fixture stream replay");
            var messages = new List<LlmMessage>
            {
                LlmMessage.System("这是不接入游戏的工具协议测试。请调用 bounded，mode=a、count=2；你可以附带 reason 说明动机。收到成功回执后只回复 本轮结束。"),
                LlmMessage.User("现在执行一次测试动作。")
            };
            foreach (bool streaming in new[] { false, true })
            {
                LlmToolResult turn = streaming
                    ? await client.SendToolRoundStreamAsync(messages, tools, null, null, null, maxTokens: 4096,
                        temperature: 0.0, timeoutSec: 120, tag: "额外参数真实流式工具链")
                    : await client.SendToolRoundAsync(messages, tools, maxTokens: 4096,
                        temperature: 0.0, timeoutSec: 120, tag: "额外参数真实非流式工具链");
                Check(turn != null && turn.Ok && turn.HasToolCalls && turn.ToolCalls.Count == 1, "real tool call succeeds " + streaming);
                var call = turn.ToolCalls[0];
                var args = JObject.Parse(call.ArgumentsJson);
                Check(call.Name == "bounded" && args.Count == 2 && args.Value<string>("mode") == "a" && args.Value<int>("count") == 2,
                    "real tool call reaches simulated executor with declared arguments only " + streaming);
                var continuation = new List<LlmMessage>(messages)
                {
                    LlmMessage.WithToolCalls(turn.ToolCalls, turn.Content, turn.ReplayReasoningContent),
                    LlmMessage.Tool(call.Id, "{\"ok\":true,\"simulation\":true,\"executed\":1}")
                };
                LlmToolResult done = await client.SendToolRoundAsync(continuation, tools, maxTokens: 4096,
                    temperature: 0.0, timeoutSec: 120, tag: "额外参数真实工具链收尾");
                Check(done != null && done.Ok && !done.HasToolCalls && done.Content?.Trim() == "本轮结束",
                    "real model accepts receipt and closes chain " + streaming);
            }
            Console.WriteLine("PASS real model argument fixture replay and live tool chains; game RPC calls=0");
            return 0;
        }
    }
}
