using System;
using Lidgren.Network;

namespace UnitTests
{
    public static class BitArrayTests
    {
        public static void Run()
        {
            NetBitArray v = new NetBitArray(256);
            for (int i = 0; i < 256; i++)
            {
                v.Clear();
                if (i > 42 && i < 65)
                    v = new NetBitArray(256);

                if (!v.IsZero)
                    throw new LidgrenException("bit vector fail 1");
                
                v.Set(i, true);

                if (!v.Get(i))
                    throw new LidgrenException("bit vector fail 2");

                if (v.IsZero)
                    throw new LidgrenException("bit vector fail 3");

                if (i != 79 && v.Get(79))
                    throw new LidgrenException("bit vector fail 4");

                int f = v.IndexOf(true);
                if (f != i)
                    throw new LidgrenException("bit vector fail 5");
            }

            v = new NetBitArray(9);
            v.Clear();
            v.Set(3, true);
            if (v.ToString() != "[000001000]")
                throw new LidgrenException("NetBitVector.RotateDown failed");
            v.RotateDown();
            if (v.Get(3) == true || v.Get(2) == false || v.Get(4) == true)
                throw new LidgrenException("NetBitVector.RotateDown failed 2");
            if (v.ToString() != "[000000100]")
                throw new LidgrenException("NetBitVector.RotateDown failed 3");

            v.Set(0, true);
            v.RotateDown();
            if (v.ToString() != "[100000010]")
                throw new LidgrenException("NetBitVector.RotateDown failed 4");

            v = new NetBitArray(38);
            v.Set(0, true);
            v.Set(1, true);
            v.Set(31, true);

            if (v.ToString() != "[00000010000000000000000000000000000011]")
                throw new LidgrenException("NetBitVector.RotateDown failed 5");

            v.RotateDown();

            if (v.ToString() != "[10000001000000000000000000000000000001]")
                throw new LidgrenException("NetBitVector.RotateDown failed 5");

            Console.WriteLine("NetBitVector tests OK");
        }
    }
}
