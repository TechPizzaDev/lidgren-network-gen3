using System;

namespace Lidgren.Network
{
	internal abstract class NetSenderChannel
	{
		// access this directly to queue things in this channel
		public NetQueue<NetOutgoingMessage> QueuedSends { get; } = new NetQueue<NetOutgoingMessage>(16);

		public abstract int WindowSize { get; }

		public abstract int GetAllowedSends();

		public abstract NetSendResult Enqueue(NetOutgoingMessage message);
		public abstract NetSocketResult SendQueuedMessages(TimeSpan now);
		public abstract void Reset();
		public abstract NetSocketResult ReceiveAcknowledge(TimeSpan now, int sequenceNumber);
	}
}
