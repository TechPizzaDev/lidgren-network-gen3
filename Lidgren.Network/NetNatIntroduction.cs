using System;
using System.Diagnostics;
using System.Net;
using System.Text;

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
                UnsentUnconnectedMessages.Enqueue((new NetAddress(clientExternal), msg));
            }

            // send message to host
            {
                NetOutgoingMessage msg = CreateMessage(1 + 32 + 2 + token.Length);
                msg._messageType = NetMessageType.NatIntroduction;
                msg.Write((byte)1);
                msg.Write(clientInternal);
                msg.Write(clientExternal);
                msg.Write(token);
                UnsentUnconnectedMessages.Enqueue((new NetAddress(hostExternal), msg));
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
            NetAddress remoteInternal = new(tmp.ReadIPAddress(), tmp.ReadUInt16());
            NetAddress remoteExternal = new(tmp.ReadIPAddress(), tmp.ReadUInt16());
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
        private void HandleNatPunch(int offset, NetAddress senderAddress)
        {
            Debug.Assert(senderAddress.IsOwned);

            NetIncomingMessage tmp = SetupReadHelperMessage(offset, 1000); // never mind length

            byte fromHostByte = tmp.ReadByte();
            if (fromHostByte == 0)
            {
                // it's from client
                LogDebug(new NetLogMessage(NetLogCode.HostNATPunchSuccess, endPoint: senderAddress));
                return; // don't alert hosts about nat punch successes; only clients
            }

            string token = tmp.ReadString();
            LogDebug(new NetLogMessage(NetLogCode.ClientNATPunchSuccess, endPoint: senderAddress, data: token));

            //
            // Release punch success to client; enabling him to Connect() to msg.SenderIPEndPoint if token is ok
            //
            var punchSuccess = CreateIncomingMessage(NetIncomingMessageType.NatIntroductionSuccess, senderAddress);
            punchSuccess.Write(token);
            ReleaseMessage(punchSuccess);

            // send a return punch just for good measure
            var punch = CreateMessage(1 + Encoding.UTF8.GetMaxByteCount(token.Length));
            punch._messageType = NetMessageType.NatPunchMessage;
            punch.Write((byte)0);
            punch.Write(token);
            UnsentUnconnectedMessages.Enqueue((senderAddress, punch));
        }
    }
}
