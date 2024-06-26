﻿using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

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
            if (connections is List<NetConnection?> list)
            {
                return GetMTU(list, out recipientCount);
            }

            ArgumentNullException.ThrowIfNull(connections);

            int mtu = NetPeerConfiguration.DefaultMTU;
            recipientCount = 0;

            foreach (NetConnection? conn in connections.AsListEnumerator())
            {
                if (conn != null)
                {
                    mtu = Math.Min(conn.CurrentMTU, mtu);
                    recipientCount++;
                }
            }
            return mtu;
        }

        public static int GetMTU(List<NetConnection?> connections, out int recipientCount)
        {
            ArgumentNullException.ThrowIfNull(connections);

            int mtu = NetPeerConfiguration.DefaultMTU;
            recipientCount = 0;

            foreach (NetConnection? conn in CollectionsMarshal.AsSpan(connections))
            {
                if (conn != null)
                {
                    mtu = Math.Min(conn.CurrentMTU, mtu);
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
                return recipient.EnqueueMessage(message, method, sequenceChannel).Result;
            }
            else
            {
                // message must be fragmented!
                if (recipient.Status != NetConnectionStatus.Connected)
                    return NetSendResult.FailedNotConnected;

                List<NetConnection> recipients = NetConnectionListPool.Rent(1);
                recipients.Add(recipient);
                NetSendResult result = SendFragmentedMessage(message, recipients, method, sequenceChannel);
                NetConnectionListPool.Return(recipients);
                return result;
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
            IEnumerable<NetConnection?> recipients,
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

            List<NetConnection> recipientList = NetConnectionListPool.Rent(recipients);
            int mtu = GetMTU(recipientList!, out _);
            if (recipientList.Count == 0)
            {
                NetConnectionListPool.Return(recipientList);
                Recycle(message);
                return NetSendResult.NoRecipients;
            }

            var retval = NetSendResult.Sent;
            int length = message.GetEncodedSize();
            if (length <= mtu)
            {
                Interlocked.Add(ref message._recyclingCount, recipientList.Count);

                foreach (NetConnection conn in CollectionsMarshal.AsSpan(recipientList))
                {
                    NetSendResult result = conn.EnqueueMessage(message, method, sequenceChannel).Result;
                    if (result > retval)
                        retval = result; // return "worst" result
                }
            }
            else
            {
                // message must be fragmented!
                retval = SendFragmentedMessage(message, recipientList, method, sequenceChannel);
            }

            NetConnectionListPool.Return(recipientList);
            return retval;
        }

        /// <summary>
        /// Streams a message to a list of connections.
        /// </summary>
        /// <param name="message">The message to stream.</param>
        /// <param name="recipients">The list of recipients to send to.</param>
        /// <param name="sequenceChannel">Sequence channel within <see cref="NetDeliveryMethod.ReliableOrdered"/>.</param>
        public ValueTask<NetSendResult> StreamMessageAsync(
            PipeReader message,
            IEnumerable<NetConnection?> recipients,
            int sequenceChannel,
            CancellationToken cancellationToken = default)
        {
            List<NetConnection> recipientList = NetConnectionListPool.Rent(recipients);
            if (recipientList.Count == 0)
            {
                NetConnectionListPool.Return(recipientList);
                return new(NetSendResult.NoRecipients);
            }

            if (recipientList.Count > 1)
            {
                NetConnectionListPool.Return(recipientList);
                throw new NotImplementedException("This method can only send to one recipient at a time.");
            }

            return SendFragmentedMessageAsync(message, recipientList[0], sequenceChannel, cancellationToken);
        }

        private void CheckUnconnectedMessage(NetOutgoingMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);

            message.AssertNotSent(nameof(message));
            AssertValidUnconnectedLength(message);
        }

        private void SendUnconnectedMessageCore(NetOutgoingMessage message, NetAddress recipient)
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
            CheckUnconnectedMessage(message);
            IPAddress? address = NetUtility.Resolve(host);
            if (address == null)
                throw new LidgrenException("Failed to resolve " + host.ToString());

            SendUnconnectedMessageCore(message, new NetAddress(address, port));
        }

        /// <summary>
        /// Send a message to an unconnected host.
        /// </summary>
        public void SendUnconnectedMessage(NetOutgoingMessage message, IPEndPoint recipient)
        {
            SendUnconnectedMessage(message, new NetAddress(recipient));
        }

        /// <summary>
        /// Send a message to an unconnected host.
        /// </summary>
        public void SendUnconnectedMessage(NetOutgoingMessage message, NetAddress recipient)
        {
            CheckUnconnectedMessage(message);
            if (recipient.IsEmpty)
                throw new ArgumentException(null, nameof(recipient));

            SendUnconnectedMessageCore(message, recipient);
        }

        /// <summary>
        /// Send a message to an unconnected recipients.
        /// </summary>
        public void SendUnconnectedMessage(NetOutgoingMessage message, IEnumerable<NetAddress> recipients)
        {
            CheckUnconnectedMessage(message);
            ArgumentNullException.ThrowIfNull(recipients);

            message._messageType = NetMessageType.Unconnected;
            message._isSent = true;

            Interlocked.Increment(ref message._recyclingCount);

            foreach (NetAddress endPoint in recipients.AsListEnumerator())
            {
                if (!endPoint.IsEmpty)
                {
                    Interlocked.Increment(ref message._recyclingCount);
                    UnsentUnconnectedMessages.Enqueue((endPoint, message));
                }
            }

            if (Interlocked.Decrement(ref message._recyclingCount) == 0)
            {
                Recycle(message);
            }
        }

        /// <summary>
        /// Send a message to this exact same netpeer (loopback).
        /// </summary>
        public void SendUnconnectedToSelf(NetOutgoingMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);
            message.AssertNotSent(nameof(message));

            if (Socket == null)
                throw new InvalidOperationException("No socket bound.");

            message._messageType = NetMessageType.Unconnected;
            message._isSent = true;

            if (!Configuration.IsMessageTypeEnabled(NetIncomingMessageType.UnconnectedData))
            {
                Interlocked.Decrement(ref message._recyclingCount);
                Recycle(message);
                return; // dropping unconnected message since it's not enabled for receiving
            }

            var om = CreateIncomingMessage(NetIncomingMessageType.UnconnectedData);
            om.Write(message);
            om.IsFragment = false;
            om.ReceiveTime = NetTime.Now;
            if (Socket.LocalEndPoint is IPEndPoint endPoint)
            {
                new NetAddress(endPoint).WriteTo(om.SenderAddress);
            }
            LidgrenException.Assert(om.BitLength == message.BitLength);

            ReleaseMessage(om);
        }
    }
}