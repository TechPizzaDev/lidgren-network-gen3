using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Lidgren.Network
{
    public partial class NetPeer
    {
        internal NetQueue<NetOutgoingMessage>? _outgoingMessagePool = new();
        internal NetQueue<NetIncomingMessage>? _incomingMessagePool = new();

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
        /// Recycles a message for reuse; taking pressure off the garbage collector.
        /// </summary>
        public void Recycle(NetIncomingMessage message)
        {
            Debug.Assert(message != null);

            if (_incomingMessagePool == null)
                return;

            LidgrenException.Assert(
                !_incomingMessagePool.Contains(message), "Recyling already recycled message! Thread race?");

            message.Reset();
            message.TrimExcess();
            _incomingMessagePool.Enqueue(message);
            Interlocked.Increment(ref Statistics._incomingRecycled);
        }

        /// <summary>
        /// Recycles a list of messages for reuse.
        /// </summary>
        public void Recycle(IEnumerable<NetIncomingMessage?> messages)
        {
            if (messages == null)
                throw new ArgumentNullException(nameof(messages));

            if (_incomingMessagePool == null)
                return;

            int count = 0;
            foreach (NetIncomingMessage? message in messages.AsListEnumerator())
            {
                if (message == null)
                    continue;

                LidgrenException.Assert(
                    !_incomingMessagePool.Contains(message), "Recyling already recycled message! Thread race?");

                message.Reset();
                message.TrimExcess();
                _incomingMessagePool.Enqueue(message);
                count++;
            }
            Interlocked.Add(ref Statistics._incomingRecycled, count);
        }

        internal void Recycle(NetOutgoingMessage message)
        {
            Debug.Assert(message != null);

            message.Reset();
            message.TrimExcess();

            if (_outgoingMessagePool == null)
                return;

            LidgrenException.Assert(
                !_outgoingMessagePool.Contains(message), "Recyling already recycled message! Thread race?");

            _outgoingMessagePool.Enqueue(message);

            Interlocked.Increment(ref Statistics._outgoingRecycled);
        }

        internal void TryRecycle(in NetMessageView view)
        {
            if (view.Buffer is NetIncomingMessage incoming)
            {
                Recycle(incoming);
            }
            else if (view.Buffer is NetOutgoingMessage outgoing)
            {
                Recycle(outgoing);
            }
        }
    }
}
