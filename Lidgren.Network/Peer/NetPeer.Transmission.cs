using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace Lidgren.Network
{
    public partial class NetPeer
    {
        private readonly struct DelayedPacket
        {
            public byte[] Data { get; }
            public TimeSpan DelayedUntil { get; }
            public IPEndPoint Target { get; }

            public DelayedPacket(byte[] data, TimeSpan delayedUntil, IPEndPoint target)
            {
                Data = data;
                DelayedUntil = delayedUntil;
                Target = target;
            }
        }

        private List<DelayedPacket> DelayedPackets { get; } = new List<DelayedPacket>();

        //Avoids allocation on mapping to IPv6
        private IPEndPoint _targetCopy = new IPEndPoint(IPAddress.Any, 0);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="byteCount"></param>
        /// <param name="target"></param>
        /// <param name="numMessages"></param>
        /// <returns>Whether the connection was reset.</returns>
        internal bool SendPacket(int byteCount, IPEndPoint target, int numMessages)
        {
            // simulate loss
            float loss = Configuration._loss;
            if (loss > 0f)
            {
                if (MWCRandom.Global.NextSingle() < loss)
                {
                    LogDebug(new NetLogMessage(NetLogCode.SimulatedLoss, endPoint: target));
                    return false;
                }
            }

            Statistics.PacketSent(byteCount, numMessages);

            if (Configuration._minimumOneWayLatency == TimeSpan.Zero &&
                Configuration._randomOneWayLatency == TimeSpan.Zero)
            {
                // no latency simulation
                var (wasSent, connectionReset) = ActuallySendPacket(_sendBuffer, byteCount, target);

                // TODO: handle 'wasSent == false' better?

                if ((!wasSent && !connectionReset) ||
                    Configuration._duplicates > 0f && MWCRandom.Global.NextSingle() < Configuration._duplicates)
                {
                    (wasSent, connectionReset) = ActuallySendPacket(_sendBuffer, byteCount, target); // send it again!
                }
                return connectionReset;
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

            return false;
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
                    ActuallySendPacket(p.Data, p.Data.Length, p.Target);
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
                ActuallySendPacket(p.Data, p.Data.Length, p.Target);
            }
            DelayedPackets.Clear();
        }

        // TODO: replace byte[] with Span in the future (held back by Socket.SendTo)
        // https://github.com/dotnet/runtime/issues/33418
        internal (bool Sent, bool ConnectionReset) ActuallySendPacket(byte[] data, int byteCount, IPEndPoint target)
        {
            if (Socket == null)
                throw new InvalidOperationException("No socket bound.");

            bool broadcasting = false;

            try
            {
                IPAddress? ba = NetUtility.GetBroadcastAddress();

                // TODO: refactor this check outta here
                if (target.Address.Equals(ba))
                {
                    // Some networks do not allow 
                    // a global broadcast so we use the BroadcastAddress from the configuration
                    // this can be resolved to a local broadcast addresss e.g 192.168.x.255                    
                    _targetCopy.Address = Configuration.BroadcastAddress;
                    _targetCopy.Port = target.Port;

                    Socket.EnableBroadcast = true;
                    broadcasting = true;
                }
                else if (
                    Configuration.DualStack &&
                    Configuration.LocalAddress.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    // Maps to IPv6 for Dual Mode
                    NetUtility.MapToIPv6(target, _targetCopy);
                }
                else
                {
                    _targetCopy.Port = target.Port;
                    _targetCopy.Address = target.Address;
                }

                int bytesSent = Socket.SendTo(data, 0, byteCount, SocketFlags.None, _targetCopy);
                if (byteCount != bytesSent)
                {
                    LogWarning(NetLogMessage.FromValues(NetLogCode.FullSendFailure,
                        endPoint: target,
                        value: bytesSent,
                        maxValue: byteCount));
                }
                //LogDebug("Sent " + numBytes + " bytes");
            }
            catch (SocketException sx)
            {
                switch (sx.SocketErrorCode)
                {
                    case SocketError.WouldBlock:
                        // send buffer full?
                        LogWarning(new NetLogMessage(NetLogCode.SocketWouldBlock, sx));
                        return (false, false);

                    case SocketError.ConnectionReset:
                        // connection reset by peer, aka connection forcibly closed aka "ICMP port unreachable" 
                        return (false, true);

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
                    Socket.EnableBroadcast = false;
            }
            return (true, false);
        }

        internal bool SendMTUPacket(int byteCount, IPEndPoint target)
        {
            if (Socket == null)
                throw new InvalidOperationException("No socket bound.");

            try
            {
                Socket.DontFragment = true;

                int bytesSent = Socket.SendTo(_sendBuffer, 0, byteCount, SocketFlags.None, target);
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
                Socket.DontFragment = false;
            }
            return true;
        }
    }
}
