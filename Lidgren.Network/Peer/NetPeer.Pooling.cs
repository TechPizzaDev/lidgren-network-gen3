using System;
using System.Collections.Generic;

namespace Lidgren.Network
{
    public partial class NetPeer
    {
        internal NetQueue<NetOutgoingMessage>? _outgoingMessagePool = new NetQueue<NetOutgoingMessage>();
        internal NetQueue<NetIncomingMessage>? _incomingMessagePool = new NetQueue<NetIncomingMessage>();

        /// <summary>
        /// Creates a new message for sending.
        /// </summary>
        public NetOutgoingMessage CreateMessage()
        {
            if (_outgoingMessagePool == null ||
                !_outgoingMessagePool.TryDequeue(out NetOutgoingMessage? message))
            {
                message = new NetOutgoingMessage(StoragePool);
            }

            return message;
        }

        public NetOutgoingMessage CreateMessage(int minimumByteCapacity)
        {
            var msg = CreateMessage();
            msg.EnsureByteCapacity(minimumByteCapacity);
            return msg;
        }

        public NetOutgoingMessage CreateMessage(string? content)
        {
            var msg = CreateMessage();
            msg.Write(content);
            return msg;
        }

        internal NetIncomingMessage CreateIncomingMessage(NetIncomingMessageType type)
        {
            if (_incomingMessagePool == null ||
                !_incomingMessagePool.TryDequeue(out NetIncomingMessage? message))
            {
                message = new NetIncomingMessage(StoragePool);
            }

            message.MessageType = type;
            return message;
        }

        internal NetIncomingMessage CreateIncomingMessage(NetIncomingMessageType type, string? content)
        {
            var msg = CreateIncomingMessage(type);
            msg.Write(content);
            return msg;

            //if (string.IsNullOrEmpty(content))
            //{
            //    msg = CreateIncomingMessage(type, 1);
            //    msg.Write(string.Empty);
            //    msg.BitPosition = 0;
            //    return msg;
            //}
            //
            //int strByteCount = BitBuffer.StringEncoding.GetMaxByteCount(content.Length);
            //msg = CreateIncomingMessage(type, strByteCount + 10);
            //msg.Write(content);
            //msg.BitPosition = 0;
            //
            //return msg;
        }

        /// <summary>
        /// Recycles a message for reuse; taking pressure off the garbage collector
        /// </summary>
        public void Recycle(NetIncomingMessage message)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            if (_incomingMessagePool == null)
                return;

            LidgrenException.Assert(
                !_incomingMessagePool.Contains(message), "Recyling already recycled message! Thread race?");

            //byte[] storage = message.GetBuffer();
            //message.SetBuffer(Array.Empty<byte>(), false);
            //StoragePool.Return(storage);

            message.Reset();
            message.TrimExcess();
            _incomingMessagePool.Enqueue(message);
        }

        /// <summary>
        /// Recycles a list of messages for reuse.
        /// </summary>
        public void Recycle(IEnumerable<NetIncomingMessage> messages)
        {
            if (messages == null)
                throw new ArgumentNullException(nameof(messages));

            if (_incomingMessagePool == null)
                return;

            // first recycle the storage of each message
            foreach (var message in messages)
            {
                message.Reset();
                message.TrimExcess();
            }

            // then recycle the message objects
            _incomingMessagePool.Enqueue(messages);
        }

        internal void Recycle(NetOutgoingMessage message)
        {
            message.Reset();
            message.TrimExcess();

            if (_outgoingMessagePool == null)
                return;

            LidgrenException.Assert(
                !_outgoingMessagePool.Contains(message), "Recyling already recycled message! Thread race?");

            _outgoingMessagePool.Enqueue(message);
        }
    }
}
