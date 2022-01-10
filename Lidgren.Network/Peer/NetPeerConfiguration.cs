using System;
using System.Buffers;
using System.Net;

namespace Lidgren.Network
{
    // TODO: split up to Server and Client config

    /// <summary>
    /// Configuration for a <see cref="NetPeer"/>.
    /// Partly immutable after a <see cref="NetPeer"/> has been initialized
    /// with specified <see cref="NetPeerConfiguration"/> instance.
    /// </summary>
    public sealed class NetPeerConfiguration
    {
        // Maximum transmission unit
        // Ethernet can take 1500 bytes of payload, so lets stay below that.
        // The aim is for a max full packet to be 1440 bytes (30 x 48 bytes, lower than 1468)
        // -20 bytes IP header
        // -8 bytes UDP header
        // -4 bytes to be on the safe side and align to 8-byte boundary
        // Total 1408 bytes
        // Note that lidgren headers (5 bytes) are not included here; since it's part of the "mtu payload"

        /// <summary>
        /// Default MTU value in bytes.
        /// </summary>
        public const int DefaultMTU = 1408;

        private const string IsLockedMessage =
            "You may not modify the configuration after it has been used to initialize a NetPeer.";

        private bool _isLocked;
        private string _networkThreadName;
        private IPAddress _localAddress;
        private IPAddress _broadcastAddress;
        private bool _dualStack;
        private ArrayPool<byte> _storagePool;

        internal bool _acceptIncomingConnections;
        internal TimeSpan _pingInterval;
        internal bool _useMessageRecycling;
        internal TimeSpan _connectionTimeout;
        internal bool _autoFlushSendQueue;
        internal NetIncomingMessageType _disabledTypes;
        internal int _port;
        internal int _receiveBufferSize;
        internal int _sendBufferSize;
        internal TimeSpan _resendHandshakeInterval;
        internal int _maximumHandshakeAttempts;
        internal TimeSpan _fragmentGroupTimeout;
        internal bool _suppressUnreliableUnorderedAcks;

        // bad network simulation
        internal float _loss;
        internal float _duplicates;
        internal TimeSpan _minimumOneWayLatency;
        internal TimeSpan _randomOneWayLatency;

        // MTU
        internal int _maximumTransmissionUnit;
        internal bool _autoExpandMTU;
        internal int _expandMTUFailAttempts;
        internal TimeSpan _expandMTUFrequency;

        /// <summary>
        /// <see cref="NetPeerConfiguration"/> constructor.
        /// </summary>
        public NetPeerConfiguration(string appIdentifier)
        {
            if (string.IsNullOrEmpty(appIdentifier))
                throw new LidgrenException("App identifier must be at least one character long.");
            AppIdentifier = appIdentifier;

            _disabledTypes =
                NetIncomingMessageType.ConnectionApproval |
                NetIncomingMessageType.UnconnectedData |
                NetIncomingMessageType.ConnectionLatencyUpdated |
                NetIncomingMessageType.NatIntroductionSuccess;

            _networkThreadName = "Lidgren Network Thread";
            _localAddress = IPAddress.Any;
            _broadcastAddress = NetUtility.RetrieveBroadcastAddress() ?? IPAddress.Broadcast;
            _storagePool = ArrayPool<byte>.Create();

            _port = 0;
            _receiveBufferSize = 131071;
            _sendBufferSize = 131071;
            _acceptIncomingConnections = false;
            _pingInterval = TimeSpan.FromSeconds(4);
            _connectionTimeout = TimeSpan.FromSeconds(25);
            _fragmentGroupTimeout = TimeSpan.FromSeconds(8);
            _useMessageRecycling = true;
            _resendHandshakeInterval = TimeSpan.FromSeconds(3);
            _maximumHandshakeAttempts = 5;
            _autoFlushSendQueue = true;

            _maximumTransmissionUnit = DefaultMTU;
            _autoExpandMTU = false;
            _expandMTUFrequency = TimeSpan.FromSeconds(2);
            _expandMTUFailAttempts = 5;
            UnreliableSizeBehaviour = NetUnreliableSizeBehaviour.NormalFragmentation;

            _loss = 0f;
            _minimumOneWayLatency = TimeSpan.Zero;
            _randomOneWayLatency = TimeSpan.Zero;
            _duplicates = 0f;

            _isLocked = false;
        }

        internal void Lock()
        {
            _isLocked = true;
        }

        /// <summary>
        /// Gets the identifier of this application;
        /// the library can only connect to matching app identifier peers.
        /// </summary>
        public string AppIdentifier { get; }

        /// <summary>
        /// Enables receiving of the specified type of message.
        /// </summary>
        public void EnableMessageType(NetIncomingMessageType type)
        {
            _disabledTypes &= ~type;
        }

        /// <summary>
        /// Disables receiving of the specified type of message.
        /// </summary>
        public void DisableMessageType(NetIncomingMessageType type)
        {
            _disabledTypes |= type;
        }

        /// <summary>
        /// Enables or disables receiving of the specified type of message.
        /// </summary>
        public void SetMessageTypeEnabled(NetIncomingMessageType type, bool enabled)
        {
            if (enabled)
                _disabledTypes &= ~type;
            else
                _disabledTypes |= type;
        }

        /// <summary>
        /// Gets whether receiving of the specified message type is enabled.
        /// </summary>
        public bool IsMessageTypeEnabled(NetIncomingMessageType type)
        {
            return !((_disabledTypes & type) == type);
        }

        /// <summary>
        /// Gets or sets the behaviour of unreliable sends above MTU.
        /// </summary>
        public NetUnreliableSizeBehaviour UnreliableSizeBehaviour { get; set; }

        public ArrayPool<byte> StoragePool
        {
            get => _storagePool;
            set => _storagePool = value ?? ArrayPool<byte>.Shared;
        }

        /// <summary>
        /// Gets or sets the name of the library network thread.
        /// <para>
        /// Cannot be changed once <see cref="NetPeer"/> is initialized.
        /// </para>
        /// </summary>
        public string NetworkThreadName
        {
            get => _networkThreadName;
            set
            {
                if (_isLocked)
                    throw new LidgrenException(
                        "NetworkThreadName may not be set after the NetPeer which uses the configuration has been started");
                _networkThreadName = value;
            }
        }

        /// <summary>
        /// Gets or sets the maximum amount of bytes to send in a single packet, excluding IP, UDP and Lidgren headers.
        /// <para>
        /// Cannot be changed once <see cref="NetPeer"/> is initialized.
        /// </para>
        /// </summary>
        public int MaximumTransmissionUnit
        {
            get => _maximumTransmissionUnit;
            set
            {
                if (_isLocked)
                    throw new LidgrenException(IsLockedMessage);
                if (value < 1 || value >= ((ushort.MaxValue + 1) / 8))
                    throw new LidgrenException("MaximumTransmissionUnit must be between 1 and " + (((ushort.MaxValue + 1) / 8) - 1) + " bytes");
                _maximumTransmissionUnit = value;
            }
        }

        /// <summary>
        /// Gets or sets the time between latency calculating pings.
        /// </summary>
        public TimeSpan PingInterval
        {
            get => _pingInterval;
            set => _pingInterval = value;
        }

        /// <summary>
        /// Gets or sets if the library should recycling messages to avoid excessive garbage collection.
        /// <para>
        /// Cannot be changed once <see cref="NetPeer"/> is initialized.
        /// </para>
        /// </summary>
        public bool UseMessageRecycling
        {
            get => _useMessageRecycling;
            set
            {
                if (_isLocked)
                    throw new LidgrenException(IsLockedMessage);
                _useMessageRecycling = value;
            }
        }

        /// <summary>
        /// Gets or sets the timeout that will be postponed on a successful ping/pong.
        /// </summary>
        public TimeSpan ConnectionTimeout
        {
            get => _connectionTimeout;
            set
            {
                if (value < _pingInterval)
                    throw new LidgrenException("Connection timeout cannot be lower than ping interval!");
                _connectionTimeout = value;
            }
        }

        /// <summary>
        /// Gets or sets the timeout used to drop inactive fragment groups.
        /// </summary>
        public TimeSpan FragmentGroupTimeout
        {
            get => _fragmentGroupTimeout;
            set => _fragmentGroupTimeout = value;
        }

        /// <summary>
        /// Enables or disables automatic flushing of the send queue.
        /// <para>
        /// If disabled, you must manully call <see cref="NetPeer.FlushSendQueue"/> to flush sent messages to network.
        /// </para>
        /// </summary>
        public bool AutoFlushSendQueue
        {
            get => _autoFlushSendQueue;
            set => _autoFlushSendQueue = value;
        }

        /// <summary>
        /// If true, will not send acks for unreliable unordered messages. 
        /// This will save bandwidth but disable flow control and duplicate detection for this type of messages.
        /// </summary>
        public bool SuppressUnreliableUnorderedAcks
        {
            get => _suppressUnreliableUnorderedAcks;
            set
            {
                if (_isLocked)
                    throw new LidgrenException(IsLockedMessage);
                _suppressUnreliableUnorderedAcks = value;
            }
        }

        /// <summary>
        /// Gets or sets the local <see cref="IPAddress"/> to bind to. Defaults to <see cref="IPAddress.Any"/>.
        /// <para>
        /// Cannot be changed once <see cref="NetPeer"/> is initialized.
        /// </para>
        /// </summary>
        public IPAddress LocalAddress
        {
            get => _localAddress;
            set
            {
                if (_isLocked)
                    throw new LidgrenException(IsLockedMessage);
                _localAddress = value;
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether the library should use IPv6 dual stack mode.
        /// If you enable this you should make sure that the <see cref="LocalAddress"/> is an IPv6 address.
        /// <para>
        /// Cannot be changed once <see cref="NetPeer"/> is initialized.
        /// </para>
        /// </summary>
        public bool DualStack
        {
            get => _dualStack;
            set
            {
                if (_isLocked)
                    throw new LidgrenException(IsLockedMessage);
                _dualStack = value;
            }
        }

        /// <summary>
        /// Gets or sets the local broadcast address to use when broadcasting
        /// </summary>
        public IPAddress BroadcastAddress
        {
            get => _broadcastAddress;
            set
            {
                if (_isLocked)
                    throw new LidgrenException(IsLockedMessage);
                _broadcastAddress = value;
            }
        }

        /// <summary>
        /// Gets or sets the local port to bind to. Defaults to 0.
        /// <para>
        /// Cannot be changed once <see cref="NetPeer"/> is initialized.
        /// </para>
        /// </summary>
        public int Port
        {
            get => _port;
            set
            {
                if (_isLocked)
                    throw new LidgrenException(IsLockedMessage);
                _port = value;
            }
        }

        /// <summary>
        /// Gets or sets the size in bytes of the receiving buffer. Defaults to 131071 bytes.
        /// <para>
        /// Cannot be changed once <see cref="NetPeer"/> is initialized.
        /// </para>
        /// </summary>
        public int ReceiveBufferSize
        {
            get => _receiveBufferSize;
            set
            {
                if (_isLocked)
                    throw new LidgrenException(IsLockedMessage);
                _receiveBufferSize = value;
            }
        }

        /// <summary>
        /// Gets or sets the size in bytes of the sending buffer. Defaults to 131071 bytes.
        /// <para>
        /// Cannot be changed once <see cref="NetPeer"/> is initialized.
        /// </para>
        /// </summary>
        public int SendBufferSize
        {
            get => _sendBufferSize;
            set
            {
                if (_isLocked)
                    throw new LidgrenException(IsLockedMessage);
                _sendBufferSize = value;
            }
        }

        /// <summary>
        /// Gets or sets if the <see cref="NetPeer"/> should accept incoming connections.
        /// <para>
        /// This is automatically set to true in <see cref="NetServer"/> and false in <see cref="NetClient"/>.
        /// </para>
        /// </summary>
        public bool AcceptIncomingConnections
        {
            get => _acceptIncomingConnections;
            set => _acceptIncomingConnections = value;
        }

        /// <summary>
        /// Gets or sets the number of seconds between handshake attempts.
        /// </summary>
        public TimeSpan ResendHandshakeInterval
        {
            get => _resendHandshakeInterval;
            set => _resendHandshakeInterval = value;
        }

        /// <summary>
        /// Gets or sets the maximum number of handshake attempts before failing to connect.
        /// </summary>
        public int MaximumHandshakeAttempts
        {
            get => _maximumHandshakeAttempts;
            set
            {
                if (value < 1)
                    throw new LidgrenException("MaximumHandshakeAttempts must be at least 1.");
                _maximumHandshakeAttempts = value;
            }
        }

        /// <summary>
        /// Gets or sets if the <see cref="NetPeer"/> should send large messages
        /// to try to expand the maximum transmission unit size.
        /// </summary>
        public bool AutoExpandMTU
        {
            get => _autoExpandMTU;
            set
            {
                if (_isLocked)
                    throw new LidgrenException(IsLockedMessage);
                _autoExpandMTU = value;
            }
        }

        /// <summary>
        /// Gets or sets how often to send large messages
        /// to expand MTU if <see cref="AutoExpandMTU"/> is enabled.
        /// </summary>
        public TimeSpan ExpandMTUFrequency
        {
            get => _expandMTUFrequency;
            set => _expandMTUFrequency = value;
        }

        /// <summary>
        /// Gets or sets the number of failed expand MTU attempts to perform before setting final MTU.
        /// </summary>
        public int ExpandMTUFailAttempts
        {
            get => _expandMTUFailAttempts;
            set => _expandMTUFailAttempts = value;
        }

        /// <summary>
        /// Gets or sets the simulated amount of sent packets lost from 0.0 to 1.0.
        /// </summary>
        public float SimulatedLoss
        {
            get => _loss;
            set => _loss = value;
        }

        /// <summary>
        /// Gets or sets the minimum simulated amount of one way latency for sent packets in seconds.
        /// </summary>
        public TimeSpan SimulatedMinimumLatency
        {
            get => _minimumOneWayLatency;
            set => _minimumOneWayLatency = value;
        }

        /// <summary>
        /// Gets or sets the simulated added random amount of one way latency for sent packets in seconds.
        /// </summary>
        public TimeSpan SimulatedRandomLatency
        {
            get => _randomOneWayLatency;
            set => _randomOneWayLatency = value;
        }

        /// <summary>
        /// Gets the average simulated one way latency in seconds.
        /// </summary>
        public TimeSpan SimulatedAverageLatency => _minimumOneWayLatency + (_randomOneWayLatency * 0.5);

        /// <summary>
        /// Gets or sets the simulated amount of duplicated packets from 0.0 to 1.0.
        /// </summary>
        public float SimulatedDuplicatesChance
        {
            get => _duplicates;
            set => _duplicates = value;
        }

        /// <summary>
        /// Creates a memberwise shallow clone of this configuration.
        /// </summary>
        public NetPeerConfiguration Clone()
        {
            var retval = (NetPeerConfiguration)MemberwiseClone();
            retval._isLocked = false;
            return retval;
        }
    }
}
