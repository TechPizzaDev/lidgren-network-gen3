
namespace Lidgren.Network
{
	internal sealed class NetReliableOrderedReceiver : NetReceiverChannel
	{
		private int _windowStart;
		private int _windowSize;
		private NetBitArray _earlyReceived;

		internal NetIncomingMessage?[] WithheldMessages { get; private set; }

		public NetReliableOrderedReceiver(NetConnection connection, int windowSize)
			: base(connection)
		{
			_windowSize = windowSize;
			_earlyReceived = new NetBitArray(windowSize);
			WithheldMessages = new NetIncomingMessage[windowSize];
		}

		private void AdvanceWindow()
		{
			_earlyReceived.Set(_windowStart % _windowSize, false);
			_windowStart = (_windowStart + 1) % NetConstants.SequenceNumbers;
		}

		public override void ReceiveMessage(NetIncomingMessage message)
		{
			int relate = NetUtility.RelativeSequenceNumber(message.SequenceNumber, _windowStart);

			// ack no matter what
			Connection.QueueAck(message._baseMessageType, message.SequenceNumber);

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
					message = WithheldMessages[nextSeqNr % _windowSize]!;
					LidgrenException.Assert(message != null);

					// remove it from withheld messages
					WithheldMessages[nextSeqNr % _windowSize] = default!;

					Peer.LogVerbose("Releasing withheld message #" + message);

					Peer.ReleaseMessage(message);

					AdvanceWindow();
					nextSeqNr++;
				}

				return;
			}

			if (relate < 0)
			{
				Peer.LogVerbose("Received message #" + message.SequenceNumber + " DROPPING DUPLICATE");
				// duplicate
				return;
			}

			// relate > 0 = early message
			if (relate > _windowSize)
			{
				// too early message!
				Peer.LogDebug("Received " + message + " TOO EARLY! Expected " + _windowStart);
				return;
			}

			_earlyReceived.Set(message.SequenceNumber % _windowSize, true);
			Peer.LogVerbose("Received " + message + " WITHHOLDING, waiting for " + _windowStart);
			WithheldMessages[message.SequenceNumber % _windowSize] = message;
		}
	}
}
