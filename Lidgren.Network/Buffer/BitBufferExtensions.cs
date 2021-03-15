using System.Diagnostics;
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
            buffer.EnsureBitCapacity(byteCount * 8);
        }

        /// <summary>
        /// Ensures the buffer can hold it's current bits and the given amount.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EnsureEnoughBitCapacity(this IBitBuffer buffer, int bitCount)
        {
            buffer.EnsureBitCapacity(buffer.BitLength + bitCount);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EnsureEnoughBitCapacity(this IBitBuffer buffer, int bitCount, int maxBitCount)
        {
            Debug.Assert(bitCount >= 1);
            Debug.Assert(bitCount <= maxBitCount);

            buffer.EnsureBitCapacity(buffer.BitLength + bitCount);
        }

        /// <summary>
        /// Gets whether <see cref="IBitBuffer.BitPosition"/> is byte-aligned, containing no stray bits.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsByteAligned(this IBitBuffer buffer)
        {
            return NetUtility.PowOf2Mod(buffer.BitPosition, 8) == 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool HasEnoughBits(this IBitBuffer buffer, int bitCount)
        {
            return buffer.BitLength - buffer.BitPosition >= bitCount;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void IncrementBitPosition(this IBitBuffer buffer, int bitCount)
        {
            buffer.BitPosition += bitCount;

            if (buffer.BitLength < buffer.BitPosition)
                buffer.BitLength = buffer.BitPosition;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetLengthByPosition(this IBitBuffer buffer)
        {
            if (buffer.BitLength < buffer.BitPosition)
                buffer.BitLength = buffer.BitPosition;
        }
    }
}
