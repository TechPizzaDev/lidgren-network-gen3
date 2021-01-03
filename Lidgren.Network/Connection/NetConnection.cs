using System;
using System.Net;
using System.Diagnostics;
using System.Collections.Generic;

namespace Lidgren.Network
{
    /// <summary>
    /// Represents a connection to a remote peer.
    /// </summary>
    [DebuggerDisplay("{DebuggerDisplay}")]
    public partial class NetConnection : IDisposable
    {
        /// <summary>
        /// Number of heartbeats to skip checking for infrequent events (ping, timeout etc).
        /// </summary>
        private const int InfrequentEventsSkipFrames = 8;

        /// <summary>
        /// Number of heartbeats to wait for more incoming messages before sending packet.
        /// </summary>
        private const int MessageCoalesceFrames = 4;

        private bool _isDisposed;
        private int _sendBufferWritePtr;
        private int _sendBufferNumMessages;
        internal NetPeerConfiguration _peerConfiguration;
        internal NetSenderChannel[] _sendChannels;
        internal NetReceiverChannel[] _receiveChannels;
        internal NetStream?[] _openStreams;
        internal NetQueue<(NetMessageType Type, int SequenceNumber)> _queuedOutgoingAcks;
        internal NetQueue<(NetMessageType Type, int SequenceNumber)> _queuedIncomingAcks;

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

        internal NetConnection(NetPeer peer, IPEndPoint remoteEndPoint)
        {
            Peer = peer;
            _peerConfiguration = Peer.Configuration;
            Status = NetConnectionStatus.None;
            RemoteEndPoint = remoteEndPoint;
            _sendChannels = new NetSenderChannel[NetConstants.TotalChannels];
            _receiveChannels = new NetReceiverChannel[NetConstants.TotalChannels];
            _openStreams = new NetStream?[NetConstants.StreamChannels];
            _queuedOutgoingAcks = new NetQueue<(NetMessageType, int)>(16);
            _queuedIncomingAcks = new NetQueue<(NetMessageType, int)>(16);
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

        internal void SetStatus(NetConnectionStatus status, string? reason = null)
        {
            // user or library thread

            if (status == Status)
                return;
            Status = status;

            if (reason == null)
                reason = string.Empty;

            if (Status == NetConnectionStatus.Connected)
            {
                _timeoutDeadline = NetTime.Now + _peerConfiguration._connectionTimeout;
                Peer.LogVerbose("Timeout deadline initialized to  " + _timeoutDeadline);
            }

            if (_peerConfiguration.IsMessageTypeEnabled(NetIncomingMessageType.StatusChanged))
            {
                NetIncomingMessage info = Peer.CreateIncomingMessage(NetIncomingMessageType.StatusChanged);
                info.EnsureCapacity(4 + reason.Length + (reason.Length > 126 ? 2 : 1));
                info.SenderConnection = this;
                info.SenderEndPoint = RemoteEndPoint;
                info.Write(Status);
                info.Write(reason);
                Peer.ReleaseMessage(info);
            }
        }

        internal void Heartbeat(TimeSpan now, uint frameCounter)
        {
            Peer.AssertIsOnLibraryThread();

            LidgrenException.Assert(
                Status != NetConnectionStatus.InitiatedConnect &&
                Status != NetConnectionStatus.RespondedConnect);

            if ((frameCounter % InfrequentEventsSkipFrames) == 0)
            {
                if (now > _timeoutDeadline)
                {
                    //
                    // connection timed out
                    //
                    Peer.LogVerbose("Connection timed out at " + now + " deadline was " + _timeoutDeadline);
                    ExecuteDisconnect("Connection timed out", true);
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

            if ((frameCounter % MessageCoalesceFrames) == 0) // coalesce a few frames
            {
                byte[] sendBuffer = Peer._sendBuffer;
                int mtu = CurrentMTU;

                //
                // send ack messages
                //
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
                        Peer.SendPacket(_sendBufferWritePtr, RemoteEndPoint, _sendBufferNumMessages, out _);
                        Statistics.PacketSent(_sendBufferWritePtr, 1);
                        _sendBufferWritePtr = 0;
                        _sendBufferNumMessages = 0;
                    }
                }

                //
                // Parse incoming acks (may trigger resends)
                //
                while (_queuedIncomingAcks.TryDequeue(out var incAck))
                {
                    //m_peer.LogVerbose("Received ack for " + acktp + "#" + seqNr);
                    NetSenderChannel channel = _sendChannels[(int)incAck.Type - 1];

                    // If we haven't sent a message on this channel there is no reason to ack it
                    if (channel == null)
                        continue;

                    channel.ReceiveAcknowledge(now, incAck.SequenceNumber);
                }
            }

            //
            // send queued messages
            //
            if (Peer._executeFlushSendQueue)
            {
                // Reverse order so reliable messages are sent first
                for (int i = _sendChannels.Length - 1; i >= 0; i--)
                {
                    var channel = _sendChannels[i];
                    LidgrenException.Assert(_sendBufferWritePtr < 1 || _sendBufferNumMessages > 0);
                    channel?.SendQueuedMessages(now);
                    LidgrenException.Assert(_sendBufferWritePtr < 1 || _sendBufferNumMessages > 0);
                }
            }

            //
            // Put on wire data has been written to send buffer but not yet sent
            //
            if (_sendBufferWritePtr > 0)
            {
                Peer.AssertIsOnLibraryThread();
                LidgrenException.Assert(_sendBufferWritePtr > 0 && _sendBufferNumMessages > 0);
                Peer.SendPacket(_sendBufferWritePtr, RemoteEndPoint, _sendBufferNumMessages, out _);
                Statistics.PacketSent(_sendBufferWritePtr, _sendBufferNumMessages);
                _sendBufferWritePtr = 0;
                _sendBufferNumMessages = 0;
            }
        }

        // Queue an item for immediate sending on the wire
        // This method is called from the ISenderChannels
        internal void QueueSendMessage(NetOutgoingMessage om, int seqNr)
        {
            Peer.AssertIsOnLibraryThread();

            int size = om.GetEncodedSize();

            // can fit this message together with previously written to buffer?
            if (_sendBufferWritePtr + size > CurrentMTU)
            {
                if (_sendBufferWritePtr > 0 && _sendBufferNumMessages > 0)
                {
                    // previous message in buffer; send these first
                    Peer.SendPacket(_sendBufferWritePtr, RemoteEndPoint, _sendBufferNumMessages, out _);
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
                Peer.SendPacket(_sendBufferWritePtr, RemoteEndPoint, _sendBufferNumMessages, out _);
                Statistics.PacketSent(_sendBufferWritePtr, _sendBufferNumMessages);
                _sendBufferWritePtr = 0;
                _sendBufferNumMessages = 0;
            }
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

        // called by SendMessage() and NetPeer.SendMessage; ie. may be user thread
        internal NetSendResult EnqueueMessage(
            NetOutgoingMessage message, NetDeliveryMethod method, int sequenceChannel)
        {
            if (Status != NetConnectionStatus.Connected)
                return NetSendResult.FailedNotConnected;

            var type = (NetMessageType)((int)method + sequenceChannel);
            message._messageType = type;

            // TODO: do we need to make this more thread safe?
            int channelSlot = (int)method - 1 + sequenceChannel;
            NetSenderChannel chan = _sendChannels[channelSlot];
            if (chan == null)
                chan = CreateSenderChannel(type);

            if (method != NetDeliveryMethod.Unreliable &&
                method != NetDeliveryMethod.UnreliableSequenced &&
                message.GetEncodedSize() > CurrentMTU)
                Peer.ThrowOrLog("Reliable message too large! Fragmentation failure?");

            var sendResult = chan.Enqueue(message);
            //if (sendResult == NetSendResult.Sent && !_peerConfiguration.m_autoFlushSendQueue)
            //	sendResult = NetSendResult.Queued; // queued since we're not autoflushing
            return sendResult;
        }

        // may be on user thread
        private NetSenderChannel CreateSenderChannel(NetMessageType type)
        {
            lock (_sendChannels)
            {
                NetDeliveryMethod method = NetUtility.GetDeliveryMethod(type);
                int sequenceChannel = (int)type - (int)method;
                int channelSlot = (int)method - 1 + sequenceChannel;

                if (_sendChannels[channelSlot] != null)
                {
                    // we were pre-empted by another call to this method
                    return _sendChannels[channelSlot];
                }
                else
                {
                    NetSenderChannel channel;
                    switch (method)
                    {
                        case NetDeliveryMethod.Unreliable:
                        case NetDeliveryMethod.UnreliableSequenced:
                            channel = new NetUnreliableSenderChannel(this, NetUtility.GetWindowSize(method));
                            break;

                        case NetDeliveryMethod.ReliableUnordered:
                        case NetDeliveryMethod.ReliableSequenced:
                        case NetDeliveryMethod.ReliableOrdered:
                        case NetDeliveryMethod.Stream:
                            channel = new NetReliableSenderChannel(this, NetUtility.GetWindowSize(method));
                            break;

                        default:
                            throw new ArgumentOutOfRangeException(nameof(type));
                    }
                    _sendChannels[channelSlot] = channel;
                    return channel;
                }
            }
        }

        // received a library message while Connected
        internal void ReceivedLibraryMessage(NetMessageType type, int offset, int payloadLength)
        {
            Peer.AssertIsOnLibraryThread();

            var now = NetTime.Now;

            switch (type)
            {
                case NetMessageType.Connect:
                    Peer.LogDebug("Received handshake message (" + type + ") despite connection being in place");
                    break;

                case NetMessageType.ConnectResponse:
                    // handshake message must have been lost
                    HandleConnectResponse(offset, payloadLength);
                    break;

                case NetMessageType.ConnectionEstablished:
                    // do nothing, all's well
                    break;

                case NetMessageType.LibraryError:
                    Peer.ThrowOrLog(
                        "LibraryError received by ReceivedMessageCore; this usually indicates a malformed message");
                    break;

                case NetMessageType.Disconnect:
                    NetIncomingMessage msg = Peer.SetupReadHelperMessage(offset, payloadLength);

                    _disconnectRequested = true;
                    _disconnectMessage = msg.ReadString();
                    _disconnectReqSendBye = false;
                    //ExecuteDisconnect(msg.ReadString(), false);
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
                        Peer.LogDebug("Received ExpandMTURequest altho AutoExpandMTU is turned off!");
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
                    Peer.LogWarning("Connection received unhandled library message: " + type);
                    break;
            }
        }

        internal void ReceivedMessage(NetIncomingMessage msg)
        {
            Peer.AssertIsOnLibraryThread();

            NetMessageType type = msg._baseMessageType;

            int channelSlot = (int)type - 1;
            NetReceiverChannel channel = _receiveChannels[channelSlot];
            if (channel == null)
                channel = CreateReceiverChannel(type);

            channel.ReceiveMessage(msg);
        }

        private NetReceiverChannel CreateReceiverChannel(NetMessageType type)
        {
            NetDeliveryMethod method = NetUtility.GetDeliveryMethod(type);
            NetReceiverChannel channel = method switch
            {
                NetDeliveryMethod.Unreliable => new NetUnreliableUnorderedReceiver(this),
                NetDeliveryMethod.UnreliableSequenced => new NetUnreliableSequencedReceiver(this),
                NetDeliveryMethod.ReliableUnordered => new NetReliableUnorderedReceiver(this, NetConstants.ReliableOrderedWindowSize),
                NetDeliveryMethod.ReliableSequenced => new NetReliableSequencedReceiver(this, NetConstants.ReliableSequencedWindowSize),
                NetDeliveryMethod.ReliableOrdered => new NetReliableOrderedReceiver(this, NetConstants.ReliableOrderedWindowSize),
                NetDeliveryMethod.Stream => new NetReliableOrderedReceiver(this, NetConstants.StreamWindowSize),
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
            var chan = _sendChannels[channelSlot];
            if (chan == null)
            {
                windowSize = NetUtility.GetWindowSize(method);
                freeWindowSlots = windowSize;
                return;
            }

            windowSize = chan.WindowSize;
            freeWindowSlots = chan.GetAllowedSends() - chan.QueuedSends.Count;
            return;
        }

        public bool CanSendImmediately(NetDeliveryMethod method, int sequenceChannel)
        {
            int channelSlot = (int)method - 1 + sequenceChannel;
            var chan = _sendChannels[channelSlot];
            if (chan == null)
                return true;
            return (chan.GetAllowedSends() - chan.QueuedSends.Count) > 0;
        }

        internal void Shutdown(string? reason)
        {
            ExecuteDisconnect(reason, true);
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
