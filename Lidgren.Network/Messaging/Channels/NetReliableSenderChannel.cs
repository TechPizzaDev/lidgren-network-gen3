using System;
using System.Threading;

namespace Lidgren.Network
{
    /// <summary>
    /// Sender part of Selective repeat ARQ for a particular NetChannel
    /// </summary>
    internal sealed class NetReliableSenderChannel : NetSenderChannel
    {
        private NetConnection _connection;
        private int _windowStart;
        private int _windowSize;
        private int _sendStart;
        private NetBitArray _receivedAcks;

        internal NetStoredReliableMessage[] StoredMessages { get; }

        public TimeSpan ResendDelay { get; set; }
        public override int WindowSize => _windowSize;

        public NetReliableSenderChannel(NetConnection connection, int windowSize)
        {
            LidgrenException.AssertIsPowerOfTwo((ulong)windowSize, nameof(windowSize));
            
            _connection = connection;
            _windowSize = windowSize;
            _windowStart = 0;
            _sendStart = 0;
            _receivedAcks = new NetBitArray(NetConstants.SequenceNumbers);
            StoredMessages = new NetStoredReliableMessage[_windowSize];
            ResendDelay = connection.ResendDelay;
        }

        public override int GetAllowedSends()
        {
            int retval =
                _windowSize -
                (_sendStart + NetConstants.SequenceNumbers - _windowStart) % NetConstants.SequenceNumbers;

            LidgrenException.Assert(retval >= 0 && retval <= _windowSize);
            return retval;
        }

        public override void Reset()
        {
            _receivedAcks.Clear();
            for (int i = 0; i < StoredMessages.Length; i++)
                StoredMessages[i].Reset();
            QueuedSends.Clear();
            _windowStart = 0;
            _sendStart = 0;
        }

        public override NetSendResult Enqueue(NetOutgoingMessage message)
        {
            QueuedSends.Enqueue(message);
            if (QueuedSends.Count <= GetAllowedSends())
                return NetSendResult.Sent;
            return NetSendResult.Queued;
        }

        // call this regularely
        public override void SendQueuedMessages(TimeSpan now)
        {
            TimeSpan resendDelay = ResendDelay;

            for (int i = 0; i < StoredMessages.Length; i++)
            {
                ref NetStoredReliableMessage storedMessage = ref StoredMessages[i];
                if (storedMessage.Message == null)
                    continue;

                TimeSpan t = storedMessage.LastSent;
                if (t > TimeSpan.Zero && (now - t) > resendDelay)
                {
                    //m_connection.m_peer.LogVerbose(
                    //    "Resending due to delay #" + storedMessage.SequenceNumber + " " + om.ToString());
                    _connection.Statistics.MessageResent(MessageResendReason.Delay);

                    _connection.QueueSendMessage(storedMessage.Message, storedMessage.SequenceNumber);

                    storedMessage.LastSent = now;
                    storedMessage.NumSent++;
                }
            }

            int num = GetAllowedSends();
            if (num > 0)
            {
                while (num > 0 && QueuedSends.TryDequeue(out NetOutgoingMessage? message))
                {
                    ExecuteSend(now, message);
                    num--;
                    LidgrenException.Assert(num == GetAllowedSends());
                }
            }
        }

        private void ExecuteSend(TimeSpan now, NetOutgoingMessage message)
        {
            int seqNr = _sendStart;
            _sendStart = (seqNr + 1) % NetConstants.SequenceNumbers;

            ref NetStoredReliableMessage storedMessage = ref StoredMessages[NetUtility.PowOf2Mod(seqNr, _windowSize)];
            LidgrenException.Assert(storedMessage.Message == null);

            _connection.QueueSendMessage(message, seqNr);

            storedMessage.SequenceNumber = seqNr;
            storedMessage.NumSent++;
            storedMessage.LastSent = now;
            storedMessage.Message = message;
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
                    _connection.Peer.Recycle(storedMessage.Message);
            }
            storedMessage.Reset();
        }

        // remoteWindowStart is remote expected sequence number; everything below this has arrived properly
        // seqNr is the actual nr received
        public override void ReceiveAcknowledge(TimeSpan now, int seqNr)
        {
            ref int windowStart = ref _windowStart;

            // late (dupe), on time or early ack?
            int relate = NetUtility.RelativeSequenceNumber(seqNr, windowStart);

            if (relate < 0)
            {
                //m_connection.m_peer.LogDebug("Received late/dupe ack for #" + seqNr);
                return; // late/duplicate ack
            }

            int windowSize = _windowSize;
            NetBitArray receivedAcks = _receivedAcks;

            if (relate == 0)
            {
                //m_connection.m_peer.LogDebug("Received right-on-time ack for #" + seqNr);

                // ack arrived right on time
                LidgrenException.Assert(seqNr == windowStart);

                receivedAcks[windowStart] = false;
                DestoreMessage(NetUtility.PowOf2Mod(windowStart, windowSize));
                windowStart = (windowStart + 1) % NetConstants.SequenceNumbers;

                // advance window if we already have early acks
                while (receivedAcks.Get(windowStart))
                {
                    //m_connection.m_peer.LogDebug("Using early ack for #" + m_windowStart + "...");
                    receivedAcks[windowStart] = false;
                    DestoreMessage(NetUtility.PowOf2Mod(windowStart, windowSize));

                    LidgrenException.Assert(
                        StoredMessages[NetUtility.PowOf2Mod(windowStart, windowSize)].Message == null,
                        "Stored message has not been recycled.");

                    windowStart = (windowStart + 1) % NetConstants.SequenceNumbers;
                    //m_connection.m_peer.LogDebug("Advancing window to #" + m_windowStart);
                }
                return;
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
                if (!receivedAcks[seqNr])
                    receivedAcks[seqNr] = true;
                //else
                //   we've already destored/been acked for this message
            }
            else if (sendRelate > 0)
            {
                // uh... we haven't sent this message yet? Weird, dupe or error...
                LidgrenException.Assert(false, "Got ack for message not yet sent?");
                return;
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
                            storedMessage.NumSent++;
                            storedMessage.LastSent = now;
                            _connection.Statistics.MessageResent(MessageResendReason.HoleInSequence);
                            _connection.QueueSendMessage(storedMessage.Message, rnr);
                        }
                        //else
                        //    already resent recently
                    }
                }
                else
                {
                    // m_connection.m_peer.LogDebug("Not resending #" + rnr + " (since we got ack)");
                }

            }
            while (rnr != windowStart);
        }
    }
}
