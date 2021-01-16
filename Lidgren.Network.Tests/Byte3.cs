
namespace Lidgren.Network.Tests
{
    public struct Byte3
    {
        public byte X;
        public byte Y;
        public byte Z;

        public Byte3(byte x, byte y, byte z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public Byte3(Byte2 xy, byte z) : this(xy.X, xy.Y, z)
        {
        }

        public Byte3(byte value) : this(value, value, value)
        {
        }
    }
}
