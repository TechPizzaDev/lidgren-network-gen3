using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Lidgren.Network
{
    public partial class NetPeer
    {
        private object InitMutex { get; } = new object();

        private Thread? _networkThread;
        private EndPoint _senderRemote;
        private uint _frameCounter;
        private TimeSpan _lastHeartbeat;
        private TimeSpan _lastSocketBind = TimeSpan.MinValue;
        private AutoResetEvent? _messageReceivedEvent;
        private List<(SynchronizationContext SyncContext, SendOrPostCallback Callback)>? _receiveCallbacks;
        internal NetIncomingMessage? _readHelperMessage;
        internal byte[] _sendBuffer = Array.Empty<byte>();
        internal byte[] _receiveBuffer = Array.Empty<byte>();

        private NetQueue<NetIncomingMessage> ReleasedIncomingMessages { get; } =
            new NetQueue<NetIncomingMessage>(4);

        internal NetQueue<(IPEndPoint EndPoint, NetOutgoingMessage Message)> UnsentUnconnectedMessages { get; } =
            new NetQueue<(IPEndPoint, NetOutgoingMessage)>(2);

        internal ConcurrentDictionary<IPEndPoint, NetConnection> Handshakes { get; } =
            new ConcurrentDictionary<IPEndPoint, NetConnection>();

        internal bool _executeFlushSendQueue;

        /// <summary>
        /// Gets the socket.
        /// </summary>
        public Socket? Socket { get; private set; }

        /// <summary>
        /// Call this to register a callback for when a new message arrives
        /// </summary>
        public void RegisterReceivedCallback(SendOrPostCallback callback, SynchronizationContext? syncContext = null)
        {
            if (syncContext == null)
                syncContext = SynchronizationContext.Current;

            if (syncContext == null)
                throw new LidgrenException("Need a SynchronizationContext to register callback on correct thread!");

            if (_receiveCallbacks == null)
                _receiveCallbacks = new List<(SynchronizationContext, SendOrPostCallback)>(1);

            _receiveCallbacks.Add((syncContext, callback));
        }

        /// <summary>
        /// Call this to unregister a callback, but remember to do it in the same synchronization context!
        /// </summary>
        public void UnregisterReceivedCallback(SendOrPostCallback callback)
        {
            if (_receiveCallbacks == null)
                return;

            // remove all callbacks regardless of sync context
            _receiveCallbacks.RemoveAll((x) => x.Callback.Equals(callback));
        }

        internal void ReleaseMessage(in NetMessageView message)
        {
            LidgrenException.Assert(message.MessageType != NetIncomingMessageType.Error);

            if (message.IsFragment)
            {
                HandleReleasedFragment(message);
                return;
            }

            ReleasedIncomingMessages.Enqueue(message.ToIncomingMessage(this));
            _messageReceivedEvent?.Set();

            if (_receiveCallbacks == null)
                return;

            foreach (var (SyncContext, Callback) in _receiveCallbacks)
            {
                try
                {
                    SyncContext.Post(Callback, this);
                }
                catch (Exception ex)
                {
                    LogWarning("Receive callback exception:" + ex);
                }
            }
        }

        internal void ReleaseMessage(NetIncomingMessage message)
        {
            message.BitPosition = 0;
            ReleaseMessage(message.View);
        }

        private Socket BindSocket(bool reuseAddress)
        {
            TimeSpan now = NetTime.Now;
            if (Socket != null && now - _lastSocketBind < TimeSpan.FromSeconds(1.0))
            {
                LogDebug("Suppressed socket rebind; last bound " + (now - _lastSocketBind) + " ago");
                return Socket; // only allow rebind once every second
            }
            _lastSocketBind = now;

            var mutex = new Mutex(false, "Global\\lidgrenSocketBind");
            try
            {
                mutex.WaitOne();

                if (Socket == null)
                {
                    Socket = new Socket(
                        Configuration.LocalAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                }

                if (reuseAddress)
                    Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                Socket.ReceiveBufferSize = Configuration.ReceiveBufferSize;
                Socket.SendBufferSize = Configuration.SendBufferSize;

                Socket.Blocking = false;
                Socket.EnableBroadcast = true;

                if (Configuration.DualStack)
                {
                    if (Configuration.LocalAddress.AddressFamily != AddressFamily.InterNetworkV6)
                    {
                        LogWarning(
                            "Configuration specifies dual stack but " +
                            "does not use IPv6 local address; dual stack will not work.");
                    }
                    else
                    {
                        Socket.DualMode = true;
                    }
                }

                var ep = new IPEndPoint(Configuration.LocalAddress, reuseAddress ? Port : Configuration.Port);
                Socket.Bind(ep);

                try
                {
                    const uint IOC_IN = 0x80000000;
                    const uint IOC_VENDOR = 0x18000000;
                    uint SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12;
                    Socket.IOControl((int)SIO_UDP_CONNRESET, new byte[] { Convert.ToByte(false) }, null);
                }
                catch
                {
                    // TODO: handle this with catching the error
                    // ignore; SIO_UDP_CONNRESET not supported on this platform
                }
            }
            finally
            {
                mutex.ReleaseMutex();
                mutex.Dispose();
            }

            IPEndPoint boundEp = (Socket.LocalEndPoint as IPEndPoint) ??
                throw new Exception("The socket has no bound endpoint.");

            LogDebug("Socket bound to " + boundEp + ": " + Socket.IsBound);
            Port = boundEp.Port;
            return Socket;
        }

        private void InitializeNetwork()
        {
            lock (InitMutex)
            {
                Configuration.Lock();

                if (Status == NetPeerStatus.Running)
                    return;

                if (Configuration._enableUPnP)
                    UPnP = new NetUPnP(this);

                ReleasedIncomingMessages.Clear();
                UnsentUnconnectedMessages.Clear();
                Handshakes.Clear();

                // bind to socket
                Socket socket = BindSocket(false);

                // TODO: recycle buffers
                _receiveBuffer = new byte[Configuration.ReceiveBufferSize];
                _sendBuffer = new byte[Configuration.SendBufferSize];

                _readHelperMessage = CreateIncomingMessage(NetIncomingMessageType.Error);
                _readHelperMessage.SetBuffer(_receiveBuffer, false);

                string? endPointString = socket.LocalEndPoint?.ToString();
                PhysicalAddress? physicalAddress = NetUtility.GetPhysicalAddress();
                byte[] idData;

                if (endPointString == null && physicalAddress == null)
                {
                    // This should realistically not happen as endPointString should not be null.
                    idData = Guid.NewGuid().ToByteArray();
                }
                else
                {
                    ReadOnlySpan<byte> epBytes = MemoryMarshal.AsBytes(endPointString.AsSpan());
                    byte[] macBytes = physicalAddress?.GetAddressBytes() ?? Array.Empty<byte>();

                    idData = new byte[epBytes.Length + macBytes.Length];
                    epBytes.CopyTo(idData);
                    macBytes.CopyTo(idData.AsSpan(epBytes.Length));
                }

                byte[] hash = SHA256.HashData(idData);
                UniqueIdentifier = BitConverter.ToInt64(hash);

                Status = NetPeerStatus.Running;
            }
        }

        private void NetworkLoop()
        {
            AssertIsOnLibraryThread();

            LogDebug("Network thread started");

            // Network loop
            do
            {
                try
                {
                    Heartbeat();
                }
                catch (Exception ex)
                {
                    LogWarning(ex.ToString());
                }
            }
            while (Status == NetPeerStatus.Running);

            // perform shutdown
            ExecutePeerShutdown();
        }

        private void ExecutePeerShutdown()
        {
            AssertIsOnLibraryThread();

            LogDebug("Shutting down...");

            // disconnect and make one final heartbeat
            var connections = Connections;
            lock (connections)
            {
                // reverse-for so elements can be removed without breaking loop
                for (int i = connections.Count; i-- > 0;)
                    connections[i]?.Shutdown(_shutdownReason);
            }

            foreach (var conn in Handshakes.Values)
                conn?.Shutdown(_shutdownReason);

            FlushDelayedPackets();

            // one final heartbeat, will send stuff and do disconnect
            Heartbeat();

            lock (InitMutex)
            {
                try
                {
                    if (Socket != null)
                    {
                        try
                        {
                            Socket.Shutdown(SocketShutdown.Receive);
                        }
                        catch (Exception ex)
                        {
                            LogDebug("Socket.Shutdown exception: " + ex.ToString());
                        }

                        try
                        {
                            Socket.Close(2); // 2 seconds timeout
                        }
                        catch (Exception ex)
                        {
                            LogDebug("Socket.Close exception: " + ex.ToString());
                        }
                    }
                }
                finally
                {
                    Socket = null;
                    Status = NetPeerStatus.NotRunning;
                    LogDebug("Shutdown complete");

                    // wake up any threads waiting for server shutdown
                    _messageReceivedEvent?.Set();
                }

                _receiveBuffer = Array.Empty<byte>();
                _sendBuffer = Array.Empty<byte>();
                UnsentUnconnectedMessages.Clear();
                Connections.Clear();
                ConnectionLookup.Clear();
                Handshakes.Clear();
            }
        }

        private void Heartbeat()
        {
            AssertIsOnLibraryThread();

            List<NetConnection> connections = Connections;

            // TODO: improve CHBpS constants
            TimeSpan now = NetTime.Now;
            TimeSpan delta = now - _lastHeartbeat;
            int maxCHBpS = Math.Min(250, 1250 - connections.Count);

            // max connection heartbeats/second max
            if (delta > TimeSpan.FromTicks(TimeSpan.TicksPerSecond / maxCHBpS) ||
                delta < TimeSpan.Zero)
            {
                _frameCounter++;
                _lastHeartbeat = now;

                // do handshake heartbeats
                if (!Handshakes.IsEmpty)
                {
                    foreach (NetConnection conn in Handshakes.Values)
                    {
                        conn.UnconnectedHeartbeat(now);

#if DEBUG
                        // sanity check
                        if (conn.Status == NetConnectionStatus.Disconnected &&
                            Handshakes.TryRemove(conn.RemoteEndPoint, out _))
                        {
                            LogWarning("Sanity fail! Handshakes list contained disconnected connection!");
                        }
#endif
                    }
                }

                SendDelayedPackets();

                // update _executeFlushSendQueue
                if (Configuration._autoFlushSendQueue)
                    _executeFlushSendQueue = true;

                // do connection heartbeats
                lock (connections)
                {
                    // reverse-for so elements can be removed without breaking loop
                    for (int i = connections.Count; i-- > 0;)
                    {
                        NetConnection conn = connections[i];
                        conn.Heartbeat(now, _frameCounter);

                        if (conn.Status == NetConnectionStatus.Disconnected)
                        {
                            connections.RemoveAt(i);
                            ConnectionLookup.TryRemove(conn.RemoteEndPoint, out _);
                        }
                    }
                }
                _executeFlushSendQueue = false;

                // send unsent unconnected messages
                while (UnsentUnconnectedMessages.TryDequeue(out var unsent))
                {
                    NetOutgoingMessage om = unsent.Message;
                    int length = 0;
                    om.Encode(_sendBuffer, ref length, 0);
                    SendPacket(length, unsent.EndPoint, 1);

                    Interlocked.Decrement(ref om._recyclingCount);
                    if (om._recyclingCount <= 0)
                        Recycle(om);
                }
            }

            if (Socket == null)
                return;

            // wait up to 10 ms for data to arrive
            if (!Socket.Poll(10000, SelectMode.SelectRead))
                return;

            // update now
            now = NetTime.Now;

            while (Socket.Available > 0)
            {
                int bytesReceived = 0;
                try
                {
                    bytesReceived = Socket.ReceiveFrom(
                        _receiveBuffer, 0, _receiveBuffer.Length, SocketFlags.None, ref _senderRemote);
                }
                catch (SocketException sx)
                {
                    switch (sx.SocketErrorCode)
                    {
                        case SocketError.ConnectionReset:
                            // connection reset by peer, aka connection forcibly closed aka "ICMP port unreachable" 
                            // we should shut down the connection; but _senderRemote seemingly cannot be trusted,
                            // so which connection should we shut down?!
                            // So, what to do?
                            LogWarning("ConnectionReset");
                            return;

                        case SocketError.NotConnected:
                            // socket is unbound; try to rebind it (happens on mobile when process goes to sleep)
                            BindSocket(true);
                            return;

                        default:
                            LogWarning("Socket exception: " + sx.ToString());
                            return;
                    }
                }

                if (bytesReceived < NetConstants.HeaderSize)
                    return;

                //LogVerbose("Received " + bytesReceived + " bytes");

                if (UPnP != null && UPnP.Status == UPnPStatus.Discovering)
                {
                    if (SetupUpnp(UPnP, now, _receiveBuffer.AsSpan(0, bytesReceived)))
                        return;
                }

                var senderEndPoint = (IPEndPoint)_senderRemote;
                ConnectionLookup.TryGetValue(senderEndPoint, out NetConnection? sender);

                //
                // parse packet into messages
                //
                int numMessages = 0;
                int numFragments = 0;
                int offset = 0;
                while ((bytesReceived - offset) >= NetConstants.HeaderSize)
                {
                    // decode header
                    //  8 bits - NetMessageType
                    //  1 bit  - Fragment?
                    // 15 bits - Sequence number
                    // 16 bits - Payload bit length

                    numMessages++;

                    var type = (NetMessageType)_receiveBuffer[offset++];

                    byte low = _receiveBuffer[offset++];
                    byte high = _receiveBuffer[offset++];

                    bool isFragment = (low & 1) == 1;
                    ushort sequenceNumber = (ushort)((low >> 1) | (high << 7));

                    numFragments++;

                    ushort payloadBitLength = (ushort)(_receiveBuffer[offset++] | (_receiveBuffer[offset++] << 8));
                    int payloadByteLength = NetBitWriter.BytesForBits(payloadBitLength);

                    if (bytesReceived - offset < payloadByteLength)
                    {
                        LogWarning(
                            "Malformed packet; stated payload length " + payloadByteLength +
                            ", remaining bytes " + (bytesReceived - offset));
                        return;
                    }

                    try
                    {
                        if (type >= NetMessageType.LibraryError)
                        {
                            if (sender != null)
                                sender.ReceivedLibraryMessage(type, offset, payloadByteLength);
                            else
                                ReceivedUnconnectedLibraryMessage(now, senderEndPoint, type, offset, payloadByteLength);
                        }
                        else
                        {
                            if (sender == null &&
                                !Configuration.IsMessageTypeEnabled(NetIncomingMessageType.UnconnectedData))
                                return; // dropping unconnected message since it's not enabled

                            ReadOnlySpan<byte> span = _receiveBuffer.AsSpan(offset, payloadByteLength);

                            if (sender == null ||
                                type == NetMessageType.Unconnected)
                            {
                                // We're connected; but we can still send unconnected messages to this peer
                                NetMessageView view = new(
                                    NetIncomingMessageType.UnconnectedData,
                                    type,
                                    isFragment,
                                    now,
                                    sequenceNumber,
                                    senderEndPoint,
                                    sender,
                                    null,
                                    span,
                                    payloadBitLength);
                                ReleaseMessage(view);
                            }
                            else
                            {
                                // connected application (non-library) message
                                NetMessageView view = new(
                                    NetIncomingMessageType.Data,
                                    type,
                                    isFragment,
                                    now,
                                    sequenceNumber,
                                    senderEndPoint,
                                    sender,
                                    null,
                                    span,
                                    payloadBitLength);
                                sender.ReceivedMessage(view);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogError("Packet parsing error: \"" + ex.Message + "\" from " + senderEndPoint);
                    }
                    offset += payloadByteLength;
                }

                Statistics.PacketReceived(bytesReceived, numMessages, numFragments);
                sender?.Statistics.PacketReceived(bytesReceived, numMessages, numFragments);
            }
        }

        private bool SetupUpnp(NetUPnP upnp, TimeSpan now, ReadOnlySpan<byte> data)
        {
            if (now >= upnp.DiscoveryDeadline ||
                data.Length <= 32)
                return false;

            // is this an UPnP response?
            string response = System.Text.Encoding.ASCII.GetString(data);
            if (response.Contains("upnp:rootdevice", StringComparison.OrdinalIgnoreCase) ||
                response.Contains("UPnP/1.0", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    int locationIndex = response.IndexOf("location:", StringComparison.OrdinalIgnoreCase) + 9;
                    ReadOnlySpan<char> locationLine = response.AsSpan()[locationIndex..];

                    ReadOnlySpan<char> location = locationLine
                        .Slice(0, locationLine.IndexOf("\r", StringComparison.Ordinal))
                        .Trim();

                    upnp.ExtractServiceUri(new Uri(location.ToString()));
                }
                catch (Exception ex)
                {
                    LogDebug("Failed to parse UPnP response: " + ex.ToString());

                    // don't try to parse this packet further
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// You need to call this to send queued messages if
        /// <see cref="NetPeerConfiguration.AutoFlushSendQueue"/> is false.
        /// </summary>
        public void FlushSendQueue()
        {
            _executeFlushSendQueue = true;
        }

        internal void HandleIncomingDiscoveryRequest(
            TimeSpan now, IPEndPoint senderEndPoint, int offset, int payloadByteLength)
        {
            if (!Configuration.IsMessageTypeEnabled(NetIncomingMessageType.DiscoveryRequest))
                return;

            var dr = CreateIncomingMessage(NetIncomingMessageType.DiscoveryRequest);
            if (payloadByteLength > 0)
                dr.Write(_receiveBuffer.AsSpan(offset, payloadByteLength));

            dr.ReceiveTime = now;
            dr.SenderEndPoint = senderEndPoint;
            ReleaseMessage(dr);
        }

        internal void HandleIncomingDiscoveryResponse(
            TimeSpan now, IPEndPoint senderEndPoint, int offset, int payloadByteLength)
        {
            if (!Configuration.IsMessageTypeEnabled(NetIncomingMessageType.DiscoveryResponse))
                return;

            var dr = CreateIncomingMessage(NetIncomingMessageType.DiscoveryResponse);
            if (payloadByteLength > 0)
                dr.Write(_receiveBuffer.AsSpan(offset, payloadByteLength));

            dr.ReceiveTime = now;
            dr.SenderEndPoint = senderEndPoint;
            ReleaseMessage(dr);
        }

        private void ReceivedUnconnectedLibraryMessage(
            TimeSpan now, IPEndPoint senderEndPoint, NetMessageType type, int offset, int payloadByteLength)
        {
            if (Handshakes.TryGetValue(senderEndPoint, out NetConnection? shake))
            {
                shake.ReceivedHandshake(now, type, offset, payloadByteLength);
                return;
            }

            // Library message from a completely unknown sender; lets just accept Connect
            switch (type)
            {
                case NetMessageType.Discovery:
                    HandleIncomingDiscoveryRequest(now, senderEndPoint, offset, payloadByteLength);
                    return;

                case NetMessageType.DiscoveryResponse:
                    HandleIncomingDiscoveryResponse(now, senderEndPoint, offset, payloadByteLength);
                    return;

                case NetMessageType.NatIntroduction:
                    if (Configuration.IsMessageTypeEnabled(NetIncomingMessageType.NatIntroductionSuccess))
                        HandleNatIntroduction(offset);
                    return;

                case NetMessageType.NatPunchMessage:
                    if (Configuration.IsMessageTypeEnabled(NetIncomingMessageType.NatIntroductionSuccess))
                        HandleNatPunch(offset, senderEndPoint);
                    return;

                case NetMessageType.ConnectResponse:
                    foreach (var hs in Handshakes)
                    {
                        if (!hs.Key.Address.Equals(senderEndPoint.Address) ||
                            !hs.Value._connectionInitiator)
                            continue;

                        // We are currently trying to connection to XX.XX.XX.XX:Y
                        // ... but we just received a ConnectResponse from XX.XX.XX.XX:Z
                        // Lets just assume the router decided to use this port instead

                        NetConnection hsconn = hs.Value;
                        ConnectionLookup.TryRemove(hs.Key, out _);
                        Handshakes.TryRemove(hs.Key, out _);

                        LogDebug("Detected host port change; rerouting connection to " + senderEndPoint);
                        hsconn.MutateEndPoint(senderEndPoint);

                        ConnectionLookup.TryAdd(senderEndPoint, hsconn);
                        Handshakes.TryAdd(senderEndPoint, hsconn);

                        hsconn.ReceivedHandshake(now, type, offset, payloadByteLength);
                        return;
                    }

                    LogWarning("Received unhandled library message " + type + " from " + senderEndPoint);
                    return;

                case NetMessageType.Connect:
                    if (!Configuration.AcceptIncomingConnections)
                    {
                        LogWarning("Received Connect, but we're not accepting incoming connections.");
                        return;
                    }
                    // handle connect
                    // It's someone wanting to shake hands with us!

                    int reservedSlots = Handshakes.Count + Connections.Count;
                    if (reservedSlots >= Configuration._maximumConnections)
                    {
                        // TODO: add event handler for this
                        // server full
                        NetOutgoingMessage full = CreateMessage("Server full");
                        full._messageType = NetMessageType.Disconnect;
                        SendLibraryMessage(full, senderEndPoint);
                        return;
                    }

                    // Ok, start handshake!
                    NetConnection conn = new NetConnection(this, senderEndPoint);
                    conn.Status = NetConnectionStatus.ReceivedInitiation;
                    Handshakes.TryAdd(senderEndPoint, conn);
                    conn.ReceivedHandshake(now, type, offset, payloadByteLength);
                    return;

                case NetMessageType.Disconnect:
                    // this is probably ok
                    LogVerbose("Received Disconnect from unconnected source: " + senderEndPoint);
                    return;

                default:
                    LogWarning("Received unhandled library message " + type + " from " + senderEndPoint);
                    return;
            }
        }

        internal void AcceptConnection(NetConnection connection)
        {
            // LogDebug("Accepted connection " + conn);
            connection.InitExpandMTU(NetTime.Now);

            if (!Handshakes.TryRemove(connection.RemoteEndPoint, out _))
                LogWarning("AcceptConnection called but Handshakes did not contain it!");

            lock (Connections)
            {
#if DEBUG
                if (Connections.Contains(connection))
                {
                    LogWarning("AcceptConnection called but Connections already contains it!");
                }
                else
#endif
                {
                    Connections.Add(connection);
                    ConnectionLookup.TryAdd(connection.RemoteEndPoint, connection);
                }
            }
        }

        [Conditional("DEBUG")]
        internal void AssertIsOnLibraryThread()
        {
            var ct = Thread.CurrentThread;
            if (ct != _networkThread)
            {
                throw new LidgrenException(
                    "Executing on wrong thread. " +
                    "Should be library thread (is " + ct.Name + ", ManagedThreadId " + ct.ManagedThreadId + ")");
            }
        }

        internal NetIncomingMessage SetupReadHelperMessage(int offset, int payloadLength)
        {
            AssertIsOnLibraryThread();

            if (_readHelperMessage == null)
                throw new InvalidOperationException("The peer is not initialized.");

            _readHelperMessage.BitLength = (offset + payloadLength) * 8;
            _readHelperMessage.BitPosition = offset * 8;
            return _readHelperMessage;
        }
    }
}
