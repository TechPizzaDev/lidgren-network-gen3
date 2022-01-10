using System;
using System.Threading;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Buffers;

namespace Lidgren.Network
{
    /// <summary>
    /// Represents a local peer capable of holding zero, one or more connections to remote peers.
    /// </summary>
    public partial class NetPeer : IDisposable
    {
        private static int _initializedPeersCount;

        private bool _isDisposed;
        private NetOutgoingMessage? _shutdownReason;

        private object MessageReceivedEventInitMutex { get; } = new object();

        private ConcurrentDictionary<IPEndPoint, NetConnection> ConnectionLookup { get; } =
            new ConcurrentDictionary<IPEndPoint, NetConnection>();

        internal List<NetConnection> Connections { get; } =
            new List<NetConnection>();

        public NetMessageScheduler DefaultScheduler { get; } =
            new NetMessageScheduler();

        /// <summary>
        /// Gets the <see cref="NetPeerStatus"/> of the <see cref="NetPeer"/>.
        /// </summary>
        public NetPeerStatus Status { get; private set; }

        /// <summary>
        /// Gets a unique identifier for this <see cref="NetPeer"/> based on IP/port and MAC address. 
        /// <para>Not available until <see cref="Start"/> has been called.</para>
        /// </summary>
        public long UniqueIdentifier { get; private set; }

        /// <summary>
        /// Gets the port number this <see cref="NetPeer"/> is listening and sending on,
        /// if <see cref="Start"/> has been called.
        /// </summary>
        public int Port { get; private set; }

        /// <summary>
        /// Gets a <see cref="NetUPnP"/> helper.
        /// </summary>
        public NetUPnP UPnP { get; }

        /// <summary>
        /// Gets or sets the application defined object containing data about the peer.
        /// </summary>
        public object? Tag { get; set; }

        /// <summary>
        /// Gets the number of active connections.
        /// </summary>
        public int ConnectionCount => Connections.Count;

        /// <summary>
        /// Statistics on this <see cref="NetPeer"/> since it was initialized.
        /// </summary>
        public NetPeerStatistics Statistics { get; }

        /// <summary>
        /// Gets the configuration used to instantiate this <see cref="NetPeer"/>.
        /// </summary>
        public NetPeerConfiguration Configuration { get; }

        public ArrayPool<byte> StoragePool => Configuration.StoragePool;

        /// <summary>
        /// Signalling event which can be waited on to determine when a message may be queued for reading.
        /// </summary>
        /// <remarks>
        /// There is no guarantee that after the event is signaled the blocked thread will 
        /// find the message in the queue. Other user created threads could be preempted and dequeue 
        /// the message before the waiting thread wakes up.
        /// </remarks>
        public AutoResetEvent MessageReceivedEvent
        {
            get
            {
                if (_messageReceivedEvent == null)
                {
                    // make sure we don't create more than one event
                    lock (MessageReceivedEventInitMutex)
                    {
                        if (_messageReceivedEvent == null)
                            _messageReceivedEvent = new AutoResetEvent(false);
                    }
                }
                return _messageReceivedEvent;
            }
        }

        /// <summary>
        /// Constructs the peer with a given configuration.
        /// </summary>
        public NetPeer(NetPeerConfiguration config)
        {
            Configuration = config ?? throw new ArgumentNullException(nameof(config));

            if (Configuration.LocalAddress.AddressFamily == AddressFamily.InterNetworkV6)
                _senderRemote = new IPEndPoint(IPAddress.IPv6Any, 0);
            else
                _senderRemote = new IPEndPoint(IPAddress.Any, 0);

            Statistics = new NetPeerStatistics(this);
            UPnP = new NetUPnP(this);

            Status = NetPeerStatus.NotRunning;
        }

        /// <summary>
        /// Appends the current connections to a collection.
        /// </summary>
        /// <param name="destination">The collection to which append connections.</param>
        /// <returns>The amount of connections appended.</returns>
        public int GetConnections(ICollection<NetConnection> destination)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));

            lock (Connections)
            {
                foreach (NetConnection conn in Connections)
                {
                    destination.Add(conn);
                }
                return Connections.Count;
            }
        }

        /// <summary>
        /// Binds to socket and spawns the networking thread.
        /// </summary>
        public void Start()
        {
            if (Status != NetPeerStatus.NotRunning)
            {
                // already running! Just ignore...
                LogWarning(new NetLogMessage(NetLogCode.AlreadyConnected));
                return;
            }

            Status = NetPeerStatus.Starting;

            // fix network thread name
            if (Configuration.NetworkThreadName == "Lidgren.Network Thread")
            {
                int pc = Interlocked.Increment(ref _initializedPeersCount);
                Configuration.NetworkThreadName = "Lidgren.Network Thread " + pc.ToString(CultureInfo.InvariantCulture);
            }

            InitializeNetwork();

            // start network thread
            _networkThread = new Thread(new ThreadStart(NetworkLoop));
            _networkThread.Name = Configuration.NetworkThreadName;
            _networkThread.IsBackground = true;
            _networkThread.Start();
        }

        /// <summary>
        /// Tries to get the connection for a certain remote endpoint.
        /// </summary>
        public bool TryGetConnection(
            IPEndPoint endPoint, [MaybeNullWhen(false)] out NetConnection? connection)
        {
            Debug.Assert(endPoint != null);

            return ConnectionLookup.TryGetValue(endPoint, out connection);
        }

        /// <summary>
        /// Gets the connection for a certain remote endpoint.
        /// </summary>
        public NetConnection? GetConnection(IPEndPoint endPoint)
        {
            TryGetConnection(endPoint, out var connection);
            return connection;
        }

        /// <summary>
        /// Tries to read a pending incoming message from any connection without blocking.
        /// </summary>
        /// <returns>Whether a message was successfully read.</returns>
        public bool TryReadMessage([MaybeNullWhen(false)] out NetIncomingMessage message)
        {
            if (ReleasedIncomingMessages.TryDequeue(out message))
                return true;
            return false;
        }

        /// <summary>
        /// Tries to read an incoming message from any connection, 
        /// blocking up to the specified timeout.
        /// </summary>
        /// <returns>Whether a message was successfully read.</returns>
        public bool TryReadMessage(
            TimeSpan timeout,
            [MaybeNullWhen(false)] out NetIncomingMessage message)
        {
            if (timeout.Ticks < 0 && timeout != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(timeout));

            // check if we already have a message to return
            if (TryReadMessage(out message))
                return true;

            AutoResetEvent resetEvent = MessageReceivedEvent;
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                do
                {
                    resetEvent.WaitOne();
                }
                while (!TryReadMessage(out message));
                return true;
            }

            do
            {
                long startTicks = Stopwatch.GetTimestamp();
                if (!resetEvent.WaitOne(timeout))
                {
                    // When the timeout hits, try to read one last time.
                    return TryReadMessage(out message);
                }

                if (TryReadMessage(out message))
                    return true;

                // Missing a message should rarely happen as we use AutoResetEvent.
                // The user is most likely reading messages without checking the reset event.

                long elapsedTicks = Stopwatch.GetTimestamp() - startTicks;
                timeout -= TimeSpan.FromSeconds(elapsedTicks * NetTime.InverseFrequency);

            }
            // Go back and wait again if we have leftover time.
            while (timeout.Ticks > 0);

            return false;
        }

        /// <summary>
        /// Tries to read an incoming message from any connection,
        /// blocking up to the specified timeout.
        /// </summary>
        /// <returns>Whether a message was successfully read.</returns>
        public bool TryReadMessage(
            int millisecondsTimeout,
            [MaybeNullWhen(false)] out NetIncomingMessage message)
        {
            return TryReadMessage(TimeSpan.FromMilliseconds(millisecondsTimeout), out message);
        }

        /// <summary>
        /// Tries to read pending incoming messages from any connection.
        /// </summary>
        /// <param name="destination">The collection to which append messages.</param>
        /// <returns>The amount of messages read.</returns>
        public int TryReadMessages(ICollection<NetIncomingMessage> destination)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));

            if (destination.IsReadOnly)
                throw new ArgumentException("The collection is read-only.");

            return ReleasedIncomingMessages.TryDrain(destination);
        }

        // send message immediately
        internal void SendLibraryMessage(NetOutgoingMessage message, IPEndPoint recipient)
        {
            AssertIsOnLibraryThread();
            LidgrenException.Assert(!message._isSent);

            int length = 0;
            message.Encode(_sendBuffer, ref length, 0);
            SendPacket(length, recipient, 1);
        }

        /// <summary>
        /// Send raw bytes; only used for debugging. 
        /// </summary>
        public NetSocketResult RawSend(byte[] buffer, int offset, int length, IPEndPoint destination)
        {
            // wrong thread might crash with network thread
            Array.Copy(buffer, offset, _sendBuffer, 0, length);
            return SendPacket(length, destination, 1);
        }

        /// <summary>
        /// Disconnects all active connections and closes the socket.
        /// </summary>
        public void Shutdown(NetOutgoingMessage? reason = null)
        {
            // called on user thread
            if (Socket == null)
                return; // already shut down

            _shutdownReason = reason;
            if (_shutdownReason != null)
            {
                _shutdownReason._messageType = NetMessageType.Disconnect;
                Interlocked.Increment(ref _shutdownReason._recyclingCount);
            }

            Status = NetPeerStatus.ShutdownRequested;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _messageReceivedEvent?.Dispose();
                    _readHelperMessage?.Dispose();

                    if (_outgoingMessagePool != null)
                    {
                        while (_outgoingMessagePool.TryDequeue(out var message))
                            message.Dispose();

                        //_outgoingMessagePool.Dispose();
                    }

                    if (_incomingMessagePool != null)
                    {
                        while (_incomingMessagePool.TryDequeue(out var message))
                            message.Dispose();

                        //_incomingMessagePool.Dispose();
                    }
                }
                _isDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
