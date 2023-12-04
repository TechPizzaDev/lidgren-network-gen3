using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Lidgren.Network
{
    internal abstract class NetSenderChannel : NetChannel
    {
        private List<NetValueTimeoutAwaitable> _idleWaiters = new();

        protected int _sendStart;
        protected NetBitArray _receivedAcks;

        // access this directly to queue things in this channel
        public NetQueue<NetOutgoingMessage> QueuedSends { get; } = new(16);

        protected NetSenderChannel(NetConnection connection, int windowSize) : base(connection, windowSize)
        {
            _receivedAcks = new NetBitArray(NetConstants.SequenceNumbers);
        }

        protected override void OnWindowSizeChanged()
        {
        }

        public abstract int GetAllowedSends();

        public abstract NetSendResult Enqueue(NetOutgoingMessage message);
        
        public abstract NetSocketResult SendQueuedMessages(TimeSpan now);

        public override void Reset()
        {
            QueuedSends.Clear();
            _receivedAcks.Clear();
            _sendStart = 0;
            base.Reset();
        }

        public abstract NetSocketResult ReceiveAcknowledge(TimeSpan now, int sequenceNumber);

        public int GetFreeWindowSlots()
        {
            return GetAllowedSends() - QueuedSends.Count;
        }

        protected void NotifyIdleWaiters(TimeSpan now, int allowedSends)
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

        public ValueTask<int> WaitForIdleAsync(int millisecondsTimeout, CancellationToken cancellationToken)
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
    }
}
