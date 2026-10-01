using System;

namespace JianghuYouling.Core.Memory
{
    /// <summary>
    /// Decides whether a companion-monthly action is directly knowable by its NPC target.
    /// Covert actions must not turn the target into an omniscient witness.
    /// </summary>
    public static class CompanionTargetAwarenessPolicy
    {
        public static bool ShouldRemember(string kind, string summary)
        {
            switch ((kind ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "gift_item":
                case "gift_silver":
                case "barter":
                case "teach":
                case "book":
                case "secret":
                case "heal":
                case "detox":
                case "regulate_breath":
                case "relationship":
                case "relationship_end":
                case "enmity":
                case "capture":
                case "intimacy":
                    return true;
                case "steal":
                    return (summary ?? string.Empty).IndexOf("败露",
                        StringComparison.Ordinal) >= 0;
                default:
                    return false;
            }
        }
    }
}
