using System;
using Lidgren.Network;
using System.Reflection;
using System.Text;

namespace UnitTests
{
    public static class ReadWriteTests
    {
        // TODO: better and cleaner/more readable tests

        public static void Run(NetPeer peer)
        {
            NetOutgoingMessage msg = peer.CreateMessage();

            msg.Write(false);
            msg.Write(-3, 6);
            msg.Write(42);
            msg.Write("duke of earl");
            msg.Write((byte)43);
            msg.Write((ushort)44);
            msg.Write(ulong.MaxValue, 64);
            msg.Write(true);
            
            msg.WritePadBits();
            
            int bcnt = 0;
            
            msg.Write(567845.0f);
            msg.WriteVar(2115998022);
            msg.Write(46.0);
            msg.Write((ushort)14, 9);
            bcnt += msg.WriteVar(-47);
            msg.WriteVar(470000);
            msg.WriteVar((uint)48);
            bcnt += msg.WriteVar((long)-49);
            
            if (bcnt != 2)
                throw new LidgrenException("WriteVar* wrote too many bytes!");
           
            NetIncomingMessage inc = Program.CreateIncomingMessage(msg.GetBuffer(), msg.BitLength);

            var bdr = new StringBuilder();
            char space = ' ';

            bdr.Append(inc.ReadBool()).Append(space);
            bdr.Append(inc.ReadInt32(6)).Append(space);
            bdr.Append(inc.ReadInt32()).Append(space);

            if (!inc.ReadString(out string strResult))
                throw new LidgrenException("Read/write failure");
            bdr.Append(strResult).Append(space);
            
            bdr.Append(inc.ReadByte()).Append(space);

            if (inc.PeekUInt16() != 44)
                throw new LidgrenException("Read/write failure");
            bdr.Append(inc.ReadUInt16()).Append(space);

            var pp = inc.PeekUInt64(64);
            if (pp != ulong.MaxValue)
                throw new LidgrenException("Read/write failure");
            bdr.Append(inc.ReadUInt64()).Append(space);

            bdr.Append(inc.ReadBool()).Append(space);
        
            inc.SkipPadBits();

            bdr.Append(inc.ReadSingle()).Append(space);
            bdr.Append(inc.ReadVarInt32()).Append(space);
            bdr.Append(inc.ReadDouble()).Append(space);
            bdr.Append(inc.ReadUInt32(9)).Append(space);
            bdr.Append(inc.ReadVarInt32()).Append(space);
            bdr.Append(inc.ReadVarInt32()).Append(space);
            bdr.Append(inc.ReadVarUInt32()).Append(space);
            bdr.Append(inc.ReadVarInt64()).Append(space);

            var bdrr = bdr.ToString();
            if (bdrr.Equals("False -3 42 duke of earl 43 44 18446744073709551615 True 567845 2115998022 46 14 -47 470000 48 -49 "))
                Console.WriteLine("Read/write tests OK");
            else
                throw new LidgrenException($"Read/write tests FAILED! ({bdrr})");

            msg = peer.CreateMessage();

            NetOutgoingMessage tmp = peer.CreateMessage();
            tmp.Write(42, 14);
            tmp.BitPosition = 0;

            msg.Write(tmp);
            msg.Write(tmp);

            if (msg.BitLength != tmp.BitLength * 2)
                throw new LidgrenException("NetOutgoingMessage.Write(NetOutgoingMessage) failed!");

            tmp = peer.CreateMessage();

            var test = new Test();
            test.Number = 42;
            test.Name = "Hallon";
            test.Age = 8.2f;

            tmp.WriteAllFields(test, BindingFlags.Public | BindingFlags.Instance);

            inc = Program.CreateIncomingMessage(tmp.GetBuffer(), tmp.BitLength);

            var readTest = new Test();
            inc.ReadAllFields(readTest, BindingFlags.Public | BindingFlags.Instance);

            LidgrenException.Assert(readTest.Number == 42);
            LidgrenException.Assert(readTest.Name == "Hallon");
            LidgrenException.Assert(readTest.Age == 8.2f);
            
            // test aligned WriteBytes/ReadBytes
            msg = peer.CreateMessage();
            var originalData = new byte[] { 5, 6, 7, 8, 9 };
            msg.Write(originalData);

            inc = Program.CreateIncomingMessage(msg.GetBuffer(), msg.BitLength);
            var readData = new byte[originalData.Length]; 
            inc.Read(readData);

            if(!readData.AsSpan().SequenceEqual(originalData))
                    throw new Exception("Read fail");
        }
    }

    public class TestBase
    {
        public int Number;
    }

    public class Test : TestBase
    {
        public float Age;
        public string Name;
    }
}
