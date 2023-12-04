using System;

namespace Lidgren.Network
{
    /// <summary>
    /// All the constants used when internally by the library.
    /// </summary>
    internal static class NetConstants
    {
        public const int UnreliableChannels = 1;
        public const int UnreliableSequencedChannels = 32;
        public const int ReliableUnorderedChannels = 1;
        public const int ReliableSequencedChannels = 32;
        public const int ReliableOrderedChannels = 32;

        public const int TotalChannels =
            UnreliableChannels + UnreliableSequencedChannels +
            ReliableUnorderedChannels + ReliableSequencedChannels + ReliableOrderedChannels;

        // The real amount is 32k, but that would realistically just waste memory.
        public const int SequenceNumbers = 1024 * 4;

        public const int HeaderSize = 5;

        public const int DefaultWindowSize = 512;
        public const int UnreliableWindowSize = DefaultWindowSize * 2;
        public const int ReliableOrderedWindowSize = DefaultWindowSize;
        public const int ReliableSequencedWindowSize = DefaultWindowSize;
        public const int ReliableUnorderedWindowSize = DefaultWindowSize;

        public const int MaxFragmentationGroups = 16384 - 1;
        public const int UnfragmentedMessageHeaderSize = 5;

        public static void AssertValidDeliveryChannel(
            NetDeliveryMethod method, int sequenceChannel,
            string? methodParamName, string? channelParamName)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(sequenceChannel, 0, channelParamName);

            switch (method)
            {
                case NetDeliveryMethod.Unreliable:
                    ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
                        sequenceChannel, UnreliableChannels, channelParamName);
                    break;

                case NetDeliveryMethod.UnreliableSequenced:
                    ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
                        sequenceChannel, UnreliableSequencedChannels, channelParamName);
                    break;

                case NetDeliveryMethod.ReliableUnordered:
                    ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
                        sequenceChannel, ReliableUnorderedChannels, channelParamName);
                    break;

                case NetDeliveryMethod.ReliableSequenced:
                    ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
                        sequenceChannel, ReliableSequencedChannels, channelParamName);
                    break;

                case NetDeliveryMethod.ReliableOrdered:
                    ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
                        sequenceChannel, ReliableOrderedChannels, channelParamName);
                    break;

                default:
                case NetDeliveryMethod.Unknown:
                    void Throw() => throw new ArgumentOutOfRangeException(methodParamName, method, null);
                    Throw();
                    break;
            }
        }
    }
}
