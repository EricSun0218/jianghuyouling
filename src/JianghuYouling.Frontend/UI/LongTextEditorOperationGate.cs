using System;

namespace JianghuYouling
{
    /// <summary>
    /// Serializes settings/persona/worldbook editor reads, saves and resets. A read also owns the
    /// lease until its UI snapshot is accepted, so a late old read cannot overwrite a newer save
    /// on screen. A window may be hidden and reopened while its worker is still running; only the
    /// operation that owns this lease may release it. This also prevents a settings rollback from
    /// overwriting a newer standalone editor save.
    /// </summary>
    internal static class LongTextEditorOperationGate
    {
        private static readonly object Gate = new object();
        private static string _owner;
        private static long _token;
        private static long _nextToken;

        internal static bool TryAcquire(string owner, out long token)
        {
            token = 0;
            if (string.IsNullOrWhiteSpace(owner)) return false;
            lock (Gate)
            {
                if (_token != 0) return false;
                token = unchecked(++_nextToken);
                if (token == 0) token = unchecked(++_nextToken);
                _owner = owner;
                _token = token;
                return true;
            }
        }

        internal static bool IsOwner(string owner, long token)
        {
            if (token == 0) return false;
            lock (Gate)
                return _token == token && string.Equals(_owner, owner, StringComparison.Ordinal);
        }

        internal static bool IsBusy
        {
            get { lock (Gate) return _token != 0; }
        }

        internal static bool Release(string owner, long token)
        {
            lock (Gate)
            {
                if (_token != token || token == 0 || !string.Equals(_owner, owner, StringComparison.Ordinal))
                    return false;
                _token = 0;
                _owner = null;
                return true;
            }
        }
    }
}
