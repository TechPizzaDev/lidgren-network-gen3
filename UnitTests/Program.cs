using System;
using System.Reflection;
using Lidgren.Network;
using System.Net;
using System.Runtime.Intrinsics.X86;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Threading;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Net.Sockets;

namespace UnitTests
{
    class Program
    {
        static void Main(string[] args)
        {
            //Span<byte> src = new byte[3] { 254, 255, 255 };
            //Span<byte> dst = new byte[3];
            //NetBitWriter.CopyBits(src, 1, 17, dst, 1);

            // TODO: check use of GetAddressBytes and optimize with span

            //Span<byte> src = new byte[] { 255, 255 }; // 0b00110010, 0b00111100 };
            //Span<byte> dst = new byte[3] { 0, 0, 0 };
            //NetBitWriter.CopyBits(src, 0, 16, dst, 9);
            //string r =
            //    Convert.ToString(dst[0], 2).PadLeft(8, '0') + "_" +
            //    Convert.ToString(dst[1], 2).PadLeft(8, '0') + "_" +
            //    Convert.ToString(dst[2], 2).PadLeft(8, '0');
            //Console.WriteLine(r);

            NetQueueTests.Run();

            BitArrayTests.Run();

            var config = new NetPeerConfiguration("unittests");
            config.EnableMessageType(NetIncomingMessageType.UnconnectedData);
            config.EnableUPnP = true;

            var peer = new NetPeer(config);
            peer.Start(); // needed for initialization

            Console.WriteLine("Unique identifier is " + NetUtility.ToHexString(peer.UniqueIdentifier));

            ReadWriteTests.Run(peer);

            MiscTests.Run();

            NetStreamingMessageTest.Run();

            //NetStreamTests.Run();

            //EncryptionTests.Run(peer);

            Console.WriteLine();

            // create threads that read all messages to test concurrency
            int readTimeout = 5000;
            var readThreads = new List<Thread>();
            for (int i = 0; i < 100; i++)
            {
                var thread = new Thread(() =>
                {
                    while (peer.TryReadMessage(readTimeout, out var message))
                    {
                        switch (message.MessageType)
                        {
                            case NetIncomingMessageType.DebugMessage:
                            case NetIncomingMessageType.VerboseDebugMessage:
                            case NetIncomingMessageType.WarningMessage:
                            case NetIncomingMessageType.ErrorMessage:
                                Console.WriteLine("Peer message: " + message.ReadString());
                                break;

                            case NetIncomingMessageType.Error:
                                throw new Exception("Received error message!");

                            case NetIncomingMessageType.Data:
                                Console.WriteLine("Data: " + message.ReadString());
                                break;

                            case NetIncomingMessageType.UnconnectedData:
                                Console.WriteLine("UnconnectedData: " + message.ReadString());
                                break;

                            default:
                                Console.WriteLine(message.MessageType);
                                break;
                        }
                    }
                });
                thread.Start();
                readThreads.Add(thread);
            }

            var om = peer.CreateMessage("henlo from myself");
            peer.SendUnconnectedMessage(om, new IPEndPoint(IPAddress.Loopback, peer.Port));
            try
            {
                peer.SendUnconnectedMessage(om, new IPEndPoint(IPAddress.Loopback, peer.Port));

                Console.WriteLine(nameof(CannotResendException) + " check failed");
            }
            catch (CannotResendException)
            {
                Console.WriteLine(nameof(CannotResendException) + " check OK");
            }

            Console.WriteLine($"Waiting for messages with {readTimeout}ms timeout...");
            foreach (var thread in readThreads)
                thread.Join();

            Console.WriteLine();
            Console.WriteLine("Tests finished");
        }

        public static NetIncomingMessage CreateIncomingMessage(ReadOnlySpan<byte> fromData, int bitLength)
        {
            var message = new NetIncomingMessage(ArrayPool<byte>.Shared);
            message.Write(fromData);
            message.BitPosition = 0;
            message.BitLength = bitLength;
            return message;
        }
    }
}