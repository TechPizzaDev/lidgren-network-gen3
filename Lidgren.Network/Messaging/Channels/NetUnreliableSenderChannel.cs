using System;
using System.Threading;

namespace Lidgren.Network
{
    /// <summary>
    /// Sender part of Selective repeat ARQ for a particular NetChannel
    /// </summary>
    internal sealed class NetUnreliableSenderChannel : NetSenderChannel
    {
        private NetConnection _connection;
        private int _windowStart;
        private int _windowSize;
        private int _sendStart;
        private bool _doFlowControl;
        private NetBitArray _receivedAcks;

        public override int WindowSize => _windowSize;

        public NetUnreliableSenderChannel(NetConnection connection, int windowSize, NetDeliveryMethod method)
        {
            LidgrenException.AssertIsPowerOfTwo((ulong)windowSize, nameof(windowSize));

            _connection = connection;
            _windowSize = windowSize;
            _windowStart = 0;
            _sendStart = 0;
            _receivedAcks = new NetBitArray(NetConstants.SequenceNumbers);

            _doFlowControl = true;
            if (method == NetDeliveryMethod.Unreliable &&
                connection.Peer.Configuration.SuppressUnreliableUnorderedAcks)
                _doFlowControl = false;
        }

        public override int GetAllowedSends()
        {
            if (!_doFlowControl)
                return int.MaxValue;

            int value = _windowSize - NetUtility.PowOf2Mod(
                _sendStart + NetConstants.SequenceNumbers - _windowStart,
                _windowSize);

            LidgrenException.Assert(value >= 0 && value <= _windowSize);
            return value;
        }

        public override void Reset()
        {
            QueuedSends.Clear();
            _receivedAcks.Clear();
            _windowStart = 0;
            _sendStart = 0;
        }

        public override NetSendResult Enqueue(NetOutgoingMessage message)
        {
            int queueLen = QueuedSends.Count + 1;
            int left = GetAllowedSends();

            if (queueLen > left ||
                (message.ByteLength > _connection.CurrentMTU &&
                _connection._peerConfiguration.UnreliableSizeBehaviour == NetUnreliableSizeBehaviour.DropAboveMTU))
            {
                _connection.Peer.Recycle(message);
                return NetSendResult.Dropped;
            }

            if (message.BitLength >= ushort.MaxValue &&
                _connection._peerConfiguration.UnreliableSizeBehaviour == NetUnreliableSizeBehaviour.IgnoreMTU)
            {
                _connection.Peer.LogError(NetLogMessage.FromValues(NetLogCode.MessageSizeExceeded, 
                    endPoint: _connection, 
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
            return new NetSocketResult(true, false);
        }

        private NetSocketResult ExecuteSend(NetOutgoingMessage message)
        {
            _connection.Peer.AssertIsOnLibraryThread();

            int seqNr = _sendStart;
            var sendResult = _connection.QueueSendMessage(message, seqNr);
            if (sendResult.Success)
            {
                _sendStart = NetUtility.PowOf2Mod(seqNr + 1, NetConstants.SequenceNumbers);

                Interlocked.Decrement(ref message._recyclingCount);
                if (message._recyclingCount <= 0)
                    _connection.Peer.Recycle(message);
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
                _connection.Peer.LogWarning(new NetLogMessage(NetLogCode.SuppressedUnreliableAck, endPoint: _connection));
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
