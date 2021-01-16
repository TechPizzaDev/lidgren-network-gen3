using System;
using System.Diagnostics;

namespace Lidgren.Network
{
    [DebuggerDisplay("{" + nameof(GetDebuggerDisplay) + "(),nq}")]
    internal struct NetStoredReliableMessage
    {
        public int SequenceNumber;
        public int NumSent;
        public TimeSpan LastSent;
        public NetOutgoingMessage? Message;

        public void Reset()
        {
            SequenceNumber = default;
            NumSent = default;
            LastSent = default;
            Message = default;
        }

        private string GetDebuggerDisplay()
        {
            return ToString();
        }

        public override string ToString()
        {
            return Message != null ? Message.DebuggerDisplay : "Empty";
        }
    }
}
