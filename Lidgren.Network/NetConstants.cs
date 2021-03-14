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
        public const int StreamChannels = 32;

        public const int TotalChannels =
            UnreliableChannels + UnreliableSequencedChannels +
            ReliableUnorderedChannels + ReliableSequencedChannels + ReliableOrderedChannels +
            StreamChannels;

        public const int SequenceNumbers = 1024;

        public const int HeaderSize = 5;

        public const int DefaultWindowSize = 128;
        public const int UnreliableWindowSize = DefaultWindowSize * 2;
        public const int ReliableOrderedWindowSize = DefaultWindowSize;
        public const int ReliableSequencedWindowSize = DefaultWindowSize;

        public const int MaxFragmentationGroups = ushort.MaxValue - 1;
        public const int UnfragmentedMessageHeaderSize = 5;

        public static void AssertValidDeliveryChannel(
            NetDeliveryMethod method, int sequenceChannel,
            string? methodParamName, string? channelParamName)
        {
            if (sequenceChannel < 0)
                throw new ArgumentOutOfRangeException(channelParamName, sequenceChannel, null);

            switch (method)
            {
                case NetDeliveryMethod.Unreliable:
                    if (sequenceChannel >= UnreliableChannels)
                        throw new ArgumentOutOfRangeException(channelParamName, sequenceChannel, null);
                    break;

                case NetDeliveryMethod.UnreliableSequenced:
                    if (sequenceChannel >= UnreliableSequencedChannels)
                        throw new ArgumentOutOfRangeException(channelParamName, sequenceChannel, null);
                    break;

                case NetDeliveryMethod.ReliableUnordered:
                    if (sequenceChannel >= ReliableUnorderedChannels)
                        throw new ArgumentOutOfRangeException(channelParamName, sequenceChannel, null);
                    break;

                case NetDeliveryMethod.ReliableSequenced:
                    if (sequenceChannel >= ReliableSequencedChannels)
                        throw new ArgumentOutOfRangeException(channelParamName, sequenceChannel, null);
                    break;

                case NetDeliveryMethod.ReliableOrdered:
                    if (sequenceChannel >= ReliableOrderedChannels)
                        throw new ArgumentOutOfRangeException(channelParamName, sequenceChannel, null);
                    break;

                default:
                case NetDeliveryMethod.Unknown:
                    throw new ArgumentOutOfRangeException(methodParamName, method, null);
            }
        }
    }
}
