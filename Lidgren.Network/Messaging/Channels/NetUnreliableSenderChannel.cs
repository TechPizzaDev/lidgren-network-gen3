using System;
using System.Threading;

namespace Lidgren.Network
{
    /// <summary>
    /// Sender part of Selective repeat ARQ for a particular NetChannel
    /// </summary>
    internal sealed class NetUnreliableSenderChannel : NetSenderChannel
    {
        private bool _doFlowControl;

        public NetUnreliableSenderChannel(NetConnection connection, int windowSize, NetDeliveryMethod method) :
            base(connection, windowSize)
        {
            _doFlowControl = true;
            if (method == NetDeliveryMethod.Unreliable &&
                connection.Peer.Configuration.SuppressUnreliableUnorderedAcks)
                _doFlowControl = false;
        }

        public override int GetAllowedSends()
        {
            if (!_doFlowControl)
                return int.MaxValue;

            int windowSize = WindowSize;
            int value = windowSize - NetUtility.PowOf2Mod(
                _sendStart + NetConstants.SequenceNumbers - _windowStart,
                windowSize);

            LidgrenException.Assert(value >= 0 && value <= windowSize);
            return value;
        }

        public override NetSendResult Enqueue(NetOutgoingMessage message)
        {
            NetConnection connection = Connection;
            int queueLen = QueuedSends.Count + 1;
            int left = GetAllowedSends();

            if (queueLen > left ||
                (message.ByteLength > connection.CurrentMTU &&
                connection._peerConfiguration.UnreliableSizeBehaviour == NetUnreliableSizeBehaviour.DropAboveMTU))
            {
                connection.Peer.Recycle(message);
                return NetSendResult.Dropped;
            }

            if (message.BitLength >= ushort.MaxValue &&
                connection._peerConfiguration.UnreliableSizeBehaviour == NetUnreliableSizeBehaviour.IgnoreMTU)
            {
                connection.Peer.LogError(NetLogMessage.FromValues(NetLogCode.MessageSizeExceeded,
                    endPoint: connection,
                    value: message.BitLength,
                    maxValue: ushort.MaxValue));

                return NetSendResult.Dropped;
            }

            QueuedSends.Enqueue(message);
            return NetSendResult.Sent;
        }

        public override NetSocketResult SendQueuedMessages(TimeSpan now)
        {
            int num = GetAllowedSends();
            while (num > 0 && QueuedSends.TryDequeue(out NetOutgoingMessage? om))
            {
                var sendResult = ExecuteSend(om);
                if (!sendResult.Success)
                {
                    QueuedSends.EnqueueFirst(om);
                    return sendResult;
                }
                num--;
            }

            NotifyIdleWaiters(now, num);

            return new NetSocketResult(true, false);
        }

        private NetSocketResult ExecuteSend(NetOutgoingMessage message)
        {
            NetConnection connection = Connection;
            connection.Peer.AssertIsOnLibraryThread();

            int seqNr = _sendStart;
            var sendResult = connection.QueueSendMessage(message, seqNr);
            if (sendResult.Success)
            {
                _sendStart = NetUtility.PowOf2Mod(seqNr + 1, NetConstants.SequenceNumbers);

                if (Interlocked.Decrement(ref message._recyclingCount) == 0)
                {
                    connection.Peer.Recycle(message);
                }
            }
            return sendResult;
        }

        // remoteWindowStart is remote expected sequence number; everything below this has arrived properly
        // seqNr is the actual nr received
        public override NetSocketResult ReceiveAcknowledge(TimeSpan now, int seqNr)
        {
            if (!_doFlowControl)
            {
                // we have no use for acks on this channel since we don't respect the window anyway
                Connection.Peer.LogWarning(new NetLogMessage(NetLogCode.SuppressedUnreliableAck, endPoint: Connection));
                return new NetSocketResult(true, false);
            }

            // late (dupe), on time or early ack?
            int relate = NetUtility.RelativeSequenceNumber(seqNr, _windowStart);

            if (relate < 0)
            {
                //m_connection.m_peer.LogDebug("Received late/dupe ack for #" + seqNr);
                return new NetSocketResult(true, false); // late/duplicate ack
            }

            if (relate == 0)
            {
                //m_connection.m_peer.LogDebug("Received right-on-time ack for #" + seqNr);

                // ack arrived right on time
                LidgrenException.Assert(seqNr == _windowStart);

                _receivedAcks[_windowStart] = false;
                _windowStart = NetUtility.PowOf2Mod(_windowStart + 1, NetConstants.SequenceNumbers);
                return new NetSocketResult(true, false);
            }

            // Advance window to this position
            _receivedAcks[seqNr] = true;

            while (_windowStart != seqNr)
            {
                _receivedAcks[_windowStart] = false;
                _windowStart = NetUtility.PowOf2Mod(_windowStart + 1, NetConstants.SequenceNumbers);
            }

            return new NetSocketResult(true, false);
        }
    }
}
