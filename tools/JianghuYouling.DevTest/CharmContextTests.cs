using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Prompt;

namespace JianghuYouling.DevTest
{
    internal static class CharmContextTests
    {
        internal static void RunOffline()
        {
            Require(CharacterCharmText.Format(0) == "0", "zero is a valid charm value");
            Require(CharacterCharmText.Format(-1).StartsWith("未知"), "native missing sentinel is unknown");
            Require(CharacterCharmText.Format(short.MinValue).StartsWith("未知"), "negative data never invents zero");
            Require(CharacterCharmText.Format(900) == "900", "native value is preserved");
            var npc = Profile(735, 246);
            string first = Flatten(npc);
            Require(first.Contains("当前魅力值:735") && first.Contains("当前魅力值:246"),
                "NPC and Taiwu have independent current values in the shared chat prompt");
            string fingerprint = PortraitDistiller.SourceEvidenceFingerprint(npc, null, null, null);
            string portraitPrompt = string.Join("\n", PortraitDistiller.BuildMessages(null, npc,
                null, null, null).Select(message => message.Content ?? string.Empty));
            Require(!portraitPrompt.Contains("当前魅力值:"), "live charm is not distilled into a permanent persona");
            npc.Charm = 0;
            string updated = Flatten(npc);
            Require(updated.Contains("当前魅力值:0") && !updated.Contains("当前魅力值:735"),
                "next turn replaces stale charm and preserves zero");
            Require(fingerprint == PortraitDistiller.SourceEvidenceFingerprint(npc, null, null, null),
                "current charm does not enter the stable portrait fingerprint");
            npc.Charm = -1;
            Require(Flatten(npc).Contains("当前魅力值:未知（本体未提供）"),
                "missing snapshot charm remains unknown");
            Require(new NpcProfileForPrompt().Charm == -1, "unloaded profile defaults to unknown");
            Console.WriteLine("[PASS] current charm context: NPC/Taiwu, refresh, zero, unknown, stable portrait exclusion");
        }

        internal static async Task<int> RunLive(OpenAiCompatibleClient client)
        {
            RunOffline();
            foreach (var sample in new[] { (Npc: 735, Taiwu: 246), (Npc: 0, Taiwu: 900), (Npc: -1, Taiwu: 120) })
            {
                var messages = TalkPromptBuilder.Build(Profile(sample.Npc, sample.Taiwu), null,
                    new List<TalkTurn>(),
                    "仅根据本轮实时资料报告双方当前魅力值。不要推测或补全。只回答两行：NPC=数值或未知；太吾=数值或未知。", false);
                LlmResult result = await client.SendAsync(messages, 256, 0.1, default, 90,
                    false, "人物实时魅力上下文回归", LlmReasoningPolicy.Off);
                string text = result?.Content ?? string.Empty;
                string expectedNpc = sample.Npc < 0 ? "未知" : sample.Npc.ToString();
                bool passed = result != null && result.Ok
                    && Regex.IsMatch(text, @"NPC\s*[=：:]\s*" + expectedNpc + @"(?!\d)", RegexOptions.IgnoreCase)
                    && Regex.IsMatch(text, @"太吾\s*[=：:]\s*" + sample.Taiwu + @"(?!\d)");
                // Only fixture values and token counts are logged; never config, key, or provider response.
                Console.WriteLine("charm_context npc=" + sample.Npc + " taiwu=" + sample.Taiwu
                    + " result=" + (passed ? "PASS" : "FAIL")
                    + " tokens=" + (result?.PromptTokens ?? 0) + "/" + (result?.CompletionTokens ?? 0));
                if (!passed) return 1;
            }
            return 0;
        }

        private static NpcProfileForPrompt Profile(int charm, int taiwuCharm)
            => new NpcProfileForPrompt
            {
                NpcId = 2001, TaiwuId = 1001, Name = "林清", Gender = "女",
                TaiwuName = "太吾", TaiwuGender = "男", PhysiologicalAge = 25, ActualAge = 25,
                Behavior = "中庸", Charm = charm,
                TaiwuInfoText = "太吾 · 男 · 当前魅力值:" + CharacterCharmText.Format(taiwuCharm),
                WorldTimeText = "第1年1月"
            };

        private static string Flatten(NpcProfileForPrompt npc)
            => string.Join("\n", TalkPromptBuilder.Build(npc, null, new List<TalkTurn>(),
                "你好", false).Select(message => message.Content ?? string.Empty));

        private static void Require(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Charm context: " + label);
        }
    }
}
