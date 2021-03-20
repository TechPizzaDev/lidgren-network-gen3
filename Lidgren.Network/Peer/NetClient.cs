using System;
using System.Net;

namespace Lidgren.Network
{
    /// <summary>
    /// Specialized version of a peer used for a "client" connection.
    /// It does not accept any incoming connections and maintains a <see cref="NetConnection"/> to a server.
    /// </summary>
    public class NetClient : NetPeer
    {
        /// <summary>
        /// Gets the connection to the server.
        /// </summary>
        public NetConnection? ServerConnection
        {
            get
            {
                lock (Connections)
                {
                    if (Connections.Count > 0)
                        return Connections[0];
                    return null;
                }
            }
        }

        /// <summary>
        /// Gets the connection status of the server connection 
        /// (or <see cref="NetConnectionStatus.Disconnected"/> if no connection).
        /// </summary>
        public NetConnectionStatus ConnectionStatus => ServerConnection?.Status ?? NetConnectionStatus.Disconnected;

        /// <summary>
        /// Constructs the client with a given configuration.
        /// </summary>
        public NetClient(NetPeerConfiguration config) : base(config)
        {
            config.AcceptIncomingConnections = false;
        }

        /// <summary>
        /// Connect to a remote server
        /// </summary>
        /// <param name="remoteEndPoint">The remote endpoint to connect to</param>
        /// <param name="hailMessage">The hail message to pass</param>
        /// <returns>server connection, or null if already connected</returns>
        public override NetConnection Connect(IPEndPoint remoteEndPoint, NetOutgoingMessage? hailMessage)
        {
            lock (Connections)
            {
                if (Connections.Count > 0)
                    throw new InvalidOperationException("Connect attempt failed; Already connected");
            }

            if (!Handshakes.IsEmpty)
                throw new InvalidOperationException("Connect attempt failed; Handshake already in progress");

            return base.Connect(remoteEndPoint, hailMessage);
        }

        /// <summary>
        /// Disconnect from server.
        /// </summary>
        /// <param name="reason">Message to send with the disconnect.</param>
        public void Disconnect(NetOutgoingMessage? reason)
        {
            var connection = ServerConnection;
            if (connection != null)
            {
                connection.Disconnect(reason);
            }
            else
            {
                if (Handshakes.IsEmpty)
                {
                    LogDebug(new NetLogMessage(NetLogCode.NotConnected));
                    return;
                }

                LogVerbose(new NetLogMessage(NetLogCode.AbortingConnectionAttempt));
                foreach (var hs in Handshakes.Values)
                {
                    hs.Disconnect(reason);
                }
            }
        }

        /// <summary>
        /// Sends message to server
        /// </summary>
        public NetSendResult SendMessage(NetOutgoingMessage msg, NetDeliveryMethod method)
        {
            var serverConnection = ServerConnection;
            if (serverConnection == null)
            {
                LogWarning(new NetLogMessage(NetLogCode.NotConnected));
                return NetSendResult.FailedNotConnected;
            }
            return serverConnection.SendMessage(msg, method, 0);
        }

        /// <summary>
        /// Sends message to server
        /// </summary>
        public NetSendResult SendMessage(NetOutgoingMessage msg, NetDeliveryMethod method, int sequenceChannel)
        {
            var serverConnection = ServerConnection;
            if (serverConnection == null)
            {
                LogWarning(new NetLogMessage(NetLogCode.NotConnected));
                return NetSendResult.FailedNotConnected;
            }
            return serverConnection.SendMessage(msg, method, sequenceChannel);
        }

        /// <summary>
        /// Returns a string that represents this object
        /// </summary>
        public override string ToString()
        {
            return "{NetClient: " + ServerConnection + "}";
        }
    }
}
