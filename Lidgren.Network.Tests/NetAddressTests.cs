using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Lidgren.Network.Tests;

public class NetAddressTests
{
    [Fact]
    public void WriteAsIPv6To()
    {
        IPEndPoint endPoint = new(IPAddress.Any, 1);
        IPEndPoint endPointV6 = NetUtility.MapToIPv6(endPoint);

        NetAddress buf = new(AddressFamily.InterNetworkV6);
        new NetAddress(endPoint).WriteAsIPv6To(buf);
        Assert.Equal(buf, new NetAddress(endPointV6));
    }
}
