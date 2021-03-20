using System;
using System.Collections.Generic;
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
            int clientCount = 1;

            var serverThread = new Thread(() =>
            {
                var config = new NetPeerConfiguration(appId)
                {
                    AcceptIncomingConnections = true,
                    Port = port,
                    AutoExpandMTU = true,
                };
                config.EnableMessageType(NetIncomingMessageType.ConnectionApproval);
                var server = new NetServer(config);

                void Server_ErrorMessage(NetPeer sender, NetLogLevel level, in NetLogMessage message)
                {
                    Console.WriteLine("Server " + level + ": " + message.Code);
                }
                server.DebugMessage += Server_ErrorMessage;
                server.WarningMessage += Server_ErrorMessage;
                server.ErrorMessage += Server_ErrorMessage;
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
                        case NetIncomingMessageType.ConnectionApproval:
                            message.SenderConnection?.Approve(server.CreateMessage("approved!"));
                            break;

                        case NetIncomingMessageType.StatusChanged:
                            Console.WriteLine("Server Status: " + message.ReadEnum<NetConnectionStatus>());
                            break;

                        case NetIncomingMessageType.Data:
                            Console.WriteLine("Server Data: " + message.ByteLength + " bytes");

                            var resp = server.CreateMessage("received " + count);
                            message.SenderConnection?.SendMessage(resp, NetDeliveryMethod.ReliableOrdered, 0);
                            count--;
                            break;


                        default:
                            Console.WriteLine("Server " + message.MessageType);
                            break;
                    }
                }

                List<NetConnection> connections = new();
                server.GetConnections(connections);
                foreach (var con in connections)
                {
                    var msg = server.CreateMessage("this is library");
                    con.Disconnect(msg);
                }

                Console.WriteLine("Server finished waiting");
                var shutMsg = server.CreateMessage("this is not library anymore");
                server.Shutdown(shutMsg);
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
                    var client = new NetClient(config);

                    void Client_ErrorMessage(NetPeer sender, NetLogLevel level, in NetLogMessage message)
                    {
                        Console.WriteLine("Client " + level + ": " + message.Code);
                    }
                    client.DebugMessage += Client_ErrorMessage;
                    client.WarningMessage += Client_ErrorMessage;
                    client.ErrorMessage += Client_ErrorMessage;
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

                    bool stop = false;
                    void Connection_StatusChanged(NetConnection connection, NetConnectionStatus status, NetBuffer? reason)
                    {
                        string print = "Client Status: " + status;
                        if (status == NetConnectionStatus.Disconnected)
                        {
                            print += " (" + reason?.ReadString() + ")";
                            stop = true;
                        }
                        else if (status == NetConnectionStatus.Connected)
                        {
                            print += " (" + connection?.RemoteHailMessage?.ReadString() + ")";
                        }
                        Console.WriteLine(print);
                    }
                    connection.StatusChanged += Connection_StatusChanged;

                    while (connection.Status != NetConnectionStatus.Connected)
                    {
                        if (connection.Status == NetConnectionStatus.Disconnected)
                        {
                            if (client.TryReadMessage(out var msg))
                            {
                                if (msg.MessageType == NetIncomingMessageType.StatusChanged)
                                    Console.WriteLine(msg.ReadEnum<NetConnectionStatus>() + ": " + msg.ReadString());
                            }
                            return;
                        }
                        Thread.Sleep(1);
                    }

                    Task.Run(() =>
                    {
                        try
                        {
                            NetOutgoingMessage outMsg = client.CreateMessage();
                            outMsg.WritePadBytes(1024 * 1024 * 128);
                            var r = connection.SendMessage(outMsg, NetDeliveryMethod.ReliableOrdered, 0);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex);
                        }
                    });

                    while (!stop && client.TryReadMessage(60000, out var message))
                    {
                        switch (message.MessageType)
                        {
                            case NetIncomingMessageType.Data:
                                Console.WriteLine("Client Data: " + message.ReadString());
                                client.Shutdown();
                                stop = true;
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
