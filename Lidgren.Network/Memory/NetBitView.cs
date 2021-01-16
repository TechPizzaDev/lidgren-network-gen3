using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Lidgren.Network
{
    /// <summary>
    /// Mutable bit view wrapped around a value of <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The type used as bit storage.</typeparam>
    public struct NetBitView<T> : IEquatable<T>, IComparable<T>, IEquatable<NetBitView<T>>, IComparable<NetBitView<T>>
        where T : unmanaged
    {
        public T Value;

        public int ByteCount => Unsafe.SizeOf<T>();

        public int Length => ByteCount * 8;

        public int PopCount
        {
            get
            {
                ReadOnlySpan<T> valueSpan = MemoryMarshal.CreateReadOnlySpan(ref Value, 1);
                ReadOnlySpan<byte> byteSpan = MemoryMarshal.AsBytes(valueSpan);
                int sum = 0;

                if (ByteCount >= sizeof(uint))
                {
                    ReadOnlySpan<uint> intSpan = MemoryMarshal.Cast<byte, uint>(byteSpan);
                    for (int i = 0; i < intSpan.Length; i++)
                        sum += BitOperations.PopCount(intSpan[i]);

                    byteSpan = byteSpan[(intSpan.Length * Unsafe.SizeOf<uint>())..];
                }

                for (int i = 0; i < byteSpan.Length; i++)
                    sum += BitOperations.PopCount(byteSpan[i]);

                return sum;
            }
        }

        public NetBitView(T value)
        {
            Value = value;
        }

        public NetBitView<T> Fill(byte value)
        {
            NetBitView<T> t = this;
            Span<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref t, 1));
            bytes.Fill(value);
            return t;
        }

        public int CompareTo(T other)
        {
            ReadOnlySpan<T> valueSpan = MemoryMarshal.CreateReadOnlySpan(ref Value, 1);
            ReadOnlySpan<T> otherSpan = MemoryMarshal.CreateReadOnlySpan(ref other, 1);
            ReadOnlySpan<byte> valueBytes = MemoryMarshal.AsBytes(valueSpan);
            ReadOnlySpan<byte> otherBytes = MemoryMarshal.AsBytes(otherSpan);
            return valueBytes.SequenceCompareTo(otherBytes);
        }

        public int CompareTo(NetBitView<T> other)
        {
            return CompareTo(other.Value);
        }

        public bool Equals(T other)
        {
            ReadOnlySpan<T> valueSpan = MemoryMarshal.CreateReadOnlySpan(ref Value, 1);
            ReadOnlySpan<T> otherSpan = MemoryMarshal.CreateReadOnlySpan(ref other, 1);
            ReadOnlySpan<byte> valueBytes = MemoryMarshal.AsBytes(valueSpan);
            ReadOnlySpan<byte> otherBytes = MemoryMarshal.AsBytes(otherSpan);
            return valueBytes.SequenceEqual(otherBytes);
        }

        public bool Equals(NetBitView<T> other)
        {
            return Equals(other.Value);
        }

        public override int GetHashCode()
        {
            ReadOnlySpan<T> span = MemoryMarshal.CreateReadOnlySpan(ref Value, 1);
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(span);
            return FastHash32(bytes);
        }

        public static int FastHash32(ReadOnlySpan<byte> bytes)
        {
            int h = 17;
            int i = 0;
            for (; i + 3 < bytes.Length; i += 4)
            {
                h = 31 * 31 * 31 * 31 * h +
                    31 * 31 * 31 * bytes[i] +
                    31 * 31 * bytes[i + 1] +
                    31 * bytes[i + 2] +
                    bytes[i + 3];
            }
            for (; i < bytes.Length; i++)
            {
                h = 31 * h + bytes[i];
            }
            return h;
        }

        public override bool Equals(object? obj)
        {
            return (obj is NetBitView<T> other && Equals(other))
                || (obj is T otherValue && Equals(otherValue));
        }

        #region Operators

        public static bool operator ==(NetBitView<T> left, NetBitView<T> right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(NetBitView<T> left, NetBitView<T> right)
        {
            return !(left == right);
        }

        public static bool operator <(NetBitView<T> left, NetBitView<T> right)
        {
            return left.CompareTo(right) < 0;
        }

        public static bool operator <=(NetBitView<T> left, NetBitView<T> right)
        {
            return left.CompareTo(right) <= 0;
        }

        public static bool operator >(NetBitView<T> left, NetBitView<T> right)
        {
            return left.CompareTo(right) > 0;
        }

        public static bool operator >=(NetBitView<T> left, NetBitView<T> right)
        {
            return left.CompareTo(right) >= 0;
        }

        public static bool operator ==(NetBitView<T> left, T right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(NetBitView<T> left, T right)
        {
            return !(left == right);
        }

        public static bool operator <(NetBitView<T> left, T right)
        {
            return left.CompareTo(right) < 0;
        }

        public static bool operator <=(NetBitView<T> left, T right)
        {
            return left.CompareTo(right) <= 0;
        }

        public static bool operator >(NetBitView<T> left, T right)
        {
            return left.CompareTo(right) > 0;
        }

        public static bool operator >=(NetBitView<T> left, T right)
        {
            return left.CompareTo(right) >= 0;
        }

        public static bool operator ==(T left, NetBitView<T> right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(T left, NetBitView<T> right)
        {
            return !(left == right);
        }

        public static bool operator <(T left, NetBitView<T> right)
        {
            return right.CompareTo(left) > 0;
        }

        public static bool operator <=(T left, NetBitView<T> right)
        {
            return right.CompareTo(left) >= 0;
        }

        public static bool operator >(T left, NetBitView<T> right)
        {
            return right.CompareTo(left) < 0;
        }

        public static bool operator >=(T left, NetBitView<T> right)
        {
            return right.CompareTo(left) <= 0;
        }

        #endregion

        public static implicit operator T(NetBitView<T> view) => view.Value;

        public static implicit operator NetBitView<T>(T value) => new NetBitView<T>(value);
    }
}
