using System;
using System.Collections.Generic;
using System.Threading;

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

                Interlocked.Increment(ref Statistics._outgoingAllocated);
            }

            return message;
        }

        public NetOutgoingMessage CreateMessage(int minimumByteCapacity)
        {
            var message = CreateMessage();
            message.EnsureByteCapacity(minimumByteCapacity);
            return message;
        }

        public NetOutgoingMessage CreateMessage(string? content)
        {
            var message = CreateMessage();
            message.Write(content);
            return message;
        }

        internal NetIncomingMessage CreateIncomingMessage(NetIncomingMessageType type)
        {
            if (_incomingMessagePool == null ||
                !_incomingMessagePool.TryDequeue(out NetIncomingMessage? message))
            {
                message = new NetIncomingMessage(StoragePool);

                Interlocked.Increment(ref Statistics._incomingAllocated);
            }

            message.MessageType = type;
            return message;
        }

        internal NetIncomingMessage CreateIncomingMessage(NetIncomingMessageType type, string? content)
        {
            var message = CreateIncomingMessage(type);
            message.Write(content);
            return message;
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

            Interlocked.Increment(ref Statistics._incomingRecycled);
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

            // first recycle the storage of each message and then recycle message
            foreach (var message in messages.AsListEnumerator())
            {
                message.Reset();
                message.TrimExcess();
                _incomingMessagePool.Enqueue(message);

                Interlocked.Increment(ref Statistics._incomingRecycled);
            }
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

            Interlocked.Increment(ref Statistics._outgoingRecycled);
        }
    }
}
