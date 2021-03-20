using System;
using System.Net;

namespace Lidgren.Network
{
    public partial class NetPeer
    {
        /// <summary>
        /// Create a connection to a remote endpoint.
        /// </summary>
        public virtual NetConnection Connect(IPEndPoint remoteEndPoint, NetOutgoingMessage? hailMessage)
        {
            if (remoteEndPoint == null)
                throw new ArgumentNullException(nameof(remoteEndPoint));

            if (Configuration.DualStack)
                remoteEndPoint = NetUtility.MapToIPv6(remoteEndPoint);

            if (Status == NetPeerStatus.NotRunning)
                throw new LidgrenException("Must call Start() first.");

            if (ConnectionLookup.ContainsKey(remoteEndPoint))
                throw new LidgrenException("Already connected to that endpoint!");

            if (Handshakes.TryGetValue(remoteEndPoint, out NetConnection? hs))
            {
                // already trying to connect to that endpoint; make another try
                switch (hs.Status)
                {
                    case NetConnectionStatus.InitiatedConnect:
                        // send another connect
                        hs._connectRequested = true;
                        break;

                    case NetConnectionStatus.RespondedConnect:
                        // send another response
                        hs.SendConnectResponse(NetTime.Now, false);
                        break;

                    default:
                        // weird
                        LogWarning(NetLogMessage.FromValues(NetLogCode.UnexpectedHandshakeStatus, value: (int)hs.Status));
                        break;
                }
                return hs;
            }

            var conn = new NetConnection(this, remoteEndPoint);
            conn.Status = NetConnectionStatus.InitiatedConnect;
            conn.LocalHailMessage = hailMessage;

            // handle on network thread
            conn._connectRequested = true;
            conn._connectionInitiator = true;

            Handshakes.TryAdd(remoteEndPoint, conn);
            return conn;
        }

        /// <summary>
        /// Create a connection to a remote endpoint.
        /// </summary>
        public NetConnection Connect(ReadOnlySpan<char> host, int port)
        {
            return Connect(new IPEndPoint(NetUtility.Resolve(host), port), null);
        }

        /// <summary>
        /// Create a connection to a remote endpoint.
        /// </summary>
        public NetConnection Connect(ReadOnlySpan<char> host, int port, NetOutgoingMessage hailMessage)
        {
            return Connect(new IPEndPoint(NetUtility.Resolve(host), port), hailMessage);
        }

        /// <summary>
        /// Create a connection to a remote endpoint
        /// </summary>
        public NetConnection Connect(IPEndPoint remoteEndPoint)
        {
            return Connect(remoteEndPoint, null);
        }
    }
}
