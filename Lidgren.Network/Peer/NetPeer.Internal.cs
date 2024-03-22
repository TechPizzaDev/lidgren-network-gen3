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

namespace Lidgren.Network
{
    public partial class NetPeer
    {
        private object InitMutex { get; } = new object();

        private Thread? _networkThread;
        private SocketAddress _senderRemote;
        private uint _frameCounter;
        private TimeSpan _lastHeartbeat;
        private TimeSpan _lastSocketBind = TimeSpan.MinValue;
        private TimeSpan _socketRebindDelay = TimeSpan.FromSeconds(1.0);
        private int _closeSendTimeoutSeconds = 5;
        private AutoResetEvent? _messageReceivedEvent;
        private List<(SynchronizationContext SyncContext, SendOrPostCallback Callback)>? _receiveCallbacks;
        internal NetIncomingMessage? _readHelperMessage;
        internal byte[] _sendBuffer = Array.Empty<byte>();
        internal byte[] _receiveBuffer = Array.Empty<byte>();

        private NetQueue<NetIncomingMessage> ReleasedIncomingMessages { get; } = new(4);

        internal NetQueue<(NetAddress Address, NetOutgoingMessage Message)> UnsentUnconnectedMessages { get; } = new(2);

        internal ConcurrentDictionary<NetAddress, NetConnection> Handshakes { get; } = new();

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
                if (!HandleReleasedFragment(message))
                {
                    TryRecycle(message);
                }
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
                    LogWarning(new NetLogMessage(NetLogCode.PacketCallbackException, message, ex));
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
            if (Socket != null && now - _lastSocketBind < _socketRebindDelay)
            {
                LogWarning(NetLogMessage.FromTime(NetLogCode.SocketRebindDelayed, time: (now - _lastSocketBind)));
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
                        LogWarning(new NetLogMessage(NetLogCode.MissingIPv6ForDualStack));
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

            LogDebug(new NetLogMessage(NetLogCode.SocketBound, endPoint: boundEp));

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

                UPnP.Invalidate(NetTime.Now);

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

            // Network loop
            do
            {
                try
                {
                    Heartbeat();
                }
                catch (Exception ex)
                {
                    LogWarning(new NetLogMessage(NetLogCode.HeartbeatException, ex));
                }
            }
            while (Status == NetPeerStatus.Running);

            // perform shutdown
            ExecutePeerShutdown();
        }

        private void ExecutePeerShutdown()
        {
            AssertIsOnLibraryThread();

            // disconnect and make one final heartbeat
            List<NetConnection> connections = Connections;
            lock (connections)
            {
                // reverse-for so elements can be removed without breaking loop
                for (int i = connections.Count; i-- > 0;)
                {
                    if (_shutdownReason != null)
                        Interlocked.Increment(ref _shutdownReason._recyclingCount);

                    connections[i].Shutdown(_shutdownReason);
                }
            }

            foreach (NetConnection conn in Handshakes.Values)
            {
                if (_shutdownReason != null)
                    Interlocked.Increment(ref _shutdownReason._recyclingCount);

                conn.Shutdown(_shutdownReason);
            }

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
                            LogWarning(new NetLogMessage(NetLogCode.SocketShutdownException, ex));
                        }

                        try
                        {
                            Socket.Close(_closeSendTimeoutSeconds);
                        }
                        catch (Exception ex)
                        {
                            LogWarning(new NetLogMessage(NetLogCode.SocketCloseException, ex));
                        }
                    }
                }
                finally
                {
                    Socket = null;
                    Status = NetPeerStatus.NotRunning;

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
                            Handshakes.TryRemove(conn.RemoteAddress, out _))
                        {
                            LogWarning(NetLogMessage.FromValues(NetLogCode.DisconnectedHandshake, endPoint: conn));
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
                    // TODO: iterate starting at different positions?

                    // reverse-for so elements can be removed without breaking loop
                    for (int i = connections.Count; i-- > 0;)
                    {
                        NetConnection conn = connections[i];
                        conn.Heartbeat(now, _frameCounter);

                        if (conn.Status == NetConnectionStatus.Disconnected)
                        {
                            connections.RemoveAt(i);
                            ConnectionLookup.TryRemove(conn.RemoteAddress, out _);
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

                    var (sent, connReset) = SendPacket(length, unsent.Address, 1);
                    if (!sent && !connReset)
                    {
                        UnsentUnconnectedMessages.EnqueueFirst(unsent);
                        break;
                    }

                    if (Interlocked.Decrement(ref om._recyclingCount) == 0)
                    {
                        Recycle(om);
                    }
                }
            }

            Socket? socket = Socket;
            if (socket == null)
                return;

            // wait up to 10 ms for data to arrive
            if (!socket.Poll(10000, SelectMode.SelectRead))
                return;

            // update now
            now = NetTime.Now;

            byte[] buffer = _receiveBuffer;

            int available = socket.Available;
            while (available > 0)
            {
                int bytesReceived = 0;
                try
                {
                    bytesReceived = socket.ReceiveFrom(buffer.AsSpan(), SocketFlags.None, _senderRemote);

                    available -= bytesReceived;
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
                            LogWarning(new NetLogMessage(NetLogCode.ConnectionReset, sx));
                            return;

                        case SocketError.NotConnected:
                            // socket is unbound; try to rebind it (happens on mobile when process goes to sleep)
                            BindSocket(true);
                            return;

                        default:
                            LogWarning(new NetLogMessage(NetLogCode.ReceiveFailure, sx));
                            return;
                    }
                }

                if (bytesReceived < NetConstants.HeaderSize)
                    return;

                //LogVerbose("Received " + bytesReceived + " bytes");

                if (UPnP.Status == UPnPStatus.Discovering)
                {
                    if (SetupUpnp(UPnP, now, buffer.AsSpan(0, bytesReceived)))
                        continue;
                }

                NetAddress senderAddress = new(_senderRemote);
                ConnectionLookup.TryGetValue(senderAddress, out NetConnection? sender);

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

                    var type = (NetMessageType)buffer[offset++];

                    byte low = buffer[offset++];
                    byte high = buffer[offset++];

                    bool isFragment = (low & 1) == 1;
                    ushort sequenceNumber = (ushort)((low >> 1) | (high << 7));

                    numFragments++;

                    ushort payloadBitLength = (ushort)(buffer[offset++] | (buffer[offset++] << 8));
                    int payloadByteLength = NetBitWriter.BytesForBits(payloadBitLength);

                    if (bytesReceived - offset < payloadByteLength)
                    {
                        LogWarning(NetLogMessage.FromValues(NetLogCode.InvalidPacketSize,
                            value: bytesReceived - offset,
                            maxValue: payloadByteLength));
                        return;
                    }

                    try
                    {
                        if (type >= NetMessageType.LibraryError)
                        {
                            if (sender != null)
                                sender.ReceivedLibraryMessage(type, offset, payloadByteLength);
                            else
                                ReceivedUnconnectedLibraryMessage(now, senderAddress, type, offset, payloadByteLength);
                        }
                        else
                        {
                            if (sender == null &&
                                !Configuration.IsMessageTypeEnabled(NetIncomingMessageType.UnconnectedData))
                                return; // dropping unconnected message since it's not enabled

                            ReadOnlySpan<byte> span = buffer.AsSpan(offset, payloadByteLength);

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
                                    senderAddress,
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
                                    senderAddress,
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
                        LogError(new NetLogMessage(NetLogCode.PacketCallbackException, ex, sender, senderAddress));
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
                    if (locationIndex == -1)
                    {
                        LogWarning(new NetLogMessage(NetLogCode.UPnPInvalidResponse, data: response));
                        return true;
                    }

                    ReadOnlySpan<char> locationLine = response.AsSpan()[locationIndex..];
                    int locationEnd = locationLine.IndexOf("\r", StringComparison.Ordinal);
                    if (locationEnd == -1)
                    {
                        LogWarning(new NetLogMessage(NetLogCode.UPnPInvalidResponse, data: response));
                        return true;
                    }

                    ReadOnlySpan<char> location = locationLine.Slice(0, locationEnd).Trim();
                    upnp.ExtractServiceUri(new Uri(location.ToString()));
                }
                catch (Exception ex)
                {
                    LogWarning(new NetLogMessage(NetLogCode.UPnPInvalidResponse, ex, data: response));
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
            TimeSpan now, NetAddress address, int offset, int payloadByteLength)
        {
            if (!Configuration.IsMessageTypeEnabled(NetIncomingMessageType.DiscoveryRequest))
                return;

            var dr = CreateIncomingMessage(NetIncomingMessageType.DiscoveryRequest, address);
            if (payloadByteLength > 0)
                dr.Write(_receiveBuffer.AsSpan(offset, payloadByteLength));

            dr.ReceiveTime = now;
            ReleaseMessage(dr);
        }

        internal void HandleIncomingDiscoveryResponse(
            TimeSpan now, NetAddress address, int offset, int payloadByteLength)
        {
            if (!Configuration.IsMessageTypeEnabled(NetIncomingMessageType.DiscoveryResponse))
                return;

            var dr = CreateIncomingMessage(NetIncomingMessageType.DiscoveryResponse, address);
            if (payloadByteLength > 0)
                dr.Write(_receiveBuffer.AsSpan(offset, payloadByteLength));

            dr.ReceiveTime = now;
            ReleaseMessage(dr);
        }

        private void ReceivedUnconnectedLibraryMessage(
            TimeSpan now, NetAddress senderAddress, NetMessageType type, int offset, int payloadByteLength)
        {
            if (Handshakes.TryGetValue(senderAddress, out NetConnection? shake))
            {
                shake.ReceivedHandshake(now, type, offset, payloadByteLength);
                return;
            }

            // Library message from a completely unknown sender; lets just accept Connect
            switch (type)
            {
                case NetMessageType.Discovery:
                    HandleIncomingDiscoveryRequest(now, senderAddress, offset, payloadByteLength);
                    return;

                case NetMessageType.DiscoveryResponse:
                    HandleIncomingDiscoveryResponse(now, senderAddress, offset, payloadByteLength);
                    return;

                case NetMessageType.NatIntroduction:
                    if (Configuration.IsMessageTypeEnabled(NetIncomingMessageType.NatIntroductionSuccess))
                        HandleNatIntroduction(offset);
                    return;

                case NetMessageType.NatPunchMessage:
                    if (Configuration.IsMessageTypeEnabled(NetIncomingMessageType.NatIntroductionSuccess))
                        HandleNatPunch(offset, senderAddress.Clone());
                    return;

                case NetMessageType.ConnectResponse:
                    foreach ((NetAddress hsAddress, NetConnection hsconn) in Handshakes)
                    {
                        if (!hsAddress.AddressEquals(senderAddress) ||
                            !hsconn._connectionInitiator)
                            continue;

                        // We are currently trying to connection to XX.XX.XX.XX:Y
                        // ... but we just received a ConnectResponse from XX.XX.XX.XX:Z
                        // Lets just assume the router decided to use this port instead

                        ConnectionLookup.TryRemove(hsAddress, out _);
                        Handshakes.TryRemove(hsAddress, out _);

                        hsconn.MutateAddress(senderAddress.Clone());
                        LogDebug(new NetLogMessage(NetLogCode.HostPortChanged, null, hsconn, hsconn.RemoteAddress));

                        ConnectionLookup.TryAdd(hsconn.RemoteAddress, hsconn);
                        Handshakes.TryAdd(hsconn.RemoteAddress, hsconn);

                        hsconn.ReceivedHandshake(now, type, offset, payloadByteLength);
                        return;
                    }
                    goto default;

                case NetMessageType.Connect:
                    if (!Configuration.AcceptIncomingConnections)
                    {
                        LogWarning(NetLogMessage.FromValues(NetLogCode.MessageTypeDisabled,
                            endPoint: senderAddress, value: (int)type));
                        return;
                    }
                    // handle connect
                    // It's someone wanting to shake hands with us!

                    NetConnection conn = new(this, senderAddress.Clone());
                    conn.Status = NetConnectionStatus.ReceivedInitiation;

                    Handshakes.TryAdd(conn.RemoteAddress, conn);
                    conn.ReceivedHandshake(now, type, offset, payloadByteLength);
                    return;

                case NetMessageType.InvalidHandshake:
                case NetMessageType.WrongAppIdentifier:
                case NetMessageType.ConnectTimedOut:
                case NetMessageType.TimedOut:
                case NetMessageType.Disconnect:
                    // this is probably ok
                    LogWarning(NetLogMessage.FromValues(NetLogCode.UnconnectedLibraryMessage,
                        endPoint: senderAddress, value: (int)type));
                    return;

                case NetMessageType.Acknowledge:
                case NetMessageType.Ping:
                    LogVerbose(NetLogMessage.FromValues(NetLogCode.UnhandledLibraryMessage,
                        endPoint: senderAddress, value: (int)type));
                    break;

                default:
                    LogWarning(NetLogMessage.FromValues(NetLogCode.UnhandledLibraryMessage,
                        endPoint: senderAddress, value: (int)type));
                    return;
            }
        }

        internal void AcceptConnection(NetConnection connection)
        {
            // LogDebug("Accepted connection " + conn);
            connection.InitExpandMTU(NetTime.Now);

            if (!Handshakes.TryRemove(connection.RemoteAddress, out _))
            {
                LogWarning(new NetLogMessage(NetLogCode.MissingHandshake));
            }

            lock (Connections)
            {
#if DEBUG
                if (Connections.Contains(connection))
                {
                    LogWarning(new NetLogMessage(NetLogCode.DuplicateConnection));
                }
                else
#endif
                {
                    Connections.Add(connection);
                    ConnectionLookup.TryAdd(connection.RemoteAddress, connection);
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

        internal NetOutgoingMessage CreateReadHelperOutMessage(int offset, int payloadLength)
        {
            AssertIsOnLibraryThread();

            if (_readHelperMessage == null)
                throw new InvalidOperationException("The peer is not initialized.");

            var message = CreateMessage();
            _readHelperMessage.BitLength = (offset + payloadLength) * 8;
            _readHelperMessage.BitPosition = offset * 8;
            message.Write(_readHelperMessage);
            message.BitPosition = 0;
            return message;
        }
    }
}
