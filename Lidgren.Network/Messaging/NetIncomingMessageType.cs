
namespace Lidgren.Network
{
	/// <summary>
	/// The type of a <see cref="NetIncomingMessage"/>.
	/// </summary>
	public enum NetIncomingMessageType
	{
		//
		// library note: values are power-of-two, 
		// but they are not flags - it's a convenience for NetPeerConfiguration.DisabledMessageTypes
		//

		/// <summary>
		/// Error; this value should never appear
		/// </summary>
		Error = 0,

		/// <summary>
		/// Status for a connection changed
		/// </summary>
		StatusChanged = 1 << 0,			// Data (string)

		/// <summary>
		/// Data sent using SendUnconnectedMessage
		/// </summary>
		UnconnectedData = 1 << 1,		// Data					Based on data received

		/// <summary>
		/// Connection approval is needed
		/// </summary>
		ConnectionApproval = 1 << 2,	// Data

		/// <summary>
		/// Application data
		/// </summary>
		Data = 1 << 3,					// Data					Based on data received

		/// <summary>
		/// Receipt of delivery
		/// </summary>
		Receipt = 1 << 4,				// Data

		/// <summary>
		/// Discovery request for a response
		/// </summary>
		DiscoveryRequest = 1 << 5,		// (no data)

		/// <summary>
		/// Discovery response to a request
		/// </summary>
		DiscoveryResponse = 1 << 6,		// Data

		/// <summary>
		/// Verbose debug message
		/// </summary>
		VerboseDebugMessage = 1 << 7,	// Data (string)

		/// <summary>
		/// Debug message
		/// </summary>
		DebugMessage = 1 << 8,			// Data (string)

		/// <summary>
		/// Warning message
		/// </summary>
		WarningMessage = 1 << 9,		// Data (string)

		/// <summary>
		/// Error message
		/// </summary>
		ErrorMessage = 1 << 10,			// Data (string)

		/// <summary>
		/// NAT introduction was successful.
		/// </summary>
		NatIntroductionSuccess = 1 << 11, // Data (as passed to master server)

		/// <summary>
		/// A roundtrip was measured and <see cref="NetConnection.AverageRoundtripTime"/> was updated.
		/// </summary>
		ConnectionLatencyUpdated = 1 << 12, // Seconds as a TimeSpan,

		/// <summary>
		/// Represents various data and operations sent by a <see cref="NetStream"/>. 
		/// </summary>
		StreamMessage = 1 << 13
	}
}
