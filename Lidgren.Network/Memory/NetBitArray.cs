/* Copyright (c) 2010 Michael Lidgren

Permission is hereby granted, free of charge, to any person obtaining a copy of this software
and associated documentation files (the "Software"), to deal in the Software without
restriction, including without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom
the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE
USE OR OTHER DEALINGS IN THE SOFTWARE.

*/
using System;
using System.Numerics;
using System.Text;

namespace Lidgren.Network
{
    /// <summary>
    /// Fixed size vector of bits.
    /// </summary>
    public sealed class NetBitVector
    {
        private const int BitsPerElement = sizeof(int) * 8;

        private readonly uint[] _data;

        /// <summary>
        /// Gets the number of bits stored in this vector.
        /// </summary>
        public int Capacity { get; }

        /// <summary>
        /// Gets the number of bits set to one.
        /// </summary>
        public int PopCount
        {
            get
            {
                int sum = 0;
                for (int i = 0; i < _data.Length; i++)
                    sum += BitOperations.PopCount(_data[i]);
                return sum;
            }
        }

        /// <summary>
        /// Gets whether this vector only contains bits set to zero.
        /// </summary>
        public bool IsZero
        {
            get
            {
                for (int i = 0; i < _data.Length; i++)
                    if (BitOperations.PopCount(_data[i]) != 0)
                        return false;
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
        /// Constructs the bit vector with a certain capacity.
        /// </summary>
        public NetBitVector(int bitsCapacity)
        {
            if (bitsCapacity < 0)
                throw new ArgumentOutOfRangeException(nameof(bitsCapacity));

            Capacity = bitsCapacity;
            _data = new uint[(Capacity + BitsPerElement - 1) / BitsPerElement];
        }

        [CLSCompliant(false)]
        public ReadOnlyMemory<uint> GetBuffer()
        {
            return _data.AsMemory();
        }

        /// <summary>
        /// Shift all bits one step down, cycling the first bit to the top.
        /// </summary>
        public void RotateDown()
        {
            // TODO: check if it can be optimized with BitOperations

            int lenMinusOne = _data.Length - 1;

            uint firstBit = _data[0] & 1;
            for (int i = 0; i < lenMinusOne; i++)
                _data[i] = ((_data[i] >> 1) & ~(1 << 31)) | _data[i + 1] << 31;

            int lastIndex = Capacity - 1 - (BitsPerElement * lenMinusOne);

            // special handling of last int
            uint last = _data[lenMinusOne];
            last >>= 1;
            last |= firstBit << lastIndex;

            _data[lenMinusOne] = last;
        }

        /// <summary>
        /// Gets the first (lowest) bit with a given value.
        /// </summary>
        public int IndexOf(bool value)
        {
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
            LidgrenException.Assert(bitIndex >= 0 && bitIndex < Capacity);

            return (_data[bitIndex / BitsPerElement] & (1 << (bitIndex % BitsPerElement))) != 0;
        }

        /// <summary>
        /// Sets or clears the bit at the specified index.
        /// </summary>
        public void Set(int bitIndex, bool value)
        {
            LidgrenException.Assert(bitIndex >= 0 && bitIndex < Capacity);

            int index = bitIndex / BitsPerElement;
            uint mask = (uint)(1 << (bitIndex % BitsPerElement));
            if (value)
            {
                //if ((_data[index] & (1 << (bitIndex % BitsPerData))) == 0)
                //    PopCount++;
                _data[index] |= mask;
            }
            else
            {
                //if ((_data[index] & (1 << (bitIndex % BitsPerData))) != 0)
                //    PopCount--;
                _data[index] &= ~mask;
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
                //PopCount = Capacity;
            }
            else
            {
                _data.AsSpan().Clear();
                //PopCount = 0;
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
        /// Returns a string that represents this bit vector.
        /// </summary>
        public override string ToString()
        {
            var bdr = new StringBuilder(Capacity + 2);
            bdr.Append('[');
            for (int i = 0; i < Capacity; i++)
                bdr.Append(Get(Capacity - i - 1) ? '1' : '0');
            bdr.Append(']');
            return bdr.ToString();
        }
    }
}
