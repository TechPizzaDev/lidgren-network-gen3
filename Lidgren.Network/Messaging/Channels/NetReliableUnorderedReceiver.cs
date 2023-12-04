
namespace Lidgren.Network
{
    internal sealed class NetReliableUnorderedReceiver : NetReceiverChannel
    {
        private NetBitArray _earlyReceived;

        public NetReliableUnorderedReceiver(NetConnection connection, int windowSize)
            : base(connection, windowSize)
        {
        }

        protected override void OnWindowSizeChanged()
        {
            _earlyReceived = new NetBitArray(WindowSize);

            base.OnWindowSizeChanged();
        }

        private void AdvanceWindow()
        {
            _earlyReceived.Set(NetUtility.PowOf2Mod(_windowStart, WindowSize), false);
            _windowStart = NetUtility.PowOf2Mod(_windowStart + 1, NetConstants.SequenceNumbers);
        }

        public override void ReceiveMessage(in NetMessageView message)
        {
            int windowSize = WindowSize;
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

                while (_earlyReceived[NetUtility.PowOf2Mod(nextSeqNr, windowSize)])
                {
                    AdvanceWindow();
                    nextSeqNr++;
                }

                return;
            }

            if (relate < 0)
            {
                Peer.LogVerbose(NetLogMessage.FromValues(NetLogCode.DuplicateMessage,
                    message, value: _windowStart, maxValue: windowSize));
                return;
            }

            // relate > 0 = early message
            if (relate > windowSize)
            {
                Peer.LogVerbose(NetLogMessage.FromValues(NetLogCode.TooEarlyMessage,
                    message, value: _windowStart, maxValue: windowSize));
                return;
            }

            _earlyReceived.Set(NetUtility.PowOf2Mod(message.SequenceNumber, windowSize), true);

            Peer.ReleaseMessage(message);
        }
    }
}
