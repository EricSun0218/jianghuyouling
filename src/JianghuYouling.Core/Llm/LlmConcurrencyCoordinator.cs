using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace JianghuYouling.Core.Llm
{
    /// <summary>
    /// LLM 工作负载分车道。前台请求在全局 provider 槽位释放时优先，
    /// 月度 Agent 在没有前台候补时可以借满全部全局槽位；
    /// 其他后台任务另由 OpenAiCompatibleClient 的两槽闸限流。
    /// </summary>
    public enum LlmWorkloadKind
    {
        Foreground = 0,
        MonthlyAgent = 1,
        Background = 2,
    }

    public static class LlmWorkloadPolicy
    {
        public static LlmWorkloadKind Classify(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return LlmWorkloadKind.Foreground;

            // Only the two state-changing monthly agents may borrow the third provider slot.
            // Match these before the broad "过月" background fallback.
            if (tag.IndexOf("过月江湖事件 Agent", StringComparison.Ordinal) >= 0
                || tag.IndexOf("同道过月 Agent", StringComparison.Ordinal) >= 0)
                return LlmWorkloadKind.MonthlyAgent;

            if (tag.IndexOf("过月", StringComparison.Ordinal) >= 0
                || tag.IndexOf("画像", StringComparison.Ordinal) >= 0
                || tag.IndexOf("记忆", StringComparison.Ordinal) >= 0
                || tag.IndexOf("梗概", StringComparison.Ordinal) >= 0
                || tag.IndexOf("对话整理", StringComparison.Ordinal) >= 0
                || tag.IndexOf("灵儿·主动", StringComparison.Ordinal) >= 0)
                return LlmWorkloadKind.Background;

            return LlmWorkloadKind.Foreground;
        }
    }

    /// <summary>
    /// 有界、无忙轮询的异步优先闸。已经取得的租约不会被抢占；前台候补优先取得
    /// 新释放的槽位，但连续让行次数有上限，避免持续聊天把月度/后台永久饿死。
    /// 两条队列内部都保持到达顺序。
    /// </summary>
    public sealed class LlmPriorityConcurrencyGate
    {
        private sealed class Waiter
        {
            public readonly bool Foreground;
            public readonly TaskCompletionSource<Lease> Completion =
                new TaskCompletionSource<Lease>(TaskCreationOptions.RunContinuationsAsynchronously);
            public LinkedListNode<Waiter> Node;
            public CancellationTokenRegistration Cancellation;

            public Waiter(bool foreground)
            {
                Foreground = foreground;
            }
        }

        public sealed class Lease : IDisposable
        {
            private LlmPriorityConcurrencyGate _owner;

            internal Lease(LlmPriorityConcurrencyGate owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                owner?.Release();
            }
        }

        private readonly object _sync = new object();
        private const int MaxConsecutiveForegroundGrants = 3;
        private readonly int _capacity;
        private readonly LinkedList<Waiter> _foreground = new LinkedList<Waiter>();
        private readonly LinkedList<Waiter> _regular = new LinkedList<Waiter>();
        private int _inUse;
        private int _consecutiveForegroundGrants;

        public LlmPriorityConcurrencyGate(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
        }

        public int Capacity => _capacity;

        public int InUse
        {
            get { lock (_sync) return _inUse; }
        }

        public int WaitingForeground
        {
            get { lock (_sync) return _foreground.Count; }
        }

        public int WaitingRegular
        {
            get { lock (_sync) return _regular.Count; }
        }

        public Task<Lease> AcquireAsync(LlmWorkloadKind workload, CancellationToken cancellationToken)
        {
            bool foreground = workload == LlmWorkloadKind.Foreground;
            lock (_sync)
            {
                if (cancellationToken.IsCancellationRequested)
                    return Task.FromCanceled<Lease>(cancellationToken);

                if (_inUse < _capacity && _foreground.Count == 0 && _regular.Count == 0)
                {
                    _inUse++;
                    return Task.FromResult(new Lease(this));
                }

                var waiter = new Waiter(foreground);
                waiter.Node = foreground
                    ? _foreground.AddLast(waiter)
                    : _regular.AddLast(waiter);

                if (cancellationToken.CanBeCanceled)
                {
                    waiter.Cancellation = cancellationToken.Register(
                        state =>
                        {
                            var pair = (Tuple<LlmPriorityConcurrencyGate, Waiter>)state;
                            pair.Item1.Cancel(pair.Item2);
                        },
                        Tuple.Create(this, waiter));
                    // Dispose registrations away from the gate lock. This also covers the
                    // synchronous already-cancelled Register path without leaking callbacks.
                    waiter.Completion.Task.ContinueWith(
                        (_, state) => ((Waiter)state).Cancellation.Dispose(),
                        waiter,
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default);
                }

                DrainLocked();
                return waiter.Completion.Task;
            }
        }

        private void Cancel(Waiter waiter)
        {
            lock (_sync)
            {
                if (waiter == null || waiter.Node == null) return;
                if (waiter.Foreground) _foreground.Remove(waiter.Node);
                else _regular.Remove(waiter.Node);
                waiter.Node = null;
                waiter.Completion.TrySetCanceled();
                DrainLocked();
            }
        }

        private void Release()
        {
            lock (_sync)
            {
                if (_inUse <= 0)
                    throw new InvalidOperationException("LLM concurrency lease released more than once");
                _inUse--;
                DrainLocked();
            }
        }

        private void DrainLocked()
        {
            while (_inUse < _capacity)
            {
                Waiter waiter;
                bool regularNeedsFairTurn = _regular.Count > 0
                    && _consecutiveForegroundGrants >= MaxConsecutiveForegroundGrants;
                if (_foreground.Count > 0 && !regularNeedsFairTurn)
                {
                    waiter = _foreground.First.Value;
                    _consecutiveForegroundGrants = _regular.Count > 0
                        ? _consecutiveForegroundGrants + 1
                        : 0;
                }
                else if (_regular.Count > 0)
                {
                    waiter = _regular.First.Value;
                    _consecutiveForegroundGrants = 0;
                }
                else
                    return;

                if (waiter.Foreground) _foreground.RemoveFirst();
                else _regular.RemoveFirst();
                waiter.Node = null;
                _inUse++;
                waiter.Completion.TrySetResult(new Lease(this));
            }
        }
    }
}
