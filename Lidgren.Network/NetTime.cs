using System;
using System.Diagnostics;

namespace Lidgren.Network
{
    /// <summary>
    /// Time helper.
    /// </summary>
    public static class NetTime
    {
        /// <summary>
        /// Gets the number of ticks in the timer mechanism upon application start.
        /// </summary>
        public static long InitializedTimestamp { get; } = Stopwatch.GetTimestamp();

        /// <summary>
        /// Gets the frequency of the timer as the number of seconds per tick.
        /// </summary>
        public static double InverseFrequency { get; } = 1.0 / Stopwatch.Frequency;

        /// <summary>
        /// Gets the amount of time elapsed since the application started in seconds.
        /// </summary>
        public static double NowSeconds => (Stopwatch.GetTimestamp() - InitializedTimestamp) * InverseFrequency;

        /// <summary>
        /// Gets the amount of time elapsed since the application started.
        /// </summary>
        public static TimeSpan Now => TimeSpan.FromSeconds(NowSeconds);

        /// <summary>
        /// Given seconds it will output a human friendly readable string
        /// (milliseconds if less than 10 seconds).
        /// </summary>
        public static string ToReadable(double seconds, IFormatProvider? formatProvider = null)
        {
            if (seconds >= 10)
                return TimeSpan.FromSeconds(seconds).ToString(null, formatProvider);

            return (seconds * 1000.0).ToString("N2", formatProvider) + " ms";
        }

        /// <summary>
        /// Given time it will output a human friendly readable string
        /// (milliseconds if less than 10 seconds).
        /// </summary>
        public static string ToReadable(TimeSpan time, IFormatProvider? formatProvider = null)
        {
            if (time.TotalSeconds >= 10)
                return time.ToString(null, formatProvider);

            return time.TotalMilliseconds.ToString("N2", formatProvider) + " ms";
        }
    }
}