using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Lidgren.Network
{
    public enum NetStreamMessageType : byte
    {
        Open,
        Data,
        Pause,
        Resume,
        Close,

        // TODO: Pause/Resume acknowledge
    }

    public class NetStream : Stream
    {
        //public delegate void StreamDataRequest(NetStream stream, int amount);
        //public delegate long StreamSeekRequest(NetStream stream, long offset, SeekOrigin origin);

        private Queue<NetIncomingMessage> _readQueue;
        //private ReaderWriterLockSlim _queueLock = new ReaderWriterLockSlim();
        private int _receivedByteCount;
        private NetIncomingMessage? _readBuffer;
        private AutoResetEvent _readEvent = new AutoResetEvent(false);
        private bool _closed;

        private byte[] _writeBuffer;
        private int _writeBufferOffset;

        public NetMessageScheduler Scheduler { get; }
        public NetConnection Connection { get; }
        public int Channel { get; }

        public bool IsDisposed { get; private set; }

        public NetPeer Peer => Connection.Peer;

        //public StreamDataRequest? ReadRequest { get; }
        //public StreamWriteRequest? WriteCallback { get; }
        //public StreamSeekRequest? SeekCallback { get; }
        //private Action? CloseCallback { get; }
        //
        //public override bool CanRead => ReadRequest != null;
        //public override bool CanWrite => WriteCallback != null;
        //public override bool CanSeek => SeekCallback != null;

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => true;

        public override long Length => throw new InvalidOperationException();

        public override long Position
        {
            get => throw new NotImplementedException();
            set => throw new InvalidOperationException();
        }

        public NetStream(
            NetMessageScheduler scheduler,
            NetConnection connection,
            int sequenceChannel
            )//,
             //StreamDataRequest? readRequest,
             //StreamWriteRequest? writeRequest,
             //StreamSeekRequest? seekCallback,
             //Action? closeCallback))
        {
            NetConstants.AssertValidDeliveryChannel(
                NetDeliveryMethod.Stream, sequenceChannel,
                null, nameof(sequenceChannel));

            //if (readRequest == null && writeRequest == null)
            //    throw new ArgumentException("Both read and write callbacks are null.");

            Scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            Connection = connection ?? throw new ArgumentNullException(nameof(connection));
            Channel = sequenceChannel;

            _readQueue = new Queue<NetIncomingMessage>();

            _writeBuffer = new byte[GetBufferSize(Connection.CurrentMTU)];
            _writeBufferOffset = 0;

            //var createMessage = Peer.CreateMessage();
            //createMessage.Write(CanRead);
            //createMessage.Write(CanWrite);
            //createMessage.Write(CanSeek);
            //createMessage.Write(CanTimeout); // TODO:
            //SendStreamMessage(createMessage);

            NetOutgoingMessage openMessage = Peer.CreateMessage();
            openMessage.Write((byte)NetStreamMessageType.Open);
            NetSendResult result = SendStreamMessage(openMessage);
            // TODO: check result; await accept response from remote
            // 
        }

        //public NetStream(
        //    NetConnection connection,
        //    int sequenceChannel,
        //    StreamReadRequest? readCallback,
        //    StreamWriteRequest? writeCallback,
        //    StreamSeekRequest? seekCallback,
        //    Action? closeCallback) 
        //    : this(
        //        connection?.Peer.DefaultScheduler!, connection!, sequenceChannel,
        //        readCallback, writeCallback, seekCallback, closeCallback)
        //{
        //}

        public static int GetBufferSize(int mtu)
        {
            return Math.Max(1, mtu - 1 - NetConstants.HeaderSize);
        }

        internal void OnDataMessage(NetIncomingMessage buffer)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            lock (_readQueue)
                _readQueue.Enqueue(buffer);

            _readEvent.Set();
        }

        internal void OnCloseMessage(NetIncomingMessage buffer)
        {
            _closed = true;
            _readEvent.Set();

            Peer.Recycle(buffer);
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty)
                return 0;

            int read = TryRead(buffer);
            if (read != 0)
                return read;

            TryRead:
            if (_closed)
                return TryRead(buffer);

            _readEvent.WaitOne();
            read = TryRead(buffer);
            if (read == 0)
                goto TryRead;
            return read;
        }

        private int TryRead(Span<byte> buffer)
        {
            int totalRead = 0;

            TryRead:
            if (buffer.IsEmpty)
                return totalRead;

            if (_readBuffer != null)
            {
                int read = _readBuffer.StreamRead(buffer);
                buffer = buffer[read..];
                totalRead += read;

                if (_readBuffer.BitPosition == _readBuffer.BitLength)
                {
                    Peer.Recycle(_readBuffer);
                    _readBuffer = null;
                }
            }

            if (_readBuffer == null)
            {
                lock (_readQueue)
                {
                    if (_readQueue.TryDequeue(out _readBuffer))
                        goto TryRead;
                }
            }
            return totalRead;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }

        public override int ReadByte()
        {
            Span<byte> tmp = stackalloc byte[1];
            if (Read(tmp) != tmp.Length)
                return -1;
            return tmp[0];
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (buffer.Length > 0)
            {
                Span<byte> space = _writeBuffer.AsSpan(_writeBufferOffset);
                if (space.IsEmpty)
                {
                    FlushCore();
                    continue;
                }

                ReadOnlySpan<byte> toCopy = buffer.Slice(0, Math.Min(buffer.Length, space.Length));
                toCopy.CopyTo(space);

                buffer = buffer[toCopy.Length..];
                _writeBufferOffset += toCopy.Length;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Write(buffer.AsSpan(offset, count));
        }

        public override void WriteByte(byte value)
        {
            Write(stackalloc byte[] { value });
        }

        public override void Flush()
        {
            if (_writeBufferOffset == 0)
                return;

            FlushCore();
        }

        private void FlushCore()
        {
            int length = _writeBufferOffset;

            NetOutgoingMessage message = Peer.CreateMessage(length + 6);
            message.Write((byte)NetStreamMessageType.Data);
            message.Write(_writeBuffer.AsSpan(0, length));

            NetSendResult result = SendStreamMessage(message);
            // TODO: check result

            _writeBufferOffset = 0;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        private NetSendResult SendClose()
        {
            NetOutgoingMessage message = Peer.CreateMessage();
            message.Write((byte)NetStreamMessageType.Close);
            return SendStreamMessage(message);
        }

        private NetSendResult SendStreamMessage(NetOutgoingMessage message)
        {
            if (!Connection.CanSendImmediately(NetDeliveryMethod.Stream, Channel))
            {
                Thread.Sleep(100);
            }
            return Connection.SendMessage(message, NetDeliveryMethod.Stream, Channel);
        }

        protected override void Dispose(bool disposing)
        {
            if (!IsDisposed)
            {
                if (disposing)
                {
                    Flush();

                    NetSendResult result = SendClose();
                    // TODO: check result
                }
                IsDisposed = true;
            }
            base.Dispose(disposing);
        }
    }
}
