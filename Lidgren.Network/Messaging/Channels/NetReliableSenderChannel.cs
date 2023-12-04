using System;
using System.Threading;

namespace Lidgren.Network
{
    /// <summary>
    /// Sender part of Selective repeat ARQ for a particular NetChannel
    /// </summary>
    internal sealed class NetReliableSenderChannel : NetSenderChannel
    {
        public NetStoredReliableMessage[] StoredMessages { get; }

        public TimeSpan ResendDelay { get; set; }

        public NetReliableSenderChannel(NetConnection connection, int windowSize) : base(connection, windowSize)
        {
            StoredMessages = new NetStoredReliableMessage[windowSize];
            ResendDelay = connection.ResendDelay;
        }

        public override int GetAllowedSends()
        {
            int value = WindowSize - NetUtility.PowOf2Mod(
                _sendStart + NetConstants.SequenceNumbers - _windowStart,
                NetConstants.SequenceNumbers);

            LidgrenException.Assert(value >= 0 && value <= WindowSize);
            return value;
        }

        public override void Reset()
        {
            for (int i = 0; i < StoredMessages.Length; i++)
            {
                StoredMessages[i].Reset();
            }
            base.Reset();
        }

        public override NetSendResult Enqueue(NetOutgoingMessage message)
        {
            QueuedSends.Enqueue(message);
            if (QueuedSends.Count <= GetAllowedSends())
                return NetSendResult.Sent;
            return NetSendResult.Queued;
        }

        // call this regularely
        public override NetSocketResult SendQueuedMessages(TimeSpan now)
        {
            NetConnection connection = Connection;
            TimeSpan resendDelay = ResendDelay;

            for (int i = 0; i < StoredMessages.Length; i++)
            {
                ref NetStoredReliableMessage storedMessage = ref StoredMessages[i];
                if (storedMessage.Message == null)
                    continue;

                TimeSpan t = storedMessage.LastSent;
                if (t > TimeSpan.Zero && (now - t) > resendDelay)
                {
                    var sendResult = connection.QueueSendMessage(storedMessage.Message, storedMessage.SequenceNumber);
                    if (!sendResult.Success)
                    {
                        return sendResult;
                    }
                    storedMessage.LastSent = now;
                    storedMessage.NumSent++;
                    connection.Statistics.MessageResent(MessageResendReason.Delay);

                }
            }

            int num = GetAllowedSends();
            while (num > 0 && QueuedSends.TryDequeue(out NetOutgoingMessage? message))
            {
                var sendResult = ExecuteSend(now, message);
                if (!sendResult.Success)
                {
                    QueuedSends.EnqueueFirst(message);
                    return sendResult;
                }
                num--;

                LidgrenException.Assert(num == GetAllowedSends());
            }

            NotifyIdleWaiters(now, num);

            return new NetSocketResult(true, false);
        }

        private NetSocketResult ExecuteSend(TimeSpan now, NetOutgoingMessage message)
        {
            int seqNr = _sendStart;
            int windowSize = WindowSize;

            ref NetStoredReliableMessage storedMessage = ref StoredMessages[NetUtility.PowOf2Mod(seqNr, windowSize)];
            LidgrenException.Assert(storedMessage.Message == null);

            var sendResult = Connection.QueueSendMessage(message, seqNr);
            if (sendResult.Success)
            {
                _sendStart = NetUtility.PowOf2Mod(seqNr + 1, NetConstants.SequenceNumbers);
                storedMessage.SequenceNumber = seqNr;
                storedMessage.NumSent++;
                storedMessage.LastSent = now;
                storedMessage.Message = message;
            }
            return sendResult;
        }

        private void DestoreMessage(int storeIndex)
        {
            ref NetStoredReliableMessage storedMessage = ref StoredMessages[storeIndex];
#if DEBUG
            if (storedMessage.Message == null)
                throw new LidgrenException(
                    "_storedMessages[" + storeIndex + "].Message is null; " +
                    "sent " + storedMessage.NumSent + " times, " +
                    "last time " + (NetTime.Now - storedMessage.LastSent) + " seconds ago");
#else
            if (storedMessage.Message != null)
#endif
            {
                if (Interlocked.Decrement(ref storedMessage.Message._recyclingCount) <= 0)
                    Connection.Peer.Recycle(storedMessage.Message);
            }
            storedMessage.Reset();
        }

        // remoteWindowStart is remote expected sequence number; everything below this has arrived properly
        // seqNr is the actual nr received
        public override NetSocketResult ReceiveAcknowledge(TimeSpan now, int seqNr)
        {
            NetConnection connection = Connection;
            int windowSize = WindowSize;
            ref int windowStart = ref _windowStart;

            // late (dupe), on time or early ack?
            int relate = NetUtility.RelativeSequenceNumber(seqNr, windowStart);

            if (relate < 0)
            {
                //m_connection.m_peer.LogDebug("Received late/dupe ack for #" + seqNr);
                return new NetSocketResult(true, false); // late/duplicate ack
            }

            NetBitArray receivedAcks = _receivedAcks;

            if (relate == 0)
            {
                //m_connection.m_peer.LogDebug("Received right-on-time ack for #" + seqNr);

                // ack arrived right on time
                LidgrenException.Assert(seqNr == windowStart);

                receivedAcks[windowStart] = false;
                DestoreMessage(NetUtility.PowOf2Mod(windowStart, windowSize));
                windowStart = NetUtility.PowOf2Mod(windowStart + 1, NetConstants.SequenceNumbers);

                // advance window if we already have early acks
                while (receivedAcks.Get(windowStart))
                {
                    //m_connection.m_peer.LogDebug("Using early ack for #" + m_windowStart + "...");
                    receivedAcks[windowStart] = false;
                    DestoreMessage(NetUtility.PowOf2Mod(windowStart, windowSize));

                    LidgrenException.Assert(
                        StoredMessages[NetUtility.PowOf2Mod(windowStart, windowSize)].Message == null,
                        "Stored message has not been recycled.");

                    windowStart = NetUtility.PowOf2Mod(windowStart + 1, NetConstants.SequenceNumbers);
                    //m_connection.m_peer.LogDebug("Advancing window to #" + m_windowStart);
                }
                return new NetSocketResult(true, false);
            }

            //
            // early ack... (if it has been sent!)
            //
            // If it has been sent either the m_windowStart message was lost
            // ... or the ack for that message was lost
            //

            //m_connection.m_peer.LogDebug("Received early ack for #" + seqNr);

            int sendRelate = NetUtility.RelativeSequenceNumber(seqNr, _sendStart);
            if (sendRelate <= 0)
            {
                // yes, we've sent this message - it's an early (but valid) ack
                //if (!receivedAcks[seqNr])
                {
                    receivedAcks[seqNr] = true;
                }
                //else
                //   we've already destored/been acked for this message
            }
            else if (sendRelate > 0)
            {
                // uh... we haven't sent this message yet? Weird, dupe or error...
                LidgrenException.Assert(false, "Got ack for message not yet sent?");
                return new NetSocketResult(true, false);
            }

            // Ok, lets resend all missing acks
            TimeSpan resendDelay = ResendDelay * 0.35;
            int rnr = seqNr;

            do
            {
                rnr--;
                if (rnr < 0)
                    rnr = NetConstants.SequenceNumbers - 1;

                if (!receivedAcks[rnr])
                {
                    ref NetStoredReliableMessage storedMessage = ref StoredMessages[NetUtility.PowOf2Mod(rnr, windowSize)];
                    if (storedMessage.NumSent == 1)
                    {
                        LidgrenException.Assert(storedMessage.Message != null, "Stored message has no outgoing message.");

                        // just sent once; resend immediately since we found gap in ack sequence
                        //m_connection.m_peer.LogVerbose("Resending #" + rnr + " (" + storedMessage.Message + ")");

                        if (now - storedMessage.LastSent >= resendDelay)
                        {
                            var sendResult = connection.QueueSendMessage(storedMessage.Message, rnr);
                            if (!sendResult.Success)
                                return sendResult;

                            storedMessage.NumSent++;
                            storedMessage.LastSent = now;
                            connection.Statistics.MessageResent(MessageResendReason.HoleInSequence);
                        }
                        //else
                        //    already resent recently
                    }
                }
                //else
                //{
                //    // m_connection.m_peer.LogDebug("Not resending #" + rnr + " (since we got ack)");
                //}
            }
            while (rnr != windowStart);

            return new NetSocketResult(true, false);
        }
    }
}
