using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;

namespace Lidgren.Network
{
    public partial class NetPeer
    {
        private void AssertValidUnconnectedLength(NetOutgoingMessage message)
        {
            if (message.ByteLength > Configuration.MaximumTransmissionUnit)
            {
                throw new LidgrenException(
                    "Unconnected message must be shorter than NetConfiguration.MaximumTransmissionUnit (currently " +
                    Configuration.MaximumTransmissionUnit + ").");
            }
        }

        public static int GetMTU(IEnumerable<NetConnection?> connections, out int recipientCount)
        {
            if (connections == null)
                throw new ArgumentNullException(nameof(connections));

            int mtu = NetPeerConfiguration.DefaultMTU;
            recipientCount = 0;

            foreach (var conn in connections.AsListEnumerator())
            {
                if (conn != null)
                {
                    if (conn.CurrentMTU < mtu)
                        mtu = conn.CurrentMTU;

                    recipientCount++;
                }
            }
            return mtu;
        }

        /// <summary>
        /// Send a message to a specific connection.
        /// </summary>
        /// <param name="message">The message to send</param>
        /// <param name="recipient">The recipient connection</param>
        /// <param name="method">How to deliver the message</param>
        /// <param name="sequenceChannel">Sequence channel within the delivery method</param>
        public NetSendResult SendMessage(
            NetOutgoingMessage message, NetConnection recipient, NetDeliveryMethod method, int sequenceChannel)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));
            if (recipient == null)
                throw new ArgumentNullException(nameof(recipient));

            NetConstants.AssertValidDeliveryChannel(
                method, sequenceChannel, nameof(method), nameof(sequenceChannel));

            message.AssertNotSent(nameof(message));
            message._isSent = true;

            bool suppressFragmentation =
                (method == NetDeliveryMethod.Unreliable || method == NetDeliveryMethod.UnreliableSequenced) &&
                Configuration.UnreliableSizeBehaviour != NetUnreliableSizeBehaviour.NormalFragmentation;

            if (suppressFragmentation || message.GetEncodedSize() <= recipient.CurrentMTU)
            {
                Interlocked.Increment(ref message._recyclingCount);
                return recipient.EnqueueMessage(message, method, sequenceChannel);
            }
            else
            {
                // message must be fragmented!
                if (recipient.Status != NetConnectionStatus.Connected)
                    return NetSendResult.FailedNotConnected;

                var recipients = NetConnectionListPool.Rent(1);
                try
                {
                    recipients.Add(recipient);
                    return SendFragmentedMessage(message, recipients, method, sequenceChannel);
                }
                finally
                {
                    NetConnectionListPool.Return(recipients);
                }
            }
        }

        /// <summary>
        /// Send a message to a specific connection.
        /// </summary>
        /// <param name="message">The message to send</param>
        /// <param name="recipient">The recipient connection</param>
        /// <param name="method">How to deliver the message</param>
        public NetSendResult SendMessage(
            NetOutgoingMessage message, NetConnection recipient, NetDeliveryMethod method)
        {
            return SendMessage(message, recipient, method, 0);
        }

        /// <summary>
        /// Send a message to a list of connections.
        /// </summary>
        /// <param name="message">The message to send</param>
        /// <param name="recipients">The list of recipients to send to</param>
        /// <param name="method">How to deliver the message</param>
        /// <param name="sequenceChannel">Sequence channel within the delivery method</param>
        public NetSendResult SendMessage(
            NetOutgoingMessage message,
            IReadOnlyCollection<NetConnection?> recipients,
            NetDeliveryMethod method,
            int sequenceChannel)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));
            if (recipients == null)
                throw new ArgumentNullException(nameof(recipients));

            NetConstants.AssertValidDeliveryChannel(
                method, sequenceChannel, nameof(method), nameof(sequenceChannel));

            message.AssertNotSent(nameof(message));
            message._isSent = true;

            int mtu = GetMTU(recipients, out int recipientCount);
            if (recipientCount == 0)
            {
                Recycle(message);
                return NetSendResult.NoRecipients;
            }

            int length = message.GetEncodedSize();
            if (length <= mtu)
            {
                Interlocked.Add(ref message._recyclingCount, recipientCount);

                var retval = NetSendResult.Sent;
                foreach (NetConnection? conn in recipients)
                {
                    if (conn == null)
                        continue;

                    var result = conn.EnqueueMessage(message, method, sequenceChannel);
                    if ((int)result > (int)retval)
                        retval = result; // return "worst" result
                }
                return retval;
            }
            else
            {
                // message must be fragmented!
                return SendFragmentedMessage(message, recipients, method, sequenceChannel);
            }
        }

        private void SendUnconnectedMessageCore(NetOutgoingMessage message, IPEndPoint recipient)
        {
            message._messageType = NetMessageType.Unconnected;
            message._isSent = true;

            Interlocked.Increment(ref message._recyclingCount);
            UnsentUnconnectedMessages.Enqueue((recipient, message));
        }

        /// <summary>
        /// Send a message to an unconnected host.
        /// </summary>
        public void SendUnconnectedMessage(NetOutgoingMessage message, ReadOnlySpan<char> host, int port)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            message.AssertNotSent(nameof(message));
            AssertValidUnconnectedLength(message);

            IPAddress? address = NetUtility.Resolve(host);
            if (address == null)
                throw new LidgrenException("Failed to resolve " + host.ToString());

            IPEndPoint recipient = new(address, port);
            SendUnconnectedMessageCore(message, recipient);
        }

        /// <summary>
        /// Send a message to an unconnected host.
        /// </summary>
        public void SendUnconnectedMessage(NetOutgoingMessage message, IPEndPoint recipient)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));
            if (recipient == null)
                throw new ArgumentNullException(nameof(recipient));

            message.AssertNotSent(nameof(message));
            AssertValidUnconnectedLength(message);

            SendUnconnectedMessageCore(message, recipient);
        }

        /// <summary>
        /// Send a message to an unconnected host.
        /// </summary>
        public void SendUnconnectedMessage(NetOutgoingMessage message, IEnumerable<IPEndPoint?> recipients)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));
            if (recipients == null)
                throw new ArgumentNullException(nameof(recipients));

            AssertValidUnconnectedLength(message);
            message.AssertNotSent(nameof(message));

            message._messageType = NetMessageType.Unconnected;
            message._isSent = true;

            int recipientCount = 0;
            foreach (var recipient in recipients.AsListEnumerator())
            {
                if (recipient != null)
                    recipientCount++;
            }
            Interlocked.Add(ref message._recyclingCount, recipientCount);

            foreach (var endPoint in recipients.AsListEnumerator())
            {
                if (endPoint == null)
                    continue;

                UnsentUnconnectedMessages.Enqueue((endPoint, message));
            }
        }

        /// <summary>
        /// Send a message to this exact same netpeer (loopback).
        /// </summary>
        public void SendUnconnectedToSelf(NetOutgoingMessage message)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));
            message.AssertNotSent(nameof(message));

            if (Socket == null)
                throw new InvalidOperationException("No socket bound.");

            message._messageType = NetMessageType.Unconnected;
            message._isSent = true;

            if (!Configuration.IsMessageTypeEnabled(NetIncomingMessageType.UnconnectedData))
                return; // dropping unconnected message since it's not enabled for receiving

            var om = CreateIncomingMessage(NetIncomingMessageType.UnconnectedData);
            om.Write(message);
            om.IsFragment = false;
            om.ReceiveTime = NetTime.Now;
            om.SenderConnection = null;
            om.SenderEndPoint = Socket.LocalEndPoint as IPEndPoint;
            LidgrenException.Assert(om.BitLength == message.BitLength);

            ReleaseMessage(om);
        }
    }
}