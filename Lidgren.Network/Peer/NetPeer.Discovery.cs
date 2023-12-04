using System;
using System.Net;

namespace Lidgren.Network
{
    public partial class NetPeer
    {
        /// <summary>
        /// Emit a discovery signal to all hosts on your subnet.
        /// </summary>
        public void DiscoverLocalPeers(int serverPort)
        {
            NetOutgoingMessage om = CreateMessage();
            om._messageType = NetMessageType.Discovery;
            UnsentUnconnectedMessages.Enqueue((new NetAddress(IPAddress.Broadcast, serverPort), om));
        }

        /// <summary>
        /// Emit a discovery signal to a single known host.
        /// </summary>
        public bool DiscoverKnownPeer(ReadOnlySpan<char> host, int serverPort)
        {
            var address = NetUtility.Resolve(host);
            if (address == null)
                return false;

            DiscoverKnownPeer(new IPEndPoint(address, serverPort));
            return true;
        }

        /// <summary>
        /// Emit a discovery signal to a single known host.
        /// </summary>
        public void DiscoverKnownPeer(IPEndPoint endPoint)
        {
            ArgumentNullException.ThrowIfNull(endPoint);

            NetOutgoingMessage om = CreateMessage();
            om._messageType = NetMessageType.Discovery;
            UnsentUnconnectedMessages.Enqueue((new NetAddress(endPoint), om));
        }

        /// <summary>
        /// Send a discovery response message.
        /// </summary>
        public void SendDiscoveryResponse(IPEndPoint recipient, NetOutgoingMessage? message = null)
        {
            ArgumentNullException.ThrowIfNull(recipient);

            if (message == null)
                message = CreateMessage();
            else
                message.AssertNotSent(nameof(message));

            if (message.ByteLength >= Configuration.MaximumTransmissionUnit)
                throw new LidgrenException(
                    "Cannot send discovery message larger than MTU (currently " +
                    Configuration.MaximumTransmissionUnit + " bytes).");

            message._messageType = NetMessageType.DiscoveryResponse;
            UnsentUnconnectedMessages.Enqueue((new NetAddress(recipient), message));
        }
    }
}
