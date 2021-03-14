
namespace Lidgren.Network
{
	internal sealed class NetReliableUnorderedReceiver : NetReceiverChannel
	{
		private int _windowStart;
		private int _windowSize;
		private NetBitArray _earlyReceived;

		public NetReliableUnorderedReceiver(NetConnection connection, int windowSize)
			: base(connection)
		{
			_windowSize = windowSize;
			_earlyReceived = new NetBitArray(windowSize);
		}

		private void AdvanceWindow()
		{
			_earlyReceived.Set(_windowStart % _windowSize, false);
			_windowStart = (_windowStart + 1) % NetConstants.SequenceNumbers;
		}

		public override void ReceiveMessage(in NetMessageView message)
		{
			int relate = NetUtility.RelativeSequenceNumber(message.SequenceNumber, _windowStart);

			// ack no matter what
			Connection.QueueAck(message.BaseMessageType, message.SequenceNumber);

			if (relate == 0)
			{
				// Log("Received message #" + message.SequenceNumber + " right on time");

				//
				// excellent, right on time
				//
				//m_peer.LogVerbose("Received RIGHT-ON-TIME " + message);

				AdvanceWindow();
				Peer.ReleaseMessage(message);

				// release withheld messages
				int nextSeqNr = (message.SequenceNumber + 1) % NetConstants.SequenceNumbers;

				while (_earlyReceived[nextSeqNr % _windowSize])
				{
					//message = m_withheldMessages[nextSeqNr % m_windowSize];
					//NetException.Assert(message != null);

					// remove it from withheld messages
					//m_withheldMessages[nextSeqNr % m_windowSize] = null;

					//m_peer.LogVerbose("Releasing withheld message #" + message);

					//m_peer.ReleaseMessage(message);

					AdvanceWindow();
					nextSeqNr++;
				}

				return;
			}

			if (relate < 0)
			{
				// duplicate
				Peer.LogVerbose("Received message #" + message.SequenceNumber + " DROPPING DUPLICATE");
				return;
			}

			// relate > 0 = early message
			if (relate > _windowSize)
			{
				// too early message!
				Peer.LogDebug("Received " + message.ToString() + " TOO EARLY! Expected " + _windowStart);
				return;
			}

			_earlyReceived.Set(message.SequenceNumber % _windowSize, true);
			//m_peer.LogVerbose("Received " + message + " WITHHOLDING, waiting for " + m_windowStart);
			//m_withheldMessages[message.m_sequenceNumber % m_windowSize] = message;

			Peer.ReleaseMessage(message);
		}
	}
}
