using System;
using System.Collections;

namespace JianghuYouling.Core.Behavior
{
    public static class MonthlyNativeSaveBarrier
    {
        /// <summary>
        /// Month-state zero can precede the end of the native save. Let the notification batch
        /// settle, then wait without spending RPC deadlines or monthly attempts. A new month or
        /// world change exits the wait so the caller's existing cancellation guards can take over.
        /// </summary>
        public static IEnumerator Wait(Func<bool> worldIsCurrent, Func<bool> isSaving,
            Func<bool> isAdvancing, Action onWaiting = null)
        {
            yield return null;
            bool reported = false;
            while (worldIsCurrent() && !isAdvancing() && isSaving())
            {
                if (!reported) { reported = true; onWaiting?.Invoke(); }
                yield return null;
            }
        }
    }
}
