using System.Diagnostics;

namespace Lidgren.Network
{
    public partial class NetPeer
    {
        public delegate void LogEvent(NetPeer sender, NetLogLevel level, in NetLogMessage message);

        public event LogEvent? VerboseMessage;
        public event LogEvent? DebugMessage;
        public event LogEvent? WarningMessage;
        public event LogEvent? ErrorMessage;

        [Conditional("DEBUG")]
        internal void LogVerbose(in NetLogMessage message)
        {
            VerboseMessage?.Invoke(this, NetLogLevel.Verbose, message);
        }

        internal void LogDebug(in NetLogMessage message)
        {
            DebugMessage?.Invoke(this, NetLogLevel.Debug, message);
        }

        internal void LogWarning(in NetLogMessage message)
        {
            WarningMessage?.Invoke(this, NetLogLevel.Warning, message);
        }

        internal void LogError(in NetLogMessage message)
        {
            ErrorMessage?.Invoke(this, NetLogLevel.Error, message);
        }
    }
}
