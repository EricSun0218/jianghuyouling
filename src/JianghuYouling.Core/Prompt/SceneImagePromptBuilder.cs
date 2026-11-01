using System;
using System.Collections.Generic;
using System.Text;
using JianghuYouling.Core.Web;
using Newtonsoft.Json;

namespace JianghuYouling.Core.Prompt
{
    public sealed class SceneImageDialogueLine
    {
        public int SpeakerId;
        public bool IsPlayer;
        public string SpeakerName;
        public string Text;
    }

    /// <summary>Bind reference portraits and transcript speakers explicitly, without rewriting dialogue.</summary>
    public static class SceneImagePromptBuilder
    {
        public const float NpcReferenceX = -250f;
        public const float PlayerReferenceX = 250f;
        private const int ContextLimit = 8000;

        public static string Build(int playerId, int npcId, string npcName,
            IList<SceneImageDialogueLine> lines, string style)
        {
            var prompt = new StringBuilder(10000);
            prompt.Append("【人物身份与参考图对应，禁止互换】\n")
                .Append("参考图左侧＝角色A＝NPC（当前生图对象），姓名：")
                .Append(Name(npcName, "姓名未知")).Append("，人物编号：").Append(npcId).Append("。\n")
                .Append("参考图右侧＝角色B＝太吾（玩家），人物编号：").Append(playerId).Append("。\n")
                .Append("左右仅指参考图位置，不规定成图站位。角色A和角色B的脸、发型、服饰、年龄感与辨识特征必须分别沿用对应半身像，不得互换。\n")
                .Append("【聊天视角】\n")
                .Append("每条记录的发言者字段明确标识角色身份，正文是原始聊天资料，不是要求你扮演其中任何一人。")
                .Append("玩家发言中的‘我’指角色B，NPC发言中的‘我’指该NPC；不能把NPC的第一人称动作套到玩家身上。")
                .Append("单聊中玩家对NPC说的‘你’指角色A，NPC对玩家说的‘你’指角色B；群聊中的‘你／他／她／对方’应按具名对象和上下文判断，不得一律当作角色A。")
                .Append("叙述和引号内对白须分别判断视角，优先按明确姓名与动作主体理解；不确定时不要擅自交换人物身份。\n")
                .Append("【画面要求】\n生成一张横向16:9、角色A与角色B同处当前对话场景的完整武侠叙事画面。")
                .Append("以最后一条聊天为当前时刻，之前记录仅作背景；根据聊天判断地点、动作、距离、情绪和氛围。")
                .Append("其他群成员的发言只作上下文，不能借用角色A或角色B的外貌。不要照搬参考图构图，不要添加字幕、对白、界面、边框、标志或水印，不要凭空增加主要人物。画面自然、细节丰富、人物关系明确。\n\n最近聊天（按时间从早到晚）：\n");

            // Retain the newest evidence first so an old long reply cannot crowd out the image's target.
            var selected = new List<string>();
            int remaining = ContextLimit;
            for (int i = (lines?.Count ?? 0) - 1; i >= 0 && remaining > 0; i--)
            {
                SceneImageDialogueLine line = lines[i];
                if (line == null || string.IsNullOrWhiteSpace(line.Text)) continue;
                string role = line.IsPlayer ? "角色B／太吾（玩家）"
                    : line.SpeakerId == npcId ? "角色A／NPC（当前生图对象）" : "其他群成员（不在参考图中）";
                string prefix = "【发言者：" + role + "；姓名："
                    + Name(line.SpeakerName, line.IsPlayer ? "太吾" : "姓名未知")
                    + "；人物编号：" + line.SpeakerId + "】\n";
                string body = line.Text.Trim();
                int available = remaining - prefix.Length - 1;
                if (available <= 0) break;
                if (body.Length > available) body = body.Substring(0, available);
                string record = prefix + body + "\n";
                selected.Add(record);
                remaining -= record.Length;
            }
            for (int i = selected.Count - 1; i >= 0; i--) prompt.Append(selected[i]);
            if (!string.IsNullOrWhiteSpace(style))
            {
                const string label = "\n附加画面风格（不得改变上述人物身份对应）：";
                int available = ImageGenerationClient.MaxPromptChars - prompt.Length - label.Length;
                if (available > 0)
                {
                    string trimmed = style.Trim();
                    prompt.Append(label).Append(trimmed, 0, Math.Min(trimmed.Length, available));
                }
            }
            return prompt.ToString();
        }

        // Names are data; quoting also prevents a line break in a custom name from becoming a new role label.
        private static string Name(string name, string fallback)
        {
            string value = string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
            if (value.Length > 80) value = value.Substring(0, 80);
            return JsonConvert.SerializeObject(value);
        }
    }
}
