
namespace Lidgren.Network
{
	internal sealed class NetUnreliableSequencedReceiver : NetReceiverChannel
	{
		private int _lastReceivedSequenceNumber = -1;

		public NetUnreliableSequencedReceiver(NetConnection connection)
			: base(connection, 1)
		{
		}

		public override void ReceiveMessage(in NetMessageView message)
		{
			int nr = message.SequenceNumber;

			// ack no matter what
			Connection.QueueAck(message.BaseMessageType, nr);

			int relate = NetUtility.RelativeSequenceNumber(nr, _lastReceivedSequenceNumber + 1);
			if (relate < 0)
				return; // drop if late

			_lastReceivedSequenceNumber = nr;
			Peer.ReleaseMessage(message);
		}
	}
}
