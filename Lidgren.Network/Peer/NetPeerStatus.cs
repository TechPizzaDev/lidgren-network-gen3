
namespace Lidgren.Network
{
	/// <summary>
	/// Status for a NetPeer instance
	/// </summary>
	public enum NetPeerStatus
	{
		/// <summary>
		/// NetPeer is not running; socket is not bound
		/// </summary>
		NotRunning = 0,

		/// <summary>
		/// NetPeer is in the process of starting up
		/// </summary>
		Starting = 1,

		/// <summary>
		/// NetPeer is bound to socket and listening for packets
		/// </summary>
		Running = 2,

		/// <summary>
		/// Shutdown has been requested and will be executed shortly
		/// </summary>
		ShutdownRequested = 3,
	}
}
