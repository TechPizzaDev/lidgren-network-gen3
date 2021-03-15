using System;
using System.Buffers;
using System.Diagnostics;
using System.Text;

namespace Lidgren.Network
{
    public class NetBuffer : IBitBuffer
    {
        /// <summary>
        /// Number of extra bytes to overallocate for message buffers to avoid resizing.
        /// </summary>
        protected const int ExtraGrowAmount = 512; // TODO: move to config

        public static Encoding StringEncoding { get; } = new UTF8Encoding(false, false);

        private int _bitPosition;
        private int _bitLength;
        private byte[] _buffer;
        private bool _recyclableBuffer;
        private bool _isDisposed;

        public ArrayPool<byte> StoragePool { get; }

        public int BitPosition
        {
            get => _bitPosition;
            set => _bitPosition = value;
        }

        public int BytePosition
        {
            get => NetUtility.DivBy8(BitPosition);
            set => BitPosition = value * 8;
        }

        public int BitLength
        {
            get => _bitLength;
            set
            {
                EnsureBitCapacity(value);
                _bitLength = value;
                if (_bitPosition > _bitLength)
                    _bitPosition = _bitLength;
            }
        }

        public int ByteLength
        {
            get => NetBitWriter.BytesForBits(_bitLength);
            set => BitLength = value * 8;
        }

        public int BitCapacity
        {
            get => _buffer.Length * 8;
            set => ByteCapacity = NetBitWriter.BytesForBits(value);
        }

        public int ByteCapacity
        {
            get => _buffer.Length;
            set
            {
                Debug.Assert(value >= 0);

                if (value > _buffer.Length)
                {
                    if (_isDisposed)
                        throw new ObjectDisposedException(GetType().FullName);

                    byte[] newBuffer = StoragePool.Rent(value);
                    _buffer.AsMemory(0, ByteLength).CopyTo(newBuffer);
                    SetBuffer(newBuffer);
                }
            }
        }

        public NetBuffer(ArrayPool<byte> storagePool)
        {
            StoragePool = storagePool ?? throw new ArgumentNullException(nameof(storagePool));
            _buffer = Array.Empty<byte>();
        }

        public void EnsureBitCapacity(int bitCount)
        {
            Debug.Assert(bitCount >= 0);

            int byteLength = NetBitWriter.BytesForBits(bitCount);
            if (ByteCapacity < byteLength)
                ByteCapacity = byteLength + ExtraGrowAmount;
        }

        public void IncrementBitPosition(int bitCount)
        {
            Debug.Assert(bitCount >= 0);
            
            _bitPosition += bitCount;
            this.SetLengthByPosition();
        }

        public byte[] GetBuffer()
        {
            return _buffer;
        }

        public void SetBuffer(byte[] buffer, bool isRecyclable = true)
        {
            if (_recyclableBuffer)
            {
                StoragePool.Return(_buffer);
            }

            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            _recyclableBuffer = isRecyclable;
        }

        public void TrimExcess()
        {
            if (_bitLength == 0)
                Recycle();
        }

        private void Recycle()
        {
            if (_recyclableBuffer)
            {
                StoragePool.Return(_buffer);
                _buffer = Array.Empty<byte>();
                _bitLength = 0;
                _bitPosition = 0;
                _recyclableBuffer = false;
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    Recycle();
                }
                _isDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
