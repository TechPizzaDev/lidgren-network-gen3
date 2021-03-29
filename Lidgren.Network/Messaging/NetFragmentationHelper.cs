using System;

namespace Lidgren.Network
{
    public static class NetFragmentationHelper
    {
        public static void WriteHeader(
            Span<byte> destination,
            ref int offset,
            int group,
            int totalBits,
            int chunkByteSize,
            int chunkNumber)
        {
            uint num1 = (uint)group;
            while (num1 >= 0x80)
            {
                destination[offset++] = (byte)(num1 | 0x80);
                num1 >>= 7;
            }
            destination[offset++] = (byte)num1;

            // write variable length fragment total bits
            uint num2 = (uint)totalBits;
            while (num2 >= 0x80)
            {
                destination[offset++] = (byte)(num2 | 0x80);
                num2 >>= 7;
            }
            destination[offset++] = (byte)num2;

            if ((group & 0b10) != 0)
                return; // this is a stream fragment

            // write variable length fragment chunk size
            uint num3 = (uint)chunkByteSize;
            while (num3 >= 0x80)
            {
                destination[offset++] = (byte)(num3 | 0x80);
                num3 >>= 7;
            }
            destination[offset++] = (byte)num3;

            // write variable length fragment chunk number
            uint num4 = (uint)chunkNumber;
            while (num4 >= 0x80)
            {
                destination[offset++] = (byte)(num4 | 0x80);
                num4 >>= 7;
            }
            destination[offset++] = (byte)num4;
        }

        public static bool ReadHeader(
            ReadOnlySpan<byte> buffer, ref int offset,
            out int group, out int totalBits, out int chunkByteSize, out int chunkNumber)
        {
            group = 0;
            totalBits = 0;
            chunkByteSize = 0;
            chunkNumber = -1;

            int part = 0;
            int shift = 0;
            while (offset < buffer.Length)
            {
                int num3 = buffer[offset++];
                part |= (num3 & 0x7f) << (shift & 0x1f);
                shift += 7;
                if ((num3 & 0x80) == 0)
                {
                    group = part;
                    break;
                }
            }

            part = 0;
            shift = 0;
            while (offset < buffer.Length)
            {
                int num3 = buffer[offset++];
                part |= (num3 & 0x7f) << (shift & 0x1f);
                shift += 7;
                if ((num3 & 0x80) == 0)
                {
                    totalBits = part;
                    if ((group & 0b10) != 0)
                    {
                        // this is a stream fragment
                        return true;
                    }
                    break;
                }
            }

            part = 0;
            shift = 0;
            while (offset < buffer.Length)
            {
                int num3 = buffer[offset++];
                part |= (num3 & 0x7f) << (shift & 0x1f);
                shift += 7;
                if ((num3 & 0x80) == 0)
                {
                    chunkByteSize = part;
                    break;
                }
            }

            part = 0;
            shift = 0;
            while (offset < buffer.Length)
            {
                int num3 = buffer[offset++];
                part |= (num3 & 0x7f) << (shift & 0x1f);
                shift += 7;
                if ((num3 & 0x80) == 0)
                {
                    chunkNumber = part;
                    break;
                }
            }

            return group != 0
                && totalBits != 0
                && chunkByteSize != 0
                && chunkNumber != -1;
        }

        public static int GetFragmentationHeaderSize(int groupId, int totalBits, int chunkByteSize, int numChunks)
        {
            return (
                NetBitWriter.BitsForValue((uint)groupId) +
                NetBitWriter.BitsForValue((uint)totalBits) +
                NetBitWriter.BitsForValue((uint)chunkByteSize) +
                NetBitWriter.BitsForValue((uint)numChunks)) / 7 + 4;
        }

        public static int GetBestChunkSize(int group, int totalBytes, int mtu)
        {
            // TODO: optimize

            int totalBits = totalBytes * 8;
            int tryChunkSize = mtu - NetConstants.HeaderSize - 4; // naive approximation
            int est = GetFragmentationHeaderSize(group, totalBits, tryChunkSize, totalBytes / tryChunkSize);
            tryChunkSize = mtu - NetConstants.HeaderSize - est; // slightly less naive approximation

            int headerSize;
            do
            {
                tryChunkSize--; // keep reducing chunk size until it fits within MTU including header

                int numChunks = totalBytes / tryChunkSize;
                if (numChunks * tryChunkSize < totalBytes)
                    numChunks++;

                headerSize = GetFragmentationHeaderSize(group, totalBits, tryChunkSize, numChunks); // 4+ bytes

            }
            while (tryChunkSize + headerSize + NetConstants.HeaderSize + 1 >= mtu);

            return tryChunkSize;
        }
    }
}
