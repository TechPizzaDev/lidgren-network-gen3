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
        private int _index;

        private IEnumerator<T> _enumerator;
        private IList<T> _list;
        private IReadOnlyList<T> _roList;

        public T Current { get; private set; }
        object IEnumerator.Current => Current!;

        public ListEnumerator(IEnumerator<T> enumerator) : this()
        {
            _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
            Current = default!;
        }

        public ListEnumerator(IList<T> list) : this()
        {
            _list = list ?? throw new ArgumentNullException(nameof(list));
            Current = default!;
        }

        public ListEnumerator(IReadOnlyList<T> list) : this()
        {
            _roList = list ?? throw new ArgumentNullException(nameof(list));
            Current = default!;
        }

        public bool MoveNext()
        {
            if (_list != null)
            {
                if ((uint)_index < (uint)_list.Count)
                {
                    Current = _list[_index++];
                    return true;
                }
            }
            else if (_roList != null)
            {
                if ((uint)_index < (uint)_roList.Count)
                {
                    Current = _roList[_index++];
                    return true;
                }
            }
            else if (_enumerator != null)
            {
                if (_enumerator.MoveNext())
                {
                    Current = _enumerator.Current;
                    return true;
                }
            }
            Current = default!;
            return false;
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
