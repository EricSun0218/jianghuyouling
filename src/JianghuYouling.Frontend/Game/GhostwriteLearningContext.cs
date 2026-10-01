using System.Collections.Generic;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Prompt;

namespace JianghuYouling
{
    /// <summary>
    /// Rebuilds a bounded imitation profile from current-world authoritative chat histories.
    /// No second learned-state file is maintained, so rollback naturally changes the source corpus.
    /// </summary>
    internal static class GhostwriteLearningContext
    {
        internal static GhostwriteImitationProfile Build(int taiwuId,
            IEnumerable<string> localNewestFirst = null)
        {
            if (!GhostwriteSelfLearningStore.Load()) return null;
            var merged = new List<string>();
            Append(merged, localNewestFirst, 12);
            Append(merged, TalkOrchestrator.RecentPlayerSpeechExamples(taiwuId, 18), 18);
            Append(merged, AssistantOrchestrator.RecentPlayerSpeechExamples(taiwuId, 10), 10);
            Append(merged, GroupChatOrchestrator.RecentPlayerSpeechExamples(taiwuId, 12), 12);
            return GhostwriteImitationProfileBuilder.Build(merged);
        }

        internal static void AddHistoricalSamples(List<LlmMessage> messages,
            GhostwriteImitationProfile profile)
        {
            if (messages == null || profile == null || string.IsNullOrWhiteSpace(profile.SamplesJson))
                return;
            messages.Add(new LlmMessage("user", profile.SamplesJson)
            {
                IsUntrustedContextData = true,
            });
        }

        private static void Append(List<string> destination, IEnumerable<string> source, int cap)
        {
            if (destination == null || source == null || cap <= 0) return;
            int count = 0;
            foreach (string value in source)
            {
                destination.Add(value);
                if (++count >= cap) break;
            }
        }
    }
}
