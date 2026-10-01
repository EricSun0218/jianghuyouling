using System;

namespace JianghuYouling.Core.Behavior
{
    /// <summary>
    /// Canonical actor/target contract shared by companion gating and execution.
    /// Tool names passed here must already be normalized by the caller.
    /// </summary>
    public static class CompanionMutationTargetPolicy
    {
        public static string TargetField(string normalizedTool)
        {
            switch ((normalizedTool ?? "").Trim())
            {
                case "send_message":
                case "relate":
                case "enmity":
                case "heal":
                case "detox":
                case "regulate_breath":
                case "dissolve_relation":
                case "adjust_favor":
                case "adjust_mood":
                case "adjust_fame":
                case "spend_night":
                case "matchmake":
                case "barter":
                case "write_book":
                case "teach":
                case "tell_secret":
                case "gift_item":
                case "gift_silver":
                case "kill":
                case "poison":
                case "capture":
                    return "target";
                case "steal":
                    return "victim";
                default:
                    return null;
            }
        }

        public static bool DefaultsToTaiwu(string normalizedTool)
        {
            switch ((normalizedTool ?? "").Trim())
            {
                case "heal":
                case "detox":
                case "regulate_breath":
                case "dissolve_relation":
                case "adjust_favor":
                case "spend_night":
                case "barter":
                case "write_book":
                case "tell_secret":
                case "gift_item":
                case "gift_silver":
                    return true;
                default:
                    return false;
            }
        }

        public static bool HasFixedTaiwuTarget(string normalizedTool)
        {
            string tool = (normalizedTool ?? "").Trim();
            return string.Equals(tool, "set_relation", StringComparison.Ordinal)
                || string.Equals(tool, "sect_support", StringComparison.Ordinal);
        }

        /// <summary>
        /// Returns Taiwu for fixed-target mutations, or for default-target mutations
        /// whose model argument is absent. Explicit names are resolved by the caller.
        /// </summary>
        public static int ImplicitTargetId(string normalizedTool, string rawTarget, int taiwuId)
        {
            if (taiwuId <= 0) return 0;
            if (HasFixedTaiwuTarget(normalizedTool)) return taiwuId;
            return DefaultsToTaiwu(normalizedTool) && string.IsNullOrWhiteSpace(rawTarget)
                ? taiwuId : 0;
        }
    }
}
