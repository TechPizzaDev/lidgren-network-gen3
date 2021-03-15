
namespace Lidgren.Network
{
    /// <summary>
    /// Behaviour of unreliable sends above MTU.
    /// </summary>
    public enum NetUnreliableSizeBehaviour
    {
        /// <summary>
        /// Unreliable message will ignore MTU and send everything in a single packet.
        /// </summary>
        IgnoreMTU = 0,

        // TODO: reclaim memory from fragments please
        /// <summary>
        /// Use normal fragmentation for unreliable messages.
        /// If a fragment is dropped, memory for received fragments is never reclaimed.
        /// </summary>
        NormalFragmentation = 1,

        /// <summary>
        /// Drops unreliable messages above the current MTU.
        /// </summary>
        DropAboveMTU = 2,
    }
}
