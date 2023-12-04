using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Lidgren.Network
{
    public partial class NetPeer
    {
        private readonly struct DelayedPacket
        {
            public ReadOnlyMemory<byte> Data { get; }
            public TimeSpan DelayedUntil { get; }
            public NetAddress Target { get; }

            public DelayedPacket(ReadOnlyMemory<byte> data, TimeSpan delayedUntil, NetAddress target)
            {
                Data = data;
                DelayedUntil = delayedUntil;
                Target = target;
            }
        }

        private List<DelayedPacket> DelayedPackets { get; } = new();

        //Avoids allocation on mapping to IPv6
        private NetAddress _targetCopy = new(AddressFamily.InterNetworkV6);

        internal NetSocketResult SendPacket(int byteCount, NetAddress target, int numMessages)
        {
            // simulate loss
            float loss = Configuration._loss;
            if (loss > 0f)
            {
                if (MWCRandom.Global.NextSingle() < loss)
                {
                    LogDebug(new NetLogMessage(NetLogCode.SimulatedLoss, endPoint: target));
                    return new NetSocketResult(true, false);
                }
            }

            Statistics.PacketSent(byteCount, numMessages);

            if (Configuration._minimumOneWayLatency == TimeSpan.Zero &&
                Configuration._randomOneWayLatency == TimeSpan.Zero)
            {
                // no latency simulation
                var sendResult = ActuallySendPacket(_sendBuffer.AsMemory(0, byteCount), target);

                // TODO: handle 'wasSent == false' better?

                //if ((!wasSent && !connectionReset) ||
                //    Configuration._duplicates > 0f && MWCRandom.Global.NextSingle() < Configuration._duplicates)
                //{
                //    (wasSent, connectionReset) = ActuallySendPacket(_sendBuffer, byteCount, target); // send it again!
                //}
                return sendResult;
            }

            // simulate latency
            int copyCount = 1;
            if (Configuration._duplicates > 0f && MWCRandom.Global.NextSingle() < Configuration._duplicates)
                copyCount++;

            TimeSpan now = NetTime.Now;
            for (int i = 0; i < copyCount; i++)
            {
                TimeSpan delay = Configuration._minimumOneWayLatency +
                    (MWCRandom.Global.NextSingle() * Configuration._randomOneWayLatency);

                byte[] data = new byte[byteCount];
                Buffer.BlockCopy(_sendBuffer, 0, data, 0, byteCount);

                // Enqueue delayed packet
                DelayedPacket p = new(data, now + delay, target);
                DelayedPackets.Add(p);

                LogDebug(NetLogMessage.FromTime(NetLogCode.SimulatedDelay, endPoint: target, time: delay));
            }

            return new NetSocketResult(true, false);
        }

        private void SendDelayedPackets()
        {
            if (DelayedPackets.Count == 0)
                return;

            TimeSpan now = NetTime.Now;

            // reverse-for so elements can be removed without breaking loop
            for (int i = DelayedPackets.Count; i-- > 0;)
            {
                DelayedPacket p = DelayedPackets[i];
                if (now > p.DelayedUntil)
                {
                    ActuallySendPacket(p.Data, p.Target);
                    DelayedPackets.RemoveAt(i);
                }
            }
        }

        private void FlushDelayedPackets()
        {
            if (Socket == null)
                return;

            foreach (DelayedPacket p in DelayedPackets)
            {
                ActuallySendPacket(p.Data, p.Target);
            }
            DelayedPackets.Clear();
        }

        internal NetSocketResult ActuallySendPacket(ReadOnlyMemory<byte> data, NetAddress target)
        {
            Socket? socket = Socket;
            Debug.Assert(socket != null, "No socket bound.");

            bool broadcasting = false;
            try
            {
                IPAddress? ba = NetUtility.GetBroadcastAddress();

                NetAddress targetAddr;

                // TODO: refactor these checks outta here
                if (ba != null && target.AddressEquals(ba))
                {
                    // Some networks do not allow 
                    // a global broadcast so we use the BroadcastAddress from the configuration
                    // this can be resolved to a local broadcast addresss e.g 192.168.x.255
                    NetAddress.WriteAddress(_targetCopy, Configuration.BroadcastAddress);
                    NetAddress.WritePort(_targetCopy, target.GetPort());
                    targetAddr = _targetCopy;

                    socket.EnableBroadcast = true;
                    broadcasting = true;
                }
                else if (
                    Configuration.DualStack &&
                    Configuration.LocalAddress.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    // Maps to IPv6 for Dual Mode
                    target.WriteAsIPv6To(_targetCopy);
                    targetAddr = _targetCopy;
                }
                else
                {
                    targetAddr = target;
                }

                int bytesSent = socket.SendTo(data.Span, SocketFlags.None, targetAddr.GetSocketAddress());
                if (data.Length != bytesSent)
                {
                    LogWarning(NetLogMessage.FromValues(NetLogCode.FullSendFailure,
                        endPoint: target,
                        value: bytesSent,
                        maxValue: data.Length));
                }
                //LogDebug("Sent " + numBytes + " bytes");
            }
            catch (SocketException sx)
            {
                switch (sx.SocketErrorCode)
                {
                    case SocketError.WouldBlock:
                        // send buffer full?
                        LogDebug(new NetLogMessage(NetLogCode.SocketWouldBlock, sx));
                        return new NetSocketResult(false, false);

                    case SocketError.ConnectionReset:
                        // connection reset by peer, aka connection forcibly closed aka "ICMP port unreachable" 
                        return new NetSocketResult(false, true);

                    default:
                        LogError(new NetLogMessage(NetLogCode.SendFailure, sx, target));
                        break;
                }
            }
            catch (Exception ex)
            {
                LogError(new NetLogMessage(NetLogCode.SendFailure, ex, target));
            }
            finally
            {
                if (broadcasting)
                    socket.EnableBroadcast = false;
            }
            return new NetSocketResult(true, false);
        }

        internal bool SendMTUPacket(int byteCount, IPEndPoint target)
        {
            Socket? socket = Socket;
            Debug.Assert(socket != null, "No socket bound.");

            try
            {
                socket.DontFragment = true;

                int bytesSent = socket.SendTo(_sendBuffer, 0, byteCount, SocketFlags.None, target);
                if (byteCount != bytesSent)
                {
                    LogWarning(NetLogMessage.FromValues(NetLogCode.FullSendFailure,
                        endPoint: target,
                        value: bytesSent,
                        maxValue: byteCount));
                }
                Statistics.PacketSent(byteCount, 1);
            }
            catch (SocketException sx)
            {
                switch (sx.SocketErrorCode)
                {
                    case SocketError.MessageSize:
                        return false;

                    case SocketError.WouldBlock:
                        // send buffer full?
                        LogError(new NetLogMessage(NetLogCode.SocketWouldBlock, sx, target));
                        return true;

                    case SocketError.ConnectionReset:
                        return true;

                    default:
                        LogError(new NetLogMessage(NetLogCode.SendFailure, sx, target));
                        break;
                }
            }
            catch (Exception ex)
            {
                LogError(new NetLogMessage(NetLogCode.SendFailure, ex, target));
            }
            finally
            {
                socket.DontFragment = false;
            }
            return true;
        }
    }
}
