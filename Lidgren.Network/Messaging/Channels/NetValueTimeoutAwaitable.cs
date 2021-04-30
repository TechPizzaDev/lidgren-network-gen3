using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace Lidgren.Network
{
    internal class NetValueTimeoutAwaitable : IValueTaskSource<int>
    {
        private static NetQueue<NetValueTimeoutAwaitable> _pool = new(16);

        private ManualResetValueTaskSourceCore<int> _mrvtsc;
        private CancellationToken _cancellationToken;

        public TimeSpan TimeoutTarget { get; private set; }

        public static NetValueTimeoutAwaitable Rent()
        {
            if (!_pool.TryDequeue(out NetValueTimeoutAwaitable? item))
            {
                item = new NetValueTimeoutAwaitable();
            }
            return item;
        }

        public ValueTask<int> RunAsync(TimeSpan timeoutTarget, CancellationToken cancellationToken)
        {
            TimeoutTarget = timeoutTarget;
            _cancellationToken = cancellationToken;

            return new ValueTask<int>(this, _mrvtsc.Version);
        }

        public bool TryComplete(TimeSpan now, int value)
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                _mrvtsc.SetException(new OperationCanceledException());
                return true;
            }
            if (value > 0 || now > TimeoutTarget)
            {
                _mrvtsc.SetResult(value);
                return true;
            }
            return false;
        }

        public int GetResult(short token)
        {
            int result = _mrvtsc.GetResult(token);
            _mrvtsc.Reset();

            if (_pool.Count < 16)
            {
                _pool.Enqueue(this);
            }

            return result;
        }

        public ValueTaskSourceStatus GetStatus(short token)
        {
            return _mrvtsc.GetStatus(token);
        }

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            _mrvtsc.OnCompleted(continuation, state, token, flags);
        }
    }
}
