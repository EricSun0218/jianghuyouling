using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using JianghuYouling.Effects;

namespace JianghuYouling
{
    /// <summary>
    /// Pure code-side contact-mode resolver. Every caller uses the backend's single
    /// authoritative scene snapshot instead of asking an LLM to infer distance from old
    /// dialogue or racing separate block, companion and captive queries.
    /// </summary>
    internal static class ConversationContactModeResolver
    {
        internal static IEnumerator Resolve(int taiwuId, int npcId, int worldGeneration,
            CancellationToken cancellationToken, Action<bool> onRemote, bool? knownCaptive = null,
            bool authoritativeEventTargetPresent = false)
        {
            if (taiwuId <= 0 || npcId < 0 || !WorldLifecycle.IsSameWorld(worldGeneration))
                yield break;

            // Adventure/event targets are already being interacted with face to face. They
            // are not guaranteed to appear in the normal map-block roster while the event
            // overlay is active, so a block-only query would incorrectly flip them to
            // long-distance transmission.
            if (authoritativeEventTargetPresent)
            {
                onRemote?.Invoke(false);
                yield break;
            }

            bool queryDone = false;
            bool queryReliable = false;
            List<int> present = null;
            try
            {
                EffectHandler.QueryTaiwuScenePresence(taiwuId, new[] { npcId },
                    (ok, ids) =>
                    {
                        queryReliable = ok;
                        present = ids;
                        queryDone = true;
                    });
            }
            catch
            {
                if (!cancellationToken.IsCancellationRequested
                    && WorldLifecycle.IsSameWorld(worldGeneration))
                    onRemote?.Invoke(true);
                yield break;
            }

            float deadline = Time.unscaledTime + 3f;
            while (!queryDone
                && Time.unscaledTime < deadline
                && !cancellationToken.IsCancellationRequested
                && WorldLifecycle.IsSameWorld(worldGeneration))
                yield return null;

            if (cancellationToken.IsCancellationRequested
                || !WorldLifecycle.IsSameWorld(worldGeneration))
                yield break;

            bool physicallyPresent = queryReliable && present != null && present.Contains(npcId);
            onRemote?.Invoke(!physicallyPresent);
        }
    }
}
