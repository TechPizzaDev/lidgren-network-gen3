using System;
using System.Buffers;
using System.IO;
using System.Text.Unicode;

namespace Lidgren.Network
{
    /// <summary>
    /// Immutable writer struct that writes a sequence of byte blocks.
    /// </summary>
    public readonly struct NetBlockWriter : IDisposable
    {
        public IBitBuffer Buffer { get; }
        public bool IsFinalBlock { get; }

        public NetBlockWriter(IBitBuffer buffer, bool isFinalBlock = true)
        {
            Buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            IsFinalBlock = isFinalBlock;
        }

        /// <summary>
        /// Writes byte blocks from a span of bytes.
        /// </summary>
        public void Write(ReadOnlySpan<byte> source)
        {
            if (Buffer == null)
                throw new InvalidOperationException();

            if (source.IsEmpty)
                return;

            Buffer.WriteVar(source.Length);
            Buffer.Write(source);
        }

        /// <summary>
        /// Writes byte blocks of UTF-8 characters from a span of characters.
        /// </summary>
        /// <exception cref="InvalidDataException"></exception>
        public void Write(ReadOnlySpan<char> source)
        {
            var status = Write(source, out _, replaceInvalidSequences: true);
            if (status == OperationStatus.InvalidData)
                throw new InvalidDataException();
        }

        /// <summary>
        /// Writes byte blocks of UTF-8 characters from a span of characters.
        /// </summary>
        public OperationStatus Write(ReadOnlySpan<char> source, out int charsRead, bool replaceInvalidSequences = true)
        {
            if (Buffer == null)
                throw new InvalidOperationException();

            charsRead = 0;
            if (source.IsEmpty)
                return OperationStatus.Done;

            Span<byte> utf8Buffer = stackalloc byte[4096];
            do
            {
                var status = Utf8.FromUtf16(source, utf8Buffer, out int read, out int written, replaceInvalidSequences);
                if (status == OperationStatus.InvalidData)
                    return status;

                Buffer.WriteVar(written);
                Buffer.Write(utf8Buffer.Slice(0, written));

                charsRead += read;
                source = source[read..];
            }
            while (source.Length > 0);

            return OperationStatus.Done;
        }

        public void Dispose()
        {
            if (IsFinalBlock)
                Buffer.WriteVar(0);
        }
    }

}
