using System;

namespace JianghuYouling.Core.Tools
{
    /// <summary>
    /// Enforces objective target invariants for hostile conversation tools after the model has
    /// selected an action. Motivation and intent belong to the scene-specific skill prompt; this
    /// boundary never parses player prose or requires a particular relationship as authorization.
    /// </summary>
    public static class HighRiskActionAuthorization
    {
        public static bool IsHighRiskTool(string toolName)
            => string.Equals(toolName, "kill", StringComparison.Ordinal)
                || string.Equals(toolName, "capture", StringComparison.Ordinal)
                || string.Equals(toolName, "poison", StringComparison.Ordinal);

        /// <summary>
        /// kill/capture can never target Taiwu. All other motivation and relationship decisions
        /// are made by the model from the current scene skill and then checked by authoritative
        /// presence, identity, life-state and refinement preconditions in the execution layer.
        /// </summary>
        public static bool IsAuthorized(string toolName, bool targetIsTaiwu,
            out string rejectionReason)
        {
            rejectionReason = null;
            if (!IsHighRiskTool(toolName)) return true;

            if (targetIsTaiwu && (toolName == "kill" || toolName == "capture"))
            {
                rejectionReason = toolName == "kill" ? "不可加害太吾" : "不可擒拿太吾";
                return false;
            }
            return true;
        }
    }
}
