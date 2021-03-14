using System;
using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using System.Globalization;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Diagnostics.CodeAnalysis;

namespace Lidgren.Network
{
    // TODO: replace IAsyncResult with Task<>

    /// <summary>
    /// Utility methods
    /// </summary>
    public static partial class NetUtility
    {
        /// <summary>
        /// Resolve endpoint callback
        /// </summary>
        public delegate void ResolveEndPointCallback(IPEndPoint? endPoint);

        /// <summary>
        /// Resolve address callback
        /// </summary>
        public delegate void ResolveAddressCallback(IPAddress? address);

        private static IPAddress? _cachedBroadcastAddress;

        [CLSCompliant(false)]
        public static ulong GetPlatformSeed(int seedInc)
        {
            ulong seed = (ulong)Stopwatch.GetTimestamp();
            return seed ^ ((ulong)Environment.WorkingSet + (ulong)seedInc);
        }

        public static NetworkInterface? GetNetworkInterface()
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces();
            if (nics == null || nics.Length < 1)
                return null;

            NetworkInterface? best = null;
            foreach (NetworkInterface adapter in nics)
            {
                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Unknown)
                    continue;

                if (!adapter.Supports(NetworkInterfaceComponent.IPv4) &&
                    !adapter.Supports(NetworkInterfaceComponent.IPv6))
                    continue;

                if (best == null)
                    best = adapter;

                if (adapter.OperationalStatus != OperationalStatus.Up)
                    continue;

                IPInterfaceProperties properties = adapter.GetIPProperties();
                foreach (UnicastIPAddressInformation unicastAddress in properties.UnicastAddresses)
                {
                    if (unicastAddress == null || unicastAddress.Address == null)
                        continue;

                    if (unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork ||
                        unicastAddress.Address.AddressFamily == AddressFamily.InterNetworkV6)
                        return adapter;
                }
            }
            return best;
        }

        /// <summary>
        /// If available, returns the physical (MAC) address for the first usable network interface.
        /// </summary>
        public static PhysicalAddress? GetPhysicalAddress()
        {
            var ni = GetNetworkInterface();
            if (ni == null)
                return null;
            return ni.GetPhysicalAddress();
        }

        public static IPAddress? RetrieveBroadcastAddress()
        {
            var ni = GetNetworkInterface();
            if (ni == null)
                return null;

            Span<byte> addressTmp = stackalloc byte[16];
            Span<byte> subnetMaskTmp = stackalloc byte[16];

            var properties = ni.GetIPProperties();
            foreach (UnicastIPAddressInformation unicastAddress in properties.UnicastAddresses)
            {
                if (unicastAddress != null &&
                    unicastAddress.Address != null &&
                    unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    if (!unicastAddress.Address.TryWriteBytes(addressTmp, out int addressLength))
                        throw new NotImplementedException("Unknown address length.");

                    if (!unicastAddress.IPv4Mask.TryWriteBytes(subnetMaskTmp, out int subnetMaskLength))
                        throw new NotImplementedException("Unknown subnet mask length.");

                    // subnet mask should realistically always have length 4
                    if (addressLength != subnetMaskLength)
                        throw new Exception("Length of IP address and subnet mask do not match.");

                    Span<byte> broadcast = stackalloc byte[addressLength];
                    for (int i = 0; i < broadcast.Length; i++)
                        broadcast[i] = (byte)(addressTmp[i] | (subnetMaskTmp[i] ^ 255));

                    return new IPAddress(broadcast);
                }
            }
            return IPAddress.Broadcast;
        }

        public static IPAddress? GetBroadcastAddress()
        {
            if (_cachedBroadcastAddress == null)
                _cachedBroadcastAddress = RetrieveBroadcastAddress();
            return _cachedBroadcastAddress;
        }

        /// <summary>
        /// Gets local IPv4 address and subnet mask.
        /// </summary>
        public static bool GetLocalAddress(
            [MaybeNullWhen(false)] out IPAddress address,
            [MaybeNullWhen(false)] out IPAddress mask)
        {
            var ni = GetNetworkInterface();
            if (ni != null)
            {
                IPInterfaceProperties properties = ni.GetIPProperties();
                foreach (UnicastIPAddressInformation unicastAddress in properties.UnicastAddresses)
                {
                    if (unicastAddress != null &&
                        unicastAddress.Address != null &&
                        unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        address = unicastAddress.Address;
                        mask = unicastAddress.IPv4Mask;
                        return true;
                    }
                }
            }

            address = null;
            mask = null;
            return false;
        }

        #region Resolve

        /// <summary>
        /// Get IPv4 endpoint from notation (xxx.xxx.xxx.xxx) or hostname and port number asynchronously.
        /// </summary>
        public static void ResolveAsync(ReadOnlySpan<char> host, int port, ResolveEndPointCallback callback)
        {
            ResolveAsync(host, (resolved) =>
            {
                if (resolved == null)
                    callback(null);
                else
                    callback(new IPEndPoint(resolved, port));
            });
        }

        /// <summary>
        /// Get IPv4 endpoint from notation (xxx.xxx.xxx.xxx) or hostname and port number
        /// </summary>
        public static IPEndPoint? Resolve(ReadOnlySpan<char> host, int port)
        {
            var resolved = Resolve(host);
            return resolved == null ? null : new IPEndPoint(resolved, port);
        }

        /// <summary>
        /// Get IPv4 address from notation (xxx.xxx.xxx.xxx) or hostname asynchronously.
        /// </summary>
        public static void ResolveAsync(ReadOnlySpan<char> host, ResolveAddressCallback callback)
        {
            if (host.IsEmpty)
                throw new ArgumentException("Supplied string must not be empty.", nameof(host));

            host = host.Trim();

            if (IPAddress.TryParse(host, out IPAddress? ipAddress))
            {
                if (ipAddress.AddressFamily == AddressFamily.InterNetwork ||
                    ipAddress.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    callback?.Invoke(ipAddress);
                    return;
                }

                throw new ArgumentException(
                    "This method will not currently resolve other than IPv4 and IPv6 addresses.",
                    nameof(host));
            }

            // ok must be a host name
            IPHostEntry entry;
            try
            {
                Dns.BeginGetHostEntry(host.ToString(), (result) =>
                {
                    try
                    {
                        entry = Dns.EndGetHostEntry(result);
                    }
                    catch (SocketException ex)
                    {
                        if (ex.SocketErrorCode == SocketError.HostNotFound)
                        {
                            //LogWrite(string.Format(CultureInfo.InvariantCulture, "Failed to resolve host '{0}'.", host.ToString()));
                            callback?.Invoke(null);
                            return;
                        }
                        else
                        {
                            throw;
                        }
                    }

                    if (entry == null)
                    {
                        callback?.Invoke(null);
                        return;
                    }

                    // check each entry for a valid IP address
                    foreach (var ipCurrent in entry.AddressList)
                    {
                        if (ipCurrent.AddressFamily == AddressFamily.InterNetwork ||
                            ipCurrent.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            callback?.Invoke(ipCurrent);
                            return;
                        }
                    }

                    callback?.Invoke(null);

                }, null);
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.HostNotFound)
                {
                    //LogWrite(string.Format(CultureInfo.InvariantCulture, "Failed to resolve host '{0}'.", host.ToString()));
                    callback?.Invoke(null);
                }
                else
                {
                    throw;
                }
            }
        }

        /// <summary>
        /// Get IPv4 address from notation (xxx.xxx.xxx.xxx) or hostname.
        /// </summary>
        public static IPAddress? Resolve(ReadOnlySpan<char> host)
        {
            if (host.IsEmpty)
                throw new ArgumentException("Supplied string must not be empty.", nameof(host));

            host = host.Trim();

            if (IPAddress.TryParse(host, out IPAddress? ipAddress))
            {
                if (ipAddress.AddressFamily == AddressFamily.InterNetwork ||
                    ipAddress.AddressFamily == AddressFamily.InterNetworkV6)
                    return ipAddress;

                throw new ArgumentException(
                    "This method will not currently resolve other than IPv4 or IPv6 addresses.",
                    nameof(host));
            }

            // ok must be a host name
            try
            {
                var addresses = Dns.GetHostAddresses(host.ToString());
                if (addresses == null)
                    return null;

                foreach (var address in addresses)
                {
                    if (address.AddressFamily == AddressFamily.InterNetwork ||
                        address.AddressFamily == AddressFamily.InterNetworkV6)
                        return address;
                }
                return null;
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.HostNotFound)
                {
                    //LogWrite(string.Format(CultureInfo.InvariantCulture, "Failed to resolve host '{0}'.", host.ToString()));
                    return null;
                }
                else
                {
                    throw;
                }
            }
        }

        #endregion

        // TODO: replace hex methods with fast .NET ones in future

        public static int GetHexCharCount(int byteCount)
        {   
            return byteCount * 2;
        }

        public static void ToHexString(ReadOnlySpan<byte> source, Span<char> destination)
        {
            destination = destination.Slice(0, Math.Min(destination.Length, GetHexCharCount(source.Length)));
            source = source.Slice(0, Math.Min(source.Length, destination.Length / 2));

            if (source.IsEmpty || destination.IsEmpty)
                return;

            for (int i = 0; i < destination.Length / 2; i++)
            {
                int value = source[i];

                int a = value >> 4;
                destination[i * 2 + 0] = (char)(a > 9 ? a + 0x37 : a + 0x30);

                int b = value & 0xF;
                destination[i * 2 + 1] = (char)(b > 9 ? b + 0x37 : b + 0x30);
            }
        }

        /// <summary>
        /// Create a hex string from an array of bytes
        /// </summary>
        public static unsafe string ToHexString(ReadOnlySpan<byte> source)
        {
            if (source.IsEmpty)
                return string.Empty;

            // TODO: pass Span directly, not now as ref-structs are not supported by generics

            fixed (byte* srcPtr = source)
            {
                return string.Create(source.Length * 2, (IntPtr)srcPtr, (dst, srcPtr) =>
                {
                    var src = new ReadOnlySpan<byte>((byte*)srcPtr, dst.Length / 2);
                    ToHexString(src, dst);
                });
            }
        }

        /// <summary>
        /// Create a hex string from an <see cref="long"/> value.
        /// </summary>
        public static string ToHexString(long value)
        {
            Span<byte> tmp = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(tmp, value);
            return ToHexString(tmp);
        }

        public static int GetHexByteCount(int textLength)
        {
            return (textLength + 1) / 2;
        }

        /// <summary>
        /// Convert a hexadecimal text to into bytes.
        /// </summary>
        public static void FromHexString(ReadOnlySpan<char> source, Span<byte> destination)
        {
            destination = destination.Slice(0, Math.Min(destination.Length, GetHexByteCount(source.Length)));
            source = source.Slice(0, Math.Min(source.Length / 2 * 2, destination.Length / 2));

            if (source.IsEmpty || destination.IsEmpty)
                return;

            int i = 0;
            for (; i < source.Length; i += 2)
                destination[i / 2] = byte.Parse(source[i..(i + 1)], NumberStyles.HexNumber);

            if (source.Length - i > 0)
                destination[i] = byte.Parse(stackalloc char[] { source[i] }, NumberStyles.HexNumber);
        }

        public static byte[] FromHexString(ReadOnlySpan<char> text)
        {
            if (text.IsEmpty)
                return Array.Empty<byte>();

            var array = new byte[GetHexByteCount(text.Length)];
            FromHexString(text, array);
            return array;
        }

        /// <summary>
        /// Returns true if the supplied address is on the same subnet as this host.
        /// </summary>
        public static bool IsLocal(IPAddress remote)
        {
            if (remote == null)
                return false;

            static void GetBytes(IPAddress address, Span<byte> destination)
            {
                if (address.AddressFamily != AddressFamily.InterNetwork)
                    throw new ArgumentException("Unsupported address type. Only IPv4 is supported.");

                if (!address.TryWriteBytes(destination, out int remoteByteCount) ||
                    remoteByteCount != 4)
                    throw new ArgumentException("Failed to get address bytes.");
            }

            Span<byte> remoteBytes = stackalloc byte[4];
            GetBytes(remote, remoteBytes);

            if (!GetLocalAddress(out IPAddress? local, out IPAddress? mask))
                return false;

            Span<byte> maskBytes = stackalloc byte[4];
            Span<byte> localBytes = stackalloc byte[4];
            GetBytes(mask, maskBytes);
            GetBytes(local, localBytes);

            uint maskBits = BitConverter.ToUInt32(maskBytes);
            uint remoteBits = BitConverter.ToUInt32(remoteBytes);
            uint localBits = BitConverter.ToUInt32(localBytes);

            // compare network portions
            return (remoteBits & maskBits) == (localBits & maskBits);
        }

        /// <summary>
        /// Returns true if the supplied endpoint is on the same subnet as this host.
        /// </summary>
        public static bool IsLocal(IPEndPoint endPoint)
        {
            if (endPoint == null)
                return false;

            return IsLocal(endPoint.Address);
        }

        /// <summary>
        /// Converts a number of bytes to a shorter, more readable string representation
        /// </summary>
        public static string ToHumanReadable(long bytes)
        {
            if (bytes < 4000) // 1-4 kb is printed in bytes
                return bytes + " bytes";
            if (bytes < 1000 * 1000) // 4-999 kb is printed in kb
                return Math.Round(bytes / 1000.0, 2) + " kilobytes";
            return Math.Round(bytes / (1000.0 * 1000.0), 2) + " megabytes"; // else megabytes
        }

        internal static int RelativeSequenceNumber(int number, int expected)
        {
            return
                (number - expected + NetConstants.SequenceNumbers + NetConstants.SequenceNumbers / 2)
                % NetConstants.SequenceNumbers - NetConstants.SequenceNumbers / 2;

            // old impl:
            //int value = ((nr + NetConstants.SequenceNumbers) - expected) % NetConstants.SequenceNumbers;
            //if (value > (NetConstants.SequenceNumbers / 2))
            //    value -= NetConstants.SequenceNumbers;
            //return value;
        }

        /// <summary>
        /// Gets the window size used internally in the library for a certain delivery method
        /// </summary>
        public static int GetWindowSize(NetDeliveryMethod method)
        {
            switch (method)
            {
                case NetDeliveryMethod.Unknown:
                    return 0;

                case NetDeliveryMethod.Unreliable:
                case NetDeliveryMethod.UnreliableSequenced:
                    return NetConstants.UnreliableWindowSize;

                case NetDeliveryMethod.ReliableOrdered:
                    return NetConstants.ReliableOrderedWindowSize;

                case NetDeliveryMethod.ReliableSequenced:
                case NetDeliveryMethod.ReliableUnordered:
                default:
                    return NetConstants.DefaultWindowSize;
            }
        }

        internal static NetDeliveryMethod GetDeliveryMethod(NetMessageType mtp)
        {
            if (mtp >= NetMessageType.UserReliableOrdered1)
                return NetDeliveryMethod.ReliableOrdered;
            else if (mtp >= NetMessageType.UserReliableSequenced1)
                return NetDeliveryMethod.ReliableSequenced;
            else if (mtp >= NetMessageType.UserReliableUnordered)
                return NetDeliveryMethod.ReliableUnordered;
            else if (mtp >= NetMessageType.UserSequenced1)
                return NetDeliveryMethod.UnreliableSequenced;
            return NetDeliveryMethod.Unreliable;
        }

        /// <summary>
        /// Copies from <paramref name="src"/> to <paramref name="dst"/>, mapping to an IPv6 address.
        /// </summary>
        /// <param name="src">Source.</param>
        /// <param name="dst">Destination.</param>
        internal static void MapToIPv6(IPEndPoint src, IPEndPoint dst)
        {
            dst.Port = src.Port;
            if (src.AddressFamily == AddressFamily.InterNetwork)
                dst.Address = src.Address.MapToIPv6();
            else
                dst.Address = src.Address;
        }

        /// <summary>
        /// Creates a new endpoint mapped to an IPv6 address.
        /// </summary>
        internal static IPEndPoint MapToIPv6(IPEndPoint endPoint)
        {
            if (endPoint.AddressFamily == AddressFamily.InterNetwork)
                return new IPEndPoint(endPoint.Address.MapToIPv6(), endPoint.Port);
            return endPoint;
        }
    }
}
