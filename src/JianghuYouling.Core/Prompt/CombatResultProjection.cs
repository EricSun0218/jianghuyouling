namespace JianghuYouling.Core.Prompt
{
    /// <summary>
    /// 将游戏本体的原生战斗结算码投影成可持久化的会话事实。
    /// 数值与 b24185552 GameData.Domains.Combat.CombatResultType / CombatType 保持一致；
    /// 未识别的新值必须如实保留数值，不能猜测胜负。
    /// </summary>
    public static class CombatResultProjection
    {
        public static string Narrative(sbyte combatType)
            => "（方才的" + CombatTypeName(combatType) + "已经分出结果。）";

        public static string ToolResult(string npcName, sbyte combatResult, sbyte combatType)
        {
            string opponent = string.IsNullOrWhiteSpace(npcName) ? "对方" : npcName.Trim();
            string result;
            switch (combatResult)
            {
                case 0: result = "太吾胜出"; break;
                case 1: result = opponent + "胜出，太吾落败"; break;
                case 2: result = "太吾主动脱离战斗"; break;
                case 3: result = opponent + "主动脱离战斗，太吾胜出"; break;
                case 4: result = "太吾战败身亡"; break;
                case 5: result = opponent + "战败身亡，太吾胜出"; break;
                default: result = "游戏本体返回未识别的战果编号 " + combatResult; break;
            }

            string text = "与" + opponent + "的" + CombatTypeName(combatType) + "结算：" + result + "。";
            if (combatType == 2 && IsPlayerWin(combatResult))
                text += "后续公开处置、秘密处置或放过对方，仍以游戏本体随后给出的选择为准。";
            return text;
        }

        public static string CombatTypeName(sbyte combatType)
        {
            switch (combatType)
            {
                case 0: return "切磋";
                case 1: return "相搏";
                case 2: return "生死斗";
                case 3: return "测试战斗";
                default: return "战斗";
            }
        }

        private static bool IsPlayerWin(sbyte combatResult)
            => combatResult == 0 || combatResult == 3 || combatResult == 5;
    }
}
