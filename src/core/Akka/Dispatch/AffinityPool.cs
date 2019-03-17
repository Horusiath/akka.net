#region copyright

//-----------------------------------------------------------------------
// <copyright file="AffinityPool.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2019 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2019 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

#endregion

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Threading;
using Akka.Configuration;
using Akka.Dispatch.MessageQueues;
using Akka.Util.Internal;

namespace Akka.Dispatch
{
    ///<summary> 
    /// An <see cref="ExecutorService"/> implementation which pins actor to particular threads
    /// and guaranteed that an actor's <see cref="Mailbox"/> will e run on the thread it used
    /// it used to run. In situations where we see a lot of cache ping pong, this
    /// might lead to significant performance improvements.
    /// 
    /// INTERNAL API
    /// </summary>
    public sealed class AffinityPool : ExecutorService
    {
        #region internal classes

        /// <summary>
        /// Following are auxiliary class and trait definitions.
        /// </summary>
        internal sealed class IdleStrategy
        {
            #region idle state

            /// <summary>
            /// Idle state: initial state.
            /// </summary>
            public const int Initial = 0;

            /// <summary>
            /// Idle state: spinning.
            /// </summary>
            public const int Spinning = 1;

            /// <summary>
            /// Idle state: yielding.
            /// </summary>
            public const int Yielding = 2;

            /// <summary>
            /// Idle state: parking.
            /// </summary>
            public const int Parking = 3;

            #endregion

            private const int MinParkPeriodNs = 1;

            private readonly int _maxSpins;
            private readonly int _maxYields;
            private readonly int _maxParkPeriodNs;
            private readonly AffinityPool _pool;

            private int _state = Initial;
            private long _turns = 0;
            private int _parkPeriods = 0;
            private int _idling = 0;

            private static int toNanos(int duration)
            {
                long m;
                if (duration > (m = int.MaxValue / 1000))
                    return int.MaxValue;
                else if (duration < -m)
                    return int.MinValue;
                else
                    return duration * 1000;
            }

            public IdleStrategy(int idleCpuLevel, AffinityPool pool)
            {
                _pool = pool;
                _maxSpins = 1100 * idleCpuLevel - 1000;
                _maxYields = 5 * idleCpuLevel;
                _maxParkPeriodNs = toNanos(250 - ((80 * (idleCpuLevel - 1)) / 3));
            }

            public bool IsIdling
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _idling != 0;
            }

            private void TransitionTo(int newState)
            {
                _state = newState;
                _turns = 0;
            }

            public void Reset()
            {
                _idling = 0;
                TransitionTo(Initial);
            }

            public void Idle()
            {
                switch (_state)
                {
                    case Initial:
                        _idling = 1;
                        TransitionTo(Spinning);
                        break;

                    case Spinning:
                        if (!ReferenceEquals(_pool.OnSpinWaitMethodHandle, null))
                            _pool.OnSpinWaitMethodHandle.InvokeExact();

                        _turns++;
                        if (_turns > _maxSpins)
                            TransitionTo(Yielding);
                        break;

                    case Yielding:
                        _turns++;
                        if (_turns > _maxYields)
                        {
                            _parkPeriods = MinParkPeriodNs;
                            TransitionTo(Parking);
                        }
                        else Thread.Yield();

                        break;

                    case Parking:
                        Thread.SpinWait(_parkPeriods);
                        _parkPeriods = Math.Min(_parkPeriods << 1, _maxParkPeriodNs);
                        break;
                }
            }
        }

        internal sealed class AffinityPoolWorker : IRunnable
        {
            private readonly BoundedAffinityTaskQueue _queue;
            private readonly IdleStrategy _idleStrategy;
            private readonly AffinityPool _pool;
            private readonly Thread _thread;

            public AffinityPoolWorker(BoundedAffinityTaskQueue queue, IdleStrategy idleStrategy, AffinityPool pool,
                Thread thread)
            {
                _queue = queue;
                _idleStrategy = idleStrategy;
                _pool = pool;
                _thread = thread;
            }

            public BoundedAffinityTaskQueue Queue => _queue;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Start() => _thread.Start();

            public void Stop()
            {
                if (_thread.ThreadState != ThreadState.WaitSleepJoin)
                    _thread.Interrupt();
            }

            public void StopIfIdle()
            {
                if (_idleStrategy.IsIdling) Stop();
            }

            public void Run()
            {
                /**
                 * We keep running as long as we are Running
                 * or we're ShuttingDown but we still have tasks to execute,
                 * and we're not interrupted.
                 */
                void RunLoop()
                {
                    while (true)
                    {
                        if (_thread.ThreadState != ThreadState.WaitSleepJoin)
                        {
                            switch (_pool.PoolState)
                            {
                                case AffinityPool.Initializing:
                                case AffinityPool.Running:
                                    ExecuteNext();
                                    break;
                                case AffinityPool.ShuttingDown:
                                    if (!ExecuteNext()) return;
                                    else break;
                            }
                        }
                    }
                }

                var abruptTermination = false;
                try
                {
                    RunLoop();
                    abruptTermination = true; // if we have reached here, our termination is not due to an exception
                }
                finally
                {
                    _pool.OnWorkerExit(this, abruptTermination);
                }
            }

            private bool ExecuteNext()
            {
                if (_queue.TryPoll(out var c))
                {
                    c.Run();
                    return true;
                }
                else
                {
                    _idleStrategy.Idle();
                    return false;
                }
            }
        }

        #endregion

        #region pool state

        /// <summary>
        /// Pool state: waiting to be initialized.
        /// </summary>
        public const int Uninitialized = 0;

        /// <summary>
        /// Pool state: currently in the process of initializing.
        /// </summary>
        public const int Initializing = 1;

        /// <summary>
        /// Pool state: accepts new tasks and processes that are enqueued.
        /// </summary>
        public const int Running = 2;

        /// <summary>
        /// Pool state: doesn't accept new tasks, processes remaining tasks in queue.
        /// </summary>
        public const int ShuttingDown = 3;

        /// <summary>
        /// Pool state: doesn't accept new tasks, doesn't process tasks in queue.
        /// </summary>
        public const int Shutdowned = 4;

        /// <summary> 
        /// Pool state: all threads are stopped, doesn't accepts new tasks and doesn't process any of them.
        /// </summary>
        public const int Terminated = 5;

        #endregion

        private readonly int _parallelism;
        private readonly object _threadFactory;
        private readonly int _idleCpuLevel;
        private readonly IQueueSelector _queueSelector;
        private readonly IRejectionHandler _rejectionHandler;

        // Held while starting/shutting down workers/pool in order to make
        // the operations linear and enforce atomicity. An example of that would be
        // adding a worker. We want the creation of the worker, addition
        // to the set and starting to worker to be an atomic action. Using
        // a concurrent set would not give us that
        private readonly object _bookKeepingLock = new object();

        private readonly ManualResetEventSlim _terminationCondition = new ManualResetEventSlim(false);

        private int _poolState = Uninitialized;
        private readonly BoundedAffinityTaskQueue[] _workQueues;
        private readonly HashSet<AffinityPoolWorker> _workers;

        public AffinityPool(
            string id,
            int parallelism,
            int affinityGroupSize,
            object threadFactory,
            int idleCpuLevel,
            IQueueSelector queueSelector,
            IRejectionHandler rejectionHandler)
            : base(id)
        {
            _parallelism = parallelism;
            _threadFactory = threadFactory;
            _idleCpuLevel = idleCpuLevel;
            _queueSelector = queueSelector;
            _rejectionHandler = rejectionHandler;
            _workQueues = new BoundedAffinityTaskQueue[parallelism];
            _workers = new HashSet<AffinityPoolWorker>();

            for (int i = 0; i < parallelism; i++)
            {
                _workQueues[i] = new BoundedAffinityTaskQueue(affinityGroupSize);
            }
        }

        public override string ToString() => $"AffinityPool(id:{Id}, parallelism:{_parallelism})";

        internal int PoolState
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _poolState;
        }

        public bool IsShutdown
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _poolState >= Shutdowned;
        }

        public bool IsTerminated
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _poolState == Terminated;
        }

        public AffinityPool Start()
        {
            lock (_bookKeepingLock)
            {
                if (_poolState == Uninitialized)
                {
                    _poolState = Initializing;
                    foreach (var queue in _workQueues)
                    {
                        AddWorker(_workers, queue);
                    }

                    _poolState = Running;
                }

                return this;
            }
        }

        // WARNING: Only call while holding the bookKeepingLock
        private void AddWorker(HashSet<AffinityPoolWorker> workers, BoundedAffinityTaskQueue queue)
        {
            var worker = new AffinityPoolWorker(queue, new IdleStrategy(_idleCpuLevel, this), this, new Thread());
            _workers.Add(worker);
            worker.Start();
        }

        ///<summary>
        /// Each worker should go through that method while terminating.
        /// In turn each worker is responsible for modifying the pool
        /// state accordingly. For example if this is the last worker
        /// and the queue is empty and we are in a ShuttingDown state
        /// the worker can transition the pool to ShutDown and attempt
        /// termination
        /// 
        /// Furthermore, if this worker has experienced abrupt termination
        /// due to an exception being thrown in user code, the worker is
        /// responsible for adding one more worker to compensate for its
        /// own termination
        ///</summary>  
        internal void OnWorkerExit(AffinityPoolWorker worker, bool abruptTermination)
        {
            lock (_bookKeepingLock)
            {
                _workers.Remove(worker);
                if (abruptTermination && _poolState == Running)
                    AddWorker(_workers, worker.Queue);
                else if (_workers.Count == 0 && !abruptTermination && _poolState >= ShuttingDown)
                {
                    _poolState = Shutdowned; // transition to shutdown and try to transition to termination
                    AttemptPoolTermination();
                }
            }
        }

        public override void Execute(IRunnable command)
        {
            var queue = _workQueues[_queueSelector.GetQueue(command, _parallelism)];
            if (_poolState >= ShuttingDown || !queue.TryAdd(command))
            {
                _rejectionHandler.Reject(command, this);
            }
        }

        public bool AwaitTermination(TimeSpan timeout)
        {
            // recurse until pool is terminated or time out reached
            lock (_bookKeepingLock)
            {
                if (_poolState == Terminated) return true;
                else if (timeout == TimeSpan.Zero) return false;
                else
                {
                    var result = _terminationCondition.Wait(timeout);
                    return result;
                }
            }
        }

        // WARNING: Only call while holding the bookKeepingLock
        private void AttemptPoolTermination()
        {
            if (_workers.Count == 0 && _poolState == Shutdowned)
            {
                _poolState = Terminated;
                _terminationCondition.Set();
            }
        }

        public void ShutdownNow()
        {
            lock (_bookKeepingLock)
            {
                _poolState = Shutdowned;
                foreach (var worker in _workers)
                {
                    worker.Stop();
                }

                AttemptPoolTermination();
            }
        }

        public override void Shutdown()
        {
            lock (_bookKeepingLock)
            {
                _poolState = ShuttingDown;

                // interrupts only idle workers.. so others can process their queues
                foreach (var worker in _workers)
                {
                    worker.StopIfIdle();
                }

                AttemptPoolTermination();
            }
        }
    }

    internal sealed class BoundedAffinityTaskQueue : AbstractBoundedNodeQueue<IRunnable>
    {
        public BoundedAffinityTaskQueue(int capacity) : base(capacity)
        {
        }
    }

    ///<summary>
    /// A `QueueSelector` is responsible for, given a <see cref="IRunnable"/> and the number of available
    /// queues, return which of the queues that <see cref="IRunnable"/> should be placed in.
    ///</summary> 
    public interface IQueueSelector
    {
        /// <summary>
        /// Must be deterministic—return the same value for the same input.
        /// </summary>
        /// <returns> Given a `Runnable` a number between 0 .. `queues` (exclusive)</returns>
        /// <exception cref="NullReferenceException">When <paramref name="command"/> is `null`.</exception>
        int GetQueue(IRunnable command, int queues);
    }

    public interface IRejectionHandler
    {
        void Reject(IRunnable command, ExecutorService service);
    }

    internal class AffinityPoolConfigurator : ExecutorServiceConfigurator
    {
        public AffinityPoolConfigurator(Config config, IDispatcherPrerequisites prerequisites) 
            : base(config, prerequisites)
        {
            _poolSize = ThreadPoolConfig.ScaledPoolSize(
                config.GetInt("parallelism-min"),
                config.GetDouble("parallelism-factor"),
                config.GetInt("parallelism-max"));

            _taskQueueSize = config.GetInt("task-queue-size");
            _idleCpuLevel = config.GetInt("idle-cpu-level");
            
            if (_idleCpuLevel < 1 || _idleCpuLevel > 10)
                throw new ArgumentException("idle-cpu-level must be between 1 and 10");


            var queueSelectorType = Type.GetType(config.GetString("queue-selector"), throwOnError: true);
            var rejectionHandler = Type.GetType(config.GetString("queue-selector"), throwOnError: true);
            
        }

        public override ExecutorService Produce(string id)
        {
            throw new System.NotImplementedException();
        }
    }

    public sealed class ThrowOnOverflowRejectionHandler : IRejectionHandler
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Reject(IRunnable command, ExecutorService service)
        {
            throw new RejectedExecutionException($"Command {command} rejected from {service}");
        }
    }
    
    public sealed class FairDistributionHashCache : IQueueSelector
    {
        public const int MaxFairDistributionThreshold = 2048;

        private readonly int _fairDistributionThreshold;
        private ImmutableIntDictionary _map;

        public FairDistributionHashCache(Config config)
        {
            _fairDistributionThreshold = config.GetInt("fair-work-distribution.threshold");
            if (_fairDistributionThreshold < 0 || _fairDistributionThreshold > MaxFairDistributionThreshold)
                throw new ArgumentException(
                    $"fair-work-distribution.threshold must be between 0 and {MaxFairDistributionThreshold}");
        }

        public int GetQueue(IRunnable command, int queues)
        {
            var runnableHash = command.GetHashCode();
            if (_fairDistributionThreshold == 0)
            {
                return Improve(runnableHash) % queues;
            }
            else
            {
                var prev = _map;
                while (true)
                {
                    var existingIndex = _map[runnableHash];
                    if (existingIndex >= 0) return existingIndex;
                    else if (prev.Count > _fairDistributionThreshold) return Improve(runnableHash) % queues;
                    else
                    {
                        var index = prev.Count % queues;
                        if (ReferenceEquals(prev,
                            Interlocked.CompareExchange(ref _map, prev.SetItem(runnableHash, index), prev)))
                        {
                            return index;
                        }
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Improve(int h)
        {
            unchecked
            {
                return 0x7FFFFFFF & (ReverseBytes(h * 0x9e3775cd) * 0x9e3775cd);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReverseBytes(int i) =>
            (i << 24) |
            ((i & 0xff00) << 8) |
            ((i >> 8) & 0xff00) |
            (i >> 24);


        public override string ToString() =>
            $"FairDistributionHashCache(fairDistributionThreshold={_fairDistributionThreshold})"
    }
}