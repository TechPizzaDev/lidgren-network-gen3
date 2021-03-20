using System;
using System.Net;

namespace Lidgren.Network
{
    public partial class NetPeer
    {
        /// <summary>
        /// Send a NAT introduction to hostExternal and clientExternal; introducing client to host.
        /// </summary>
        public void Introduce(
            IPEndPoint hostInternal,
            IPEndPoint hostExternal,
            IPEndPoint clientInternal,
            IPEndPoint clientExternal,
            ReadOnlySpan<char> token)
        {
            // send message to client
            {
                NetOutgoingMessage msg = CreateMessage(1 + 32 + 2 + token.Length);
                msg._messageType = NetMessageType.NatIntroduction;
                msg.Write((byte)0);
                msg.Write(hostInternal);
                msg.Write(hostExternal);
                msg.Write(token);
                UnsentUnconnectedMessages.Enqueue((clientExternal, msg));
            }

            // send message to host
            {
                NetOutgoingMessage msg = CreateMessage(1 + 32 + 2 + token.Length);
                msg._messageType = NetMessageType.NatIntroduction;
                msg.Write((byte)1);
                msg.Write(clientInternal);
                msg.Write(clientExternal);
                msg.Write(token);
                UnsentUnconnectedMessages.Enqueue((hostExternal, msg));
            }
        }

        /// <summary>
        /// Called when host/client receives a NatIntroduction message from a master server
        /// </summary>
        internal void HandleNatIntroduction(int offset)
        {
            AssertIsOnLibraryThread();

            // read intro
            NetIncomingMessage tmp = SetupReadHelperMessage(offset, 1000); // never mind length

            byte hostByte = tmp.ReadByte();
            IPEndPoint remoteInternal = tmp.ReadIPEndPoint();
            IPEndPoint remoteExternal = tmp.ReadIPEndPoint();
            string token = tmp.ReadString();
            bool isHost = hostByte != 0;

            LogDebug(NetLogMessage.FromValues(NetLogCode.NATIntroductionReceived, value: hostByte));

            if (!isHost && !Configuration.IsMessageTypeEnabled(NetIncomingMessageType.NatIntroductionSuccess))
                return; // no need to punch - we're not listening for nat intros!

            // send internal punch
            var internalPunch = CreateMessage(1);
            internalPunch._messageType = NetMessageType.NatPunchMessage;
            internalPunch.Write(hostByte);
            internalPunch.Write(token);
            UnsentUnconnectedMessages.Enqueue((remoteInternal, internalPunch));
            LogDebug(new NetLogMessage(NetLogCode.NATPunchSent, endPoint: remoteInternal));

            // send external punch
            var externalPunch = CreateMessage(1);
            externalPunch._messageType = NetMessageType.NatPunchMessage;
            externalPunch.Write(hostByte);
            externalPunch.Write(token);
            UnsentUnconnectedMessages.Enqueue((remoteExternal, externalPunch));
            LogDebug(new NetLogMessage(NetLogCode.NATPunchSent, endPoint: remoteExternal));
        }

        /// <summary>
        /// Called when receiving a NatPunchMessage from a remote endpoint
        /// </summary>
        private void HandleNatPunch(int offset, IPEndPoint senderEndPoint)
        {
            NetIncomingMessage tmp = SetupReadHelperMessage(offset, 1000); // never mind length

            byte fromHostByte = tmp.ReadByte();
            if (fromHostByte == 0)
            {
                // it's from client
                LogDebug(new NetLogMessage(NetLogCode.HostNATPunchSuccess, endPoint: senderEndPoint));
                return; // don't alert hosts about nat punch successes; only clients
            }

            string token = tmp.ReadString();
            LogDebug(new NetLogMessage(NetLogCode.ClientNATPunchSuccess, endPoint: senderEndPoint, data: token));
            
            //
            // Release punch success to client; enabling him to Connect() to msg.SenderIPEndPoint if token is ok
            //
            var punchSuccess = CreateIncomingMessage(NetIncomingMessageType.NatIntroductionSuccess);
            punchSuccess.SenderEndPoint = senderEndPoint;
            punchSuccess.Write(token);
            ReleaseMessage(punchSuccess);

            // send a return punch just for good measure
            var punch = CreateMessage(1);
            punch._messageType = NetMessageType.NatPunchMessage;
            punch.Write((byte)0);
            punch.Write(token);
            UnsentUnconnectedMessages.Enqueue((senderEndPoint, punch));
        }
    }
}
