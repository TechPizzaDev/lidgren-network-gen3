
namespace Lidgren.Network
{
    public readonly struct NetSocketResult
    {
        public bool Success { get; }
        public bool ConnectionReset { get; }

        public NetSocketResult(bool success, bool connectionReset)
        {
            Success = success;
            ConnectionReset = connectionReset;
        }

        public void Deconstruct(out bool success, out bool connectionReset)
        {
            success = Success;
            connectionReset = ConnectionReset;
        }
    }
}
