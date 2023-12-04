using System;

namespace Lidgren.Network
{
    public readonly ref struct NetMessageView
    {
        public NetIncomingMessageType MessageType { get; }
        public NetMessageType BaseMessageType { get; }
        public bool IsFragment { get; }
        public TimeSpan Time { get; }
        public int SequenceNumber { get; }
        public NetAddress Address { get; }
        public NetConnection? Connection { get; }
        public NetBuffer? Buffer { get; }
        public ReadOnlySpan<byte> Span { get; }
        public int BitLength { get; }

        public NetMessageView(
            NetIncomingMessageType messageType,
            NetMessageType baseMessageType,
            bool isFragment,
            TimeSpan time,
            int sequenceNumber,
            NetAddress address,
            NetConnection? connection,
            NetBuffer? message,
            ReadOnlySpan<byte> span,
            int bitLength)
        {
            MessageType = messageType;
            BaseMessageType = baseMessageType;
            IsFragment = isFragment;
            Time = time;
            SequenceNumber = sequenceNumber;
            Address = address;
            Connection = connection;
            Buffer = message;
            Span = span;
            BitLength = bitLength;
        }

        public NetIncomingMessage ToIncomingMessage(NetPeer peer)
        {
            if (Buffer is NetIncomingMessage incoming)
            {
                return incoming;
            }

            NetIncomingMessage msg = peer.CreateIncomingMessage(MessageType, Address);
            msg._baseMessageType = BaseMessageType;
            msg.IsFragment = IsFragment;
            msg.ReceiveTime = Time;
            msg.SequenceNumber = SequenceNumber;
            msg.SenderConnection = Connection;
            
            msg.Write(Span);
            msg.BitLength = BitLength;
            msg.BitPosition = 0;
            return msg;
        }

        public override string ToString()
        {
            return BaseMessageType + ": #" + SequenceNumber + ", " + BitLength + " bits";
        }
    }
}
