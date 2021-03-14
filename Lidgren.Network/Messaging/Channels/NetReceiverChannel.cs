using System;

namespace Lidgren.Network
{
	internal abstract class NetReceiverChannel
	{
		public NetConnection Connection { get; }
		public NetPeer Peer => Connection.Peer;

		public NetReceiverChannel(NetConnection connection)
		{
			Connection = connection ?? throw new ArgumentNullException(nameof(connection));
		}

		public abstract void ReceiveMessage(in NetMessageView message);
	}
}
