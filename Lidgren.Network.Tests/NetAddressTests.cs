using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Lidgren.Network.Tests;

public class NetAddressTests
{
    private static IPEndPoint ScopedIPv6 { get; } = new(
        new IPAddress(
            new byte[] { 0xfe, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            scopeid: 0xdeadbeef),
        port: 1);

    [Fact]
    public void WriteAsIPv6To()
    {
        IPEndPoint endPoint = new(IPAddress.Loopback, 1);
        IPEndPoint endPointV6 = NetUtility.MapToIPv6(endPoint);

        NetAddress buf = new(AddressFamily.InterNetworkV6);
        new NetAddress(endPoint).WriteAsIPv6To(buf);
        NetAddress cmp = new(endPointV6);
        Assert.Equal(buf, cmp);

        Equal(endPointV6, cmp.GetIPEndPoint());
    }

    [Fact]
    public void WriteAsIPv6To_PreserveScope()
    {
        NetAddress cmp = new(ScopedIPv6);
        NetAddress buf = new(AddressFamily.InterNetworkV6);
        cmp.WriteAsIPv6To(buf);
        Assert.Equal(buf, cmp);

        Equal(ScopedIPv6, cmp.GetIPEndPoint());
    }

    [Fact]
    public void WriteTo_PreserveScope()
    {
        NetAddress buf = new(AddressFamily.InterNetworkV6);
        NetAddress cmp = new(ScopedIPv6);
        cmp.WriteTo(buf);
        Assert.Equal(buf, cmp);

        Equal(ScopedIPv6, cmp.GetIPEndPoint());
    }

    private static void Equal(IPEndPoint expected, IPEndPoint value)
    {
        Assert.Equal(expected.Address.ScopeId, value.Address.ScopeId);
        Assert.Equal(expected, value);
    }
}
