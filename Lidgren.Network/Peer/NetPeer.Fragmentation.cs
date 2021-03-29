using System;
using System.Threading;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading.Tasks;
using System.Buffers;

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
        ServerError
    }

    static class WaitAsyncHelper
    {
        public static async Task<bool> WaitOneAsync(this WaitHandle handle, int millisecondsTimeout, CancellationToken cancellationToken)
        {
            RegisteredWaitHandle? registeredHandle = null;
            CancellationTokenRegistration tokenRegistration = default;
            try
            {
                var tcs = new TaskCompletionSource<bool>();
                registeredHandle = ThreadPool.RegisterWaitForSingleObject(
                    handle,
                    (state, timedOut) => ((TaskCompletionSource<bool>)state!).TrySetResult(!timedOut),
                    tcs,
                    millisecondsTimeout,
                    true);
                tokenRegistration = cancellationToken.Register(
                    state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
                    tcs);
                return await tcs.Task;
            }
            finally
            {
                if (registeredHandle != null)
                    registeredHandle.Unregister(null);
                tokenRegistration.Dispose();
            }
        }
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

                Interlocked.Add(ref chunk._recyclingCount, recipientCount);

                foreach (NetConnection? recipient in recipients.AsListEnumerator())
                {
                    if (recipient == null)
                        continue;

                    NetSendResult result = recipient.EnqueueMessage(chunk, method, sequenceChannel).Result;
                    if (result > retval)
                        retval = result; // return "worst" result
                }

                bitsLeft -= bitsPerChunk;
            }
            return retval;
        }

        // on user thread
        private async ValueTask<NetSendResult> SendFragmentedMessage(
            PipeReader reader,
            NetConnection recipient,
            int sequenceChannel,
            CancellationToken cancellationToken)
        {
            int group = GetNextFragmentGroup();

            NetOutgoingMessage CreateStreamChunk(int length, NetStreamFragmentType type)
            {
                NetOutgoingMessage chunk = CreateMessage(length);
                chunk._fragmentGroup = group << 2 | 0b11;
                chunk._fragmentGroupTotalBits = (int)type;
                chunk._fragmentChunkByteSize = 0;
                chunk._fragmentChunkNumber = 0;
                return chunk;
            }

            (NetSendResult Result, NetSenderChannel?) SendChunk(NetOutgoingMessage chunk)
            {
                return recipient.EnqueueMessage(chunk, NetDeliveryMethod.ReliableOrdered, sequenceChannel);
            }

            NetSendResult finalResult = NetSendResult.Sent;
            Exception? exception = null;
            try
            {
                ReadResult readResult;
                bool sentEnding = false;
                do
                {
                    readResult = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    if (readResult.IsCanceled)
                    {
                        NetOutgoingMessage cancelChunk = CreateStreamChunk(0, NetStreamFragmentType.Cancelled);
                        finalResult = SendChunk(cancelChunk).Result;
                        break;
                    }

                    ReadOnlySequence<byte> buffer = readResult.Buffer;
                    ReadOnlySequence<byte> slice = buffer;

                    int mtu = recipient.CurrentMTU;
                    int bytesPerChunk = NetFragmentationHelper.GetBestChunkSize(group, (int)slice.Length, mtu);

                    while (slice.Length > 0)
                    {
                        long chunkLength = Math.Min(slice.Length, bytesPerChunk);
                        NetOutgoingMessage chunk = CreateStreamChunk((int)chunkLength, NetStreamFragmentType.Data);
                        foreach (ReadOnlyMemory<byte> memory in slice.Slice(0, chunkLength))
                        {
                            chunk.Write(memory.Span);
                        }
                        slice = slice.Slice(chunkLength);

                        LidgrenException.Assert(chunk.GetEncodedSize() <= mtu);
                        Interlocked.Add(ref chunk._recyclingCount, 1);

                        var (result, channel) = SendChunk(chunk);
                        if (result == NetSendResult.Queued)
                        {
                            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                        }
                        else if (result != NetSendResult.Sent)
                        {
                            NetOutgoingMessage cancelChunk = CreateStreamChunk(0, NetStreamFragmentType.Cancelled);
                            finalResult = SendChunk(cancelChunk).Result;
                            reader.CancelPendingRead();
                            break;
                        }
                    }

                    reader.AdvanceTo(buffer.End);
                }
                while (!readResult.IsCompleted);

                if (!sentEnding)
                {
                    NetOutgoingMessage endChunk = CreateStreamChunk(0, NetStreamFragmentType.EndOfStream);
                    finalResult = SendChunk(endChunk).Result;
                }
            }
            catch (Exception ex)
            {
                exception = ex;

                NetOutgoingMessage errorChunk = CreateStreamChunk(0, NetStreamFragmentType.ServerError);
                finalResult = SendChunk(errorChunk).Result;
            }

            await reader.CompleteAsync(exception).ConfigureAwait(false);

            return finalResult;
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
                var msgType = (NetStreamFragmentType)totalBits;
                HandleStreamFragment(message, headerOffset, groupNum, msgType);
                return false; // streams don't consume messages
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

            NetConnection connection = message.Connection!;

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

        private void HandleStreamFragment(in NetMessageView message, int headerOffset, int group, NetStreamFragmentType type)
        {
            NetConnection connection = message.Connection!;

            if (type == NetStreamFragmentType.Data)
            {
                if (!connection._receivedStreamFragmentGroups.TryGetValue(group, out ReceivedStreamFragmentGroup? info))
                {
                    info = new ReceivedStreamFragmentGroup();
                    connection._receivedStreamFragmentGroups.Add(group, info);

                    connection._openedStreamGroups.Enqueue(info.Pipe.Reader);

                    NetIncomingMessage noticeMessage = CreateIncomingMessage(NetIncomingMessageType.DataStream);
                    noticeMessage.SenderConnection = connection;
                    ReleaseMessage(noticeMessage);
                }

                ReadOnlySpan<byte> messageData = message.Span[headerOffset..];
                info.Pipe.Writer.Write(messageData);
                info.Pipe.Writer.FlushAsync();
            }
            else
            {
                if (!connection._receivedStreamFragmentGroups.Remove(group, out ReceivedStreamFragmentGroup? info))
                {
                    // post "empty stream" message
                    return;
                }

                info.Pipe.Writer.Complete();
            }
        }
    }
}
