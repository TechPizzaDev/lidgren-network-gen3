using System;

namespace Lidgren.Network
{
    public class NetUPnPDiscoveryEventArgs : EventArgs
    {
        public NetUPnP UPnP { get; }
        public UPnPStatus Status { get; }
        public TimeSpan DiscoveryStartTime { get; }
        public TimeSpan DiscoveryEndTime { get; }
        public TimeSpan DiscoveryDuration => DiscoveryEndTime - DiscoveryStartTime;

        public NetUPnPDiscoveryEventArgs(
            NetUPnP upnp, UPnPStatus status, TimeSpan discoveryStartTime, TimeSpan discoveryEndTime)
        {
            UPnP = upnp ?? throw new ArgumentNullException(nameof(upnp));
            Status = status;
            DiscoveryStartTime = discoveryStartTime;
            DiscoveryEndTime = discoveryEndTime;
        }
    }
}
