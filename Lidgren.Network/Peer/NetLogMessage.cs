using System;

namespace Lidgren.Network
{
    public readonly struct NetLogMessage
    {
        public NetLogCode Code { get; init; }
        public Exception? Exception { get; init; }
        public object? EndPoint { get; init; }
        public object? Data { get; init; }
        public long PackedValue { get; init; }

        public TimeSpan Time => new(PackedValue);
        public int Value => (int)(PackedValue & uint.MaxValue);
        public int MaxValue => (int)(PackedValue >> 32);

        public NetLogMessage(
            NetLogCode code,
            Exception? exception = null,
            object? endPoint = null,
            object? data = null)
        {
            Code = code;
            Exception = exception;
            EndPoint = endPoint;
            Data = data;
            PackedValue = default;
        }

        public NetLogMessage(
            NetLogCode code,
            in NetMessageView message,
            Exception? exception = null)
        {
            Code = code;
            Exception = exception;
            EndPoint = (object?)message.Connection ?? message.Address;
            Data = message.Buffer;
            PackedValue = message.Time.Ticks;
        }

        public static NetLogMessage FromValues(
            NetLogCode code,
            Exception? exception = null,
            object? endPoint = null,
            object? data = null,
            int value = default,
            int maxValue = default)
        {
            return new NetLogMessage
            {
                Code = code,
                Exception = exception,
                EndPoint = endPoint,
                Data = data,
                PackedValue = (long)value | ((long)maxValue << 32)
            };
        }

        public static NetLogMessage FromValues(
            NetLogCode code,
            in NetMessageView message,
            Exception? exception = null,
            int value = default,
            int maxValue = default)
        {
            return FromValues(
                code,
                exception,
                (object?)message.Connection ?? message.Address,
                message.Buffer,
                value,
                maxValue);
        }

        public static NetLogMessage FromTime(
           NetLogCode code,
           Exception? exception = null,
           object? endPoint = null,
           object? data = null,
           TimeSpan time = default)
        {
            return new NetLogMessage
            {
                Code = code,
                Exception = exception,
                EndPoint = endPoint,
                Data = data,
                PackedValue = time.Ticks
            };
        }

        public static NetLogMessage FromTime(
            NetLogCode code,
            in NetMessageView message,
            Exception? exception = null,
            TimeSpan time = default)
        {
            return FromTime(
                code,
                exception,
                (object?)message.Connection ?? message.Address,
                message.Buffer,
                time);
        }
    }
}
