
namespace Lidgren.Network
{
    internal sealed class NetReliableUnorderedReceiver : NetReceiverChannel
    {
        private int _windowStart;
        private int _windowSize;
        private NetBitArray _earlyReceived;

        public NetReliableUnorderedReceiver(NetConnection connection, int windowSize)
            : base(connection)
        {
            LidgrenException.AssertIsPowerOfTwo((ulong)windowSize, nameof(windowSize));

            _windowSize = windowSize;
            _earlyReceived = new NetBitArray(windowSize);
        }

        private void AdvanceWindow()
        {
            _earlyReceived.Set(NetUtility.PowOf2Mod(_windowStart, _windowSize), false);
            _windowStart = NetUtility.PowOf2Mod(_windowStart + 1, NetConstants.SequenceNumbers);
        }

        public override void ReceiveMessage(in NetMessageView message)
        {
            int relate = NetUtility.RelativeSequenceNumber(message.SequenceNumber, _windowStart);

            // ack no matter what
            Connection.QueueAck(message.BaseMessageType, message.SequenceNumber);

            if (relate == 0)
            {
                // Log("Received message #" + message.SequenceNumber + " right on time");

                // excellent, right on time

                AdvanceWindow();
                Peer.ReleaseMessage(message);

                // release withheld messages
                int nextSeqNr = NetUtility.PowOf2Mod(message.SequenceNumber + 1, NetConstants.SequenceNumbers);

                while (_earlyReceived[NetUtility.PowOf2Mod(nextSeqNr, _windowSize)])
                {
                    AdvanceWindow();
                    nextSeqNr++;
                }

                return;
            }

            if (relate < 0)
            {
                Peer.LogVerbose(NetLogMessage.FromValues(NetLogCode.DuplicateMessage,
                    message, value: _windowStart, maxValue: _windowSize));
                return;
            }

            // relate > 0 = early message
            if (relate > _windowSize)
            {
                Peer.LogVerbose(NetLogMessage.FromValues(NetLogCode.TooEarlyMessage,
                    message, value: _windowStart, maxValue: _windowSize));
                return;
            }

            _earlyReceived.Set(NetUtility.PowOf2Mod(message.SequenceNumber, _windowSize), true);

            Peer.ReleaseMessage(message);
        }
    }
}
