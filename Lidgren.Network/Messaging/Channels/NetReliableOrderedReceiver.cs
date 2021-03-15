using System;

namespace Lidgren.Network
{
    internal sealed class NetReliableOrderedReceiver : NetReceiverChannel
    {
        private int _windowStart;
        private int _windowSize;
        private NetBitArray _earlyReceived;

        public NetIncomingMessage?[] WithheldMessages { get; private set; }

        public NetReliableOrderedReceiver(NetConnection connection, int windowSize)
            : base(connection)
        {
            LidgrenException.AssertIsPowerOfTwo((ulong)windowSize, nameof(windowSize));

            _windowSize = windowSize;
            _earlyReceived = new NetBitArray(windowSize);
            WithheldMessages = new NetIncomingMessage[windowSize];
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

                int nextSeqNr = NetUtility.PowOf2Mod(message.SequenceNumber + 1, NetConstants.SequenceNumbers);
                nextSeqNr = NetUtility.PowOf2Mod(nextSeqNr, _windowSize);

                AdvanceWindow();
                Peer.ReleaseMessage(message);

                // release withheld messages
                while (_earlyReceived[nextSeqNr])
                {
                    NetIncomingMessage? withheldMessage = WithheldMessages[nextSeqNr];
                    LidgrenException.Assert(withheldMessage != null);

                    // remove it from withheld messages
                    WithheldMessages[nextSeqNr] = default;

                    AdvanceWindow();
                    nextSeqNr++;
                    nextSeqNr = NetUtility.PowOf2Mod(nextSeqNr, _windowSize);

                    Peer.LogVerbose("Releasing withheld message #" + withheldMessage);
                    Peer.ReleaseMessage(withheldMessage);
                }
                return;
            }

            if (relate < 0)
            {
                Peer.LogVerbose("Received message #" + message.SequenceNumber + " DROPPING DUPLICATE");
                // duplicate
                return;
            }

            // relate > 0 = early message
            if (relate > _windowSize)
            {
                // too early message!
                Peer.LogDebug("Received " + message.ToString() + " TOO EARLY! Expected " + _windowStart);
                return;
            }

            int messageIndex = NetUtility.PowOf2Mod(message.SequenceNumber, _windowSize);

            _earlyReceived.Set(messageIndex, true);
            Peer.LogVerbose("Received " + message.ToString() + " WITHHOLDING, waiting for " + _windowStart);

            ref NetIncomingMessage? messageSlot = ref WithheldMessages[messageIndex];

            // the amount of messages may overflow within a given window so just dump existing message
            if (messageSlot != null)
            {
                Peer.Recycle(messageSlot);
            }

            messageSlot = message.ToIncomingMessage(Peer);
        }
    }
}
