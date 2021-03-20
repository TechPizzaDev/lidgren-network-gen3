using System;
using System.Threading;
using System.Collections.Generic;

namespace Lidgren.Network
{
    internal class ReceivedFragmentGroup
    {
        public byte[] Data;
        public NetBitArray ReceivedChunks;
        public int Count;
        public TimeSpan LastReceived; // TODO: discard after certain age

        public ReceivedFragmentGroup(byte[] data, NetBitArray receivedChunks)
        {
            Data = data;
            ReceivedChunks = receivedChunks;
        }
    }

    public partial class NetPeer
    {
        private int _lastUsedFragmentGroup;

        // on user thread
        // the message must not be sent already
        private NetSendResult SendFragmentedMessage(
            NetOutgoingMessage message,
            IEnumerable<NetConnection?> recipients,
            NetDeliveryMethod method,
            int sequenceChannel)
        {
            // determine minimum mtu for all recipients
            int mtu = GetMTU(recipients, out int recipientCount);
            if (recipientCount == 0)
            {
                Recycle(message);
                return NetSendResult.NoRecipients;
            }

            // Note: this group id is PER SENDING/NetPeer; ie. same id is sent to all recipients;
            // this should be ok however; as long as recipients differentiate between same id but different sender
            int group = Interlocked.Increment(ref _lastUsedFragmentGroup);
            if (group >= NetConstants.MaxFragmentationGroups)
            {
                // TODO: not thread safe; but in practice probably not an issue
                _lastUsedFragmentGroup = 1;
                group = 1;
            }

            // do not send msg; but set fragmentgroup in case user tries to recycle it immediately
            message._fragmentGroup = group;

            // create fragmentation specifics
            int totalBytes = message.ByteLength;

            int bytesPerChunk = NetFragmentationHelper.GetBestChunkSize(group, totalBytes, mtu);

            int numChunks = totalBytes / bytesPerChunk;
            if (numChunks * bytesPerChunk < totalBytes)
                numChunks++;

            var retval = NetSendResult.Sent;

            int bitsPerChunk = bytesPerChunk * 8;
            int bitsLeft = message.BitLength;
            byte[] buffer = message.GetBuffer();
            for (int i = 0; i < numChunks; i++)
            {
                NetOutgoingMessage chunk = CreateMessage();
                chunk.SetBuffer(buffer, false);
                chunk.BitLength = Math.Min(bitsLeft, bitsPerChunk);

                chunk._fragmentGroup = group;
                chunk._fragmentGroupTotalBits = totalBytes * 8;
                chunk._fragmentChunkByteSize = bytesPerChunk;
                chunk._fragmentChunkNumber = i;

                LidgrenException.Assert(chunk.BitLength != 0);
                LidgrenException.Assert(chunk.GetEncodedSize() < mtu);

                Interlocked.Add(ref chunk._recyclingCount, recipientCount);

                foreach (NetConnection? recipient in recipients.AsListEnumerator())
                {
                    if (recipient == null)
                        continue;

                    NetSendResult result = recipient.EnqueueMessage(chunk, method, sequenceChannel);
                    if (result > retval)
                        retval = result; // return "worst" result
                }

                bitsLeft -= bitsPerChunk;
            }
            return retval;
        }

        private bool HandleReleasedFragment(in NetMessageView message)
        {
            LidgrenException.Assert(message.Connection != null, "The message has no associated connection.");

            AssertIsOnLibraryThread();

            // read fragmentation header and combine fragments
            int headerOffset = 0;
            if (!NetFragmentationHelper.ReadHeader(
                message.Span,
                ref headerOffset,
                out int group,
                out int totalBits,
                out int chunkByteSize,
                out int chunkNumber))
            {
                LogWarning(new NetLogMessage(NetLogCode.InvalidFragmentHeader, message));
                return false;
            }

            LidgrenException.Assert(message.Span.Length > headerOffset);
            LidgrenException.Assert(group > 0);
            LidgrenException.Assert(totalBits > 0);
            LidgrenException.Assert(chunkByteSize > 0);

            int totalBytes = NetBitWriter.BytesForBits(totalBits);
            int totalChunkCount = totalBytes / chunkByteSize;
            if (totalChunkCount * chunkByteSize < totalBytes)
                totalChunkCount++;

            LidgrenException.Assert(chunkNumber < totalChunkCount);

            if (chunkNumber >= totalChunkCount)
            {
                LogWarning(NetLogMessage.FromValues(NetLogCode.InvalidFragmentIndex,
                    message, null, value: chunkNumber, maxValue: totalChunkCount));
                return false;
            }

            NetConnection connection = message.Connection;

            if (!connection._receivedFragmentGroups.TryGetValue(group, out ReceivedFragmentGroup? info))
            {
                info = new ReceivedFragmentGroup(new byte[totalBytes], new NetBitArray(totalChunkCount));
                connection._receivedFragmentGroups.Add(group, info);
            }

            if (info.ReceivedChunks[chunkNumber])
            {
                LogDebug(NetLogMessage.FromValues(NetLogCode.DuplicateFragment,
                    value: chunkNumber, maxValue: totalChunkCount));

                return false;
            }

            info.ReceivedChunks[chunkNumber] = true;
            info.LastReceived = message.Time;
            info.Count++;

            // copy to data
            int offset = chunkNumber * chunkByteSize;
            message.Span[headerOffset..].CopyTo(info.Data.AsSpan(offset));

            //LogVerbose("Found fragment #" + chunkNumber + " in group " + group + " offset " + 
            //    offset + " of total bits " + totalBits + " (total chunks done " + cnt + ")");

            LogVerbose(NetLogMessage.FromValues(NetLogCode.ReceivedFragment, 
                value: chunkNumber, maxValue: totalChunkCount));
            
            if (info.Count != totalChunkCount)
            {
                return false;
            }

            connection._receivedFragmentGroups.Remove(group);

            // Done! Transform this incoming message
            NetIncomingMessage incomingMessage = message.ToIncomingMessage(this);
            incomingMessage.SetBuffer(info.Data, false);
            incomingMessage.BitLength = totalBits;
            incomingMessage.IsFragment = false;

            LogVerbose(NetLogMessage.FromValues(NetLogCode.FragmentGroupFinished,
                value: group, maxValue: totalChunkCount));

            ReleaseMessage(incomingMessage);
            return true;
        }
    }
}
