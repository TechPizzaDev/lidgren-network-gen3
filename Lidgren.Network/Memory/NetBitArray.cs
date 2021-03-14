using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Lidgren.Network
{
    // TODO: optimize

    /// <summary>
    /// Mutable array of bits.
    /// </summary>
    public readonly struct NetBitArray : IEquatable<NetBitArray>
    {
        public const int BitsPerElement = sizeof(uint) * 8;

        private readonly uint[]? _data;

        /// <summary>
        /// Gets the total number of stored bits.
        /// </summary>
        public int Length { get; }

        /// <summary>
        /// Gets the number of bits set to one.
        /// </summary>
        public int PopCount
        {
            get
            {
                int sum = 0;
                if (_data != null)
                {
                    for (int i = 0; i < _data.Length; i++)
                        sum += BitOperations.PopCount(_data[i]);
                }
                return sum;
            }
        }

        /// <summary>
        /// Gets whether all bits are set to zero.
        /// </summary>
        public bool IsZero
        {
            get
            {
                if (_data != null)
                {
                    for (int i = 0; i < _data.Length; i++)
                        if (BitOperations.PopCount(_data[i]) != 0)
                            return false;
                }
                return true;
            }
        }

        /// <summary>
        /// Gets or sets a bit at the specified index.
        /// </summary>
        [System.Runtime.CompilerServices.IndexerName("Bits")]
        public bool this[int index]
        {
            get => Get(index);
            set => Set(index, value);
        }

        /// <summary>
        /// Constructs the bit array with a certain capacity.
        /// </summary>
        public NetBitArray(int bitCapacity)
        {
            if (bitCapacity < 0)
                throw new ArgumentOutOfRangeException(nameof(bitCapacity));

            Length = bitCapacity;
            _data = bitCapacity == 0 ? null : new uint[(Length + BitsPerElement - 1) / BitsPerElement];
        }

        [CLSCompliant(false)]
        public Memory<uint> GetBuffer()
        {
            return _data.AsMemory();
        }

        public NetBitArray Slice(int start, int count)
        {
            NetBitArray array = new NetBitArray(count);
            ReadOnlySpan<byte> srcBytes = MemoryMarshal.AsBytes(_data.AsSpan());
            Span<byte> dstBytes = MemoryMarshal.AsBytes(array._data.AsSpan());
            NetBitWriter.CopyBits(srcBytes, start, count, dstBytes, start);
            return array;
        }

        /// <summary>
        /// Shift all bits one step down, cycling the first bit to the top.
        /// </summary>
        public void RotateDown()
        {
            // TODO: check if it can be optimized with BitOperations

            if (_data == null)
                return;

            uint firstBit = _data[0] & 1;

            for (int i = 0; i < _data.Length - 1; i++)
                _data[i] = ((_data[i] >> 1) & ~(1 << 31)) | _data[i + 1] << 31;

            int lastIndex = Length - 1 - (BitsPerElement * (_data.Length - 1));

            // special handling of last int
            ref uint last = ref _data[^1];
            last >>= 1;
            last |= firstBit << lastIndex;
        }

        /// <summary>
        /// Gets the first (lowest) bit with a given value.
        /// </summary>
        public int IndexOf(bool value)
        {
            if (_data == null)
                return -1;

            int flag = value ? 1 : 0;
            int offset = 0;
            uint data = _data[0];

            int a = 0;
            while (((data >> a) & 1) != flag)
            {
                a++;
                if (a == BitsPerElement)
                {
                    offset++;
                    a = 0;
                    data = _data[offset];
                }
            }

            return (offset * BitsPerElement) + a;
        }

        /// <summary>
        /// Gets the bit at the specified index.
        /// </summary>
        public bool Get(int bitIndex)
        {
            return (_data![bitIndex / BitsPerElement] & (1 << (bitIndex % BitsPerElement))) != 0;
        }

        /// <summary>
        /// Sets or clears the bit at the specified index.
        /// </summary>
        public void Set(int bitIndex, bool value)
        {
            int index = bitIndex / BitsPerElement;
            uint mask = (uint)(1 << (bitIndex % BitsPerElement));
            if (value)
            {
                _data![index] |= mask;
            }
            else
            {
                _data![index] &= ~mask;
            }
        }

        /// <summary>
        /// Sets all values to a specified value.
        /// </summary>
        public void SetAll(bool value)
        {
            if (value)
            {
                _data.AsSpan().Fill(uint.MaxValue);
            }
            else
            {
                _data.AsSpan().Clear();
            }
        }

        /// <summary>
        /// Sets all bits to zero.
        /// </summary>
        public void Clear()
        {
            SetAll(false);
        }

        /// <summary>
        /// Returns a string that represents this bit array.
        /// </summary>
        public override string ToString()
        {
            var bdr = new StringBuilder(Length + 2);
            bdr.Append('[');
            for (int i = 0; i < Length; i++)
                bdr.Append(Get(Length - i - 1) ? '1' : '0');
            bdr.Append(']');
            return bdr.ToString();
        }

        public bool Equals(NetBitArray other)
        {
            if (Length != other.Length)
                return false;

            Span<uint> d1 = _data;
            Span<uint> d2 = other._data;

            for (int i = 0; i < d1.Length - 1; i++)
            {
                if (d1[i] != d2[i])
                    return false;
            }

            if (d1.Length > 0)
            {
                uint mask = ~(uint.MaxValue << (Length % BitsPerElement));
                return (d1[^1] & mask) == (d2[^1] & mask);
            }

            return true;
        }

        public override bool Equals(object? obj)
        {
            return obj is NetBitArray other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                uint result = 17;
                if (_data != null)
                {
                    for (int i = 0; i < _data.Length - 1; i++)
                        result = result * 31 + _data[i];

                    if (_data.Length > 0)
                    {
                        uint tmp = _data[^1];
                        tmp &= ~(uint.MaxValue << (Length % BitsPerElement));
                        result = result * 31 + tmp;
                    }
                }
                return (int)result;
            }
        }

        public static bool operator ==(NetBitArray left, NetBitArray right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(NetBitArray left, NetBitArray right)
        {
            return !(left == right);
        }
    }
}
