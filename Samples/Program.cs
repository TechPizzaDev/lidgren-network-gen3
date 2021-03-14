using System;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Lidgren.Network;

namespace Samples
{
    class Program
    {
        static void Main(string[] args)
        {
            while (true)
            {
                Console.Write("Enter Tess Index> ");
                string? indexInput = Console.ReadLine() ?? "0";
                int index = int.Parse(indexInput);

                string stressTestId = "StressTestApp";
                int stressTestPort = 20001;

                switch (index)
                {
                    case 0:
                        new StressTestServer().Run(stressTestId, stressTestPort);
                        break;

                    case 1:
                        new StressTestClient().Run(stressTestId, stressTestPort, 32);
                        break;
                }
            }
        }
    }

    class StressTestServer
    {
        public void Run(string appId, int port)
        {
            var config = new NetPeerConfiguration(appId)
            {
                AcceptIncomingConnections = true,
                Port = port,
                AutoExpandMTU = true
            };
            config.DisableMessageType(NetIncomingMessageType.DebugMessage);
            var server = new NetServer(config);
            server.Start();

            var logger = Task.Run(() =>
            {
                while (server.Status != NetPeerStatus.NotRunning)
                {
                    Console.WriteLine("Server Incoming: " +
                        server.Statistics.IncomingRecycled + " / " +
                        server.Statistics.IncomingAllocated);

                    Thread.Sleep(500);
                }
            });

            void OnStream(NetStream stream)
            {
                int transferred = 0;

                var t = new Thread(() =>
                {
                    Span<byte> tmp = stackalloc byte[1024 * 512];
                    int read;
                    while ((read = stream.Read(tmp)) > 0)
                    {
                        //Console.WriteLine("Server Stream Read: " + tmp[0]);
                        transferred += read;
                    }

                    Console.WriteLine($"Server Stream {stream.Channel} Read Finished: {transferred}");
                });
                t.Name = "Client Stream";
                t.Start();

                Task.Run(() =>
                {
                    while (t.IsAlive)
                    {
                        Console.WriteLine($"Server Stream {stream.Channel} Read Transferred: " + transferred);
                        Thread.Sleep(1000);
                    }
                });
            }

            while (server.TryReadMessage(5000, out var message))
            {
                switch (message.MessageType)
                {
                    case NetIncomingMessageType.StatusChanged:
                        Console.WriteLine("Server Status: " + message.ReadEnum<NetConnectionStatus>());
                        break;

                    case NetIncomingMessageType.DebugMessage:
                        Console.WriteLine("Server Debug: " + message.ReadString());
                        break;

                    case NetIncomingMessageType.WarningMessage:
                        Console.WriteLine("Server Warning: " + message.ReadString());
                        break;

                    case NetIncomingMessageType.Data:
                        Console.WriteLine("Server Data: " + message.ReadString());
                        break;

                    case NetIncomingMessageType.ErrorMessage:
                        Console.WriteLine("Server Error: " + message.ReadString());
                        break;

                    /*
                    case NetIncomingMessageType.StreamMessage:
                    {
                        var type = (NetStreamMessageType)message.ReadByte();
                        int channel = message.SequenceChannel;

                        if (type != NetStreamMessageType.Data)
                            Console.WriteLine("Server Stream: " + type);

                        var connection = message.SenderConnection;
                        if (connection == null)
                        {
                            // send error message back to sender
                            break;
                        }

                        ref NetStream? stream = ref connection._openStreams[channel];
                        switch (type)
                        {
                            case NetStreamMessageType.Open:
                                if (stream != null)
                                {
                                    // send "AlreadyOpen" message
                                    break;
                                }
                                stream = new NetStream(server.DefaultScheduler, connection, channel);

                                OnStream(stream);
                                break;

                            case NetStreamMessageType.Data:
                                if (stream == null)
                                {
                                    // send "NotOpen" message
                                    break;
                                }
                                stream.OnDataMessage(message);
                                break;

                            case NetStreamMessageType.Close:
                                if (stream == null)
                                {
                                    // send "NotOpen" message (or maybe not?) 
                                    break;
                                }
                                stream.OnCloseMessage(message);
                                stream = null;
                                break;
                        }
                        break;
                    }
                    */

                    default:
                        Console.WriteLine("Server " + message.MessageType);
                        break;
                }
            }

            logger.Wait();
        }
    }

    class StressTestClient
    {
        public void Run(string appId, int port, int streams)
        {
            var config = new NetPeerConfiguration(appId)
            {
                AcceptIncomingConnections = false,
                AutoExpandMTU = true
            };
            config.DisableMessageType(NetIncomingMessageType.DebugMessage);
            var client = new NetClient(config);
            client.Start();

            var logger = Task.Run(() =>
            {
                while (client.Status != NetPeerStatus.NotRunning)
                {
                    Console.WriteLine("Client Outgoing: " +
                        client.Statistics.OutgoingRecycled + " / " +
                        client.Statistics.OutgoingAllocated);

                    Thread.Sleep(500);
                }
            });

            NetConnection connection = client.Connect(new IPEndPoint(IPAddress.Loopback, port));

            while (connection.Status != NetConnectionStatus.Connected)
            {
                if (connection.Status == NetConnectionStatus.Disconnected)
                    throw new Exception("Failed to connect.");
                Thread.Sleep(1);
            }

            var msg = client.CreateMessage("hello");
            connection.SendMessage(msg, NetDeliveryMethod.ReliableOrdered, 0);

            for (int j = 0; j < streams; j++)
            {
                int channel = j;
                var t = new Thread(() =>
                {
                    try
                    {
                        var stream = new NetStream(client.DefaultScheduler, connection, channel);
                        Span<byte> span = stackalloc byte[1024 * 64];
                        for (int i = 0; i < 1024 * 1024 * 32; i += span.Length)
                        {
                            stream.Write(span);
                            Thread.Sleep(1);
                        }
                        stream.Dispose();
                        Console.WriteLine($"Server Stream {channel} Data Written");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex);
                    }
                });
                t.Name = "Server Stream " + j;
                t.Start();
            }

            while (client.TryReadMessage(5000, out var message))
            {
                switch (message.MessageType)
                {
                    case NetIncomingMessageType.StatusChanged:
                        Console.WriteLine("Client Status: " + message.ReadEnum<NetConnectionStatus>());
                        break;

                    case NetIncomingMessageType.DebugMessage:
                        Console.WriteLine("Client Debug: " + message.ReadString());
                        break;

                    case NetIncomingMessageType.WarningMessage:
                        Console.WriteLine("Client Warning: " + message.ReadString());
                        break;

                    case NetIncomingMessageType.Data:
                        Console.WriteLine("Client Data: " + message.ReadString());
                        break;

                    case NetIncomingMessageType.ErrorMessage:
                        Console.WriteLine("Client Error: " + message.ReadString());
                        break;

                    /*
                    case NetIncomingMessageType.StreamMessage:
                    {
                        var type = (NetStreamMessageType)message.ReadByte();
                        int channel = message.SequenceChannel;

                        Console.WriteLine("Client Stream: " + type);
                        break;
                    }
                    */

                    default:
                        Console.WriteLine("Client " + message.MessageType);
                        break;
                }

                client.Recycle(message);
            }

            logger.Wait();
        }
    }
}
