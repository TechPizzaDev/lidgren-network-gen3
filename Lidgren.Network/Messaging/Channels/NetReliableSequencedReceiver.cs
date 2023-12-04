
namespace Lidgren.Network
{
	internal sealed class NetReliableSequencedReceiver : NetReceiverChannel
	{
		public NetReliableSequencedReceiver(NetConnection connection, int windowSize)
			: base(connection, windowSize)
		{
		}

		private void AdvanceWindow()
		{
			_windowStart = NetUtility.PowOf2Mod(_windowStart + 1, NetConstants.SequenceNumbers);
		}

		public override void ReceiveMessage(in NetMessageView message)
        {
            NetConnection connection = Connection;
            NetPeer peer = connection.Peer;
            int windowSize = WindowSize;
            int nr = message.SequenceNumber;

			int relate = NetUtility.RelativeSequenceNumber(nr, _windowStart);

			// ack no matter what
			connection.QueueAck(message.BaseMessageType, nr);

			if (relate == 0)
			{
				// Log("Received message #" + message.SequenceNumber + " right on time");

				// excellent, right on time
				
				AdvanceWindow();
				peer.ReleaseMessage(message);
				return;
			}

			if (relate < 0)
			{
				peer.LogVerbose(NetLogMessage.FromValues(NetLogCode.DuplicateOrLateMessage,
					message, value: _windowStart, maxValue: windowSize));
				return;
			}

			// relate > 0 = early message
			if (relate > windowSize)
			{
				// too early message!
				peer.LogVerbose(NetLogMessage.FromValues(NetLogCode.TooEarlyMessage,
					message, value: _windowStart, maxValue: windowSize));
				return;
			}

			// ok
			_windowStart = NetUtility.PowOf2Mod(_windowStart + relate, NetConstants.SequenceNumbers);
			peer.ReleaseMessage(message);
			return;
		}
	}
}
