using System;
using System.IO;
using System.Security.Cryptography;

namespace Lidgren.Network
{
    public class NetSymmetricEncryption<TAlgorithm> : NetEncryption
        where TAlgorithm : SymmetricAlgorithm
    {
        // TODO: cache ICryptoTransform
        // TODO: optimize by not creating a new MemoryStream for every call (possibly with span)

        public TAlgorithm Algorithm { get; private set; }

        public override bool SupportsIV => true;

        public NetSymmetricEncryption(NetPeer peer, TAlgorithm algorithm) : base(peer)
        {
            Algorithm = algorithm ?? throw new ArgumentNullException(nameof(algorithm));
        }

        public override void SetKey(byte[] key)
        {
            Algorithm.Key = key;
        }

        public override void SetIV(byte[] iv)
        {
            Algorithm.IV = iv;
        }

        public bool ValidKeySize(int bitLength)
        {
            return Algorithm.ValidKeySize(bitLength);
        }

        public override bool Encrypt(NetOutgoingMessage message)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            int unEncLenBits = message.BitLength;

            var ms = new MemoryStream();
            using (var encryptor = Algorithm.CreateEncryptor())
            using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write, true))
                cs.Write(message.GetBuffer().AsSpan(0, message.ByteLength));

            int length = (int)ms.Length;

            message.BitPosition = 0;
            message.EnsureByteCapacity(length + 4);
            message.Write((uint)unEncLenBits);
            message.Write(ms.GetBuffer().AsSpan(0, length));
            message.ByteLength = length + 4;

            return true;
        }

        public override unsafe bool Decrypt(NetIncomingMessage message)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            int unEncLenBits = (int)message.ReadUInt32();
            int byteLen = NetBitWriter.BytesForBits(unEncLenBits);
            var result = message.StoragePool.Rent(byteLen);
            
            // TODO: create stream that takes Memory<byte> overload
            fixed (byte* msgPtr = message.GetBuffer())
            {
                using var ms = new UnmanagedMemoryStream(msgPtr + 4, message.ByteLength - 4);
                using var decryptor = Algorithm.CreateDecryptor();
                using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
                cs.Read(result, 0, byteLen);
            }

            message.SetBuffer(result, true);
            message.BitLength = unEncLenBits;
            message.BitPosition = 0;

            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (!IsDisposed)
            {
                if (disposing)
                    Algorithm.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}