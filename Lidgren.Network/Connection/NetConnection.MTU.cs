using System;

namespace Lidgren.Network
{
    public partial class NetConnection
    {
        private enum ExpandMTUStatus
        {
            None,
            InProgress,
            Finished
        }

        private const int ProtocolMaxMTU = (int)((ushort.MaxValue / 8f) - 1f);

        private ExpandMTUStatus _expandMTUStatus;
        private int _largestSuccessfulMTU;
        private int _smallestFailedMTU;

        private TimeSpan _lastSentMTUAttemptTime;
        private int _lastSentMTUAttemptSize;
        private int _mtuAttemptFails;

        /// <summary>
        /// Gets the current MTU in bytes. 
        /// If <see cref="NetPeerConfiguration.AutoExpandMTU"/> is false,
        /// this will be <see cref="NetPeerConfiguration.MaximumTransmissionUnit"/>.
        /// </summary>
        public int CurrentMTU { get; private set; }

        internal void InitExpandMTU(TimeSpan now)
        {
            // wait a bit before starting to expand mtu
            _lastSentMTUAttemptTime =
                now + _peerConfiguration._expandMTUFrequency + AverageRoundtripTime + TimeSpan.FromSeconds(1.5);

            _largestSuccessfulMTU = 512;
            _smallestFailedMTU = -1;
            CurrentMTU = _peerConfiguration.MaximumTransmissionUnit;
        }

        private void MTUExpansionHeartbeat(TimeSpan now)
        {
            if (_expandMTUStatus == ExpandMTUStatus.Finished)
                return;

            if (_expandMTUStatus == ExpandMTUStatus.None)
            {
                if (!_peerConfiguration._autoExpandMTU)
                {
                    FinalizeMTU(CurrentMTU);
                    return;
                }

                // begin expansion
                ExpandMTU(now);
                return;
            }

            if (now > _lastSentMTUAttemptTime + _peerConfiguration.ExpandMTUFrequency)
            {
                _mtuAttemptFails++;
                if (_mtuAttemptFails == 3)
                {
                    FinalizeMTU(CurrentMTU);
                    return;
                }

                // timed out; ie. failed
                _smallestFailedMTU = _lastSentMTUAttemptSize;
                ExpandMTU(now);
            }
        }

        private void ExpandMTU(TimeSpan now)
        {
            int tryMTU;

            // we've nevered encountered failure
            if (_smallestFailedMTU == -1)
            {
                // we've never encountered failure; expand by 25% each time
                tryMTU = (int)(CurrentMTU * 1.25f);
                //m_peer.LogDebug("Trying MTU " + tryMTU);
            }
            else
            {
                // we HAVE encountered failure; so try in between
                tryMTU = (int)((_smallestFailedMTU + _largestSuccessfulMTU) / 2.0f);
                //m_peer.LogDebug("Trying MTU " + m_smallestFailedMTU + " <-> " + m_largestSuccessfulMTU + " = " + tryMTU);
            }

            if (tryMTU > ProtocolMaxMTU)
                tryMTU = ProtocolMaxMTU;

            if (tryMTU == _largestSuccessfulMTU)
            {
                //m_peer.LogDebug("Found optimal MTU - exiting");
                FinalizeMTU(_largestSuccessfulMTU);
                return;
            }

            SendExpandMTU(now, tryMTU);
        }

        private void SendExpandMTU(TimeSpan now, int size)
        {
            NetOutgoingMessage om = Peer.CreateMessage();
            om.WritePadBytes(size);
            om._messageType = NetMessageType.ExpandMTURequest;
            int length = 0;
            om.Encode(Peer._sendBuffer, ref length, 0);
            Peer.Recycle(om);

            if (!Peer.SendMTUPacket(length, RemoteEndPoint))
            {
                //m_peer.LogDebug("Send MTU failed for size " + size);

                // failure
                if (_smallestFailedMTU == -1 || size < _smallestFailedMTU)
                {
                    _smallestFailedMTU = size;
                    _mtuAttemptFails++;
                    if (_mtuAttemptFails >= _peerConfiguration.ExpandMTUFailAttempts)
                    {
                        FinalizeMTU(_largestSuccessfulMTU);
                        return;
                    }
                }
                ExpandMTU(now);
                return;
            }

            _lastSentMTUAttemptSize = size;
            _lastSentMTUAttemptTime = now;
        }

        private void FinalizeMTU(int size)
        {
            if (_expandMTUStatus == ExpandMTUStatus.Finished)
                return;

            _expandMTUStatus = ExpandMTUStatus.Finished;

            int previousMTU = CurrentMTU;
            CurrentMTU = size;
            if (CurrentMTU != _peerConfiguration._maximumTransmissionUnit)
            {
                Peer.LogDebug(NetLogMessage.FromValues(NetLogCode.ExpandedMTU,
                    endPoint: this,
                    value: previousMTU,
                    maxValue: CurrentMTU));
            }
        }

        private void SendMTUSuccess(int size)
        {
            NetOutgoingMessage om = Peer.CreateMessage();
            om.Write(size);
            om._messageType = NetMessageType.ExpandMTUSuccess;
            int length = 0;
            om.Encode(Peer._sendBuffer, ref length, 0);
            Peer.Recycle(om);

            Peer.SendPacket(length, RemoteEndPoint, 1);

            //m_peer.LogDebug("Received MTU expand request for " + size + " bytes");
        }

        private void HandleExpandMTUSuccess(TimeSpan now, int size)
        {
            if (size > _largestSuccessfulMTU)
                _largestSuccessfulMTU = size;

            if (size < CurrentMTU)
            {
                //m_peer.LogDebug("Received low MTU expand success (size " + size + "); current mtu is " + m_currentMTU);
                return;
            }

            //m_peer.LogDebug("Expanding MTU to " + size);
            CurrentMTU = size;

            ExpandMTU(now);
        }
    }
}
