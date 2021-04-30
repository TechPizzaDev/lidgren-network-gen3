using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Lidgren.Network
{
    internal abstract class NetSenderChannel
    {
        private List<NetValueTimeoutAwaitable> _idleWaiters = new();

        // access this directly to queue things in this channel
        public NetQueue<NetOutgoingMessage> QueuedSends { get; } = new NetQueue<NetOutgoingMessage>(16);

        public abstract int WindowSize { get; }

        public abstract int GetAllowedSends();

        public abstract NetSendResult Enqueue(NetOutgoingMessage message);
        public abstract NetSocketResult SendQueuedMessages(TimeSpan now);
        public abstract void Reset();
        public abstract NetSocketResult ReceiveAcknowledge(TimeSpan now, int sequenceNumber);

        public virtual int GetFreeWindowSlots()
        {
            return GetAllowedSends() - QueuedSends.Count;
        }

        protected virtual void NotifyIdleWaiters(TimeSpan now, int allowedSends)
        {
            int freeWindowSlots = allowedSends - QueuedSends.Count;

            for (int i = _idleWaiters.Count; i-- > 0;)
            {
                var waiter = _idleWaiters[i];
                if (waiter.TryComplete(now, freeWindowSlots))
                {
                    freeWindowSlots = GetFreeWindowSlots();
                    _idleWaiters.RemoveAt(i);
                    continue;
                }
            }
        }

        public virtual ValueTask<int> WaitForIdleAsync(int millisecondsTimeout, CancellationToken cancellationToken)
        {
            if (millisecondsTimeout < 1)
            {
                return new ValueTask<int>(GetFreeWindowSlots());
            }

            var waiter = NetValueTimeoutAwaitable.Rent();
            var timeoutTarget = NetTime.Now + TimeSpan.FromMilliseconds(millisecondsTimeout);
            ValueTask<int> task = waiter.RunAsync(timeoutTarget, cancellationToken);
            _idleWaiters.Add(waiter);
            return task;
        }

        public virtual int WaitForIdle(int millisecondsTimeout, CancellationToken cancellationToken)
        {
            var task = WaitForIdleAsync(millisecondsTimeout, cancellationToken);
            while (!task.IsCompleted)
            {
                Thread.Sleep(1);
            }
            return task.Result;
        }
    }
}
