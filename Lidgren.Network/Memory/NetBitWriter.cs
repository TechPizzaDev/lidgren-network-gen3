using System;
using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Lidgren.Network
{
    // TODO: optimize with Intrinsics (remember to steal/look at old impl)

    /// <summary>
    /// Helper class for <see cref="NetBuffer"/> to write/read bits.
    /// </summary>
    public static class NetBitWriter
    {
        public const int MaxVarInt64Size = 10;
        public const int MaxVarInt32Size = 5;

        #region CopyBits

        /// <summary>
        /// Copies bits between buffers.
        /// </summary>
        /// <param name="sourceBitOffset">The offset in bits into <paramref name="source"/>.</param>
        /// <param name="bitCount">The amount of bits to copy from <paramref name="source"/>.</param>
        /// <param name="destinationBitOffset">The offset in bits into <paramref name="destination"/>.</param>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void CopyBits(
            ReadOnlySpan<byte> source, int sourceBitOffset, int bitCount,
            Span<byte> destination, int destinationBitOffset)
        {
            if (bitCount == 0)
                return;

            var src = source[NetUtility.DivBy8(sourceBitOffset )..];
            var dst = destination[NetUtility.DivBy8(destinationBitOffset)..];

            sourceBitOffset = NetUtility.PowOf2Mod(sourceBitOffset, 8);
            destinationBitOffset = NetUtility.PowOf2Mod(destinationBitOffset, 8);

            int srcNextRemBits = 8 - sourceBitOffset;
            int dstNextRemBits = 8 - destinationBitOffset;

            #region Bulk-copy most bits

            int i;
            if (sourceBitOffset > 0)
            {
                var byteSrc = src.Slice(0, BytesForBits(bitCount));
                if (destinationBitOffset > 0)
                {
                    for (i = 0; i < byteSrc.Length - 1; i++)
                    {
                        int value = ReadBitsAtSrc(src, i, sourceBitOffset, srcNextRemBits);
                        WriteBitsAtDst(dst, i, value, destinationBitOffset, dstNextRemBits);
                    }
                }
                else
                {
                    for (i = 0; i < byteSrc.Length - 1; i++)
                    {
                        int value = ReadBitsAtSrc(src, i, sourceBitOffset, srcNextRemBits);
                        dst[i] = (byte)value;
                    }
                }
            }
            else if (destinationBitOffset > 0)
            {
                var byteSrc = src.Slice(0, BytesForBits(bitCount));
                for (i = 0; i < byteSrc.Length - 1; i++)
                {
                    int value = byteSrc[i];
                    WriteBitsAtDst(dst, i, value, destinationBitOffset, dstNextRemBits);
                }
            }
            else
            {
                var byteSrc = src.Slice(0, NetUtility.DivBy8(bitCount));
                byteSrc.CopyTo(dst);
                i = byteSrc.Length;
            }
            bitCount -= i * 8;

            #endregion

            #region Copy remaining bits

            if (bitCount == 0)
                return;

            // mask away unused bits lower than relevant bits in last byte
            int lastValue = src[i] >> sourceBitOffset;

            int bitsInNextByte = bitCount - srcNextRemBits;
            if (bitsInNextByte < 1)
            {
                // we don't need to read from the next byte, 
                // we need to mask away unused bits higher than relevant bits
                lastValue &= 255 >> (8 - bitCount);
            }
            else
            {
                int nextValue = src[i + 1];

                // mask away unused bits higher than relevant bits in next byte
                nextValue &= 255 >> (8 - bitsInNextByte);

                lastValue |= nextValue << srcNextRemBits;
            }

            int bitsLeftInDst = dstNextRemBits - bitCount;
            if (bitsLeftInDst >= 0)
            {
                // everything fits in the last byte

                int mask = (255 >> dstNextRemBits) | (255 << (8 - bitsLeftInDst));

                dst[i] = (byte)(
                    (dst[i] & mask) | // Mask out lower and upper bits
                    (lastValue << destinationBitOffset)); // Insert new bits
            }
            else
            {
                dst[i] = (byte)(
                    (dst[i] & (255 >> dstNextRemBits)) | // Mask out upper bits
                    (lastValue << destinationBitOffset)); // Write the lower bits to the upper bits of last byte

                dst[i + 1] = (byte)(
                    (dst[i + 1] & (255 << (bitCount - dstNextRemBits))) | // Mask out lower bits
                    (lastValue >> dstNextRemBits)); // Write the upper bits to the lower bits of next byte
            }

            #endregion
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private static void WriteBitsAtDst(
            Span<byte> dst, int i, int value, int destinationBitOffset, int dstNextRemBits)
        {
            int lastValue = value & (255 >> destinationBitOffset);
            dst[i] |= (byte)(lastValue << destinationBitOffset);

            int nextValue = value & (255 << dstNextRemBits);
            dst[i + 1] |= (byte)(nextValue >> dstNextRemBits);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private static int ReadBitsAtSrc(
            ReadOnlySpan<byte> byteSrc, int i, int sourceBitOffset, int srcNextRemBits)
        {
            int last = byteSrc[i] >> sourceBitOffset;
            int next = (byteSrc[i + 1] << srcNextRemBits) & 255;
            return last | next;
        }

        // old impl is left here as SSE reference code
        /*

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static unsafe void CopyBits_old(
            ReadOnlySpan<byte> source, int sourceBitOffset, int bitCount,
            Span<byte> destination, int destinationBitOffset)
        {
            int srcNextPart = sourceBitOffset % 8;
            int dstNextPart = destinationBitOffset % 8;

            var src = source.Slice(sourceBitOffset / 8);
            var dst = destination.Slice(destinationBitOffset / 8);

            int i = 0;
            int bitsLeft = bitCount;
            int byteCount = bitsLeft / 8;
            if (byteCount > 0)
            {
                if (srcNextPart == 0 &&
                    dstNextPart == 0)
                {
                    src.Slice(i, byteCount).CopyTo(dst);
                    i += byteCount;
                }
                else
                {
                    if (bitsLeft > dst.Length * 8 - dstNextPart)
                        throw new ArgumentException(
                            "The given offsets and count reach outside the destination buffer.");

                    if (bitsLeft > src.Length * 8 - srcNextPart)
                        throw new ArgumentException(
                            "The given offsets and count reach outside the source buffer.");

                    int srcLastPart = 8 - srcNextPart;
                    int dstLastPart = 8 - dstNextPart;
                    byte srcNextClearMask = (byte)(255 >> srcLastPart);
                    byte dstLastClearMask = (byte)(255 >> dstLastPart);
                    byte dstNextClearMask = (byte)(255 << dstNextPart);

                    //fixed (byte* srcPtr = src)
                    //fixed (byte* dstPtr = dst)
                    {
                        // TODO: fix SSE

                        #region SSE2
                        

                        if (Sse2.IsSupported)
                        {
                            byte bSrcNextPart = (byte)srcNextPart;
                            byte bDstLastPart = (byte)dstLastPart;
                            byte bDstNextPart = (byte)dstNextPart;

                            var vLastClearMask = Vector128.Create(lastClearMask);
                            var vNextClearMask = Vector128.Create(nextClearMask);
                            var castByteMask = Vector128.Create((short)byte.MaxValue);

                            for (; i <= byteCount - Vector128<byte>.Count; i += Vector128<byte>.Count)
                            {
                                var values = Sse2.LoadVector128(srcPtr + i);

                                // we have to widen to int16 as shifts don't operate on int8
                                var lowValues = Sse2.UnpackLow(values, Vector128<byte>.Zero).AsInt16();
                                var highValues = Sse2.UnpackHigh(values, Vector128<byte>.Zero).AsInt16();

                                // process the first values
                                {
                                    var last1 = Sse2.ShiftRightLogical(lowValues, bSrcNextPart);
                                    var last2 = Sse2.ShiftRightLogical(highValues, bSrcNextPart);
                                    last1 = Sse2.ShiftLeftLogical(last1, bDstNextPart);
                                    last2 = Sse2.ShiftLeftLogical(last2, bDstNextPart);

                                    // "cast" from int16 to int8 as we just left-shifted 
                                    last1 = Sse2.And(last1, castByteMask);
                                    last2 = Sse2.And(last2, castByteMask);

                                    // write to destination
                                    var lastPack = Sse2.PackUnsignedSaturate(last1, last2);
                                    var lastResult = Sse2.LoadVector128(dstPtr + i);
                                    lastResult = Sse2.And(lastResult, vLastClearMask); // clear before writing
                                    lastResult = Sse2.Or(lastResult, lastPack); // write last part
                                    Sse2.Store(dstPtr + i, lastResult);
                                }

                                // process the second values
                                {
                                    var next1 = Sse2.ShiftLeftLogical(lowValues, bSrcNextPart);
                                    var next2 = Sse2.ShiftLeftLogical(highValues, bSrcNextPart);

                                    // "cast" from int16 to int8 as we just left-shifted 
                                    next1 = Sse2.And(next1, castByteMask);
                                    next2 = Sse2.And(next2, castByteMask);
                                    next1 = Sse2.ShiftRightLogical(next1, bDstLastPart);
                                    next2 = Sse2.ShiftRightLogical(next2, bDstLastPart);

                                    // write to destination
                                    var nextPack = Sse2.PackUnsignedSaturate(next1, next2);
                                    var nextResult = Sse2.LoadVector128(dstPtr + i + 1);
                                    nextResult = Sse2.And(nextResult, vNextClearMask); // clear before writing
                                    nextResult = Sse2.Or(nextResult, nextPack); // write next part
                                    Sse2.Store(dstPtr + i + 1, nextResult);
                                }
                            }
                        }

                        #endregion

                        for (; i < byteCount; i++)
                        {
                            int value = src[i];

                            if (srcNextPart > 0)
                            {
                                // mask away bits left of relevant bits 
                                int next = src[i + 1] & srcNextClearMask;

                                // shift away bits right of relevant bits
                                value >>= srcNextPart;
                                value |= next << srcLastPart;
                            }

                            dst[i] &= dstLastClearMask; // clear before writing
                            dst[i] |= (byte)(value << dstNextPart); // write last part

                            if (dstNextPart > 0)
                            {
                                dst[i + 1] &= dstNextClearMask; // clear before writing
                                dst[i + 1] |= (byte)(value >> dstLastPart); // write next part
                            }
                        }
                    }
                }
                bitsLeft -= byteCount * 8;
            }

            if (bitsLeft > 0)
            {
                int value = src[i] >> srcNextPart;

                // Mask out all the bits we dont want
                value &= 255 >> (8 - bitsLeft);

                int bitsFree = 8 - dstNextPart;
                int lastBits = bitsFree - bitsLeft;

                // Check if everything fits in the last byte
                if (lastBits >= 0)
                {
                    int mask = (255 >> bitsFree) | (255 << (8 - lastBits));

                    dst[i] = (byte)(
                        (dst[i] & mask) | // Mask out lower and upper bits
                        (value << dstNextPart)); // Insert new bits
                }
                else
                {
                    dst[i] = (byte)(
                        (dst[i] & (255 >> bitsFree)) | // Mask out upper bits
                        (value << dstNextPart)); // Write the lower bits to the upper bits in the first byte

                    dst[i + 1] = (byte)(
                        (dst[i + 1] & (255 << (bitsLeft - bitsFree))) | // Mask out lower bits
                        (value >> bitsFree)); // Write the upper bits to the lower bits of the second byte
                }
            }
        }

        */

        #endregion

        #region ReadByte[Unchecked]

        /// <summary>
        /// Read 1 to 8 bits from a buffer into a byte.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static byte ReadByte(ReadOnlySpan<byte> source, int bitOffset, int bitCount)
        {
            Debug.Assert(bitCount >= 0);
            Debug.Assert(bitCount <= 8);

            if (bitCount == 0)
                return 0;

            int byteOffset = NetUtility.DivBy8(bitOffset);
            int firstByteLength = NetUtility.PowOf2Mod(bitOffset, 8);

            if (bitCount == 8 && firstByteLength == 0)
                return source[byteOffset];

            // mask away unused bits lower than (right of) relevant bits in first byte
            byte first = (byte)(source[byteOffset] >> firstByteLength);

            int bitsInSecondByte = bitCount - (8 - firstByteLength);
            if (bitsInSecondByte < 1)
            {
                // we don't need to read from the second byte, but we DO need
                // to mask away unused bits higher than (left of) relevant bits
                return (byte)(first & (255 >> (8 - bitCount)));
            }

            byte second = source[byteOffset + 1];

            // mask away unused bits higher than (left of) relevant bits in second byte
            second &= (byte)(255 >> (8 - bitsInSecondByte));

            return (byte)(first | (byte)(second << (bitCount - bitsInSecondByte)));
        }

        #endregion

        #region WriteByte[Unchecked]

        /// <summary>
        /// Writes 1 to 8 bits of data to a buffer.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void WriteByte(
            byte source, int bitCount, Span<byte> destination, int destinationBitOffset)
        {
            Debug.Assert(bitCount >= 0);
            Debug.Assert(bitCount <= 8);

            if (bitCount == 0)
                return;

            // Mask out all the bits we dont want
            source &= (byte)(255 >> (8 - bitCount));

            int p = NetUtility.DivBy8(destinationBitOffset);
            int bitsUsed = NetUtility.PowOf2Mod(destinationBitOffset, 8);
            int bitsFree = 8 - bitsUsed;
            int bitsLeft = bitsFree - bitCount;

            // Fast path, everything fits in the first byte
            if (bitsLeft >= 0)
            {
                int mask = (255 >> bitsFree) | (255 << (8 - bitsLeft));

                destination[p] = (byte)(
                    (destination[p] & mask) | // Mask out lower and upper bits
                    (source << bitsUsed)); // Insert new bits

                return;
            }

            destination[p] = (byte)(
                (destination[p] & (255 >> bitsFree)) | // Mask out upper bits
                (source << bitsUsed)); // Write the lower bits to the upper bits in the first byte

            p += 1;

            destination[p] = (byte)(
                (destination[p] & (255 << (bitCount - bitsFree))) | // Mask out lower bits
                (source >> bitsFree)); // Write the upper bits to the lower bits of the second byte
        }

        /// <summary>
        /// Writes a byte of data to a buffer.
        /// </summary>
        public static void WriteByte(
            byte source, Span<byte> destination, int destinationBitOffset)
        {
            WriteByte(source, 8, destination, destinationBitOffset);
        }

        #endregion

        #region VarInt

        /// <summary>
        /// Gets the amount of bytes needed to store a variable sized <see cref="ulong"/>.
        /// </summary>
        /// <returns>Amount of bytes written.</returns>
        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetVarIntSize(ulong value)
        {
            return BitsForValue(value) / 7 + 1;
        }

        /// <summary>
        /// Gets the amount of bytes needed to store a variable sized <see cref="long"/>.
        /// </summary>
        /// <returns>Amount of bytes written.</returns>
        public static int GetVarIntSize(long value)
        {
            ulong zigzag = (ulong)(value << 1) ^ (ulong)(value >> 63);
            return GetVarIntSize(zigzag);
        }

        /// <summary>
        /// Gets the amount of bytes needed to store a variable sized <see cref="uint"/>.
        /// </summary>
        /// <returns>Amount of bytes written.</returns>
        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetVarIntSize(uint value)
        {
            return BitsForValue(value) / 7 + 1;
        }

        /// <summary>
        /// Gets the amount of bytes needed to store a variable sized <see cref="int"/>.
        /// </summary>
        /// <returns>Amount of bytes written.</returns>
        public static int GetVarIntSize(int value)
        {
            uint zigzag = (uint)(value << 1) ^ (uint)(value >> 31);
            return GetVarIntSize(zigzag);
        }

        #endregion

        /// <summary>
        /// Returns how many bits are necessary to hold a certain number
        /// </summary>
        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int BitsForValue(uint value)
        {
            return 32 - BitOperations.LeadingZeroCount(value);
        }

        /// <summary>
        /// Returns how many bits are necessary to hold a certain number.
        /// </summary>
        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int BitsForValue(ulong value)
        {
            return 64 - BitOperations.LeadingZeroCount(value);
        }

        /// <summary>
        /// Returns how many bytes are required to hold a certain number of bits.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int BytesForBits(int bitCount)
        {
            return NetUtility.DivBy8(bitCount + 7);
        }

        /// <summary>
        /// Tries to read a variable sized <see cref="uint"/>.
        /// </summary>
        [CLSCompliant(false)]
        public static OperationStatus ReadVarUInt32(IBitBuffer buffer, bool peek, out uint result)
        {
            const int MaxBytesWithoutOverflow = MaxVarInt32Size - 1;

            result = 0;

            if (!buffer.HasEnoughBits(8))
                return OperationStatus.NeedMoreData;

            int startPosition = buffer.BitPosition;
            try
            {
                // Read the integer 7 bits at a time. 
                // The high bit of the byte when on means to continue reading more bytes.

                byte tmp;
                for (int shift = 0; shift < MaxBytesWithoutOverflow * 7; shift += 7)
                {
                    tmp = buffer.ReadByte();
                    result |= (tmp & 0x7Fu) << shift;

                    if (tmp <= 0x7Fu)
                        return OperationStatus.Done;

                    if (!buffer.HasEnoughBits(8))
                        return OperationStatus.NeedMoreData;
                }

                // Read the 5th byte. Since we already read 28 bits,
                // the value of this byte must fit within 4 bits (32 - 28),
                // and it must not have the high bit set.
                tmp = buffer.ReadByte();
                if (tmp > 0b_1111u)
                {
                    result = default;
                    return OperationStatus.InvalidData;
                }

                result |= (uint)tmp << (MaxBytesWithoutOverflow * 7);
                return OperationStatus.Done;
            }
            finally
            {
                if (peek)
                    buffer.BitPosition = startPosition;
            }
        }

        /// <summary>
        /// Tries to read a variable sized <see cref="ulong"/>.
        /// </summary>
        [CLSCompliant(false)]
        public static OperationStatus ReadVarUInt64(IBitBuffer buffer, bool peek, out ulong result)
        {
            const int MaxBytesWithoutOverflow = MaxVarInt64Size - 1;

            result = 0;

            if (!buffer.HasEnoughBits(8))
                return OperationStatus.NeedMoreData;

            int startPosition = buffer.BitPosition;
            try
            {
                // Read the integer 7 bits at a time. 
                // The high bit of the byte when on means to continue reading more bytes.

                byte tmp;
                for (int shift = 0; shift < MaxBytesWithoutOverflow * 7; shift += 7)
                {
                    tmp = buffer.ReadByte();
                    result |= (tmp & 0x7Ful) << shift;

                    if (tmp <= 0x7Fu)
                        return OperationStatus.Done;

                    if (!buffer.HasEnoughBits(8))
                        return OperationStatus.NeedMoreData;
                }

                // Read the 10th byte. Since we already read 63 bits,
                // the value of this byte must fit within 1 bit (64 - 63),
                // and it must not have the high bit set.
                tmp = buffer.ReadByte();
                if (tmp > 0b_1u)
                    return OperationStatus.InvalidData;

                result |= (ulong)tmp << (MaxBytesWithoutOverflow * 7);
                return OperationStatus.Done;
            }
            finally
            {
                if (peek)
                    buffer.BitPosition = startPosition;
            }
        }
    }
}
