using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Lidgren.Network;

namespace UnitTests
{
    public static class NetStreamingMessageTest
    {
        public static void Run()
        {
            Console.WriteLine("Testing streaming messages");

            string appId = "NetStreamingMessages";
            int port = 20002;
            int clientCount = 16;

            var serverThread = new Thread(() =>
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

                Task.Run(() =>
                {
                    while (server.Status == NetPeerStatus.Running)
                    {
                        Console.WriteLine("Server Incoming: " +
                            server.Statistics.IncomingRecycled + " / " +
                            server.Statistics.IncomingAllocated);

                        Thread.Sleep(500);
                    }
                });

                int count = clientCount;
                while (count > 0 && server.TryReadMessage(60000, out var message))
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
                            Console.WriteLine("Server Data: " + message.ByteLength + " bytes");

                            var resp = server.CreateMessage("received " + count);
                            message.SenderConnection?.SendMessage(resp, NetDeliveryMethod.ReliableOrdered, 0);
                            count--;
                            break;

                        case NetIncomingMessageType.ErrorMessage:
                            Console.WriteLine("Server Error: " + message.ReadString());
                            break;

                        default:
                            Console.WriteLine("Server " + message.MessageType);
                            break;
                    }
                }

                Thread.Sleep(100);
                server.Shutdown(null);
            });

            Thread[] clientThreads = new Thread[clientCount];

            for (int t = 0; t < clientThreads.Length; t++)
            {
                int tt = t;
                clientThreads[t] = new Thread(() =>
                {
                    var config = new NetPeerConfiguration(appId)
                    {
                        AcceptIncomingConnections = false,
                        AutoExpandMTU = true,
                        SendBufferSize = 1024 * 1024
                    };
                    config.DisableMessageType(NetIncomingMessageType.DebugMessage);
                    var client = new NetClient(config);
                    client.Start();

                    Task.Run(() =>
                    {
                        while (client.ConnectionStatus != NetConnectionStatus.Connected)
                            Thread.Sleep(1);

                        while (client.ConnectionStatus == NetConnectionStatus.Connected)
                        {
                            Console.WriteLine("Client " + tt + " Outgoing: " +
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

                    NetOutgoingMessage outMsg = client.CreateMessage();
                    outMsg.WritePadBytes(1024 * 1024 * 16);
                    connection.SendMessage(outMsg, NetDeliveryMethod.ReliableOrdered, 1);

                    bool stop = false;
                    while (!stop && client.TryReadMessage(60000, out var message))
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
                                client.Shutdown(null);
                                stop = true;
                                break;

                            case NetIncomingMessageType.ErrorMessage:
                                Console.WriteLine("Client Error: " + message.ReadString());
                                break;

                            default:
                                Console.WriteLine("Client " + message.MessageType);
                                break;
                        }

                        client.Recycle(message);
                    }
                });
            }

            serverThread.Start();
            for (int i = 0; i < clientThreads.Length; i++)
                clientThreads[i].Start();

            serverThread.Join();
            for (int i = 0; i < clientThreads.Length; i++)
                clientThreads[i].Join();

            Thread.Sleep(500);
        }
    }
}
