
namespace Lidgren.Network
{
    internal abstract class NetReceiverChannel : NetChannel
    {
        protected NetReceiverChannel(NetConnection connection, int windowSize) : base(connection, windowSize)
        {
        }

        protected override void OnWindowSizeChanged()
        {
        }

        public abstract void ReceiveMessage(in NetMessageView message);
    }
}
