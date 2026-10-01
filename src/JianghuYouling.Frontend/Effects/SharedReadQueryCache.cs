using System;
using System.Collections.Generic;

namespace JianghuYouling.Effects
{
    /// <summary>
    /// Tiny cross-workflow single-flight cache for authoritative read-only RPCs. It deliberately
    /// stores no inventory, secrets or transcript/model state. A landed mutation invalidates both
    /// completed and in-flight entries, so consumers never reuse a pre-mutation relationship or
    /// presence snapshot.
    /// </summary>
    internal static class SharedReadQueryCache
    {
        private sealed class Cached
        {
            public object Value;
            public long ExpiresUtcTicks;
        }

        private sealed class Pending
        {
            public readonly List<Action<object>> Callbacks = new List<Action<object>>();
            public object InvalidatedValue;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Cached> Values =
            new Dictionary<string, Cached>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Pending> PendingCalls =
            new Dictionary<string, Pending>(StringComparer.Ordinal);

        /// <returns>true when a cached value was served or this caller joined an existing RPC;
        /// false when the caller owns the RPC and must eventually call Complete.</returns>
        public static bool TryServeOrJoin<T>(string key, Action<T> callback, T invalidatedValue)
        {
            if (string.IsNullOrWhiteSpace(key) || callback == null) return false;
            object immediate = null;
            bool serve = false;
            lock (Gate)
            {
                if (Values.TryGetValue(key, out Cached cached))
                {
                    if (cached != null && cached.ExpiresUtcTicks > DateTime.UtcNow.Ticks
                        && cached.Value is T)
                    {
                        immediate = cached.Value;
                        serve = true;
                    }
                    else Values.Remove(key);
                }
                if (!serve)
                {
                    if (PendingCalls.TryGetValue(key, out Pending pending))
                    {
                        pending.Callbacks.Add(value => callback(value is T typed ? typed : invalidatedValue));
                        return true;
                    }
                    var owner = new Pending { InvalidatedValue = invalidatedValue };
                    owner.Callbacks.Add(value => callback(value is T typed ? typed : invalidatedValue));
                    PendingCalls[key] = owner;
                    return false;
                }
            }
            callback((T)immediate);
            return true;
        }

        public static void Complete<T>(string key, T value, bool cacheable, TimeSpan lifetime)
        {
            List<Action<object>> callbacks = null;
            lock (Gate)
            {
                if (!PendingCalls.TryGetValue(key, out Pending pending)) return;
                PendingCalls.Remove(key);
                callbacks = pending.Callbacks;
                if (cacheable && lifetime > TimeSpan.Zero)
                    Values[key] = new Cached
                    {
                        Value = value,
                        ExpiresUtcTicks = DateTime.UtcNow.Add(lifetime).Ticks,
                    };
            }
            foreach (Action<object> callback in callbacks)
                try { callback(value); } catch { }
        }

        public static void InvalidateAll()
        {
            List<KeyValuePair<List<Action<object>>, object>> canceled =
                new List<KeyValuePair<List<Action<object>>, object>>();
            lock (Gate)
            {
                Values.Clear();
                foreach (Pending pending in PendingCalls.Values)
                    if (pending != null)
                        canceled.Add(new KeyValuePair<List<Action<object>>, object>(
                            pending.Callbacks, pending.InvalidatedValue));
                PendingCalls.Clear();
            }
            foreach (var pending in canceled)
                foreach (Action<object> callback in pending.Key)
                    try { callback(pending.Value); } catch { }
        }
    }
}
