using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Lidgren.Network
{
    /// <summary>
    /// Methods to encrypt and decrypt data using the XTEA algorithm.
    /// </summary>
    public sealed class NetXteaEncryption : NetBlockEncryption
    {
        private const int KeySize = 16;
        //private const int c_delta = unchecked((int)0x9E3779B9);

        private readonly int _rounds;
        private readonly uint[] _sum0;
        private readonly uint[] _sum1;

        /// <summary>
        /// Gets the block size for this cipher.
        /// </summary>
        public override int BlockSize => 8;

        public override bool SupportsIV => false;

        public NetXteaEncryption(NetPeer peer, int rounds = 32) : base(peer)
        {
            _rounds = rounds;
            _sum0 = new uint[_rounds];
            _sum1 = new uint[_rounds];
        }

        public override void SetKey(byte[] data)
        {
            Span<byte> key = stackalloc byte[KeySize];
            if (data.Length > key.Length)
                SHA256.HashData(data, key);
            else
                data.CopyTo(key);

            Span<uint> tmp = stackalloc uint[8];
            int i = 0;
            int j = 0;
            while (i < 4)
            {
                tmp[i] = BinaryPrimitives.ReadUInt32LittleEndian(key[j..]);
                i++;
                j += 4;
            }
            for (i = j = 0; i < 32; i++)
            {
                _sum0[i] = ((uint)j) + tmp[j & 3];
                j += -1640531527;
                _sum1[i] = ((uint)j) + tmp[(j >> 11) & 3];
            }
        }

        public override void SetIV(byte[] iv)
        {
            // TODO:
            throw new NotSupportedException();
        }

        protected override void EncryptBlock(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            uint v0 = BinaryPrimitives.ReadUInt32LittleEndian(source);
            uint v1 = BinaryPrimitives.ReadUInt32LittleEndian(source[4..]);

            for (int i = 0; i < _rounds; i++)
            {
                v0 += (((v1 << 4) ^ (v1 >> 5)) + v1) ^ _sum0[i];
                v1 += (((v0 << 4) ^ (v0 >> 5)) + v0) ^ _sum1[i];
            }

            BinaryPrimitives.WriteUInt32LittleEndian(destination, v0);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], v1);
        }

        protected override void DecryptBlock(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            uint v0 = BinaryPrimitives.ReadUInt32LittleEndian(source);
            uint v1 = BinaryPrimitives.ReadUInt32LittleEndian(source[4..]);

            for (int i = _rounds; i-- > 0;)
            {
                v1 -= (((v0 << 4) ^ (v0 >> 5)) + v0) ^ _sum1[i];
                v0 -= (((v1 << 4) ^ (v1 >> 5)) + v1) ^ _sum0[i];
            }

            BinaryPrimitives.WriteUInt32LittleEndian(destination, v0);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], v1);
        }
    }
}