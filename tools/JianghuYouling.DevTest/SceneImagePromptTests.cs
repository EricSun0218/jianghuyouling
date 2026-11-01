using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Security;
using JianghuYouling.Core.Prompt;
using JianghuYouling.Core.Web;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.DevTest
{
    internal static class SceneImagePromptTests
    {
        public static void Run()
        {
            var lines = new List<SceneImageDialogueLine>
            {
                new SceneImageDialogueLine { SpeakerId = 101, IsPlayer = true, SpeakerName = "太吾", Text = "我递给你一把伞。" },
                new SceneImageDialogueLine { SpeakerId = 202, SpeakerName = "沈雁", Text = "我接过伞，向你道谢。" },
            };
            string single = SceneImagePromptBuilder.Build(101, 202, "沈雁", lines, "水墨");
            Require(single.Contains("左侧＝角色A＝NPC（当前生图对象），姓名：\"沈雁\"，人物编号：202"), "left NPC identity");
            Require(single.Contains("右侧＝角色B＝太吾（玩家），人物编号：101"), "right player identity");
            Require(single.Contains("发言者：角色B／太吾（玩家）；姓名：\"太吾\"；人物编号：101】\n我递给你一把伞。"), "player action stays with player");
            Require(single.Contains("发言者：角色A／NPC（当前生图对象）；姓名：\"沈雁\"；人物编号：202】\n我接过伞，向你道谢。"), "NPC action stays with NPC");
            Require(single.Contains("NPC对玩家说的‘你’指角色B"), "second-person ownership is explicit");
            Require(SceneImagePromptBuilder.NpcReferenceX < SceneImagePromptBuilder.PlayerReferenceX, "portrait left/right contract");

            lines.Add(new SceneImageDialogueLine { SpeakerId = 303, SpeakerName = "陆舟", Text = "我在门外等你们。" });
            string group = SceneImagePromptBuilder.Build(101, 303, "陆舟", lines, null);
            Require(group.Contains("其他群成员（不在参考图中）；姓名：\"沈雁\"；人物编号：202】\n我接过伞"), "previous group speaker does not become target NPC");
            Require(group.Contains("角色A／NPC（当前生图对象）；姓名：\"陆舟\"；人物编号：303】\n我在门外"), "actual group target remains identified");
            Require(group.IndexOf("我递给你一把伞。", StringComparison.Ordinal) < group.IndexOf("我在门外等你们。", StringComparison.Ordinal), "chronological order");

            lines[0].Text = new string('旧', 20000);
            string bounded = SceneImagePromptBuilder.Build(101, 303, "陆舟", lines, new string('风', 20000));
            Require(bounded.Length <= ImageGenerationClient.MaxPromptChars, "provider character bound");
            Require(bounded.Contains("我在门外等你们。") && bounded.Contains("我接过伞，向你道谢。"), "newest replies survive context pressure");
            Require(bounded.Contains("禁止互换") && bounded.Contains("右侧＝角色B"), "identity instructions survive pressure");
            string empty = SceneImagePromptBuilder.Build(101, 202, null, null, null);
            Require(empty.Contains("姓名未知") && !empty.Contains("小队"), "missing name never invents an identity");
            Console.WriteLine("PASS scene image prompt: portrait identities, speaker perspective, group attribution and bounded recent context");
        }

        internal static async Task<int> RunLive(string configPath)
        {
            Run();
            if (string.IsNullOrWhiteSpace(configPath)
                || !ProtectedConfigFile.TryLoad(configPath, out var config, out var key, out _)
                || config == null || string.IsNullOrWhiteSpace(key))
            {
                Console.Error.WriteLine("FAIL scene image live: protected configuration unavailable");
                return 2;
            }
            var client = new OpenAiCompatibleClient(config["baseUrl"]?.ToString(),
                config["chatPath"]?.ToString(), key, config["model"]?.ToString());
            key = null;
            if (!string.IsNullOrWhiteSpace(client.ConfigurationError)) return 2;
            for (int sample = 0; sample < 3; sample++)
            {
                bool group = sample == 1;
                var lines = new List<SceneImageDialogueLine>();
                if (sample == 2)
                    lines.Add(new SceneImageDialogueLine { SpeakerId = 101, IsPlayer = true,
                        SpeakerName = "太吾", Text = new string('旧', 20000) });
                lines.Add(new SceneImageDialogueLine { SpeakerId = group ? 202 : 101,
                    IsPlayer = !group, SpeakerName = group ? "沈雁" : "太吾",
                    Text = group ? "陆舟，我把伞递给你。" : "沈雁，我把伞递给你。" });
                lines.Add(new SceneImageDialogueLine { SpeakerId = group ? 303 : 202,
                    SpeakerName = group ? "陆舟" : "沈雁", Text = "我接过你递来的伞，向你道谢。" });
                string prompt = SceneImagePromptBuilder.Build(101, group ? 303 : 202,
                    group ? "陆舟" : "沈雁", lines, "水墨");
                var messages = new List<LlmMessage>
                {
                    new LlmMessage("system", "你是画面角色归属审核器，只解析给定生图提示词，不绘图、不续写。只输出JSON，字段值均为人物编号：left（参考图左侧）、right（参考图右侧）、giver（最后一次递伞者）、receiver（最后一次接伞者）。不要输出解释或Markdown。"),
                    new LlmMessage("user", prompt)
                };
                var result = await client.SendAsync(messages, 512, 0.0, default, 120,
                    false, "生图角色归属在线回归", LlmReasoningPolicy.Off);
                bool passed = false;
                if (result != null && result.Ok)
                {
                    try
                    {
                        var answer = JObject.Parse(result.Content ?? "");
                        passed = answer["left"]?.Value<int>() == (group ? 303 : 202)
                            && answer["right"]?.Value<int>() == 101
                            && answer["giver"]?.Value<int>() == (group ? 202 : 101)
                            && answer["receiver"]?.Value<int>() == (group ? 303 : 202);
                    }
                    catch (Exception) { passed = false; }
                }
                // Only synthetic case IDs, status and token counts; no keys, configuration or response bodies.
                Console.WriteLine("scene_image_roles case=" + sample + " result=" + (passed ? "PASS" : "FAIL")
                    + " tokens=" + (result?.PromptTokens ?? 0) + "/" + (result?.CompletionTokens ?? 0));
                if (!passed) return 1;
            }
            return 0;
        }

        private static void Require(bool condition, string label)
        {
            if (!condition) throw new Exception("Scene image prompt: " + label);
        }
    }
}
