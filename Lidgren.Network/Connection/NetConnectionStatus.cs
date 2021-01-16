
namespace Lidgren.Network
{
    /// <summary>
    /// Status for a <see cref="NetConnection"/> instance.
    /// </summary>
    public enum NetConnectionStatus : byte
    {
        /// <summary>
        /// No connection, or attempt, in place
        /// </summary>
        None,
        
        /// <summary>
        /// Connect has been sent; waiting for ConnectResponse
        /// </summary>
        InitiatedConnect,

        /// <summary>
        /// Connect was received, but ConnectResponse hasn't been sent yet
        /// </summary>
        ReceivedInitiation,

        /// <summary>
        /// Connect was received and ApprovalMessage released to the application; awaiting Approve() or Deny()
        /// </summary>
        RespondedAwaitingApproval, // We got Connect, released ApprovalMessage

        /// <summary>
        /// Connect was received and ConnectResponse has been sent; waiting for ConnectionEstablished
        /// </summary>
        RespondedConnect, // we got Connect, sent ConnectResponse

        /// <summary>
        /// Connected
        /// </summary>
        Connected,		  // we received ConnectResponse (if initiator) or ConnectionEstablished (if passive)

        /// <summary>
        /// In the process of disconnecting
        /// </summary>
        Disconnecting,

        /// <summary>
        /// Disconnected
        /// </summary>
        Disconnected
    }
}
