using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Lidgren.Network
{
    /// <summary>
    /// Thread-safe (blocking) expanding queue with TryDequeue() and EnqueueFirst().
    /// </summary>
    [DebuggerDisplay("{DebuggerDisplay}")]
    public sealed class NetQueue<T> : IDisposable
    {
        public const int ArrayMaxGrowth = 1024;
        public const int ArrayGrowthFactor = 2;

        // Example:
        // m_capacity = 8
        // m_size = 6
        // m_head = 4
        //
        // [0] item
        // [1] item (tail = ((head + size - 1) % capacity)
        // [2] 
        // [3] 
        // [4] item (head)
        // [5] item
        // [6] item 
        // [7] item

        private ReaderWriterLockSlim Lock { get; } = new ReaderWriterLockSlim();
        private T[] _items;
        private int _head;

        /// <summary>
        /// Gets the number of items in the queue.
        /// </summary>
        public int Count { get; private set; }

        /// <summary>
        /// Gets the current capacity of the queue.
        /// </summary>
        public int Capacity => _items.Length;

        internal string DebuggerDisplay => $"Count = {Count}, Capacity = {Capacity}";

        /// <summary>
        /// Constructs the queue with an initial capacity.
        /// </summary>
        public NetQueue(int initialCapacity)
        {
            _items = new T[initialCapacity];
        }

        /// <summary>
        /// Constructs the queue with a default capacity.
        /// </summary>
        public NetQueue()
        {
            _items = Array.Empty<T>();
        }

        private void AddCapacity(int baseLength)
        {
            int newLength = Math.Min(Math.Max(16, baseLength * ArrayGrowthFactor), baseLength + ArrayMaxGrowth);
            SetCapacity(newLength);
        }

        /// <summary>
        /// Adds an item last/tail of the queue
        /// </summary>
        public void Enqueue(T item)
        {
            Lock.EnterWriteLock();
            try
            {
                if (Count == _items.Length)
                    AddCapacity(_items.Length);

                int slot = (_head + Count) % _items.Length;
                _items[slot] = item;
                Count++;
            }
            finally
            {
                Lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Adds an item last/tail of the queue
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="items"/> is null.</exception>
        public void Enqueue(IEnumerable<T> items)
        {
            if (items == null)
                throw new ArgumentNullException(nameof(items));

            Lock.EnterWriteLock();
            try
            {
                int expectedCount = Count;
                if (items is ICollection<T> coll)
                    expectedCount += coll.Count;
                else if (items is IReadOnlyCollection<T> roColl)
                    expectedCount += roColl.Count;

                if (expectedCount > Capacity)
                    AddCapacity(expectedCount);

                // TODO: possibly optimize when collection is a List

                foreach (T item in items.AsListEnumerator())
                {
                    // check capacity as we cannot be sure about the resulting count
                    if (Count == _items.Length)
                        AddCapacity(_items.Length);

                    int slot = (_head + Count) % _items.Length;
                    _items[slot] = item;
                    Count++;
                }
            }
            finally
            {
                Lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Places an item first, at the head of the queue
        /// </summary>
        public void EnqueueFirst(T item)
        {
            Lock.EnterWriteLock();
            try
            {
                if (Count >= _items.Length)
                    AddCapacity(_items.Length);

                _head--;
                if (_head < 0)
                    _head = _items.Length - 1;
                _items[_head] = item;
                Count++;
            }
            finally
            {
                Lock.ExitWriteLock();
            }
        }

        // must be called from within a write locked m_lock!
        private void SetCapacity(int newCapacity)
        {
            if (_items.Length == 0)
            {
                _items = new T[newCapacity];
                _head = 0;
                return;
            }

            var newItems = new T[newCapacity];

            if (_head + Count - 1 < _items.Length)
            {
                Array.Copy(_items, _head, newItems, 0, Count);
            }
            else
            {
                Array.Copy(_items, _head, newItems, 0, _items.Length - _head);
                Array.Copy(_items, 0, newItems, _items.Length - _head, Count - (_items.Length - _head));
            }

            _items = newItems;
            _head = 0;
        }

        /// <summary>
        /// Gets an item from the head of the queue, or returns default if empty.
        /// </summary>
        public bool TryDequeue([MaybeNullWhen(false)] out T item)
        {
            if (Count == 0)
            {
                item = default;
                return false;
            }

            Lock.EnterWriteLock();
            try
            {
                if (Count == 0)
                {
                    item = default;
                    return false;
                }

                item = _items[_head];

                if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                    _items[_head] = default!;

                _head = (_head + 1) % _items.Length;
                Count--;

                return true;
            }
            finally
            {
                Lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Dequeues as many items as possible, appending them to a collection.
        /// </summary>
        /// <returns>The number of items dequeued.</returns>
        public int TryDrain(ICollection<T> destination, Action<T>? onItem = null)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));

            if (Count == 0)
                return 0;

            Lock.EnterWriteLock();
            try
            {
                int count = Count;
                do
                {
                    Span<T> slice = _items.AsSpan(_head, Math.Min(Count, _items.Length - _head));
                    foreach (T item in slice)
                    {
                        destination.Add(item);
                        onItem?.Invoke(item);
                    }
                    slice.Clear();

                    _head = 0;
                    Count -= slice.Length;
                }
                while (Count > 0);
                return count;
            }
            finally
            {
                Lock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Tries to get a value without dequeuing it at a given offset.
        /// </summary>
        /// <param name="offset">The offset of the item.</param>
        /// <param name="value">The peek result.</param>
        /// <returns>Whether the peek returned a value.</returns>
        public bool TryPeek(int offset, [MaybeNullWhen(false)] out T value)
        {
            Lock.EnterReadLock();
            try
            {
                if (Count == 0)
                {
                    value = default;
                    return false;
                }

                value = _items[(_head + offset) % _items.Length];
                return true;
            }
            finally
            {
                Lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Tries to get a value without dequeuing, returning <see langword="default"/> if queue is empty.
        /// </summary>
        public T Peek(int offset)
        {
            if (TryPeek(offset, out T? value))
                return value;
            return default!;
        }

        /// <summary>
        /// Determines whether an item is in the queue
        /// </summary>
        public bool Contains(T item, IEqualityComparer<T>? comparer)
        {
            Lock.EnterReadLock();
            try
            {
                int left = Count;
                int offset = _head;
                while (left > 0)
                {
                    Span<T> slice = _items.AsSpan(offset, Math.Min(left, _items.Length - offset));
                    if (comparer == null)
                    {
                        foreach (T other in slice)
                        {
                            if (EqualityComparer<T>.Default.Equals(item, other))
                                return true;
                        }
                    }
                    else
                    {
                        foreach (T other in slice)
                        {
                            if (comparer.Equals(item, other))
                                return true;
                        }
                    }

                    left -= slice.Length;
                    offset = 0;
                }
                return false;
            }
            finally
            {
                Lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Determines whether an item is in the queue
        /// </summary>
        public bool Contains(T item)
        {
            return Contains(item, null);
        }

        /// <summary>
        /// Copies the queue items into a given span.
        /// </summary>
        public void CopyTo(Span<T> destination)
        {
            Lock.EnterReadLock();
            try
            {
                int left = Count;
                int offset = _head;
                while (left > 0)
                {
                    Span<T> slice = _items.AsSpan(offset, Math.Min(left, Count - _head));
                    slice.CopyTo(destination);

                    destination = destination[slice.Length..];
                    left -= slice.Length;
                    offset = 0;
                }
            }
            finally
            {
                Lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Removes all objects from the queue.
        /// </summary>
        public void Clear()
        {
            Lock.EnterWriteLock();
            try
            {
                _items.AsSpan().Clear();
                _head = 0;
                Count = 0;
            }
            finally
            {
                Lock.ExitWriteLock();
            }
        }

        public void Dispose()
        {
            Lock.Dispose();
        }
    }
}
