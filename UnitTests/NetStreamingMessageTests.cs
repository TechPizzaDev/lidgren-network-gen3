using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Lidgren.Network;

namespace UnitTests
{
    public static class NetStreamingMessageTest
    {
        public static void Run(string? arg)
        {
            Console.WriteLine("Testing streaming messages");

            string appId = "NetStreamingMessages";
            int port = 20002;
            int clientCount = 4;

            var serverThread = new Thread(() =>
            {
                var config = new NetPeerConfiguration(appId)
                {
                    AcceptIncomingConnections = true,
                    Port = port,
                    AutoExpandMTU = true,
                    ReceiveBufferSize = 1024 * 1024
                };
                config.EnableMessageType(NetIncomingMessageType.ConnectionApproval);
                var server = new NetServer(config);

                void Server_ErrorMessage(NetPeer sender, NetLogLevel level, in NetLogMessage message)
                {
                    Console.WriteLine($"Server {level}: {message.Code}");

                    if (message.Code == NetLogCode.ExpandedMTU)
                    {
                        var connection = (NetConnection?)message.EndPoint;
                        Console.WriteLine($"New MTU after expansion: {connection?.CurrentMTU}");
                    }

                    if (level > NetLogLevel.Debug && message.Exception != null)
                        Console.WriteLine(message.Exception);
                }
                server.DebugMessage += Server_ErrorMessage;
                server.WarningMessage += Server_ErrorMessage;
                server.ErrorMessage += Server_ErrorMessage;
                server.Start();

                Task.Run(() =>
                {
                    return;

                    while (server.Status == NetPeerStatus.Running)
                    {
                        Console.WriteLine(
                            $"Server Incoming: {server.Statistics.IncomingRecycled} / {server.Statistics.IncomingAllocated}");

                        Thread.Sleep(500);
                    }
                });

                long totalRead = 0;
                long tmpRead = 0;

                List<Task> tasks = new();
                int count = clientCount;
                while (count > 0 && server.TryReadMessage(10000, out var message))
                {
                    switch (message.MessageType)
                    {
                        case NetIncomingMessageType.ConnectionApproval:
                            message.SenderConnection?.Approve(server.CreateMessage("approved!"));
                            break;

                        case NetIncomingMessageType.StatusChanged:
                            Console.WriteLine($"Server Status: {message.ReadEnum<NetConnectionStatus>()}");
                            break;

                        case NetIncomingMessageType.Data:
                            Console.WriteLine($"Server Data: {message.ByteLength} bytes");

                            var resp = server.CreateMessage($"received {count}");
                            message.SenderConnection?.SendMessage(resp, NetDeliveryMethod.ReliableOrdered, 0);
                            count--;
                            break;

                        case NetIncomingMessageType.DataStream:
                            if (message.SenderConnection!.TryDequeueDataStream(out PipeReader? reader))
                            {
                                async Task ReadLoop()
                                {
                                    Console.WriteLine($"{DateTime.UtcNow.TimeOfDay.TotalMilliseconds:0.000}] CLIENT STARTING STREAM ON SERVER");

                                    Stream stream = reader.AsStream();
                                    byte[] buffer = new byte[1024 * 1024];

                                    //using (var fs = new FileStream("receivedfile", FileMode.Create))

                                    var fs = Stream.Null;
                                    {
                                        int read;
                                        while ((read = await stream.ReadAsync(buffer)) > 0)
                                        {
                                            totalRead += read;
                                            tmpRead += read;

                                            if (tmpRead >= 1024 * 1024 * 4)
                                            {
                                                tmpRead = 0;
                                                Console.WriteLine($"{DateTime.UtcNow.TimeOfDay.TotalMilliseconds:0.000}]  READ {totalRead / 1024}kB FROM CLIENT STREAM");
                                            }
                                            //fs.Write(buffer.AsSpan(0, read));
                                        }
                                    }

                                    Console.WriteLine($"{DateTime.UtcNow.TimeOfDay.TotalMilliseconds:0.000}] CLIENT STREAM COMPLETED ON SERVER");
                                }
                                tasks.Add(ReadLoop());
                            }
                            break;

                        default:
                            Console.WriteLine($"Server {message.MessageType}");
                            break;
                    }
                }

                Task.WaitAll(tasks.ToArray());

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
                        if (message.Code == NetLogCode.SocketWouldBlock)
                            return;

                        Console.WriteLine($"Client {level}: {message.Code}");

                        if (message.Code == NetLogCode.ExpandedMTU)
                        {
                            var connection = (NetConnection?)message.EndPoint;
                            Console.WriteLine($"New MTU after expansion: {connection?.CurrentMTU}");
                        }

                        if (level > NetLogLevel.Debug && message.Exception != null)
                            Console.WriteLine(message.Exception);
                    }
                    client.DebugMessage += Client_ErrorMessage;
                    client.WarningMessage += Client_ErrorMessage;
                    client.ErrorMessage += Client_ErrorMessage;
                    client.Start();

                    Task.Run(() =>
                    {
                        return;

                        while (client.ConnectionStatus != NetConnectionStatus.Connected)
                            Thread.Sleep(1);

                        while (client.ConnectionStatus == NetConnectionStatus.Connected)
                        {
                            Console.WriteLine(
                                $"Client {tt} Outgoing: {client.Statistics.OutgoingRecycled} / {client.Statistics.OutgoingAllocated}");

                            Thread.Sleep(500);
                        }
                    });

                    NetConnection connection = client.Connect(new IPEndPoint(IPAddress.Loopback, port));

                    bool stop = false;
                    void Connection_StatusChanged(NetConnection connection, NetConnectionStatus status, NetOutgoingMessage? reason)
                    {
                        string print = "Client Status: " + status;
                        if (reason != null)
                        {
                            print += " #" + reason.MessageType;
                            if (reason.BitLength != 0)
                            {
                                if (status == NetConnectionStatus.Disconnected)
                                {
                                    print += " (" + reason?.ReadString() + ")";
                                    stop = true;
                                }
                                else if (status == NetConnectionStatus.Connected)
                                {
                                    print += " (" + connection?.RemoteHailMessage?.ReadString() + ")";
                                }
                            }
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
                                    Console.WriteLine(msg.ReadEnum<NetConnectionStatus>());
                            }
                            return;
                        }
                        Thread.Sleep(1);
                    }

                    Task.Run(async () =>
                    {
                        try
                        {
                            var fs = new FakeReadStream(1024 * 1024 * 128);
                            PipeReader reader = PipeReader.Create(fs);
                            var result = await connection.StreamMessageAsync(reader, 0);

                            Console.WriteLine($"{DateTime.UtcNow.TimeOfDay.TotalMilliseconds:0.000}] {result} FILE from thread {tt}");
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
                                Console.WriteLine($"Client Data: {message.ReadString()}");
                                client.Shutdown();
                                stop = true;
                                break;

                            default:
                                Console.WriteLine($"Client {message.MessageType}");
                                break;
                        }

                        client.Recycle(message);
                    }
                });
            }

            if (arg == null || arg == "server")
                serverThread.Start();

            if (arg == null || arg != "server")
            {
                for (int i = 0; i < clientThreads.Length; i++)
                    clientThreads[i].Start();
            }

            if (arg == null || arg == "server")
                serverThread.Join();

            if (arg == null || arg != "server")
            {
                for (int i = 0; i < clientThreads.Length; i++)
                    clientThreads[i].Join();
            }
        }
    }
}
