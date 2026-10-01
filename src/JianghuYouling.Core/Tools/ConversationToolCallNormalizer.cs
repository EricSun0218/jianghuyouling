using System;
using System.Collections.Generic;
using JianghuYouling.Core.Llm;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Tools
{
    /// <summary>
    /// Corrects objective schema invariants after the model has selected a tool. It never parses
    /// player prose to choose, redirect, force, or cancel an action.
    /// </summary>
    public static class ConversationToolCallNormalizer
    {
        /// <summary>
        /// Normalizes start_combat calls in-place. The game action always targets current Taiwu;
        /// malformed enum values receive safe defaults without reinterpreting the conversation.
        /// </summary>
        public static bool Normalize(IList<LlmToolCall> calls)
        {
            if (calls == null || calls.Count == 0) return false;
            bool changed = false;
            for (int i = 0; i < calls.Count; i++)
            {
                LlmToolCall call = calls[i];
                if (call == null) continue;

                if (!string.Equals(call.Name, "start_combat", StringComparison.Ordinal))
                    continue;

                JObject args;
                try { args = JObject.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson); }
                catch { args = new JObject(); }

                if (!string.Equals(args.Value<string>("opponent"), "taiwu", StringComparison.Ordinal))
                {
                    args["opponent"] = "taiwu";
                    changed = true;
                }

                string mode = args.Value<string>("mode");
                if (mode != "play" && mode != "beat" && mode != "die")
                {
                    args["mode"] = "beat";
                    changed = true;
                }

                string initiator = args.Value<string>("initiator");
                if (initiator != "taiwu" && initiator != "npc")
                {
                    args["initiator"] = "npc";
                    changed = true;
                }

                string normalized = args.ToString(Newtonsoft.Json.Formatting.None);
                if (!string.Equals(call.ArgumentsJson, normalized, StringComparison.Ordinal))
                    call.ArgumentsJson = normalized;
            }
            return changed;
        }

    }
}
