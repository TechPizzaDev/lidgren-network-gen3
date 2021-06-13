using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;

namespace Lidgren.Network
{
    // TODO: check NetBuffer.Read

    public static class BitBufferPeekExtensions
    {
        /// <summary>
        /// Tries to read the specified number of bits without advancing the read position.
        /// </summary>
        /// <param name="destination">The destination span.</param>
        /// <param name="bitCount">The number of bits to read.</param>
        public static bool TryPeekBits(this IBitBuffer buffer, Span<byte> destination, int bitCount)
        {
            if (!buffer.HasEnoughBits(bitCount))
                return false;

            NetBitWriter.CopyBits(buffer.GetBuffer(), buffer.BitPosition, bitCount, destination, 0);
            return true;
        }

        /// <summary>
        /// Reads the specified number of bits without advancing the read position.
        /// </summary>
        /// <param name="destination">The destination span.</param>
        /// <param name="bitCount">The number of bits to read.</param>
        public static void Peek(this IBitBuffer buffer, Span<byte> destination, int bitCount)
        {
            if (!buffer.TryPeekBits(destination, bitCount))
                throw new EndOfMessageException();
        }

        /// <summary>
        /// Reads the specified number of bits,
        /// between one and <paramref name="maxBitCount"/>, 
        /// without advancing the read position.
        /// </summary>
        /// <param name="destination">The destination span.</param>
        /// <param name="bitCount">The number of bits to read.</param>
        /// <param name="maxBitCount">The maximum amount of bits to read.</param>
        public static void PeekBits(this IBitBuffer buffer, Span<byte> destination, int bitCount, int maxBitCount)
        {
            Debug.Assert(bitCount >= 1);
            Debug.Assert(bitCount <= maxBitCount);

            buffer.TryPeekBits(destination, bitCount);
        }

        /// <summary>
        /// Tries to read the specified number of bytes without advancing the read position.
        /// </summary>
        /// <param name="destination">The destination span.</param>
        public static bool TryPeek(this IBitBuffer buffer, Span<byte> destination)
        {
            if (!buffer.IsByteAligned())
                return buffer.TryPeekBits(destination, destination.Length * 8);

            if (!buffer.HasEnoughBits(destination.Length))
                return false;

            buffer.GetBuffer().AsSpan(buffer.BytePosition, destination.Length).CopyTo(destination);
            return true;
        }

        /// <summary>
        /// Reads the specified number of bytes without advancing the read position.
        /// </summary>
        public static void Peek(this IBitBuffer buffer, Span<byte> destination)
        {
            if (!buffer.TryPeek(destination))
                throw new EndOfMessageException();
        }

        /// <summary>
        /// Reads a 1-bit <see cref="bool"/> without advancing the read position.
        /// </summary>
        public static bool PeekBool(this IBitBuffer buffer)
        {
            if (!buffer.HasEnoughBits(1))
                throw new EndOfMessageException();

            return NetBitWriter.ReadByte(buffer.GetBuffer(), buffer.BitPosition, 1) > 0;
        }

        /// <summary>
        /// Reads an <see cref="sbyte"/> without advancing the read position.
        /// </summary>
        [CLSCompliant(false)]
        public static sbyte PeekSByte(this IBitBuffer buffer)
        {
            if (!buffer.HasEnoughBits(8))
                throw new EndOfMessageException();

            return (sbyte)NetBitWriter.ReadByte(buffer.GetBuffer(), buffer.BitPosition, 8);
        }

        /// <summary>
        /// Reads a <see cref="byte"/> without advancing the read position.
        /// </summary>
        public static byte PeekByte(this IBitBuffer buffer)
        {
            if (!buffer.HasEnoughBits(8))
                throw new EndOfMessageException();

            return NetBitWriter.ReadByte(buffer.GetBuffer(), buffer.BitPosition, 8);
        }

        /// <summary>
        /// Reads the specified number of bits into a <see cref="byte"/> without advancing the read position.
        /// </summary>
        public static byte PeekByte(this IBitBuffer buffer, int bitCount)
        {
            if (!buffer.HasEnoughBits(bitCount))
                throw new EndOfMessageException();

            return NetBitWriter.ReadByte(buffer.GetBuffer(), buffer.BitPosition, bitCount);
        }

        #region Int16

        /// <summary>
        /// Reads an <see cref="short"/> without advancing the read position.
        /// </summary>
        public static short PeekInt16(this IBitBuffer buffer)
        {
            Span<byte> tmp = stackalloc byte[sizeof(short)];
            buffer.Peek(tmp);
            return BinaryPrimitives.ReadInt16LittleEndian(tmp);
        }

        /// <summary>
        /// Reads a <see cref="ushort"/> without advancing the read position.
        /// </summary>
        [CLSCompliant(false)]
        public static ushort PeekUInt16(this IBitBuffer buffer)
        {
            Span<byte> tmp = stackalloc byte[sizeof(ushort)];
            buffer.Peek(tmp);
            return BinaryPrimitives.ReadUInt16LittleEndian(tmp);
        }

        /// <summary>
        /// Reads the specified number of bits into an <see cref="short"/> without advancing the read position.
        /// </summary>
        public static short PeekInt16(this IBitBuffer buffer, int bitCount)
        {
            Span<byte> tmp = stackalloc byte[sizeof(short)];
            buffer.PeekBits(tmp, bitCount, tmp.Length * 8);
            return BinaryPrimitives.ReadInt16LittleEndian(tmp);
        }

        /// <summary>
        /// Reads the specified number of bits into an <see cref="ushort"/> without advancing the read position.
        /// </summary>
        [CLSCompliant(false)]
        public static ushort PeekUInt16(this IBitBuffer buffer, int bitCount)
        {
            Span<byte> tmp = stackalloc byte[sizeof(ushort)];
            buffer.PeekBits(tmp, bitCount, tmp.Length * 8);
            return BinaryPrimitives.ReadUInt16LittleEndian(tmp);
        }

        #endregion

        #region Int32

        /// <summary>
        /// Reads an <see cref="int"/> without advancing the read position.
        /// </summary>
        public static int PeekInt32(this IBitBuffer buffer)
        {
            Span<byte> tmp = stackalloc byte[sizeof(int)];
            buffer.Peek(tmp);
            return BinaryPrimitives.ReadInt32LittleEndian(tmp);
        }

        /// <summary>
        /// Reads a <see cref="uint"/> without advancing the read position.
        /// </summary>
        [CLSCompliant(false)]
        public static uint PeekUInt32(this IBitBuffer buffer)
        {
            Span<byte> tmp = stackalloc byte[sizeof(uint)];
            buffer.Peek(tmp);
            return BinaryPrimitives.ReadUInt32LittleEndian(tmp);
        }

        /// <summary>
        /// Reads the specified number of bits into an <see cref="int"/> without advancing the read position.
        /// </summary>
        public static int PeekInt32(this IBitBuffer buffer, int bitCount)
        {
            Span<byte> tmp = stackalloc byte[sizeof(int)];
            buffer.PeekBits(tmp, bitCount, tmp.Length * 8);
            return BinaryPrimitives.ReadInt32LittleEndian(tmp);
        }

        /// <summary>
        /// Reads the specified number of bits into an <see cref="uint"/> without advancing the read position.
        /// </summary>
        [CLSCompliant(false)]
        public static uint PeekUInt32(this IBitBuffer buffer, int bitCount)
        {
            Span<byte> tmp = stackalloc byte[sizeof(uint)];
            buffer.PeekBits(tmp, bitCount, tmp.Length * 8);
            return BinaryPrimitives.ReadUInt32LittleEndian(tmp);
        }

        #endregion

        #region Int64

        /// <summary>
        /// Reads an <see cref="long"/> without advancing the read position.
        /// </summary>
        public static long PeekInt64(this IBitBuffer buffer)
        {
            Span<byte> tmp = stackalloc byte[sizeof(long)];
            buffer.Peek(tmp);
            return BinaryPrimitives.ReadInt64LittleEndian(tmp);
        }

        /// <summary>
        /// Reads a <see cref="ulong"/> without advancing the read position.
        /// </summary>
        [CLSCompliant(false)]
        public static ulong PeekUInt64(this IBitBuffer buffer)
        {
            Span<byte> tmp = stackalloc byte[sizeof(ulong)];
            buffer.Peek(tmp);
            return BinaryPrimitives.ReadUInt64LittleEndian(tmp);
        }

        /// <summary>
        /// Reads the specified number of bits into an <see cref="long"/> without advancing the read position.
        /// </summary>
        public static long PeekInt64(this IBitBuffer buffer, int bitCount)
        {
            Span<byte> tmp = stackalloc byte[sizeof(long)];
            buffer.PeekBits(tmp, bitCount, tmp.Length * 8);
            return BinaryPrimitives.ReadInt64LittleEndian(tmp);
        }

        /// <summary>
        /// Reads the specified number of bits into an <see cref="ulong"/> without advancing the read position.
        /// </summary>
        [CLSCompliant(false)]
        public static ulong PeekUInt64(this IBitBuffer buffer, int bitCount)
        {
            Span<byte> tmp = stackalloc byte[sizeof(ulong)];
            buffer.PeekBits(tmp, bitCount, tmp.Length * 8);
            return BinaryPrimitives.ReadUInt64LittleEndian(tmp);
        }

        #endregion

        #region VarInt 

        /// <summary>
        /// Tries to read a variable sized <see cref="uint"/> without advancing the read position.
        /// </summary>
        [CLSCompliant(false)]
        public static OperationStatus PeekVarUInt32(this IBitBuffer buffer, out uint result)
        {
            return NetBitWriter.ReadVarUInt32(buffer, peek: true, out result);
        }

        /// <summary>
        /// Tries to read a variable sized <see cref="ulong"/> without advancing the read position.
        /// </summary>
        [CLSCompliant(false)]
        public static OperationStatus PeekVarUInt64(this IBitBuffer buffer, out ulong result)
        {
            return NetBitWriter.ReadVarUInt64(buffer, peek: true, out result);
        }

        [CLSCompliant(false)]
        public static uint PeekVarUInt32(this IBitBuffer buffer)
        {
            var status = buffer.PeekVarUInt32(out uint value);
            if (status == OperationStatus.Done)
                return value;

            if (status == OperationStatus.NeedMoreData)
                throw new EndOfMessageException();

            return default;
        }

        [CLSCompliant(false)]
        public static ulong PeekVarUInt64(this IBitBuffer buffer)
        {
            var status = buffer.PeekVarUInt64(out ulong value);
            if (status == OperationStatus.Done)
                return value;

            if (status == OperationStatus.NeedMoreData)
                throw new EndOfMessageException();

            return default;
        }

        /// <summary>
        /// Reads a variable sized <see cref="int"/> written by <see cref="WriteVar(int)"/>.
        /// </summary>
        public static int PeekVarInt32(this IBitBuffer buffer)
        {
            uint n = buffer.PeekVarUInt32();
            return (int)(n >> 1) ^ -(int)(n & 1); // decode zigzag
        }

        /// <summary>
        /// Reads a variable sized <see cref="long"/> written by <see cref="WriteVar(long)"/>.
        /// </summary>
        public static long PeekVarInt64(this IBitBuffer buffer)
        {
            ulong n = buffer.PeekVarUInt64();
            return (long)(n >> 1) ^ -(long)(n & 1); // decode zigzag
        }

        #endregion

        #region Float

        /// <summary>
        /// Reads a 32-bit <see cref="float"/> without advancing the read position.
        /// </summary>
        public static float PeekSingle(this IBitBuffer buffer)
        {
            int intValue = buffer.PeekInt32();
            return BitConverter.Int32BitsToSingle(intValue);
        }

        /// <summary>
        /// Reads a 64-bit <see cref="double"/> without advancing the read position.
        /// </summary>
        public static double PeekDouble(this IBitBuffer buffer)
        {
            long intValue = buffer.PeekInt64();
            return BitConverter.Int64BitsToDouble(intValue);
        }

        #endregion

        //public static bool PeekStringHeader(this IBitBuffer buffer, out int length)
        //{
        //    if (buffer == null)
        //        throw new ArgumentNullException(nameof(buffer));
        //
        //    int startPosition = buffer.BitPosition;
        //    bool read = buffer.ReadStringLength(out length);
        //    buffer.BitPosition = startPosition;
        //    return read;
        //}

        /// <summary>
        /// Reads a <see cref="string"/> without advancing the read position.
        /// </summary>
        public static bool PeekString(this IBitBuffer buffer, [MaybeNullWhen(false)] out string result, bool replaceInvalidSequences = true)
        {
            int startPosition = buffer.BitPosition;
            bool read = buffer.ReadString(out result, replaceInvalidSequences);
            buffer.BitPosition = startPosition;
            return read;
        }

        /// <summary>
        /// Reads a <see cref="string"/> without advancing the read position.
        /// </summary>
        /// <exception cref="InvalidDataException"></exception>
        public static string PeekString(this IBitBuffer buffer, bool replaceInvalidSequences)
        {
            if (!buffer.PeekString(out string? value, replaceInvalidSequences))
                throw new InvalidDataException();
            return value;
        }

        /// <summary>
        /// Reads a <see cref="string"/> without advancing the read position.
        /// </summary>
        public static string PeekString(this IBitBuffer buffer)
        {
            return buffer.PeekString(replaceInvalidSequences: true);
        }

        /// <summary>
        /// Reads a <see cref="TimeSpan"/> without advancing the read position.
        /// </summary>
        public static TimeSpan PeekTimeSpan(this IBitBuffer buffer)
        {
            return new TimeSpan(buffer.PeekVarInt64());
        }

        /// <summary>
        /// Reads an enum of type <typeparamref name="TEnum"/> without advancing the read position.
        /// </summary>
        public static TEnum PeekEnum<TEnum>(this IBitBuffer buffer)
            where TEnum : Enum
        {
            if (Unsafe.SizeOf<TEnum>() == 1)
            {
                byte b = buffer.PeekByte();
                return Unsafe.As<byte, TEnum>(ref b);
            }
            else if (Unsafe.SizeOf<TEnum>() == 2)
            {
                ushort b = buffer.PeekUInt16();
                return Unsafe.As<ushort, TEnum>(ref b);
            }
            else if (Unsafe.SizeOf<TEnum>() == 4)
            {
                int b = buffer.PeekVarInt32();
                return Unsafe.As<int, TEnum>(ref b);
            }
            else
            {
                long b = buffer.PeekVarInt64();
                return Unsafe.As<long, TEnum>(ref b);
            }
        }
    }
}
