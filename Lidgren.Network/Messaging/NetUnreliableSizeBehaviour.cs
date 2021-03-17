
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

        /// <summary>
        /// Use normal fragmentation for unreliable messages.
        /// If a fragment is dropped, memory is reclaimed after <see cref="NetPeerConfiguration.FragmentGroupTimeout"/>.
        /// </summary>
        NormalFragmentation = 1,

        /// <summary>
        /// Drops unreliable messages above the current MTU.
        /// </summary>
        DropAboveMTU = 2,
    }
}
