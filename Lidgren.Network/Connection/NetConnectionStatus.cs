
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
        /// Connect was received and <see cref="NetIncomingMessageType.ConnectionApproval"/> released to the application.
        /// Awaiting <see cref="NetConnection.Approve"/> or <see cref="NetConnection.Deny"/>.
        /// </summary>
        RespondedAwaitingApproval,

        /// <summary>
        /// Connect was received and ConnectResponse has been sent; waiting for ConnectionEstablished
        /// </summary>
        RespondedConnect, 

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
