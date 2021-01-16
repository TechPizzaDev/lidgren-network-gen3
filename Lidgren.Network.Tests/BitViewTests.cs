using System.Numerics;
using Xunit;

namespace Lidgren.Network.Tests.BitVector
{
    public class BitVectorByte : BitViewTest<byte>
    {
        [Fact]
        public void ValueToVectorComparison()
        {
            ValueComparison(byte.MinValue, byte.MaxValue);
            ValueComparison(1, 2);
        }
    }

    public class BitVectorInt16 : BitViewTest<ushort>
    {
        [Fact]
        public void ValueToVectorComparison()
        {
            ValueComparison(ushort.MinValue, ushort.MaxValue);
            ValueComparison(1, 2);
        }
    }

    public class BitVectorByte3 : BitViewTest<Byte3>
    {
        [Fact]
        public void ValueToVectorComparison()
        {
            ValueComparison(new Byte3(byte.MinValue), new Byte3(byte.MaxValue));
            ValueComparison(new Byte3(1), new Byte3(2));
        }
    }

    public class BitVectorInt32 : BitViewTest<int>
    {
        [Fact]
        public void ValueToVectorComparison()
        {
            ValueComparison(int.MinValue, int.MaxValue);
            ValueComparison(1, 2);
        }
    }

    public class BitVectorInt64 : BitViewTest<long>
    {
        [Fact]
        public void ValueToVectorComparison()
        {
            ValueComparison(long.MinValue, long.MaxValue);
            ValueComparison(1, 2);
        }
    }

    public class BitVectorNumericsByteVector : BitViewTest<Vector<byte>>
    {
        [Fact]
        public void ValueToVectorComparison()
        {
            ValueComparison(new Vector<byte>(byte.MinValue), new Vector<byte>(byte.MaxValue));
            ValueComparison(new Vector<byte>(1), new Vector<byte>(2));
        }
    }
}
