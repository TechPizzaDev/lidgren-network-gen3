using System;
using System.Threading;
using System.Threading.Tasks;

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
        private NetBitArray _receivedAcks;

        public override int WindowSize => _windowSize;

        public NetUnreliableSenderChannel(NetConnection connection, int windowSize)
        {
            _connection = connection;
            _windowSize = windowSize;
            _windowStart = 0;
            _sendStart = 0;
            _receivedAcks = new NetBitArray(NetConstants.SequenceNumbers);
        }

        public override int GetAllowedSends()
        {
            int value = _windowSize - (_sendStart + NetConstants.SequenceNumbers - _windowStart) % _windowSize;
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
                _connection.Peer.LogError(string.Format(
                    "Unreliable message size exceeded {0} bits ({1})",
                    ushort.MaxValue, message.BitLength));
                return NetSendResult.Dropped;
            }

            QueuedSends.Enqueue(message);
            return NetSendResult.Sent;
        }

        // call this regularely
        public override void SendQueuedMessages(TimeSpan now)
        {
            int num = GetAllowedSends();
            while (num > 0)
            {
                if (!QueuedSends.TryDequeue(out NetOutgoingMessage? om))
                    break;

                ExecuteSend(om);
                num--;
            }
        }

        private void ExecuteSend(NetOutgoingMessage message)
        {
            _connection.Peer.AssertIsOnLibraryThread();

            int seqNr = _sendStart;
            _sendStart = (_sendStart + 1) % NetConstants.SequenceNumbers;

            _connection.QueueSendMessage(message, seqNr);

            Interlocked.Decrement(ref message._recyclingCount);
            if (message._recyclingCount <= 0)
                _connection.Peer.Recycle(message);
        }

        // remoteWindowStart is remote expected sequence number; everything below this has arrived properly
        // seqNr is the actual nr received
        public override void ReceiveAcknowledge(TimeSpan now, int seqNr)
        {
            // late (dupe), on time or early ack?
            int relate = NetUtility.RelativeSequenceNumber(seqNr, _windowStart);

            if (relate < 0)
            {
                //m_connection.m_peer.LogDebug("Received late/dupe ack for #" + seqNr);
                return; // late/duplicate ack
            }

            if (relate == 0)
            {
                //m_connection.m_peer.LogDebug("Received right-on-time ack for #" + seqNr);

                // ack arrived right on time
                LidgrenException.Assert(seqNr == _windowStart);

                _receivedAcks[_windowStart] = false;
                _windowStart = (_windowStart + 1) % NetConstants.SequenceNumbers;
                return;
            }

            // Advance window to this position
            _receivedAcks[seqNr] = true;

            while (_windowStart != seqNr)
            {
                _receivedAcks[_windowStart] = false;
                _windowStart = (_windowStart + 1) % NetConstants.SequenceNumbers;
            }
        }
    }
}
