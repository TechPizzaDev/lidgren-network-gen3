
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
			NetConnection connection = Connection;

            // ack no matter what
            connection.QueueAck(message.BaseMessageType, message.SequenceNumber);

            connection.Peer.ReleaseMessage(message);
		}
	}
}
