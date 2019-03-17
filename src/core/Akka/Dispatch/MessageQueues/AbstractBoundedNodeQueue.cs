//-----------------------------------------------------------------------
// <copyright file="AbstractBoundedNodeQueue.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2019 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2019 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Runtime.CompilerServices;
using System.Runtime.ConstrainedExecution;
using System.Threading;

namespace Akka.Dispatch.MessageQueues
{
    
    /// <summary> 
    /// Lock-free bounded non-blocking multiple-producer single-consumer queue based on the works of:
    /// 
    /// Andriy Plokhotnuyk (https://github.com/plokhotnyuk)
    ///   - https://github.com/plokhotnyuk/actors/blob/2e65abb7ce4cbfcb1b29c98ee99303d6ced6b01f/src/test/scala/akka/dispatch/Mailboxes.scala
    ///     (Apache V2: https://github.com/plokhotnyuk/actors/blob/master/LICENSE)
    /// 
    /// Dmitriy Vyukov's non-intrusive MPSC queue:
    ///   - http://www.1024cores.net/home/lock-free-algorithms/queues/non-intrusive-mpsc-node-based-queue
    ///   (Simplified BSD)
    /// </summary>
    public abstract class AbstractBoundedNodeQueue<T>
    {
        #region node

        protected internal sealed class Node
        {
            internal T Value;
            private Node _next;
            internal int Count;

            public Node Next
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => Volatile.Read(ref _next);

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                set => Volatile.Write(ref _next, value);
            }
        }

        #endregion
        
        private readonly int _capacity;
        private Node _enq;
        private Node _deq;

        protected AbstractBoundedNodeQueue(int capacity)
        {
            if (capacity < 0) ThrowCapacityException();
            
            _capacity = capacity;
            var n = new Node();
            Volatile.Write(ref _enq, n);
            Volatile.Write(ref _deq, n);
        }

        /// <summary>
        /// The maximum capacity of this queue.
        /// </summary>
        public int Capacity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _capacity;
        }

        public bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ReferenceEquals(Volatile.Read(ref _enq), Volatile.Read(ref _deq));
        }

        protected Node PeekNode()
        {
            while (true)
            {
                var deq = Volatile.Read(ref _deq);
                var next = deq.Next;

                if (!ReferenceEquals(next, null) || ReferenceEquals(deq, Volatile.Read(ref _enq)))
                {
                    return next;
                }
            }
        }

        public T Peek()
        {
            var n = PeekNode();
            return !ReferenceEquals(n, null) ? n.Value : default(T);
        }

        public bool TryAdd(T value)
        {
            Node n = null;
            while (true)
            {
                var lastNode = Volatile.Read(ref _enq);
                var lastNodeCount = lastNode.Count;
                if (lastNodeCount - Volatile.Read(ref _deq).Count < _capacity)
                {
                    // Trade a branch for avoiding to create a new node if full,
                    // and to avoid creating multiple nodes on write conflict á la Be Kind to Your GC
                    if (ReferenceEquals(n, null))
                    {
                        n = new Node();
                        n.Value = value;
                    }

                    n.Count = lastNodeCount + 1; // Piggyback on the HB-edge between getEnq() and casEnq()

                    // Try to append the node to the end, if we fail we continue loopin'
                    if (ReferenceEquals(lastNode, Interlocked.CompareExchange(ref _enq, n, lastNode)))
                    {
                        lastNode.Next = n;
                        return true;
                    }
                }
                else return false; // Over capacity—couldn't add the node
            }
        }

        internal bool TryAddNode(Node n)
        {
            n.Next = null; // Make sure we're not corrupting the queue
            while (true)
            {
                var lastNode = Volatile.Read(ref _enq);
                var lastNodeCount = lastNode.Count;
                if (lastNodeCount - Volatile.Read(ref _deq).Count < _capacity)
                {
                    n.Count = lastNodeCount + 1; // Piggyback on the HB-edge between getEnq() and casEnq()
                    
                    // Try to append the node to the end, if we fail we continue loopin'
                    if (ReferenceEquals(lastNode, Interlocked.CompareExchange(ref _enq, n, lastNode)))
                    {
                        lastNode.Next = n;
                        return true;
                    }
                    
                }
                else return false;
            }
        }

        /// <summary>
        /// Returns an approximation of the queue's "current" size
        /// </summary>
        public int ApproximateSize()
        {
            //Order of operations is extremely important here
            // If no item was dequeued between when we looked at the count of the enqueuing end,
            // there should be no out-of-bounds
            while (true)
            {
                var deqCountBefore = Volatile.Read(ref _deq).Count;
                var enqCount = Volatile.Read(ref _enq).Count;
                var deqCountAfter = Volatile.Read(ref _deq).Count;

                if (deqCountAfter == deqCountBefore)
                    return enqCount - deqCountAfter;
            }
        }
        
        /// <summary>
        /// Removes the first element of this queue if any
        /// </summary>
        /// <returns> The value of the first element of the queue, default if empty</returns>
        public bool TryPoll(out T result)
        {
            var n = PollNode();
            if (ReferenceEquals(n, null))
            {
                result = default(T);
                return false;
            }

            result = n.Value;
            return true;
        }

        /// <summary>
        /// Removes the first element of this queue if any.
        /// </summary>
        /// <returns> The <see cref="Node"/> of the first element of the queue, null if empty.</returns>
        internal Node PollNode()
        {
            while (true)
            {
                var deq = Volatile.Read(ref _deq);
                var next = deq.Next;
                if (!ReferenceEquals(next, null))
                {
                    if (ReferenceEquals(deq, Interlocked.CompareExchange(ref _deq, next, deq)))
                    {
                        deq.Value = next.Value;
                        deq.Next = null;
                        next.Value = default(T);
                        return deq;
                    }
                }
                else if (ReferenceEquals(deq, Volatile.Read(ref _enq))) return null;  // If we got a null and head meets tail, we are empty
            }
        }
        
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowCapacityException() => throw new ArgumentException("AbstractBoundedNodeQueue.capacity must be >= 0");
    }
}