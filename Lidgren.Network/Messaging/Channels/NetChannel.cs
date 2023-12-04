using System;

namespace Lidgren.Network;

internal abstract class NetChannel
{
    private int _windowSize;

    protected int _windowStart;

    public NetConnection Connection { get; }

    public int WindowSize
    {
        get => _windowSize;
        protected set
        {
            LidgrenException.AssertIsPowerOfTwo((ulong) value, nameof(value));
            if (_windowSize == 1)
            {
                throw new InvalidOperationException();
            }
            _windowSize = value;
            OnWindowSizeChanged();
        }
    }

    protected NetChannel(NetConnection connection, int windowSize)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        WindowSize = windowSize;
    }

    protected abstract void OnWindowSizeChanged();

    public virtual void Reset()
    {
        _windowStart = 0;
    }
}
