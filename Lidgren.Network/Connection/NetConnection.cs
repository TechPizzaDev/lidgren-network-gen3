using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net;
using System.Threading.Tasks;

namespace Lidgren.Network
{
    /// <summary>
    /// Represents a connection to a remote peer.
    /// </summary>
    [DebuggerDisplay("{DebuggerDisplay}")]
    public partial class NetConnection : IDisposable
    {
        public delegate void ConnectionStatusChanged(
            NetConnection connection, NetConnectionStatus status, NetOutgoingMessage? reason);

        /// <summary>
        /// Number of heartbeats to skip checking for infrequent events (ping, timeout etc).
        /// </summary>
        private const uint InfrequentEventsSkipFrames = 8;

        /// <summary>
        /// Number of heartbeats to wait for more incoming messages before sending packet.
        /// </summary>
        private const uint MessageCoalesceFrames = 4;

        /// <summary>
        /// Number of heartbeats to wait before trying to reclaim fragments.
        /// </summary>
        private const uint ReclaimFragmentsFrames = 128;

        private bool _isDisposed;
        private int _sendBufferWritePtr;
        private int _sendBufferNumMessages;
        internal NetPeerConfiguration _peerConfiguration;
        internal NetSenderChannel?[] _sendChannels = new NetSenderChannel[NetConstants.TotalChannels];
        internal NetReceiverChannel?[] _receiveChannels = new NetReceiverChannel[NetConstants.TotalChannels];
        internal NetQueue<(NetMessageType Type, int SequenceNumber)> _queuedOutgoingAcks = new(16);
        internal NetQueue<(NetMessageType Type, int SequenceNumber)> _queuedIncomingAcks = new(16);
        internal Dictionary<int, ReceivedNormalFragmentGroup> _receivedNormalFragmentGroups = new();
        internal Dictionary<int, ReceivedStreamFragmentGroup> _receivedStreamFragmentGroups = new();
        internal ConcurrentQueue<PipeReader> _openedStreamGroups = new();
        private List<int> _fragmentGroupsToDrop = new();

        internal string DebuggerDisplay =>
            $"RemoteUniqueIdentifier = {RemoteUniqueIdentifier}, RemoteEndPoint = {RemoteEndPoint}";

        /// <summary>
        /// Gets the peer which holds this connection.
        /// </summary>
        public NetPeer Peer { get; }

        /// <summary>
        /// Gets or sets the application defined object containing data about the connection.
        /// </summary>
        public object? Tag { get; set; }

        /// <summary>
        /// Gets the current status of the connection.
        /// </summary>
        public NetConnectionStatus Status { get; internal set; }

        /// <summary>
        /// Gets various statistics for this connection.
        /// </summary>
        public NetConnectionStatistics Statistics { get; private set; }

        /// <summary>
        /// Gets the remote endpoint for the connection.
        /// </summary>
        public IPEndPoint RemoteEndPoint { get; private set; }

        /// <summary>
        /// Gets the unique identifier of the remote <see cref="NetPeer"/> for this connection.
        /// </summary>
        public long RemoteUniqueIdentifier { get; private set; }

        /// <summary>
        /// Gets the local hail message that was sent as part of the handshake.
        /// </summary>
        public NetOutgoingMessage? LocalHailMessage { get; internal set; }

        /// <summary>
        /// Gets the time before automatically resending an unacked message.
        /// </summary>
        public TimeSpan ResendDelay
        {
            get
            {
                var avgRtt = AverageRoundtripTime;
                if (avgRtt <= TimeSpan.Zero)
                    avgRtt = TimeSpan.FromSeconds(0.1); // "default" resend is based on 100 ms roundtrip time
                return TimeSpan.FromMilliseconds(25) + (avgRtt * 2.1); // 25 ms + double rtt
            }
        }

        public event ConnectionStatusChanged? StatusChanged;

        internal NetConnection(NetPeer peer, IPEndPoint remoteEndPoint)
        {
            Peer = peer;
            _peerConfiguration = Peer.Configuration;
            Status = NetConnectionStatus.None;
            RemoteEndPoint = remoteEndPoint;
            Statistics = new NetConnectionStatistics(this);
            AverageRoundtripTime = default;
            CurrentMTU = _peerConfiguration.MaximumTransmissionUnit;
        }

        /// <summary>
        /// Change the internal endpoint to this new one.
        /// Used when, during handshake, a switch in port is detected (due to NAT).
        /// </summary>
        internal void MutateEndPoint(IPEndPoint endPoint)
        {
            RemoteEndPoint = endPoint;
        }

        internal void SetStatus(NetConnectionStatus status, NetOutgoingMessage? reason = null)
        {
            // user or library thread

            if (status == Status)
                return;
            Status = status;

            if (Status == NetConnectionStatus.Connected)
            {
                _timeoutDeadline = NetTime.Now + _peerConfiguration._connectionTimeout;
                Peer.LogVerbose(NetLogMessage.FromTime(NetLogCode.DeadlineTimeoutInitialized, time: _timeoutDeadline));
            }

            StatusChanged?.Invoke(this, Status, reason);

            if (_peerConfiguration.IsMessageTypeEnabled(NetIncomingMessageType.StatusChanged))
            {
                NetIncomingMessage info = Peer.CreateIncomingMessage(NetIncomingMessageType.StatusChanged);
                info.SenderConnection = this;
                info.SenderEndPoint = RemoteEndPoint;
                info.Write(Status);
                Peer.ReleaseMessage(info);
            }
        }

        internal void Heartbeat(TimeSpan now, uint frameCounter)
        {
            Peer.AssertIsOnLibraryThread();

            LidgrenException.Assert(
                Status != NetConnectionStatus.InitiatedConnect &&
                Status != NetConnectionStatus.RespondedConnect);

            if (NetUtility.PowOf2Mod(frameCounter, ReclaimFragmentsFrames) == 0)
            {
                foreach ((int groupId, ReceivedNormalFragmentGroup group) in _receivedNormalFragmentGroups)
                {
                    TimeSpan idleTime = now - group.LastReceived;
                    if (idleTime >= Peer.Configuration.FragmentGroupTimeout)
                    {
                        _fragmentGroupsToDrop.Add(groupId);

                        Peer.LogWarning(NetLogMessage.FromTime(NetLogCode.FragmentGroupTimeout,
                            endPoint: this, time: idleTime));
                    }
                }

                foreach (int groupId in _fragmentGroupsToDrop)
                {
                    if (_receivedNormalFragmentGroups.Remove(groupId, out ReceivedNormalFragmentGroup? group))
                    {
                        // TODO: recycle?
                    }
                }
                _fragmentGroupsToDrop.Clear();
            }

            if (NetUtility.PowOf2Mod(frameCounter, InfrequentEventsSkipFrames) == 0)
            {
                if (now > _timeoutDeadline)
                {
                    Peer.LogVerbose(NetLogMessage.FromTime(NetLogCode.ConnectionTimedOut, time: now));
                    ExecuteDisconnect(NetMessageType.TimedOut);
                    return;
                }

                // send ping?
                if (Status == NetConnectionStatus.Connected)
                {
                    if (now > _sentPingTime + Peer.Configuration._pingInterval)
                        SendPing();

                    // handle expand mtu
                    MTUExpansionHeartbeat(now);
                }

                if (_disconnectRequested)
                {
                    ExecuteDisconnect(_disconnectMessage, _disconnectReqSendBye);
                    return;
                }
            }

            // Note: at this point m_sendBufferWritePtr and m_sendBufferNumMessages may be non-null;
            // resends may already be queued up

            if (NetUtility.PowOf2Mod(frameCounter, MessageCoalesceFrames) == 0) // coalesce a few frames
            {
                byte[] sendBuffer = Peer._sendBuffer;
                int mtu = CurrentMTU;

                // send ack messages
                while (_queuedOutgoingAcks.Count > 0)
                {
                    int acks = (mtu - (_sendBufferWritePtr + 5)) / 3; // 3 bytes per actual ack
                    if (acks > _queuedOutgoingAcks.Count)
                        acks = _queuedOutgoingAcks.Count;

                    LidgrenException.Assert(acks > 0);

                    _sendBufferNumMessages++;

                    // write acks header
                    sendBuffer[_sendBufferWritePtr++] = (byte)NetMessageType.Acknowledge;
                    sendBuffer[_sendBufferWritePtr++] = 0; // no sequence number
                    sendBuffer[_sendBufferWritePtr++] = 0; // no sequence number
                    int len = acks * 3 * 8; // bits
                    sendBuffer[_sendBufferWritePtr++] = (byte)len;
                    sendBuffer[_sendBufferWritePtr++] = (byte)(len >> 8);

                    // write acks
                    for (int i = 0; i < acks; i++)
                    {
                        _queuedOutgoingAcks.TryDequeue(out var ack);

                        //m_peer.LogVerbose("Sending ack for " + tuple.Item1 + "#" + tuple.Item2);

                        sendBuffer[_sendBufferWritePtr++] = (byte)ack.Type;
                        sendBuffer[_sendBufferWritePtr++] = (byte)ack.SequenceNumber;
                        sendBuffer[_sendBufferWritePtr++] = (byte)(ack.SequenceNumber >> 8);
                    }

                    if (_queuedOutgoingAcks.Count > 0)
                    {
                        // send packet and go for another round of acks
                        LidgrenException.Assert(_sendBufferWritePtr > 0 && _sendBufferNumMessages > 0);
                        Peer.SendPacket(_sendBufferWritePtr, RemoteEndPoint, _sendBufferNumMessages);
                        Statistics.PacketSent(_sendBufferWritePtr, 1);
                        _sendBufferWritePtr = 0;
                        _sendBufferNumMessages = 0;
                    }
                }

                // Parse incoming acks (may trigger resends)
                while (_queuedIncomingAcks.TryDequeue(out (NetMessageType Type, int SequenceNumber) incAck))
                {
                    //m_peer.LogVerbose("Received ack for " + acktp + "#" + seqNr);
                    NetSenderChannel? channel = _sendChannels[(int)incAck.Type - 1];

                    // If we haven't sent a message on this channel there is no reason to ack it
                    if (channel == null)
                        continue;

                    var receiveResult = channel.ReceiveAcknowledge(now, incAck.SequenceNumber);
                    if (!receiveResult.Success)
                    {
                        _queuedIncomingAcks.EnqueueFirst(incAck);
                        break;
                    }
                }
            }

            // send queued messages
            if (Peer._executeFlushSendQueue)
            {
                // Reverse order so reliable messages are sent first
                for (int i = _sendChannels.Length; i-- > 0;)
                {
                    NetSenderChannel? channel = _sendChannels[i];
                    LidgrenException.Assert(_sendBufferWritePtr < 1 || _sendBufferNumMessages > 0);
                    if (channel != null)
                    {
                        channel.SendQueuedMessages(now);
                    }
                    LidgrenException.Assert(_sendBufferWritePtr < 1 || _sendBufferNumMessages > 0);
                }
            }

            // Put on wire data has been written to send buffer but not yet sent
            if (_sendBufferWritePtr > 0)
            {
                LidgrenException.Assert(_sendBufferWritePtr > 0 && _sendBufferNumMessages > 0);
                Peer.SendPacket(_sendBufferWritePtr, RemoteEndPoint, _sendBufferNumMessages);
                Statistics.PacketSent(_sendBufferWritePtr, _sendBufferNumMessages);
                _sendBufferWritePtr = 0;
                _sendBufferNumMessages = 0;
            }
        }

        // Queue an item for immediate sending on the wire
        // This method is called from the ISenderChannels
        internal NetSocketResult QueueSendMessage(NetOutgoingMessage om, int seqNr)
        {
            Peer.AssertIsOnLibraryThread();

            int size = om.GetEncodedSize();

            // can fit this message together with previously written to buffer?
            if (_sendBufferWritePtr + size > CurrentMTU)
            {
                if (_sendBufferWritePtr > 0 && _sendBufferNumMessages > 0)
                {
                    // previous message in buffer; send these first
                    var sendResult = Peer.SendPacket(_sendBufferWritePtr, RemoteEndPoint, _sendBufferNumMessages);
                    if (!sendResult.Success)
                        return sendResult;

                    Statistics.PacketSent(_sendBufferWritePtr, _sendBufferNumMessages);
                    _sendBufferWritePtr = 0;
                    _sendBufferNumMessages = 0;
                }
            }

            // encode it into buffer regardless if it (now) fits within MTU or not
            om.Encode(Peer._sendBuffer, ref _sendBufferWritePtr, seqNr);
            _sendBufferNumMessages++;

            if (_sendBufferWritePtr > CurrentMTU)
            {
                // send immediately; we're already over MTU
                var sendResult = Peer.SendPacket(_sendBufferWritePtr, RemoteEndPoint, _sendBufferNumMessages);
                if (!sendResult.Success)
                    return sendResult;

                Statistics.PacketSent(_sendBufferWritePtr, _sendBufferNumMessages);
                _sendBufferWritePtr = 0;
                _sendBufferNumMessages = 0;
            }

            return new NetSocketResult(true, false);
        }

        public bool TryDequeueDataStream([MaybeNullWhen(false)] out PipeReader reader)
        {
            return _openedStreamGroups.TryDequeue(out reader);
        }

        /// <summary>
        /// Send a message to this remote connection.
        /// </summary>
        /// <param name="message">The message to send</param>
        /// <param name="method">How to deliver the message</param>
        /// <param name="sequenceChannel">Sequence channel within the delivery method</param>
        public NetSendResult SendMessage(NetOutgoingMessage message, NetDeliveryMethod method, int sequenceChannel)
        {
            return Peer.SendMessage(message, this, method, sequenceChannel);
        }

        /// <summary>
        /// Stream a message to this remote connection.
        /// </summary>
        /// <param name="message">The message to stream.</param>
        /// <param name="method">How to deliver the message</param>
        /// <param name="sequenceChannel">Sequence channel within the delivery method</param>
        public async ValueTask<NetSendResult> StreamMessageAsync(PipeReader message, int sequenceChannel)
        {
            List<NetConnection> recipientList = NetConnectionListPool.Rent(1);
            try
            {
                recipientList.Add(this);
                return await Peer.StreamMessageAsync(message, recipientList, sequenceChannel);
            }
            finally
            {
                NetConnectionListPool.Return(recipientList);
            }
        }

        // called by SendMessage() and NetPeer.SendMessage; ie. may be user thread
        internal (NetSendResult Result, NetSenderChannel? Channel) EnqueueMessage(
            NetOutgoingMessage message, NetDeliveryMethod method, int sequenceChannel)
        {
            if (Status != NetConnectionStatus.Connected)
                return (NetSendResult.FailedNotConnected, null);

            var type = (NetMessageType)((int)method + sequenceChannel);
            message._messageType = type;

            // TODO: do we need to make this more thread safe?
            int channelSlot = (int)method - 1 + sequenceChannel;
            NetSenderChannel? chan = _sendChannels[channelSlot];
            if (chan == null)
                chan = CreateSenderChannel(type);

            if (method != NetDeliveryMethod.Unreliable &&
                method != NetDeliveryMethod.UnreliableSequenced &&
                message.GetEncodedSize() > CurrentMTU)
                Peer.ThrowOrLog("Reliable message too large! Fragmentation failure?");

            NetSendResult sendResult = chan.Enqueue(message);
            return (sendResult, chan);
        }

        // may be on user thread
        private NetSenderChannel CreateSenderChannel(NetMessageType type)
        {
            lock (_sendChannels)
            {
                NetDeliveryMethod method = NetUtility.GetDeliveryMethod(type);
                int sequenceChannel = (int)type - (int)method;
                int channelSlot = (int)method - 1 + sequenceChannel;

                NetSenderChannel? channel = _sendChannels[channelSlot];
                if (channel != null)
                {
                    // we were pre-empted by another call to this method
                    return channel;
                }
                else
                {
                    channel = method switch
                    {
                        NetDeliveryMethod.Unreliable or
                        NetDeliveryMethod.UnreliableSequenced =>
                        new NetUnreliableSenderChannel(this, NetUtility.GetWindowSize(method), method),

                        NetDeliveryMethod.ReliableUnordered or
                        NetDeliveryMethod.ReliableSequenced or
                        NetDeliveryMethod.ReliableOrdered =>
                        new NetReliableSenderChannel(this, NetUtility.GetWindowSize(method)),

                        _ => throw new ArgumentOutOfRangeException(nameof(type)),
                    };
                    _sendChannels[channelSlot] = channel;
                    return channel;
                }
            }
        }

        // received a library message while Connected
        internal void ReceivedLibraryMessage(NetMessageType type, int offset, int payloadLength)
        {
            Peer.AssertIsOnLibraryThread();

            TimeSpan now = NetTime.Now;

            switch (type)
            {
                case NetMessageType.Connect:
                    Peer.LogDebug(new NetLogMessage(NetLogCode.UnexpectedConnect));
                    break;

                case NetMessageType.ConnectResponse:
                    // handshake message must have been lost
                    HandleConnectResponse(offset, payloadLength);
                    break;

                case NetMessageType.ConnectionEstablished:
                    // do nothing, all's well
                    break;

                case NetMessageType.LibraryError:
                    Peer.LogWarning(new NetLogMessage(NetLogCode.UnexpectedLibraryError, null, this));
                    break;

                case NetMessageType.InvalidHandshake:
                case NetMessageType.WrongAppIdentifier:
                case NetMessageType.ConnectTimedOut:
                case NetMessageType.TimedOut:
                case NetMessageType.Disconnect:
                    NetOutgoingMessage msg = Peer.CreateReadHelperOutMessage(offset, payloadLength);
                    msg._messageType = type;
                    _disconnectMessage = msg;
                    _disconnectReqSendBye = false;
                    _disconnectRequested = true;
                    //ExecuteDisconnect(msg, false);
                    break;

                case NetMessageType.Acknowledge:
                    for (int i = 0; i < payloadLength; i += 3)
                    {
                        var ackType = (NetMessageType)Peer._receiveBuffer[offset++]; // netmessagetype
                        int seqNr = Peer._receiveBuffer[offset++];
                        seqNr |= Peer._receiveBuffer[offset++] << 8;

                        // need to enqueue this and handle it in the netconnection heartbeat;
                        // so be able to send resends together with normal sends
                        _queuedIncomingAcks.Enqueue((ackType, seqNr));
                    }
                    break;

                case NetMessageType.Ping:
                    byte pingNr = Peer._receiveBuffer[offset++];
                    SendPong(pingNr);
                    break;

                case NetMessageType.Pong:
                    NetIncomingMessage pmsg = Peer.SetupReadHelperMessage(offset, payloadLength);
                    byte pongNr = pmsg.ReadByte();
                    var remoteSendTime = pmsg.ReadTimeSpan();
                    ReceivedPong(now, pongNr, remoteSendTime);
                    break;

                case NetMessageType.ExpandMTURequest:
                    SendMTUSuccess(payloadLength);
                    break;

                case NetMessageType.ExpandMTUSuccess:
                    if (Peer.Configuration.AutoExpandMTU == false)
                    {
                        Peer.LogDebug(new NetLogMessage(NetLogCode.UnexpectedMTUExpandRequest));
                        break;
                    }
                    NetIncomingMessage emsg = Peer.SetupReadHelperMessage(offset, payloadLength);
                    int size = emsg.ReadInt32();
                    HandleExpandMTUSuccess(now, size);
                    break;

                case NetMessageType.NatIntroduction:
                    // Unusual situation where server is actually already known,
                    // but got a nat introduction - oh well, lets handle it as usual
                    Peer.HandleNatIntroduction(offset);
                    break;

                default:
                    Peer.LogWarning(NetLogMessage.FromValues(NetLogCode.UnhandledLibraryMessage,
                        endPoint: this, value: (int)type));
                    break;
            }
        }

        internal void ReceivedMessage(in NetMessageView message)
        {
            Peer.AssertIsOnLibraryThread();

            NetMessageType type = message.BaseMessageType;

            int channelSlot = (int)type - 1;
            NetReceiverChannel? channel = _receiveChannels[channelSlot];
            if (channel == null)
                channel = CreateReceiverChannel(type);

            channel.ReceiveMessage(message);
        }

        private NetReceiverChannel CreateReceiverChannel(NetMessageType type)
        {
            NetDeliveryMethod method = NetUtility.GetDeliveryMethod(type);
            NetReceiverChannel channel = method switch
            {
                NetDeliveryMethod.Unreliable =>
                new NetUnreliableUnorderedReceiver(this),

                NetDeliveryMethod.UnreliableSequenced =>
                new NetUnreliableSequencedReceiver(this),

                NetDeliveryMethod.ReliableUnordered =>
                new NetReliableUnorderedReceiver(this, NetConstants.ReliableOrderedWindowSize),

                NetDeliveryMethod.ReliableSequenced =>
                new NetReliableSequencedReceiver(this, NetConstants.ReliableSequencedWindowSize),

                NetDeliveryMethod.ReliableOrdered =>
                new NetReliableOrderedReceiver(this, NetConstants.ReliableOrderedWindowSize),

                _ => throw new ArgumentOutOfRangeException(nameof(type)),
            };

            int channelSlot = (int)type - 1;
            LidgrenException.Assert(_receiveChannels[channelSlot] == null);
            _receiveChannels[channelSlot] = channel;

            return channel;
        }

        internal void QueueAck(NetMessageType type, int sequenceNumber)
        {
            _queuedOutgoingAcks.Enqueue((type, sequenceNumber));
        }

        /// <summary>
        /// Zero <paramref name="windowSize"/> indicates that the channel is not yet been instantiated (used).
        /// Negative <paramref name="freeWindowSlots"/> means this amount of 
        /// messages are currently queued but delayed due to closed window.
        /// </summary>
        public void GetSendQueueInfo(
            NetDeliveryMethod method, int sequenceChannel, out int windowSize, out int freeWindowSlots)
        {
            int channelSlot = (int)method - 1 + sequenceChannel;
            NetSenderChannel? chan = _sendChannels[channelSlot];
            if (chan == null)
            {
                windowSize = NetUtility.GetWindowSize(method);
                freeWindowSlots = windowSize;
                return;
            }

            windowSize = chan.WindowSize;
            freeWindowSlots = chan.GetFreeWindowSlots();
            return;
        }

        public bool CanSendImmediately(NetDeliveryMethod method, int sequenceChannel)
        {
            int channelSlot = (int)method - 1 + sequenceChannel;
            NetSenderChannel? chan = _sendChannels[channelSlot];
            if (chan == null)
                return true;
            return chan.GetFreeWindowSlots() > 0;
        }

        internal void Shutdown(NetOutgoingMessage? reason)
        {
            _disconnectMessage ??= reason;
            ExecuteDisconnect(_disconnectMessage, true);
        }

        /// <summary>
        /// Returns a <see cref="string"/> that represents this object.
        /// </summary>
        public override string ToString()
        {
            return "{NetConnection: @" + RemoteEndPoint + "}";
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _queuedIncomingAcks?.Dispose();
                    _queuedOutgoingAcks?.Dispose();
                }
                _isDisposed = true;
            }
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
