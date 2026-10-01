using System;

namespace JianghuYouling.Core.Behavior
{
    public enum NavigationRowAction
    {
        Open,
        Hide,
    }

    public static class NavigationRowBindingPolicy
    {
        /// <summary>
        /// Pooled sidebar rows may be rebound while an input event is queued. Only dispatch
        /// a row action when the captured binding still matches the current entry identity.
        /// </summary>
        public static bool IsCurrent(string boundIdentity, string entryIdentity)
            => !string.IsNullOrWhiteSpace(boundIdentity)
                && string.Equals(boundIdentity, entryIdentity, StringComparison.Ordinal);
    }

    /// <summary>
    /// Pointer-down/dispatch guard for pooled conversation rows. Unity may rebind a visible
    /// row between pointer-down and Button.onClick; comparing two fields read at click time
    /// cannot detect that race. This guard captures identity+revision at pointer-down and
    /// consumes the action only if the row still owns that exact binding.
    /// </summary>
    public sealed class NavigationRowBindingGuard
    {
        private struct Capture
        {
            public bool Present;
            public long Revision;
            public string Identity;
        }

        private long _revision;
        private string _identity;
        private Capture _open;
        private Capture _hide;

        public string Identity => _identity;
        public long Revision => _revision;

        public void Bind(string identity)
        {
            string normalized = string.IsNullOrWhiteSpace(identity) ? null : identity;
            if (string.Equals(_identity, normalized, StringComparison.Ordinal)) return;
            _identity = normalized;
            _revision++;
            _open = default(Capture);
            _hide = default(Capture);
        }

        public void Clear()
        {
            _identity = null;
            _revision++;
            _open = default(Capture);
            _hide = default(Capture);
        }

        public void CapturePointerDown(NavigationRowAction action)
        {
            var captured = new Capture
            {
                Present = !string.IsNullOrWhiteSpace(_identity),
                Revision = _revision,
                Identity = _identity,
            };
            if (action == NavigationRowAction.Hide) _hide = captured;
            else _open = captured;
        }

        public bool TryConsume(NavigationRowAction action, out string identity)
        {
            Capture captured = action == NavigationRowAction.Hide ? _hide : _open;
            if (action == NavigationRowAction.Hide) _hide = default(Capture);
            else _open = default(Capture);
            bool current = captured.Present
                && captured.Revision == _revision
                && NavigationRowBindingPolicy.IsCurrent(captured.Identity, _identity);
            identity = current ? captured.Identity : null;
            return current;
        }
    }
}
