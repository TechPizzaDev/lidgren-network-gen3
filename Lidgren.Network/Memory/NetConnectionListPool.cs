using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Lidgren.Network
{
    using Bucket = Stack<List<NetConnection>>;

    public static class NetConnectionListPool
    {
        public const int MaxLength = 65536;

        private static Bucket[] Buckets { get; } = new Bucket[SelectBucketIndex(MaxLength) + 1];
        private static Bucket LargeBucket { get; } = new Bucket();

        public static int LargeListCount => LargeBucket.Count;

        static NetConnectionListPool()
        {
            for (int i = 0; i < Buckets.Length; i++)
                Buckets[i] = new Bucket();
        }

        /// <summary>
        /// Rents a <see cref="List{T}"/> from the pool.
        /// The list is empty and has at least the given capacity.
        /// </summary>
        /// <param name="capacity">The minimum capacity of the list.</param>
        /// <returns>The rented list.</returns>
        public static List<NetConnection> Rent(int capacity = 0)
        {
            Debug.Assert(capacity >= 0);

            int bucketIndex = capacity == 0 ? 0 : SelectBucketIndex(capacity);

            Bucket bucket = bucketIndex < Buckets.Length ? Buckets[bucketIndex] : LargeBucket;
            lock (bucket)
            {
                if (bucket.TryPop(out var list))
                    return list;
            }
            return new List<NetConnection>(GetMaxSizeForBucket(bucketIndex));
        }

        /// <summary>
        /// Returns a <see cref="List{T}"/> to the pool. 
        /// The list is cleared can be rented by <see cref="Rent"/>.
        /// </summary>
        /// <param name="list">
        /// The list to return. Does not have to be rented by <see cref="Rent"/>.
        /// </param>
        public static void Return(List<NetConnection> list)
        {
            if (list == null)
                throw new ArgumentNullException(nameof(list));

            list.Clear();

            int capacity = list.Capacity;
            int bucketIndex = capacity == 0 ? 0 : SelectBucketIndex(capacity);
            int bucketSize = GetMaxSizeForBucket(bucketIndex);
            int bucketsPerSize = Math.Max(1024 / bucketSize, 4);

            Bucket bucket = bucketIndex < Buckets.Length ? Buckets[bucketIndex] : LargeBucket;
            lock (bucket)
            {
                if (bucket.Count < bucketsPerSize)
                    bucket.Push(list);
            }
        }

        /// <summary>
        /// Gets a list of the <see cref="NetPeer"/> connections if there are any, 
        /// returning <see langword="null"/> otherwise.
        /// </summary>
        /// <remarks>
        /// The list is rented from <see cref="NetConnectionListPool"/> and returning it to the pool is advised.
        /// </remarks>
        /// <returns>A list with connections or <see langword="null"/> if there are none.</returns>
        public static List<NetConnection>? GetConnections(NetPeer peer)
        {
            if (peer == null)
                throw new ArgumentNullException(nameof(peer));

            var connections = peer.Connections;
            lock (connections)
            {
                int count = connections.Count;
                if (count > 0)
                {
                    var list = Rent(count);
                    list.AddRange(connections);
                    return list;
                }
            }
            return null;
        }

        // borrowed from internal System.Buffers.Utilities
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int SelectBucketIndex(int capacity)
        {
            Debug.Assert(capacity >= 0);

            // Buffers are bucketed so that a request between 2^(n-1) + 1 and 2^n is given a buffer of 2^n
            // Bucket index is log2(bufferSize - 1) with the exception that buffers between length 1 and 16
            // are combined, and the index is slid down by 3 to compensate.
            return BitOperations.Log2((uint)capacity - 1 | 15) - 3;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetMaxSizeForBucket(int binIndex)
        {
            int maxSize = 16 << binIndex;
            Debug.Assert(maxSize >= 0);
            return maxSize;
        }
    }
}
