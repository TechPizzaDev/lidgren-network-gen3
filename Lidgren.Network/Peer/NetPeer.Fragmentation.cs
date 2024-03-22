using System;
using System.Threading;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading.Tasks;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Lidgren.Network
{
    internal class ReceivedNormalFragmentGroup
    {
        public byte[] Data;
        public NetBitArray ReceivedChunks;
        public int Count;
        public TimeSpan LastReceived;

        public ReceivedNormalFragmentGroup(byte[] data, NetBitArray receivedChunks)
        {
            Data = data;
            ReceivedChunks = receivedChunks;
        }
    }

    internal class ReceivedStreamFragmentGroup
    {
        public Pipe Pipe = new();
    }

    public enum NetStreamFragmentType
    {
        Data,
        EndOfStream,
        Cancelled,
        SendFailure,
        ServerError
    }

    public partial class NetPeer
    {
        private int _lastUsedFragmentGroup;

        private int GetNextFragmentGroup()
        {
            // Note: this group id is PER SENDING/NetPeer; ie. same id is sent to all recipients;
            // this should be ok however; as long as recipients differentiate between same id but different sender
            int group = Interlocked.Increment(ref _lastUsedFragmentGroup);
            if (group >= NetConstants.MaxFragmentationGroups)
            {
                // TODO: not thread safe; but in practice probably not an issue
                _lastUsedFragmentGroup = 1;
                group = 1;
            }
            return group;
        }

        // on user thread
        // the message must not be sent already
        private NetSendResult SendFragmentedMessage(
            NetOutgoingMessage message,
            List<NetConnection> recipients,
            NetDeliveryMethod method,
            int sequenceChannel)
        {
            // determine minimum mtu for all recipients
            int mtu = GetMTU(recipients!, out _);
            int group = GetNextFragmentGroup();

            // do not send msg; but set fragmentgroup in case user tries to recycle it immediately
            message._fragmentGroup = group << 2 | 1;

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

                chunk._fragmentGroup = group << 2 | 1;
                chunk._fragmentGroupTotalBits = totalBytes * 8;
                chunk._fragmentChunkByteSize = bytesPerChunk;
                chunk._fragmentChunkNumber = i;

                LidgrenException.Assert(chunk.BitLength != 0);
                LidgrenException.Assert(chunk.GetEncodedSize() <= mtu);

                Interlocked.Add(ref chunk._recyclingCount, recipients.Count);

                foreach (NetConnection recipient in CollectionsMarshal.AsSpan(recipients))
                {
                    NetSendResult result = recipient.EnqueueMessage(chunk, method, sequenceChannel).Result;
                    if (result > retval)
                        retval = result; // return "worst" result
                }

                bitsLeft -= bitsPerChunk;
            }
            return retval;
        }

        private NetOutgoingMessage CreateStreamChunk(int length, int group, NetStreamFragmentType type)
        {
            NetOutgoingMessage chunk = CreateMessage(length);
            chunk._fragmentGroup = group << 2 | 0b11;
            chunk._fragmentGroupTotalBits = (int) type;
            chunk._fragmentChunkByteSize = 0;
            chunk._fragmentChunkNumber = 0;
            return chunk;
        }

        /// <remarks>
        /// Called on user thread.
        /// </remarks>
        internal async ValueTask<NetSendResult> SendFragmentedMessageAsync(
            PipeReader reader,
            NetConnection recipient,
            int sequenceChannel,
            CancellationToken cancellationToken)
        {
            int group = GetNextFragmentGroup();

            (NetSendResult Result, NetSenderChannel?) SendChunk(NetOutgoingMessage chunk)
            {
                return recipient.EnqueueMessage(chunk, NetDeliveryMethod.ReliableOrdered, sequenceChannel);
            }

            NetSendResult? finalResult = null;
            Exception? exception = null;
            try
            {
                ReadResult readResult;
                do
                {
                    readResult = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    if (readResult.IsCanceled)
                    {
                        NetOutgoingMessage cancelChunk = CreateStreamChunk(0, group, NetStreamFragmentType.Cancelled);
                        finalResult = SendChunk(cancelChunk).Result;
                        break;
                    }

                    ReadOnlySequence<byte> buffer = readResult.Buffer;

                    int mtu = recipient.CurrentMTU;
                    int bytesPerChunk = NetFragmentationHelper.GetBestChunkSize(group, (int) buffer.Length, mtu);

                    int chunkLength = (int) Math.Min(buffer.Length, bytesPerChunk);
                    NetOutgoingMessage chunk = CreateStreamChunk(chunkLength, group, NetStreamFragmentType.Data);

                    ReadOnlySequence<byte> consumedBuffer = buffer.Slice(0, chunkLength);
                    foreach (ReadOnlyMemory<byte> memory in consumedBuffer)
                    {
                        chunk.Write(memory.Span);
                    }

                    reader.AdvanceTo(consumedBuffer.End);

                    LidgrenException.Assert(chunk.GetEncodedSize() <= mtu);
                    Interlocked.Add(ref chunk._recyclingCount, 1);

                    var (result, channel) = SendChunk(chunk);
                    if (result == NetSendResult.Queued)
                    {
                        int freeSlots = await channel!.WaitForIdleAsync(millisecondsTimeout: 10000, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else if (result != NetSendResult.Sent)
                    {
                        NetOutgoingMessage cancelChunk = CreateStreamChunk(0, group, NetStreamFragmentType.SendFailure);
                        finalResult = SendChunk(cancelChunk).Result;

                        reader.CancelPendingRead();
                        break;
                    }
                }
                while (!readResult.IsCompleted);

                if (finalResult == null)
                {
                    NetOutgoingMessage endChunk = CreateStreamChunk(0, group, NetStreamFragmentType.EndOfStream);
                    finalResult = SendChunk(endChunk).Result;
                }
            }
            catch (Exception ex)
            {
                exception = ex;

                NetOutgoingMessage errorChunk = CreateStreamChunk(0, group, NetStreamFragmentType.ServerError);
                finalResult = SendChunk(errorChunk).Result;
            }

            await reader.CompleteAsync(exception).ConfigureAwait(false);

            return finalResult.Value;
        }

        /// <summary>
        /// Parses a fragment message and dispatches the appropriate callbacks.
        /// </summary>
        /// <returns>Whether <paramref name="message"/> was consumed and should not be recycled.</returns>
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

            int groupNum = group >> 2;

            LidgrenException.Assert(message.Span.Length >= headerOffset);
            LidgrenException.Assert(groupNum > 0);

            if ((group & 0b10) != 0)
            {
                var msgType = (NetStreamFragmentType) totalBits;
                ValueTask streamTask = HandleStreamFragment(message, headerOffset, groupNum, msgType, out _);
                if (!streamTask.IsCompleted)
                {
                    streamTask.GetAwaiter().GetResult();
                }
                return false; // streams don't forward messages
            }

            return HandleNormalFragment(
                message, headerOffset, groupNum, totalBits, chunkByteSize, chunkNumber);
        }

        private bool HandleNormalFragment(
            in NetMessageView message, int headerOffset,
            int group, int totalBits, int chunkByteSize, int chunkNumber)
        {
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

            NetConnection? connection = message.Connection;
            Debug.Assert(connection != null);

            if (!connection._receivedNormalFragmentGroups.TryGetValue(group, out ReceivedNormalFragmentGroup? info))
            {
                info = new ReceivedNormalFragmentGroup(new byte[totalBytes], new NetBitArray(totalChunkCount));
                connection._receivedNormalFragmentGroups.Add(group, info);
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

            connection._receivedNormalFragmentGroups.Remove(group);

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

        private ValueTask HandleStreamFragment(
            in NetMessageView message, int headerOffset, int group, NetStreamFragmentType type, out ReceivedStreamFragmentGroup? info)
        {
            NetConnection connection = message.Connection!;

            if (type == NetStreamFragmentType.Data)
            {
                if (!connection._receivedStreamFragmentGroups.TryGetValue(group, out info))
                {
                    info = new ReceivedStreamFragmentGroup();
                    connection._receivedStreamFragmentGroups.Add(group, info);

                    connection._openedStreamGroups.Enqueue(info.Pipe.Reader);

                    NetIncomingMessage noticeMessage = CreateIncomingMessage(NetIncomingMessageType.DataStream, message.Address);
                    noticeMessage.SenderConnection = connection;
                    noticeMessage.ReceiveTime = message.Time;
                    ReleaseMessage(noticeMessage);
                }

                ReadOnlySpan<byte> messageData = message.Span[headerOffset..];
                info.Pipe.Writer.Write(messageData);
                return FlushStream(info);
            }

            if (connection._receivedStreamFragmentGroups.Remove(group, out info))
            {
                if (type == NetStreamFragmentType.EndOfStream)
                {
                    return info.Pipe.Writer.CompleteAsync();
                }

                info.Pipe.Reader.CancelPendingRead();
            }
            else
            {
                // post "empty stream" message
            }
            return ValueTask.CompletedTask;
        }

        private async ValueTask FlushStream(ReceivedStreamFragmentGroup info)
        {
            FlushResult result = await info.Pipe.Writer.FlushAsync();
            if (result.IsCompleted)
            {
                // TODO: send NetStreamFragmentType.Cancelled message?
            }
        }
    }
}
