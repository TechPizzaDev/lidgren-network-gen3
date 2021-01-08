using System;
using System.Buffers;
using System.Text.Unicode;

namespace Lidgren.Network
{
    /// <summary>
    /// Mutable reader struct that reads a sequence of byte blocks.
    /// </summary>
    public struct NetBlockReader
    {
        public IBitBuffer Buffer { get; }

        /// <summary>
        /// Gets the amount of bytes left in the current byte block.
        /// </summary>
        public int BlockBytesLeft { get; private set; }

        public NetBlockReader(IBitBuffer buffer)
        {
            Buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            BlockBytesLeft = Buffer.ReadVarInt32();
        }

        /// <summary>
        /// Reads byte blocks into a span of bytes.
        /// </summary>
        public int Read(Span<byte> destination)
        {
            if (Buffer == null)
                throw new InvalidOperationException();

            if (destination.IsEmpty || BlockBytesLeft == 0)
                return 0;

            int bytesWritten = 0;

            TryRead:
            do
            {
                int toRead = Math.Min(BlockBytesLeft, destination.Length);
                Buffer.Peek(destination.Slice(0, toRead));

                bytesWritten += toRead;
                BlockBytesLeft -= toRead;

                destination = destination[toRead..];
                Buffer.IncrementBitPosition(toRead * 8);
            }
            while (BlockBytesLeft > 0 && destination.Length > 0);

            if (BlockBytesLeft == 0)
                BlockBytesLeft = Buffer.ReadVarInt32();

            if (destination.Length != 0 && BlockBytesLeft != 0)
                goto TryRead;

            return bytesWritten;
        }

        /// <summary>
        /// Reads byte blocks of UTF-8 characters into a span of characters.
        /// </summary>
        public OperationStatus Read(Span<char> destination, out int charsRead, bool replaceInvalidSequences = true)
        {
            if (Buffer == null)
                throw new InvalidOperationException();

            charsRead = 0;
            if (destination.IsEmpty || BlockBytesLeft == 0)
                return OperationStatus.Done;

            Span<byte> buffer = stackalloc byte[4096];

            TryRead:
            do
            {
                int toRead = Math.Min(BlockBytesLeft, buffer.Length);
                Span<byte> slice = buffer.Slice(0, toRead);
                Buffer.Peek(slice);

                var status = Utf8.ToUtf16(slice, destination, out int read, out int written, replaceInvalidSequences);
                if (status == OperationStatus.InvalidData)
                    return status;

                charsRead += written;
                BlockBytesLeft -= read;

                destination = destination[written..];
                Buffer.IncrementBitPosition(read * 8);
            }
            while (BlockBytesLeft > 0 && destination.Length > 0);

            if (BlockBytesLeft == 0)
                BlockBytesLeft = Buffer.ReadVarInt32();

            if (destination.Length != 0 && BlockBytesLeft != 0)
                goto TryRead;

            return OperationStatus.Done;
        }
    }

}
