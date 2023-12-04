
namespace Lidgren.Network
{
	internal sealed class NetUnreliableUnorderedReceiver : NetReceiverChannel
	{
		public NetUnreliableUnorderedReceiver(NetConnection connection)
			: base(connection, 1)
		{
		}

		public override void ReceiveMessage(in NetMessageView message)
		{
			// ack no matter what
			Connection.QueueAck(message.BaseMessageType, message.SequenceNumber);

			Peer.ReleaseMessage(message);
		}
	}
}
