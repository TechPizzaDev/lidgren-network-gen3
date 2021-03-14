
namespace Lidgren.Network
{
	/// <summary>
	/// Result of a SendMessage call
	/// </summary>
	public enum NetSendResult
	{
		/// <summary>
		/// Failed to enqueue message because there is no connection.
		/// </summary>
		FailedNotConnected = 0,

		/// <summary>
		/// No recipients were specified.
		/// </summary>
		NoRecipients = 1,

		/// <summary>
		/// Message is being sent by the peer.
		/// </summary>
		Sent = 2,

		/// <summary>
		/// Message is queued for sending.
		/// </summary>
		Queued = 3,

		/// <summary>
		/// Message was discarded and an error message was logged.
		/// </summary>
		Dropped = 4
	}
}
