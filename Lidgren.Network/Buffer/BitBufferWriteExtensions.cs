using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Lidgren.Network
{
    public static class BitBufferWriteExtensions
    {
        /// <summary>
        /// Writes a certain amount of bits from a span.
        /// </summary>
        /// <param name="bitOffset">The offset in bits into <paramref name="source"/>.</param>
        /// <param name="bitCount">The amount of bits to copy from <paramref name="source"/>.</param>
        public static void Write(this IBitBuffer buffer, ReadOnlySpan<byte> source, int bitOffset, int bitCount)
        {
            if (source.IsEmpty)
                return;

            buffer.EnsureEnoughBitCapacity(bitCount);
            NetBitWriter.CopyBits(source, bitOffset, bitCount, buffer.GetBuffer(), buffer.BitPosition);
            buffer.IncrementBitPosition(bitCount);
        }

        /// <summary>
        /// Writes a certain amount of bits from a span.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write(this IBitBuffer buffer, ReadOnlySpan<byte> source, int bitCount)
        {
            buffer.Write(source, 0, bitCount);
        }

        /// <summary>
        /// Writes bytes from a span.
        /// </summary>
        public static void Write(this IBitBuffer buffer, ReadOnlySpan<byte> source)
        {
            if (!buffer.IsByteAligned())
            {
                buffer.Write(source, 0, source.Length * 8);
                return;
            }

            buffer.EnsureEnoughBitCapacity(source.Length * 8);
            source.CopyTo(buffer.GetBuffer().AsSpan(buffer.BytePosition));
            buffer.IncrementBitPosition(source.Length * 8);
        }

        #region Bool

        /// <summary>
        /// Writes a <see cref="bool"/> value using 8 bits.
        /// </summary>
        public static void Write(this IBitBuffer buffer, bool value)
        {
            buffer.Write(value ? (byte)1 : (byte)0);
        }

        /// <summary>
        /// Writes a <see cref="bool"/> value using 1 bit.
        /// </summary>
        public static void WriteBit(this IBitBuffer buffer, bool value)
        {
            buffer.EnsureEnoughBitCapacity(1);
            NetBitWriter.WriteByte(value ? (byte)1 : (byte)0, 1, buffer.GetBuffer(), buffer.BitPosition);
            buffer.IncrementBitPosition(1);
        }

        #endregion

        #region Int8

        /// <summary>
        /// Writes a <see cref="sbyte"/>.
        /// </summary>
        [CLSCompliant(false)]
        public static void Write(this IBitBuffer buffer, sbyte value)
        {
            buffer.EnsureEnoughBitCapacity(8);
            NetBitWriter.WriteByte((byte)value, 8, buffer.GetBuffer(), buffer.BitPosition);
            buffer.IncrementBitPosition(8);
        }

        /// <summary>
        /// Write a <see cref="byte"/>.
        /// </summary>
        public static void Write(this IBitBuffer buffer, byte value)
        {
            buffer.EnsureEnoughBitCapacity(8);
            NetBitWriter.WriteByte(value, 8, buffer.GetBuffer(), buffer.BitPosition);
            buffer.IncrementBitPosition(8);
        }

        /// <summary>
        /// Writes a <see cref="byte"/> using 1 to 8 bits.
        /// </summary>
        public static void Write(this IBitBuffer buffer, byte source, int bitCount)
        {
            buffer.EnsureEnoughBitCapacity(bitCount, maxBitCount: 8);
            NetBitWriter.WriteByte(source, bitCount, buffer.GetBuffer(), buffer.BitPosition);
            buffer.IncrementBitPosition(bitCount);
        }

        #endregion

        #region Int16

        /// <summary>
        /// Writes a 16-bit <see cref="short"/>.
        /// </summary>
        public static void Write(this IBitBuffer buffer, short value)
        {
            Span<byte> tmp = stackalloc byte[sizeof(short)];
            BinaryPrimitives.WriteInt16LittleEndian(tmp, value);
            buffer.Write(tmp);
        }

        /// <summary>
        /// Writes an 16-bit <see cref="ushort"/>.
        /// </summary>
        /// <param name="value"></param>
        [CLSCompliant(false)]
        public static void Write(this IBitBuffer buffer, ushort value)
        {
            Span<byte> tmp = stackalloc byte[sizeof(ushort)];
            BinaryPrimitives.WriteUInt16LittleEndian(tmp, value);
            buffer.Write(tmp);
        }

        #endregion

        #region Int32

        /// <summary>
        /// Writes a 32-bit <see cref="int"/>.
        /// </summary>
        public static void Write(this IBitBuffer buffer, int value)
        {
            Span<byte> tmp = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(tmp, value);
            buffer.Write(tmp);
        }

        /// <summary>
        /// Writes a 32-bit <see cref="uint"/>.
        /// </summary>
        [CLSCompliant(false)]
        public static void Write(this IBitBuffer buffer, uint value)
        {
            Span<byte> tmp = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(tmp, value);
            buffer.Write(tmp);
        }

        #endregion

        #region Int64

        /// <summary>
        /// Writes a 64-bit <see cref="long"/>.
        /// </summary>
        public static void Write(this IBitBuffer buffer, long value)
        {
            Span<byte> tmp = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(tmp, value);
            buffer.Write(tmp);
        }

        /// <summary>
        /// Writes a 64-bit <see cref="ulong"/>.
        /// </summary>
        [CLSCompliant(false)]
        public static void Write(this IBitBuffer buffer, ulong value)
        {
            Span<byte> tmp = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(tmp, value);
            buffer.Write(tmp);
        }

        #endregion

        #region Float

        /// <summary>
        /// Writes a 32-bit <see cref="float"/>.
        /// </summary>
        public static void Write(this IBitBuffer buffer, float value)
        {
            int intValue = BitConverter.SingleToInt32Bits(value);
            buffer.Write(intValue);
        }

        /// <summary>
        /// Writes a 64-bit <see cref="double"/>.
        /// </summary>
        public static void Write(this IBitBuffer buffer, double value)
        {
            long intValue = BitConverter.DoubleToInt64Bits(value);
            buffer.Write(intValue);
        }

        #endregion

        /// <summary>
        /// Writes a <see cref="short"/> using 1 to 16 bits.
        /// </summary>
        public static void Write(this IBitBuffer buffer, short value, int bitCount)
        {
            Span<byte> tmp = stackalloc byte[sizeof(short)];
            if (bitCount != tmp.Length * 8)
            {
                // make first bit sign
                int signBit = 1 << (bitCount - 1);
                if (value < 0)
                    value = (short)((-value - 1) | signBit);
                else
                    value = (short)(value & (~signBit));
            }

            BinaryPrimitives.WriteInt16LittleEndian(tmp, value);
            buffer.Write(tmp, bitCount);
        }

        /// <summary>
        /// Writes an unsigned <see cref="ushort"/> using 1 to 16 bits.
        /// </summary>
        [CLSCompliant(false)]
        public static void Write(this IBitBuffer buffer, ushort value, int bitCount)
        {
            Span<byte> tmp = stackalloc byte[sizeof(ushort)];
            BinaryPrimitives.WriteUInt16LittleEndian(tmp, value);
            buffer.Write(tmp, bitCount);
        }

        /// <summary>
        /// Writes a <see cref="uint"/> using 1 to 32 bits.
        /// </summary>
        [CLSCompliant(false)]
        public static void Write(this IBitBuffer buffer, uint value, int bitCount)
        {
            Span<byte> tmp = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(tmp, value);
            buffer.Write(tmp, bitCount);
        }

        /// <summary>
        /// Writes a <see cref="int"/> using 1 to 32 bits.
        /// </summary>
        public static void Write(this IBitBuffer buffer, int value, int bitCount)
        {
            Span<byte> tmp = stackalloc byte[sizeof(int)];
            if (bitCount != tmp.Length * 8)
            {
                // make first bit sign
                int signBit = 1 << (bitCount - 1);
                if (value < 0)
                    value = (-value - 1) | signBit;
                else
                    value &= ~signBit;
            }

            BinaryPrimitives.WriteInt32LittleEndian(tmp, value);
            buffer.Write(tmp, bitCount);
        }

        /// <summary>
        /// Writes an <see cref="ulong"/> using 1 to 64 bits
        /// </summary>
        [CLSCompliant(false)]
        public static void Write(this IBitBuffer buffer, ulong value, int bitCount)
        {
            Span<byte> tmp = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(tmp, value);
            buffer.Write(tmp, bitCount);
        }

        /// <summary>
        /// Writes a <see cref="long"/> using 1 to 64 bits.
        /// </summary>
        public static void Write(this IBitBuffer buffer, long value, int bitCount)
        {
            Span<byte> tmp = stackalloc byte[sizeof(long)];
            if (bitCount != tmp.Length * 8)
            {
                // make first bit sign
                long signBit = 1 << (bitCount - 1);
                if (value < 0)
                    value = (-value - 1) | signBit;
                else
                    value &= ~signBit;
            }

            BinaryPrimitives.WriteInt64LittleEndian(tmp, value);
            buffer.Write(tmp, bitCount);
        }

        #region VarInt

        /// <summary>
        /// Write variable sized <see cref="ulong"/>.
        /// </summary>
        /// <returns>Amount of bytes written.</returns>
        [CLSCompliant(false)]
        public static int WriteVar(this IBitBuffer buffer, ulong value)
        {
            Span<byte> tmp = stackalloc byte[NetBitWriter.MaxVarInt64Size];

            int offset = 0;
            ulong bits = value;
            while (bits > 0x7Fu)
            {
                tmp[offset++] = (byte)(bits | ~0x7Fu);
                bits >>= 7;
            }
            tmp[offset++] = (byte)bits;

            buffer.Write(tmp.Slice(0, offset));
            return offset;
        }

        /// <summary>
        /// Write variable sized <see cref="long"/>.
        /// </summary>
        /// <returns>Amount of bytes written.</returns>
        public static int WriteVar(this IBitBuffer buffer, long value)
        {
            ulong zigzag = (ulong)(value << 1) ^ (ulong)(value >> 63);
            return buffer.WriteVar(zigzag);
        }

        /// <summary>
        /// Write variable sized <see cref="uint"/>.
        /// </summary>
        /// <returns>Amount of bytes written.</returns>
        [CLSCompliant(false)]
        public static int WriteVar(this IBitBuffer buffer, uint value)
        {
            Span<byte> tmp = stackalloc byte[NetBitWriter.MaxVarInt32Size];

            int offset = 0;
            uint bits = value;
            while (bits > 0x7Fu)
            {
                tmp[offset++] = (byte)(bits | ~0x7Fu);
                bits >>= 7;
            }
            tmp[offset++] = (byte)bits;

            buffer.Write(tmp.Slice(0, offset));
            return offset;
        }

        /// <summary>
        /// Write variable sized <see cref="int"/>.
        /// </summary>
        /// <returns>Amount of bytes written.</returns>
        public static int WriteVar(this IBitBuffer buffer, int value)
        {
            uint zigzag = (uint)(value << 1) ^ (uint)(value >> 31);
            return buffer.WriteVar(zigzag);
        }

        #endregion

        /// <summary>
        /// Compress (lossy) a <see cref="float"/> in the range -1..1 using the specified amount of bits.
        /// </summary>
        public static void WriteSigned(this IBitBuffer buffer, float value, int bitCount)
        {
            LidgrenException.Assert(
                (value >= -1.0) && (value <= 1.0),
                " WriteSignedSingle() must be passed a float in the range -1 to 1; val is " + value);

            float unit = (value + 1f) * 0.5f;
            int maxVal = (1 << bitCount) - 1;
            uint writeVal = (uint)(unit * maxVal);

            buffer.Write(writeVal, bitCount);
        }

        /// <summary>
        /// Compress (lossy) a <see cref="float"/> in the range 0..1 using the specified amount of bits.
        /// </summary>
        public static void WriteUnit(this IBitBuffer buffer, float value, int bitCount)
        {
            LidgrenException.Assert(
                (value >= 0.0) && (value <= 1.0),
                " WriteUnitSingle() must be passed a float in the range 0 to 1; val is " + value);

            int maxValue = (1 << bitCount) - 1;
            uint writeVal = (uint)(value * maxValue);

            buffer.Write(writeVal, bitCount);
        }

        /// <summary>
        /// Compress a <see cref="float"/> within a specified range using the specified amount of bits.
        /// </summary>
        public static void WriteRanged(this IBitBuffer buffer, float value, float min, float max, int bitCount)
        {
            LidgrenException.Assert(
                (value >= min) && (value <= max),
                " WriteRangedSingle() must be passed a float in the range MIN to MAX; val is " + value);

            float range = max - min;
            float unit = (value - min) / range;
            int maxVal = (1 << bitCount) - 1;

            buffer.Write((uint)(maxVal * unit), bitCount);
        }

        /// <summary>
        /// Writes an <see cref="int"/> with the least amount of bits needed for the specified range.
        /// Returns the number of bits written.
        /// </summary>
        public static int WriteRanged(this IBitBuffer buffer, int min, int max, int value)
        {
            LidgrenException.Assert(value >= min && value <= max, "Value not within min/max range!");

            uint range = (uint)(max - min);
            int numBits = NetBitWriter.BitsForValue(range);

            uint rvalue = (uint)(value - min);
            buffer.Write(rvalue, numBits);

            return numBits;
        }

        public static NetBlockWriter OpenBlockWriter(this IBitBuffer buffer, bool isFinalBlock)
        {
            return new NetBlockWriter(buffer, isFinalBlock);
        }

        /// <summary>
        /// Writes byte blocks from a span of bytes.
        /// </summary>
        public static void Write(this IBitBuffer buffer, ReadOnlySpan<byte> source, bool isFinalBlock)
        {
            using NetBlockWriter writer = buffer.OpenBlockWriter(isFinalBlock);
            writer.Write(source);
        }

        /// <summary>
        /// Writes byte blocks of UTF-8 characters from a span of characters.
        /// </summary>
        public static void Write(this IBitBuffer buffer, ReadOnlySpan<char> source, bool isFinalBlock)
        {
            using NetBlockWriter writer = buffer.OpenBlockWriter(isFinalBlock);
            writer.Write(source);
        }

        /// <summary>
        /// Writes byte blocks with final block of UTF-8 characters from a span of characters.
        /// </summary>
        public static void Write(this IBitBuffer buffer, ReadOnlySpan<char> source)
        {
            buffer.Write(source, isFinalBlock: true);
        }

        /// <summary>
        /// Writes byte blocks of UTF-8 characters from a <see cref="string"/>.
        /// </summary>
        public static void Write(this IBitBuffer buffer, string? source, bool isFinalBlock)
        {
            buffer.Write(source.AsSpan(), isFinalBlock);
        }

        /// <summary>
        /// Writes byte blocks with final block of UTF-8 characters from a <see cref="string"/>.
        /// </summary>
        public static void Write(this IBitBuffer buffer, string? source)
        {
            buffer.Write(source, isFinalBlock: true);
        }

        /// <summary>
        /// Writes an <see cref="IPAddress"/> .
        /// </summary>
        public static void Write(this IBitBuffer buffer, IPAddress address)
        {
            Span<byte> tmp = stackalloc byte[16];
            if (!address.TryWriteBytes(tmp, out int count))
                throw new ArgumentException("Failed to get address bytes.", nameof(address));
            tmp = tmp.Slice(0, count);

            buffer.Write((byte)tmp.Length);
            buffer.Write(tmp);
        }

        /// <summary>
        /// Writes an <see cref="IPEndPoint"/> description.
        /// </summary>
        public static void Write(this IBitBuffer buffer, IPEndPoint endPoint)
        {
            if (endPoint == null)
                throw new ArgumentNullException(nameof(endPoint));

            buffer.Write(endPoint.Address);
            buffer.Write((ushort)endPoint.Port);
        }

        /// <summary>
        /// Writes a <see cref="TimeSpan"/>.
        /// </summary>
        public static void Write(this IBitBuffer buffer, TimeSpan time)
        {
            buffer.WriteVar(time.Ticks);
        }

        /// <summary>
        /// Writes the current local time (<see cref="NetTime.Now"/>).
        /// </summary>
        public static void WriteLocalTime(this IBitBuffer buffer)
        {
            buffer.Write(NetTime.Now);
        }

        /// <summary>
        /// Append all the bits from a <see cref="IBitBuffer"/> to this buffer.
        /// </summary>
        public static void Write(this IBitBuffer buffer, IBitBuffer sourceBuffer)
        {
            buffer.Write(sourceBuffer.GetBuffer(), 0, sourceBuffer.BitLength);
        }

        /// <summary>
        /// Encodes a <see cref="NetBitArray"/> to this buffer.
        /// </summary>
        public static void Write(this IBitBuffer buffer, NetBitArray bitArray)
        {
            buffer.WriteVar(bitArray.Length);

            ReadOnlySpan<uint> values = bitArray.GetBuffer().Span;
            int bitsLeft = NetUtility.PowOf2Mod(bitArray.Length, NetBitArray.BitsPerElement);
            if (bitsLeft == 0)
            {
                for (int i = 0; i < values.Length; i++)
                    buffer.Write(values[i]);
            }
            else
            {
                for (int i = 0; i < values.Length - 1; i++)
                    buffer.Write(values[i]);

                uint last = values[^1];
                buffer.Write(last, bitsLeft);
            }
        }

        /// <summary>
        /// Writes the bytes of a <typeparamref name="T"/> value to this buffer.
        /// </summary>
        public static void Write<T>(this IBitBuffer buffer, in T value)
            where T : unmanaged
        {
            ReadOnlySpan<T> span = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(value), 1);
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(span);
            buffer.Write(bytes);
        }

        /// <summary>
        /// Encodes a <see cref="BigInteger"/> to this buffer.
        /// </summary>
        public static void Write(this IBitBuffer buffer, BigInteger bigInt, bool isUnsigned)
        {
            int byteCount = bigInt.GetByteCount(isUnsigned);
            Span<byte> tmp = stackalloc byte[4096];
            byte[]? rented = null;
            if (byteCount > tmp.Length)
            {
                rented = ArrayPool<byte>.Shared.Rent(byteCount);
                tmp = rented;
            }

            try
            {
                if (!bigInt.TryWriteBytes(tmp, out int written, isUnsigned, isBigEndian: false))
                    throw new Exception();

                buffer.WriteVar(written);
                buffer.Write(tmp.Slice(0, written));
            }
            finally
            {
                if (rented != null)
                    ArrayPool<byte>.Shared.Return(rented);
            }
        }

        /// <summary>
        /// Encodes an enum to this buffer.
        /// </summary>
        /// <typeparam name="TEnum">The enum type. The base type determines encoding method.</typeparam>
        /// <param name="buffer">The destination buffer.</param>
        /// <param name="value">The value to encode.</param>
        public static void Write<TEnum>(this IBitBuffer buffer, TEnum value)
            where TEnum : unmanaged, Enum
        {
            if (Unsafe.SizeOf<TEnum>() == 1)
            {
                byte v = Unsafe.As<TEnum, byte>(ref value);
                buffer.Write(v);
            }
            else if (Unsafe.SizeOf<TEnum>() == 2)
            {
                ushort v = Unsafe.As<TEnum, ushort>(ref value);
                buffer.Write(v);
            }
            else if (Unsafe.SizeOf<TEnum>() == 4)
            {
                uint v = (uint)EnumConverter.ToUInt64(value);
                buffer.WriteVar(v);
            }
            else
            {
                ulong v = EnumConverter.ToUInt64(value);
                buffer.WriteVar(v);
            }
        }

        /// <summary>
        /// Byte-aligns the write position, 
        /// decreasing work for subsequent writes if the position was not aligned.
        /// </summary>
        public static void WritePadBits(this IBitBuffer buffer)
        {
            buffer.BitPosition = NetBitWriter.BytesForBits(buffer.BitPosition) * 8;
            buffer.EnsureBitCapacity(buffer.BitPosition);
            buffer.SetLengthByPosition();
        }

        /// <summary>
        /// Pads the write position with the specified number of bits.
        /// </summary>
        public static void WritePadBits(this IBitBuffer buffer, int bitCount)
        {
            Debug.Assert(bitCount >= 0);
            if (bitCount > 0)
            {
                buffer.BitPosition += bitCount;
                buffer.EnsureBitCapacity(buffer.BitPosition);
                buffer.SetLengthByPosition();
            }
        }

        /// <summary>
        /// Pads the write position with the specified number of bytes.
        /// </summary>
        public static void WritePadBytes(this IBitBuffer buffer, int byteCount)
        {
            buffer.WritePadBits(byteCount * 8);
        }
    }

}
