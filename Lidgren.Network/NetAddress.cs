using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Lidgren.Network
{
    public readonly struct NetAddress : IEquatable<NetAddress>
    {
        private const int FamilySize = 2;
        private const int PortOffset = 2;
        private const int AddressOffset = PortOffset + sizeof(ushort);
        private const int IPv4AddressSize = 4;
        private const int IPv6AddressSize = 16;

        private static ushort IPv4FamilyValue { get; } = GetAddressFamilyValue(AddressFamily.InterNetwork);
        private static ushort IPv6FamilyValue { get; } = GetAddressFamilyValue(AddressFamily.InterNetworkV6);

        private static ReadOnlySpan<byte> IPv6LabelTemplate => new byte[16] 
        {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff
        };

        private static readonly IPEndPoint _epTemplate = new(IPAddress.None, 0);

        private readonly SocketAddress? _address;

#if DEBUG
        private readonly bool _isOwned;
#endif

        internal NetAddress(SocketAddress? address, bool owned) : this(address)
        {
#if DEBUG
            _isOwned = owned;
#endif
        }

        public NetAddress(SocketAddress? address)
        {
            _address = address;
        }

        public NetAddress(AddressFamily family) : this(CreateEmptyAddress(family), true)
        {
        }

        public NetAddress(IPEndPoint endPoint) : this(endPoint.Serialize(), true)
        {
        }

        public NetAddress(IPAddress address, int port) : this(new IPEndPoint(address, port))
        {
        }

        internal bool IsOwned
#if DEBUG
            => _isOwned;
#else
            => true;
#endif

        public AddressFamily Family => IsEmpty ? AddressFamily.Unspecified : _address.Family;

        [MemberNotNullWhen(false, nameof(_address))]
        public bool IsEmpty => _address == null || _address.Size == 0;

        private Span<byte> GetAddressSpan(AddressFamily family)
        {
            if (_address != null)
            {
                Memory<byte> data = _address.Buffer.Slice(0, _address.Size);
                return data.Slice(AddressOffset, SocketAddress.GetMaximumAddressSize(family)).Span;
            }
            return default;
        }

        public bool AddressEquals(NetAddress other)
        {
            AddressFamily srcFamily = Family;
            AddressFamily otherFamily = other.Family;
            if (srcFamily == otherFamily)
            {
                Span<byte> srcSpan = GetAddressSpan(srcFamily);
                Span<byte> otherSpan = other.GetAddressSpan(otherFamily);
                return srcSpan.SequenceEqual(otherSpan);
            }
            return false;
        }

        public bool AddressEquals(IPAddress? other)
        {
            if (other != null && !IsEmpty)
            {
                AddressFamily srcFamily = _address.Family;
                if (srcFamily == other.AddressFamily)
                {
                    Span<byte> otherBytes = stackalloc byte[16];
                    if (other.TryWriteBytes(otherBytes, out int written))
                    {
                        Debug.Assert(written <= SocketAddress.GetMaximumAddressSize(srcFamily));
                        Span<byte> srcSpan = _address.Buffer.Slice(AddressOffset, written).Span;
                        Span<byte> otherSpan = otherBytes.Slice(0, written);
                        return srcSpan.SequenceEqual(otherSpan);
                    }
                }
            }
            return false;
        }

        public NetAddress Clone()
        {
            if (IsEmpty)
            {
                return default;
            }
            return new NetAddress(CloneSocketAddress(), true);
        }

        internal SocketAddress CloneSocketAddress()
        {
            Debug.Assert(_address != null);
            SocketAddress addr = new(_address.Family);
            _address.Buffer.Span.CopyTo(addr.Buffer.Span);
            return addr;
        }

        public void WriteTo(NetAddress destination)
        {
            Span<byte> srcSpan = GetSpan();
            if (destination._address == null)
            {
                if (srcSpan.IsEmpty)
                {
                    return;
                }
                ThrowDstTooSmall(nameof(destination));
            }
            destination._address.Size = srcSpan.Length;
            srcSpan.CopyTo(destination._address.Buffer.Span);
        }

        public bool Equals(NetAddress other)
        {
            return GetSpan().SequenceEqual(other.GetSpan());
        }

        public override bool Equals(object? obj)
        {
            return obj is NetAddress other && Equals(other);
        }

        internal Span<byte> GetSpan()
        {
            if (_address != null)
            {
                return _address.Buffer.Slice(0, _address.Size).Span;
            }
            return default;
        }

        public override int GetHashCode()
        {
            HashCode code = new();
            code.AddBytes(GetSpan());
            return code.ToHashCode();
        }

        public SocketAddress GetSocketAddress()
        {
            Debug.Assert(_address != null);
            return _address;
        }

        public IPEndPoint GetIPEndPoint()
        {
            return (IPEndPoint) _epTemplate.Create(GetSocketAddress());
        }

        public ushort GetPort()
        {
            if (_address != null)
            {
                return GetPort(_address.Buffer.Span);
            }
            return 0;
        }

        private static ushort GetPort(ReadOnlySpan<byte> buffer)
        {
            return BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(PortOffset, sizeof(ushort)));
        }

        /// <summary>
        /// </summary>
        /// <param name="destination"></param>
        /// <exception cref="NotSupportedException"></exception>
        public void WriteAsIPv6To(NetAddress destination)
        {
            if (IsEmpty)
            {
                if (destination._address != null)
                {
                    destination._address.Size = 0;
                }
                return;
            }
            if (destination._address == null)
            {
                ThrowDstTooSmall(nameof(destination));
            }

            int serializedSize = SocketAddress.GetMaximumAddressSize(AddressFamily.InterNetworkV6);
            destination._address.Size = serializedSize;
            Span<byte> dstSpan = destination._address.Buffer.Span;

            AddressFamily srcFamily = _address.Family;
            if (srcFamily == AddressFamily.InterNetworkV6)
            {
                WriteTo(destination);

                dstSpan.Slice(serializedSize).Clear();
            }
            else if (srcFamily == AddressFamily.InterNetwork)
            {
                ReadOnlySpan<byte> srcSpan = _address.Buffer.Span;
                ReadOnlySpan<byte> srcPort = srcSpan.Slice(PortOffset, sizeof(ushort));
                ReadOnlySpan<byte> srcAddress = srcSpan.Slice(AddressOffset, IPv4AddressSize);

                MemoryMarshal.Write(dstSpan, IPv6FamilyValue);
                srcPort.CopyTo(dstSpan.Slice(PortOffset, sizeof(ushort)));

                IPv6LabelTemplate.CopyTo(dstSpan.Slice(AddressOffset));

                Span<byte> dstAddress = dstSpan.Slice(AddressOffset + IPv6LabelTemplate.Length);
                srcAddress.CopyTo(dstAddress);

                dstAddress.Slice(srcAddress.Length).Clear();
            }
            else
            {
                throw new NotSupportedException();
            }
        }

        public override string ToString()
        {
            if (_address != null)
            {
                return _address.ToString();
            }
            return Family.ToString();
        }

        public static void WriteAddress(NetAddress destination, IPAddress address)
        {
            if (destination._address == null)
            {
                ThrowDstTooSmall(nameof(destination));
            }

            Span<byte> buffer = stackalloc byte[16];
            if (address.TryWriteBytes(buffer, out int written))
            {
                destination._address.Size = AddressOffset + written;
                buffer.Slice(0, written).CopyTo(destination._address.Buffer.Slice(AddressOffset).Span);
            }
        }

        public static void WritePort(NetAddress destination, ushort port)
        {
            Span<byte> dstSpan = destination.GetSpan();
            BinaryPrimitives.WriteUInt16BigEndian(dstSpan.Slice(PortOffset, sizeof(ushort)), port);
        }

        internal static SocketAddress CreateEmptyAddress(AddressFamily family)
        {
            SocketAddress addr = new(family);
            addr.Size = 0;
            return addr;
        }

        internal static ushort GetAddressFamilyValue(AddressFamily family)
        {
            SocketAddress addr = new(family);
            return BitConverter.ToUInt16(addr.Buffer.Slice(0, FamilySize).Span);
        }

        [DoesNotReturn]
        private static void ThrowDstTooSmall(string paramName)
        {
            throw new ArgumentOutOfRangeException(paramName, "Destination too small.");
        }
    }
}
