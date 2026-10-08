using System;
using System.IO;
using JianghuYouling.Core.Text;
using JianghuYouling.Core.Web;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.DevTest
{
    internal static class DialogueSpeechTests
    {
        internal static void Run()
        {
            TestDialogueBoundary();
            TestSpeechPreparation();
            TestSettingsPersistence();
            Console.WriteLine("[PASS] shared dialogue colors, dialogue-only speech and durable reading mode");
        }

        static void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Dialogue speech: " + label);
        }

        static void Equal(string actual, string expected, string label)
            => Check(string.Equals(actual, expected, StringComparison.Ordinal), label);

        static void TestDialogueBoundary()
        {
            Equal(DialogueText.ExtractDialogue(null), "", "null has no dialogue");
            Equal(DialogueText.ExtractDialogue(""), "", "empty has no dialogue");
            Equal(DialogueText.ExtractDialogue("他微笑。（点头）(抬手)[沉吟]【低声】某某说：你好。‘引用’'quote'"),
                "", "bare prose, actions and single quotes keep their narration classification");
            Equal(DialogueText.ExtractDialogue("他拱手。「别来无恙。」她答：『一切安好。』末尾旁白。"),
                "「别来无恙。」\n『一切安好。』", "keep only dialogue, ordered and separated");
            Equal(DialogueText.ExtractDialogue("他说：“进来。”又说：\"坐。\""),
                "“进来。”\n\"坐。\"", "curly and ASCII double quotes share the color rule");
            Equal(DialogueText.ExtractDialogue("「他说『先走』，我应“好”。」旁白。"),
                "「他说『先走』，我应“好”。」", "nested types close only at their matching delimiter");
            Equal(DialogueText.ExtractDialogue("“甲\"乙\"丙”尾"),
                "“甲\"乙\"丙”", "ASCII double quotes nest inside curly quotes");
            Equal(DialogueText.ExtractDialogue("「甲\n乙」\r\n他转身。『丙』"),
                "「甲\n乙」\n『丙』", "dialogue boundary survives line breaks");
            Equal(DialogueText.ExtractDialogue("他道：「话未说完\n又一句"),
                "「话未说完\n又一句", "unfinished dialogue remains dialogue through the current end");
            Equal(DialogueText.ExtractDialogue("「甲』乙」尾『丙』"),
                "「甲』乙」\n『丙』", "mismatched closer does not end another quote type");
            Equal(DialogueText.ExtractDialogue("「甲『乙」尾"),
                "「甲『乙」尾", "wrong nested closer keeps the same unfinished span as the UI");
            Equal(DialogueText.ExtractDialogue("「甲」「乙」"), "「甲」\n「乙」", "adjacent utterances stay separate");
            Equal(DialogueText.ExtractDialogue("「（动作）‘引文’仍在对白内」"),
                "「（动作）‘引文’仍在对白内」", "do not introduce a second action or single-quote rule");

            Equal(DialogueText.Colorize(null, "N", "D"), "", "empty color output unchanged");
            Equal(DialogueText.Colorize("旁白", "N", "D"), "<color=N>旁白</color>", "narration color unchanged");
            Equal(DialogueText.Colorize("旁白「对白」尾", "N", "D"),
                "<color=N>旁白</color><color=D>「对白」</color><color=N>尾</color>",
                "color transitions are unchanged");
            Equal(DialogueText.Colorize("「甲」「乙」", "N", "D"),
                "<color=N></color><color=D>「甲」</color><color=N></color><color=D>「乙」</color><color=N></color>",
                "empty narration transitions at adjacent dialogue boundaries are unchanged");
            Equal(DialogueText.Colorize("「甲『乙』丙」", "N", "D"),
                "<color=N></color><color=D>「甲『乙』丙」</color><color=N></color>",
                "nested dialogue keeps one outer accent");
            Equal(DialogueText.Colorize("前「未完\n下一行", "N", "D"),
                "<color=N>前</color><color=D>「未完\n下一行</color><color=N></color>",
                "streaming unfinished dialogue closes the same rich-text tags");
        }

        static void TestSpeechPreparation()
        {
            const string reply = "　**他点头**。\n「#你好`。」「再会。」  ";
            Equal(TtsProviderUtil.PrepareSpeechText(reply, false),
                "他点头。\n「你好。」「再会。」", "full reading preserves the existing cleanup and narration");
            Equal(TtsProviderUtil.PrepareSpeechText(reply, true),
                "「你好。」\n「再会。」", "dialogue selection occurs before speech cleanup");
            Equal(TtsProviderUtil.PrepareSpeechText("这是灵儿的一段普通说明。", false),
                "这是灵儿的一段普通说明。", "default full reading retains unquoted assistant prose");
            Equal(TtsProviderUtil.PrepareSpeechText("这是灵儿的一段普通说明。", true),
                "", "assistant has no silent full-reading fallback");
            foreach (string text in new[] { "没有引号的旁白", "‘这仍是旁白’", "「」", "「……！？」", "「🙂」", "「**#`」" })
                Check(!TtsProviderUtil.ContainsReadableSpeech(TtsProviderUtil.PrepareSpeechText(text, true)),
                    "no dialogue or punctuation-only dialogue has no synthesis work");

            string longNarration = new string('旁', TtsProviderUtil.MaxSpeechCharacters + 1) + "「你好。」";
            foreach (string provider in new[] { TtsProviderUtil.ProviderMiniMax, TtsProviderUtil.ProviderOpenAi,
                TtsProviderUtil.ProviderDashScopeQwen, TtsProviderUtil.ProviderVolcengineSeedAudio })
            {
                Check(TtsProviderUtil.TryPlanSpeech(provider, TtsProviderUtil.PrepareSpeechText(longNarration, true),
                    out var chunks, out _), "excluded narration cannot consume the speech budget");
                Equal(string.Join("", chunks), "「你好。」", "every provider receives only the selected text");
                Check(!TtsProviderUtil.TryPlanSpeech(provider, TtsProviderUtil.PrepareSpeechText(longNarration, false),
                    out _, out _), "full-reading budget remains enforced");
            }
        }

        static void TestSettingsPersistence()
        {
            string priorRoot = JianghuYoulingPaths.Root;
            string temp = Path.GetFullPath(Path.GetTempPath());
            string root = Path.Combine(temp, "JHYL_DialogueSpeech_" + Guid.NewGuid().ToString("N"));
            try
            {
                JianghuYoulingPaths.Root = Path.Combine(root, "persist");
                Check(!new TtsSettings().DialogueOnly, "new settings default to full reading");
                Check(TtsSettings.TryLoad(out var absent) && !absent.DialogueOnly, "missing settings default to full reading");
                string path = Path.Combine(JianghuYoulingPaths.Settings, "tts_params.json");
                Directory.CreateDirectory(JianghuYoulingPaths.Settings);
                File.WriteAllText(path, "{\"voice\":\"fixture-voice\",\"model\":\"fixture-model\",\"speed\":1.2,\"vol\":0.8,\"pitch\":2,\"emotion\":\"happy\",\"dynamic\":false}");
                Check(TtsSettings.TryLoad(out var settings) && !settings.DialogueOnly,
                    "an existing settings document without the option still reads everything");
                settings.DialogueOnly = true;
                Check(settings.Save(), "dialogue-only setting durably saves");
                Check(TtsSettings.TryLoad(out var loaded) && loaded.DialogueOnly, "reopen restores dialogue-only selection");
                Check(loaded.Voice == "fixture-voice" && loaded.Model == "fixture-model"
                    && loaded.Speed == 1.2f && loaded.Vol == 0.8f && loaded.Pitch == 2
                    && loaded.Emotion == "happy" && !loaded.Dynamic, "saving the mode preserves other speech settings");
                Check(JObject.Parse(File.ReadAllText(path))["dialogueOnly"].Type == JTokenType.Boolean,
                    "the setting uses a strict JSON boolean");

                using (var transaction = SettingsBatchTransaction.Capture(new[] { path }))
                {
                    loaded.DialogueOnly = false;
                    Check(loaded.Save(), "mode can be changed within a settings batch");
                    Check(transaction.Rollback(), "a later settings failure rolls the batch back");
                }
                Check(TtsSettings.TryLoad(out loaded) && loaded.DialogueOnly, "rollback restores the previous reading mode");
                File.WriteAllText(path, "{broken");
                Check(TtsSettings.TryLoad(out loaded) && loaded.DialogueOnly, "backup recovery preserves the selected mode");
                loaded.DialogueOnly = false;
                Check(loaded.Save() && TtsSettings.TryLoad(out loaded) && !loaded.DialogueOnly,
                    "full reading can be durably restored");

                int fixture = 0;
                foreach (string invalid in new[] { "{\"dialogueOnly\":\"true\"}", "{\"dialogueOnly\":1}",
                    "{\"dialogueOnly\":null}", "{\"dialogueOnly\":[]}",
                    "{\"dialogueOnly\":true,\"dialogueOnly\":false}", "{\"dialogueOnly\":true,\"extra\":false}" })
                {
                    JianghuYoulingPaths.Root = Path.Combine(root, "invalid-" + fixture++);
                    Directory.CreateDirectory(JianghuYoulingPaths.Settings);
                    File.WriteAllText(Path.Combine(JianghuYoulingPaths.Settings, "tts_params.json"), invalid);
                    Check(!TtsSettings.TryLoad(out _), "malformed reading mode fails closed instead of playing narration");
                }
                JianghuYoulingPaths.Root = Path.Combine(root, "blocked-save");
                Directory.CreateDirectory(Path.Combine(JianghuYoulingPaths.Settings, "tts_params.json"));
                Check(!new TtsSettings { DialogueOnly = true }.Save(), "storage failure is not reported as a successful save");
            }
            finally
            {
                JianghuYoulingPaths.Root = priorRoot;
                string full = Path.GetFullPath(root);
                string tempPrefix = temp.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (full.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(full).StartsWith("JHYL_DialogueSpeech_", StringComparison.Ordinal)
                    && Directory.Exists(full)) Directory.Delete(full, true);
            }
        }
    }
}
