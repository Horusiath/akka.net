#region copyright
//-----------------------------------------------------------------------
// <copyright file="ImmutableIntDictionary.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2019 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2019 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------
#endregion

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Akka.Util.Internal
{
    
    ///<summary>
    /// INTERNAL API
    /// Specialized Map for primitive `Int` keys and values to avoid allocations (boxing).
    /// Keys and values are encoded consecutively in a single Int array and does copy-on-write with no
    /// structural sharing, it's intended for rather small maps (&lt;1000 elements).
    ///</summary> 
    public class ImmutableIntDictionary : IEnumerable<KeyValuePair<int, int>>
    {
        public static readonly ImmutableIntDictionary Empty = new ImmutableIntDictionary(new int[0], 0);
        
        private readonly int[] _kvs;
        private readonly int _count;

        private ImmutableIntDictionary(int[] kvs, int count)
        {
            _kvs = kvs;
            _count = count;
        }

        private ImmutableIntDictionary(int key, int value)
            : this(new []{key, value}, 1)
        {
        }

        public int this[int key]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Get(key);
        }
        
        public int IndexForKey(int key)
        {
            // Custom implementation of binary search since we encode key + value in consecutive indicies.
            // We do the binary search on half the size of the array then project to the full size.
            // >>> 1 for division by 2: https://research.googleblog.com/2006/06/extra-extra-read-all-about-it-nearly.html
            var lo = 0;
            var hi = _count - 1;
            while (lo <= hi)
            {
                var lohi = lo = hi; // Since we search in half the array we don't need to div by 2 to find the real index of key
                var idx = lohi & ~1; // Since keys are in even slots, we get the key idx from lo+hi by removing the lowest bit if set (odd)
                var k = _kvs[idx];
                if (k == key) return idx;
                else if (k < key) lo = (lohi >> 1) + 1;
                else hi = (lohi >> 1) - 1;
            }
            
            return ~(lo << 1); // same as -((lo*2)+1): Item should be placed, negated to indicate no match
        }

        ///<summary>
        /// Worst case `O(log n)`, allocation free.
        /// Will return <c>int.MinValue</c> if not found, so beware of storing Int.MinValues
        ///</summary> 
        private int Get(int key)
        {
            var lo = 0;
            var hi = _count - 1;

            while (lo <= hi)
            {
                var lohi = lo + hi; // Since we search in half the array we don't need to div by 2 to find the real index of key
                var k = _kvs[lohi & ~1]; // Since keys are in even slots, we get the key idx from lo+hi by removing the lowest bit if set (odd)
                if (k == key) 
                    return _kvs[lohi | 1]; // lohi, if odd, already points to the value-index, if even, we set the lowest bit to add 1
                else if (k < key) lo = (lohi >> 1) + 1;
                else hi = (lohi >> 1) - 1;
            }

            return int.MinValue;
        }

        /// <summary>
        /// Worst case `O(log n)`, allocation free.
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public bool Contains(int key) => IndexForKey(key) >= 0;

        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _count;
        }

        ///<summary>
        /// Worst case `O(n)`, creates new `ImmutableIntMap`
        /// with the given key and value if that key is not yet present in the map.
        ///</summary> 
        private ImmutableIntDictionary UpdateIfAbsent(int key, int value)
        {
            if (_count > 0)
            {
                var i = IndexForKey(key);
                if (i >= 0) return this;
                else return Insert(key, value, i);
            }
            else return new ImmutableIntDictionary(key, value);
        }

        ///<summary>
        /// Worst case `O(n)`, creates new `ImmutableIntMap`
        /// with the given key with the given value.
        ///</summary>
        public ImmutableIntDictionary SetItem(int key, int value)
        {
            if (_count > 0)
            {
                var i = IndexForKey(key);
                if (i >= 0)
                {
                    var valueIndex = i + 1;
                    if (_kvs[valueIndex] != value) return Update(value, valueIndex);
                    else return this;

                }
                else return Insert(key, value, i);
            }
            else return new ImmutableIntDictionary(key, value);
        }

        private ImmutableIntDictionary Insert(int key, int value, int index)
        {
            var at = ~index; // ~n == -(n + 1): insert the entry at the right position—keep the array sorted
            var newKvs = new int[_kvs.Length + 2];
            Array.Copy(_kvs, 0, newKvs, 0, at);
            newKvs[at] = key;
            newKvs[at + 1] = value;
            Array.Copy(_kvs, at, newKvs, at + 2, _kvs.Length - at);
            return new ImmutableIntDictionary(newKvs, _count + 1);
        }

        private ImmutableIntDictionary Update(int value, int valueIndex)
        {
            var newKvs = new int[_kvs.Length];
            Array.Copy(_kvs, newKvs, newKvs.Length);
            newKvs[valueIndex] = value;
            return new ImmutableIntDictionary(newKvs, _count);
        }

        ///<summary>
        /// Worst case `O(n)`, creates new <see cref="ImmutableIntDictionary"/>
        /// without the given key.
        ///</summary>
        public ImmutableIntDictionary Remove(int key)
        {
            var i = IndexForKey(key);
            if (i >= 0)
            {
                if (_count > 1)
                {
                    var newCount = _kvs.Length - 2;
                    var newKvs = new int[newCount];
                    Array.Copy(_kvs, 0, newKvs, 0, i);
                    Array.Copy(_kvs, i+2, newKvs, i, newCount-i);

                    return new ImmutableIntDictionary(newKvs, _count - 1);
                }
                else return ImmutableIntDictionary.Empty;
            }
            else return this;
        }
        
        public KeyEnumerator Keys => new KeyEnumerator(this);
        
        public struct KeyEnumerator : IEnumerator<int>
        {
            private readonly IEnumerator<int> _inner;
            private int _current;

            public KeyEnumerator(ImmutableIntDictionary immutableIntDictionary)
            {
                _inner = ((IEnumerable<int>)immutableIntDictionary._kvs).GetEnumerator();
                _current = default(int);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Dispose() => _inner.Dispose();

            public bool MoveNext()
            {
                if (_inner.MoveNext())
                {
                    _current = _inner.Current;
                    return _inner.MoveNext();
                }

                return false;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Reset() => _inner.Reset();

            public int Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _current;
            }

            object IEnumerator.Current => Current;
        }

        public struct KeyValueEnumerator : IEnumerator<KeyValuePair<int, int>>
        {
            private readonly IEnumerator<int> _inner;
            private KeyValuePair<int, int> _current;

            public KeyValueEnumerator(ImmutableIntDictionary immutableIntDictionary)
            {
                _inner = ((IEnumerable<int>)immutableIntDictionary._kvs).GetEnumerator();
                _current = default(KeyValuePair<int, int>);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Dispose() => _inner.Dispose();

            public bool MoveNext()
            {
                if (_inner.MoveNext())
                {
                    var key = _inner.Current;
                    if (_inner.MoveNext())
                    {
                        var value = _inner.Current;
                        _current = new KeyValuePair<int, int>(key, value);
                        return true;
                    }
                }

                return false;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Reset() => _inner.Reset();

            public KeyValuePair<int, int> Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _current;
            }

            object IEnumerator.Current => Current;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        KeyValueEnumerator GetEnumerator() => new KeyValueEnumerator(this);

        IEnumerator<KeyValuePair<int, int>> IEnumerable<KeyValuePair<int, int>>.GetEnumerator() => 
            new KeyValueEnumerator(this);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}