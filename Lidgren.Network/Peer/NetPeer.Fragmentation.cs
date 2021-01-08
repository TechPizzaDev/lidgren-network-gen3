using System;
using System.Threading;
using System.Collections.Generic;

namespace Lidgren.Network
{
    internal readonly struct ReceivedFragmentGroup
    {
        public byte[] Data { get; }
        public NetBitVector ReceivedChunks { get; }
        //public TimeSpan LastReceived { get; set; } // TODO: discard after certain age

        public ReceivedFragmentGroup(byte[] data, NetBitVector receivedChunks)
        {
            Data = data;
            ReceivedChunks = receivedChunks;
        }
    }

    public partial class NetPeer
    {
        private int _lastUsedFragmentGroup;

        private Dictionary<NetConnection, Dictionary<int, ReceivedFragmentGroup>> _receivedFragmentGroups =
            new Dictionary<NetConnection, Dictionary<int, ReceivedFragmentGroup>>();

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
            message._fragmentGroup = group;

            // do not send msg; but set fragmentgroup in case user tries to recycle it immediately

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
                chunk.SetBuffer(buffer);
                chunk.BitLength = Math.Min(bitsLeft, bitsPerChunk);

                chunk._fragmentGroup = group;
                chunk._fragmentGroupTotalBits = totalBytes * 8;
                chunk._fragmentChunkByteSize = bytesPerChunk;
                chunk._fragmentChunkNumber = i;

                LidgrenException.Assert(chunk.BitLength != 0);
                LidgrenException.Assert(chunk.GetEncodedSize() < mtu);

                Interlocked.Add(ref chunk._recyclingCount, recipientCount);

                foreach (var recipient in recipients.AsListEnumerator())
                {
                    if (recipient == null)
                        continue;

                    var result = recipient.EnqueueMessage(chunk, method, sequenceChannel);
                    if ((int)result > (int)retval)
                        retval = result; // return "worst" result
                }

                bitsLeft -= bitsPerChunk;
            }
            return retval;
        }

        private void HandleReleasedFragment(NetIncomingMessage message)
        {
            if (message.SenderConnection == null)
                throw new ArgumentException("The message has no associated connection.", nameof(message));

            AssertIsOnLibraryThread();

            // read fragmentation header and combine fragments
            int headerOffset = 0;
            if(!NetFragmentationHelper.ReadHeader(
                message.GetBuffer(),
                ref headerOffset,
                out int group,
                out int totalBits,
                out int chunkByteSize,
                out int chunkNumber))
            {
                LogWarning("Failed to read fragmentation header.");
                return;
            }

            LidgrenException.Assert(message.ByteLength > headerOffset);
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
                LogWarning("Index out of bounds for chunk " + chunkNumber + " (total chunks " + totalChunkCount + ")");
                return;
            }

            if (!_receivedFragmentGroups.TryGetValue(message.SenderConnection, out var groups))
            {
                groups = new Dictionary<int, ReceivedFragmentGroup>();
                _receivedFragmentGroups.Add(message.SenderConnection, groups);
            }

            if (!groups.TryGetValue(group, out ReceivedFragmentGroup info))
            {
                info = new ReceivedFragmentGroup(new byte[totalBytes], new NetBitVector(totalChunkCount));
                groups.Add(group, info);
            }

            info.ReceivedChunks[chunkNumber] = true;
            //info.LastReceived = NetTime.Now;

            // copy to data
            int offset = chunkNumber * chunkByteSize;
            message.GetBuffer().AsSpan()[headerOffset..message.ByteLength].CopyTo(info.Data.AsSpan(offset));

            int chunkCount = info.ReceivedChunks.PopCount;

            //LogVerbose("Found fragment #" + chunkNumber + " in group " + group + " offset " + 
            //    offset + " of total bits " + totalBits + " (total chunks done " + cnt + ")");

            LogVerbose(
                "Received fragment " + chunkNumber + " of " + totalChunkCount + " (" + chunkCount + " chunks received)");

            if (chunkCount == totalChunkCount)
            {
                // Done! Transform this incoming message
                message.BitLength = 0;
                message.TrimExcess();

                message.SetBuffer(info.Data);
                message.BitLength = totalBits;

                message.IsFragment = false;

                LogVerbose(
                    "Fragment group #" + group + " fully received in " +
                    totalChunkCount + " chunks (" + totalBits + " bits)");

                groups.Remove(group);

                ReleaseMessage(message);
            }
            else
            {
                // data has been copied; recycle this incoming message
                Recycle(message);
            }
            return;
        }
    }
}
