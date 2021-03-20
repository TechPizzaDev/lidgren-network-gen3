﻿
namespace Lidgren.Network
{
	internal sealed class NetReliableSequencedReceiver : NetReceiverChannel
	{
		private int _windowStart;
		private int _windowSize;

		public NetReliableSequencedReceiver(NetConnection connection, int windowSize)
			: base(connection)
		{
			_windowSize = windowSize;
		}

		private void AdvanceWindow()
		{
			_windowStart = NetUtility.PowOf2Mod(_windowStart + 1, NetConstants.SequenceNumbers);
		}

		public override void ReceiveMessage(in NetMessageView message)
		{
			int nr = message.SequenceNumber;

			int relate = NetUtility.RelativeSequenceNumber(nr, _windowStart);

			// ack no matter what
			Connection.QueueAck(message.BaseMessageType, nr);

			if (relate == 0)
			{
				// Log("Received message #" + message.SequenceNumber + " right on time");

				// excellent, right on time
				
				AdvanceWindow();
				Peer.ReleaseMessage(message);
				return;
			}

			if (relate < 0)
			{
				Peer.LogVerbose(NetLogMessage.FromValues(NetLogCode.DuplicateOrLateMessage,
					message, value: _windowStart, maxValue: _windowSize));
				return;
			}

			// relate > 0 = early message
			if (relate > _windowSize)
			{
				// too early message!
				Peer.LogVerbose(NetLogMessage.FromValues(NetLogCode.TooEarlyMessage,
					message, value: _windowStart, maxValue: _windowSize));
				return;
			}

			// ok
			_windowStart = NetUtility.PowOf2Mod(_windowStart + relate, NetConstants.SequenceNumbers);
			Peer.ReleaseMessage(message);
			return;
		}
	}
}
