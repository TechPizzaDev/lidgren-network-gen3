﻿namespace Lidgren.Network
{
    internal sealed class NetReliableOrderedReceiver : NetReceiverChannel
    {
        private NetBitArray _earlyReceived;

        public NetIncomingMessage?[] WithheldMessages { get; private set; }

        public NetReliableOrderedReceiver(NetConnection connection, int windowSize)
            : base(connection, windowSize)
        {
            WithheldMessages = new NetIncomingMessage[windowSize];
        }

        private void AdvanceWindow()
        {
            _earlyReceived.Set(NetUtility.PowOf2Mod(_windowStart, WindowSize), false);
            _windowStart = NetUtility.PowOf2Mod(_windowStart + 1, NetConstants.SequenceNumbers);
        }

        protected override void OnWindowSizeChanged()
        {
            _earlyReceived = new NetBitArray(WindowSize);
            WithheldMessages = new NetIncomingMessage[WindowSize];
            base.OnWindowSizeChanged();
        }

        public override void ReceiveMessage(in NetMessageView message)
        {
            NetConnection connection = Connection;
            NetPeer peer = connection.Peer;
            int windowSize = WindowSize;
            int relate = NetUtility.RelativeSequenceNumber(message.SequenceNumber, _windowStart);

            // ack no matter what
            connection.QueueAck(message.BaseMessageType, message.SequenceNumber);

            if (relate == 0)
            {
                // Log("Received message #" + message.SequenceNumber + " right on time");

                // excellent, right on time

                int nextSeqNr = NetUtility.PowOf2Mod(message.SequenceNumber + 1, NetConstants.SequenceNumbers);
                nextSeqNr = NetUtility.PowOf2Mod(nextSeqNr, windowSize);

                AdvanceWindow();
                peer.ReleaseMessage(message);

                // release withheld messages
                while (_earlyReceived[nextSeqNr])
                {
                    NetIncomingMessage? withheldMessage = WithheldMessages[nextSeqNr];
                    LidgrenException.Assert(withheldMessage != null);

                    // remove it from withheld messages
                    WithheldMessages[nextSeqNr] = default;

                    AdvanceWindow();
                    nextSeqNr++;
                    nextSeqNr = NetUtility.PowOf2Mod(nextSeqNr, windowSize);

                    peer.LogVerbose(NetLogMessage.FromValues(NetLogCode.WithheldMessage,
                        withheldMessage.View, value: _windowStart, maxValue: windowSize));
                    peer.ReleaseMessage(withheldMessage);
                }
                return;
            }

            if (relate < 0)
            {
                peer.LogVerbose(NetLogMessage.FromValues(NetLogCode.DuplicateMessage,
                    message, value: _windowStart, maxValue: windowSize));
                return;
            }

            // relate > 0 = early message
            if (relate > windowSize)
            {
                peer.LogVerbose(NetLogMessage.FromValues(NetLogCode.TooEarlyMessage,
                    message, value: _windowStart, maxValue: windowSize));
                return;
            }

            int messageIndex = NetUtility.PowOf2Mod(message.SequenceNumber, windowSize);
            _earlyReceived.Set(messageIndex, true);

            ref NetIncomingMessage? messageSlot = ref WithheldMessages[messageIndex];
            if (messageSlot != null)
            {
                // the amount of messages may overflow within a given window so just dump existing message
                peer.Recycle(messageSlot);
            }
            messageSlot = message.ToIncomingMessage(peer);

            peer.LogVerbose(NetLogMessage.FromValues(NetLogCode.EarlyMessage,
                message, value: _windowStart, maxValue: windowSize));
        }
    }
}
