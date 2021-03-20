using System;
using System.Threading;

namespace Lidgren.Network
{
    public partial class NetConnection
    {
        internal bool _connectRequested;
        internal bool _disconnectRequested;
        internal bool _disconnectReqSendBye;
        internal NetOutgoingMessage? _disconnectMessage;
        internal bool _connectionInitiator;
        internal TimeSpan _lastHandshakeSendTime;
        internal int _handshakeAttempts;

        /// <summary>
        /// The message that the remote part specified via
        /// <see cref="NetPeer"/>.Connect or <see cref="NetPeer"/>.Approve.
        /// </summary>
        public NetIncomingMessage? RemoteHailMessage { get; internal set; }

        // heartbeat called when connection still is in m_handshakes of NetPeer
        internal void UnconnectedHeartbeat(TimeSpan now)
        {
            Peer.AssertIsOnLibraryThread();

            if (_disconnectRequested)
            {
                ExecuteDisconnect(_disconnectMessage, true);
            }

            if (_connectRequested)
            {
                switch (Status)
                {
                    case NetConnectionStatus.Connected:
                    case NetConnectionStatus.RespondedConnect:
                        // reconnect
                        ExecuteDisconnect(NetMessageType.ConnectTimedOut);
                        break;

                    case NetConnectionStatus.InitiatedConnect:
                        // send another connect attempt
                        SendConnect(now);
                        break;

                    case NetConnectionStatus.Disconnected:
                        Peer.ThrowOrLog("This connection is Disconnected; spent. A new one should have been created");
                        break;

                    case NetConnectionStatus.Disconnecting:
                        // let disconnect finish first
                        break;

                    case NetConnectionStatus.None:
                    default:
                        SendConnect(now);
                        break;
                }
                return;
            }

            if (now - _lastHandshakeSendTime > _peerConfiguration._resendHandshakeInterval)
            {
                if (_handshakeAttempts >= _peerConfiguration._maximumHandshakeAttempts)
                {
                    // failed to connect
                    ExecuteDisconnect(NetMessageType.ConnectTimedOut);
                    return;
                }

                // resend handshake
                switch (Status)
                {
                    case NetConnectionStatus.InitiatedConnect:
                        SendConnect(now);
                        break;

                    case NetConnectionStatus.RespondedConnect:
                        SendConnectResponse(now, true);
                        break;

                    case NetConnectionStatus.RespondedAwaitingApproval:
                        // awaiting approval
                        _lastHandshakeSendTime = now; // postpone handshake resend
                        break;

                    case NetConnectionStatus.None:
                    case NetConnectionStatus.ReceivedInitiation:
                    default:
                        Peer.LogWarning(NetLogMessage.FromValues(NetLogCode.UnexpectedHandshakeStatus, value: (int)Status));
                        break;
                }
            }
        }

        internal void ExecuteDisconnect(NetOutgoingMessage? reason, bool sendByeMessage)
        {
            Peer.AssertIsOnLibraryThread();

            // clear send queues
            for (int i = 0; i < _sendChannels.Length; i++)
            {
                NetSenderChannel? channel = _sendChannels[i];
                if (channel != null)
                    channel.Reset();
            }

            if (sendByeMessage)
                SendDisconnect(reason, true);

            if (Status == NetConnectionStatus.ReceivedInitiation)
                // nothing much has happened yet; no need to send disconnected status message
                Status = NetConnectionStatus.Disconnected;
            else
                SetStatus(NetConnectionStatus.Disconnected, reason);

            // in case we're still in handshake
            Peer.Handshakes.TryRemove(RemoteEndPoint, out _);

            _disconnectRequested = false;
            _connectRequested = false;
            _handshakeAttempts = 0;
        }

        internal void ExecuteDisconnect(NetMessageType? reasonType)
        {
            NetOutgoingMessage? reason = null;
            if (reasonType.HasValue)
            {
                reason = Peer.CreateMessage();
                reason._messageType = reasonType.GetValueOrDefault();
            }
            ExecuteDisconnect(reason, reasonType.HasValue);
        }

        internal void SendConnect(TimeSpan now)
        {
            Peer.AssertIsOnLibraryThread();

            int preAllocate = 13 + _peerConfiguration.AppIdentifier.Length;
            preAllocate += LocalHailMessage == null ? 0 : LocalHailMessage.ByteLength;

            NetOutgoingMessage om = Peer.CreateMessage(preAllocate);
            om._messageType = NetMessageType.Connect;
            om.Write(_peerConfiguration.AppIdentifier);
            om.Write(Peer.UniqueIdentifier);
            om.Write(now);

            WriteLocalHail(om);

            Peer.SendLibraryMessage(om, RemoteEndPoint);

            _connectRequested = false;
            _lastHandshakeSendTime = now;
            _handshakeAttempts++;

            if (_handshakeAttempts > 1)
            {
                Peer.LogDebug(new NetLogMessage(NetLogCode.ResendingConnect));
            }
            SetStatus(NetConnectionStatus.InitiatedConnect);
        }

        internal void SendConnectResponse(TimeSpan now, bool onLibraryThread)
        {
            if (onLibraryThread)
                Peer.AssertIsOnLibraryThread();

            NetOutgoingMessage om = Peer.CreateMessage(
                _peerConfiguration.AppIdentifier.Length + 13 +
                (LocalHailMessage == null ? 0 : LocalHailMessage.ByteLength));

            om._messageType = NetMessageType.ConnectResponse;
            om.Write(_peerConfiguration.AppIdentifier);
            om.Write(Peer.UniqueIdentifier);
            om.Write(now);

            WriteLocalHail(om);

            if (onLibraryThread)
                Peer.SendLibraryMessage(om, RemoteEndPoint);
            else
                Peer.UnsentUnconnectedMessages.Enqueue((RemoteEndPoint, om));

            _lastHandshakeSendTime = now;
            _handshakeAttempts++;

            if (_handshakeAttempts > 1)
            {
                Peer.LogDebug(new NetLogMessage(NetLogCode.ResendingRespondedConnect));
            }
            SetStatus(NetConnectionStatus.RespondedConnect);
        }

        internal void SendDisconnect(NetOutgoingMessage? message, bool onLibraryThread)
        {
            if (onLibraryThread)
                Peer.AssertIsOnLibraryThread();

            if (message == null)
            {
                message = Peer.CreateMessage();
                message._messageType = NetMessageType.Disconnect;
            }

            if (onLibraryThread)
                Peer.SendLibraryMessage(message, RemoteEndPoint);
            else
                Peer.UnsentUnconnectedMessages.Enqueue((RemoteEndPoint, message));
        }

        private void WriteLocalHail(NetOutgoingMessage om)
        {
            if (LocalHailMessage == null)
                return;

            byte[]? hi = LocalHailMessage.GetBuffer();
            if (hi.Length >= LocalHailMessage.ByteLength)
            {
                if (om.ByteLength + LocalHailMessage.ByteLength > _peerConfiguration._maximumTransmissionUnit - 10)
                {
                    Peer.ThrowOrLog(
                        "Hail message too large; can maximally be " +
                        (_peerConfiguration._maximumTransmissionUnit - 10 - om.ByteLength));
                }

                om.Write(hi.AsSpan(0, LocalHailMessage.ByteLength));
            }
        }

        internal void SendConnectionEstablished()
        {
            NetOutgoingMessage om = Peer.CreateMessage();
            om._messageType = NetMessageType.ConnectionEstablished;
            om.Write(NetTime.Now);
            Peer.SendLibraryMessage(om, RemoteEndPoint);

            _handshakeAttempts = 0;

            InitializePing();
            if (Status != NetConnectionStatus.Connected)
                SetStatus(NetConnectionStatus.Connected);
        }

        /// <summary>
        /// Approves this connection; sending a connection response to the remote host.
        /// </summary>
        /// <param name="localHail">The local hail message that will be set as RemoteHailMessage on the remote host</param>
        public void Approve(NetOutgoingMessage? localHail)
        {
            if (Status != NetConnectionStatus.RespondedAwaitingApproval)
            {
                Peer.LogWarning(NetLogMessage.FromValues(NetLogCode.UnexpectedApprove, endPoint: this, value: (int)Status));
                return;
            }

            LocalHailMessage = localHail;
            _handshakeAttempts = 0;
            SendConnectResponse(NetTime.Now, false);
        }

        /// <summary>
        /// Denies this connection; disconnecting it and sending a reason 
        /// in a <see cref="NetIncomingMessageType.StatusChanged"/> message.
        /// </summary>
        /// <param name="reason">The stated reason for the disconnect.</param>
        public void Deny(NetOutgoingMessage? reason = null)
        {
            // send disconnect; remove from handshakes
            if (reason != null)
                reason._messageType = NetMessageType.Disconnect;

            SendDisconnect(reason, false);

            // remove from handshakes
            Peer.Handshakes.TryRemove(RemoteEndPoint, out _);
        }

        internal void ReceivedHandshake(TimeSpan now, NetMessageType type, int offset, int payloadLength)
        {
            Peer.AssertIsOnLibraryThread();

            switch (type)
            {
                case NetMessageType.Connect:
                    if (Status == NetConnectionStatus.ReceivedInitiation)
                    {
                        // Whee! Server full has already been checked
                        var (success, hail, hailLength) = ValidateHandshakeData(offset, payloadLength);
                        if (success)
                        {
                            if (hail != null)
                            {
                                RemoteHailMessage = Peer.CreateIncomingMessage(NetIncomingMessageType.Data);
                                RemoteHailMessage.SetBuffer(hail, true);
                                RemoteHailMessage.ByteLength = hailLength;
                            }
                            else
                            {
                                RemoteHailMessage = null;
                            }

                            if (_peerConfiguration.IsMessageTypeEnabled(NetIncomingMessageType.ConnectionApproval))
                            {
                                // ok, let's not add connection just yet
                                var appMsg = Peer.CreateIncomingMessage(NetIncomingMessageType.ConnectionApproval);

                                appMsg.ReceiveTime = now;
                                appMsg.SenderConnection = this;
                                appMsg.SenderEndPoint = RemoteEndPoint;

                                if (RemoteHailMessage != null)
                                    appMsg.Write(RemoteHailMessage.GetBuffer().AsSpan(0, RemoteHailMessage.ByteLength));

                                SetStatus(NetConnectionStatus.RespondedAwaitingApproval);
                                Peer.ReleaseMessage(appMsg);
                                return;
                            }

                            SendConnectResponse(now, true);
                        }
                        return;
                    }
                    if (Status == NetConnectionStatus.RespondedAwaitingApproval)
                    {
                        Peer.LogWarning(new NetLogMessage(NetLogCode.IgnoringMultipleConnects, endPoint: this));
                        return;
                    }
                    if (Status == NetConnectionStatus.RespondedConnect)
                    {
                        // our ConnectResponse must have been lost
                        SendConnectResponse(now, true);
                        return;
                    }
                    Peer.LogDebug(NetLogMessage.FromValues(NetLogCode.UnhandledConnect, endPoint: this, value: (int)Status));
                    break;

                case NetMessageType.ConnectResponse:
                    HandleConnectResponse(offset, payloadLength);
                    break;

                case NetMessageType.ConnectionEstablished:
                    switch (Status)
                    {
                        case NetConnectionStatus.Connected:
                            // ok...
                            break;

                        case NetConnectionStatus.Disconnected:
                        case NetConnectionStatus.Disconnecting:
                        case NetConnectionStatus.None:
                            // too bad, almost made it
                            break;

                        case NetConnectionStatus.ReceivedInitiation:
                            // uh, a little premature... ignore
                            break;

                        case NetConnectionStatus.InitiatedConnect:
                            // weird, should have been RespondedConnect...
                            break;

                        case NetConnectionStatus.RespondedConnect:
                            // awesome
                            NetIncomingMessage msg = Peer.SetupReadHelperMessage(offset, payloadLength);
                            InitializeRemoteTimeOffset(msg.ReadTimeSpan());

                            Peer.AcceptConnection(this);
                            InitializePing();
                            SetStatus(NetConnectionStatus.Connected);
                            return;
                    }
                    break;

                case NetMessageType.InvalidHandshake:
                case NetMessageType.WrongAppIdentifier:
                case NetMessageType.ConnectTimedOut:
                case NetMessageType.TimedOut:
                case NetMessageType.Disconnect:
                    NetOutgoingMessage? reason = null;
                    try
                    {
                        reason = Peer.CreateReadHelperOutMessage(offset, payloadLength);
                    }
                    catch
                    {
                    }
                    ExecuteDisconnect(reason, false);
                    break;

                case NetMessageType.Discovery:
                    Peer.HandleIncomingDiscoveryRequest(now, RemoteEndPoint, offset, payloadLength);
                    return;

                case NetMessageType.DiscoveryResponse:
                    Peer.HandleIncomingDiscoveryResponse(now, RemoteEndPoint, offset, payloadLength);
                    return;

                case NetMessageType.Ping:
                    // silently ignore
                    return;

                default:
                    Peer.LogDebug(NetLogMessage.FromValues(NetLogCode.UnhandledHandshakeMessage, value: (int)Status));
                    break;
            }
        }

        private void HandleConnectResponse(int offset, int payloadLength)
        {
            switch (Status)
            {
                case NetConnectionStatus.InitiatedConnect:
                    var (success, hail, hailLength) = ValidateHandshakeData(offset, payloadLength);
                    if (success)
                    {
                        if (hail != null)
                        {
                            RemoteHailMessage = Peer.CreateIncomingMessage(NetIncomingMessageType.Data);
                            RemoteHailMessage.SetBuffer(hail, true);
                            RemoteHailMessage.ByteLength = hailLength;
                        }
                        else
                        {
                            RemoteHailMessage = null;
                        }

                        Peer.AcceptConnection(this);
                        SendConnectionEstablished();
                        return;
                    }
                    break;

                case NetConnectionStatus.RespondedConnect:
                    // hello?
                    break;

                case NetConnectionStatus.Disconnecting:
                case NetConnectionStatus.Disconnected:
                case NetConnectionStatus.ReceivedInitiation:
                case NetConnectionStatus.None:
                    // anyway, bye!
                    break;

                case NetConnectionStatus.Connected:
                    // my ConnectionEstablished must have been lost, send another one
                    SendConnectionEstablished();
                    return;
            }
        }

        // TODO: consider outputting a NetIncomingMessage instead of byte[]
        private (bool Success, byte[]? Hail, int HailLength) ValidateHandshakeData(int offset, int payloadLength)
        {
            // create temporary incoming message
            NetIncomingMessage msg = Peer.SetupReadHelperMessage(offset, payloadLength);
            try
            {
                string remoteAppIdentifier = msg.ReadString();
                long remoteUniqueIdentifier = msg.ReadInt64();
                InitializeRemoteTimeOffset(msg.ReadTimeSpan());

                byte[]? hail = null;
                int hailLength = payloadLength - (msg.BytePosition - offset);
                if (hailLength > 0)
                {
                    hail = msg.StoragePool.Rent(hailLength);
                    msg.Read(hail.AsSpan(0, hailLength));
                }

                if (remoteAppIdentifier != Peer.Configuration.AppIdentifier)
                {
                    ExecuteDisconnect(NetMessageType.WrongAppIdentifier);
                    return (false, hail, hailLength);
                }

                RemoteUniqueIdentifier = remoteUniqueIdentifier;
                return (true, hail, hailLength);
            }
            catch (Exception ex)
            {
                // whatever; we failed
                ExecuteDisconnect(NetMessageType.InvalidHandshake);
                Peer.LogWarning(new NetLogMessage(NetLogCode.InvalidHandshake, ex));

                return (false, null, 0);
            }
        }

        /// <summary>
        /// Disconnect from the remote peer.
        /// </summary>
        /// <param name="reason">The message to send with the disconnect.</param>
        public void Disconnect(NetOutgoingMessage? reason = null)
        {
            // user or library thread
            if (Status == NetConnectionStatus.None ||
                Status == NetConnectionStatus.Disconnected)
            {
                return;
            }

            _disconnectMessage = reason;
            if (_disconnectMessage != null)
            {
                _disconnectMessage._messageType = NetMessageType.Disconnect;
                Interlocked.Increment(ref _disconnectMessage._recyclingCount);
            }

            if (Status != NetConnectionStatus.Disconnected &&
                Status != NetConnectionStatus.None)
            {
                SetStatus(NetConnectionStatus.Disconnecting, reason);
            }

            _handshakeAttempts = 0;
            _disconnectRequested = true;
            _disconnectReqSendBye = true;
        }
    }
}
