using System.Buffers;
using Xunit;

namespace Lidgren.Network.Tests
{
    public class BitArrayTests
    {
        [Fact]
        public void Equality()
        {
            NetBitArray array1 = new NetBitArray(1);
            NetBitArray array2 = new NetBitArray(2);

            Assert.True(array1.Equals(array1));
            Assert.False(array1.Equals(array2));
            Assert.False(array2.Equals(array1));

            array1[0] = true;
            array2[0] = true;

            Assert.True(array1.Equals(array1));
            Assert.False(array1.Equals(array2));
            Assert.False(array2.Equals(array1));
        }

        [Fact]
        public void Slice()
        {
            for (int i = 0; i < NetBitArray.BitsPerElement * (64 + 1) + 1; i++)
            {
                NetBitArray array1 = new NetBitArray(i);

                NetBitArray array2 = array1[..array1.Length];
                Assert.True(array1.Equals(array2));

                NetBitArray array3 = array1[0..^0];
                Assert.True(array1.Equals(array3));

                NetBitArray array4 = array1[array1.Length..];
                Assert.True(array4.Length == 0);
            }
        }

        [Fact]
        public void Write()
        {
            NetBuffer buffer = new NetBuffer(ArrayPool<byte>.Shared);

            for (int i = 0; i < NetBitArray.BitsPerElement * (64 + 1) + 1; i++)
            {
                NetBitArray array = new NetBitArray(i);

                buffer.Write(array);

                buffer.BitPosition = 0;

                NetBitArray readArray = buffer.ReadBitArray();

                //Assert.True(array == readArray);
            }
        }
    }
}
