using System;

namespace JianghuYouling.Core.Tools
{
    /// <summary>
    /// Commit gate for assistant-owned settings: persistence is the commit point;
    /// process-local state is applied only after that commit succeeds.
    /// </summary>
    public static class SettingMutationCommit
    {
        public static bool PersistThenApply(Func<bool> persist, Action apply = null)
        {
            if (persist == null) return false;
            bool committed;
            try { committed = persist(); }
            catch { return false; }
            if (!committed) return false;
            apply?.Invoke();
            return true;
        }
    }
}
