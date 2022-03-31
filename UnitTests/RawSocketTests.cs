using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace UnitTests
{
    public static class RawSocketTests
    {
        public static void Run()
        {
            Thread serverThread = new(ServerThread);
            serverThread.Priority = ThreadPriority.AboveNormal;
            serverThread.Start();

            Thread[] clientThreads = new Thread[100];
            for (int i = 0; i < clientThreads.Length; i++)
            {
                clientThreads[i] = new Thread(ClientThread);
                clientThreads[i].Start();
            }

            serverThread.Join();
            for (int i = 0; i < clientThreads.Length; i++)
            {
                clientThreads[i].Join();
            }
        }

        private static void ServerThread()
        {
            Socket server = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            server.Blocking = false;
            server.ReceiveBufferSize = 1024 * 1024;

            server.Bind(new IPEndPoint(IPAddress.Any, 12345));

            Span<byte> buffer = stackalloc byte[10000];

            HashSet<IPEndPoint> connections = new();

            long bytesReceived = 0;
            long packetsReceived = 0;
            long lastBytesReceived = 0;
            long lastPacketsReceived = 0;
            TimeSpan lastTime = TimeSpan.Zero;

            Stopwatch realWatch = new();
            Stopwatch watch = new();

            realWatch.Start();
            watch.Start();

            EndPoint endPoint = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                if (watch.ElapsedMilliseconds >= 20)
                {
                    watch.Restart();
                    TimeSpan timeNow = realWatch.Elapsed;

                    long diffBytesReceived = bytesReceived - lastBytesReceived;
                    long diffPacketsReceived = packetsReceived - lastPacketsReceived;
                    TimeSpan diffTime = timeNow - lastTime;

                    Console.WriteLine(
                        timeNow.TotalMilliseconds.ToString("0") + "ms] " +
                        packetsReceived + " p, " + (bytesReceived / 1000 / 1000) + "MB -> " +
                        (diffBytesReceived / 1000d / 1000d).ToString("0.0") + "MB by " + diffPacketsReceived +
                        " in " + diffTime.TotalMilliseconds.ToString("0") + "ms");

                    lastBytesReceived = bytesReceived;
                    lastPacketsReceived = packetsReceived;
                    lastTime = timeNow;
                }

                if (!server.Poll(1000 * 1000, SelectMode.SelectRead))
                {
                    break;
                }

                int read = server.ReceiveFrom(buffer, ref endPoint);
                bytesReceived += read;
                packetsReceived++;

                //if (connections.Add((IPEndPoint)endPoint))
                //{
                //    Console.WriteLine("Server: New connection");
                //}
            }
        }

        private static void ClientThread()
        {
            Socket client = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            client.Blocking = true;
            client.DontFragment = true;

            IPEndPoint endPoint = new(IPAddress.Loopback, 12345);
            Span<byte> buffer = stackalloc byte[8000];

            for (int i = 0; i < 1000; i++)
            {
                int sent = client.SendTo(buffer, endPoint);
                if (sent != buffer.Length)
                    throw new Exception("Send size mismatch. Not blocking?");
            }
        }
    }
}
