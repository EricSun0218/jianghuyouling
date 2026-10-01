using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Behavior;
using JianghuYouling.Core.Tools;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.DevTest
{
    /// <summary>
    /// Real-provider smoke for the two model-facing contracts that remove avoidable monthly-event
    /// rounds. It never executes a game mutation and never prints prompts, replies or credentials.
    /// </summary>
    internal static class MonthlyEventRoundReductionTests
    {
        public static async Task<int> RunDeepSeekAsync(OpenAiCompatibleClient client)
        {
            if (client == null) return 1;
            Console.WriteLine("=== DeepSeek 过月江湖事件轮次压缩真实冒烟 ===");

            var tools = BuildTools();
            LlmToolResult queryTurn = await client.SendToolRoundAsync(
                new List<LlmMessage>
                {
                    LlmMessage.System(
                        "你是过月江湖事件 Agent。查询工具只用于你决定行动前主动比较事实；"
                        + "若直接调用动作，代码会自动补齐缺失的权威前置并在可靠时同轮执行。"
                        + "本步尚在比较名册1号与2号的技能，不要执行动作；本地前置矩阵要求 a.skills+b.skills。"
                        + "现在在同一个回复中恰好并列调用两次 event_query_person："
                        + "person=1,section=skills 与 person=2,section=skills。"
                        + "不要调用动作，不要输出正文，不要拆成两轮。"),
                    LlmMessage.User("开始本步权威预查。"),
                },
                tools, "auto", 768, 0.0, default, 60,
                "DeepSeek过月事件批量查询真实冒烟", false, LlmReasoningPolicy.Auto);
            bool actorSkills = false, targetSkills = false, queryOnly = true;
            if (queryTurn != null && queryTurn.Ok && queryTurn.HasToolCalls)
                foreach (LlmToolCall call in queryTurn.ToolCalls)
                {
                    if (call == null
                        || !string.Equals(call.Name, "event_query_person",
                            StringComparison.Ordinal))
                    {
                        queryOnly = false;
                        continue;
                    }
                    try
                    {
                        JObject args = JObject.Parse(call.ArgumentsJson ?? "{}");
                        int person = args.Value<int?>("person") ?? 0;
                        string section = (args.Value<string>("section") ?? string.Empty).Trim();
                        if (section == "skills" && person == 1) actorSkills = true;
                        if (section == "skills" && person == 2) targetSkills = true;
                    }
                    catch { queryOnly = false; }
                }
            bool batchOk = queryTurn != null && queryTurn.Ok && queryTurn.HasToolCalls
                && queryOnly && actorSkills && targetSkills;
            Console.WriteLine("  同轮双方技能预查=" + (batchOk ? "PASS" : "FAIL")
                + " calls=" + (queryTurn?.ToolCalls?.Count ?? 0));

            LlmToolResult stopTurn = await client.SendToolRoundAsync(
                new List<LlmMessage>
                {
                    LlmMessage.System(
                        "你是过月江湖事件 Agent。以下三项均为已经落地的权威成功回执，"
                        + "不是待执行计划：甲赠银给乙；乙因亲历提高对甲的好感；丙因嫉妒向甲下了寒毒。"
                        + "三项覆盖传递、关系、危险行动。最后一条成功回执已经明确要求你重新对照原始欲望、"
                        + "未决线索、承诺与冲突；此案的争端现已自然落定，没有仍需执行的具体下一步。"
                        + "数字最低线本身不代表完成，但你完成因果复核后决定停手，就必须不调用任何工具。"
                        + "只用一句简短确认结束行动阶段，不写故事正文；正文会在玩家点击详情后由后台模型生成。"),
                    LlmMessage.User("直接进入人物行动与场景。"),
                },
                tools, "auto", 1200, 0.2, default, 60,
                "DeepSeek过月事件首次停手真实冒烟", false, LlmReasoningPolicy.Auto);
            string story = stopTurn?.Content ?? string.Empty;
            bool stopOk = stopTurn != null && stopTurn.Ok && !stopTurn.HasToolCalls
                && CountReadable(story) <= 160;
            Console.WriteLine("  因果复核后首次无工具收束=" + (stopOk ? "PASS" : "FAIL")
                + " chars=" + CountReadable(story));

            LlmToolResult moodOnlyAppearanceTurn = await client.SendToolRoundAsync(
                new List<LlmMessage>
                {
                    LlmMessage.System(
                        "你是过月江湖事件 Agent。名册1号本月只是心情舒畅，没有身份转折、长期计划、"
                        + "重大经历、关系转折、伪装需要或稳定形象意愿，也没有别的待办。"
                        + BehaviorDispositionPolicy.BuildRosterMoodAndFameDirective()
                        + "请按规则判断是否应仅凭心情改变外貌；若不应，简短结束且不要调用任何工具。"),
                    LlmMessage.User("按人物当前真实动机决定。"),
                },
                tools, "auto", 512, 0.0, default, 60,
                "DeepSeek过月事件纯心情禁止换发真实冒烟", false,
                LlmReasoningPolicy.Auto);
            bool moodOnlyAppearanceOk = moodOnlyAppearanceTurn != null
                && moodOnlyAppearanceTurn.Ok
                && !HasTool(moodOnlyAppearanceTurn, "event_appearance");
            Console.WriteLine("  纯心情不触发外貌工具="
                + (moodOnlyAppearanceOk ? "PASS" : "FAIL")
                + " calls=" + ToolNames(moodOnlyAppearanceTurn));

            bool appearanceAbsent = tools.All(tool => tool == null || tool.Name != "event_appearance");
            Console.WriteLine("  过月江湖事件工具面移除外貌变化=" + (appearanceAbsent ? "PASS" : "FAIL"));
            return batchOk && stopOk && moodOnlyAppearanceOk && appearanceAbsent ? 0 : 1;
        }

        private static List<ToolDef> BuildTools()
        {
            JObject Pair(params (string, JObject, bool)[] extra)
            {
                var fields = new List<(string, JObject, bool)>
                {
                    ("a", ToolDef.Int(), true),
                    ("b", ToolDef.Int(), true),
                    ("reason", ToolDef.Str(), true),
                };
                if (extra != null) fields.AddRange(extra);
                return ToolDef.Obj(fields.ToArray());
            }

            return new List<ToolDef>
            {
                ToolDef.Of("event_query_person", "只读查询人物切片；同一行动的多个独立前置必须在同轮并列查齐。",
                    ToolDef.Obj(("person", ToolDef.Int(), true),
                        ("section", ToolDef.Sel("", "status", "items", "skills",
                            "secrets", "relations", "all"), true))),
                ToolDef.Of("event_teach", "传授技能。", Pair(
                    ("type", ToolDef.Sel("", "combat", "life"), true),
                    ("skill", ToolDef.Str(), true))),
                ToolDef.Of("event_gift_silver", "赠送银钱。",
                    Pair(("amount", ToolDef.Int(), true))),
                ToolDef.Of("event_favor", "改变好感。",
                    Pair(("delta", ToolDef.Int(), true))),
                ToolDef.Of("event_poison", "下毒。",
                    Pair(("poison_type", ToolDef.Sel("", "烈毒", "郁毒", "寒毒",
                        "赤毒", "腐毒", "幻毒"), true))),
            };
        }

        private static int CountReadable(string text)
        {
            int count = 0;
            if (string.IsNullOrEmpty(text)) return count;
            foreach (char value in text)
                if (!char.IsWhiteSpace(value)) count++;
            return count;
        }

        private static bool HasTool(LlmToolResult result, string name)
        {
            if (result == null || !result.HasToolCalls || result.ToolCalls == null) return false;
            foreach (LlmToolCall call in result.ToolCalls)
                if (call != null && string.Equals(call.Name, name, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static string ToolNames(LlmToolResult result)
        {
            if (result == null) return "<null>";
            if (!result.Ok) return "<request-failed>";
            if (result.ToolCalls == null || result.ToolCalls.Count == 0) return "<none>";
            var names = new List<string>();
            foreach (LlmToolCall call in result.ToolCalls)
                names.Add(call?.Name ?? "<unnamed>");
            return string.Join(",", names);
        }
    }
}
