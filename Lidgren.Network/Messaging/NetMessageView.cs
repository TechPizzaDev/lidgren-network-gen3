using System;
using System.Net;

namespace Lidgren.Network
{
    public readonly ref struct NetMessageView
    {
        public NetIncomingMessageType MessageType { get; }
        public NetMessageType BaseMessageType { get; }
        public bool IsFragment { get; }
        public TimeSpan Time { get; }
        public int SequenceNumber { get; }
        public IPEndPoint? EndPoint { get; }
        public NetConnection? Connection { get; }
        public NetBuffer? Message { get; }
        public ReadOnlySpan<byte> Span { get; }
        public int BitLength { get; }

        public NetMessageView(
            NetIncomingMessageType messageType,
            NetMessageType baseMessageType,
            bool isFragment,
            TimeSpan time,
            int sequenceNumber,
            IPEndPoint? endPoint,
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
            EndPoint = endPoint;
            Connection = connection;
            Message = message;
            Span = span;
            BitLength = bitLength;
        }

        public NetIncomingMessage ToIncomingMessage(NetPeer peer)
        {
            if (Message is NetIncomingMessage incoming)
                return incoming;

            NetIncomingMessage msg = peer.CreateIncomingMessage(MessageType);
            msg._baseMessageType = BaseMessageType;
            msg.IsFragment = IsFragment;
            msg.ReceiveTime = Time;
            msg.SequenceNumber = SequenceNumber;
            msg.SenderConnection = Connection;
            msg.SenderEndPoint = EndPoint;

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
