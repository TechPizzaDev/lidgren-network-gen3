using System.Text;

namespace Lidgren.Network
{
    /// <summary>
    /// Statistics for a <see cref="NetPeer"/> instance.
    /// </summary>
    public sealed class NetPeerStatistics
    {
        private readonly NetPeer _peer;

        internal int _sentPackets;
        internal int _receivedPackets;

        internal int _sentMessages;
        internal int _receivedMessages;
        internal int _receivedFragments;

        internal int _sentBytes;
        internal int _receivedBytes;

        internal NetPeerStatistics(NetPeer peer)
        {
            _peer = peer;
        }

        internal void Reset()
        {
            _sentPackets = 0;
            _receivedPackets = 0;

            _sentMessages = 0;
            _receivedMessages = 0;
            _receivedFragments = 0;

            _sentBytes = 0;
            _receivedBytes = 0;
        }

        /// <summary>
        /// Gets the number of sent packets since the NetPeer was initialized.
        /// </summary>
        public int SentPackets => _sentPackets;

        /// <summary>
        /// Gets the number of received packets since the NetPeer was initialized.
        /// </summary>
        public int ReceivedPackets => _receivedPackets;

        /// <summary>
        /// Gets the number of sent messages since the NetPeer was initialized.
        /// </summary>
        public int SentMessages => _sentMessages;

        /// <summary>
        /// Gets the number of received messages since the NetPeer was initialized.
        /// </summary>
        public int ReceivedMessages => _receivedMessages;

        /// <summary>
        /// Gets the number of sent bytes since the NetPeer was initialized.
        /// </summary>
        public int SentBytes => _sentBytes;

        /// <summary>
        /// Gets the number of received bytes since the NetPeer was initialized.
        /// </summary>
        public int ReceivedBytes => _receivedBytes;

        internal void PacketSent(int byteCount, int messageCount)
        {
            _sentPackets++;
            _sentBytes += byteCount;
            _sentMessages += messageCount;
        }

        internal void PacketReceived(int byteCount, int messageCount, int fragmentCount)
        {
            _receivedPackets++;
            _receivedBytes += byteCount;
            _receivedMessages += messageCount;
            _receivedFragments += fragmentCount;
        }

        /// <summary>
        /// Builds and returns a string that represents this object.
        /// </summary>
        public override string ToString()
        {
            var sb = new StringBuilder();

            sb.AppendFormat("{0} active connections", _peer.ConnectionCount).AppendLine();

            sb.AppendFormat(
                "Sent {0} bytes in {1} messages in {2} packets",
                _sentBytes, _sentMessages, _sentPackets).AppendLine();

            sb.AppendFormat(
                "Received {0} bytes in {1} messages ({2} fragments) in {3} packets", 
                _receivedBytes, _receivedMessages, _receivedFragments, _receivedPackets).AppendLine();

            return sb.ToString();
        }
    }
}