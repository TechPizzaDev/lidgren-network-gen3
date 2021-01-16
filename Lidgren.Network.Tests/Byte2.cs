
namespace Lidgren.Network.Tests
{
    public struct Byte2
    {
        public byte X;
        public byte Y;

        public Byte2(byte x, byte y)
        {
            X = x;
            Y = y;
        }

        public Byte2(byte value) : this(value, value)
        {
        }
    }
}
