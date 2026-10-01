using System;

namespace JianghuYouling.Core.Influence
{
    /// <summary>
    /// 对话反应的确定性默认策略。数值尺度以游戏原生 ChangeAlertness 调用为准：
    /// 普通交互通常是数百，断交/离异等重大事件才是数千至数万。
    /// </summary>
    public static class ConversationReactionPolicy
    {
        // b24185552: routine haircut interactions use -500/+200 and a complete failure uses
        // +1000. +8000..+30000 is reserved for severing friendship, sworn bonds or marriage.
        // A single ordinary model-authored chat reaction must never impersonate those events.
        public const int OrdinaryConversationAlertnessCap = 1000;

        /// <summary>
        /// 模型没有给出非零戒心变化时，由满意度补出温和联动：满意会降低戒心，
        /// 反感会提高戒心。满幅交谈最多 ±1000，远低于原生断交/离异量级。
        /// </summary>
        public static int ResolveAlertnessShift(int satisfaction, int requestedShift, bool supportsAlertness)
        {
            if (!supportsAlertness) return 0;
            if (requestedShift != 0)
                return Clamp(requestedShift, -OrdinaryConversationAlertnessCap, OrdinaryConversationAlertnessCap);
            int sat = Clamp(satisfaction, -100, 100);
            return -sat * 10;
        }

        private static int Clamp(int value, int min, int max)
            => Math.Max(min, Math.Min(max, value));
    }
}
