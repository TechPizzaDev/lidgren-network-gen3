using System;
using System.Collections;
using System.Collections.Generic;

namespace Lidgren.Network
{
    /// <summary>
    /// Used to reduce allocations when creating enumerators 
    /// from enumerables by using list indexing.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    internal struct ListEnumerator<T> : IEnumerator<T>, IEnumerable<T>
    {
        private delegate bool MoveNextDelegate(ref ListEnumerator<T> enumerator);

        private int _index;
        private MoveNextDelegate _moveDelegate;

        private IEnumerator<T>? _enumerator;
        private IList<T>? _list;
        private IReadOnlyList<T>? _roList;

        public T Current { get; private set; }
        object IEnumerator.Current => Current!;

        public ListEnumerator(IEnumerator<T> enumerator) : this()
        {
            _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
            _moveDelegate = MoveNextEnumerator;
            Current = default!;
        }

        public ListEnumerator(IList<T> list) : this()
        {
            _list = list ?? throw new ArgumentNullException(nameof(list));
            _moveDelegate = MoveNextList;
            Current = default!;
        }

        public ListEnumerator(IReadOnlyList<T> list) : this()
        {
            _roList = list ?? throw new ArgumentNullException(nameof(list));
            _moveDelegate = MoveNextROList;
            Current = default!;
        }

        private static bool MoveNextList(ref ListEnumerator<T> enumerator)
        {
            if ((uint)enumerator._index < (uint)enumerator._list!.Count)
            {
                enumerator.Current = enumerator._list[enumerator._index++];
                return true;
            }
            enumerator.Current = default!;
            return false;
        }

        private static bool MoveNextROList(ref ListEnumerator<T> enumerator)
        {
            if ((uint)enumerator._index < (uint)enumerator._roList!.Count)
            {
                enumerator.Current = enumerator._roList[enumerator._index++];
                return true;
            }

            enumerator.Current = default!;
            return false;
        }

        private static bool MoveNextEnumerator(ref ListEnumerator<T> enumerator)
        {
            if (enumerator._enumerator!.MoveNext())
            {
                enumerator.Current = enumerator._enumerator.Current;
                return true;
            }

            enumerator.Current = default!;
            return false;
        }

        public bool MoveNext()
        {
            return _moveDelegate.Invoke(ref this);
        }

        public void Dispose()
        {
            _enumerator?.Dispose();
        }

        public ListEnumerator<T> GetEnumerator()
        {
            return this;
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            return GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        void IEnumerator.Reset()
        {
            throw new NotSupportedException();
        }
    }
}
