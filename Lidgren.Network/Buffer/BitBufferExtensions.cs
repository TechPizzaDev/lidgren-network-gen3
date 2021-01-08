using System;
using System.Runtime.CompilerServices;

namespace Lidgren.Network
{
    public static class BitBufferExtensions
    {
        /// <summary>
        /// Ensures that the buffer can hold this number of bytes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EnsureByteCapacity(this IBitBuffer buffer, int byteCount)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            buffer.EnsureBitCapacity(byteCount * 8);
        }

        /// <summary>
        /// Ensures the buffer can hold it's current bits and the given amount.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EnsureEnoughBitCapacity(this IBitBuffer buffer, int bitCount)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            buffer.EnsureBitCapacity(buffer.BitLength + bitCount);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EnsureEnoughBitCapacity(this IBitBuffer buffer, int bitCount, int maxBitCount)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (bitCount < 1)
                throw new ArgumentOutOfRangeException(nameof(bitCount));
            if (bitCount > maxBitCount)
                throw new ArgumentOutOfRangeException(nameof(bitCount));

            buffer.EnsureBitCapacity(buffer.BitLength + bitCount);
        }

        /// <summary>
        /// Gets whether <see cref="IBitBuffer.BitPosition"/> is byte-aligned, containing no stray bits.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsByteAligned(this IBitBuffer buffer)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            return buffer.BitPosition % 8 == 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool HasEnoughBits(this IBitBuffer buffer, int bitCount)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            return buffer.BitLength - buffer.BitPosition >= bitCount;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void IncrementBitPosition(this IBitBuffer buffer, int bitCount)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            buffer.BitPosition += bitCount;

            if (buffer.BitLength < buffer.BitPosition)
                buffer.BitLength = buffer.BitPosition;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetLengthByPosition(this IBitBuffer buffer)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            if (buffer.BitLength < buffer.BitPosition)
                buffer.BitLength = buffer.BitPosition;
        }
    }
}
