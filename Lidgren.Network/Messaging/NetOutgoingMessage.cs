using System;
using System.Buffers;
using System.Diagnostics;

namespace Lidgren.Network
{
    /// <summary>
    /// Outgoing message used to send data to remote peers.
    /// </summary>
    [DebuggerDisplay("{DebuggerDisplay}")]
    public sealed class NetOutgoingMessage : NetBuffer
    {
        internal NetMessageType _messageType;
        internal bool _isSent; // TODO: create better error/assert for this
        internal int _recyclingCount;

        internal int _fragmentGroup;             // which group of fragments ths belongs to
        internal int _fragmentGroupTotalBits;    // total number of bits in this group
        internal int _fragmentChunkByteSize;	 // size, in bytes, of every chunk but the last one
        internal int _fragmentChunkNumber;       // which number chunk this is, starting with 0

        internal string DebuggerDisplay => $"BitLength = {BitLength}";

        public NetMessageType MessageType => _messageType;

        public NetMessageView View => new(
            NetIncomingMessageType.Error,
            MessageType,
            _fragmentGroup != 0,
            TimeSpan.Zero,
            0,
            default,
            null,
            this,
            GetBuffer().AsSpan(0, ByteLength),
            BitLength);

        public NetOutgoingMessage(ArrayPool<byte> storagePool) : base(storagePool)
        {
        }

        internal void Reset()
        {
            _messageType = NetMessageType.LibraryError;
            _isSent = false;
            _recyclingCount = 0;
            _fragmentGroup = 0;
            BitLength = 0;
        }

        internal void Encode(Span<byte> destination, ref int offset, int sequenceNumber)
        {
            //  8 bits - NetMessageType
            //  1 bit  - Fragment?
            // 15 bits - Sequence number
            // 16 bits - Payload length in bits

            destination[offset++] = (byte) _messageType;
            destination[offset++] = (byte) ((_fragmentGroup == 0 ? 0 : 1) | (sequenceNumber << 1));
            destination[offset++] = (byte) (sequenceNumber >> 7);

            int srcOffset;
            if (_fragmentGroup == 0)
            {
                ushort bitLength = checked((ushort) BitLength);
                destination[offset++] = (byte) bitLength;
                destination[offset++] = (byte) (bitLength >> 8);

                srcOffset = 0;
            }
            else
            {
                int baseOffset = offset;
                offset += 2; // reserve space for length

                NetFragmentationHelper.WriteHeader(
                    destination, ref offset,
                    _fragmentGroup, _fragmentGroupTotalBits, _fragmentChunkByteSize, _fragmentChunkNumber);
                int hdrLen = offset - baseOffset - 2;

                // write length
                ushort actualBitLength = checked((ushort) (BitLength + (hdrLen * 8)));
                destination[baseOffset + 0] = (byte) actualBitLength;
                destination[baseOffset + 1] = (byte) (actualBitLength >> 8);

                srcOffset = _fragmentChunkNumber * _fragmentChunkByteSize;
            }

            int byteLen = NetBitWriter.BytesForBits(BitLength);
            GetBuffer().AsSpan(srcOffset, byteLen).CopyTo(destination[offset..]);
            offset += byteLen;
        }

        internal void AssertNotSent(string? paramName = null)
        {
            if (_isSent)
                throw new CannotResendException(paramName);
        }

        internal int GetEncodedSize()
        {
            int size = NetConstants.UnfragmentedMessageHeaderSize; // base headers
            if (_fragmentGroup != 0)
            {
                size += NetFragmentationHelper.GetFragmentationHeaderSize(
                    _fragmentGroup, _fragmentGroupTotalBits, _fragmentChunkByteSize, _fragmentChunkNumber);
            }
            size += ByteLength;
            return size;
        }

        /// <summary>
        /// Encrypt this message using the provided algorithm.
        /// No more writing can be done before sending it or the message will be corrupt.
        /// </summary>
        public bool Encrypt(NetEncryption encryption)
        {
            if (encryption == null)
                throw new ArgumentNullException(nameof(encryption));

            return encryption.Encrypt(this);
        }

        /// <summary>
        /// Returns a <see cref="string"/> that represents this object.
        /// </summary>
        public override string ToString()
        {
            if (_isSent)
                return "{NetOutgoingMessage: " + _messageType + ", " + ByteLength + " bytes}";

            return "{NetOutgoingMessage: " + ByteLength + " bytes}";
        }
    }
}
